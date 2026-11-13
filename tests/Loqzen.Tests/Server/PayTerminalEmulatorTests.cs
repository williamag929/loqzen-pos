using System.Net.Http.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// Pay-on-terminal end to end against the REAL Docker emulator script (docker/emulator/payterminal/server.py, JSON
/// lines over TCP) started as a child process on a free loopback port, through the JsonTcp driver and the Kestrel
/// endpoint. Skipped (at discovery time) when python3 is not installed.
/// </summary>
public class PayTerminalEmulatorTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();
    private EmulatorProcess? _emulator;

    public async Task InitializeAsync()
    {
        await _fx.InitializeAsync();
        _emulator = await EmulatorProcess.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _emulator?.Dispose();
        await _fx.DisposeAsync();
    }

    private Task<PaymentTerminalDto> RegisterEmulatorTerminalAsync(string name = "Datáfono emulado") =>
        _fx.WithServiceAsync<IPaymentTerminalService, PaymentTerminalDto>(s =>
            s.SaveAsync(new SavePaymentTerminalRequest(null, name, PaymentTerminalProvider.JsonTcp, _emulator!.Address, "EMU-1", true, 1)));

    private Task<Guid> ProductPricedAsync(decimal price) => _fx.WithDbAsync(async db =>
    {
        var p = new Product { Sku = $"E-{price:0}", Name = $"Emu {price:0}", Price = price, TaxId = null, IsActive = true };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    });

    private async Task<Guid> SentComandaAsync(string table, Guid productId, decimal quantity = 1)
    {
        var open = await _fx.PostJsonAsync("/api/comandas", new { tableName = table, waiterName = "Ana" });
        open.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await _fx.ReadJsonAsync(open))["id"]!.GetValue<Guid>();
        (await _fx.PostJsonAsync($"/api/comandas/{id}/items", new { productId, quantity })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.PostJsonAsync($"/api/comandas/{id}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    [PythonFact]
    public async Task Emulator_Listed_WithProviderLabel_AndPingOk()
    {
        var terminal = await RegisterEmulatorTerminalAsync();

        var ok = await _fx.WithServiceAsync<IPaymentTerminalService, bool>(s => s.TestConnectionAsync(terminal.Id));
        ok.Should().BeTrue("the emulator answers {\"event\":\"pong\"}");

        var listed = (await _fx.GetJsonAsync("/api/payment-terminals")).AsArray()
            .Single(t => t!["id"]!.GetValue<Guid>() == terminal.Id)!;
        listed["provider"]!.GetValue<string>().Should().Be("JsonTcp");
        listed["providerLabel"]!.GetValue<string>().Should().Be("Emulador / JSON TCP");
        listed["lastPingOk"]!.GetValue<bool>().Should().BeTrue();
        listed["lastSeenAt"]!.GetValue<DateTime>().Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
    }

    [PythonFact]
    public async Task Emulator_UnreachableAddress_PingFalse()
    {
        var port = EmulatorProcess.FreePort();
        var dead = await _fx.WithServiceAsync<IPaymentTerminalService, PaymentTerminalDto>(s =>
            s.SaveAsync(new SavePaymentTerminalRequest(null, "Datáfono apagado", PaymentTerminalProvider.JsonTcp, $"127.0.0.1:{port}", null, true, 2)));

        var ok = await _fx.WithServiceAsync<IPaymentTerminalService, bool>(s => s.TestConnectionAsync(dead.Id));

        ok.Should().BeFalse();
        (await _fx.WithDbAsync(db => db.PaymentTerminals.SingleAsync(t => t.Id == dead.Id))).LastPingOk.Should().BeFalse();
    }

    [PythonFact]
    public async Task Emulator_Approved_EndToEnd_WithVoucher()
    {
        var shift = await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero", 20_000m));
        var terminal = await RegisterEmulatorTerminalAsync();
        var comandaId = await SentComandaAsync("Mesa 1", await ProductPricedAsync(12_500m));

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { terminalId = terminal.Id, waiterName = "Ana" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["success"]!.GetValue<bool>().Should().BeTrue();

        var charge = body["charge"]!;
        charge["status"]!.GetValue<string>().Should().Be("Approved");
        charge["amount"]!.GetValue<decimal>().Should().Be(12_500m);
        charge["terminalName"]!.GetValue<string>().Should().Be("Datáfono emulado");
        charge["authorizationCode"]!.GetValue<string>().Should().HaveLength(6);
        charge["cardBrand"]!.GetValue<string>().Should().BeOneOf("VISA", "MASTERCARD", "AMEX");
        charge["cardLast4"]!.GetValue<string>().Should().HaveLength(4);
        charge["providerReference"]!.GetValue<string>().Should().StartWith("RRN");
        charge["message"]!.GetValue<string>().Should().Be("Aprobada");
        var referenceText = charge["referenceText"]!.GetValue<string>();
        referenceText.Should().StartWith("AUTH " + charge["authorizationCode"]!.GetValue<string>());

        var voucher = charge["voucherText"]!.GetValue<string>();
        var lines = voucher.Split('\n');
        lines.Should().HaveCountGreaterThan(5);
        lines.Should().Contain(l => l.Contains("DATAFONO EMULADO"));
        lines.Should().Contain(l => l.Contains("APROBADA"));
        lines.Should().Contain(l => l.Contains("12,500 COP"));
        lines.Should().Contain(l => l.StartsWith("FACTURA") && l.EndsWith(charge["invoiceReference"]!.GetValue<string>()));
        lines.Should().OnlyContain(l => l.Length <= 32, "voucher lines fit a 58 mm receipt");

        body["payment"]!["success"]!.GetValue<bool>().Should().BeTrue();
        body["comanda"]!["status"]!.GetValue<string>().Should().Be("Closed");

        await _fx.WithDbAsync(async db =>
        {
            var pay = await db.Payments.SingleAsync();
            pay.Method.Should().Be(PaymentMethod.Card);
            pay.Reference.Should().Be(referenceText);
            pay.TerminalPaymentId.Should().Be(charge["id"]!.GetValue<Guid>());
            var order = await db.Orders.SingleAsync(o => o.Id == pay.OrderId);
            order.CashShiftId.Should().Be(shift.Id);
            order.Status.Should().Be(OrderStatus.Completed);
        });
    }

    [PythonFact]
    public async Task Emulator_Declined_EndsIn99()
    {
        await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero", 20_000m));
        var terminal = await RegisterEmulatorTerminalAsync();
        var comandaId = await SentComandaAsync("Mesa 2", await ProductPricedAsync(10_099m));

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { terminalId = terminal.Id, waiterName = "Ana" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["success"]!.GetValue<bool>().Should().BeFalse();
        body["charge"]!["status"]!.GetValue<string>().Should().Be("Declined");
        body["charge"]!["message"]!.GetValue<string>().Should().Be("Fondos insuficientes");
        body["charge"]!["voucherText"].Should().BeNull();
        body["payment"].Should().BeNull();
        body["comanda"]!["status"]!.GetValue<string>().Should().Be("ReadyToPay");
        await _fx.WithDbAsync(async db => (await db.Orders.CountAsync()).Should().Be(0));
    }

    [PythonFact]
    public async Task Emulator_Error_EndsIn97()
    {
        await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero", 20_000m));
        var terminal = await RegisterEmulatorTerminalAsync();
        var comandaId = await SentComandaAsync("Mesa 3", await ProductPricedAsync(10_097m));

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { terminalId = terminal.Id, waiterName = "Ana" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["success"]!.GetValue<bool>().Should().BeFalse();
        body["charge"]!["status"]!.GetValue<string>().Should().Be("Error");
        body["charge"]!["message"]!.GetValue<string>().Should().Contain("Error de comunicación");
        body["payment"].Should().BeNull();
    }

    [PythonFact]
    public async Task Emulator_Hung_ClientAbort_SendsCancel_RowNotPending()
    {
        await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero", 20_000m));
        var terminal = await RegisterEmulatorTerminalAsync();
        var comandaId = await SentComandaAsync("Mesa 4", await ProductPricedAsync(10_098m)); // …98: the emulator never answers

        using var abort = new CancellationTokenSource();
        var first = _fx.Client.PostAsJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { terminalId = terminal.Id, waiterName = "Ana" }, ServerFixture.Json, abort.Token);
        (await _fx.JsonTcpDriver.WaitForChargeStartAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("the charge must reach the driver");
        // The device has the charge and is "waiting for a card" (its handler logs the hung state once it read the request).
        (await ServerFixture.WaitUntilAsync(() => _emulator!.Log.Contains("hung terminal"), TimeSpan.FromSeconds(5)))
            .Should().BeTrue("the emulator must have received the charge request:\n" + _emulator!.Log);
        (await _fx.WithDbAsync(db => db.TerminalPayments.SingleAsync(p => p.ComandaId == comandaId))).Status.Should().Be(TerminalPaymentStatus.Pending);

        // The tablet taps "Cancelar" → HttpClient aborts → RequestAborted → driver sends {"op":"cancel"} and hangs up.
        abort.Cancel();
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();

        (await _fx.JsonTcpDriver.WaitForChargeEndAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await _fx.WaitForDbAsync(db => db.TerminalPayments.AnyAsync(p => p.ComandaId == comandaId && p.Status != TerminalPaymentStatus.Pending), TimeSpan.FromSeconds(10))).Should().BeTrue();
        var row = await _fx.WithDbAsync(db => db.TerminalPayments.SingleAsync(p => p.ComandaId == comandaId));
        row.Status.Should().BeOneOf(TerminalPaymentStatus.Cancelled, TerminalPaymentStatus.Timeout);
        row.OrderId.Should().BeNull();

        // The terminal is free for the next charge.
        var next = await SentComandaAsync("Mesa 5", await ProductPricedAsync(3_000m));
        var retry = await _fx.PostJsonAsync($"/api/comandas/{next}/pay-on-terminal", new { terminalId = terminal.Id, waiterName = "Ana" });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(retry))["success"]!.GetValue<bool>().Should().BeTrue();

        // Protocol contract (driver doc + README): on cancellation the POS SENDS {"op":"cancel"} so the device stops
        // asking for the card. The emulator logs "cancelled by POS" when it receives it.
        (await ServerFixture.WaitUntilAsync(() => _emulator!.Log.Contains("cancelled by POS"), TimeSpan.FromSeconds(2)))
            .Should().BeTrue("the terminal must receive the cancel op, not just a dropped connection. Emulator log:\n" + _emulator!.Log);
    }
}

/// <summary>Marks a fact that needs <c>python3</c> on PATH; the test is reported as skipped (not failed) without it.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (EmulatorProcess.PythonPath() is null)
            Skip = "python3 is not installed: the payment terminal emulator (docker/emulator/payterminal/server.py) cannot be started.";
    }
}

/// <summary>
/// docker/emulator/payterminal/server.py as a child process (<c>python3 -I</c>) on a free loopback port with
/// TERMINAL_DELAY_SECONDS=0. Waits up to 10 s for the port to accept connections; kills the process on Dispose.
/// </summary>
public sealed class EmulatorProcess : IDisposable
{
    private readonly Process _process;
    private readonly System.Text.StringBuilder _log = new();

    public int Port { get; }
    public string Address => $"127.0.0.1:{Port}";
    public string Log { get { lock (_log) return _log.ToString(); } }

    private EmulatorProcess(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public static string? PythonPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "python3.exe" : "python3");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docker", "emulator", "payterminal", "server.py");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("docker/emulator/payterminal/server.py not found above " + AppContext.BaseDirectory);
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public static async Task<EmulatorProcess> StartAsync()
    {
        var python = PythonPath() ?? throw new InvalidOperationException("python3 not found on PATH.");
        var port = FreePort();
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(ScriptPath());
        psi.Environment["TERMINAL_PORT"] = port.ToString();
        psi.Environment["TERMINAL_DELAY_SECONDS"] = "0";
        psi.Environment["TERMINAL_DECLINE_RATE"] = "0";

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the payment terminal emulator.");
        var emu = new EmulatorProcess(process, port);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (emu._log) emu._log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (emu._log) emu._log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"The emulator exited early (code {process.ExitCode}):\n{emu.Log}");
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return emu; // the server accepts connections; the handler sees EOF and logs it, harmless
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }
        emu.Dispose();
        throw new TimeoutException($"The emulator did not listen on port {port} within 10 s:\n{emu.Log}");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // Already gone.
        }
        _process.Dispose();
    }
}
