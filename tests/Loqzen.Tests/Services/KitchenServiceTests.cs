using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Services;

public class KitchenServiceTests : IDisposable
{
    private readonly PosDbContext _db = TestDbContextFactory.Create();
    private readonly TestClock _clock = new();
    private readonly KitchenService _svc;

    public KitchenServiceTests() => _svc = new KitchenService(_db, _clock);
    public void Dispose() => _db.Dispose();

    private KitchenOrder Add(string table, DateTime createdAt, KitchenOrderStatus status = KitchenOrderStatus.Pending)
    {
        var ko = new KitchenOrder { OrderNumber = table, TableNumber = table, Status = status, CreatedAt = createdAt };
        _db.KitchenOrders.Add(ko);
        _db.SaveChanges();
        return ko;
    }

    [Fact]
    public async Task GetActive_IsInArrivalOrder_NotTableOrder_AndExcludesDelivered()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        Add("Mesa 9", now.AddMinutes(-10));
        Add("Mesa 1", now.AddMinutes(-2));
        Add("Mesa 5", now.AddMinutes(-20), KitchenOrderStatus.Delivered);

        var list = await _svc.GetActiveAsync();
        list.Select(k => k.TableNumber).Should().Equal("Mesa 9", "Mesa 1");
    }

    [Fact]
    public async Task Advance_FullCycle_SetsDeliveredAt_ThenUndoRestoresReady()
    {
        var ko = Add("Mesa 1", _clock.GetUtcNow().UtcDateTime);
        (await _svc.AdvanceAsync(ko.Id)).Status.Should().Be(KitchenOrderStatus.InPreparation);
        (await _svc.AdvanceAsync(ko.Id)).Status.Should().Be(KitchenOrderStatus.Ready);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var delivered = await _svc.AdvanceAsync(ko.Id);
        delivered.Status.Should().Be(KitchenOrderStatus.Delivered);
        delivered.DeliveredAt.Should().Be(_clock.GetUtcNow().UtcDateTime);

        var undone = await _svc.UndoDeliverAsync(ko.Id);
        undone.Status.Should().Be(KitchenOrderStatus.Ready);
        undone.DeliveredAt.Should().BeNull();
        (await _svc.GetActiveAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Advance_AfterDelivered_Throws()
    {
        var ko = Add("Mesa 1", _clock.GetUtcNow().UtcDateTime, KitchenOrderStatus.Delivered);
        var act = () => _svc.AdvanceAsync(ko.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MarkPrinted_KeepsFirstPrintTime_OnReprint()
    {
        var ko = Add("Mesa 1", _clock.GetUtcNow().UtcDateTime);
        var first = (await _svc.MarkPrintedAsync(ko.Id)).PrintedAt;
        first.Should().NotBeNull();
        _clock.Advance(TimeSpan.FromMinutes(3));
        (await _svc.MarkPrintedAsync(ko.Id)).PrintedAt.Should().Be(first);
    }
}

public class KdsTimingTests
{
    private static readonly KdsSettings S = new() { WarningMinutes = 5, MaxPrepMinutes = 15, ArchiveDelaySeconds = 20 };
    private static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, KdsUrgency.OnTime)]
    [InlineData(4.9, KdsUrgency.OnTime)]
    [InlineData(5, KdsUrgency.Warning)]
    [InlineData(15, KdsUrgency.Warning)]
    [InlineData(15.1, KdsUrgency.Late)]
    public void Urgency_FollowsConfiguredThresholds(double minutes, KdsUrgency expected) =>
        KdsTiming.Urgency(T0, T0.AddMinutes(minutes), S).Should().Be(expected);

    [Fact]
    public void ArchiveRemaining_CountsDownAndClampsAtZero()
    {
        KdsTiming.ArchiveRemainingSeconds(T0, T0.AddSeconds(5), S).Should().Be(15);
        KdsTiming.ArchiveRemainingSeconds(T0, T0.AddSeconds(30), S).Should().Be(0);
    }
}
