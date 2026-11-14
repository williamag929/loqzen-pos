using System;
using System.Collections.Generic;
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
    /// <summary>
    /// KitchenOrders built by <see cref="ConfirmStationBatch"/> but not yet committed. They are
    /// published as <see cref="KitchenEventType.Created"/> only after the SaveChanges that persists them
    /// (see <see cref="SaveAndPublishTicketsAsync"/>), never before.
    /// </summary>
    private readonly List<KitchenOrder> _createdTickets = new();

    private void PublishKitchenEvent(KitchenEventType type, KitchenOrder ko) =>
        _events?.Publish(new KitchenEvent(type, ko.Id, ko.Station, ko.OrderNumber, ko.TableNumber, ko.Status, UtcNow));

    /// <summary>
    /// "Something about this comanda changed" for every register's comanda list (KitchenOrderId = comanda id,
    /// Station "", OrderNumber "#N"). Published AFTER SaveChanges, never before.
    /// </summary>
    private void PublishComandaChanged(Comanda c) =>
        _events?.Publish(new KitchenEvent(KitchenEventType.ComandaChanged, c.Id, string.Empty, $"#{c.Number}", c.TableName, KitchenOrderStatus.Pending, UtcNow));

    /// <summary>SaveChanges, then publishes the collected tickets and one ComandaChanged per <paramref name="changed"/> comanda.</summary>
    private async Task SaveAndPublishAsync(CancellationToken ct, params Comanda[] changed)
    {
        await SaveAndPublishTicketsAsync(ct);
        foreach (var c in changed) PublishComandaChanged(c);
    }

    /// <summary>Publishes Created for every ticket collected since the last save, then forgets them.</summary>
    private void PublishPendingTickets()
    {
        if (_createdTickets.Count == 0) return;
        var batch = _createdTickets.ToArray();
        _createdTickets.Clear();
        foreach (var ko in batch) PublishKitchenEvent(KitchenEventType.Created, ko);
    }

    /// <summary>
    /// SaveChanges for any public method that may have confirmed items. Publishes the collected tickets
    /// only when the commit succeeded; on failure the pending list is dropped so a later call cannot
    /// announce tickets that never reached the DB.
    /// </summary>
    private async Task SaveAndPublishTicketsAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            _createdTickets.Clear();
            throw;
        }
        PublishPendingTickets();
    }

    /// <summary>
    /// Marks items Confirmed and creates ONE KitchenOrder per station (+ Outbox "KitchenOrderCreated"),
    /// so drinks go to the bar screen and food to the kitchen screen.
    /// Caller is responsible for SaveChanges so everything commits atomically (use
    /// <see cref="SaveAndPublishTicketsAsync"/> so the KDS is notified once the tickets are committed).
    /// </summary>
    private void ConfirmItems(Comanda comanda, IReadOnlyList<ComandaItem> items)
    {
        foreach (var station in items.GroupBy(i => i.Station))
            ConfirmStationBatch(comanda, station.Key, station.ToList());
    }

    private void ConfirmStationBatch(Comanda comanda, string station, IReadOnlyList<ComandaItem> items)
    {
        var ko = new KitchenOrder
        {
            OrderNumber = $"CMD-{comanda.Number}",
            TableNumber = comanda.TableName,
            WaiterName = comanda.WaiterName,
            Station = station,
            Status = KitchenOrderStatus.Pending,
            CreatedAt = UtcNow,
            UpdatedAt = UtcNow
        };
        foreach (var i in items)
        {
            ko.Items.Add(new KitchenOrderItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.WeightKg ?? i.Quantity,
                Notes = i.Notes
            });
            i.Status = ComandaItemStatus.Confirmed;
            i.IsOnHold = false;
            i.ConfirmedAt = UtcNow;
            i.KitchenOrderId = ko.Id;
            i.UpdatedAt = UtcNow;
        }
        _db.KitchenOrders.Add(ko);
        _createdTickets.Add(ko);
        _db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "KitchenOrderCreated",
            AggregateType = "KitchenOrder",
            AggregateId = ko.Id,
            PayloadJson = JsonSerializer.Serialize(new { kitchenOrderId = ko.Id, comandaId = comanda.Id, table = comanda.TableName, items = items.Count }),
            Status = SyncStatus.Pending,
            UpdatedAt = UtcNow
        });
    }

    /// <summary>Simple routing: drinks go to the bar, everything else to the hot kitchen.</summary>
    private static string StationFor(Product p) =>
        p.Category?.Name?.Contains("Bebida", StringComparison.OrdinalIgnoreCase) == true ? "Barra" : "Cocina Caliente";

    private Task<Comanda?> LoadAsync(Guid id, CancellationToken ct) =>
        _db.Comandas.Include(c => c.Items).Include(c => c.Order).FirstOrDefaultAsync(c => c.Id == id, ct);

    private async Task<Comanda> RequireAsync(Guid id, CancellationToken ct) =>
        await LoadAsync(id, ct) ?? throw new InvalidOperationException("Comanda no encontrada.");

    // ── payment lock (shared comandas between registers) ─────────────────────

    /// <summary>Code of THIS register; null when no ITerminalContext is set (single-register install = the master).</summary>
    private string? LocalTerminalCode => _terminal is { IsSet: true } ? _terminal.Code : null;

    private static bool SameCode(string? a, string? b) =>
        a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when another register holds a lock younger than ComandaSettings.PaymentLockMinutes. An older lock is
    /// stale (that register crashed or gave up) and is ignored; a lock without a timestamp is treated as fresh.
    /// </summary>
    private bool IsLockedByOther(Comanda c, string? localCode)
    {
        if (string.IsNullOrWhiteSpace(c.PaymentLockedByTerminalCode) || SameCode(c.PaymentLockedByTerminalCode, localCode))
            return false;
        if (c.PaymentLockedAt == null) return true;
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _settings.PaymentLockMinutes));
        return UtcNow - c.PaymentLockedAt.Value < ttl;
    }

    /// <summary>Throws the user-facing 409 message when another register is collecting this table right now.</summary>
    private void EnsureNotLockedByOther(Comanda c, string? localCode)
    {
        if (IsLockedByOther(c, localCode))
            throw new InvalidOperationException($"La mesa la está cobrando {c.PaymentLockedByTerminalCode}.");
    }

    private static void ClearPaymentLock(Comanda c)
    {
        c.PaymentLockedByTerminalCode = null;
        c.PaymentLockedAt = null;
    }

    private static string NormalizeTerminalCode(string? terminalCode)
    {
        if (string.IsNullOrWhiteSpace(terminalCode))
            throw new ArgumentException("El código de caja es obligatorio.", nameof(terminalCode));
        return TerminalSettings.NormalizeCode(terminalCode);
    }

    private static ComandaDto MapToDto(Comanda c) => new()
    {
        Id = c.Id,
        Number = c.Number,
        DiningTableId = c.DiningTableId,
        TableName = c.TableName,
        WaiterName = c.WaiterName,
        Status = c.Status,
        OpenedAt = c.OpenedAt,
        ClosedAt = c.ClosedAt,
        OrderId = c.OrderId,
        // Paid on another register: the invoice number travels in RemoteOrderNumber (the Order row arrives later via the sync inbox).
        OrderNumber = c.Order?.OrderNumber ?? c.RemoteOrderNumber,
        PaymentLockedByTerminalCode = c.PaymentLockedByTerminalCode,
        PaymentLockedAt = c.PaymentLockedAt,
        PaidByTerminalCode = c.PaidByTerminalCode,
        Items = c.Items.OrderBy(i => i.AddedAt).Select(i => new ComandaItemDto
        {
            Id = i.Id,
            ProductId = i.ProductId,
            ProductName = i.ProductName,
            UnitPrice = i.UnitPrice,
            TaxRate = i.TaxRate,
            Quantity = i.Quantity,
            WeightKg = i.WeightKg,
            Notes = i.Notes,
            Station = i.Station,
            Status = i.Status,
            IsOnHold = i.IsOnHold,
            AddedAt = i.AddedAt,
            ConfirmedAt = i.ConfirmedAt
        }).ToList()
    };
}
