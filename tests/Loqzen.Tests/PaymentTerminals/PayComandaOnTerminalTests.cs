using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>Pay at table: comanda → ReadyToPay → charge on the terminal → CloseAndPay(Card, reference, terminalPaymentId).</summary>
public class PayComandaOnTerminalTests : PaymentTerminalServiceTestBase
{
    private const decimal Total = 53550m; // 2 × 20.000 + 5.000 = 45.000 + 19 % IVA

    private async Task<CashShift> OpenShiftAsync() => await Shifts.OpenShiftAsync("Caja", 50000m);

    /// <summary>Open comanda on "Mesa 3" with 2 burgers + 1 soda, items confirmed (sent to kitchen).</summary>
    private async Task<ComandaDto> OpenComandaWithConfirmedItemsAsync()
    {
        var c = await Comandas.OpenComandaAsync("Mesa 3", "Ana");
        await Comandas.AddItemAsync(c.Id, Burger.Id, 2);
        await Comandas.AddItemAsync(c.Id, Soda.Id);
        Clock.Advance(TimeSpan.FromSeconds(31));
        (await Comandas.ConfirmDueItemsAsync()).Should().Be(2);
        var dto = (await Comandas.GetByIdAsync(c.Id))!;
        dto.Status.Should().Be(ComandaStatus.Open);
        dto.Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed);
        dto.Total.Should().Be(Total);
        return dto;
    }

    private async Task<Comanda> ComandaRowAsync(Guid id)
    {
        await using var ctx = Sqlite.NewContext();
        return await ctx.Comandas.AsNoTracking().Include(c => c.Items).SingleAsync(c => c.Id == id);
    }

    [Fact]
    public async Task Approved_ChargesTheTotal_ClosesTheComanda_AndLinksPaymentAndChargeBothWays()
    {
        var shift = await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync("Inalámbrico");
        var waiter = Guid.NewGuid();

        ComandaStatus? statusSeenByDevice = null;
        TerminalPayment? pendingSeenByDevice = null;
        Driver.OnCharge = async (_, _, _) =>
        {
            await using var ctx = Sqlite.NewContext();
            statusSeenByDevice = (await ctx.Comandas.AsNoTracking().SingleAsync(x => x.Id == c.Id)).Status;
            pendingSeenByDevice = await ctx.TerminalPayments.AsNoTracking().SingleAsync();
            return FakeTerminalDriver.ApprovedVisa;
        };

        var result = await Service.PayComandaOnTerminalAsync(c.Id, t.Id, waiter, "Ana");

        // While the card was being read: comanda already ReadyToPay, Pending audit row with the comanda's data.
        statusSeenByDevice.Should().Be(ComandaStatus.ReadyToPay);
        pendingSeenByDevice!.Status.Should().Be(TerminalPaymentStatus.Pending);
        pendingSeenByDevice.Amount.Should().Be(Total);
        pendingSeenByDevice.Purpose.Should().Be(TerminalPaymentPurpose.Comanda);
        pendingSeenByDevice.InvoiceReference.Should().Be($"Comanda #{c.Number}");
        pendingSeenByDevice.ComandaId.Should().Be(c.Id);
        pendingSeenByDevice.CashShiftId.Should().Be(shift.Id);
        pendingSeenByDevice.OrderId.Should().BeNull();
        pendingSeenByDevice.RequestedByUserId.Should().Be(waiter);
        pendingSeenByDevice.RequestedByName.Should().Be("Ana");
        Driver.Requests.Single().Amount.Should().Be(Total);
        Driver.Requests.Single().InvoiceReference.Should().Be($"Comanda #{c.Number}");

        // Outcome.
        result.Success.Should().BeTrue();
        result.Charge.IsApproved.Should().BeTrue();
        result.Payment.Should().NotBeNull();
        result.Payment!.Success.Should().BeTrue(result.Payment.ErrorMessage);
        result.Payment.ChangeAmount.Should().Be(0m);

        var order = await Db.Orders.AsNoTracking().Include(o => o.Payments).Include(o => o.Items).SingleAsync(o => o.Id == result.Payment.OrderId);
        order.Status.Should().Be(OrderStatus.Completed);
        order.Total.Should().Be(Total);
        order.CashShiftId.Should().Be(shift.Id);
        order.Items.Should().HaveCount(2);
        var payment = order.Payments.Should().ContainSingle().Subject;
        payment.Method.Should().Be(PaymentMethod.Card);
        payment.Amount.Should().Be(Total);
        payment.TenderedAmount.Should().Be(Total);
        payment.ChangeAmount.Should().Be(0m);
        payment.Reference.Should().Be(result.Charge.ReferenceText).And.Be("AUTH 123456 · VISA ****4242");
        payment.TerminalPaymentId.Should().Be(result.Charge.Id);

        var charge = await RowAsync(result.Charge.Id);
        charge.OrderId.Should().Be(order.Id, "the audit row is linked back to the invoice");
        charge.ComandaId.Should().Be(c.Id);
        result.Charge.OrderId.Should().Be(order.Id, "the returned DTO reflects the link");

        var comanda = await ComandaRowAsync(c.Id);
        comanda.Status.Should().Be(ComandaStatus.Closed);
        comanda.OrderId.Should().Be(order.Id);
        (await Comandas.GetActiveComandasAsync()).Should().BeEmpty();
        (await Db.OutboxMessages.CountAsync(m => m.EventType == "OrderCompleted")).Should().Be(1);
    }

    [Fact]
    public async Task Approved_SendsTableAsDescription()
    {
        await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync();

        await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana");

        Driver.Requests.Single().Description.Should().Contain("Mesa 3");
    }

    [Fact]
    public async Task Declined_LeavesComandaReadyToPay_NoOrder_PaymentNull()
    {
        await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult(TerminalChargeResult.Declined("Fondos insuficientes"));

        var result = await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana");

        result.Success.Should().BeFalse();
        result.Payment.Should().BeNull();
        result.Charge.Status.Should().Be(TerminalPaymentStatus.Declined);
        result.Charge.Message.Should().Be("Fondos insuficientes");
        result.Charge.OrderId.Should().BeNull();
        (await Db.Orders.CountAsync()).Should().Be(0);
        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.ReadyToPay);
        (await RowAsync(result.Charge.Id)).Status.Should().Be(TerminalPaymentStatus.Declined);
    }

    [Theory]
    [InlineData(TerminalPaymentStatus.Error)]
    [InlineData(TerminalPaymentStatus.Timeout)]
    public async Task ErrorOrTimeout_LeavesComandaReadyToPay(TerminalPaymentStatus status)
    {
        await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult(status == TerminalPaymentStatus.Error ? TerminalChargeResult.Error("x") : TerminalChargeResult.Timeout());

        var result = await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana");

        result.Success.Should().BeFalse();
        result.Payment.Should().BeNull();
        result.Charge.Status.Should().Be(status);
        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.ReadyToPay);
        (await Db.Orders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ReadyToPayComanda_CanBeRetriedAfterADecline()
    {
        await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult(TerminalChargeResult.Declined());
        (await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana")).Success.Should().BeFalse();

        Driver.OnCharge = (_, _, _) => Task.FromResult(FakeTerminalDriver.ApprovedVisa);
        var retry = await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana");

        retry.Success.Should().BeTrue();
        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.Closed);
        (await Db.TerminalPayments.CountAsync(p => p.ComandaId == c.Id)).Should().Be(2, "both attempts are audited");
    }

    [Fact]
    public async Task NoOpenShift_Throws_AbraTurno_AndDoesNotTouchTheComanda()
    {
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync();

        await Service.Invoking(s => s.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Abra turno*");

        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.Open);
        Driver.Requests.Should().BeEmpty();
        (await Db.TerminalPayments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClosedComanda_Throws()
    {
        var shift = await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        (await Comandas.CloseAndPayAsync(c.Id, shift.Id, PaymentMethod.Cash, 60000m)).Success.Should().BeTrue();
        var t = await AddTerminalAsync();

        await Service.Invoking(s => s.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*cerrada*");
        Driver.Requests.Should().BeEmpty();
        (await Db.TerminalPayments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UnknownComanda_KeyNotFound()
    {
        await OpenShiftAsync();
        var t = await AddTerminalAsync();
        await Service.Invoking(s => s.PayComandaOnTerminalAsync(Guid.NewGuid(), t.Id, null, "Ana"))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task UnknownTerminal_KeyNotFound_BeforeTheComandaIsMutated()
    {
        await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();

        await Service.Invoking(s => s.PayComandaOnTerminalAsync(c.Id, Guid.NewGuid(), null, "Ana"))
            .Should().ThrowAsync<KeyNotFoundException>();

        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.Open, "validation happens before MarkReadyToPay");
        (await Db.TerminalPayments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InactiveTerminal_Throws_BeforeTheComandaIsMutated()
    {
        await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync(isActive: false);

        await Service.Invoking(s => s.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*inactivo*");
        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task EmptyComanda_Throws_NoProducts()
    {
        await OpenShiftAsync();
        var c = await Comandas.OpenComandaAsync("Mesa 1", "Ana");
        var t = await AddTerminalAsync();

        await Service.Invoking(s => s.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tiene productos*");
        Driver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingItems_AreConfirmedByReadyToPay_AndBilled()
    {
        await OpenShiftAsync();
        var c = await Comandas.OpenComandaAsync("Mesa 5", "Ana");
        await Comandas.AddItemAsync(c.Id, Soda.Id); // still Pending (yellow)
        var t = await AddTerminalAsync();

        var result = await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana");

        result.Success.Should().BeTrue();
        Driver.Requests.Single().Amount.Should().Be(5950m);
        (await ComandaRowAsync(c.Id)).Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed);
    }

    [Fact]
    public async Task CallerCancelsAfterTheCardWasCharged_ComandaIsStillClosed()
    {
        await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync();
        using var cts = new CancellationTokenSource();
        Driver.OnCharge = (_, _, _) =>
        {
            cts.Cancel(); // the cashier walks away right as the device approves
            return Task.FromResult(FakeTerminalDriver.ApprovedVisa);
        };

        var result = await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana", null, cts.Token);

        result.Success.Should().BeTrue("the card is charged: closing must not be aborted by the caller's token");
        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.Closed);
    }

    [Fact]
    public async Task ShiftClosedBetweenChargeAndClose_ChargeApprovedButPaymentFails_ComandaStaysReadyToPay()
    {
        var shift = await OpenShiftAsync();
        var c = await OpenComandaWithConfirmedItemsAsync();
        var t = await AddTerminalAsync();
        Driver.OnCharge = async (_, _, _) =>
        {
            await using var ctx = Sqlite.NewContext();
            var s = await ctx.CashShifts.SingleAsync(x => x.Id == shift.Id);
            s.Status = CashShiftStatus.Closed;
            await ctx.SaveChangesAsync();
            return FakeTerminalDriver.ApprovedVisa;
        };

        var result = await Service.PayComandaOnTerminalAsync(c.Id, t.Id, null, "Ana");

        result.Success.Should().BeFalse();
        result.Charge.IsApproved.Should().BeTrue("the device did charge the card; the audit row must say so");
        result.Payment.Should().NotBeNull();
        result.Payment!.Success.Should().BeFalse();
        result.Payment.ErrorMessage.Should().Contain("turno");
        result.Charge.OrderId.Should().BeNull();
        (await ComandaRowAsync(c.Id)).Status.Should().Be(ComandaStatus.ReadyToPay);
        (await Db.Orders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TerminalBusyWithAnotherTable_Throws_BeforeTouchingTheComanda()
    {
        await OpenShiftAsync();
        var c1 = await OpenComandaWithConfirmedItemsAsync();
        var c2 = await Comandas.OpenComandaAsync("Mesa 9", "Ana");
        await Comandas.AddItemAsync(c2.Id, Soda.Id);
        var t = await AddTerminalAsync();
        var release = new TaskCompletionSource<TerminalChargeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Driver.OnCharge = (_, _, _) => release.Task;

        var first = Service.PayComandaOnTerminalAsync(c1.Id, t.Id, null, "Ana");
        await Driver.Entered;

        await Service.Invoking(s => s.PayComandaOnTerminalAsync(c2.Id, t.Id, null, "Pedro"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*ocupado*");
        (await ComandaRowAsync(c2.Id)).Status.Should().Be(ComandaStatus.Open, "a busy terminal must not move the table to ReadyToPay nor push its items to the kitchen");

        release.SetResult(FakeTerminalDriver.ApprovedVisa);
        (await first).Success.Should().BeTrue();
    }
}
