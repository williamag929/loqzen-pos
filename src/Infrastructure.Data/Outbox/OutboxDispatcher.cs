using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.Sync;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Outbox;

/// <summary>
/// Pushes Pending <see cref="OutboxMessage"/> rows through the <see cref="IOutboxSink"/>, oldest first.
/// Each message is saved right after its attempt (one SaveChanges per message) so a crash mid-batch loses at
/// most the outcome of one delivery, and a message that already went out is never re-sent.
/// Delivery errors never escape: they are recorded on the message (RetryCount / LastError) and drive an
/// exponential backoff. Database errors DO propagate (they mean something is wrong with the local store).
/// </summary>
public class OutboxDispatcher : IOutboxDispatcher
{
    /// <summary>Upper bound of the per-message backoff.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(300);

    private const int MaxErrorLength = 500;

    private readonly PosDbContext _db;
    private readonly IOutboxSink _sink;
    private readonly SyncSettings _settings;
    private readonly TimeProvider _clock;

    public OutboxDispatcher(PosDbContext db, IOutboxSink sink, SyncSettings settings, TimeProvider clock)
    {
        _db = db;
        _sink = sink;
        _settings = settings;
        _clock = clock;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Backoff after <paramref name="retryCount"/> failed attempts: <c>min(2^retryCount, 300)</c> seconds
    /// (2, 4, 8, 16, 32, 64, 128, 256, 300, 300, ... for RetryCount 1, 2, 3, ...). Measured from the last attempt (UpdatedAt ?? CreatedAt).
    /// </summary>
    public static TimeSpan BackoffFor(int retryCount)
    {
        if (retryCount <= 0) return TimeSpan.Zero;
        var seconds = Math.Min(Math.Pow(2, retryCount), MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public async Task<OutboxDispatchResult> DispatchPendingAsync(CancellationToken ct = default)
    {
        // Shutdown already requested: leave quietly instead of throwing from the first query.
        if (ct.IsCancellationRequested) return new OutboxDispatchResult(0, 0, 0);
        // Sync off (standalone store): do not even open the database.
        if (!_settings.Enabled) return new OutboxDispatchResult(0, 0, 0);

        var batchSize = Math.Max(1, _settings.BatchSize);
        var batch = await _db.OutboxMessages
            .Where(m => m.Status == SyncStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        int sent = 0, failed = 0, skipped = 0;
        foreach (var message in batch)
        {
            if (ct.IsCancellationRequested) break;

            var now = UtcNow;
            if (message.RetryCount > 0)
            {
                var lastAttempt = message.UpdatedAt ?? message.CreatedAt;
                if (now < lastAttempt + BackoffFor(message.RetryCount))
                {
                    skipped++;
                    continue;
                }
            }

            try
            {
                await _sink.DeliverAsync(message, ct);
                message.Status = SyncStatus.Synced;
                message.ProcessedAt = now;
                message.LastError = null;
                message.UpdatedAt = now;
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller is shutting down: leave the message untouched for the next pass.
                break;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = Truncate(ex.Message, MaxErrorLength);
                message.Status = message.RetryCount >= Math.Max(1, _settings.MaxRetries)
                    ? SyncStatus.Failed
                    : SyncStatus.Pending;
                message.UpdatedAt = now;
                failed++;
            }

            // One commit per message: a crash loses at most this attempt's bookkeeping.
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        return new OutboxDispatchResult(sent, failed, skipped);
    }

    public async Task<(int Pending, int Failed, int Synced)> GetCountsAsync(CancellationToken ct = default)
    {
        var pending = await _db.OutboxMessages.CountAsync(m => m.Status == SyncStatus.Pending, ct);
        var failedCount = await _db.OutboxMessages.CountAsync(m => m.Status == SyncStatus.Failed, ct);
        var synced = await _db.OutboxMessages.CountAsync(m => m.Status == SyncStatus.Synced, ct);
        return (pending, failedCount, synced);
    }

    public async Task<int> RetryFailedAsync(CancellationToken ct = default)
    {
        var failedMessages = await _db.OutboxMessages
            .Where(m => m.Status == SyncStatus.Failed)
            .ToListAsync(ct);
        if (failedMessages.Count == 0) return 0;

        var now = UtcNow;
        foreach (var m in failedMessages)
        {
            m.Status = SyncStatus.Pending;
            m.RetryCount = 0;
            m.LastError = null;
            m.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return failedMessages.Count;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
