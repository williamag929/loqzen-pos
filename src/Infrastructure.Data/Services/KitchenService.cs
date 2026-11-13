using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public class KitchenService : IKitchenService
{
    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IKitchenEventBus? _events;

    /// <param name="events">Optional: when provided, every mutation publishes a KitchenEvent after SaveChanges.</param>
    public KitchenService(PosDbContext db, TimeProvider clock, IKitchenEventBus? events = null)
    {
        _db = db;
        _clock = clock;
        _events = events;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private void Publish(KitchenEventType type, KitchenOrder ko) =>
        _events?.Publish(new KitchenEvent(type, ko.Id, ko.Station, ko.OrderNumber, ko.TableNumber, ko.Status, UtcNow));

    public async Task<IReadOnlyList<KitchenOrder>> GetActiveAsync(string? station = null, CancellationToken ct = default)
    {
        var query = _db.KitchenOrders.AsNoTracking()
            .Include(k => k.Items)
            .Where(k => k.Status != KitchenOrderStatus.Delivered && k.Status != KitchenOrderStatus.Cancelled);
        if (!string.IsNullOrWhiteSpace(station))
        {
            var wanted = station.Trim().ToLower();
            query = query.Where(k => k.Station.ToLower() == wanted);
        }
        return await query.OrderBy(k => k.CreatedAt).ToListAsync(ct);
    }

    public Task<KitchenOrder?> GetByIdAsync(Guid kitchenOrderId, CancellationToken ct = default) =>
        _db.KitchenOrders.AsNoTracking().Include(k => k.Items).FirstOrDefaultAsync(k => k.Id == kitchenOrderId, ct);

    public async Task<KitchenOrder> CreateAsync(KitchenOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.OrderNumber)) throw new ArgumentException("OrderNumber es obligatorio.", nameof(order));
        if (order.Items.Count == 0) throw new ArgumentException("La orden de cocina no tiene productos.", nameof(order));

        if (order.CreatedAt == default) order.CreatedAt = UtcNow;
        order.UpdatedAt = UtcNow;
        _db.KitchenOrders.Add(order);
        _db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "KitchenOrderCreated",
            AggregateType = "KitchenOrder",
            AggregateId = order.Id,
            PayloadJson = JsonSerializer.Serialize(new { kitchenOrderId = order.Id, order.OrderNumber, table = order.TableNumber, station = order.Station, items = order.Items.Count }),
            Status = SyncStatus.Pending,
            UpdatedAt = UtcNow
        });
        await _db.SaveChangesAsync(ct);
        Publish(KitchenEventType.Created, order);
        return order;
    }

    public async Task<KitchenOrder> AdvanceAsync(Guid kitchenOrderId, CancellationToken ct = default)
    {
        var ko = await RequireAsync(kitchenOrderId, ct);
        ko.Status = ko.Status switch
        {
            KitchenOrderStatus.Pending => KitchenOrderStatus.InPreparation,
            KitchenOrderStatus.InPreparation => KitchenOrderStatus.Ready,
            KitchenOrderStatus.Ready => KitchenOrderStatus.Delivered,
            _ => throw new InvalidOperationException("La orden ya fue entregada o cancelada.")
        };
        if (ko.Status == KitchenOrderStatus.Delivered) ko.DeliveredAt = UtcNow;
        ko.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        Publish(KitchenEventType.StatusChanged, ko);
        return ko;
    }

    public async Task<KitchenOrder> MarkPrintedAsync(Guid kitchenOrderId, CancellationToken ct = default)
    {
        var ko = await RequireAsync(kitchenOrderId, ct);
        if (ko.PrintedAt == null)
        {
            ko.PrintedAt = UtcNow;
            ko.UpdatedAt = UtcNow;
            await _db.SaveChangesAsync(ct);
            Publish(KitchenEventType.Printed, ko);
        }
        return ko;
    }

    public async Task<KitchenOrder> UndoDeliverAsync(Guid kitchenOrderId, CancellationToken ct = default)
    {
        var ko = await RequireAsync(kitchenOrderId, ct);
        if (ko.Status != KitchenOrderStatus.Delivered)
            throw new InvalidOperationException("La orden no está entregada.");
        ko.Status = KitchenOrderStatus.Ready;
        ko.DeliveredAt = null;
        ko.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        Publish(KitchenEventType.Undone, ko);
        return ko;
    }

    private async Task<KitchenOrder> RequireAsync(Guid id, CancellationToken ct) =>
        await _db.KitchenOrders.Include(k => k.Items).FirstOrDefaultAsync(k => k.Id == id, ct)
        ?? throw new InvalidOperationException("Orden de cocina no encontrada.");
}
