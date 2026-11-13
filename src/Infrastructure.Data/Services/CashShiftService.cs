using System;
using System.Collections.Generic;
using System.Linq;
using Core.Application.DTOs;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

/// <summary>
/// Cash shift lifecycle and reconciliation. Split into partial files: legacy API (this),
/// Reconciliation (expected cash, cuadre, history), Operations (movements, partial counts, close).
/// </summary>
public partial class CashShiftService : ICashShiftService
{
    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;
    private readonly CashShiftSettings _settings;

    public CashShiftService(PosDbContext db, TimeProvider? clock = null, CashShiftSettings? settings = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _settings = settings ?? new CashShiftSettings();
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task<CashShift?> GetCurrentShiftAsync(CancellationToken ct = default)
        => await _db.CashShifts
            .FirstOrDefaultAsync(s => s.Status == CashShiftStatus.Open, ct);

    public Task<CashShift> OpenShiftAsync(string cashierName, decimal openingAmount, CancellationToken ct = default)
        => OpenShiftAsync(cashierName, openingAmount, null, ct);

    public async Task<CashShift> CloseShiftAsync(decimal closingAmount, string? notes = null, CancellationToken ct = default)
    {
        var shift = await GetCurrentShiftAsync(ct)
            ?? throw new InvalidOperationException("No hay ningún turno abierto para cerrar.");

        var expected = (await ComputeAsync(shift, ct)).ExpectedCash;
        shift.ExpectedAmount = expected;
        shift.ClosingAmount  = closingAmount;
        shift.ClosedAt       = UtcNow;
        shift.Status         = CashShiftStatus.Closed;
        shift.Notes          = notes;
        shift.UpdatedAt      = UtcNow;

        await _db.SaveChangesAsync(ct);
        return shift;
    }

    public async Task<decimal> GetCashTotalForShiftAsync(Guid shiftId, CancellationToken ct = default)
    {
        // Sum client-side: SQLite cannot SUM decimal columns via EF Core.
        var amounts = await _db.Payments
            .Where(p => p.Order != null
                     && p.Order.CashShiftId == shiftId
                     && p.Method == PaymentMethod.Cash)
            .Select(p => p.Amount)
            .ToListAsync(ct);
        return amounts.Sum();
    }

    public async Task<CashCloseSummaryDto> GetCloseSummaryAsync(CancellationToken ct = default)
    {
        var shift = await GetCurrentShiftAsync(ct)
            ?? throw new InvalidOperationException("No hay ningún turno abierto.");

        // Same breakdown as GetReconciliationAsync (completed orders only), so both screens agree.
        var r = await ComputeAsync(shift, ct);
        return new CashCloseSummaryDto
        {
            ShiftId = shift.Id,
            ShiftNumber = shift.ShiftNumber,
            CashierName = shift.CashierName,
            OpenedAt = shift.OpenedAt,
            OpeningAmount = shift.OpeningAmount,
            CashSales = r.CashSales,
            CardSales = r.CardSales,
            TransferSales = r.TransferSales,
            OtherSales = r.OtherSales,
            OrderCount = r.OrderCount,
            NonSaleCashNet = r.ExpectedCash - r.OpeningAmount - r.CashSales
        };
    }

    public async Task<CashShift> CloseShiftWithCountAsync(IEnumerable<CashCountLineDto> countLines, string? notes = null, CancellationToken ct = default)
    {
        var lines = ValidateCountLines(countLines, nameof(countLines));
        await EnsureNoActiveComandasAsync(ct);

        var shift = await GetCurrentShiftAsync(ct)
            ?? throw new InvalidOperationException("No hay ningún turno abierto para cerrar.");

        var expected = (await ComputeAsync(shift, ct)).ExpectedCash;

        foreach (var l in lines)
            _db.CashCountLines.Add(new CashCountLine { CashShiftId = shift.Id, Denomination = l.Denomination, Quantity = l.Quantity });

        shift.ExpectedAmount = expected;
        shift.ClosingAmount = lines.Sum(l => l.Total);
        shift.ClosedAt = UtcNow;
        shift.Status = CashShiftStatus.Closed;
        shift.Notes = notes;
        shift.UpdatedAt = UtcNow;

        await _db.SaveChangesAsync(ct);
        return shift;
    }
}
