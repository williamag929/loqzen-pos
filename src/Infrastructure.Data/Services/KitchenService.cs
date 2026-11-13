using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public KitchenService(PosDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<KitchenOrder>> GetActiveAsync(CancellationToken ct = default) =>
        await _db.KitchenOrders.AsNoTracking()
            .Include(k => k.Items)
            .Where(k => k.Status != KitchenOrderStatus.Delivered && k.Status != KitchenOrderStatus.Cancelled)
            .OrderBy(k => k.CreatedAt)
            .ToListAsync(ct);

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
        return ko;
    }

    private async Task<KitchenOrder> RequireAsync(Guid id, CancellationToken ct) =>
        await _db.KitchenOrders.Include(k => k.Items).FirstOrDefaultAsync(k => k.Id == id, ct)
        ?? throw new InvalidOperationException("Orden de cocina no encontrada.");
}
