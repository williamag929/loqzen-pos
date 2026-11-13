using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Hardware;

namespace Core.Application.Services;

/// <summary>
/// Application side of card terminals: registry (Maestros → 💳 Datáfonos), assignment to waiters, and the charge
/// flow = write a Pending <c>TerminalPayment</c> → call the provider driver → update the row. The caller then
/// records the sale/comanda/recaudo with <see cref="TerminalPaymentDto.ReferenceText"/> as the payment reference.
/// </summary>
public interface IPaymentTerminalService
{
    Task<IReadOnlyList<PaymentTerminalDto>> GetTerminalsAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<PaymentTerminalDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    /// <summary>Create (Id null) or update. Name required and unique among active (case/accent-insensitive); Address required unless Provider == Mock.</summary>
    Task<PaymentTerminalDto> SaveAsync(SavePaymentTerminalRequest request, CancellationToken ct = default);
    /// <summary>Soft delete (IsActive = false). Throws if a charge is Pending on it.</summary>
    Task DeactivateAsync(Guid id, CancellationToken ct = default);

    /// <summary>Hands the terminal to a waiter (userId/name) or back to the register (null, null).</summary>
    Task<PaymentTerminalDto> AssignAsync(Guid terminalId, Guid? userId, string? userName, CancellationToken ct = default);

    /// <summary>Driver ping; stores LastSeenAt/LastPingOk. Never throws.</summary>
    Task<bool> TestConnectionAsync(Guid terminalId, CancellationToken ct = default);

    /// <summary>
    /// Sends the amount to the terminal and waits. Validates: terminal active, amount &gt; 0, no other Pending charge on
    /// that terminal (409 InvalidOperationException "El datáfono está ocupado"). Applies
    /// PaymentTerminalSettings.ChargeTimeoutSeconds (→ Timeout) and the caller's token (→ Cancelled). The returned DTO
    /// is the persisted audit row. Business outcomes never throw.
    /// </summary>
    Task<TerminalPaymentDto> ChargeAsync(ChargeOnTerminalRequest request, IProgress<TerminalChargeProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Pay-at-table: the comanda is marked ReadyToPay if still Open, charged on the terminal for its total and, when
    /// approved, closed with IComandaService.CloseAndPayAsync(Card, total, reference) on the register's open shift
    /// (ICashShiftService.GetCurrentShiftAsync; InvalidOperationException "Abra turno en la caja" when none).
    /// Declined/timeout/error → Charge filled, Payment null, comanda left ReadyToPay.
    /// </summary>
    Task<PayComandaOnTerminalResult> PayComandaOnTerminalAsync(Guid comandaId, Guid terminalId, Guid? requestedByUserId, string requestedByName, IProgress<TerminalChargeProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Charges newest first (Maestros tab + reconciliation). Filter by terminal when given.</summary>
    Task<IReadOnlyList<TerminalPaymentDto>> GetRecentChargesAsync(int take = 50, Guid? terminalId = null, CancellationToken ct = default);

    /// <summary>Approved terminal charges in a shift (to compare with the datáfono's batch close).</summary>
    Task<IReadOnlyList<TerminalPaymentDto>> GetApprovedForShiftAsync(Guid cashShiftId, CancellationToken ct = default);
}
