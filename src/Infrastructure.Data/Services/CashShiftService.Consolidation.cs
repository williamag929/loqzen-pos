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
    private const string LocalTerminalCode = "LOCAL";

    /// <summary>
    /// Cierre del día: every shift whose OpenedAt falls in the LOCAL day (TimeProvider.LocalTimeZone), grouped by
    /// register, with the same sums as <see cref="ComputeAsync"/> (completed orders' payments by method, completed
    /// bill payments, manual movements) and Σ(Closing − Expected) over the closed shifts. Legacy rows
    /// (TerminalId == null) count as the context terminal, or as "LOCAL" when no context is set.
    /// Sums are done in memory: SQLite cannot SUM decimals.
    /// </summary>
    public async Task<DayConsolidationDto> GetDayConsolidationAsync(DateOnly localDay, CancellationToken ct = default)
    {
        var tz = _clock.LocalTimeZone;
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), tz);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), tz);

        var result = new DayConsolidationDto { Day = localDay, FromUtc = fromUtc, ToUtc = toUtc };

        var shifts = await _db.CashShifts.AsNoTracking()
            .Where(s => s.OpenedAt >= fromUtc && s.OpenedAt < toUtc)
            .ToListAsync(ct);
        if (shifts.Count == 0) return result;

        var shiftIds = shifts.Select(s => s.Id).ToList();

        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.Order != null && p.Order.CashShiftId != null && shiftIds.Contains(p.Order.CashShiftId.Value)
                     && p.Order.Status == OrderStatus.Completed)
            .Select(p => new { ShiftId = p.Order!.CashShiftId!.Value, p.Method, p.Amount, p.OrderId })
            .ToListAsync(ct);

        var bills = await _db.BillPayments.AsNoTracking()
            .Where(b => shiftIds.Contains(b.CashShiftId) && b.Status == BillPaymentStatus.Completed)
            .Select(b => new { ShiftId = b.CashShiftId, b.Amount, b.Commission })
            .ToListAsync(ct);

        var movements = await _db.CashMovements.AsNoTracking()
            .Where(m => shiftIds.Contains(m.CashShiftId))
            .Select(m => new { ShiftId = m.CashShiftId, m.Type, m.Amount })
            .ToListAsync(ct);

        var terminals = await _db.Terminals.AsNoTracking().ToListAsync(ct);
        var terminalById = terminals.ToDictionary(t => t.Id);

        // Legacy rows: the context terminal when set, else a synthetic "LOCAL" bucket.
        Guid? legacyId = _terminal is { IsSet: true } ? _terminal.Id : null;
        var legacyCode = _terminal is { IsSet: true } ? _terminal.Code : LocalTerminalCode;

        var groups = shifts
            .GroupBy(s => (Id: s.TerminalId ?? legacyId, Code: s.TerminalId == null ? legacyCode : (s.TerminalCode ?? legacyCode)))
            .ToList();

        foreach (var g in groups)
        {
            var ids = g.Select(s => s.Id).ToHashSet();
            var pays = payments.Where(p => ids.Contains(p.ShiftId)).ToList();
            var bl = bills.Where(b => ids.Contains(b.ShiftId)).ToList();
            var mv = movements.Where(m => ids.Contains(m.ShiftId)).ToList();

            decimal Sales(PaymentMethod m) => pays.Where(p => p.Method == m).Sum(p => p.Amount);
            decimal Moves(Func<CashMovementType, bool> pick) => mv.Where(m => pick(m.Type)).Sum(m => m.Amount);

            var terminal = g.Key.Id is { } tid && terminalById.TryGetValue(tid, out var t) ? t : null;
            var name = terminal?.Name
                ?? (_terminal is { IsSet: true } && g.Key.Id == _terminal.Id ? _terminal.Name : null)
                ?? (g.Key.Code == LocalTerminalCode ? "Esta caja" : g.Key.Code);

            var day = new TerminalDayDto
            {
                TerminalId = g.Key.Id,
                TerminalCode = g.Key.Code,
                TerminalName = name,
                ShiftCount = g.Count(),
                OpenShiftCount = g.Count(s => s.Status == CashShiftStatus.Open),
                OrderCount = pays.Select(p => p.OrderId).Distinct().Count(),
                CashSales = Sales(PaymentMethod.Cash),
                CardSales = Sales(PaymentMethod.Card),
                TransferSales = Sales(PaymentMethod.Transfer),
                OtherSales = Sales(PaymentMethod.Other),
                BillPaymentCount = bl.Count,
                BillPaymentsTotal = bl.Sum(b => b.Amount),
                BillPaymentCommission = bl.Sum(b => b.Commission),
                CashIn = Moves(t => t == CashMovementType.PaidIn),
                CashOut = Moves(t => t is CashMovementType.PaidOut or CashMovementType.Expense or CashMovementType.SafeDrop or CashMovementType.TipPayout),
                Difference = g.Where(s => s.Status == CashShiftStatus.Closed).Sum(s => s.ClosingAmount - s.ExpectedAmount),
                Shifts = g.OrderBy(s => s.ShiftNumber).ThenBy(s => s.OpenedAt).Select(ToHistoryItem).ToList()
            };
            result.Terminals.Add(day);
        }

        // Master first, then by code.
        result.Terminals = result.Terminals
            .OrderBy(d => d.TerminalId is { } id && terminalById.TryGetValue(id, out var t) && t.Kind == TerminalKind.Master ? 0
                        : _terminal is { IsSet: true, Kind: TerminalKind.Master } && d.TerminalId == _terminal.Id ? 0
                        : d.TerminalCode == LocalTerminalCode ? 0 : 1)
            .ThenBy(d => d.TerminalCode, StringComparer.Ordinal)
            .ToList();
        return result;
    }
}
