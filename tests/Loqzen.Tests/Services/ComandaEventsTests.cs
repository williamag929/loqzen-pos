using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>
/// ComandaService publishes KitchenEvents (Created per station ticket, ItemCancelled) only after the
/// SaveChanges that commits them, so the KDS never shows a ticket that is not in the DB.
/// </summary>
public class ComandaEventsTests : ComandaTestBase
{
    private const string Kitchen = "Cocina Caliente";
    private const string Bar = "Barra";

    private readonly KitchenEventBus _bus = new();
    private readonly List<KitchenEvent> _events = new();
    private readonly ComandaService _svc;

    public ComandaEventsTests()
    {
        _bus.Subscribe(_events.Add);
        _svc = CreateService(_bus);
    }

    private DateTime Now => Clock.GetUtcNow().UtcDateTime;

    private async Task<Guid> CreateOverrideAsync()
    {
        var o = new ManagerOverride
        {
            Action = AuthorizedAction.CancelKitchenItem,
            Description = "Anular producto en cocina",
            ManagerUserId = Guid.NewGuid(),
            ManagerName = "Gerente",
            AuthorizedAt = Now
        };
        Db.ManagerOverrides.Add(o);
        await Db.SaveChangesAsync();
        return o.Id;
    }

    [Fact]
    public async Task ConfirmPendingNow_BurgerAndSoda_PublishesOneCreatedPerStation()
    {
        var c = await _svc.OpenComandaAsync("Mesa 1", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);
        await _svc.AddItemAsync(c.Id, Soda.Id);
        _events.Should().BeEmpty("adding pending items does not reach the kitchen yet");

        await _svc.ConfirmPendingNowAsync(c.Id);

        _events.Should().HaveCount(2).And.OnlyContain(e => e.Type == KitchenEventType.Created);
        _events.Select(e => e.Station).Should().BeEquivalentTo(new[] { Kitchen, Bar });
        _events.Should().OnlyContain(e => e.OrderNumber == $"CMD-{c.Number}" && e.TableNumber == "Mesa 1"
                                          && e.Status == KitchenOrderStatus.Pending && e.OccurredAt == Now);

        var tickets = await Db.KitchenOrders.Select(k => k.Id).ToListAsync();
        _events.Select(e => e.KitchenOrderId).Should().BeEquivalentTo(tickets, "events reference committed tickets");
    }

    [Fact]
    public async Task ConfirmPendingNow_WithNothingPending_PublishesNothing()
    {
        var c = await _svc.OpenComandaAsync("Mesa 1", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);
        await _svc.ConfirmPendingNowAsync(c.Id);
        _events.Clear();

        await _svc.ConfirmPendingNowAsync(c.Id);

        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmDueItems_AfterDelay_PublishesCreated()
    {
        var c = await _svc.OpenComandaAsync("Mesa 2", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);
        Clock.Advance(TimeSpan.FromSeconds(31));

        var confirmed = await _svc.ConfirmDueItemsAsync();

        confirmed.Should().Be(1);
        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.Created);
        e.Station.Should().Be(Kitchen);
        e.TableNumber.Should().Be("Mesa 2");
        e.KitchenOrderId.Should().Be((await Db.KitchenOrders.SingleAsync()).Id);
    }

    [Fact]
    public async Task ConfirmDueItems_WhenNothingDue_PublishesNothing()
    {
        var c = await _svc.OpenComandaAsync("Mesa 2", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);
        Clock.Advance(TimeSpan.FromSeconds(10));

        var confirmed = await _svc.ConfirmDueItemsAsync();

        confirmed.Should().Be(0);
        _events.Should().BeEmpty();
        (await Db.KitchenOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmDueItems_TwoTablesDue_PublishesOneCreatedPerTicket()
    {
        var c1 = await _svc.OpenComandaAsync("Mesa 1", "Ana");
        await _svc.AddItemAsync(c1.Id, Burger.Id);
        var c2 = await _svc.OpenComandaAsync("Mesa 2", "Ana");
        await _svc.AddItemAsync(c2.Id, Soda.Id);
        Clock.Advance(TimeSpan.FromMinutes(1));

        await _svc.ConfirmDueItemsAsync();

        _events.Should().HaveCount(2);
        _events.Select(e => (e.TableNumber, e.Station)).Should().BeEquivalentTo(new[] { ("Mesa 1", Kitchen), ("Mesa 2", Bar) });
    }

    [Fact]
    public async Task CancelConfirmedItem_PublishesItemCancelled_WithTicketStation()
    {
        var c = await _svc.OpenComandaAsync("Mesa 3", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);
        var dto = await _svc.AddItemAsync(c.Id, Soda.Id);
        await _svc.ConfirmPendingNowAsync(c.Id);
        _events.Clear();
        var sodaItem = dto.Items.Single(i => i.ProductId == Soda.Id).Id;

        await _svc.CancelConfirmedItemAsync(c.Id, sodaItem, await CreateOverrideAsync());

        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.ItemCancelled);
        e.Station.Should().Be(Bar);
        e.TableNumber.Should().Be("Mesa 3");
        e.KitchenOrderId.Should().Be((await Db.KitchenOrders.SingleAsync(k => k.Station == Bar)).Id);
        e.Status.Should().Be(KitchenOrderStatus.Cancelled, "the only line of the bar ticket was struck");
    }

    [Fact]
    public async Task CancelConfirmedItem_OneOfTwoLines_EventStatusStaysPending()
    {
        var c = await _svc.OpenComandaAsync("Mesa 3", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);
        var dto = await _svc.AddItemAsync(c.Id, Burger.Id);
        await _svc.ConfirmPendingNowAsync(c.Id);
        _events.Clear();

        await _svc.CancelConfirmedItemAsync(c.Id, dto.Items.First().Id, await CreateOverrideAsync());

        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.ItemCancelled);
        e.Station.Should().Be(Kitchen);
        e.Status.Should().Be(KitchenOrderStatus.Pending);
    }

    [Fact]
    public async Task CancelConfirmedItem_Rejected_PublishesNothing()
    {
        var c = await _svc.OpenComandaAsync("Mesa 3", "Ana");
        var dto = await _svc.AddItemAsync(c.Id, Burger.Id);
        await _svc.ConfirmPendingNowAsync(c.Id);
        _events.Clear();

        var act = () => _svc.CancelConfirmedItemAsync(c.Id, dto.Items.Single().Id, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadyToPay_WithPendingItem_PublishesCreated()
    {
        var c = await _svc.OpenComandaAsync("Mesa 4", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);

        await _svc.MarkReadyToPayAsync(c.Id);

        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.Created);
        e.Station.Should().Be(Kitchen);
        e.TableNumber.Should().Be("Mesa 4");
    }

    [Fact]
    public async Task MarkReadyToPay_WithEverythingConfirmed_PublishesNothing()
    {
        var c = await _svc.OpenComandaAsync("Mesa 4", "Ana");
        await _svc.AddItemAsync(c.Id, Burger.Id);
        await _svc.ConfirmPendingNowAsync(c.Id);
        _events.Clear();

        await _svc.MarkReadyToPayAsync(c.Id);

        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAndPay_WithPendingItem_PublishesCreated()
    {
        var shiftId = await OpenShiftAsync();
        var c = await _svc.OpenComandaAsync("Mesa 5", "Ana");
        await _svc.AddItemAsync(c.Id, Soda.Id);

        var result = await _svc.CloseAndPayAsync(c.Id, shiftId, PaymentMethod.Cash, 10000m);

        result.Success.Should().BeTrue();
        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.Created);
        e.Station.Should().Be(Bar);
    }

    [Fact]
    public async Task ServiceWithoutBus_StillConfirms_AndPublishesNothing()
    {
        var c = await Service.OpenComandaAsync("Mesa 6", "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);

        await Service.ConfirmPendingNowAsync(c.Id);

        (await Db.KitchenOrders.CountAsync()).Should().Be(1);
        _events.Should().BeEmpty("the base Service has no bus");
    }
}
