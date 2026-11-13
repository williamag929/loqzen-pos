using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>KitchenService publishes a KitchenEvent after each committed mutation; Create/GetById/station filter.</summary>
public class KitchenServiceEventsTests : IDisposable
{
    private readonly PosDbContext _db = TestDbContextFactory.Create();
    private readonly TestClock _clock = new();
    private readonly KitchenEventBus _bus = new();
    private readonly List<KitchenEvent> _events = new();
    private readonly KitchenService _svc;

    public KitchenServiceEventsTests()
    {
        _bus.Subscribe(_events.Add);
        _svc = new KitchenService(_db, _clock, _bus);
    }

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static KitchenOrder NewOrder(string station = "Cocina Caliente", string table = "Mesa 4", string number = "VR-1")
    {
        var ko = new KitchenOrder { OrderNumber = number, TableNumber = table, Station = station };
        ko.Items.Add(new KitchenOrderItem { ProductId = Guid.NewGuid(), ProductName = "Hamburguesa", Quantity = 2 });
        return ko;
    }

    // ---- CreateAsync ----

    [Fact]
    public async Task Create_PersistsOrder_AddsOutboxRow_AndPublishesCreated()
    {
        _clock.Advance(TimeSpan.FromMinutes(3));
        var ko = NewOrder(station: "Barra", table: "Mesa 7", number: "VR-42");

        var saved = await _svc.CreateAsync(ko);

        saved.Id.Should().Be(ko.Id);
        saved.UpdatedAt.Should().Be(Now);
        (await _db.KitchenOrders.Include(k => k.Items).SingleAsync()).Items.Should().ContainSingle();

        var outbox = await _db.OutboxMessages.SingleAsync();
        outbox.EventType.Should().Be("KitchenOrderCreated");
        outbox.AggregateType.Should().Be("KitchenOrder");
        outbox.AggregateId.Should().Be(ko.Id);
        outbox.Status.Should().Be(SyncStatus.Pending);
        outbox.PayloadJson.Should().Contain("Barra").And.Contain("VR-42").And.Contain("Mesa 7");

        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.Created);
        e.KitchenOrderId.Should().Be(ko.Id);
        e.Station.Should().Be("Barra");
        e.OrderNumber.Should().Be("VR-42");
        e.TableNumber.Should().Be("Mesa 7");
        e.Status.Should().Be(KitchenOrderStatus.Pending);
        e.OccurredAt.Should().Be(Now);
    }

    [Fact]
    public async Task Create_SetsCreatedAtFromClock_WhenDefault()
    {
        _clock.Advance(TimeSpan.FromHours(1));
        var ko = NewOrder();
        ko.CreatedAt = default;

        await _svc.CreateAsync(ko);

        ko.CreatedAt.Should().Be(Now);
        (await _db.KitchenOrders.AsNoTracking().SingleAsync()).CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Create_KeepsExplicitCreatedAt()
    {
        var explicitTime = new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc);
        var ko = NewOrder();
        ko.CreatedAt = explicitTime;

        await _svc.CreateAsync(ko);

        ko.CreatedAt.Should().Be(explicitTime, "a tablet may send its own timestamp");
    }

    [Fact]
    public async Task Create_WithoutItems_Throws_AndPersistsNothing()
    {
        var ko = new KitchenOrder { OrderNumber = "VR-1" };

        var act = () => _svc.CreateAsync(ko);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*productos*");
        (await _db.KitchenOrders.CountAsync()).Should().Be(0);
        (await _db.OutboxMessages.CountAsync()).Should().Be(0);
        _events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithBlankOrderNumber_Throws(string number)
    {
        var ko = NewOrder(number: number);
        var act = () => _svc.CreateAsync(ko);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*OrderNumber*");
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_NullOrder_Throws()
    {
        var act = () => _svc.CreateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- Advance / MarkPrinted / Undo ----

    [Fact]
    public async Task Advance_ThreeTimes_PublishesStatusChanged_WithEachNewStatus()
    {
        var ko = await _svc.CreateAsync(NewOrder());
        _events.Clear();

        await _svc.AdvanceAsync(ko.Id);
        await _svc.AdvanceAsync(ko.Id);
        await _svc.AdvanceAsync(ko.Id);

        _events.Should().HaveCount(3);
        _events.Should().OnlyContain(e => e.Type == KitchenEventType.StatusChanged && e.KitchenOrderId == ko.Id);
        _events.Select(e => e.Status).Should().Equal(
            KitchenOrderStatus.InPreparation, KitchenOrderStatus.Ready, KitchenOrderStatus.Delivered);
    }

    [Fact]
    public async Task Advance_AfterDelivered_Throws_AndPublishesNothing()
    {
        var ko = await _svc.CreateAsync(NewOrder());
        await _svc.AdvanceAsync(ko.Id);
        await _svc.AdvanceAsync(ko.Id);
        await _svc.AdvanceAsync(ko.Id);
        _events.Clear();

        var act = () => _svc.AdvanceAsync(ko.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkPrinted_Twice_PublishesPrintedExactlyOnce()
    {
        var ko = await _svc.CreateAsync(NewOrder());
        _events.Clear();

        await _svc.MarkPrintedAsync(ko.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _svc.MarkPrintedAsync(ko.Id);

        _events.Should().ContainSingle().Which.Type.Should().Be(KitchenEventType.Printed);
    }

    [Fact]
    public async Task UndoDeliver_PublishesUndone_WithReadyStatus()
    {
        var ko = await _svc.CreateAsync(NewOrder());
        await _svc.AdvanceAsync(ko.Id);
        await _svc.AdvanceAsync(ko.Id);
        await _svc.AdvanceAsync(ko.Id);
        _events.Clear();

        await _svc.UndoDeliverAsync(ko.Id);

        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.Undone);
        e.Status.Should().Be(KitchenOrderStatus.Ready);
        e.KitchenOrderId.Should().Be(ko.Id);
    }

    [Fact]
    public async Task UndoDeliver_WhenNotDelivered_Throws_AndPublishesNothing()
    {
        var ko = await _svc.CreateAsync(NewOrder());
        _events.Clear();

        var act = () => _svc.UndoDeliverAsync(ko.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*entregada*");
        _events.Should().BeEmpty();
    }

    // ---- queries ----

    [Fact]
    public async Task GetActive_FiltersByStation_CaseInsensitive()
    {
        await _svc.CreateAsync(NewOrder(station: "Barra", table: "Mesa 1"));
        await _svc.CreateAsync(NewOrder(station: "Cocina Caliente", table: "Mesa 2"));
        await _svc.CreateAsync(NewOrder(station: "BARRA", table: "Mesa 3"));

        var bar = await _svc.GetActiveAsync("barra");
        bar.Select(k => k.TableNumber).Should().BeEquivalentTo(new[] { "Mesa 1", "Mesa 3" });

        (await _svc.GetActiveAsync(" Barra ")).Should().HaveCount(2, "station is trimmed");
        (await _svc.GetActiveAsync(null)).Should().HaveCount(3);
        (await _svc.GetActiveAsync("")).Should().HaveCount(3);
        (await _svc.GetActiveAsync("Postres")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetActive_ExcludesDeliveredAndCancelled()
    {
        var delivered = await _svc.CreateAsync(NewOrder(table: "Mesa 1"));
        await _svc.AdvanceAsync(delivered.Id);
        await _svc.AdvanceAsync(delivered.Id);
        await _svc.AdvanceAsync(delivered.Id);
        var cancelled = NewOrder(table: "Mesa 2");
        cancelled.Status = KitchenOrderStatus.Cancelled;
        await _svc.CreateAsync(cancelled);
        await _svc.CreateAsync(NewOrder(table: "Mesa 3"));

        var active = await _svc.GetActiveAsync();

        active.Should().ContainSingle().Which.TableNumber.Should().Be("Mesa 3");
    }

    [Fact]
    public async Task GetById_ReturnsOrderWithItems_OrNullForUnknown()
    {
        var ko = await _svc.CreateAsync(NewOrder());

        var found = await _svc.GetByIdAsync(ko.Id);
        found.Should().NotBeNull();
        found!.Items.Should().ContainSingle();

        (await _svc.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ---- no bus ----

    [Fact]
    public async Task Service_WithoutBus_StillWorks()
    {
        var silent = new KitchenService(_db, _clock);

        var ko = await silent.CreateAsync(NewOrder());
        await silent.AdvanceAsync(ko.Id);
        await silent.MarkPrintedAsync(ko.Id);

        var stored = await silent.GetByIdAsync(ko.Id);
        stored!.Status.Should().Be(KitchenOrderStatus.InPreparation);
        stored.PrintedAt.Should().Be(Now);
        _events.Should().BeEmpty("this service has no bus");
    }
}
