using System;
using Core.Domain.Common;
using Core.Domain.Enums;

namespace Core.Domain.Entities;

/// <summary>
/// One POS installation (cash register). The master's table lists itself plus every satellite that has
/// ever reported in; a satellite's table holds only itself. <see cref="Code"/> is the stable identity
/// shared across databases ("CAJA-01"); it is unique and case-insensitive.
/// </summary>
public class Terminal : BaseEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TerminalKind Kind { get; set; } = TerminalKind.Master;
    public bool IsActive { get; set; } = true;

    /// <summary>Last heartbeat / request seen by the master (UTC). Null on a terminal that never reported.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Last Outbox envelope applied by the master from this terminal (UTC).</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>Last catalog snapshot this terminal pulled from the master (UTC). Satellite side only.</summary>
    public DateTime? LastCatalogPullAt { get; set; }

    public string? AppVersion { get; set; }
    public string? LastAddress { get; set; }
}

/// <summary>
/// Idempotency ledger of the master's sync inbox: one row per Outbox message id ever received, so a
/// satellite that retries (timeout after the master committed) never duplicates a sale.
/// </summary>
public class SyncInboxReceipt : BaseEntity
{
    /// <summary>The satellite's OutboxMessage.Id (unique).</summary>
    public Guid MessageId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public SyncInboxOutcome Outcome { get; set; }
    public string? Error { get; set; }
    public DateTime ReceivedAt { get; set; }
}
