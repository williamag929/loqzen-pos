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

        var payments = await _db.Payments
            .Where(p => p.Order != null && p.Order.CashShiftId == shift.Id && p.Order.Status == OrderStatus.Completed)
            .Select(p => new { p.Method, p.Amount, p.OrderId })
            .ToListAsync(ct);

        decimal SumOf(PaymentMethod m) => payments.Where(p => p.Method == m).Sum(p => p.Amount);

        return new CashCloseSummaryDto
        {
            ShiftId = shift.Id,
            ShiftNumber = shift.ShiftNumber,
            CashierName = shift.CashierName,
            OpenedAt = shift.OpenedAt,
            OpeningAmount = shift.OpeningAmount,
            CashSales = SumOf(PaymentMethod.Cash),
            CardSales = SumOf(PaymentMethod.Card),
            TransferSales = SumOf(PaymentMethod.Transfer),
            OtherSales = SumOf(PaymentMethod.Other),
            OrderCount = payments.Select(p => p.OrderId).Distinct().Count()
        };
    }

    public async Task<CashShift> CloseShiftWithCountAsync(IEnumerable<CashCountLineDto> countLines, string? notes = null, CancellationToken ct = default)
    {
        var lines = countLines.Where(l => l.Quantity != 0).ToList();
        if (lines.Any(l => l.Quantity < 0 || l.Denomination <= 0))
            throw new ArgumentException("Cantidades y denominaciones deben ser positivas.", nameof(countLines));

        var activeComandas = await _db.Comandas
            .CountAsync(c => c.Status == ComandaStatus.Open || c.Status == ComandaStatus.ReadyToPay, ct);
        if (activeComandas > 0)
            throw new InvalidOperationException($"Hay {activeComandas} comanda(s) abiertas. Ciérrelas antes del cuadre de caja.");

        var shift = await GetCurrentShiftAsync(ct)
            ?? throw new InvalidOperationException("No hay ningún turno abierto para cerrar.");

        foreach (var l in lines)
            _db.CashCountLines.Add(new CashCountLine { CashShiftId = shift.Id, Denomination = l.Denomination, Quantity = l.Quantity });

        var cashSales = await GetCashTotalForShiftAsync(shift.Id, ct);
        shift.ExpectedAmount = shift.OpeningAmount + cashSales;
        shift.ClosingAmount = lines.Sum(l => l.Total);
        shift.ClosedAt = DateTime.UtcNow;
        shift.Status = CashShiftStatus.Closed;
        shift.Notes = notes;
        shift.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return shift;
    }
}
