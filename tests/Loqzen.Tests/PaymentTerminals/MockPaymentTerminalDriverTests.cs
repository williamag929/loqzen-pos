using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Hardware.PaymentTerminals;

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>In-process simulator: answer by the last two digits of the amount, progress stages, voucher shape, timeouts.</summary>
public class MockPaymentTerminalDriverTests
{
    private readonly ManualTimeProvider _clock = new();

    private MockPaymentTerminalDriver Driver(int delaySeconds = 0, int timeoutSeconds = 90) =>
        new(new PaymentTerminalSettings { MockDelaySeconds = delaySeconds, ChargeTimeoutSeconds = timeoutSeconds, Currency = "COP" }, _clock);

    private static TerminalChargeRequest Req(decimal amount, string reference = "ORD-000123") =>
        new("ignored", amount, "COP", reference, "Mesa 4");

    private sealed class ProgressSink : IProgress<TerminalChargeProgress>
    {
        public readonly List<TerminalChargeProgress> Items = new();
        public void Report(TerminalChargeProgress value) => Items.Add(value);
    }

    // ── approved ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approved_HasSixDigitAuth_KnownBrand_Last4_Reference_AndMessage()
    {
        var r = await Driver().ChargeAsync(Req(12500m), null, CancellationToken.None);

        r.Status.Should().Be(TerminalPaymentStatus.Approved);
        r.IsApproved.Should().BeTrue();
        r.AuthorizationCode.Should().MatchRegex("^[0-9]{6}$");
        r.CardBrand.Should().BeOneOf("VISA", "MASTERCARD", "AMEX");
        r.CardLast4.Should().MatchRegex("^[0-9]{4}$");
        r.ProviderReference.Should().StartWith("MOCK-");
        r.Message.Should().Be("Aprobada");
    }

    [Fact]
    public async Task Approved_VoucherIsAscii_AtMost32Columns_AndCarriesAuthAmountAndCard()
    {
        var r = await Driver().ChargeAsync(Req(12500m, "ORD-000123"), null, CancellationToken.None);

        r.VoucherLines.Should().NotBeNullOrEmpty();
        foreach (var line in r.VoucherLines!)
        {
            line.Length.Should().BeLessThanOrEqualTo(32, $"voucher line '{line}'");
            line.Should().MatchRegex("^[\\x20-\\x7E]*$", $"voucher line '{line}' must be printable ASCII");
        }
        r.VoucherLines.Should().Contain(l => l.Contains("AUTORIZACION") && l.EndsWith(r.AuthorizationCode!));
        r.VoucherLines.Should().Contain(l => l.StartsWith("TOTAL") && l.Contains("12,500 COP"));
        r.VoucherLines.Should().Contain(l => l.StartsWith("TARJETA") && l.Contains($"{r.CardBrand} ****{r.CardLast4}"));
        r.VoucherLines.Should().Contain(l => l.StartsWith("FACTURA") && l.EndsWith("ORD-000123"));
        r.VoucherLines.Should().Contain(l => l.Trim() == "APROBADA");
        r.VoucherLines.Should().Contain(l => l.Trim() == "DATAFONO SIMULADO");
    }

    [Fact]
    public async Task Approved_BrandCyclesAcrossConsecutiveCharges()
    {
        var d = Driver();
        var brands = new List<string?>();
        for (var i = 0; i < 3; i++)
            brands.Add((await d.ChargeAsync(Req(1000m), null, CancellationToken.None)).CardBrand);

        brands.Should().OnlyHaveUniqueItems("three consecutive approvals walk VISA/MASTERCARD/AMEX");
    }

    [Fact]
    public async Task Progress_ReportsStagesInOrder_EndingWithDone()
    {
        var sink = new ProgressSink();
        await Driver().ChargeAsync(Req(5000m), sink, CancellationToken.None);

        sink.Items.Select(p => p.Stage).Should().Equal(
            TerminalChargeStage.Connecting, TerminalChargeStage.WaitingForCard, TerminalChargeStage.Processing, TerminalChargeStage.Done);
        sink.Items.Last().Message.Should().Be("Transacción aprobada.");
        sink.Items.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Message));
    }

    [Fact]
    public async Task WithDelay_WalksTheStagesAsTheClockAdvances_WithoutWallClockWaits()
    {
        var sink = new ProgressSink();
        var task = Driver(delaySeconds: 4).ChargeAsync(Req(5000m), sink, CancellationToken.None);

        sink.Items.Select(p => p.Stage).Should().Equal(TerminalChargeStage.Connecting);
        task.IsCompleted.Should().BeFalse();

        await AdvanceAsync(TimeSpan.FromSeconds(1), task);
        sink.Items.Select(p => p.Stage).Should().Equal(TerminalChargeStage.Connecting, TerminalChargeStage.WaitingForCard);

        await AdvanceAsync(TimeSpan.FromSeconds(2), task);
        sink.Items.Select(p => p.Stage).Should().Contain(TerminalChargeStage.Processing);

        await AdvanceAsync(TimeSpan.FromSeconds(1), task);
        (await task).Status.Should().Be(TerminalPaymentStatus.Approved);
        sink.Items.Last().Stage.Should().Be(TerminalChargeStage.Done);
    }

    // ── amount rules ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10099)]
    [InlineData(99)]
    [InlineData(1099.50)]
    public async Task AmountEndingIn99_IsDeclined(decimal amount)
    {
        var sink = new ProgressSink();
        var r = await Driver().ChargeAsync(Req(amount), sink, CancellationToken.None);

        r.Status.Should().Be(TerminalPaymentStatus.Declined);
        r.IsApproved.Should().BeFalse();
        r.Message.Should().Be("Fondos insuficientes (simulado)");
        r.AuthorizationCode.Should().BeNull();
        r.VoucherLines.Should().BeNull();
        sink.Items.Last().Should().Be(new TerminalChargeProgress(TerminalChargeStage.Done, "Fondos insuficientes (simulado)"));
    }

    [Fact]
    public async Task AmountEndingIn97_IsError()
    {
        var r = await Driver().ChargeAsync(Req(20097m), null, CancellationToken.None);

        r.Status.Should().Be(TerminalPaymentStatus.Error);
        r.Message.Should().Be("Error de comunicación (simulado)");
    }

    [Fact]
    public async Task AmountEndingIn98_NeverAnswers_ThenTimesOutAfterChargeTimeoutSeconds()
    {
        var sink = new ProgressSink();
        var task = Driver(timeoutSeconds: 90).ChargeAsync(Req(30098m), sink, CancellationToken.None);

        sink.Items.Select(p => p.Stage).Should().Equal(TerminalChargeStage.Connecting, TerminalChargeStage.WaitingForCard);
        _clock.Advance(TimeSpan.FromSeconds(89));
        task.IsCompleted.Should().BeFalse("the hung terminal must not answer before the timeout");

        _clock.Advance(TimeSpan.FromSeconds(1));
        var r = await task;

        r.Status.Should().Be(TerminalPaymentStatus.Timeout);
        r.Message.Should().Be("El datáfono no respondió a tiempo.");
        sink.Items.Last().Should().Be(new TerminalChargeProgress(TerminalChargeStage.Done, "El datáfono no respondió."));
    }

    [Fact]
    public async Task AmountEndingIn98_CallerCancels_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var sink = new ProgressSink();
        var task = Driver().ChargeAsync(Req(30098m), sink, cts.Token);
        task.IsCompleted.Should().BeFalse();

        cts.Cancel();
        var r = await task;

        r.Status.Should().Be(TerminalPaymentStatus.Cancelled);
        r.Message.Should().Be("Cancelada desde el POS.");
        sink.Items.Last().Should().Be(new TerminalChargeProgress(TerminalChargeStage.Done, "Transacción cancelada."));
    }

    [Fact]
    public async Task CancelledDuringDelay_ReturnsCancelled_NotAnException()
    {
        using var cts = new CancellationTokenSource();
        var task = Driver(delaySeconds: 8).ChargeAsync(Req(5000m), null, cts.Token);
        cts.Cancel();

        var r = await task;
        r.Status.Should().Be(TerminalPaymentStatus.Cancelled);
    }

    [Fact]
    public async Task NullRequest_Throws()
    {
        await Driver().Invoking(d => d.ChargeAsync(null!, null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Ping_IsAlwaysTrue()
    {
        (await Driver().PingAsync(null)).Should().BeTrue();
        (await Driver().PingAsync("whatever")).Should().BeTrue();
    }

    [Fact]
    public void Provider_IsMock()
    {
        Driver().Provider.Should().Be(PaymentTerminalProvider.Mock);
    }

    // ── factory ──────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_ListsRegisteredProviders_AndReturnsTheSameInstance()
    {
        var mock = Driver();
        var tcp = new JsonTcpPaymentTerminalDriver(new PaymentTerminalSettings());
        var factory = new PaymentTerminalDriverFactory(new IPaymentTerminalDriver[] { mock, tcp });

        factory.AvailableProviders.Should().BeEquivalentTo(new[] { PaymentTerminalProvider.Mock, PaymentTerminalProvider.JsonTcp });
        factory.GetDriver(PaymentTerminalProvider.Mock).Should().BeSameAs(mock);
        factory.GetDriver(PaymentTerminalProvider.JsonTcp).Should().BeSameAs(tcp);
    }

    [Fact]
    public void Factory_UnknownProvider_ThrowsWithSpanishLabel()
    {
        var factory = new PaymentTerminalDriverFactory(new IPaymentTerminalDriver[] { Driver() });

        factory.Invoking(f => f.GetDriver(PaymentTerminalProvider.Redeban))
            .Should().Throw<InvalidOperationException>().WithMessage("*Redeban*");
        factory.Invoking(f => f.GetDriver(PaymentTerminalProvider.JsonTcp))
            .Should().Throw<InvalidOperationException>().WithMessage("*Emulador / JSON TCP*");
        factory.Invoking(f => f.GetDriver(PaymentTerminalProvider.MercadoPagoPoint))
            .Should().Throw<InvalidOperationException>().WithMessage("*Mercado Pago Point*");
    }

    [Fact]
    public void Factory_WithoutDrivers_HasNoProviders()
    {
        new PaymentTerminalDriverFactory(Array.Empty<IPaymentTerminalDriver>()).AvailableProviders.Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Advances the fake clock once the driver has armed its next timer (continuations may run asynchronously).</summary>
    private async Task AdvanceAsync(TimeSpan by, Task charge)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_clock.ActiveTimers == 0 && !charge.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("driver did not arm a timer");
            await Task.Delay(5);
        }
        _clock.Advance(by);
        // Let the continuation after the fired timer run so the next stage is reported before the caller asserts.
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (_clock.ActiveTimers == 0 && !charge.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("driver did not continue after the timer");
            await Task.Delay(5);
        }
    }
}
