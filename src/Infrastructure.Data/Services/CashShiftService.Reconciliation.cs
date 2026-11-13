using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public partial class CashShiftService
{
    /// <summary>
    /// THE expected-cash computation (single source of truth). Fills sales by method (completed orders),
    /// completed bill payments by method, and manual movements by type; ExpectedCash is then the DTO formula:
    /// opening + cash sales + cash bill payments (amount + commission) + paid-in − paid-out − expenses
    /// − safe drops − tip payouts. A voided bill payment simply drops out (BillPaymentVoidsCash = 0);
    /// BillPayment / BillPaymentVoid movements are listed for display but never summed (no double count).
    /// Sums are done in memory: SQLite cannot SUM decimals.
    /// </summary>
    private async Task<ShiftReconciliationDto> ComputeAsync(CashShift shift, CancellationToken ct)
    {
        var payments = await _db.Payments
            .Where(p => p.Order != null && p.Order.CashShiftId == shift.Id && p.Order.Status == OrderStatus.Completed)
            .Select(p => new { p.Method, p.Amount, p.OrderId })
            .ToListAsync(ct);

        var bills = await _db.BillPayments
            .Where(b => b.CashShiftId == shift.Id && b.Status == BillPaymentStatus.Completed)
            .Select(b => new { b.Method, b.Amount, b.Commission })
            .ToListAsync(ct);

        var movements = await _db.CashMovements
            .Where(m => m.CashShiftId == shift.Id)
            .ToListAsync(ct);
        movements = movements.OrderBy(m => m.OccurredAt).ToList();

        decimal Sales(PaymentMethod m) => payments.Where(p => p.Method == m).Sum(p => p.Amount);
        decimal Bills(PaymentMethod m) => bills.Where(b => b.Method == m).Sum(b => b.Amount + b.Commission);
        decimal Moves(CashMovementType t) => movements.Where(m => m.Type == t).Sum(m => m.Amount);

        return new ShiftReconciliationDto
        {
            ShiftId = shift.Id,
            ShiftNumber = shift.ShiftNumber,
            TerminalCode = shift.TerminalCode,
            CashierName = shift.CashierName,
            Status = shift.Status,
            OpenedAt = shift.OpenedAt,
            ClosedAt = shift.ClosedAt,
            ClosedByName = shift.ClosedByName,
            OpeningAmount = shift.OpeningAmount,

            CashSales = Sales(PaymentMethod.Cash),
            CardSales = Sales(PaymentMethod.Card),
            TransferSales = Sales(PaymentMethod.Transfer),
            OtherSales = Sales(PaymentMethod.Other),
            OrderCount = payments.Select(p => p.OrderId).Distinct().Count(),

            BillPaymentCount = bills.Count,
            BillPaymentsCash = Bills(PaymentMethod.Cash),
            BillPaymentsCard = Bills(PaymentMethod.Card),
            BillPaymentsTransfer = Bills(PaymentMethod.Transfer),
            BillPaymentCommissions = bills.Sum(b => b.Commission),
            BillPaymentsToRemit = bills.Sum(b => b.Amount),

            PaidIn = Moves(CashMovementType.PaidIn),
            PaidOut = Moves(CashMovementType.PaidOut),
            Expenses = Moves(CashMovementType.Expense),
            SafeDrops = Moves(CashMovementType.SafeDrop),
            TipPayouts = Moves(CashMovementType.TipPayout),
            BillPaymentVoidsCash = 0m,
            Movements = movements.Select(ToDto).ToList(),
        };
    }

    public async Task<ShiftReconciliationDto> GetReconciliationAsync(Guid? shiftId = null, CancellationToken ct = default)
    {
        var shift = shiftId is { } id
            ? await _db.CashShifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
                ?? throw new InvalidOperationException("Turno no encontrado.")
            : await Scoped(_db.CashShifts.AsNoTracking()).FirstOrDefaultAsync(s => s.Status == CashShiftStatus.Open, ct)
                ?? throw new InvalidOperationException("No hay ningún turno abierto.");

        var r = await ComputeAsync(shift, ct);

        var partials = await _db.PartialCashCounts.AsNoTracking()
            .Where(p => p.CashShiftId == shift.Id).ToListAsync(ct);
        r.PartialCounts = partials.OrderBy(p => p.CountedAt).Select(p => new PartialCountDto
        {
            Id = p.Id,
            ExpectedAmount = p.ExpectedAmount,
            CountedAmount = p.CountedAmount,
            UserName = p.UserName,
            CountedAt = p.CountedAt
        }).ToList();

        r.Notes = shift.Notes;
        r.FloatLeftAmount = shift.FloatLeftAmount;
        r.WasBlindCount = shift.WasBlindCount;

        if (shift.Status == CashShiftStatus.Closed)
        {
            r.CountedCash = shift.ClosingAmount;
            var lines = await _db.CashCountLines.AsNoTracking()
                .Where(l => l.CashShiftId == shift.Id).ToListAsync(ct);
            r.CountLines = lines.OrderByDescending(l => l.Denomination)
                .Select(l => new CashCountLineDto { Denomination = l.Denomination, Quantity = l.Quantity }).ToList();
            if (shift.DifferenceOverrideId is { } oid)
                r.DifferenceApprovedBy = await _db.ManagerOverrides.AsNoTracking()
                    .Where(o => o.Id == oid).Select(o => o.ManagerName).FirstOrDefaultAsync(ct);
        }
        return r;
    }

    public async Task<decimal> GetSuggestedOpeningAmountAsync(CancellationToken ct = default)
    {
        var last = await Scoped(_db.CashShifts.AsNoTracking())
            .Where(s => s.Status == CashShiftStatus.Closed)
            .OrderByDescending(s => s.ShiftNumber)
            .Select(s => new { s.FloatLeftAmount, s.ClosedByName })
            .FirstOrDefaultAsync(ct);
        // Shifts closed by the legacy flow never recorded a float (ClosedByName is null): use the default.
        return last is { ClosedByName: not null } ? last.FloatLeftAmount : _settings.DefaultFloat;
    }

    public async Task<IReadOnlyList<ShiftHistoryItemDto>> GetHistoryAsync(int take = 30, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        return await Scoped(_db.CashShifts.AsNoTracking())
            .Where(s => s.Status == CashShiftStatus.Closed)
            .OrderByDescending(s => s.ShiftNumber)
            .Take(take)
            .Select(s => ToHistoryItem(s))
            .ToListAsync(ct);
    }

    private static ShiftHistoryItemDto ToHistoryItem(CashShift s) => new()
    {
        ShiftId = s.Id,
        ShiftNumber = s.ShiftNumber,
        TerminalCode = s.TerminalCode,
        CashierName = s.CashierName,
        OpenedAt = s.OpenedAt,
        ClosedAt = s.ClosedAt,
        ExpectedAmount = s.ExpectedAmount,
        ClosingAmount = s.ClosingAmount,
        HadManagerApproval = s.DifferenceOverrideId != null
    };

    private static CashMovementDto ToDto(CashMovement m) => new()
    {
        Id = m.Id,
        Type = m.Type,
        Amount = m.Amount,
        Reason = m.Reason,
        Reference = m.Reference,
        UserName = m.UserName,
        ManagerOverrideId = m.ManagerOverrideId,
        OccurredAt = m.OccurredAt
    };

    private static List<CashCountLineDto> ValidateCountLines(IEnumerable<CashCountLineDto>? countLines, string paramName)
    {
        var lines = (countLines ?? Enumerable.Empty<CashCountLineDto>()).Where(l => l.Quantity != 0).ToList();
        if (lines.Any(l => l.Quantity < 0 || l.Denomination <= 0))
            throw new ArgumentException("Cantidades y denominaciones deben ser positivas.", paramName);
        return lines;
    }

    private async Task EnsureNoActiveComandasAsync(CancellationToken ct)
    {
        var activeComandas = await _db.Comandas
            .CountAsync(c => c.Status == ComandaStatus.Open || c.Status == ComandaStatus.ReadyToPay, ct);
        if (activeComandas > 0)
            throw new InvalidOperationException($"Hay {activeComandas} comanda(s) abiertas. Ciérrelas antes del cuadre de caja.");
    }

    private async Task<CashShift> RequireOpenShiftAsync(CancellationToken ct) =>
        await GetCurrentShiftAsync(ct) ?? throw new InvalidOperationException("No hay ningún turno abierto.");

    /// <summary>Tracked load + single-use check (same pattern as ComandaService.CancelConfirmedItemAsync).</summary>
    private async Task<ManagerOverride> RequireOverrideAsync(Guid? overrideId, AuthorizedAction action, CancellationToken ct)
    {
        var o = overrideId is { } id ? await _db.ManagerOverrides.FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        if (o == null || o.Action != action || o.ConsumedAt != null)
            throw new InvalidOperationException("Se requiere autorización de gerente.");
        return o;
    }

    private void Consume(ManagerOverride o, string consumedFor)
    {
        o.ConsumedAt = UtcNow;
        o.ConsumedFor = consumedFor;
        o.UpdatedAt = UtcNow;
    }
}
