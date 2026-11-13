using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Core.Domain.Enums;

namespace Core.Application.DTOs;

/// <summary>
/// Bound from appsettings.json section "Terminal". Identifies this register and, for a satellite, where the
/// master is. A satellite keeps selling offline on its own SQLite; the master consolidates.
/// </summary>
public class TerminalSettings
{
    /// <summary>Stable identity shared across databases ("CAJA-01"). Case-insensitive, trimmed, 2–20 chars.</summary>
    public string Code { get; set; } = "CAJA-01";

    public string Name { get; set; } = "Caja principal";

    public TerminalKind Mode { get; set; } = TerminalKind.Master;

    /// <summary>Base URL of the master's embedded server, satellite only ("http://192.168.1.10:5055").</summary>
    public string? MasterUrl { get; set; }

    /// <summary>Optional shared secret: sent as Bearer by the satellite, checked by the master on /api/sync/*, /api/terminals/*, /api/catalog/snapshot and /api/reports/*.</summary>
    public string? ApiKey { get; set; }

    /// <summary>How often a satellite pulls the catalog snapshot (products, categories, taxes, billers, users).</summary>
    public int CatalogPullSeconds { get; set; } = 60;

    /// <summary>How often a satellite reports itself to the master (and the master marks it online).</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    public bool IsSatellite => Mode == TerminalKind.Satellite;

    public static string NormalizeCode(string? code)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (c.Length is < 2 or > 20) throw new ArgumentException("El código de caja debe tener entre 2 y 20 caracteres.", nameof(code));
        return c;
    }
}

public class TerminalDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TerminalKind Kind { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastCatalogPullAt { get; set; }
    public string? AppVersion { get; set; }
    public string? LastAddress { get; set; }

    /// <summary>Open shift on that terminal as known by this database (null when none / not replicated yet).</summary>
    public int? OpenShiftNumber { get; set; }
    public string? OpenShiftCashier { get; set; }

    public string KindLabel => Kind == TerminalKind.Master ? "Principal" : "Satélite";

    /// <summary>"En línea" when seen within <paramref name="onlineWindow"/> of <paramref name="now"/>.</summary>
    public bool IsOnline(DateTime now, TimeSpan onlineWindow) => LastSeenAt is { } t && now - t <= onlineWindow;
}

/// <summary>Body of POST /api/terminals/heartbeat (satellite → master).</summary>
public sealed record TerminalHeartbeatRequest(string Code, string Name, TerminalKind Kind, string? AppVersion = null);

// ── Catalog snapshot (master → satellite) ────────────────────────────────────

/// <summary>
/// Everything a satellite needs to sell, keyed by the master's ids (ids are preserved on the satellite so
/// replicated sales reference the same products/users). PinHash travels here on purpose: the same PINs must work
/// on every register. Only ever served inside the LAN, optionally behind TerminalSettings.ApiKey.
/// </summary>
public class CatalogSnapshotDto
{
    public DateTime GeneratedAt { get; set; }
    public string MasterCode { get; set; } = string.Empty;
    public List<SnapshotCategory> Categories { get; set; } = new();
    public List<SnapshotTax> Taxes { get; set; } = new();
    public List<SnapshotProduct> Products { get; set; } = new();
    public List<SnapshotBiller> Billers { get; set; } = new();
    public List<SnapshotUser> Users { get; set; } = new();
}

public sealed record SnapshotCategory(Guid Id, string Name, string? ColorHex, string? Icon, int SortOrder, bool IsActive);
public sealed record SnapshotTax(Guid Id, string Name, decimal Rate, bool IsActive);
public sealed record SnapshotProduct(
    Guid Id, string Sku, string? Barcode, string Name, string? Description, decimal Price, decimal Cost,
    bool IsWeighed, bool IsActive, Guid? CategoryId, Guid? TaxId);
public sealed record SnapshotBiller(Guid Id, string Name, string? Code, decimal CommissionFixed, decimal CommissionPercent, string? ColorHex, int SortOrder, bool IsActive);
public sealed record SnapshotUser(Guid Id, string Name, string PinHash, UserRole Role, bool IsActive);

/// <summary>Counts of rows inserted/updated/deactivated by ICatalogSyncService.ApplySnapshotAsync.</summary>
public sealed record CatalogApplyResult(int Inserted, int Updated, int Deactivated)
{
    public int Total => Inserted + Updated + Deactivated;
}

// ── Sync envelope (satellite → master) ───────────────────────────────────────

/// <summary>
/// Wire shape of one Outbox message as POSTed by HttpOutboxSink and received by /api/sync/inbox.
/// <c>Payload</c> is the stored PayloadJson as a nested JSON value. <c>TerminalCode</c> identifies the sender.
/// </summary>
public class SyncEnvelopeDto
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? TerminalCode { get; set; }
    public JsonElement Payload { get; set; }
}

public sealed record SyncInboxResult(SyncInboxOutcome Outcome, string? Error = null)
{
    /// <summary>Applied / Duplicate / Ignored are all "the sender may mark the message Synced".</summary>
    public bool IsAcknowledged => Outcome != SyncInboxOutcome.Rejected;
}

/// <summary>
/// Event types the master replicates from the Outbox (every other type is <see cref="SyncInboxOutcome.Ignored"/>).
/// Payload contracts (camelCase JSON, enums as names, amounts as numbers, dates ISO-8601 UTC):
/// <list type="bullet">
/// <item><c>OrderCompleted</c> (Aggregate Order): { order: { id, orderNumber, status, subtotal, taxTotal, discountTotal, total, notes, customerId, cashShiftId, terminalId, terminalCode, createdAt, completedAt,
///   items: [{ id, productId, productName, unitPrice, quantity, weightKg, taxRate, taxAmount, subtotal, total }],
///   payments: [{ id, method, amount, tenderedAmount, changeAmount, reference, processedAt }] } }</item>
/// <item><c>CashShiftOpened</c> (CashShift): { shift: { id, shiftNumber, cashierId, cashierName, openedAt, openingAmount, openedByUserId, terminalId, terminalCode, status } }</item>
/// <item><c>CashShiftClosed</c> (CashShift): { shift: { ...same as opened..., closedAt, closingAmount, expectedAmount, floatLeftAmount, wasBlindCount, notes, closedByUserId, closedByName, differenceOverrideId,
///   countLines: [{ denomination, quantity }] } }  — upserts the shift (an opened event may have been lost).</item>
/// <item><c>CashMovementRecorded</c> (CashMovement): { movement: { id, cashShiftId, type, amount, reason, reference, userId, userName, managerOverrideId, billPaymentId, occurredAt } }</item>
/// <item><c>BillPaymentRecorded</c> / <c>BillPaymentVoided</c> (BillPayment): { billPayment: { id, cashShiftId, terminalId, terminalCode, billerId, billerName, accountReference, customerName, amount, commission, method, tenderedAmount, changeAmount, receiptNumber, status, userId, userName, paidAt, voidedAt, voidOverrideId, voidReason } }</item>
/// </list>
/// Older payloads without the nested object (pre-2026-11-13 "flat" OrderCompleted) are Rejected with a clear error.
/// </summary>
public static class SyncEventTypes
{
    public const string OrderCompleted = "OrderCompleted";
    public const string CashShiftOpened = "CashShiftOpened";
    public const string CashShiftClosed = "CashShiftClosed";
    public const string CashMovementRecorded = "CashMovementRecorded";
    public const string BillPaymentRecorded = "BillPaymentRecorded";
    public const string BillPaymentVoided = "BillPaymentVoided";

    public static readonly IReadOnlyList<string> Replicated = new[]
    { OrderCompleted, CashShiftOpened, CashShiftClosed, CashMovementRecorded, BillPaymentRecorded, BillPaymentVoided };

    public static bool IsReplicated(string? eventType) =>
        eventType != null && Replicated.Contains(eventType, StringComparer.Ordinal);
}

// ── Consolidated day (master) ────────────────────────────────────────────────

/// <summary>One register's slice of the day. Sums follow the same ExpectedCash rule as ShiftReconciliationDto.</summary>
public class TerminalDayDto
{
    public Guid? TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = string.Empty;
    public int ShiftCount { get; set; }
    public int OpenShiftCount { get; set; }
    public int OrderCount { get; set; }
    public decimal CashSales { get; set; }
    public decimal CardSales { get; set; }
    public decimal TransferSales { get; set; }
    public decimal OtherSales { get; set; }
    public decimal TotalSales => CashSales + CardSales + TransferSales + OtherSales;
    public int BillPaymentCount { get; set; }
    public decimal BillPaymentsTotal { get; set; }
    public decimal BillPaymentCommission { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    /// <summary>Σ ClosingAmount − ExpectedAmount over the CLOSED shifts of the day.</summary>
    public decimal Difference { get; set; }
    public List<ShiftHistoryItemDto> Shifts { get; set; } = new();
}

/// <summary>Cierre del día across registers: one TerminalDayDto per terminal that had activity + grand totals.</summary>
public class DayConsolidationDto
{
    public DateOnly Day { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public List<TerminalDayDto> Terminals { get; set; } = new();

    public int OrderCount => Terminals.Sum(t => t.OrderCount);
    public decimal CashSales => Terminals.Sum(t => t.CashSales);
    public decimal CardSales => Terminals.Sum(t => t.CardSales);
    public decimal TransferSales => Terminals.Sum(t => t.TransferSales);
    public decimal OtherSales => Terminals.Sum(t => t.OtherSales);
    public decimal TotalSales => Terminals.Sum(t => t.TotalSales);
    public decimal BillPaymentsTotal => Terminals.Sum(t => t.BillPaymentsTotal);
    public decimal BillPaymentCommission => Terminals.Sum(t => t.BillPaymentCommission);
    public decimal Difference => Terminals.Sum(t => t.Difference);
    public int OpenShiftCount => Terminals.Sum(t => t.OpenShiftCount);
}
