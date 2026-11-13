using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Enums;

namespace Infrastructure.Hardware.PaymentTerminals;

/// <summary>
/// Generic "JSON lines over TCP" terminal (the Docker emulator in docker/emulator/payterminal speaks it; a real
/// integration can be adapted to it with a small bridge). Address is "host:port"; port defaults to 9102.
///
/// Protocol — one TCP connection per operation, every message is one JSON object terminated by '\n' (UTF-8):
/// <code>
///   POS  → {"op":"charge","amount":12500,"currency":"COP","reference":"ORD-000123","description":"Mesa 4"}
///   TERM → {"event":"progress","stage":"WaitingForCard","message":"Acerque la tarjeta"}      (0..n times)
///   TERM → {"event":"progress","stage":"Processing","message":"Procesando..."}
///   TERM → {"event":"result","status":"Approved","auth":"123456","brand":"VISA","last4":"4242",
///           "ref":"RRN000123","message":"Aprobada","voucher":["LOQZEN POS","VENTA", "..."]}
///   POS  → {"op":"cancel"}            (sent when the POS gives up; the terminal should abort and may answer a
///                                      result with status "Cancelled" — the POS does not wait for it)
///   POS  → {"op":"ping"}     TERM → {"event":"pong"}
/// </code>
/// result.status ∈ Approved | Declined | Error | Cancelled | Timeout (unknown → Error). stage ∈ Connecting |
/// WaitingForCard | Processing | Done. The connection is closed by the POS after the result.
///
/// Cancellation: the driver cannot tell *why* <c>ct</c> fired (cashier pressed "Cancelar" vs. the service's charge
/// timeout) — both arrive as the same token. It therefore sends "cancel", closes the socket and always returns
/// <see cref="TerminalChargeResult.Cancelled"/>; the service, which knows which token tripped, maps that to Timeout
/// when its own timeout was the cause. Transport failures (unreachable host, connection reset, malformed JSON) are
/// returned as <see cref="TerminalChargeResult.Error"/>; the driver never throws for outcomes.
/// </summary>
public class JsonTcpPaymentTerminalDriver : IPaymentTerminalDriver
{
    public const int DefaultPort = 9102;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PingReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancelSendTimeout = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly PaymentTerminalSettings _settings;

    public JsonTcpPaymentTerminalDriver(PaymentTerminalSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public PaymentTerminalProvider Provider => PaymentTerminalProvider.JsonTcp;

    public async Task<TerminalChargeResult> ChargeAsync(TerminalChargeRequest request, IProgress<TerminalChargeProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseAddress(request.Address, out var host, out var port))
            return TerminalChargeResult.Error("El datáfono no tiene una dirección válida (host:puerto).");

        progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Connecting, $"Conectando con el datáfono en {host}:{port}…"));

        using var client = new TcpClient { NoDelay = true };
        try
        {
            var connected = await ConnectAsync(client, host, port, ct);
            if (!connected)
                return ct.IsCancellationRequested
                    ? TerminalChargeResult.Cancelled()
                    : TerminalChargeResult.Error($"No se pudo conectar con el datáfono en {host}:{port} (tiempo de espera agotado).");
        }
        catch (OperationCanceledException) { return TerminalChargeResult.Cancelled(); }
        catch (Exception ex) { return TerminalChargeResult.Error($"No se pudo conectar con el datáfono en {host}:{port}: {ex.Message}"); }

        NetworkStream stream;
        StreamReader reader;
        StreamWriter writer;
        try
        {
            stream = client.GetStream();
            reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
            writer = new StreamWriter(stream, Utf8NoBom) { NewLine = "\n", AutoFlush = true };
        }
        catch (Exception ex) { return TerminalChargeResult.Error($"Error de comunicación con el datáfono: {ex.Message}"); }

        try
        {
            var charge = new
            {
                op = "charge",
                amount = request.Amount,
                currency = string.IsNullOrWhiteSpace(request.Currency) ? _settings.Currency : request.Currency,
                reference = request.InvoiceReference,
                description = request.Description
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(charge, JsonOptions).AsMemory(), ct);

            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    return TerminalChargeResult.Error("El datáfono cerró la conexión sin enviar un resultado. Verifique en el dispositivo si la transacción se realizó.");
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var evt = GetString(root, "event");

                if (evt == "progress")
                {
                    var stage = ParseStage(GetString(root, "stage"));
                    progress?.Report(new TerminalChargeProgress(stage, GetString(root, "message") ?? DefaultMessage(stage)));
                    continue;
                }

                if (evt == "result")
                {
                    var result = ParseResult(root);
                    progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Done,
                        result.IsApproved ? "Transacción aprobada." : result.Message ?? "Transacción finalizada."));
                    return result;
                }

                // Unknown events (pong, keepalive, vendor extensions) are ignored.
            }
        }
        catch (OperationCanceledException)
        {
            await TrySendCancelAsync(client, writer, reader);
            progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Done, "Transacción cancelada."));
            return TerminalChargeResult.Cancelled();
        }
        catch (JsonException ex)
        {
            return TerminalChargeResult.Error($"Respuesta inválida del datáfono: {ex.Message}. Verifique en el dispositivo si la transacción se realizó.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return TerminalChargeResult.Error($"Error de comunicación con el datáfono: {ex.Message}. Verifique en el dispositivo si la transacción se realizó.");
        }
    }

    public async Task<bool> PingAsync(string? address, CancellationToken ct = default)
    {
        if (!TryParseAddress(address, out var host, out var port)) return false;
        try
        {
            using var client = new TcpClient { NoDelay = true };
            if (!await ConnectAsync(client, host, port, ct)) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PingReadTimeout);

            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
            await using var writer = new StreamWriter(stream, Utf8NoBom) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync("{\"op\":\"ping\"}".AsMemory(), timeout.Token);

            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line)) return false;
            using var doc = JsonDocument.Parse(line);
            return GetString(doc.RootElement, "event") == "pong";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Connects with a 5 s cap. False on timeout; throws OperationCanceledException only when <paramref name="ct"/> fired.</summary>
    private static async Task<bool> ConnectAsync(TcpClient client, string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Best effort "cancel" so the terminal stops asking for a card; never blocks the caller for more than ~1 s.
    /// After writing, the send side is shut down (graceful FIN instead of the abortive RST that disposing a TcpClient
    /// with unread data produces on Linux) and the device's "Cancelled" result is drained, so the cancel is actually
    /// delivered even when it races the charge line. Every error is swallowed: the POS is giving up anyway.
    /// </summary>
    private static async Task TrySendCancelAsync(TcpClient client, StreamWriter writer, StreamReader reader)
    {
        try
        {
            using var cts = new CancellationTokenSource(CancelSendTimeout);
            await writer.WriteLineAsync("{\"op\":\"cancel\"}".AsMemory(), cts.Token);
            client.Client.Shutdown(SocketShutdown.Send);

            // Drain until the device answers (expected: {"event":"result","status":"Cancelled"}) or closes.
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (GetString(doc.RootElement, "event") == "result") break;
            }
        }
        catch
        {
            // The socket may already be gone or the device may not answer cancels; nothing more to do.
        }
    }

    private static TerminalChargeResult ParseResult(JsonElement root)
    {
        var statusText = GetString(root, "status");
        if (!Enum.TryParse<TerminalPaymentStatus>(statusText, ignoreCase: true, out var status) || status == TerminalPaymentStatus.Pending)
            return TerminalChargeResult.Error($"Estado desconocido devuelto por el datáfono: '{statusText}'.");

        var message = GetString(root, "message");
        switch (status)
        {
            case TerminalPaymentStatus.Declined: return TerminalChargeResult.Declined(message);
            case TerminalPaymentStatus.Cancelled: return TerminalChargeResult.Cancelled();
            case TerminalPaymentStatus.Timeout: return TerminalChargeResult.Timeout();
            case TerminalPaymentStatus.Error: return TerminalChargeResult.Error(message ?? "Error reportado por el datáfono.");
        }

        var auth = GetString(root, "auth");
        if (string.IsNullOrWhiteSpace(auth))
            return TerminalChargeResult.Error("El datáfono aprobó sin código de autorización. Verifique en el dispositivo si la transacción se realizó.");

        List<string>? voucher = null;
        if (root.TryGetProperty("voucher", out var v) && v.ValueKind == JsonValueKind.Array)
        {
            voucher = new List<string>();
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) voucher.Add(item.GetString() ?? string.Empty);
        }

        return new TerminalChargeResult(
            TerminalPaymentStatus.Approved,
            AuthorizationCode: auth,
            CardBrand: GetString(root, "brand"),
            CardLast4: GetString(root, "last4"),
            ProviderReference: GetString(root, "ref"),
            Message: message ?? "Aprobada",
            VoucherLines: voucher);
    }

    private static TerminalChargeStage ParseStage(string? stage) =>
        Enum.TryParse<TerminalChargeStage>(stage, ignoreCase: true, out var s) ? s : TerminalChargeStage.Processing;

    private static string DefaultMessage(TerminalChargeStage stage) => stage switch
    {
        TerminalChargeStage.Connecting => "Conectando con el datáfono…",
        TerminalChargeStage.WaitingForCard => "Acerque o inserte la tarjeta en el datáfono…",
        TerminalChargeStage.Processing => "Procesando la transacción…",
        _ => "Transacción finalizada."
    };

    private static string? GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>"host", "host:port" or "[ipv6]:port". Port defaults to <see cref="DefaultPort"/>.</summary>
    internal static bool TryParseAddress(string? address, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultPort;
        if (string.IsNullOrWhiteSpace(address)) return false;
        address = address.Trim();

        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            if (close < 0) return false;
            host = address[1..close];
            var rest = address[(close + 1)..];
            if (rest.Length == 0) return host.Length > 0;
            return rest[0] == ':' && int.TryParse(rest[1..], out port) && port is > 0 and < 65536 && host.Length > 0;
        }

        var colon = address.LastIndexOf(':');
        if (colon < 0 || address.IndexOf(':') != colon)
        {
            // No port, or a bare IPv6 literal without brackets: use the default port.
            host = address;
            return host.Length > 0;
        }

        host = address[..colon];
        return host.Length > 0 && int.TryParse(address[(colon + 1)..], out port) && port is > 0 and < 65536;
    }
}
