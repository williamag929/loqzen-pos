using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Sync;

/// <summary>
/// Master side of the Outbox: applies one satellite envelope (see <see cref="SyncEventTypes"/> for the payload
/// contract) into this database. Idempotent by envelope id through <see cref="SyncInboxReceipt"/>; a Rejected
/// receipt is re-evaluated on retry (the satellite may have fixed the message). Rows, receipt and the sender's
/// Terminal.LastSyncAt are saved in ONE SaveChanges. Payload problems never throw: they come back as Rejected.
/// Replicated rows always reference THIS database's Terminal row for the sender (by code), so TerminalId stays
/// consistent with the master's Terminals table whatever id the satellite used locally.
/// </summary>
public class SyncInboxService : ISyncInboxService
{
    private const string PlaceholderCashier = "(sincronizando…)";
    private const string UnknownCode = "DESCONOCIDA";
    private const int MaxErrorLength = 500;

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ITerminalContext? _terminal;

    public SyncInboxService(PosDbContext db, TimeProvider? clock = null, ITerminalContext? terminal = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _terminal = terminal;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Thrown while parsing/validating a payload; always turned into a Rejected result.</summary>
    private sealed class InvalidPayloadException : Exception
    {
        public InvalidPayloadException(string message) : base(message) { }
    }

    public async Task<SyncInboxResult> ApplyAsync(SyncEnvelopeDto envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (_terminal is { IsSet: true, Kind: TerminalKind.Satellite })
            return new SyncInboxResult(SyncInboxOutcome.Rejected, "Una caja satélite no recibe sincronización de otras cajas.");
        if (envelope.Id == Guid.Empty)
            return new SyncInboxResult(SyncInboxOutcome.Rejected, "El mensaje no tiene id.");

        // Idempotency: a receipt that was not Rejected means the message is already in. A Rejected one is
        // re-evaluated (the satellite may have fixed the payload) and its row updated in place.
        var receipt = await _db.SyncInboxReceipts.FirstOrDefaultAsync(r => r.MessageId == envelope.Id, ct);
        if (receipt != null && receipt.Outcome != SyncInboxOutcome.Rejected)
            return new SyncInboxResult(SyncInboxOutcome.Duplicate);

        var now = UtcNow;
        string? code = null;
        string? error = null;
        SyncInboxOutcome outcome;
        Terminal? sender = null;

        try
        {
            code = ParseSenderCode(envelope.TerminalCode);
            if (!SyncEventTypes.IsReplicated(envelope.EventType))
            {
                outcome = SyncInboxOutcome.Ignored;
            }
            else
            {
                sender = await GetOrCreateSenderAsync(code, ReadTerminalId(envelope.Payload), now, ct);
                await ApplyEventAsync(envelope, sender, now, ct);
                outcome = SyncInboxOutcome.Applied;
            }
        }
        catch (InvalidPayloadException ex)
        {
            DiscardPendingChanges(receipt);
            outcome = SyncInboxOutcome.Rejected;
            error = ex.Message;
        }
        catch (JsonException ex)
        {
            DiscardPendingChanges(receipt);
            outcome = SyncInboxOutcome.Rejected;
            error = "Payload JSON inválido: " + ex.Message;
        }

        if (sender == null && code != null && outcome != SyncInboxOutcome.Rejected)
            sender = await _db.Terminals.FirstOrDefaultAsync(t => t.Code == code, ct);
        if (sender != null)
        {
            sender.LastSeenAt = now;
            if (outcome == SyncInboxOutcome.Applied) sender.LastSyncAt = now;
            sender.UpdatedAt = now;
        }

        if (receipt == null)
        {
            receipt = new SyncInboxReceipt { MessageId = envelope.Id, CreatedAt = now };
            _db.SyncInboxReceipts.Add(receipt);
        }
        else
        {
            receipt.UpdatedAt = now;
        }
        receipt.TerminalCode = code ?? UnknownCode;
        receipt.EventType = Truncate(envelope.EventType, 100);
        receipt.AggregateType = Truncate(envelope.AggregateType, 100);
        receipt.AggregateId = envelope.AggregateId;
        receipt.Outcome = outcome;
        receipt.Error = error == null ? null : Truncate(error, MaxErrorLength);
        receipt.ReceivedAt = now;

        await _db.SaveChangesAsync(ct);
        return new SyncInboxResult(outcome, error);
    }

    public async Task<IReadOnlyList<SyncInboxReceiptDto>> GetRecentAsync(int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var rows = await _db.SyncInboxReceipts.AsNoTracking()
            .OrderByDescending(r => r.ReceivedAt)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(r => new SyncInboxReceiptDto(r.MessageId, r.TerminalCode, r.EventType, r.AggregateId, r.Outcome, r.Error, r.ReceivedAt)).ToList();
    }

    // ── sender / validation ──────────────────────────────────────────────────

    private string ParseSenderCode(string? terminalCode)
    {
        if (string.IsNullOrWhiteSpace(terminalCode))
            throw new InvalidPayloadException("El mensaje no indica la caja de origen (terminalCode).");
        string code;
        try { code = TerminalSettings.NormalizeCode(terminalCode); }
        catch (ArgumentException ex) { throw new InvalidPayloadException(ex.Message); }
        if (_terminal is { IsSet: true } && string.Equals(_terminal.Code, code, StringComparison.Ordinal))
            throw new InvalidPayloadException($"El mensaje dice venir de esta misma caja ({code}).");
        return code;
    }

    /// <summary>
    /// Sender row by code. A satellite that never sent a heartbeat is auto-created (keeping the id it reported in
    /// the payload when that id is free, so both databases agree whenever possible).
    /// </summary>
    private async Task<Terminal> GetOrCreateSenderAsync(string code, Guid? reportedId, DateTime now, CancellationToken ct)
    {
        var row = _db.ChangeTracker.Entries<Terminal>().Select(e => e.Entity).FirstOrDefault(t => t.Code == code)
                  ?? await _db.Terminals.FirstOrDefaultAsync(t => t.Code == code, ct);
        if (row != null) return row;

        var id = reportedId is { } rid && rid != Guid.Empty && !await _db.Terminals.AnyAsync(t => t.Id == rid, ct)
            ? rid : Guid.NewGuid();
        row = new Terminal { Id = id, Code = code, Name = code, Kind = TerminalKind.Satellite, IsActive = true, CreatedAt = now };
        _db.Terminals.Add(row);
        return row;
    }

    private static Guid? ReadTerminalId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "order", "shift", "billPayment", "movement" })
        {
            if (payload.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Object
                && inner.TryGetProperty("terminalId", out var tid) && tid.ValueKind == JsonValueKind.String
                && Guid.TryParse(tid.GetString(), out var g))
                return g;
        }
        return null;
    }

    /// <summary>Rejected after entities were added: drop them so the receipt is the only thing saved.</summary>
    private void DiscardPendingChanges(SyncInboxReceipt? keep)
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            if (ReferenceEquals(entry.Entity, keep)) continue;
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            else if (entry.State is EntityState.Modified or EntityState.Deleted) entry.Reload();
        }
    }

    // ── dispatch ─────────────────────────────────────────────────────────────

    private async Task ApplyEventAsync(SyncEnvelopeDto envelope, Terminal sender, DateTime now, CancellationToken ct)
    {
        switch (envelope.EventType)
        {
            case SyncEventTypes.OrderCompleted:
                await ApplyOrderAsync(Section<OrderPayload>(envelope.Payload, "order"), sender, envelope, now, ct);
                break;
            case SyncEventTypes.CashShiftOpened:
            case SyncEventTypes.CashShiftClosed:
                await ApplyShiftAsync(Section<ShiftPayload>(envelope.Payload, "shift"), envelope.EventType == SyncEventTypes.CashShiftClosed, sender, now, ct);
                break;
            case SyncEventTypes.CashMovementRecorded:
                await ApplyMovementAsync(Section<MovementPayload>(envelope.Payload, "movement"), sender, envelope, now, ct);
                break;
            case SyncEventTypes.BillPaymentRecorded:
            case SyncEventTypes.BillPaymentVoided:
                await ApplyBillPaymentAsync(Section<BillPaymentPayload>(envelope.Payload, "billPayment"), sender, envelope, now, ct);
                break;
            default:
                throw new InvalidPayloadException($"Tipo de evento no soportado: {envelope.EventType}.");
        }
    }

    private static T Section<T>(JsonElement payload, string key)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidPayloadException("El payload no es un objeto JSON.");
        if (!payload.TryGetProperty(key, out var inner) || inner.ValueKind != JsonValueKind.Object)
            throw new InvalidPayloadException($"El payload no trae el objeto '{key}' (formato anterior al 2026-11-13; actualice la caja satélite).");
        return JsonSerializer.Deserialize<T>(inner.GetRawText(), PayloadOptions)
            ?? throw new InvalidPayloadException($"El objeto '{key}' está vacío.");
    }

    // ── Order ────────────────────────────────────────────────────────────────

    private async Task ApplyOrderAsync(OrderPayload p, Terminal sender, SyncEnvelopeDto envelope, DateTime now, CancellationToken ct)
    {
        if (p.Id == Guid.Empty) throw new InvalidPayloadException("order.id es obligatorio.");
        if (string.IsNullOrWhiteSpace(p.OrderNumber)) throw new InvalidPayloadException("order.orderNumber es obligatorio.");
        var items = p.Items ?? throw new InvalidPayloadException("order.items es obligatorio.");
        var payments = p.Payments ?? throw new InvalidPayloadException("order.payments es obligatorio.");
        if (items.Any(i => i.Id == Guid.Empty) || payments.Any(x => x.Id == Guid.Empty))
            throw new InvalidPayloadException("Cada item y pago de la orden debe traer id.");
        if (items.Select(i => i.Id).Concat(payments.Select(x => x.Id)).GroupBy(id => id).Any(g => g.Count() > 1))
            throw new InvalidPayloadException("Ids repetidos en items/pagos de la orden.");

        // Already replicated (e.g. the satellite retried after a timeout): nothing changes.
        if (await _db.Orders.AnyAsync(o => o.Id == p.Id, ct)) return;

        var completedAt = Utc(p.CompletedAt) ?? Utc(envelope.OccurredAt) ?? now;
        var shift = p.CashShiftId is { } shiftId && shiftId != Guid.Empty
            ? await EnsureShiftAsync(shiftId, p.ShiftNumber, sender, completedAt, now, ct)
            : null;

        var orderNumber = Truncate(p.OrderNumber, 50);
        if (await _db.Orders.AnyAsync(o => o.OrderNumber == orderNumber, ct))
            orderNumber = Truncate($"{sender.Code}:{orderNumber}", 50);

        var customerId = p.CustomerId is { } cid && cid != Guid.Empty && await _db.Customers.AnyAsync(c => c.Id == cid, ct)
            ? cid : (Guid?)null;

        var order = new Order
        {
            Id = p.Id,
            OrderNumber = orderNumber,
            Status = OrderStatus.Completed,
            Subtotal = p.Subtotal,
            TaxTotal = p.TaxTotal,
            DiscountTotal = p.DiscountTotal,
            Total = p.Total,
            Notes = p.Notes,
            CustomerId = customerId,
            CashShiftId = shift?.Id,
            TerminalId = sender.Id,
            TerminalCode = sender.Code,
            CreatedAt = Utc(p.CreatedAt) ?? completedAt,
            UpdatedAt = completedAt
        };
        foreach (var i in items)
        {
            order.Items.Add(new OrderItem
            {
                Id = i.Id,
                ProductId = i.ProductId,
                ProductName = Truncate(i.ProductName ?? string.Empty, 200),
                UnitPrice = i.UnitPrice,
                Quantity = i.Quantity,
                WeightKg = i.WeightKg,
                TaxRate = i.TaxRate,
                TaxAmount = i.TaxAmount,
                Subtotal = i.Subtotal,
                Total = i.Total,
                CreatedAt = order.CreatedAt
            });
        }
        foreach (var x in payments)
        {
            order.Payments.Add(new Payment
            {
                Id = x.Id,
                Method = x.Method ?? PaymentMethod.Cash,
                Amount = x.Amount,
                TenderedAmount = x.TenderedAmount,
                ChangeAmount = x.ChangeAmount,
                Reference = x.Reference,
                ProcessedAt = Utc(x.ProcessedAt) ?? completedAt,
                CreatedAt = Utc(x.ProcessedAt) ?? completedAt
            });
        }
        _db.Orders.Add(order);
    }

    // ── CashShift ────────────────────────────────────────────────────────────

    private async Task ApplyShiftAsync(ShiftPayload p, bool closed, Terminal sender, DateTime now, CancellationToken ct)
    {
        if (p.Id == Guid.Empty) throw new InvalidPayloadException("shift.id es obligatorio.");
        if (p.OpenedAt == null) throw new InvalidPayloadException("shift.openedAt es obligatorio.");
        if (closed && p.ClosedAt == null) throw new InvalidPayloadException("shift.closedAt es obligatorio en CashShiftClosed.");
        if (closed && p.CountLines != null && p.CountLines.Any(l => l.Quantity < 0 || l.Denomination <= 0))
            throw new InvalidPayloadException("Cantidades y denominaciones del arqueo deben ser positivas.");

        var shift = await FindShiftAsync(p.Id, ct);
        if (shift != null && shift.TerminalCode != null && !string.Equals(shift.TerminalCode, sender.Code, StringComparison.Ordinal))
            throw new InvalidPayloadException($"El turno {p.Id} pertenece a la caja {shift.TerminalCode}, no a {sender.Code}.");

        if (shift == null)
        {
            shift = new CashShift { Id = p.Id, CreatedAt = now };
            _db.CashShifts.Add(shift);
        }
        else
        {
            shift.UpdatedAt = now;
        }

        shift.ShiftNumber = p.ShiftNumber ?? shift.ShiftNumber;
        shift.CashierId = p.CashierId ?? shift.CashierId;
        shift.CashierName = Truncate(string.IsNullOrWhiteSpace(p.CashierName) ? shift.CashierName : p.CashierName, 100);
        shift.OpenedAt = Utc(p.OpenedAt)!.Value;
        shift.OpeningAmount = p.OpeningAmount;
        shift.OpenedByUserId = p.OpenedByUserId;
        shift.TerminalId = sender.Id;
        shift.TerminalCode = sender.Code;

        if (closed)
        {
            shift.Status = CashShiftStatus.Closed;
            shift.ClosedAt = Utc(p.ClosedAt);
            shift.ClosingAmount = p.ClosingAmount;
            shift.ExpectedAmount = p.ExpectedAmount;
            shift.FloatLeftAmount = p.FloatLeftAmount;
            shift.WasBlindCount = p.WasBlindCount;
            shift.Notes = p.Notes;
            shift.ClosedByUserId = p.ClosedByUserId;
            shift.ClosedByName = string.IsNullOrWhiteSpace(p.ClosedByName) ? null : Truncate(p.ClosedByName, 100);
            shift.DifferenceOverrideId = p.DifferenceOverrideId;

            // Replace the count lines (a re-sent close must not duplicate them).
            var existing = await _db.CashCountLines.Where(l => l.CashShiftId == shift.Id).ToListAsync(ct);
            _db.CashCountLines.RemoveRange(existing);
            foreach (var l in p.CountLines ?? new List<CountLinePayload>())
            {
                if (l.Quantity == 0) continue;
                _db.CashCountLines.Add(new CashCountLine { CashShiftId = shift.Id, Denomination = l.Denomination, Quantity = l.Quantity, CreatedAt = now });
            }
        }
        else if (shift.Status != CashShiftStatus.Closed)
        {
            // An opened event never reopens a shift the close already replicated (events may arrive out of order).
            shift.Status = p.Status ?? CashShiftStatus.Open;
        }
    }

    // ── CashMovement ─────────────────────────────────────────────────────────

    private async Task ApplyMovementAsync(MovementPayload p, Terminal sender, SyncEnvelopeDto envelope, DateTime now, CancellationToken ct)
    {
        if (p.Id == Guid.Empty) throw new InvalidPayloadException("movement.id es obligatorio.");
        if (p.CashShiftId == Guid.Empty) throw new InvalidPayloadException("movement.cashShiftId es obligatorio.");
        if (p.Type == null) throw new InvalidPayloadException("movement.type es obligatorio.");
        if (p.Amount <= 0) throw new InvalidPayloadException("movement.amount debe ser mayor que cero.");

        var occurredAt = Utc(p.OccurredAt) ?? Utc(envelope.OccurredAt) ?? now;
        var shift = await EnsureShiftAsync(p.CashShiftId, ReadShiftNumber(envelope.Payload), sender, occurredAt, now, ct);

        var m = await _db.CashMovements.FirstOrDefaultAsync(x => x.Id == p.Id, ct);
        if (m == null)
        {
            m = new CashMovement { Id = p.Id, CreatedAt = now };
            _db.CashMovements.Add(m);
        }
        else
        {
            m.UpdatedAt = now;
        }
        m.CashShiftId = shift.Id;
        m.Type = p.Type.Value;
        m.Amount = p.Amount;
        m.Reason = Truncate(string.IsNullOrWhiteSpace(p.Reason) ? "(sin motivo)" : p.Reason, 200);
        m.Reference = string.IsNullOrWhiteSpace(p.Reference) ? null : Truncate(p.Reference, 100);
        m.UserId = p.UserId;
        m.UserName = Truncate(p.UserName ?? string.Empty, 100);
        m.ManagerOverrideId = p.ManagerOverrideId;
        m.BillPaymentId = p.BillPaymentId;
        m.OccurredAt = occurredAt;
    }

    // ── BillPayment ──────────────────────────────────────────────────────────

    private async Task ApplyBillPaymentAsync(BillPaymentPayload p, Terminal sender, SyncEnvelopeDto envelope, DateTime now, CancellationToken ct)
    {
        if (p.Id == Guid.Empty) throw new InvalidPayloadException("billPayment.id es obligatorio.");
        if (p.CashShiftId == Guid.Empty) throw new InvalidPayloadException("billPayment.cashShiftId es obligatorio.");
        if (string.IsNullOrWhiteSpace(p.ReceiptNumber)) throw new InvalidPayloadException("billPayment.receiptNumber es obligatorio.");
        if (string.IsNullOrWhiteSpace(p.AccountReference)) throw new InvalidPayloadException("billPayment.accountReference es obligatorio.");
        if (p.Amount <= 0) throw new InvalidPayloadException("billPayment.amount debe ser mayor que cero.");

        var paidAt = Utc(p.PaidAt) ?? Utc(envelope.OccurredAt) ?? now;
        var shift = await EnsureShiftAsync(p.CashShiftId, ReadShiftNumber(envelope.Payload), sender, paidAt, now, ct);

        var b = await _db.BillPayments.FirstOrDefaultAsync(x => x.Id == p.Id, ct);
        if (b == null)
        {
            b = new BillPayment { Id = p.Id, CreatedAt = now };
            _db.BillPayments.Add(b);
            // Receipt numbers are a per-register sequence: "REC-000001" exists on every caja. Keep the sender's
            // number when free, otherwise qualify it with the code so the unique index holds.
            var receipt = Truncate(p.ReceiptNumber, 30);
            if (await _db.BillPayments.AnyAsync(x => x.ReceiptNumber == receipt && x.Id != p.Id, ct))
                receipt = Truncate($"{sender.Code}:{receipt}", 30);
            b.ReceiptNumber = receipt;
        }
        else
        {
            b.UpdatedAt = now;
        }

        b.CashShiftId = shift.Id;
        b.TerminalId = sender.Id;
        b.TerminalCode = sender.Code;
        b.BillerId = p.BillerId;
        b.BillerName = Truncate(string.IsNullOrWhiteSpace(p.BillerName) ? "(convenio)" : p.BillerName, 100);
        b.AccountReference = Truncate(p.AccountReference, 60);
        b.CustomerName = string.IsNullOrWhiteSpace(p.CustomerName) ? null : Truncate(p.CustomerName, 150);
        b.Amount = p.Amount;
        b.Commission = p.Commission;
        b.Method = p.Method ?? PaymentMethod.Cash;
        b.TenderedAmount = p.TenderedAmount;
        b.ChangeAmount = p.ChangeAmount;
        b.Status = p.Status ?? (envelope.EventType == SyncEventTypes.BillPaymentVoided ? BillPaymentStatus.Voided : BillPaymentStatus.Completed);
        b.UserId = p.UserId;
        b.UserName = Truncate(p.UserName ?? string.Empty, 100);
        b.PaidAt = paidAt;
        b.VoidedAt = Utc(p.VoidedAt);
        b.VoidOverrideId = p.VoidOverrideId;
        b.VoidReason = string.IsNullOrWhiteSpace(p.VoidReason) ? null : Truncate(p.VoidReason, 200);
        if (envelope.EventType == SyncEventTypes.BillPaymentVoided && b.Status != BillPaymentStatus.Voided)
            b.Status = BillPaymentStatus.Voided;
    }

    // ── shift placeholder ────────────────────────────────────────────────────

    private async Task<CashShift?> FindShiftAsync(Guid id, CancellationToken ct) =>
        _db.ChangeTracker.Entries<CashShift>().Select(e => e.Entity).FirstOrDefault(s => s.Id == id)
        ?? await _db.CashShifts.FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>
    /// The FK of orders/movements/bill payments needs the shift row. When the CashShiftOpened event has not
    /// arrived yet (lost, or still queued) a placeholder is created with the same id; the opened/closed events
    /// later fill every field.
    /// </summary>
    private async Task<CashShift> EnsureShiftAsync(Guid shiftId, int? shiftNumber, Terminal sender, DateTime openedAt, DateTime now, CancellationToken ct)
    {
        var shift = await FindShiftAsync(shiftId, ct);
        if (shift != null)
        {
            if (shift.TerminalCode != null && !string.Equals(shift.TerminalCode, sender.Code, StringComparison.Ordinal))
                throw new InvalidPayloadException($"El turno {shiftId} pertenece a la caja {shift.TerminalCode}, no a {sender.Code}.");
            return shift;
        }

        shift = new CashShift
        {
            Id = shiftId,
            ShiftNumber = shiftNumber ?? await NextPlaceholderNumberAsync(sender.Code, ct),
            CashierName = PlaceholderCashier,
            OpenedAt = openedAt,
            Status = CashShiftStatus.Open,
            TerminalId = sender.Id,
            TerminalCode = sender.Code,
            CreatedAt = now
        };
        _db.CashShifts.Add(shift);
        return shift;
    }

    /// <summary>0, −1, −2… so two placeholders of the same caja never collide on the (TerminalCode, ShiftNumber) unique index.</summary>
    private async Task<int> NextPlaceholderNumberAsync(string code, CancellationToken ct)
    {
        var used = await _db.CashShifts.Where(s => s.TerminalCode == code && s.ShiftNumber <= 0).Select(s => s.ShiftNumber).ToListAsync(ct);
        used.AddRange(_db.ChangeTracker.Entries<CashShift>().Select(e => e.Entity).Where(s => s.TerminalCode == code && s.ShiftNumber <= 0).Select(s => s.ShiftNumber));
        return used.Count == 0 ? 0 : used.Min() - 1;
    }

    private static int? ReadShiftNumber(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("shiftNumber", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var v))
            return v;
        return null;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DateTime? Utc(DateTime? d) => d switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)
    };

    private static string Truncate(string? s, int max)
    {
        var t = s?.Trim() ?? string.Empty;
        return t.Length > max ? t[..max] : t;
    }

    // ── payload shapes (SyncEventTypes contract) ─────────────────────────────

    private sealed class OrderPayload
    {
        public Guid Id { get; set; }
        public string? OrderNumber { get; set; }
        public OrderStatus? Status { get; set; }
        public decimal Subtotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal Total { get; set; }
        public string? Notes { get; set; }
        public Guid? CustomerId { get; set; }
        public Guid? CashShiftId { get; set; }
        public int? ShiftNumber { get; set; }
        public Guid? TerminalId { get; set; }
        public string? TerminalCode { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public List<OrderItemPayload>? Items { get; set; }
        public List<PaymentPayload>? Payments { get; set; }
    }

    private sealed class OrderItemPayload
    {
        public Guid Id { get; set; }
        public Guid ProductId { get; set; }
        public string? ProductName { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal? WeightKg { get; set; }
        public decimal TaxRate { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Total { get; set; }
    }

    private sealed class PaymentPayload
    {
        public Guid Id { get; set; }
        public PaymentMethod? Method { get; set; }
        public decimal Amount { get; set; }
        public decimal TenderedAmount { get; set; }
        public decimal ChangeAmount { get; set; }
        public string? Reference { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }

    private sealed class ShiftPayload
    {
        public Guid Id { get; set; }
        public int? ShiftNumber { get; set; }
        public Guid? CashierId { get; set; }
        public string? CashierName { get; set; }
        public DateTime? OpenedAt { get; set; }
        public decimal OpeningAmount { get; set; }
        public Guid? OpenedByUserId { get; set; }
        public Guid? TerminalId { get; set; }
        public string? TerminalCode { get; set; }
        public CashShiftStatus? Status { get; set; }
        public DateTime? ClosedAt { get; set; }
        public decimal ClosingAmount { get; set; }
        public decimal ExpectedAmount { get; set; }
        public decimal FloatLeftAmount { get; set; }
        public bool WasBlindCount { get; set; }
        public string? Notes { get; set; }
        public Guid? ClosedByUserId { get; set; }
        public string? ClosedByName { get; set; }
        public Guid? DifferenceOverrideId { get; set; }
        public List<CountLinePayload>? CountLines { get; set; }
    }

    private sealed class CountLinePayload
    {
        public decimal Denomination { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class MovementPayload
    {
        public Guid Id { get; set; }
        public Guid CashShiftId { get; set; }
        public CashMovementType? Type { get; set; }
        public decimal Amount { get; set; }
        public string? Reason { get; set; }
        public string? Reference { get; set; }
        public Guid? UserId { get; set; }
        public string? UserName { get; set; }
        public Guid? ManagerOverrideId { get; set; }
        public Guid? BillPaymentId { get; set; }
        public DateTime? OccurredAt { get; set; }
    }

    private sealed class BillPaymentPayload
    {
        public Guid Id { get; set; }
        public Guid CashShiftId { get; set; }
        public Guid? TerminalId { get; set; }
        public string? TerminalCode { get; set; }
        public Guid BillerId { get; set; }
        public string? BillerName { get; set; }
        public string? AccountReference { get; set; }
        public string? CustomerName { get; set; }
        public decimal Amount { get; set; }
        public decimal Commission { get; set; }
        public PaymentMethod? Method { get; set; }
        public decimal TenderedAmount { get; set; }
        public decimal ChangeAmount { get; set; }
        public string? ReceiptNumber { get; set; }
        public BillPaymentStatus? Status { get; set; }
        public Guid? UserId { get; set; }
        public string? UserName { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime? VoidedAt { get; set; }
        public Guid? VoidOverrideId { get; set; }
        public string? VoidReason { get; set; }
    }
}
