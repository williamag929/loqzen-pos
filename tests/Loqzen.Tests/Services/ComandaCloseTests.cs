using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

public class ComandaCloseTests : ComandaTestBase
{
    [Fact]
    public async Task CloseAndPay_GeneratesInvoice_WithOutbox_AndClosesComanda()
    {
        var shiftId = await OpenShiftAsync();
        var c = await Service.OpenComandaAsync("Mesa 3", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id, quantity: 2); // 40,000 + 7,600 IVA
        await Service.AddItemAsync(c.Id, Soda.Id);                //  5,000 +   950 IVA

        var result = await Service.CloseAndPayAsync(c.Id, shiftId, PaymentMethod.Cash, 60000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ChangeAmount.Should().Be(6450m);
        var order = await Db.Orders.Include(o => o.Items).Include(o => o.Payments).SingleAsync();
        order.Status.Should().Be(OrderStatus.Completed);
        order.CashShiftId.Should().Be(shiftId);
        order.Subtotal.Should().Be(45000m);
        order.TaxTotal.Should().Be(8550m);
        order.Total.Should().Be(53550m);
        order.Items.Should().HaveCount(2);
        order.Payments.Single().Amount.Should().Be(53550m);
        (await Db.OutboxMessages.CountAsync(m => m.EventType == "OrderCompleted")).Should().Be(1);

        var closed = await Service.GetByIdAsync(c.Id);
        closed!.Status.Should().Be(ComandaStatus.Closed);
        closed.OrderId.Should().Be(order.Id);
        (await Service.GetActiveComandasAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAndPay_InsufficientCash_FailsWithoutSideEffects()
    {
        var shiftId = await OpenShiftAsync();
        var c = await Service.OpenComandaAsync("Mesa 3", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);

        var result = await Service.CloseAndPayAsync(c.Id, shiftId, PaymentMethod.Cash, 1000m);

        result.Success.Should().BeFalse();
        (await Db.Orders.CountAsync()).Should().Be(0);
        (await Db.KitchenOrders.CountAsync()).Should().Be(0, "failed payment must not send items to kitchen");
        (await Service.GetByIdAsync(c.Id))!.Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task CloseAndPay_ExcludesCancelledItems()
    {
        var shiftId = await OpenShiftAsync();
        var c = await Service.OpenComandaAsync("Mesa 3", "Ana");
        await Service.AddItemAsync(c.Id, Soda.Id);
        var dto = await Service.AddItemAsync(c.Id, Burger.Id);
        await Service.CancelPendingItemAsync(c.Id, dto.Items.Single(i => i.ProductId == Burger.Id).Id);

        var result = await Service.CloseAndPayAsync(c.Id, shiftId, PaymentMethod.Card, 0m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var order = await Db.Orders.Include(o => o.Items).SingleAsync();
        order.Items.Should().ContainSingle(i => i.ProductName == "Gaseosa");
        order.Total.Should().Be(5950m);
    }

    [Fact]
    public async Task CloseAndPay_WithoutOpenShift_Fails()
    {
        var c = await Service.OpenComandaAsync("Mesa 3", "Ana");
        await Service.AddItemAsync(c.Id, Soda.Id);
        var result = await Service.CloseAndPayAsync(c.Id, Guid.NewGuid(), PaymentMethod.Card, 0m);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("turno");
    }

    [Fact]
    public async Task ClosedTable_CanBeReopened()
    {
        var shiftId = await OpenShiftAsync();
        var c = await Service.OpenComandaAsync("Mesa 3", "Ana");
        await Service.AddItemAsync(c.Id, Soda.Id);
        await Service.CloseAndPayAsync(c.Id, shiftId, PaymentMethod.Card, 0m);

        var again = await Service.OpenComandaAsync("Mesa 3", "Ana");
        again.Number.Should().Be(2);
    }
}
