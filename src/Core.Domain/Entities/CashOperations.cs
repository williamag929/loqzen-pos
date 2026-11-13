using System;
using Core.Domain.Common;
using Core.Domain.Enums;

namespace Core.Domain.Entities;

/// <summary>
/// Cash that enters or leaves the drawer WITHOUT being a sale: paid-in, paid-out, expense, drop to safe,
/// tip payout, or the cash side of a bill payment (recaudo). Every movement changes the expected cash of
/// the shift, so the close reconciles to the peso. Amount is always positive; direction comes from Type.
/// </summary>
public class CashMovement : BaseEntity
{
    public Guid CashShiftId { get; set; }
    public CashShift? CashShift { get; set; }

    public CashMovementType Type { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Reference { get; set; }

    public Guid? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;

    /// <summary>Manager approval when the movement type requires it (see Authorization settings).</summary>
    public Guid? ManagerOverrideId { get; set; }

    /// <summary>Set when the movement was created by a bill payment (cash).</summary>
    public Guid? BillPaymentId { get; set; }

    public DateTime OccurredAt { get; set; }
}

/// <summary>Company whose bills the store collects (energía, agua, gas, recargas, convenios).</summary>
public class Biller : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    /// <summary>Fixed commission charged to the customer per payment (COP).</summary>
    public decimal CommissionFixed { get; set; }
    /// <summary>Percent commission (0.01 = 1 %), added on top of the fixed part.</summary>
    public decimal CommissionPercent { get; set; }
    public string? ColorHex { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A bill paid at the counter. Not a sale: it has its own receipt number and reconciles separately.</summary>
public class BillPayment : BaseEntity
{
    public Guid CashShiftId { get; set; }
    /// <summary>Register that collected it (null = legacy row, owned by the master).</summary>
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid BillerId { get; set; }
    public string BillerName { get; set; } = string.Empty;

    /// <summary>Contract / account / invoice number printed on the customer's bill.</summary>
    public string AccountReference { get; set; } = string.Empty;
    public string? CustomerName { get; set; }

    /// <summary>Amount of the bill (goes to the biller).</summary>
    public decimal Amount { get; set; }
    /// <summary>Commission kept by the store.</summary>
    public decimal Commission { get; set; }
    public decimal Total => Amount + Commission;

    public PaymentMethod Method { get; set; }
    public decimal TenderedAmount { get; set; }
    public decimal ChangeAmount { get; set; }

    /// <summary>Sequential, e.g. "REC-000123".</summary>
    public string ReceiptNumber { get; set; } = string.Empty;

    public BillPaymentStatus Status { get; set; } = BillPaymentStatus.Completed;
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }

    public DateTime? VoidedAt { get; set; }
    public Guid? VoidOverrideId { get; set; }
    public string? VoidReason { get; set; }
}

/// <summary>Mid-shift count (arqueo parcial). Does not close the shift; records expected vs counted at that moment.</summary>
public class PartialCashCount : BaseEntity
{
    public Guid CashShiftId { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal CountedAmount { get; set; }
    public decimal Difference => CountedAmount - ExpectedAmount;
    /// <summary>JSON array of { denomination, quantity }.</summary>
    public string LinesJson { get; set; } = "[]";
    public string UserName { get; set; } = string.Empty;
    public DateTime CountedAt { get; set; }
}
