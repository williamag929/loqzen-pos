using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;

namespace Core.Application.Services;

/// <summary>
/// Bill payments at the counter (recaudos). Not sales: own receipt numbers (REC-000001), excluded from
/// sales totals, reconciled in the cash close. A cash payment creates a CashMovement (BillPayment) in the
/// same SaveChanges; voiding a cash payment creates a BillPaymentVoid movement.
/// </summary>
public interface IBillPaymentService
{
    Task<IReadOnlyList<BillerDto>> GetBillersAsync(bool includeInactive = false, CancellationToken ct = default);
    /// <summary>Name required (max 100); commissions &gt;= 0; percent &lt;= 0.2.</summary>
    Task<BillerDto> SaveBillerAsync(BillerDto biller, CancellationToken ct = default);

    /// <summary>
    /// Requires an open shift and an active biller; reference required (max 60); amount &gt; 0.
    /// Commission = biller.CommissionFor(amount). Cash: tendered &gt;= total, change = tendered − total.
    /// Card/Transfer: tendered = total. Outbox "BillPaymentRecorded".
    /// </summary>
    Task<BillPaymentDto> RecordAsync(BillPaymentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Voids a payment of the OPEN shift. Requires an unconsumed ManagerOverride with Action = VoidBillPayment
    /// (consumed here) and a reason. Throws if already voided or from a closed shift. Outbox "BillPaymentVoided".
    /// </summary>
    Task<BillPaymentDto> VoidAsync(Guid billPaymentId, string reason, Guid managerOverrideId, CancellationToken ct = default);

    /// <summary>Payments of a shift (default: the open one), newest first, including voided.</summary>
    Task<IReadOnlyList<BillPaymentDto>> GetForShiftAsync(Guid? shiftId = null, CancellationToken ct = default);
}
