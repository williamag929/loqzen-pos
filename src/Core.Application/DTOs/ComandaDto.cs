using System;
using System.Collections.Generic;
using System.Linq;
using Core.Domain.Enums;

namespace Core.Application.DTOs;

public class DiningTableDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Open comanda on this table, if any.</summary>
    public Guid? OpenComandaId { get; set; }
}

public class ComandaDto
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public Guid? DiningTableId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string WaiterName { get; set; } = string.Empty;
    public ComandaStatus Status { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public List<ComandaItemDto> Items { get; set; } = new();

    /// <summary>Billable lines = everything not cancelled.</summary>
    public IEnumerable<ComandaItemDto> BillableItems => Items.Where(i => i.Status != ComandaItemStatus.Cancelled);
    public decimal Subtotal => BillableItems.Sum(i => i.Subtotal);
    public decimal TaxTotal => BillableItems.Sum(i => i.TaxAmount);
    public decimal Total => Subtotal + TaxTotal;
    public int PendingCount => Items.Count(i => i.Status == ComandaItemStatus.Pending);
}

public class ComandaItemDto
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal Quantity { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Notes { get; set; }
    public string Station { get; set; } = string.Empty;
    public ComandaItemStatus Status { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    public decimal EffectiveQuantity => WeightKg ?? Quantity;
    public decimal Subtotal => UnitPrice * EffectiveQuantity;
    public decimal TaxAmount => Subtotal * TaxRate;
    public decimal Total => Subtotal + TaxAmount;
}

/// <summary>Bound from appsettings.json section "Comanda".</summary>
public class ComandaSettings
{
    /// <summary>Seconds an added item stays Pending (yellow) before auto-confirm to KDS (green).</summary>
    public int ConfirmationDelaySeconds { get; set; } = 30;
}

/// <summary>Cash close summary (cuadre de caja).</summary>
public class CashCloseSummaryDto
{
    public Guid ShiftId { get; set; }
    public int ShiftNumber { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public decimal OpeningAmount { get; set; }
    public decimal CashSales { get; set; }
    public decimal CardSales { get; set; }
    public decimal TransferSales { get; set; }
    public decimal OtherSales { get; set; }
    public int OrderCount { get; set; }
    /// <summary>Opening + cash sales.</summary>
    public decimal ExpectedCash => OpeningAmount + CashSales;
    public decimal TotalSales => CashSales + CardSales + TransferSales + OtherSales;
}

public class CashCountLineDto
{
    public decimal Denomination { get; set; }
    public int Quantity { get; set; }
    public decimal Total => Denomination * Quantity;
}
