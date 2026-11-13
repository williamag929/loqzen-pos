using System;
using System.Collections.Generic;
using Core.Domain.Enums;

namespace Core.Application.DTOs;

/// <summary>
/// Data Transfer Object representing an order.
/// </summary>
public class OrderDto
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? CashShiftId { get; set; }
    public List<OrderItemDto> Items { get; set; } = new();
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Data Transfer Object representing an order item.
/// </summary>
public class OrderItemDto
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Total { get; set; }
}

/// <summary>
/// Result of a payment processing operation.
/// </summary>
public class PaymentResultDto
{
    public bool Success { get; set; }
    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public decimal ChangeAmount { get; set; }
    public string? ErrorMessage { get; set; }

    public static PaymentResultDto Successful(Guid orderId, string orderNumber, decimal changeAmount) => new()
    {
        Success = true,
        OrderId = orderId,
        OrderNumber = orderNumber,
        ChangeAmount = changeAmount
    };

    public static PaymentResultDto Failed(Guid orderId, string errorMessage) => new()
    {
        Success = false,
        OrderId = orderId,
        ErrorMessage = errorMessage
    };
}

/// <summary>
/// Discount type enumeration
/// </summary>
public enum DiscountType
{
    Percentage,  // e.g. 10% off
    FixedAmount  // e.g. $5000 off
}

/// <summary>
/// Discount information for an order or line item
/// </summary>
public class DiscountDto
{
    public DiscountType Type { get; set; }
    public decimal Value { get; set; }
    public string? Reason { get; set; }
    public string? AuthorizedBy { get; set; }
    
    public decimal CalculateDiscount(decimal subtotal)
    {
        return Type switch
        {
            DiscountType.Percentage => subtotal * (Value / 100m),
            DiscountType.FixedAmount => Math.Min(Value, subtotal), // Can't discount more than subtotal
            _ => 0m
        };
    }
}
