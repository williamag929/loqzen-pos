using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

public class ComandaLifecycleTests : ComandaTestBase
{
    [Fact]
    public async Task OpenComanda_SameTableTwice_Throws()
    {
        await Service.OpenComandaAsync("Mesa 1", "Ana");
        var act = () => Service.OpenComandaAsync("Mesa 1", "Luis");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ya tiene una comanda*");
    }

    [Fact]
    public async Task MultipleTables_CanBeOpenSimultaneously()
    {
        await Service.OpenComandaAsync("Mesa 1", "Ana");
        await Service.OpenComandaAsync("Mesa 2", "Ana");
        var active = await Service.GetActiveComandasAsync();
        active.Select(c => c.Number).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public async Task AddItem_StartsPending_AndNotInKitchen()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        var dto = await Service.AddItemAsync(c.Id, Burger.Id);

        dto.Items.Single().Status.Should().Be(ComandaItemStatus.Pending);
        (await Db.KitchenOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmDue_BeforeDelay_DoesNothing()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);

        Clock.Advance(TimeSpan.FromSeconds(29));
        (await Service.ConfirmDueItemsAsync()).Should().Be(0);
        (await Db.KitchenOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmDue_AfterDelay_CreatesOneKitchenOrderWithOutbox()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);
        await Service.AddItemAsync(c.Id, Burger.Id, notes: "sin cebolla");

        Clock.Advance(TimeSpan.FromSeconds(30));
        (await Service.ConfirmDueItemsAsync()).Should().Be(2);

        var dto = await Service.GetByIdAsync(c.Id);
        dto!.Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed);
        var ko = await Db.KitchenOrders.Include(k => k.Items).SingleAsync();
        ko.TableNumber.Should().Be("Mesa 1");
        ko.Items.Should().HaveCount(2);
        ko.Items.Should().Contain(i => i.Notes == "sin cebolla");
        (await Db.OutboxMessages.CountAsync(m => m.EventType == "KitchenOrderCreated")).Should().Be(1);
    }

    [Fact]
    public async Task ConfirmDue_OnlyConfirmsItemsWhoseDelayElapsed()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);
        Clock.Advance(TimeSpan.FromSeconds(20));
        await Service.AddItemAsync(c.Id, Soda.Id);
        Clock.Advance(TimeSpan.FromSeconds(10)); // burger = 30s, soda = 10s

        (await Service.ConfirmDueItemsAsync()).Should().Be(1);
        var dto = await Service.GetByIdAsync(c.Id);
        dto!.Items.Single(i => i.ProductId == Burger.Id).Status.Should().Be(ComandaItemStatus.Confirmed);
        dto.Items.Single(i => i.ProductId == Soda.Id).Status.Should().Be(ComandaItemStatus.Pending);
    }

    [Fact]
    public async Task CancelPending_Works_ButConfirmedItemCannotBeCancelled()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        var dto = await Service.AddItemAsync(c.Id, Burger.Id);

        var cancelled = await Service.CancelPendingItemAsync(c.Id, dto.Items.Single().Id);
        cancelled.Items.Single().Status.Should().Be(ComandaItemStatus.Cancelled);
        cancelled.Total.Should().Be(0);

        var dto2 = await Service.AddItemAsync(c.Id, Soda.Id);
        var sodaId = dto2.Items.Single(i => i.ProductId == Soda.Id).Id;
        await Service.ConfirmPendingNowAsync(c.Id);
        var act = () => Service.CancelPendingItemAsync(c.Id, sodaId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cocina*");
    }

    [Fact]
    public async Task MarkReadyToPay_SendsPendingToKitchen_BlocksNewItems_UntilReopened()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);
        var ready = await Service.MarkReadyToPayAsync(c.Id);

        ready.Status.Should().Be(ComandaStatus.ReadyToPay);
        ready.Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed);
        var act = () => Service.AddItemAsync(c.Id, Soda.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await Service.ReopenAsync(c.Id);
        (await Service.AddItemAsync(c.Id, Soda.Id)).Items.Should().HaveCount(2);
    }
}
