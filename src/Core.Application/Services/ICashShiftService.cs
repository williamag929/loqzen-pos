using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Core.Application.DTOs;
using System.Collections.Generic;

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
    /// <remarks>Legacy: use GetReconciliationAsync / CloseShiftAsync(CloseShiftRequest).</remarks>
    Task<CashShift> CloseShiftAsync(decimal closingAmount, string? notes = null, CancellationToken ct = default);

    /// <summary>Returns the total cash collected (PaymentMethod.Cash) for the given shift.</summary>
    Task<decimal> GetCashTotalForShiftAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>Cuadre de caja: sales by payment method + expected cash for the open shift.</summary>
    /// <remarks>Legacy: use GetReconciliationAsync / CloseShiftAsync(CloseShiftRequest).</remarks>
    Task<Core.Application.DTOs.CashCloseSummaryDto> GetCloseSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// Closes the open shift from a denomination count (arqueo). ClosingAmount = sum of lines.
    /// Count lines are persisted with the shift. Throws if any comanda is still active.
    /// </summary>
    /// <remarks>Legacy: use GetReconciliationAsync / CloseShiftAsync(CloseShiftRequest).</remarks>
    Task<CashShift> CloseShiftWithCountAsync(System.Collections.Generic.IEnumerable<Core.Application.DTOs.CashCountLineDto> countLines, string? notes = null, CancellationToken ct = default);

    // ── Cuadre de caja de referencia (2026-11) ───────────────────────────────

    /// <summary>Opening amount to suggest: FloatLeftAmount of the last closed shift, else CashShiftSettings.DefaultFloat.</summary>
    Task<decimal> GetSuggestedOpeningAmountAsync(CancellationToken ct = default);

    /// <summary>Opens a shift recording who opened it. Same rules as OpenShiftAsync.</summary>
    Task<CashShift> OpenShiftAsync(string cashierName, decimal openingAmount, Guid? openedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Records a manual movement on the OPEN shift (only CashMovementTypes.Manual). Amount &gt; 0, reason required.
    /// When CashShiftSettings.MovementRequiresManager(type), request.ManagerOverrideId must reference an unconsumed
    /// ManagerOverride with Action = CashMovement; it is consumed in the same SaveChanges. Outbox "CashMovementRecorded".
    /// A cash-out larger than the current expected cash throws InvalidOperationException.
    /// </summary>
    Task<CashMovementDto> RecordMovementAsync(CashMovementRequest request, CancellationToken ct = default);

    /// <summary>Reconciliation of the open shift (or of <paramref name="shiftId"/>, open or closed).</summary>
    Task<ShiftReconciliationDto> GetReconciliationAsync(Guid? shiftId = null, CancellationToken ct = default);

    /// <summary>Mid-shift count: stores expected vs counted; does not close. Returns the stored count.</summary>
    Task<PartialCountDto> RecordPartialCountAsync(IEnumerable<CashCountLineDto> lines, string userName, CancellationToken ct = default);

    /// <summary>
    /// Closes the open shift from a denomination count. Blocks if comandas are open. If |difference| &gt;
    /// MaxDifferenceWithoutManager: requires DifferenceOverrideId (unconsumed, Action = CashDifference, consumed here)
    /// AND non-empty Notes, otherwise throws CashDifferenceRequiresApprovalException (nothing saved).
    /// FloatLeftAmount must be between 0 and the counted cash. Outbox "CashShiftClosed". Returns the final reconciliation.
    /// </summary>
    Task<ShiftReconciliationDto> CloseShiftAsync(CloseShiftRequest request, CancellationToken ct = default);

    /// <summary>Closed shifts, newest first. Scoped to THIS terminal when an ITerminalContext is set.</summary>
    Task<IReadOnlyList<ShiftHistoryItemDto>> GetHistoryAsync(int take = 30, CancellationToken ct = default);

    // ── Multi-register (2026-11, segunda caja) ───────────────────────────────
    // Every "current shift" query above is scoped to the current terminal (ITerminalContext) when one is set:
    // on the master the database also holds the satellites' replicated shifts, which may be Open.
    // Legacy rows (TerminalId == null) count as the master's.

    /// <summary>
    /// Cierre del día across registers: all shifts whose OpenedAt falls in the LOCAL day (TimeProvider.LocalTimeZone),
    /// grouped by terminal, plus their orders/bill payments/movements. Includes open shifts (flagged).
    /// </summary>
    Task<DayConsolidationDto> GetDayConsolidationAsync(DateOnly localDay, CancellationToken ct = default);
}
