using System;
using System.Collections.Generic;
using System.Linq;
using Core.Domain.Enums;

namespace Core.Application.Kitchen;

public enum KitchenEventType
{
    Created = 0,        // new ticket for a station
    StatusChanged = 1,  // Pending -> InPreparation -> Ready -> Delivered
    Printed = 2,
    ItemCancelled = 3,  // a line was struck through (manager)
    Undone = 4          // Delivered -> Ready (undo)
}

/// <summary>What changed on the KDS. Published AFTER the change is committed to the DB.</summary>
public sealed record KitchenEvent(
    KitchenEventType Type,
    Guid KitchenOrderId,
    string Station,
    string OrderNumber,
    string TableNumber,
    KitchenOrderStatus Status,
    DateTime OccurredAt);

/// <summary>
/// In-process pub/sub so the desktop KDS and the embedded Kestrel WebSocket hub react instantly
/// instead of polling. Handlers must be fast and must not throw (exceptions are swallowed).
/// Handlers run on the publisher's thread: UI subscribers must marshal to the dispatcher.
/// </summary>
public interface IKitchenEventBus
{
    void Publish(KitchenEvent e);
    IDisposable Subscribe(Action<KitchenEvent> handler);
}

/// <summary>Default thread-safe implementation (singleton).</summary>
public sealed class KitchenEventBus : IKitchenEventBus
{
    private readonly object _gate = new();
    private readonly List<Action<KitchenEvent>> _handlers = new();

    public void Publish(KitchenEvent e)
    {
        Action<KitchenEvent>[] snapshot;
        lock (_gate) snapshot = _handlers.ToArray();
        foreach (var h in snapshot)
        {
            try { h(e); }
            catch { /* a broken subscriber must never break the sale */ }
        }
    }

    public IDisposable Subscribe(Action<KitchenEvent> handler)
    {
        lock (_gate) _handlers.Add(handler);
        return new Subscription(() => { lock (_gate) _handlers.Remove(handler); });
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        public Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
