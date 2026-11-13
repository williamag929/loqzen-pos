using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>
/// Manager-authorized cancellation of an item that is already in the kitchen:
/// comanda item → Cancelled, KDS item struck through, KitchenOrder cancelled only when empty, outbox event.
/// </summary>
public class ComandaCancelConfirmedItemTests : ComandaTestBase
{
    private const string Kitchen = "Cocina Caliente";
    private const string Bar = "Barra";

    /// <summary>Open comanda, Burger (kitchen) + Soda (bar), both confirmed → one KitchenOrder per station.</summary>
    private async Task<(Guid comandaId, Guid burgerItemId, Guid sodaItemId)> ConfirmedBurgerAndSodaAsync()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);
        var dto = await Service.AddItemAsync(c.Id, Soda.Id);
        await Service.ConfirmPendingNowAsync(c.Id);

        var burgerItem = dto.Items.Single(i => i.ProductId == Burger.Id).Id;
        var sodaItem = dto.Items.Single(i => i.ProductId == Soda.Id).Id;
        (await Db.KitchenOrders.CountAsync()).Should().Be(2, "one per station");
        return (c.Id, burgerItem, sodaItem);
    }

    private async Task<Guid> CreateOverrideAsync(AuthorizedAction action = AuthorizedAction.CancelKitchenItem)
    {
        var o = new ManagerOverride
        {
            Action = action,
            Description = "Anular producto en cocina",
            ManagerUserId = Guid.NewGuid(),
            ManagerName = "Gerente",
            AuthorizedAt = Clock.GetUtcNow().UtcDateTime
        };
        Db.ManagerOverrides.Add(o);
        await Db.SaveChangesAsync();
        return o.Id;
    }

    private Task<KitchenOrder> KitchenOrderFor(string station) =>
        Db.KitchenOrders.AsNoTracking().Include(k => k.Items).SingleAsync(k => k.Station == station);

    [Fact]
    public async Task Cancel_OnlyItemOfStation_CancelsItemAndKitchenOrder_LeavesOtherStationUntouched()
    {
        var (cid, burgerId, sodaId) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();
        Clock.Advance(TimeSpan.FromMinutes(2));

        var dto = await Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);

        dto.Items.Single(i => i.Id == burgerId).Status.Should().Be(ComandaItemStatus.Cancelled);
        dto.Items.Single(i => i.Id == sodaId).Status.Should().Be(ComandaItemStatus.Confirmed);

        var kitchen = await KitchenOrderFor(Kitchen);
        kitchen.Status.Should().Be(KitchenOrderStatus.Cancelled, "its only item was cancelled");
        kitchen.Items.Single().IsCancelled.Should().BeTrue();
        kitchen.UpdatedAt.Should().Be(Clock.GetUtcNow().UtcDateTime);

        var bar = await KitchenOrderFor(Bar);
        bar.Status.Should().Be(KitchenOrderStatus.Pending);
        bar.Items.Single().IsCancelled.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_AddsExactlyOneOutboxMessage_KitchenItemCancelled()
    {
        var (cid, burgerId, _) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();
        var before = await Db.OutboxMessages.CountAsync();

        await Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);

        (await Db.OutboxMessages.CountAsync()).Should().Be(before + 1);
        var msg = await Db.OutboxMessages.SingleAsync(m => m.EventType == "KitchenItemCancelled");
        msg.AggregateType.Should().Be("KitchenOrder");
        msg.AggregateId.Should().Be((await KitchenOrderFor(Kitchen)).Id);
        msg.Status.Should().Be(SyncStatus.Pending);
        msg.PayloadJson.Should().Contain(burgerId.ToString()).And.Contain(overrideId.ToString()).And.Contain("Hamburguesa");
    }

    [Fact]
    public async Task Cancel_TotalExcludesCancelledItem()
    {
        var (cid, burgerId, _) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();

        var dto = await Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);

        dto.BillableItems.Should().ContainSingle(i => i.ProductId == Soda.Id);
        dto.Subtotal.Should().Be(5000m);
        dto.Total.Should().Be(5950m, "only the soda (5000 + 19%) remains");
    }

    [Fact]
    public async Task Cancel_OneOfTwoBurgers_StrikesOnlyOneKitchenItem_OrderStaysPending()
    {
        var c = await Service.OpenComandaAsync("Mesa 2", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);
        var dto = await Service.AddItemAsync(c.Id, Burger.Id);
        await Service.ConfirmPendingNowAsync(c.Id);
        var overrideId = await CreateOverrideAsync();
        var firstBurger = dto.Items.First().Id;

        var after = await Service.CancelConfirmedItemAsync(c.Id, firstBurger, overrideId);

        after.Items.Count(i => i.Status == ComandaItemStatus.Cancelled).Should().Be(1);
        after.Items.Count(i => i.Status == ComandaItemStatus.Confirmed).Should().Be(1);
        after.Total.Should().Be(23800m, "one burger still billed");

        var kitchen = await KitchenOrderFor(Kitchen);
        kitchen.Status.Should().Be(KitchenOrderStatus.Pending, "a burger is still to be cooked");
        kitchen.Items.Should().HaveCount(2);
        kitchen.Items.Count(i => i.IsCancelled).Should().Be(1);
    }

    [Fact]
    public async Task Cancel_SecondBurgerToo_CancelsKitchenOrder()
    {
        var c = await Service.OpenComandaAsync("Mesa 2", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);
        var dto = await Service.AddItemAsync(c.Id, Burger.Id);
        await Service.ConfirmPendingNowAsync(c.Id);

        // Overrides are single-use: each cancellation needs its own approval.
        foreach (var item in dto.Items)
            await Service.CancelConfirmedItemAsync(c.Id, item.Id, await CreateOverrideAsync());

        var kitchen = await KitchenOrderFor(Kitchen);
        kitchen.Status.Should().Be(KitchenOrderStatus.Cancelled);
        kitchen.Items.Should().OnlyContain(i => i.IsCancelled);
    }

    [Fact]
    public async Task Cancel_WithUnknownOverrideId_Throws_AndChangesNothing()
    {
        var (cid, burgerId, _) = await ConfirmedBurgerAndSodaAsync();
        var outboxBefore = await Db.OutboxMessages.CountAsync();

        var act = () => Service.CancelConfirmedItemAsync(cid, burgerId, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        (await Db.ComandaItems.SingleAsync(i => i.Id == burgerId)).Status.Should().Be(ComandaItemStatus.Confirmed);
        (await KitchenOrderFor(Kitchen)).Items.Single().IsCancelled.Should().BeFalse();
        (await Db.OutboxMessages.CountAsync()).Should().Be(outboxBefore);
    }

    [Fact]
    public async Task Cancel_PendingItem_Throws()
    {
        var c = await Service.OpenComandaAsync("Mesa 3", "Ana");
        var dto = await Service.AddItemAsync(c.Id, Burger.Id);
        var overrideId = await CreateOverrideAsync();

        var act = () => Service.CancelConfirmedItemAsync(c.Id, dto.Items.Single().Id, overrideId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pendiente*");
        (await Db.ComandaItems.SingleAsync()).Status.Should().Be(ComandaItemStatus.Pending);
    }

    [Fact]
    public async Task Cancel_AlreadyCancelledItem_Throws()
    {
        var (cid, burgerId, _) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();
        await Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);

        var act = () => Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ya está anulado*");
        (await Db.OutboxMessages.CountAsync(m => m.EventType == "KitchenItemCancelled")).Should().Be(1, "no duplicate event");
    }

    [Fact]
    public async Task Cancel_UnknownItem_Throws()
    {
        var (cid, _, _) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();

        var act = () => Service.CancelConfirmedItemAsync(cid, Guid.NewGuid(), overrideId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no encontrado*");
    }

    [Fact]
    public async Task Cancel_OnClosedComanda_Throws()
    {
        var (cid, burgerId, _) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();
        var comanda = await Db.Comandas.SingleAsync(c => c.Id == cid);
        comanda.Status = ComandaStatus.Closed;
        comanda.ClosedAt = Clock.GetUtcNow().UtcDateTime;
        await Db.SaveChangesAsync();

        var act = () => Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no está activa*");
        (await Db.ComandaItems.SingleAsync(i => i.Id == burgerId)).Status.Should().Be(ComandaItemStatus.Confirmed);
    }

    [Fact]
    public async Task Cancel_OnReadyToPayComanda_IsAllowed()
    {
        var (cid, burgerId, _) = await ConfirmedBurgerAndSodaAsync();
        await Service.MarkReadyToPayAsync(cid);
        var overrideId = await CreateOverrideAsync();

        var dto = await Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);
        dto.Status.Should().Be(ComandaStatus.ReadyToPay);
        dto.Items.Single(i => i.Id == burgerId).Status.Should().Be(ComandaItemStatus.Cancelled);
    }

    [Fact]
    public async Task Kds_HidesFullyCancelledOrder_ButShowsPartiallyCancelledOne_WithFlag()
    {
        // Mesa 1: burger (kitchen) + soda (bar). Mesa 2: two burgers (kitchen).
        var (c1, burger1, _) = await ConfirmedBurgerAndSodaAsync();
        var c2 = await Service.OpenComandaAsync("Mesa 2", "Ana");
        await Service.AddItemAsync(c2.Id, Burger.Id);
        var dto2 = await Service.AddItemAsync(c2.Id, Burger.Id);
        await Service.ConfirmPendingNowAsync(c2.Id);

        var kds = new KitchenService(Db, Clock);
        (await kds.GetActiveAsync()).Should().HaveCount(3);

        // One fresh (single-use) approval per cancellation.
        await Service.CancelConfirmedItemAsync(c1, burger1, await CreateOverrideAsync());          // kitchen order of Mesa 1 fully cancelled
        await Service.CancelConfirmedItemAsync(c2.Id, dto2.Items.First().Id, await CreateOverrideAsync()); // Mesa 2 partially cancelled

        var active = await kds.GetActiveAsync();
        active.Should().HaveCount(2);
        active.Should().NotContain(k => k.TableNumber == "Mesa 1" && k.Station == Kitchen, "fully cancelled ticket leaves the KDS");
        active.Should().ContainSingle(k => k.TableNumber == "Mesa 1" && k.Station == Bar);

        var mesa2 = active.Single(k => k.TableNumber == "Mesa 2");
        mesa2.Status.Should().Be(KitchenOrderStatus.Pending);
        mesa2.Items.Should().HaveCount(2);
        mesa2.Items.Count(i => i.IsCancelled).Should().Be(1, "struck-through line is visible to the cook");
    }
    // ---- single-use overrides ----

    [Fact]
    public async Task Cancel_MarksOverrideConsumed_WithClockAndItemReference()
    {
        var (cid, burgerId, _) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();
        Clock.Advance(TimeSpan.FromMinutes(5));

        await Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);

        var stored = await Db.ManagerOverrides.AsNoTracking().SingleAsync(o => o.Id == overrideId);
        stored.ConsumedAt.Should().Be(Clock.GetUtcNow().UtcDateTime);
        stored.ConsumedFor.Should().Be($"ComandaItem {burgerId}");
        stored.UpdatedAt.Should().Be(Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Cancel_ReusingConsumedOverride_Throws_AndChangesNothing()
    {
        var (cid, burgerId, sodaId) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();
        await Service.CancelConfirmedItemAsync(cid, burgerId, overrideId);
        var outboxBefore = await Db.OutboxMessages.CountAsync();
        var consumedAt = (await Db.ManagerOverrides.AsNoTracking().SingleAsync(o => o.Id == overrideId)).ConsumedAt;

        var act = () => Service.CancelConfirmedItemAsync(cid, sodaId, overrideId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        (await Db.ComandaItems.SingleAsync(i => i.Id == sodaId)).Status.Should().Be(ComandaItemStatus.Confirmed);
        var bar = await KitchenOrderFor(Bar);
        bar.Status.Should().Be(KitchenOrderStatus.Pending);
        bar.Items.Single().IsCancelled.Should().BeFalse();
        (await Db.OutboxMessages.CountAsync()).Should().Be(outboxBefore, "no event for the rejected attempt");
        var stored = await Db.ManagerOverrides.AsNoTracking().SingleAsync(o => o.Id == overrideId);
        stored.ConsumedAt.Should().Be(consumedAt, "the first consumption is kept");
        stored.ConsumedFor.Should().Be($"ComandaItem {burgerId}");
    }

    [Fact]
    public async Task Cancel_OverrideNotConsumed_WhenCancellationIsRejected()
    {
        var (cid, _, _) = await ConfirmedBurgerAndSodaAsync();
        var overrideId = await CreateOverrideAsync();

        var act = () => Service.CancelConfirmedItemAsync(cid, Guid.NewGuid(), overrideId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var stored = await Db.ManagerOverrides.AsNoTracking().SingleAsync(o => o.Id == overrideId);
        stored.ConsumedAt.Should().BeNull("a failed attempt must not burn the approval");
        stored.ConsumedFor.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_WithOverrideForAnotherAction_IsRejected()
    {
        var (cid, burger, _) = await ConfirmedBurgerAndSodaAsync();
        var discountApproval = await CreateOverrideAsync(AuthorizedAction.ApplyDiscount);

        var act = () => Service.CancelConfirmedItemAsync(cid, burger, discountApproval);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        (await Db.ComandaItems.SingleAsync(i => i.Id == burger)).Status.Should().Be(ComandaItemStatus.Confirmed);
    }

    [Fact]
    public async Task Cancel_StrikesTheKitchenLineWithMatchingNotes_NotItsTwin()
    {
        var c = await Service.OpenComandaAsync("Mesa 3", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);                      // plain
        var dto = await Service.AddItemAsync(c.Id, Burger.Id, notes: "sin cebolla");
        await Service.ConfirmPendingNowAsync(c.Id);
        var noted = dto.Items.Single(i => i.Notes == "sin cebolla").Id;

        await Service.CancelConfirmedItemAsync(c.Id, noted, await CreateOverrideAsync());

        var ko = await KitchenOrderFor(Kitchen);
        ko.Items.Single(i => i.IsCancelled).Notes.Should().Be("sin cebolla");
        ko.Items.Single(i => !i.IsCancelled).Notes.Should().BeNull();
    }

    [Theory]
    [InlineData(KitchenOrderStatus.Ready)]
    [InlineData(KitchenOrderStatus.Delivered)]
    public async Task Cancel_LastItem_OfReadyOrDeliveredTicket_DoesNotFlipTicketToCancelled(KitchenOrderStatus status)
    {
        var (cid, burger, _) = await ConfirmedBurgerAndSodaAsync();
        var ko = await Db.KitchenOrders.SingleAsync(k => k.Station == Kitchen);
        ko.Status = status;
        await Db.SaveChangesAsync();

        await Service.CancelConfirmedItemAsync(cid, burger, await CreateOverrideAsync());

        var after = await KitchenOrderFor(Kitchen);
        after.Status.Should().Be(status, "the dish already exists; only the line is marked");
        after.Items.Single().IsCancelled.Should().BeTrue();
    }
}
