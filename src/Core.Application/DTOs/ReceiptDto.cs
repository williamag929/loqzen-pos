using System;
using System.Collections.Generic;

namespace Core.Application.DTOs;

public class ReceiptDto
{
    public string StoreName { get; set; } = "LOQZEN POS";
    public string StoreAddress { get; set; } = "Local Principal";
    public string StoreTaxId { get; set; } = "NIT 900.000.000-1";
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Now;
    public string CashierName { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string? CustomerDocument { get; set; }

    public List<ReceiptItemDto> Items { get; set; } = new();
    
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal Total { get; set; }
    
    public string PaymentMethodName { get; set; } = "Efectivo";
    public decimal TenderedAmount { get; set; }
    public decimal ChangeAmount { get; set; }
}

public class ReceiptItemDto
{
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
}

public class CartItemDto
{
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; } = 1;
    public bool IsWeighed { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal TaxRate { get; set; } = 0.19m;

    public decimal Subtotal => UnitPrice * (IsWeighed ? (WeightKg ?? Quantity) : Quantity);
    public decimal TaxAmount => Subtotal * TaxRate;
    public decimal Total => Subtotal + TaxAmount;
}
