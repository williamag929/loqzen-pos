using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Domain.Entities;
using Core.Domain.Enums;

namespace Core.Application.Services;

public interface ICashShiftService
{
    /// <summary>Gets the currently open shift, or null if no shift is open.</summary>
    Task<CashShift?> GetCurrentShiftAsync(CancellationToken ct = default);

    /// <summary>Opens a new cash shift. Throws if one is already open.</summary>
    Task<CashShift> OpenShiftAsync(string cashierName, decimal openingAmount, CancellationToken ct = default);

    /// <summary>
    /// Closes the currently open shift.
    /// Calculates: ExpectedAmount = OpeningAmount + sum of cash sales in shift.
    /// Returns the closed shift with Difference populated.
    /// </summary>
    Task<CashShift> CloseShiftAsync(decimal closingAmount, string? notes = null, CancellationToken ct = default);

    /// <summary>Returns the total cash collected (PaymentMethod.Cash) for the given shift.</summary>
    Task<decimal> GetCashTotalForShiftAsync(Guid shiftId, CancellationToken ct = default);
}
