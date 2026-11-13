using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Core.Application.Kitchen;
using Presentation.Server.Dtos;

namespace Presentation.Server.Realtime;

/// <summary>
/// WebSocket fan-out for the KDS tablets. Each connection carries an optional station filter;
/// kitchen events are pushed to every socket whose filter is empty or matches (case-insensitive).
/// Connections are removed on close or error (no leak), writes are serialized per socket, and a
/// failure to deliver never propagates to the publisher of <see cref="IKitchenEventBus"/>.
/// </summary>
public sealed class KdsSocketHub : IDisposable
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] PongFrame = Encoding.UTF8.GetBytes("pong");

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public int Count => _connections.Count;

    /// <summary>Runs the receive loop until the client closes, the server stops or the socket faults.</summary>
    public async Task HandleAsync(WebSocket socket, string? station, CancellationToken requestAborted)
    {
        var conn = new Connection(socket, string.IsNullOrWhiteSpace(station) ? null : station.Trim());
        var id = Guid.NewGuid();
        _connections[id] = conn;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _shutdown.Token);
        var buffer = new byte[4 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "bye").ConfigureAwait(false);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
                if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
                    await conn.SendAsync(PongFrame, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // server stopping or request aborted: fall through to cleanup
            await TryCloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "server stopping").ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // client vanished (tablet went to sleep, Wi-Fi dropped) — nothing to do but drop it
        }
        catch (Exception)
        {
            // never let a socket failure bubble into Kestrel's request pipeline as an unhandled error
        }
        finally
        {
            _connections.TryRemove(id, out _);
            conn.Dispose();
        }
    }

    /// <summary>Bus handler: serializes once, then fans out without blocking the publisher.</summary>
    public void OnKitchenEvent(KitchenEvent e)
    {
        if (_disposed || _connections.IsEmpty) return;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(e.ToSocketMessage(), ServerJson.Options);
            _ = BroadcastAsync(payload, e.Station);
        }
        catch
        {
            // a broken serializer/socket must never break the sale
        }
    }

    private async Task BroadcastAsync(byte[] payload, string station)
    {
        foreach (var (id, conn) in _connections)
        {
            if (!conn.Accepts(station)) continue;
            try
            {
                await conn.SendAsync(payload, _shutdown.Token).ConfigureAwait(false);
            }
            catch
            {
                _connections.TryRemove(id, out _);
                conn.Abort();
            }
        }
    }

    /// <summary>Closes every socket so the host can stop without waiting for the shutdown timeout.</summary>
    public async Task CloseAllAsync()
    {
        _shutdown.Cancel();
        var tasks = _connections.Values
            .Select(c => TryCloseAsync(c.Socket, WebSocketCloseStatus.EndpointUnavailable, "server stopping"))
            .ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* best effort */ }
        foreach (var c in _connections.Values) c.Abort();
        _connections.Clear();
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(status, reason, cts.Token).ConfigureAwait(false);
        }
        catch { /* peer already gone */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        foreach (var c in _connections.Values) c.Abort();
        _connections.Clear();
        _shutdown.Dispose();
    }

    private sealed class Connection : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Connection(WebSocket socket, string? station)
        {
            Socket = socket;
            Station = station;
        }

        public WebSocket Socket { get; }
        public string? Station { get; }

        public bool Accepts(string station) =>
            Station is null || string.Equals(Station, station, StringComparison.OrdinalIgnoreCase);

        public async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                if (Socket.State != WebSocketState.Open) return;
                await Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Abort()
        {
            // After a completed close handshake the socket is already Closed: aborting it would send a
            // TCP reset that races the client's read of our Close frame (clients saw "connection reset"
            // ~15% of the time on loopback). Only abort sockets that are still mid-flight or faulted;
            // Kestrel disposes a cleanly closed socket itself.
            if (Socket.State == WebSocketState.Closed) return;
            try { Socket.Abort(); } catch { /* already closed */ }
        }

        public void Dispose()
        {
            Abort();
            try { Socket.Dispose(); } catch { /* ignore */ }
            _writeLock.Dispose();
        }
    }
}
