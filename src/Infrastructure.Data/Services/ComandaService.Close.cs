using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public partial class ComandaService
{
    public async Task<ComandaDto> MarkReadyToPayAsync(Guid comandaId, CancellationToken ct = default)
    {
        var comanda = await RequireAsync(comandaId, ct);
        if (comanda.Status != ComandaStatus.Open)
            throw new InvalidOperationException("La comanda no está abierta.");
        if (!comanda.Items.Any(i => i.Status != ComandaItemStatus.Cancelled))
            throw new InvalidOperationException("La comanda no tiene productos.");

        // Anything still waiting goes to the kitchen now — it is being billed.
        ConfirmItems(comanda, comanda.Items.Where(i => i.Status == ComandaItemStatus.Pending).ToList());
        comanda.Status = ComandaStatus.ReadyToPay;
        comanda.UpdatedAt = UtcNow;
        await SaveAndPublishTicketsAsync(ct);
        return MapToDto(comanda);
    }

    public async Task<ComandaDto> ReopenAsync(Guid comandaId, CancellationToken ct = default)
    {
        var comanda = await RequireAsync(comandaId, ct);
        if (comanda.Status != ComandaStatus.ReadyToPay)
            throw new InvalidOperationException("Solo se puede reabrir una comanda lista para pago.");
        comanda.Status = ComandaStatus.Open;
        comanda.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return MapToDto(comanda);
    }

    public async Task<PaymentResultDto> CloseAndPayAsync(Guid comandaId, Guid cashShiftId, PaymentMethod method, decimal tenderedAmount, CancellationToken ct = default)
    {
        var comanda = await RequireAsync(comandaId, ct);
        if (comanda.Status is ComandaStatus.Closed or ComandaStatus.Cancelled)
            return PaymentResultDto.Failed(Guid.Empty, "La comanda ya está cerrada.");

        var shiftNumber = await _db.CashShifts
            .Where(s => s.Id == cashShiftId && s.Status == CashShiftStatus.Open)
            .Select(s => (int?)s.ShiftNumber)
            .FirstOrDefaultAsync(ct);
        if (shiftNumber == null)
            return PaymentResultDto.Failed(Guid.Empty, "No hay turno de caja abierto.");

        var billable = comanda.Items.Where(i => i.Status != ComandaItemStatus.Cancelled).ToList();
        if (billable.Count == 0)
            return PaymentResultDto.Failed(Guid.Empty, "La comanda no tiene productos.");

        var order = BuildInvoice(comanda, billable, cashShiftId);

        // Validate payment BEFORE mutating anything tracked, so a failure leaves no partial state.
        if (method == PaymentMethod.Cash && tenderedAmount < order.Total)
            return PaymentResultDto.Failed(Guid.Empty, $"Monto recibido insuficiente. Total: {order.Total:N0}");

        // Kitchen must receive everything that is billed.
        ConfirmItems(comanda, billable.Where(i => i.Status == ComandaItemStatus.Pending).ToList());

        var tendered = method == PaymentMethod.Cash ? tenderedAmount : order.Total;
        var change = tendered - order.Total;
        order.Payments.Add(new Payment
        {
            Method = method,
            Amount = order.Total,
            TenderedAmount = tendered,
            ChangeAmount = change,
            ProcessedAt = UtcNow
        });

        _db.Orders.Add(order);
        comanda.OrderId = order.Id;
        comanda.Status = ComandaStatus.Closed;
        comanda.ClosedAt = UtcNow;
        comanda.UpdatedAt = UtcNow;

        _db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = SyncEventTypes.OrderCompleted,
            AggregateType = "Order",
            AggregateId = order.Id,
            PayloadJson = OutboxPayloads.OrderCompleted(order, shiftNumber, UtcNow,
                new { comandaId = comanda.Id, comandaNumber = comanda.Number, table = comanda.TableName }),
            Status = SyncStatus.Pending,
            UpdatedAt = UtcNow
        });

        await SaveAndPublishTicketsAsync(ct);
        return PaymentResultDto.Successful(order.Id, order.OrderNumber, change);
    }

    private Order BuildInvoice(Comanda comanda, System.Collections.Generic.IEnumerable<ComandaItem> billable, Guid cashShiftId)
    {
        var order = new Order
        {
            OrderNumber = $"FAC-{UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}",
            Status = OrderStatus.Completed,
            CashShiftId = cashShiftId,
            Notes = $"Comanda #{comanda.Number} - {comanda.TableName}",
            TerminalId = _terminal is { IsSet: true } ? _terminal.Id : null,
            TerminalCode = _terminal is { IsSet: true } ? _terminal.Code : null,
            CreatedAt = UtcNow,
            UpdatedAt = UtcNow
        };
        foreach (var i in billable)
        {
            var qty = i.WeightKg ?? i.Quantity;
            var subtotal = Math.Round(i.UnitPrice * qty, 2);
            var tax = Math.Round(subtotal * i.TaxRate, 2);
            order.Items.Add(new OrderItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                UnitPrice = i.UnitPrice,
                Quantity = i.Quantity,
                WeightKg = i.WeightKg,
                TaxRate = i.TaxRate,
                Subtotal = subtotal,
                TaxAmount = tax,
                Total = subtotal + tax,
                UpdatedAt = UtcNow
            });
        }
        order.Subtotal = order.Items.Sum(x => x.Subtotal);
        order.TaxTotal = order.Items.Sum(x => x.TaxAmount);
        order.Total = order.Subtotal + order.TaxTotal;
        return order;
    }
}
