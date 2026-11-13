using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

/// <summary>
/// Sales service implementation for checkout operations.
/// Handles order lifecycle, totals calculation, and payment processing with Outbox pattern.
/// </summary>
public class SalesService : ISalesService
{
    private readonly PosDbContext _dbContext;
    private readonly ITerminalContext? _terminal;

    /// <param name="terminal">Optional: when set, new orders are stamped with this register's id/code.</param>
    public SalesService(PosDbContext dbContext, ITerminalContext? terminal = null)
    {
        _dbContext = dbContext;
        _terminal = terminal;
    }

    private void StampTerminal(Order order)
    {
        if (_terminal is not { IsSet: true } || order.TerminalId != null) return;
        order.TerminalId = _terminal.Id;
        order.TerminalCode = _terminal.Code;
    }

    public async Task<OrderDto> CreateOrderAsync(Guid? customerId, Guid cashShiftId, CancellationToken ct = default)
    {
        var orderNumber = $"ORD-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}";

        var order = new Order
        {
            OrderNumber = orderNumber,
            Status = OrderStatus.Draft,
            CustomerId = customerId,
            CashShiftId = cashShiftId,
            UpdatedAt = DateTime.UtcNow
        };
        StampTerminal(order);

        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync(ct);
        return MapToDto(order);
    }

    public async Task<OrderDto> AddItemAsync(Guid orderId, Guid productId, decimal quantity, decimal? weightKg = null, CancellationToken ct = default)
    {
        var order = await GetOrderWithItemsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found.");

        var product = await _dbContext.Products.Include(p => p.Tax)
            .FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw new InvalidOperationException($"Product {productId} not found.");

        var existingItem = order.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existingItem != null && !product.IsWeighed)
        {
            existingItem.Quantity += quantity;
            RecalculateItemTotals(existingItem, product);
        }
        else
        {
            var taxRate = product.Tax?.Rate ?? 0m;
            var effectiveQty = product.IsWeighed ? (weightKg ?? quantity) : quantity;
            var subtotal = product.Price * effectiveQty;
            var taxAmount = subtotal * taxRate;

            var newItem = new OrderItem
            {
                OrderId = orderId,
                ProductId = productId,
                ProductName = product.Name,
                UnitPrice = product.Price,
                Quantity = quantity,
                WeightKg = product.IsWeighed ? weightKg : null,
                TaxRate = taxRate,
                TaxAmount = taxAmount,
                Subtotal = subtotal,
                Total = subtotal + taxAmount,
                UpdatedAt = DateTime.UtcNow
            };
            order.Items.Add(newItem);
            _dbContext.OrderItems.Add(newItem);
        }

        RecalculateOrderTotals(order);
        order.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
        return MapToDto(order);
    }

    public async Task<OrderDto> RemoveItemAsync(Guid orderId, Guid itemId, CancellationToken ct = default)
    {
        var order = await GetOrderWithItemsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Order item {itemId} not found.");

        order.Items.Remove(item);
        _dbContext.OrderItems.Remove(item);
        RecalculateOrderTotals(order);
        order.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);
        return MapToDto(order);
    }

    public async Task<OrderDto> UpdateItemQuantityAsync(Guid orderId, Guid itemId, decimal newQuantity, CancellationToken ct = default)
    {
        if (newQuantity <= 0)
            throw new ArgumentException("Quantity must be greater than zero.", nameof(newQuantity));

        var order = await GetOrderWithItemsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Order item {itemId} not found.");

        var product = await _dbContext.Products.Include(p => p.Tax)
            .FirstOrDefaultAsync(p => p.Id == item.ProductId, ct);

        item.Quantity = newQuantity;
        if (product != null)
            RecalculateItemTotals(item, product);
        else
        {
            var effectiveQty = item.WeightKg ?? newQuantity;
            item.Subtotal = item.UnitPrice * effectiveQty;
            item.TaxAmount = item.Subtotal * item.TaxRate;
            item.Total = item.Subtotal + item.TaxAmount;
        }
        item.UpdatedAt = DateTime.UtcNow;

        RecalculateOrderTotals(order);
        order.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
        return MapToDto(order);
    }

    public async Task<OrderDto> ApplyDiscountAsync(Guid orderId, decimal discountAmount, CancellationToken ct = default)
    {
        if (discountAmount < 0)
            throw new ArgumentException("Discount amount cannot be negative.", nameof(discountAmount));

        var order = await GetOrderWithItemsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found.");

        order.DiscountTotal = discountAmount;
        RecalculateOrderTotals(order);
        order.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);
        return MapToDto(order);
    }

    public async Task<OrderDto> ApplyDiscountAsync(Guid orderId, DiscountDto discount, CancellationToken ct = default)
    {
        var order = await GetOrderWithItemsAsync(orderId, ct)
            ?? throw new InvalidOperationException($"Order {orderId} not found.");

        var discountAmount = discount.CalculateDiscount(order.Subtotal);
        
        // Ensure discount doesn't exceed subtotal
        order.DiscountTotal = Math.Min(discountAmount, order.Subtotal);
        RecalculateOrderTotals(order);
        order.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(ct);
        return MapToDto(order);
    }

    public async Task<PaymentResultDto> ProcessPaymentAsync(
        Guid orderId, PaymentMethod method, decimal amount, decimal tenderedAmount, CancellationToken ct = default)
    {
        var order = await GetOrderWithItemsAsync(orderId, ct);
        if (order == null)
            return PaymentResultDto.Failed(orderId, $"Order {orderId} not found.");

        if (order.Status != OrderStatus.Draft && order.Status != OrderStatus.Pending)
            return PaymentResultDto.Failed(orderId, $"Order is in status {order.Status} and cannot be paid.");

        if (order.Items.Count == 0)
            return PaymentResultDto.Failed(orderId, "Cannot process payment for an empty order.");

        var changeAmount = 0m;
        if (method == PaymentMethod.Cash)
        {
            if (tenderedAmount < order.Total)
                return PaymentResultDto.Failed(orderId, $"Tendered amount is less than order total.");
            changeAmount = tenderedAmount - order.Total;
        }

        var payment = new Payment
        {
            OrderId = orderId,
            Method = method,
            Amount = order.Total,
            TenderedAmount = tenderedAmount,
            ChangeAmount = changeAmount,
            ProcessedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        order.Status = OrderStatus.Completed;
        order.UpdatedAt = DateTime.UtcNow;
        StampTerminal(order);
        order.Payments.Add(payment);

        var shiftNumber = order.CashShiftId is { } shiftId
            ? await _dbContext.CashShifts.Where(s => s.Id == shiftId).Select(s => (int?)s.ShiftNumber).FirstOrDefaultAsync(ct)
            : null;

        // Outbox pattern: write sync event in same transaction (contract shape, see SyncEventTypes)
        var outboxMessage = new OutboxMessage
        {
            EventType = SyncEventTypes.OrderCompleted,
            AggregateType = "Order",
            AggregateId = order.Id,
            PayloadJson = OutboxPayloads.OrderCompleted(order, shiftNumber, DateTime.UtcNow),
            Status = SyncStatus.Pending,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Payments.Add(payment);
        _dbContext.OutboxMessages.Add(outboxMessage);
        await _dbContext.SaveChangesAsync(ct);

        return PaymentResultDto.Successful(order.Id, order.OrderNumber, changeAmount);
    }

    public async Task<OrderDto?> GetCurrentOrderAsync(Guid cashShiftId, CancellationToken ct = default)
    {
        var order = await _dbContext.Orders.Include(o => o.Items)
            .Where(o => o.CashShiftId == cashShiftId && o.Status == OrderStatus.Draft)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return order != null ? MapToDto(order) : null;
    }

    public async Task<OrderDto?> GetByIdAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await GetOrderWithItemsAsync(orderId, ct);
        return order != null ? MapToDto(order) : null;
    }

    #region Private Helpers

    private async Task<Order?> GetOrderWithItemsAsync(Guid orderId, CancellationToken ct) =>
        await _dbContext.Orders.Include(o => o.Items).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == orderId, ct);

    private static void RecalculateItemTotals(OrderItem item, Product product)
    {
        var taxRate = product.Tax?.Rate ?? item.TaxRate;
        var effectiveQty = product.IsWeighed ? (item.WeightKg ?? item.Quantity) : item.Quantity;
        item.Subtotal = product.Price * effectiveQty;
        item.TaxRate = taxRate;
        item.TaxAmount = item.Subtotal * taxRate;
        item.Total = item.Subtotal + item.TaxAmount;
        item.UpdatedAt = DateTime.UtcNow;
    }

    private static void RecalculateOrderTotals(Order order)
    {
        order.Subtotal = order.Items.Sum(i => i.Subtotal);
        order.TaxTotal = order.Items.Sum(i => i.TaxAmount);
        order.Total = order.Subtotal + order.TaxTotal - order.DiscountTotal;
        if (order.Total < 0) order.Total = 0;
    }

    private static OrderDto MapToDto(Order order) => new()
    {
        Id = order.Id,
        OrderNumber = order.OrderNumber,
        Status = order.Status,
        CustomerId = order.CustomerId,
        CashShiftId = order.CashShiftId,
        Items = order.Items.Select(MapItemToDto).ToList(),
        Subtotal = order.Subtotal,
        TaxTotal = order.TaxTotal,
        DiscountTotal = order.DiscountTotal,
        Total = order.Total,
        Notes = order.Notes,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt
    };

    private static OrderItemDto MapItemToDto(OrderItem item) => new()
    {
        Id = item.Id,
        ProductId = item.ProductId,
        ProductName = item.ProductName,
        UnitPrice = item.UnitPrice,
        Quantity = item.Quantity,
        WeightKg = item.WeightKg,
        TaxRate = item.TaxRate,
        TaxAmount = item.TaxAmount,
        Subtotal = item.Subtotal,
        Total = item.Total
    };

    #endregion
}
