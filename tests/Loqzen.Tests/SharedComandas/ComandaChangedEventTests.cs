using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// Every comanda mutation on the master publishes ONE KitchenEventType.ComandaChanged after SaveChanges
/// (KitchenOrderId = comanda id, OrderNumber "#N", TableNumber, Station "", Status Pending) so every register's
/// comanda list refreshes. Ticket events (Created / ItemCancelled) come before it.
/// </summary>
public class ComandaChangedEventTests : SharedComandaTestBase
{
    private void AssertLastIsComandaChanged(ComandaDto c, int expectedCount = 1)
    {
        var changed = ComandaChangedEvents.ToList();
        changed.Should().HaveCount(expectedCount);
        var e = changed.Last();
        e.KitchenOrderId.Should().Be(c.Id);
        e.OrderNumber.Should().Be($"#{c.Number}");
        e.TableNumber.Should().Be(c.TableName);
        e.Station.Should().BeEmpty();
        e.Status.Should().Be(KitchenOrderStatus.Pending);
        e.OccurredAt.Should().Be(Now);
        Events.Last().Should().BeSameAs(e, "ComandaChanged is the last event of the operation");
    }

    [Fact]
    public async Task Open_PublishesComandaChanged()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task AddItem_PublishesComandaChanged()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        Events.Clear();

        c = await Service.AddItemAsync(c.Id, Burger.Id);

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task CancelPendingItem_PublishesComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        c = await Service.CancelPendingItemAsync(c.Id, c.Items[0].Id);

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task SetItemNotes_PublishesComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        c = await Service.SetItemNotesAsync(c.Id, c.Items[0].Id, "sin cebolla");

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task SetItemHold_PublishesComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        c = await Service.SetItemHoldAsync(c.Id, c.Items[0].Id, true);

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task ConfirmPendingNow_PublishesCreatedPerStationThenComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        c = await Service.ConfirmPendingNowAsync(c.Id);

        Events.Should().HaveCount(3);
        Events.Take(2).Should().OnlyContain(e => e.Type == KitchenEventType.Created);
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task ConfirmPendingNow_NothingPending_PublishesNothing()
    {
        var c = await OpenWithItemsAsync();
        await Service.ConfirmPendingNowAsync(c.Id);
        Events.Clear();

        await Service.ConfirmPendingNowAsync(c.Id);

        Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmDueItems_PublishesComandaChangedPerComanda()
    {
        var a = await OpenWithItemsAsync("Mesa 1");
        var b = await OpenWithItemsAsync("Mesa 2");
        Clock.Advance(TimeSpan.FromSeconds(31));
        Events.Clear();

        var n = await Service.ConfirmDueItemsAsync();

        n.Should().Be(4);
        Events.Count(e => e.Type == KitchenEventType.Created).Should().Be(4);
        ComandaChangedEvents.Select(e => e.KitchenOrderId).Should().BeEquivalentTo(new[] { a.Id, b.Id });
        ComandaChangedEvents.Select(e => e.OrderNumber).Should().BeEquivalentTo(new[] { $"#{a.Number}", $"#{b.Number}" });
        Events.TakeLast(2).Should().OnlyContain(e => e.Type == KitchenEventType.ComandaChanged, "tickets first, then the list refresh");
    }

    [Fact]
    public async Task ConfirmDueItems_NothingDue_PublishesNothing()
    {
        await OpenWithItemsAsync();
        Events.Clear();

        (await Service.ConfirmDueItemsAsync()).Should().Be(0);

        Events.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadyToPay_PublishesCreatedThenComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        c = await Service.MarkReadyToPayAsync(c.Id);

        Events.Count(e => e.Type == KitchenEventType.Created).Should().Be(2);
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task Reopen_PublishesComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        await Service.MarkReadyToPayAsync(c.Id);
        Events.Clear();

        c = await Service.ReopenAsync(c.Id);

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task LockForPayment_OpenComanda_PublishesCreatedThenComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        c = await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        Events.Count(e => e.Type == KitchenEventType.Created).Should().Be(2);
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task LockForPayment_Refresh_PublishesComandaChangedOnly()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Events.Clear();

        c = await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task LockForPayment_Refused_PublishesNothing()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Events.Clear();

        await Service.Invoking(s => s.LockForPaymentAsync(c.Id, OtherSatelliteCode)).Should().ThrowAsync<InvalidOperationException>();

        Events.Should().BeEmpty();
    }

    [Fact]
    public async Task UnlockPayment_ByHolder_PublishesComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Events.Clear();

        c = await Service.UnlockPaymentAsync(c.Id, SatelliteCode);

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task CloseAndPay_PublishesCreatedThenComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        Events.Clear();

        var result = await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeTrue();
        Events.Count(e => e.Type == KitchenEventType.Created).Should().Be(2);
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task CloseAndPay_Failed_PublishesNothing()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        Events.Clear();

        var result = await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 1m);

        result.Success.Should().BeFalse();
        Events.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseRemotely_PublishesCreatedThenComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        c = await Service.CloseRemotelyAsync(RemoteClose(c.Id));

        Events.Count(e => e.Type == KitchenEventType.Created).Should().Be(2);
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task CloseRemotely_AfterLock_PublishesComandaChangedOnly()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Events.Clear();

        c = await Service.CloseRemotelyAsync(RemoteClose(c.Id));

        Events.Should().ContainSingle();
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task CancelConfirmedItem_PublishesItemCancelledThenComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        await Service.ConfirmPendingNowAsync(c.Id);
        var approval = await CreateCancelOverrideAsync();
        Events.Clear();

        c = await Service.CancelConfirmedItemAsync(c.Id, c.Items[0].Id, approval);

        Events.Should().HaveCount(2);
        Events[0].Type.Should().Be(KitchenEventType.ItemCancelled);
        AssertLastIsComandaChanged(c);
    }

    [Fact]
    public async Task EventsAreNotPublishedWithoutABus()
    {
        var silent = new Infrastructure.Data.Services.ComandaService(Db, Settings, Clock, null, Master);
        Events.Clear();

        var c = await silent.OpenComandaAsync("Mesa 7", "Ana");
        await silent.AddItemAsync(c.Id, Burger.Id);
        await silent.LockForPaymentAsync(c.Id, SatelliteCode);

        Events.Should().BeEmpty();
    }
}
