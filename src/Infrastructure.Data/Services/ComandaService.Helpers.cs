using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public partial class ComandaService
{
    /// <summary>
    /// Marks items Confirmed and creates ONE KitchenOrder per station (+ Outbox "KitchenOrderCreated"),
    /// so drinks go to the bar screen and food to the kitchen screen.
    /// Caller is responsible for SaveChanges so everything commits atomically.
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
        OrderNumber = c.Order?.OrderNumber,
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
