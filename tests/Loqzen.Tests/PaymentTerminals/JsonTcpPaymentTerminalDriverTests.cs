using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Hardware.PaymentTerminals;

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>
/// JsonTcpPaymentTerminalDriver against an in-test TCP terminal that speaks the protocol documented in the driver
/// class comment and docker/emulator/payterminal/README.md (JSON lines, one connection per operation).
/// </summary>
public class JsonTcpPaymentTerminalDriverTests
{
    private static readonly JsonTcpPaymentTerminalDriver Driver = new(new PaymentTerminalSettings { Currency = "COP" });

    private static TerminalChargeRequest Req(string address, decimal amount = 12500m, string reference = "ORD-000123", string? description = "Mesa 4") =>
        new(address, amount, "COP", reference, description);

    private static string Progress(string stage, string? message = null) =>
        message == null ? $"{{\"event\":\"progress\",\"stage\":\"{stage}\"}}" : $"{{\"event\":\"progress\",\"stage\":\"{stage}\",\"message\":\"{message}\"}}";

    private const string ApprovedResult =
        "{\"event\":\"result\",\"status\":\"Approved\",\"auth\":\"654321\",\"brand\":\"MASTERCARD\",\"last4\":\"1111\",\"ref\":\"RRN000123\",\"message\":\"Aprobada\",\"voucher\":[\"LOQZEN POS\",\"VENTA\",\"APROBADA\"]}";

    private sealed class ProgressSink : IProgress<TerminalChargeProgress>
    {
        public readonly List<TerminalChargeProgress> Items = new();
        public readonly TaskCompletionSource<TerminalChargeProgress> WaitingForCard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(TerminalChargeProgress value)
        {
            lock (Items) Items.Add(value);
            if (value.Stage == TerminalChargeStage.WaitingForCard) WaitingForCard.TrySetResult(value);
        }
        public List<TerminalChargeStage> Stages { get { lock (Items) return Items.Select(i => i.Stage).ToList(); } }
    }

    /// <summary>Scripted terminal: reads the first line of each connection and hands it to the script.</summary>
    private sealed class FakeTerminalServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, StreamWriter, StreamReader, Task> _script;
        private readonly List<string> _received = new();

        public FakeTerminalServer(Func<string, StreamWriter, StreamReader, Task> script)
        {
            _script = script;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Address => $"127.0.0.1:{Port}";

        public List<string> Received { get { lock (_received) return _received.ToList(); } }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, new UTF8Encoding(false));
                    var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                    var line = await reader.ReadLineAsync();
                    if (line == null) return;
                    lock (_received) _received.Add(line);
                    await _script(line, writer, reader);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Waits until the terminal has received <paramref name="count"/> lines (client writes race the assertion).</summary>
        public async Task<List<string>> WaitForReceivedAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Received.Count < count && DateTime.UtcNow < deadline) await Task.Delay(10);
            return Received;
        }

        public void Dispose() => _listener.Stop();
    }

    private static FakeTerminalServer Respond(params string[] lines) => new(async (_, w, _) =>
    {
        foreach (var l in lines) await w.WriteLineAsync(l);
    });

    /// <summary>A port nothing listens on (bound then released).</summary>
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ── charge ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approved_WithProgressAndVoucher()
    {
        using var server = Respond(Progress("WaitingForCard", "Acerque la tarjeta"), Progress("Processing", "Procesando..."), ApprovedResult);
        var sink = new ProgressSink();

        var r = await Driver.ChargeAsync(Req(server.Address), sink, CancellationToken.None);

        r.Status.Should().Be(TerminalPaymentStatus.Approved);
        r.AuthorizationCode.Should().Be("654321");
        r.CardBrand.Should().Be("MASTERCARD");
        r.CardLast4.Should().Be("1111");
        r.ProviderReference.Should().Be("RRN000123");
        r.Message.Should().Be("Aprobada");
        r.VoucherLines.Should().Equal("LOQZEN POS", "VENTA", "APROBADA");

        sink.Stages.Should().Equal(TerminalChargeStage.Connecting, TerminalChargeStage.WaitingForCard, TerminalChargeStage.Processing, TerminalChargeStage.Done);
        sink.Items[0].Message.Should().Contain(server.Address);
        sink.Items[1].Message.Should().Be("Acerque la tarjeta");
        sink.Items[2].Message.Should().Be("Procesando...");
        sink.Items[3].Message.Should().Be("Transacción aprobada.");
    }

    [Fact]
    public async Task Charge_SendsTheDocumentedChargeMessage()
    {
        using var server = Respond(ApprovedResult);

        await Driver.ChargeAsync(Req(server.Address, 12500m, "ORD-000123", "Mesa 4"), null, CancellationToken.None);

        var sent = (await server.WaitForReceivedAsync(1)).Single();
        using var doc = JsonDocument.Parse(sent);
        var root = doc.RootElement;
        root.GetProperty("op").GetString().Should().Be("charge");
        root.GetProperty("amount").GetDecimal().Should().Be(12500m);
        root.GetProperty("currency").GetString().Should().Be("COP");
        root.GetProperty("reference").GetString().Should().Be("ORD-000123");
        root.GetProperty("description").GetString().Should().Be("Mesa 4");
    }

    [Fact]
    public async Task Charge_WithoutDescription_OmitsTheKey_AndFallsBackToSettingsCurrency()
    {
        using var server = Respond(ApprovedResult);

        await Driver.ChargeAsync(new TerminalChargeRequest(server.Address, 100m, "", "X", null), null, CancellationToken.None);

        var sent = (await server.WaitForReceivedAsync(1)).Single();
        using var doc = JsonDocument.Parse(sent);
        doc.RootElement.TryGetProperty("description", out _).Should().BeFalse("nulls are not serialized");
        doc.RootElement.GetProperty("currency").GetString().Should().Be("COP");
    }

    [Fact]
    public async Task Declined_ReturnsDeclinedWithTerminalMessage()
    {
        using var server = Respond(Progress("WaitingForCard"), "{\"event\":\"result\",\"status\":\"Declined\",\"message\":\"Fondos insuficientes\"}");
        var sink = new ProgressSink();

        var r = await Driver.ChargeAsync(Req(server.Address, 10099m), sink, CancellationToken.None);

        r.Status.Should().Be(TerminalPaymentStatus.Declined);
        r.Message.Should().Be("Fondos insuficientes");
        r.AuthorizationCode.Should().BeNull();
        sink.Items.Last().Should().Be(new TerminalChargeProgress(TerminalChargeStage.Done, "Fondos insuficientes"));
        sink.Items[1].Message.Should().Be("Acerque o inserte la tarjeta en el datáfono…", "a progress without message gets the default text");
    }

    [Fact]
    public async Task DeclinedWithoutMessage_UsesDefaultText()
    {
        using var server = Respond("{\"event\":\"result\",\"status\":\"declined\"}");
        var r = await Driver.ChargeAsync(Req(server.Address), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Declined, "status is parsed case-insensitively");
        r.Message.Should().Be("Rechazada por el emisor.");
    }

    [Fact]
    public async Task ErrorStatus_ReturnsErrorWithTerminalMessage()
    {
        using var server = Respond("{\"event\":\"result\",\"status\":\"Error\",\"message\":\"Error de comunicación con el emisor\"}");
        var r = await Driver.ChargeAsync(Req(server.Address, 20097m), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Be("Error de comunicación con el emisor");
    }

    [Fact]
    public async Task TimeoutAndCancelledStatuses_FromTheTerminal_AreMapped()
    {
        using var s1 = Respond("{\"event\":\"result\",\"status\":\"Timeout\"}");
        (await Driver.ChargeAsync(Req(s1.Address), null, CancellationToken.None)).Status.Should().Be(TerminalPaymentStatus.Timeout);

        using var s2 = Respond("{\"event\":\"result\",\"status\":\"Cancelled\"}");
        (await Driver.ChargeAsync(Req(s2.Address), null, CancellationToken.None)).Status.Should().Be(TerminalPaymentStatus.Cancelled);
    }

    [Theory]
    [InlineData("{\"event\":\"result\",\"status\":\"Weird\"}")]
    [InlineData("{\"event\":\"result\",\"status\":\"Pending\"}")]
    [InlineData("{\"event\":\"result\"}")]
    public async Task UnknownOrPendingStatus_IsError(string result)
    {
        using var server = Respond(result);
        var r = await Driver.ChargeAsync(Req(server.Address), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Contain("Estado desconocido");
    }

    [Fact]
    public async Task ApprovedWithoutAuthCode_IsError()
    {
        using var server = Respond("{\"event\":\"result\",\"status\":\"Approved\",\"brand\":\"VISA\"}");
        var r = await Driver.ChargeAsync(Req(server.Address), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Contain("sin código de autorización");
    }

    [Fact]
    public async Task ApprovedWithoutVoucher_HasNullVoucherLines()
    {
        using var server = Respond("{\"event\":\"result\",\"status\":\"Approved\",\"auth\":\"000001\"}");
        var r = await Driver.ChargeAsync(Req(server.Address), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Approved);
        r.VoucherLines.Should().BeNull();
        r.Message.Should().Be("Aprobada");
    }

    [Fact]
    public async Task MalformedJson_IsError_MentioningTheDevice()
    {
        using var server = Respond(Progress("WaitingForCard"), "{this is not json");
        var r = await Driver.ChargeAsync(Req(server.Address), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Contain("Respuesta inválida").And.Contain("Verifique en el dispositivo");
    }

    [Fact]
    public async Task ConnectionClosedWithoutResult_IsError()
    {
        using var server = Respond(Progress("WaitingForCard", "Acerque la tarjeta"));
        var r = await Driver.ChargeAsync(Req(server.Address), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Contain("cerró la conexión sin enviar un resultado");
    }

    [Fact]
    public async Task UnknownEventsAndBlankLines_AreIgnored()
    {
        using var server = Respond("", "{\"event\":\"keepalive\"}", "{\"event\":\"pong\"}", ApprovedResult);
        var sink = new ProgressSink();
        var r = await Driver.ChargeAsync(Req(server.Address), sink, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Approved);
        sink.Stages.Should().Equal(TerminalChargeStage.Connecting, TerminalChargeStage.Done);
    }

    [Fact]
    public async Task UnknownStage_IsReportedAsProcessing()
    {
        using var server = Respond(Progress("Reading", "Leyendo chip"), ApprovedResult);
        var sink = new ProgressSink();
        await Driver.ChargeAsync(Req(server.Address), sink, CancellationToken.None);
        sink.Items[1].Should().Be(new TerminalChargeProgress(TerminalChargeStage.Processing, "Leyendo chip"));
    }

    [Fact]
    public async Task ConnectionRefused_IsError_NotAnException()
    {
        var r = await Driver.ChargeAsync(Req($"127.0.0.1:{FreePort()}"), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Contain("No se pudo conectar con el datáfono en 127.0.0.1:");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:abc")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData(":9102")]
    [InlineData("[::1")]
    public async Task InvalidAddress_IsError(string? address)
    {
        var r = await Driver.ChargeAsync(Req(address!), null, CancellationToken.None);
        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Contain("dirección válida");
    }

    [Fact]
    public async Task HostWithoutPort_DefaultsTo9102()
    {
        // A pre-cancelled token makes the connect step exit immediately, whatever is (or is not) listening on 9102;
        // the Connecting progress line carries the endpoint the driver parsed.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sink = new ProgressSink();

        var r = await Driver.ChargeAsync(Req("127.0.0.1"), sink, cts.Token);

        JsonTcpPaymentTerminalDriver.DefaultPort.Should().Be(9102);
        sink.Items.Should().ContainSingle().Which.Message.Should().Contain("127.0.0.1:9102");
        r.Status.Should().Be(TerminalPaymentStatus.Cancelled);
    }

    [Fact]
    public async Task BracketedIpv6_WithPort_IsParsed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sink = new ProgressSink();
        await Driver.ChargeAsync(Req("[::1]:9103"), sink, cts.Token);
        sink.Items.Single().Message.Should().Contain("::1:9103");
    }

    [Fact]
    public async Task Cancel_SendsCancelOp_AndReturnsCancelled()
    {
        var gotCancel = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeTerminalServer(async (_, w, r) =>
        {
            await w.WriteLineAsync(Progress("WaitingForCard", "Acerque la tarjeta"));
            var next = await r.ReadLineAsync();          // the POS gives up → {"op":"cancel"}
            gotCancel.TrySetResult(next);
            if (next != null) await w.WriteLineAsync("{\"event\":\"result\",\"status\":\"Cancelled\"}");
        });
        using var cts = new CancellationTokenSource();
        var sink = new ProgressSink();

        var task = Driver.ChargeAsync(Req(server.Address, 30098m), sink, cts.Token);
        await sink.WaitingForCard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        task.IsCompleted.Should().BeFalse();
        cts.Cancel();

        var result = await task;
        result.Status.Should().Be(TerminalPaymentStatus.Cancelled);
        result.Message.Should().Be("Cancelada desde el POS.");
        sink.Items.Last().Should().Be(new TerminalChargeProgress(TerminalChargeStage.Done, "Transacción cancelada."));

        var cancelLine = await gotCancel.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelLine.Should().Be("{\"op\":\"cancel\"}");
    }

    [Fact]
    public async Task CancelBeforeConnecting_ReturnsCancelled()
    {
        using var server = Respond(ApprovedResult);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Driver.ChargeAsync(Req(server.Address), null, cts.Token);
        r.Status.Should().Be(TerminalPaymentStatus.Cancelled);
    }

    // ── ping ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_Pong_IsTrue_AndSendsPingOp()
    {
        using var server = Respond("{\"event\":\"pong\"}");
        (await Driver.PingAsync(server.Address)).Should().BeTrue();
        (await server.WaitForReceivedAsync(1)).Single().Should().Be("{\"op\":\"ping\"}");
    }

    [Fact]
    public async Task Ping_ClosedPort_IsFalse()
    {
        (await Driver.PingAsync($"127.0.0.1:{FreePort()}")).Should().BeFalse();
    }

    [Fact]
    public async Task Ping_InvalidAddress_IsFalse()
    {
        (await Driver.PingAsync(null)).Should().BeFalse();
        (await Driver.PingAsync("")).Should().BeFalse();
        (await Driver.PingAsync("host:abc")).Should().BeFalse();
    }

    [Fact]
    public async Task Ping_UnexpectedAnswer_IsFalse()
    {
        using var s1 = Respond("{\"event\":\"result\",\"status\":\"Approved\"}");
        (await Driver.PingAsync(s1.Address)).Should().BeFalse();

        using var s2 = Respond("not json");
        (await Driver.PingAsync(s2.Address)).Should().BeFalse();

        using var s3 = new FakeTerminalServer((_, _, _) => Task.CompletedTask); // closes without answering
        (await Driver.PingAsync(s3.Address)).Should().BeFalse();
    }

    [Fact]
    public void Provider_IsJsonTcp()
    {
        Driver.Provider.Should().Be(PaymentTerminalProvider.JsonTcp);
    }
}
