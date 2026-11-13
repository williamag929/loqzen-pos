using System;
using System.Collections.Generic;
using Core.Domain.Common;
using Core.Domain.Enums;

namespace Core.Domain.Entities;

public class KitchenOrder : BaseEntity
{
    public string OrderNumber { get; set; } = string.Empty;
    public Guid? OrderId { get; set; }
    public string TableNumber { get; set; } = "Mesa 1";
    public Guid? WaiterId { get; set; }
    public string WaiterName { get; set; } = "Mesero Principal";
    public KitchenOrderStatus Status { get; set; } = KitchenOrderStatus.Pending;
    public string Station { get; set; } = "Cocina Caliente";
    public string? GeneralNotes { get; set; }

    /// <summary>First time the kitchen ticket was printed (null = never → print button shows green).</summary>
    public DateTime? PrintedAt { get; set; }

    /// <summary>When it was marked delivered/archived. The KDS keeps the card visible for a short undo window.</summary>
    public DateTime? DeliveredAt { get; set; }
    
    public ICollection<KitchenOrderItem> Items { get; set; } = new List<KitchenOrderItem>();
}

public class KitchenOrderItem : BaseEntity
{
    public Guid KitchenOrderId { get; set; }
    public KitchenOrder? KitchenOrder { get; set; }

    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public string? Notes { get; set; }
    public bool IsPrepared { get; set; } = false;
}
