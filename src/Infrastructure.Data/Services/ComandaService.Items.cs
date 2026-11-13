using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public partial class ComandaService
{
    public async Task<ComandaDto> AddItemAsync(Guid comandaId, Guid productId, decimal quantity = 1, decimal? weightKg = null, string? notes = null, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad debe ser positiva.");
        if (weightKg is <= 0) throw new ArgumentOutOfRangeException(nameof(weightKg), "El peso debe ser positivo.");

        var comanda = await RequireAsync(comandaId, ct);
        if (comanda.Status != ComandaStatus.Open)
            throw new InvalidOperationException("Solo se pueden agregar productos a una comanda abierta.");

        var product = await _db.Products.Include(p => p.Tax).Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == productId && p.IsActive, ct)
            ?? throw new InvalidOperationException("Producto no encontrado o inactivo.");

        var item = new ComandaItem
        {
            ComandaId = comanda.Id,
            ProductId = product.Id,
            ProductName = product.Name,
            UnitPrice = product.Price,
            TaxRate = product.Tax?.Rate ?? 0m,
            Quantity = quantity,
            WeightKg = product.IsWeighed ? (weightKg ?? quantity) : null,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Station = StationFor(product),
            Status = ComandaItemStatus.Pending,
            AddedAt = UtcNow,
            UpdatedAt = UtcNow
        };
        comanda.Items.Add(item);
        _db.ComandaItems.Add(item);
        comanda.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return MapToDto(comanda);
    }

    public async Task<ComandaDto> CancelPendingItemAsync(Guid comandaId, Guid itemId, CancellationToken ct = default)
    {
        var comanda = await RequireAsync(comandaId, ct);
        var item = comanda.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException("Item no encontrado en la comanda.");
        if (item.Status != ComandaItemStatus.Pending)
            throw new InvalidOperationException("El item ya fue enviado a cocina; no se puede anular desde aquí.");

        item.Status = ComandaItemStatus.Cancelled;
        item.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return MapToDto(comanda);
    }

    public async Task<ComandaDto> CancelConfirmedItemAsync(Guid comandaId, Guid itemId, Guid managerOverrideId, CancellationToken ct = default)
    {
        // Validate everything before touching tracked entities.
        var comanda = await RequireAsync(comandaId, ct);
        if (!ActiveStatuses.Contains(comanda.Status))
            throw new InvalidOperationException("La comanda no está activa; no se pueden anular productos.");

        var item = comanda.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException("Item no encontrado en la comanda.");
        if (item.Status == ComandaItemStatus.Pending)
            throw new InvalidOperationException("El item aún no fue enviado a cocina; anúlelo como pendiente (CancelPendingItemAsync).");
        if (item.Status == ComandaItemStatus.Cancelled)
            throw new InvalidOperationException("El item ya está anulado.");
        if (item.Status != ComandaItemStatus.Confirmed)
            throw new InvalidOperationException("Solo se pueden anular items confirmados.");

        // The approval must be a real CancelKitchenItem override (a discount/return approval cannot be reused here)
        // and still unused: each approval is single-use, so one PIN cannot strike unlimited items.
        var managerOverride = await _db.ManagerOverrides.FirstOrDefaultAsync(o => o.Id == managerOverrideId, ct);
        if (managerOverride == null
            || managerOverride.Action != AuthorizedAction.CancelKitchenItem
            || managerOverride.ConsumedAt != null)
            throw new InvalidOperationException("Se requiere autorización de gerente.");

        KitchenOrder? kitchenOrder = null;
        if (item.KitchenOrderId != null)
            kitchenOrder = await _db.KitchenOrders.Include(k => k.Items)
                .FirstOrDefaultAsync(k => k.Id == item.KitchenOrderId, ct);

        // --- mutate ---
        item.Status = ComandaItemStatus.Cancelled;
        item.UpdatedAt = UtcNow;
        comanda.UpdatedAt = UtcNow;

        // Consume the approval in the same SaveChanges as the cancellation.
        managerOverride.ConsumedAt = UtcNow;
        managerOverride.ConsumedFor = $"ComandaItem {item.Id}";
        managerOverride.UpdatedAt = UtcNow;

        if (kitchenOrder != null)
        {
            // ComandaItem has no FK to its kitchen line: prefer the line that matches product + notes +
            // quantity (so "sin cebolla" is struck, not its twin), then fall back to product only.
            var qty = item.WeightKg ?? item.Quantity;
            var candidates = kitchenOrder.Items.Where(ki => ki.ProductId == item.ProductId && !ki.IsCancelled).ToList();
            var kitchenItem = candidates.FirstOrDefault(ki => ki.Notes == item.Notes && ki.Quantity == qty)
                           ?? candidates.FirstOrDefault(ki => ki.Notes == item.Notes)
                           ?? candidates.FirstOrDefault();
            if (kitchenItem != null)
            {
                kitchenItem.IsCancelled = true;
                kitchenItem.UpdatedAt = UtcNow;
            }
            // A ticket that is already Ready/Delivered stays as it was (the dish exists); only tickets
            // still being worked on are cancelled outright when nothing is left to cook.
            var stillCooking = kitchenOrder.Status is KitchenOrderStatus.Pending or KitchenOrderStatus.InPreparation;
            if (stillCooking && kitchenOrder.Items.Count > 0 && kitchenOrder.Items.All(ki => ki.IsCancelled))
            {
                kitchenOrder.Status = KitchenOrderStatus.Cancelled;
                kitchenOrder.UpdatedAt = UtcNow;
            }
        }

        _db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "KitchenItemCancelled",
            AggregateType = "KitchenOrder",
            AggregateId = kitchenOrder?.Id ?? comanda.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                comandaId = comanda.Id,
                itemId = item.Id,
                productName = item.ProductName,
                managerOverrideId
            }),
            Status = SyncStatus.Pending,
            UpdatedAt = UtcNow
        });

        await _db.SaveChangesAsync(ct);
        // Status reflects the ticket after the change (may be Cancelled when nothing is left to cook).
        if (kitchenOrder != null) PublishKitchenEvent(KitchenEventType.ItemCancelled, kitchenOrder);
        return MapToDto(comanda);
    }

    public async Task<ComandaDto> SetItemNotesAsync(Guid comandaId, Guid itemId, string? notes, CancellationToken ct = default)
    {
        var clean = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (clean?.Length > ComandaSettings.MaxNotesLength)
            throw new ArgumentException($"La nota no puede superar {ComandaSettings.MaxNotesLength} caracteres.", nameof(notes));

        var (comanda, item) = await RequirePendingItemAsync(comandaId, itemId, ct);
        item.Notes = clean;
        item.IsOnHold = false; // saved → let it go to the kitchen (immediately if its delay already elapsed)
        item.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return MapToDto(comanda);
    }

    public async Task<ComandaDto> SetItemHoldAsync(Guid comandaId, Guid itemId, bool onHold, CancellationToken ct = default)
    {
        var (comanda, item) = await RequirePendingItemAsync(comandaId, itemId, ct);
        item.IsOnHold = onHold;
        item.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return MapToDto(comanda);
    }

    private async Task<(Comanda, ComandaItem)> RequirePendingItemAsync(Guid comandaId, Guid itemId, CancellationToken ct)
    {
        var comanda = await RequireAsync(comandaId, ct);
        var item = comanda.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException("Item no encontrado en la comanda.");
        if (item.Status != ComandaItemStatus.Pending)
            throw new InvalidOperationException("El item ya fue enviado a cocina; la nota ya no se puede cambiar.");
        return (comanda, item);
    }

    public async Task<ComandaDto> ConfirmPendingNowAsync(Guid comandaId, CancellationToken ct = default)
    {
        var comanda = await RequireAsync(comandaId, ct);
        ConfirmItems(comanda, comanda.Items.Where(i => i.Status == ComandaItemStatus.Pending).ToList());
        await SaveAndPublishTicketsAsync(ct);
        return MapToDto(comanda);
    }

    public async Task<int> ConfirmDueItemsAsync(CancellationToken ct = default)
    {
        var cutoff = UtcNow.AddSeconds(-Math.Max(0, _settings.ConfirmationDelaySeconds));
        var comandas = await _db.Comandas.Include(c => c.Items)
            .Where(c => ActiveStatuses.Contains(c.Status))
            .Where(c => c.Items.Any(i => i.Status == ComandaItemStatus.Pending && !i.IsOnHold && i.AddedAt <= cutoff))
            .ToListAsync(ct);

        var count = 0;
        foreach (var c in comandas)
        {
            var due = c.Items.Where(i => i.Status == ComandaItemStatus.Pending && !i.IsOnHold && i.AddedAt <= cutoff).ToList();
            ConfirmItems(c, due);
            count += due.Count;
        }

        if (count > 0) await SaveAndPublishTicketsAsync(ct);
        return count;
    }
}
