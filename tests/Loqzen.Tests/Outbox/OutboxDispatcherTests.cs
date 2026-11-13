using Core.Application.Sync;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Loqzen.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Outbox;

/// <summary>
/// Outbox dispatcher against REAL SQLite (ordering, Take, CountAsync translate to SQL): oldest first,
/// one SaveChanges per message, exponential backoff by RetryCount, Failed after MaxRetries, cancellation.
/// </summary>
public class OutboxDispatcherTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly SyncSettings _settings = new() { Enabled = true, BatchSize = 50, MaxRetries = 10 };
    private readonly FakeSink _sink = new();

    public OutboxDispatcherTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private OutboxDispatcher Dispatcher() => new(_db, _sink, _settings, _clock);

    private OutboxMessage Seed(string eventType, DateTime? createdAt = null, SyncStatus status = SyncStatus.Pending,
        int retryCount = 0, DateTime? updatedAt = null)
    {
        var m = new OutboxMessage
        {
            EventType = eventType,
            AggregateType = "Test",
            AggregateId = Guid.NewGuid(),
            PayloadJson = "{\"n\":1}",
            Status = status,
            RetryCount = retryCount,
            CreatedAt = createdAt ?? Now,
            UpdatedAt = updatedAt
        };
        _db.OutboxMessages.Add(m);
        _db.SaveChanges();
        return m;
    }

    private Task<OutboxMessage> Reload(Guid id) => _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);

    /// <summary>Records every delivery; fails the first <see cref="FailTimes"/> calls (or whatever <see cref="ShouldFail"/> says).</summary>
    private sealed class FakeSink : IOutboxSink
    {
        public int FailTimes { get; set; }
        public int Calls { get; private set; }
        public List<OutboxMessage> Delivered { get; } = new();
        public Func<OutboxMessage, bool>? ShouldFail { get; set; }
        public Func<OutboxMessage, Exception> ExceptionFactory { get; set; } = _ => new HttpRequestException("HTTP 500 Internal Server Error");
        public Func<OutboxMessage, CancellationToken, Task>? OnCall { get; set; }

        public async Task DeliverAsync(OutboxMessage message, CancellationToken ct = default)
        {
            Calls++;
            if (OnCall != null) await OnCall(message, ct);
            var fail = ShouldFail?.Invoke(message) ?? FailTimes-- > 0;
            if (fail) throw ExceptionFactory(message);
            Delivered.Add(message);
        }
    }

    // ---- 1. disabled ----

    [Fact]
    public async Task Disabled_TouchesNothing()
    {
        _settings.Enabled = false;
        var m = Seed("A");

        var result = await Dispatcher().DispatchPendingAsync();

        result.Should().Be(new OutboxDispatchResult(0, 0, 0));
        _sink.Calls.Should().Be(0);
        var stored = await Reload(m.Id);
        stored.Status.Should().Be(SyncStatus.Pending);
        stored.UpdatedAt.Should().BeNull();
    }

    // ---- 2. happy path ----

    [Fact]
    public async Task HappyPath_SendsAllPending_OldestFirst_AndMarksSynced()
    {
        var t0 = Now;
        // Inserted newest first: ordering must come from CreatedAt, not insertion.
        var c = Seed("C", t0.AddSeconds(-10));
        var b = Seed("B", t0.AddSeconds(-20));
        var a = Seed("A", t0.AddSeconds(-30));
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = await Dispatcher().DispatchPendingAsync();

        result.Should().Be(new OutboxDispatchResult(3, 0, 0));
        _sink.Delivered.Select(m => m.EventType).Should().Equal("A", "B", "C");

        foreach (var id in new[] { a.Id, b.Id, c.Id })
        {
            var stored = await Reload(id);
            stored.Status.Should().Be(SyncStatus.Synced);
            stored.ProcessedAt.Should().Be(Now);
            stored.UpdatedAt.Should().Be(Now);
            stored.LastError.Should().BeNull();
            stored.RetryCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task HappyPath_SecondPass_HasNothingToSend()
    {
        Seed("A");
        await Dispatcher().DispatchPendingAsync();

        var result = await Dispatcher().DispatchPendingAsync();

        result.Should().Be(new OutboxDispatchResult(0, 0, 0));
        _sink.Calls.Should().Be(1);
    }

    // ---- 3. batch size ----

    [Fact]
    public async Task BatchSize_LimitsOnePass_ToTheOldestMessages()
    {
        _settings.BatchSize = 2;
        var t0 = Now;
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(Seed($"M{i}", t0.AddSeconds(i)).Id);

        var result = await Dispatcher().DispatchPendingAsync();

        result.Should().Be(new OutboxDispatchResult(2, 0, 0));
        _sink.Delivered.Select(m => m.EventType).Should().Equal("M0", "M1");
        (await _db.OutboxMessages.CountAsync(m => m.Status == SyncStatus.Pending)).Should().Be(3);
        (await _db.OutboxMessages.CountAsync(m => m.Status == SyncStatus.Synced)).Should().Be(2);

        var second = await Dispatcher().DispatchPendingAsync();
        second.Sent.Should().Be(2);
        _sink.Delivered.Select(m => m.EventType).Should().Equal("M0", "M1", "M2", "M3");
    }

    // ---- 4. failure bookkeeping + immediate backoff ----

    [Fact]
    public async Task Failure_RecordsRetryAndError_StaysPending_AndIsSkippedImmediatelyAfter()
    {
        _sink.FailTimes = 1;
        var m = Seed("A", Now.AddMinutes(-5));
        _clock.Advance(TimeSpan.FromSeconds(7));

        var first = await Dispatcher().DispatchPendingAsync();

        first.Should().Be(new OutboxDispatchResult(0, 1, 0));
        var stored = await Reload(m.Id);
        stored.Status.Should().Be(SyncStatus.Pending);
        stored.RetryCount.Should().Be(1);
        stored.LastError.Should().Be("HTTP 500 Internal Server Error");
        stored.UpdatedAt.Should().Be(Now);
        stored.ProcessedAt.Should().BeNull();

        var second = await Dispatcher().DispatchPendingAsync();

        second.Should().Be(new OutboxDispatchResult(0, 0, 1));
        _sink.Calls.Should().Be(1, "backoff: the sink is not called again right away");
        var unchanged = await Reload(m.Id);
        unchanged.RetryCount.Should().Be(1);
        unchanged.UpdatedAt.Should().Be(stored.UpdatedAt);
        unchanged.LastError.Should().Be(stored.LastError);
    }

    // ---- 5. backoff timing ----

    [Fact]
    public async Task Backoff_AfterFirstFailure_SkipsBefore2s_AttemptsAt2s()
    {
        _sink.FailTimes = 1;
        var m = Seed("A");
        await Dispatcher().DispatchPendingAsync();
        (await Reload(m.Id)).RetryCount.Should().Be(1);

        _clock.Advance(TimeSpan.FromSeconds(1.9));
        (await Dispatcher().DispatchPendingAsync()).Should().Be(new OutboxDispatchResult(0, 0, 1));
        _sink.Calls.Should().Be(1);

        _clock.Advance(TimeSpan.FromSeconds(0.1));
        (await Dispatcher().DispatchPendingAsync()).Should().Be(new OutboxDispatchResult(1, 0, 0));
        _sink.Calls.Should().Be(2);
        (await Reload(m.Id)).Status.Should().Be(SyncStatus.Synced);
    }

    [Fact]
    public async Task Backoff_WithRetryCount3_Skips7s_Attempts8s_MeasuredFromUpdatedAt()
    {
        var m = Seed("A", createdAt: Now.AddHours(-1), retryCount: 3, updatedAt: Now);

        _clock.Advance(TimeSpan.FromSeconds(7));
        (await Dispatcher().DispatchPendingAsync()).Should().Be(new OutboxDispatchResult(0, 0, 1));
        _sink.Calls.Should().Be(0);

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Dispatcher().DispatchPendingAsync()).Should().Be(new OutboxDispatchResult(1, 0, 0));
        _sink.Calls.Should().Be(1);
        (await Reload(m.Id)).Status.Should().Be(SyncStatus.Synced);
    }

    [Fact]
    public async Task Backoff_WithoutUpdatedAt_IsMeasuredFromCreatedAt()
    {
        Seed("A", createdAt: Now, retryCount: 2, updatedAt: null);

        _clock.Advance(TimeSpan.FromSeconds(3.9));
        (await Dispatcher().DispatchPendingAsync()).SkippedBackoff.Should().Be(1);

        _clock.Advance(TimeSpan.FromSeconds(0.1));
        (await Dispatcher().DispatchPendingAsync()).Sent.Should().Be(1);
    }

    // ---- 6. BackoffFor ----

    [Fact]
    public void BackoffFor_IsZeroThenPowersOfTwo_CappedAt300()
    {
        Enumerable.Range(0, 13).Select(n => OutboxDispatcher.BackoffFor(n).TotalSeconds)
            .Should().Equal(0, 2, 4, 8, 16, 32, 64, 128, 256, 300, 300, 300, 300);
        OutboxDispatcher.BackoffFor(-1).Should().Be(TimeSpan.Zero);
        OutboxDispatcher.MaxBackoff.Should().Be(TimeSpan.FromSeconds(300));
    }

    // ---- 7. error truncation ----

    [Fact]
    public async Task LastError_IsTruncatedTo500Chars()
    {
        var longMessage = new string('x', 600);
        _sink.FailTimes = 1;
        _sink.ExceptionFactory = _ => new InvalidOperationException(longMessage);
        var m = Seed("A");

        await Dispatcher().DispatchPendingAsync();

        var stored = await Reload(m.Id);
        stored.LastError.Should().HaveLength(500);
        stored.LastError.Should().Be(longMessage[..500]);
    }

    // ---- 8. MaxRetries ----

    [Fact]
    public async Task MaxRetries_MarksFailed_AndStopsCallingTheSink()
    {
        _settings.MaxRetries = 3;
        _sink.FailTimes = 10;
        var m = Seed("A");

        await Dispatcher().DispatchPendingAsync();                      // retry 1
        (await Reload(m.Id)).Status.Should().Be(SyncStatus.Pending);
        _clock.Advance(OutboxDispatcher.BackoffFor(1));
        await Dispatcher().DispatchPendingAsync();                      // retry 2
        (await Reload(m.Id)).Status.Should().Be(SyncStatus.Pending);
        _clock.Advance(OutboxDispatcher.BackoffFor(2));
        var third = await Dispatcher().DispatchPendingAsync();          // retry 3 → Failed

        third.Should().Be(new OutboxDispatchResult(0, 1, 0));
        var failed = await Reload(m.Id);
        failed.Status.Should().Be(SyncStatus.Failed);
        failed.RetryCount.Should().Be(3);
        failed.LastError.Should().NotBeNullOrEmpty();
        _sink.Calls.Should().Be(3);

        _clock.Advance(TimeSpan.FromHours(1));
        var fourth = await Dispatcher().DispatchPendingAsync();

        fourth.Should().Be(new OutboxDispatchResult(0, 0, 0));
        _sink.Calls.Should().Be(3, "a Failed message is no longer picked up");
    }

    // ---- 9. partial batch ----

    [Fact]
    public async Task PartialBatch_OneFailureDoesNotAffectTheOthers()
    {
        var t0 = Now;
        var a = Seed("A", t0.AddSeconds(1));
        var bad = Seed("BAD", t0.AddSeconds(2));
        var c = Seed("C", t0.AddSeconds(3));
        _sink.ShouldFail = m => m.EventType == "BAD";

        var result = await Dispatcher().DispatchPendingAsync();

        result.Should().Be(new OutboxDispatchResult(2, 1, 0));
        _sink.Delivered.Select(m => m.EventType).Should().Equal("A", "C");
        (await Reload(a.Id)).Status.Should().Be(SyncStatus.Synced);
        (await Reload(c.Id)).Status.Should().Be(SyncStatus.Synced);
        var stored = await Reload(bad.Id);
        stored.Status.Should().Be(SyncStatus.Pending);
        stored.RetryCount.Should().Be(1);
        stored.LastError.Should().Contain("500");
    }

    // ---- 10/11. cancellation ----

    [Fact]
    public async Task PreCancelledToken_SendsNothing_AndChangesNothing()
    {
        var m = Seed("A");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OutboxDispatchResult? result = null;
        var ex = await Record.ExceptionAsync(async () => result = await Dispatcher().DispatchPendingAsync(cts.Token));

        // Either way is acceptable to the caller (a timer that is shutting down), but nothing may be sent or changed.
        if (ex != null) ex.Should().BeAssignableTo<OperationCanceledException>();
        else result.Should().Be(new OutboxDispatchResult(0, 0, 0));
        _sink.Calls.Should().Be(0);
        var stored = await Reload(m.Id);
        stored.Status.Should().Be(SyncStatus.Pending);
        stored.RetryCount.Should().Be(0);
        stored.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task CancellationInsideSink_StopsThePass_AndLeavesTheMessageUntouched()
    {
        var t0 = Now;
        var a = Seed("A", t0.AddSeconds(1));
        var b = Seed("B", t0.AddSeconds(2));
        var c = Seed("C", t0.AddSeconds(3));
        using var cts = new CancellationTokenSource();
        _sink.OnCall = (_, ct) =>
        {
            if (_sink.Calls == 2) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        };

        var result = await Dispatcher().DispatchPendingAsync(cts.Token);

        result.Should().Be(new OutboxDispatchResult(1, 0, 0));
        _sink.Calls.Should().Be(2, "the third message is never attempted");
        (await Reload(a.Id)).Status.Should().Be(SyncStatus.Synced);
        foreach (var id in new[] { b.Id, c.Id })
        {
            var stored = await Reload(id);
            stored.Status.Should().Be(SyncStatus.Pending);
            stored.RetryCount.Should().Be(0, "a shutdown is not a delivery failure");
            stored.LastError.Should().BeNull();
            stored.UpdatedAt.Should().BeNull();
        }
    }

    // ---- 12. counts ----

    [Fact]
    public async Task GetCounts_ReturnsPendingFailedSynced()
    {
        Seed("P1"); Seed("P2");
        Seed("F1", status: SyncStatus.Failed, retryCount: 10);
        Seed("S1", status: SyncStatus.Synced); Seed("S2", status: SyncStatus.Synced); Seed("S3", status: SyncStatus.Synced);

        var counts = await Dispatcher().GetCountsAsync();

        counts.Should().Be((2, 1, 3));
    }

    [Fact]
    public async Task GetCounts_EmptyTable_IsAllZero()
    {
        (await Dispatcher().GetCountsAsync()).Should().Be((0, 0, 0));
    }

    // ---- 13. retry failed ----

    [Fact]
    public async Task RetryFailed_ResetsFailedMessagesToPending_AndReturnsCount()
    {
        var f1 = Seed("F1", status: SyncStatus.Failed, retryCount: 10, updatedAt: Now.AddHours(-1));
        var f2 = Seed("F2", status: SyncStatus.Failed, retryCount: 4, updatedAt: Now.AddHours(-1));
        var s = Seed("S", status: SyncStatus.Synced);
        var p = Seed("P");
        _db.OutboxMessages.Where(m => m.Status == SyncStatus.Failed).ToList().ForEach(m => m.LastError = "boom");
        await _db.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));

        var count = await Dispatcher().RetryFailedAsync();

        count.Should().Be(2);
        foreach (var id in new[] { f1.Id, f2.Id })
        {
            var stored = await Reload(id);
            stored.Status.Should().Be(SyncStatus.Pending);
            stored.RetryCount.Should().Be(0);
            stored.LastError.Should().BeNull();
            stored.UpdatedAt.Should().Be(Now);
        }
        (await Reload(s.Id)).Status.Should().Be(SyncStatus.Synced);
        (await Reload(p.Id)).Status.Should().Be(SyncStatus.Pending);

        // They are deliverable again immediately (RetryCount 0 = no backoff).
        var result = await Dispatcher().DispatchPendingAsync();
        result.Sent.Should().Be(3);
    }

    [Fact]
    public async Task RetryFailed_WhenNoneFailed_ReturnsZero()
    {
        Seed("P");
        Seed("S", status: SyncStatus.Synced);

        (await Dispatcher().RetryFailedAsync()).Should().Be(0);
    }
}
