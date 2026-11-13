using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Core.Application.Services;

/// <summary>
/// Identity of THIS register, available to every service (singleton, filled once at start-up from
/// ITerminalService.EnsureRegisteredAsync). Services take it as an optional ctor parameter: when it is null
/// or not yet set (unit tests, tooling) they behave as a single-register install (no terminal filter, TerminalId null).
/// </summary>
public interface ITerminalContext
{
    bool IsSet { get; }
    Guid Id { get; }
    string Code { get; }
    string Name { get; }
    TerminalKind Kind { get; }
    bool IsSatellite => Kind == TerminalKind.Satellite;
}

/// <summary>Default mutable implementation; registered as singleton and set by the desktop after migrations.</summary>
public sealed class TerminalContext : ITerminalContext
{
    public bool IsSet { get; private set; }
    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public TerminalKind Kind { get; private set; }

    public void Set(TerminalDto terminal)
    {
        Id = terminal.Id; Code = terminal.Code; Name = terminal.Name; Kind = terminal.Kind; IsSet = true;
    }

    public static TerminalContext For(Guid id, string code, string name, TerminalKind kind)
    {
        var c = new TerminalContext();
        c.Set(new TerminalDto { Id = id, Code = code, Name = name, Kind = kind });
        return c;
    }
}

public interface ITerminalService
{
    /// <summary>
    /// Upserts the local row for this register (by normalized code) and returns it. On a Master the first call also
    /// ADOPTS legacy rows: CashShifts/Orders/BillPayments with TerminalId == null get this terminal's id/code
    /// (they were created before multi-register support and belong to the master).
    /// </summary>
    Task<TerminalDto> EnsureRegisteredAsync(string code, string name, TerminalKind kind, string? appVersion = null, CancellationToken ct = default);

    /// <summary>All terminals known to this database, master first then by code, with the open-shift columns filled.</summary>
    Task<IReadOnlyList<TerminalDto>> GetAllAsync(CancellationToken ct = default);

    Task<TerminalDto?> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Master side: records a satellite heartbeat (upsert by code, Kind from the request, LastSeenAt = now, LastAddress).
    /// Throws InvalidOperationException if the code equals this terminal's own code or the request says Master.
    /// </summary>
    Task<TerminalDto> HeartbeatAsync(TerminalHeartbeatRequest request, string? remoteAddress, CancellationToken ct = default);

    /// <summary>Satellite side: remembers when the catalog was last pulled (own row).</summary>
    Task MarkCatalogPulledAsync(CancellationToken ct = default);
}

/// <summary>Catalog replication master → satellite. Ids are preserved.</summary>
public interface ICatalogSyncService
{
    /// <summary>Master: every category, tax, product, biller and user (active AND inactive, so deactivations propagate).</summary>
    Task<CatalogSnapshotDto> BuildSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Satellite: upsert by id in one transaction (categories and taxes before products). Rows present locally but
    /// absent from the snapshot are deactivated (IsActive=false), never deleted (sales reference them). A local row
    /// with an unknown id but the same natural key as a snapshot row (Sku / name, case- and accent-insensitive — the
    /// demo seed of a fresh database) is a "twin": deleted when nothing references it, otherwise deactivated and its
    /// key mangled ("SKU~1a2b3c4d", "Nombre (local)") so the master's row can be inserted.
    /// Throws InvalidOperationException when called on a Master terminal.
    /// </summary>
    Task<CatalogApplyResult> ApplySnapshotAsync(CatalogSnapshotDto snapshot, CancellationToken ct = default);
}

/// <summary>Master side of the Outbox: applies one satellite envelope into this database.</summary>
public interface ISyncInboxService
{
    /// <summary>
    /// Idempotent by envelope Id (SyncInboxReceipt). Applies the replicated event types (see SyncEventTypes),
    /// Ignores the rest, Rejects malformed payloads / own-terminal envelopes / calls on a Satellite.
    /// Entity rows + receipt + Terminal.LastSyncAt are saved in ONE SaveChanges. Never throws for a bad payload
    /// (returns Rejected with the reason); database failures propagate.
    /// </summary>
    Task<SyncInboxResult> ApplyAsync(SyncEnvelopeDto envelope, CancellationToken ct = default);

    /// <summary>Receipts newest first (for the "🖥️ Cajas" screen and tests).</summary>
    Task<IReadOnlyList<SyncInboxReceiptDto>> GetRecentAsync(int take = 50, CancellationToken ct = default);
}

public sealed record SyncInboxReceiptDto(Guid MessageId, string TerminalCode, string EventType, Guid AggregateId, SyncInboxOutcome Outcome, string? Error, DateTime ReceivedAt);

/// <summary>
/// Satellite → master HTTP client (base URL = TerminalSettings.MasterUrl, Bearer = TerminalSettings.ApiKey).
/// Throws HttpRequestException / TimeoutException when the master is unreachable; callers treat that as "offline".
/// </summary>
public interface IMasterApiClient
{
    /// <summary>GET {MasterUrl}/api/catalog/snapshot</summary>
    Task<CatalogSnapshotDto> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>POST {MasterUrl}/api/terminals/heartbeat → the master's view of this terminal.</summary>
    Task<TerminalDto> HeartbeatAsync(TerminalHeartbeatRequest request, CancellationToken ct = default);

    /// <summary>GET {MasterUrl}/health → true when it answers 200.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}
