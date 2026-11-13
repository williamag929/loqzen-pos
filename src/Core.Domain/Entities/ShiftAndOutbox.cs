using System;
using System.Collections.Generic;
using Core.Domain.Common;
using Core.Domain.Enums;

namespace Core.Domain.Entities;

public class CashShift : BaseEntity
{
    public int ShiftNumber { get; set; }
    public Guid CashierId { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    
    public decimal OpeningAmount { get; set; }
    public decimal ClosingAmount { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal Difference => ClosingAmount - ExpectedAmount;
    
    public CashShiftStatus Status { get; set; } = CashShiftStatus.Open;
    public string? Notes { get; set; }

    public Guid? OpenedByUserId { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? ClosedByName { get; set; }

    /// <summary>Cash left in the drawer for the next shift (fondo). Suggested as the next opening amount.</summary>
    public decimal FloatLeftAmount { get; set; }

    /// <summary>Manager approval recorded when the difference exceeded the threshold.</summary>
    public Guid? DifferenceOverrideId { get; set; }

    /// <summary>True when the cashier counted without seeing the expected amount.</summary>
    public bool WasBlindCount { get; set; }

    public ICollection<CashMovement> Movements { get; set; } = new List<CashMovement>();

    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<CashCountLine> CountLines { get; set; } = new List<CashCountLine>();
}

public class OutboxMessage : BaseEntity
{
    public string EventType { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; } = 0;
    public string? LastError { get; set; }
}
