using Core.Domain.Entities;
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
    Undone = 4,         // Delivered -> Ready (undo)
    /// <summary>A comanda changed (opened, items, ready, reopened, locked, closed). KitchenOrderId = comanda id, OrderNumber = "#N", Status = Pending. Lets every register's comanda list refresh.</summary>
    ComandaChanged = 5
}

/// <summary>
/// Wire shape of one /ws/kds message (server → browser KDS, server → satellite registers). The server serializes
/// exactly this record (camelCase, enums as names); MasterKitchenSocketClient deserializes it and republishes a
/// KitchenEvent on the local bus. Type is "event" for events, "ping"/"pong" for keep-alives.
/// </summary>
public sealed record KitchenSocketEnvelope(
    string Type,
    KitchenEventType Event,
    Guid KitchenOrderId,
    string Station,
    string OrderNumber,
    string TableNumber,
    KitchenOrderStatus Status,
    DateTime OccurredAt)
{
    public KitchenEvent ToEvent() => new(Event, KitchenOrderId, Station, OrderNumber, TableNumber, Status, OccurredAt);
}

/// <summary>
/// Full KitchenOrder over the wire (/api/kds/orders, /api/kds/orders/{id} and the mutation responses) so a satellite
/// register can rebuild the entity for its own KDS screen. Every field of KitchenOrder/KitchenOrderItem travels.
/// </summary>
public sealed record KitchenOrderWire(
    Guid Id, string OrderNumber, Guid? OrderId, string TableNumber, Guid? WaiterId, string WaiterName,
    KitchenOrderStatus Status, string Station, string? GeneralNotes, DateTime CreatedAt, DateTime? UpdatedAt,
    DateTime? PrintedAt, DateTime? DeliveredAt, IReadOnlyList<KitchenOrderItemWire> Items)
{
    public KitchenOrder ToEntity()
    {
        var ko = new KitchenOrder
        {
            Id = Id, OrderNumber = OrderNumber, OrderId = OrderId, TableNumber = TableNumber, WaiterId = WaiterId,
            WaiterName = WaiterName, Status = Status, Station = Station, GeneralNotes = GeneralNotes,
            CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, PrintedAt = PrintedAt, DeliveredAt = DeliveredAt
        };
        foreach (var i in Items) ko.Items.Add(i.ToEntity(Id));
        return ko;
    }

    public static KitchenOrderWire From(KitchenOrder ko) => new(
        ko.Id, ko.OrderNumber, ko.OrderId, ko.TableNumber, ko.WaiterId, ko.WaiterName, ko.Status, ko.Station,
        ko.GeneralNotes, ko.CreatedAt, ko.UpdatedAt, ko.PrintedAt, ko.DeliveredAt,
        ko.Items.Select(KitchenOrderItemWire.From).ToList());
}

public sealed record KitchenOrderItemWire(
    Guid Id, Guid ProductId, string ProductName, decimal Quantity, string? Notes, bool IsPrepared, bool IsCancelled, DateTime CreatedAt)
{
    public KitchenOrderItem ToEntity(Guid kitchenOrderId) => new()
    {
        Id = Id, KitchenOrderId = kitchenOrderId, ProductId = ProductId, ProductName = ProductName, Quantity = Quantity,
        Notes = Notes, IsPrepared = IsPrepared, IsCancelled = IsCancelled, CreatedAt = CreatedAt
    };
    public static KitchenOrderItemWire From(KitchenOrderItem i) =>
        new(i.Id, i.ProductId, i.ProductName, i.Quantity, i.Notes, i.IsPrepared, i.IsCancelled, i.CreatedAt);
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
