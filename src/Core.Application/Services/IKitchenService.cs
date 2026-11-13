using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Domain.Entities;

namespace Core.Application.Services;

/// <summary>
/// KDS operations. Time comes from an injected TimeProvider. Every mutation publishes a
/// <see cref="Core.Application.Kitchen.KitchenEvent"/> on <see cref="Core.Application.Kitchen.IKitchenEventBus"/>
/// AFTER SaveChanges so the desktop KDS and the LAN WebSocket hub refresh instantly.
/// </summary>
public interface IKitchenService
{
    /// <summary>
    /// Orders not yet delivered/cancelled, with items, in ARRIVAL order (oldest first). Not tracked.
    /// <paramref name="station"/> filters by station (case-insensitive); null/empty = all.
    /// </summary>
    Task<IReadOnlyList<KitchenOrder>> GetActiveAsync(string? station = null, CancellationToken ct = default);

    /// <summary>Not tracked; null when it does not exist.</summary>
    Task<KitchenOrder?> GetByIdAsync(Guid kitchenOrderId, CancellationToken ct = default);

    /// <summary>
    /// Persists a new ticket (quick sale, comandera tablet) + Outbox "KitchenOrderCreated" in one SaveChanges,
    /// then publishes <c>KitchenEventType.Created</c>. Sets CreatedAt from the clock if default.
    /// </summary>
    Task<KitchenOrder> CreateAsync(KitchenOrder order, CancellationToken ct = default);

    /// <summary>Pending → InPreparation → Ready → Delivered (sets DeliveredAt). Returns the updated order.</summary>
    Task<KitchenOrder> AdvanceAsync(Guid kitchenOrderId, CancellationToken ct = default);

    /// <summary>Records the first print time (later reprints keep the original). Returns the updated order.</summary>
    Task<KitchenOrder> MarkPrintedAsync(Guid kitchenOrderId, CancellationToken ct = default);

    /// <summary>Undo "Entregar" during the archive window: Delivered → Ready, clears DeliveredAt.</summary>
    Task<KitchenOrder> UndoDeliverAsync(Guid kitchenOrderId, CancellationToken ct = default);
}
