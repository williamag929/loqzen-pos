using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Domain.Entities;

namespace Core.Application.Services;

/// <summary>KDS operations. Time comes from an injected TimeProvider.</summary>
public interface IKitchenService
{
    /// <summary>Orders not yet delivered/cancelled, with items, in ARRIVAL order (oldest first). Not tracked.</summary>
    Task<IReadOnlyList<KitchenOrder>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>Pending → InPreparation → Ready → Delivered (sets DeliveredAt). Returns the updated order.</summary>
    Task<KitchenOrder> AdvanceAsync(Guid kitchenOrderId, CancellationToken ct = default);

    /// <summary>Records the first print time (later reprints keep the original). Returns the updated order.</summary>
    Task<KitchenOrder> MarkPrintedAsync(Guid kitchenOrderId, CancellationToken ct = default);

    /// <summary>Undo "Entregar" during the archive window: Delivered → Ready, clears DeliveredAt.</summary>
    Task<KitchenOrder> UndoDeliverAsync(Guid kitchenOrderId, CancellationToken ct = default);
}
