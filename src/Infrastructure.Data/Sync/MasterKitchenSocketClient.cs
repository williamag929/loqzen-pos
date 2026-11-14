using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Kitchen;

namespace Infrastructure.Data.Sync;

/// <summary>
/// Keeps a ClientWebSocket to <c>{MasterUrl}/ws/kds</c> (http→ws, https→wss; <c>Authorization: Bearer ApiKey</c> when
/// configured) and republishes every <see cref="KitchenSocketEnvelope"/> of type "event" as a <see cref="KitchenEvent"/>
/// on the local <see cref="IKitchenEventBus"/>, so the satellite's desktop KDS and comanda list refresh instantly.
/// Sends "ping" every 20 s (the hub answers "pong"); reconnects with backoff 1, 2, 4 … 30 s.
/// <see cref="IsConnected"/>/<see cref="LastError"/> feed the connection chip; <see cref="Changed"/> is raised on the
/// socket thread (UI subscribers marshal to the dispatcher). Start/Stop are idempotent; DisposeAsync stops.
/// </summary>
public class MasterKitchenSocketClient : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions Json = MasterHttp.Json;

    private readonly TerminalSettings _settings;
    private readonly IKitchenEventBus _bus;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _connected;
    private volatile bool _sessionConnected;
    private volatile string? _lastError;

    public MasterKitchenSocketClient(TerminalSettings settings, IKitchenEventBus bus, TimeProvider? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsConnected => _connected;

    /// <summary>Last connection/receive error (null once connected again).</summary>
    public string? LastError => _lastError;

    /// <summary>Raised when IsConnected or LastError changes. Raised on the socket thread.</summary>
    public event Action? Changed;

    /// <summary>ws(s) URL derived from <see cref="TerminalSettings.MasterUrl"/>; throws when it is missing/invalid.</summary>
    public static Uri BuildSocketUri(string? masterUrl)
    {
        if (string.IsNullOrWhiteSpace(masterUrl))
            throw new InvalidOperationException("Terminal.MasterUrl no está configurada: la caja satélite no sabe dónde está la caja principal.");
        if (!Uri.TryCreate(masterUrl.Trim(), UriKind.Absolute, out var http))
            throw new InvalidOperationException($"Terminal.MasterUrl no es una URL válida: '{masterUrl}'.");
        var scheme = http.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || http.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var builder = new UriBuilder(http) { Scheme = scheme, Path = "/ws/kds", Query = string.Empty, Fragment = string.Empty };
        if (http.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts != null) return; // already running
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        if (cts == null) return;

        cts.Cancel();
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); }
            catch { /* the loop never surfaces its own errors */ }
        }
        cts.Dispose();
        SetState(connected: false, error: null);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // ── loop ─────────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            _sessionConnected = false;
            try
            {
                await SessionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(connected: false, error: Describe(ex));
            }
            if (_sessionConnected) backoff = TimeSpan.FromSeconds(1); // a session that got through resets the ladder

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, _clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = backoff >= MaxBackoff ? MaxBackoff : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
        SetState(connected: false, error: _lastError);
    }

    /// <summary>One connection: connect, pump frames (+ ping timer) until the socket closes or fails.</summary>
    private async Task SessionAsync(CancellationToken ct)
    {
        var uri = BuildSocketUri(_settings.MasterUrl);
        using var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            socket.Options.SetRequestHeader("Authorization", "Bearer " + _settings.ApiKey.Trim());

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"La caja principal no aceptó la conexión en {ConnectTimeout.TotalSeconds:0} s.");
            }
        }
        _sessionConnected = true;
        SetState(connected: true, error: null);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = PingLoopAsync(socket, sessionCts.Token);
        try
        {
            await ReceiveLoopAsync(socket, sessionCts.Token).ConfigureAwait(false);
        }
        finally
        {
            sessionCts.Cancel();
            try { await ping.ConfigureAwait(false); } catch { /* cancelled */ }
            SetState(connected: false, error: _lastError);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false); }
                catch { /* best effort */ }
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var frame = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("La caja principal cerró la conexión en tiempo real.");
                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            HandleText(Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
        }
    }

    /// <summary>Text frame → envelope → local bus. "pong"/non-event frames and malformed JSON are ignored. Public so tests can feed frames without a socket.</summary>
    public void HandleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var trimmed = text.Trim();
        if (trimmed.Equals("pong", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("ping", StringComparison.OrdinalIgnoreCase)) return;
        if (trimmed[0] != '{') return;

        KitchenSocketEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<KitchenSocketEnvelope>(trimmed, Json);
        }
        catch (JsonException)
        {
            return; // a frame we do not understand must never take the connection down
        }
        if (envelope == null || !string.Equals(envelope.Type, "event", StringComparison.OrdinalIgnoreCase)) return;
        _bus.Publish(envelope.ToEvent());
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var ping = Encoding.UTF8.GetBytes("ping");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PingInterval, _clock, ct).ConfigureAwait(false);
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(ping, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
    }

    // ── state ────────────────────────────────────────────────────────────────

    private void SetState(bool connected, string? error)
    {
        bool changed;
        lock (_gate)
        {
            changed = _connected != connected || !string.Equals(_lastError, error, StringComparison.Ordinal);
            _connected = connected;
            _lastError = error;
        }
        if (!changed) return;
        try { Changed?.Invoke(); }
        catch { /* a broken subscriber must not kill the socket loop */ }
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException ws when ws.InnerException != null => $"{MasterHttp.OfflinePrefix}: {ws.InnerException.Message}",
        _ => $"{MasterHttp.OfflinePrefix}: {ex.Message}"
    };
}
