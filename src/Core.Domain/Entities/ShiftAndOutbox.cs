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
