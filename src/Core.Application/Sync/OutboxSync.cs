using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Domain.Entities;

namespace Core.Application.Sync;

/// <summary>Bound from appsettings.json section "Sync": where pending Outbox messages are delivered.</summary>
public class SyncSettings
{
    /// <summary>Off by default: a standalone store has no ERP. When off nothing is ever sent (messages stay Pending).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>HTTP endpoint that receives one JSON envelope per message (POST). The Docker webhook mock listens on http://localhost:5000/webhook.</summary>
    public string WebhookUrl { get; set; } = "http://localhost:5000/webhook";

    /// <summary>Optional bearer token sent as Authorization header.</summary>
    public string? ApiKey { get; set; }

    public int IntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 50;

    /// <summary>After this many failed deliveries a message is marked Failed and skipped (operator can reset it).</summary>
    public int MaxRetries { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Delivers ONE message to the outside world. Throw to signal a failed attempt.</summary>
public interface IOutboxSink
{
    Task DeliverAsync(OutboxMessage message, CancellationToken ct = default);
}

/// <summary>Result of one dispatcher pass.</summary>
public sealed record OutboxDispatchResult(int Sent, int Failed, int SkippedBackoff);

/// <summary>
/// Pushes Pending Outbox messages through the sink, oldest first, with exponential backoff per message
/// (RetryCount drives the delay) and marks them Synced / Failed. Safe to call from a timer; never throws
/// for delivery errors (they are recorded on the message). Offline never blocks sales: this runs AFTER commit.
/// </summary>
public interface IOutboxDispatcher
{
    Task<OutboxDispatchResult> DispatchPendingAsync(CancellationToken ct = default);

    /// <summary>Counts by status for the UI ("3 pendientes · 1 fallido").</summary>
    Task<(int Pending, int Failed, int Synced)> GetCountsAsync(CancellationToken ct = default);

    /// <summary>Puts Failed messages back to Pending with RetryCount = 0.</summary>
    Task<int> RetryFailedAsync(CancellationToken ct = default);
}
