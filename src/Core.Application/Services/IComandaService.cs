using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Core.Application.Services;

/// <summary>
/// Multi-table comanda workflow. Time-dependent logic must use an injected
/// <see cref="TimeProvider"/> so it is testable.
/// </summary>
public interface IComandaService
{
    Task<IReadOnlyList<DiningTableDto>> GetTablesAsync(CancellationToken ct = default);

    /// <summary>Open + ReadyToPay comandas, oldest first.</summary>
    Task<IReadOnlyList<ComandaDto>> GetActiveComandasAsync(CancellationToken ct = default);
    Task<ComandaDto?> GetByIdAsync(Guid comandaId, CancellationToken ct = default);

    /// <summary>Throws InvalidOperationException if the table already has an active comanda.</summary>
    Task<ComandaDto> OpenComandaAsync(string tableName, string waiterName, Guid? diningTableId = null, CancellationToken ct = default);

    /// <summary>Adds a Pending item. Only allowed when comanda is Open.</summary>
    Task<ComandaDto> AddItemAsync(Guid comandaId, Guid productId, decimal quantity = 1, decimal? weightKg = null, string? notes = null, CancellationToken ct = default);

    /// <summary>Cancels an item; only Pending items can be cancelled (confirmed items are already in kitchen).</summary>
    Task<ComandaDto> CancelPendingItemAsync(Guid comandaId, Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a Confirmed item that is already in the kitchen. Requires a recorded manager approval
    /// (<paramref name="managerOverrideId"/> must exist). Marks the matching KitchenOrderItem IsCancelled
    /// (struck-through on the KDS) and, if every item of that KitchenOrder is cancelled, cancels the
    /// KitchenOrder too. Writes an Outbox "KitchenItemCancelled" in the same SaveChanges.
    /// </summary>
    Task<ComandaDto> CancelConfirmedItemAsync(Guid comandaId, Guid itemId, Guid managerOverrideId, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, if empty) the notes of a Pending item and releases its hold.
    /// Throws if the item is already in the kitchen or notes exceed ComandaSettings.MaxNotesLength.
    /// </summary>
    Task<ComandaDto> SetItemNotesAsync(Guid comandaId, Guid itemId, string? notes, CancellationToken ct = default);

    /// <summary>Puts a Pending item on hold (editing notes) or releases it. Held items are skipped by auto-confirm.</summary>
    Task<ComandaDto> SetItemHoldAsync(Guid comandaId, Guid itemId, bool onHold, CancellationToken ct = default);

    /// <summary>Confirms all Pending items of a comanda immediately (sends to KDS).</summary>
    Task<ComandaDto> ConfirmPendingNowAsync(Guid comandaId, CancellationToken ct = default);

    /// <summary>
    /// Confirms every Pending item whose AddedAt + ConfirmationDelaySeconds &lt;= now, across all comandas.
    /// For each comanda with newly confirmed items, creates ONE KitchenOrder (+ OutboxMessage "KitchenOrderCreated")
    /// in the same SaveChanges. Returns the number of items confirmed.
    /// </summary>
    Task<int> ConfirmDueItemsAsync(CancellationToken ct = default);

    /// <summary>Open -> ReadyToPay. Confirms any remaining Pending items first.</summary>
    Task<ComandaDto> MarkReadyToPayAsync(Guid comandaId, CancellationToken ct = default);

    /// <summary>ReadyToPay -> Open (customer orders more).</summary>
    Task<ComandaDto> ReopenAsync(Guid comandaId, CancellationToken ct = default);

    /// <summary>
    /// Generates the invoice: creates Order(Completed) + OrderItems + Payment + OutboxMessage("OrderCompleted")
    /// in ONE SaveChanges, sets comanda Closed with OrderId. Requires an open cash shift.
    /// </summary>
    Task<PaymentResultDto> CloseAndPayAsync(Guid comandaId, Guid cashShiftId, PaymentMethod method, decimal tenderedAmount, CancellationToken ct = default);
}
