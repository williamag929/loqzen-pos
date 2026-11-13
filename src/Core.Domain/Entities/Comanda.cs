using System;
using System.Collections.Generic;
using Core.Domain.Common;
using Core.Domain.Enums;

namespace Core.Domain.Entities;

/// <summary>Physical table / service point (Mesa 1, Barra, Domicilio...).</summary>
public class DiningTable : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Open tab for a table. Items are added over time; each item waits a configurable
/// delay (Pending / yellow) before it is Confirmed (green) and sent to the KDS.
/// Closing + paying the comanda generates the invoice (Order + Payment + Outbox).
/// </summary>
public class Comanda : BaseEntity
{
    public int Number { get; set; }
    public Guid? DiningTableId { get; set; }
    public DiningTable? DiningTable { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string WaiterName { get; set; } = string.Empty;
    public ComandaStatus Status { get; set; } = ComandaStatus.Open;
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public string? Notes { get; set; }

    /// <summary>Invoice generated when the comanda is paid.</summary>
    public Guid? OrderId { get; set; }
    public Order? Order { get; set; }

    public ICollection<ComandaItem> Items { get; set; } = new List<ComandaItem>();
}

public class ComandaItem : BaseEntity
{
    public Guid ComandaId { get; set; }
    public Comanda? Comanda { get; set; }

    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal? WeightKg { get; set; }
    public string? Notes { get; set; }
    public string Station { get; set; } = "Cocina Caliente";

    public ComandaItemStatus Status { get; set; } = ComandaItemStatus.Pending;

    /// <summary>
    /// True while the cashier is editing this item's notes: auto-confirm skips it so the kitchen
    /// never receives the item WITHOUT the note. Explicit actions (send now / pay) still confirm it.
    /// </summary>
    public bool IsOnHold { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>KDS ticket created when this item was confirmed.</summary>
    public Guid? KitchenOrderId { get; set; }
}

/// <summary>One line of the cash count (arqueo) at shift close: denomination x quantity.</summary>
public class CashCountLine : BaseEntity
{
    public Guid CashShiftId { get; set; }
    public CashShift? CashShift { get; set; }
    public decimal Denomination { get; set; }
    public int Quantity { get; set; }
    public decimal Total => Denomination * Quantity;
}
