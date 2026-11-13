using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public class CashShiftService : ICashShiftService
{
    private readonly PosDbContext _db;

    public CashShiftService(PosDbContext db) => _db = db;

    public async Task<CashShift?> GetCurrentShiftAsync(CancellationToken ct = default)
        => await _db.CashShifts
            .FirstOrDefaultAsync(s => s.Status == CashShiftStatus.Open, ct);

    public async Task<CashShift> OpenShiftAsync(string cashierName, decimal openingAmount, CancellationToken ct = default)
    {
        var existing = await GetCurrentShiftAsync(ct);
        if (existing != null)
            throw new InvalidOperationException($"Ya existe un turno abierto (Turno #{existing.ShiftNumber} – {existing.CashierName}).");

        var lastShift = await _db.CashShifts
            .OrderByDescending(s => s.ShiftNumber)
            .Select(s => s.ShiftNumber)
            .FirstOrDefaultAsync(ct);

        var shift = new CashShift
        {
            ShiftNumber  = lastShift + 1,
            CashierName  = cashierName,
            OpeningAmount = openingAmount,
            OpenedAt     = DateTime.UtcNow,
            Status       = CashShiftStatus.Open
        };

        _db.CashShifts.Add(shift);
        await _db.SaveChangesAsync(ct);
        return shift;
    }

    public async Task<CashShift> CloseShiftAsync(decimal closingAmount, string? notes = null, CancellationToken ct = default)
    {
        var shift = await GetCurrentShiftAsync(ct)
            ?? throw new InvalidOperationException("No hay ningún turno abierto para cerrar.");

        var cashSales = await GetCashTotalForShiftAsync(shift.Id, ct);
        shift.ExpectedAmount = shift.OpeningAmount + cashSales;
        shift.ClosingAmount  = closingAmount;
        shift.ClosedAt       = DateTime.UtcNow;
        shift.Status         = CashShiftStatus.Closed;
        shift.Notes          = notes;
        shift.UpdatedAt      = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return shift;
    }

    public async Task<decimal> GetCashTotalForShiftAsync(Guid shiftId, CancellationToken ct = default)
        => await _db.Payments
            .Where(p => p.Order != null
                     && p.Order.CashShiftId == shiftId
                     && p.Method == PaymentMethod.Cash)
            .SumAsync(p => p.Amount, ct);
}
