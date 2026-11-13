using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

/// <summary>
/// Bill payments at the counter (recaudos). A cash payment adds a BillPayment CashMovement and a void of a
/// cash payment adds a BillPaymentVoid movement (both display-only: the reconciliation takes recaudo cash from
/// completed BillPayments). Business change + Outbox are committed in one SaveChanges.
/// </summary>
public class BillPaymentService : IBillPaymentService
{
    private const string ReceiptPrefix = "REC-";
    private const decimal MaxCommissionPercent = 0.2m;

    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ITerminalContext? _terminal;

    /// <param name="terminal">Optional: when set, "the open shift" is this register's and new payments are stamped with its id/code.</param>
    public BillPaymentService(PosDbContext db, TimeProvider clock, ITerminalContext? terminal = null)
    {
        _db = db;
        _clock = clock;
        _terminal = terminal;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Same rule as CashShiftService.Scoped: this register's shifts (+ legacy rows on the master).</summary>
    private IQueryable<CashShift> OpenShifts()
    {
        IQueryable<CashShift> q = _db.CashShifts.AsNoTracking().Where(s => s.Status == CashShiftStatus.Open);
        if (_terminal is not { IsSet: true }) return q;
        var id = _terminal.Id;
        return _terminal.Kind == TerminalKind.Master
            ? q.Where(s => s.TerminalId == id || s.TerminalId == null)
            : q.Where(s => s.TerminalId == id);
    }

    public async Task<IReadOnlyList<BillerDto>> GetBillersAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var query = _db.Billers.AsNoTracking();
        if (!includeInactive) query = query.Where(b => b.IsActive);
        var list = await query.OrderBy(b => b.SortOrder).ThenBy(b => b.Name).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<BillerDto> SaveBillerAsync(BillerDto biller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(biller);
        // --- validate ---
        var name = biller.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 100)
            throw new ArgumentException("El nombre del convenio es obligatorio (máximo 100 caracteres).", nameof(biller));
        if (biller.CommissionFixed < 0 || biller.CommissionPercent < 0)
            throw new ArgumentException("Las comisiones no pueden ser negativas.", nameof(biller));
        if (biller.CommissionPercent > MaxCommissionPercent)
            throw new ArgumentException("La comisión porcentual no puede superar el 20 %.", nameof(biller));
        var code = string.IsNullOrWhiteSpace(biller.Code) ? null : biller.Code.Trim();
        if (code is { Length: > 30 })
            throw new ArgumentException("El código admite máximo 30 caracteres.", nameof(biller));
        var color = string.IsNullOrWhiteSpace(biller.ColorHex) ? null : biller.ColorHex.Trim();
        if (color is { Length: > 9 })
            throw new ArgumentException("El color admite máximo 9 caracteres (#RRGGBB).", nameof(biller));

        // Compare in memory: SQLite lower() only folds ASCII, so "ÁREA" vs "área" would slip through. The biller list is tiny.
        var others = await _db.Billers.Where(b => b.Id != biller.Id).Select(b => b.Name).ToListAsync(ct);
        var duplicate = others.Any(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase));
        if (duplicate)
            throw new InvalidOperationException($"Ya existe un convenio llamado '{name}'.");

        Biller entity;
        if (biller.Id == Guid.Empty)
        {
            entity = new Biller { CreatedAt = UtcNow };
            _db.Billers.Add(entity);
        }
        else
        {
            entity = await _db.Billers.FirstOrDefaultAsync(b => b.Id == biller.Id, ct)
                ?? throw new InvalidOperationException("Convenio no encontrado.");
            entity.UpdatedAt = UtcNow;
        }

        // --- mutate ---
        entity.Name = name;
        entity.Code = code;
        entity.CommissionFixed = biller.CommissionFixed;
        entity.CommissionPercent = biller.CommissionPercent;
        entity.ColorHex = color;
        entity.SortOrder = biller.SortOrder;
        entity.IsActive = biller.IsActive;

        await _db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<BillPaymentDto> RecordAsync(BillPaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // --- validate ---
        var reference = request.AccountReference?.Trim() ?? string.Empty;
        if (reference.Length == 0)
            throw new ArgumentException("La referencia / número de contrato es obligatoria.", nameof(request));
        if (reference.Length > 60)
            throw new ArgumentException("La referencia admite máximo 60 caracteres.", nameof(request));
        if (request.Amount <= 0)
            throw new ArgumentException("El valor a pagar debe ser mayor que cero.", nameof(request));
        if (request.Method is not (PaymentMethod.Cash or PaymentMethod.Card or PaymentMethod.Transfer))
            throw new ArgumentException("Medio de pago no permitido para recaudos.", nameof(request));
        var customer = string.IsNullOrWhiteSpace(request.CustomerName) ? null : request.CustomerName.Trim();
        if (customer is { Length: > 150 })
            throw new ArgumentException("El nombre del cliente admite máximo 150 caracteres.", nameof(request));

        var shift = await OpenShifts().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No hay ningún turno abierto.");
        var biller = await _db.Billers.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BillerId && b.IsActive, ct)
            ?? throw new InvalidOperationException("Convenio no encontrado o inactivo.");

        var commission = ToDto(biller).CommissionFor(request.Amount);
        var total = request.Amount + commission;
        decimal tendered, change;
        if (request.Method == PaymentMethod.Cash)
        {
            if (request.TenderedAmount < total)
                throw new ArgumentException($"El valor recibido ({request.TenderedAmount:N0}) es menor que el total ({total:N0}).", nameof(request));
            tendered = request.TenderedAmount;
            change = tendered - total;
        }
        else
        {
            tendered = total;
            change = 0m;
        }

        var receiptNumber = await NextReceiptNumberAsync(ct);
        var userName = Truncate(request.UserName, 100);

        // --- mutate (one SaveChanges, Outbox included) ---
        var payment = new BillPayment
        {
            CashShiftId = shift.Id,
            TerminalId = shift.TerminalId ?? (_terminal is { IsSet: true } ? _terminal.Id : null),
            TerminalCode = shift.TerminalCode ?? (_terminal is { IsSet: true } ? _terminal.Code : null),
            BillerId = biller.Id,
            BillerName = biller.Name,
            AccountReference = reference,
            CustomerName = customer,
            Amount = request.Amount,
            Commission = commission,
            Method = request.Method,
            TenderedAmount = tendered,
            ChangeAmount = change,
            ReceiptNumber = receiptNumber,
            Status = BillPaymentStatus.Completed,
            UserId = request.UserId,
            UserName = userName,
            PaidAt = UtcNow,
            CreatedAt = UtcNow
        };
        _db.BillPayments.Add(payment);

        if (payment.Method == PaymentMethod.Cash)
            _db.CashMovements.Add(new CashMovement
            {
                CashShiftId = shift.Id,
                Type = CashMovementType.BillPayment,
                Amount = total,
                Reason = Truncate($"Recaudo {biller.Name}", 200),
                Reference = receiptNumber,
                UserId = request.UserId,
                UserName = userName,
                BillPaymentId = payment.Id,
                OccurredAt = UtcNow,
                CreatedAt = UtcNow
            });

        // Top-level shiftNumber lets the master's inbox number a placeholder shift when this event arrives before CashShiftOpened.
        _db.OutboxMessages.Add(Outbox(SyncEventTypes.BillPaymentRecorded, payment, new { shiftNumber = shift.ShiftNumber }));

        await _db.SaveChangesAsync(ct);
        return ToDto(payment);
    }

    public async Task<BillPaymentDto> VoidAsync(Guid billPaymentId, string reason, Guid managerOverrideId, CancellationToken ct = default)
    {
        // --- validate ---
        var why = reason?.Trim() ?? string.Empty;
        if (why.Length == 0)
            throw new ArgumentException("El motivo de la anulación es obligatorio.", nameof(reason));
        if (why.Length > 200)
            throw new ArgumentException("El motivo admite máximo 200 caracteres.", nameof(reason));

        var payment = await _db.BillPayments.FirstOrDefaultAsync(b => b.Id == billPaymentId, ct)
            ?? throw new InvalidOperationException("Recaudo no encontrado.");
        if (payment.Status == BillPaymentStatus.Voided)
            throw new InvalidOperationException("El recaudo ya está anulado.");

        var shiftOpen = await _db.CashShifts
            .AnyAsync(s => s.Id == payment.CashShiftId && s.Status == CashShiftStatus.Open, ct);
        if (!shiftOpen)
            throw new InvalidOperationException("Solo se pueden anular recaudos del turno abierto.");

        var approval = await _db.ManagerOverrides.FirstOrDefaultAsync(o => o.Id == managerOverrideId, ct);
        if (approval == null || approval.Action != AuthorizedAction.VoidBillPayment || approval.ConsumedAt != null)
            throw new InvalidOperationException("Se requiere autorización de gerente.");

        // --- mutate (one SaveChanges, Outbox included) ---
        payment.Status = BillPaymentStatus.Voided;
        payment.VoidedAt = UtcNow;
        payment.VoidOverrideId = approval.Id;
        payment.VoidReason = why;
        payment.UpdatedAt = UtcNow;

        approval.ConsumedAt = UtcNow;
        approval.ConsumedFor = $"BillPayment {payment.Id}";
        approval.UpdatedAt = UtcNow;

        if (payment.Method == PaymentMethod.Cash)
            _db.CashMovements.Add(new CashMovement
            {
                CashShiftId = payment.CashShiftId,
                Type = CashMovementType.BillPaymentVoid,
                Amount = payment.Total,
                Reason = Truncate($"Anulación {payment.ReceiptNumber}: {why}", 200),
                Reference = payment.ReceiptNumber,
                UserId = approval.RequestedByUserId,
                UserName = Truncate(approval.RequestedByName ?? approval.ManagerName, 100),
                ManagerOverrideId = approval.Id,
                BillPaymentId = payment.Id,
                OccurredAt = UtcNow,
                CreatedAt = UtcNow
            });

        _db.OutboxMessages.Add(Outbox(SyncEventTypes.BillPaymentVoided, payment, new { managerName = approval.ManagerName }));

        await _db.SaveChangesAsync(ct);
        return ToDto(payment);
    }

    public async Task<IReadOnlyList<BillPaymentDto>> GetForShiftAsync(Guid? shiftId = null, CancellationToken ct = default)
    {
        var id = shiftId ?? await OpenShifts().Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (id == null) return Array.Empty<BillPaymentDto>();

        var list = await _db.BillPayments.AsNoTracking()
            .Where(b => b.CashShiftId == id)
            .OrderByDescending(b => b.PaidAt)
            .ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    /// <summary>"REC-" + 6-digit sequence: max existing + 1. Zero-padded numbers sort lexicographically.</summary>
    private async Task<string> NextReceiptNumberAsync(CancellationToken ct)
    {
        var last = await _db.BillPayments
            .Where(b => b.ReceiptNumber.StartsWith(ReceiptPrefix))
            .OrderByDescending(b => b.ReceiptNumber)
            .Select(b => b.ReceiptNumber)
            .FirstOrDefaultAsync(ct);
        var n = last != null && int.TryParse(last[ReceiptPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
        return ReceiptPrefix + (n + 1).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Contract payload <c>{ billPayment: {...} }</c> (see SyncEventTypes) plus legacy flat keys.</summary>
    private OutboxMessage Outbox(string eventType, BillPayment payment, object? extra = null) => new()
    {
        EventType = eventType,
        AggregateType = "BillPayment",
        AggregateId = payment.Id,
        PayloadJson = OutboxPayloads.BillPayment(payment, extra),
        Status = SyncStatus.Pending,
        CreatedAt = UtcNow,
        UpdatedAt = UtcNow
    };

    private static string Truncate(string? s, int max)
    {
        var t = s?.Trim() ?? string.Empty;
        return t.Length > max ? t[..max] : t;
    }

    private static BillerDto ToDto(Biller b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Code = b.Code,
        CommissionFixed = b.CommissionFixed,
        CommissionPercent = b.CommissionPercent,
        ColorHex = b.ColorHex,
        SortOrder = b.SortOrder,
        IsActive = b.IsActive
    };

    private static BillPaymentDto ToDto(BillPayment p) => new()
    {
        Id = p.Id,
        ReceiptNumber = p.ReceiptNumber,
        BillerId = p.BillerId,
        BillerName = p.BillerName,
        AccountReference = p.AccountReference,
        CustomerName = p.CustomerName,
        Amount = p.Amount,
        Commission = p.Commission,
        Method = p.Method,
        TenderedAmount = p.TenderedAmount,
        ChangeAmount = p.ChangeAmount,
        Status = p.Status,
        UserName = p.UserName,
        PaidAt = p.PaidAt,
        VoidedAt = p.VoidedAt,
        VoidReason = p.VoidReason
    };
}
