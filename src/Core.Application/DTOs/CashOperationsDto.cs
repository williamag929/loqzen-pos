using System;
using System.Collections.Generic;
using System.Linq;
using Core.Domain.Enums;

namespace Core.Application.DTOs;

/// <summary>Bound from appsettings.json section "CashShift".</summary>
public class CashShiftSettings
{
    /// <summary>Cashier counts WITHOUT seeing the expected amount; the difference is revealed only after confirming the count.</summary>
    public bool BlindCount { get; set; } = true;

    /// <summary>|difference| above this (COP) requires manager PIN + a note to close the shift.</summary>
    public decimal MaxDifferenceWithoutManager { get; set; } = 5000m;

    /// <summary>Default cash left in the drawer for the next shift.</summary>
    public decimal DefaultFloat { get; set; } = 200000m;

    /// <summary>Denominations shown in the count, highest first (COP by default).</summary>
    public List<decimal> Bills { get; set; } = new() { 100000m, 50000m, 20000m, 10000m, 5000m, 2000m };
    public List<decimal> Coins { get; set; } = new() { 1000m, 500m, 200m, 100m, 50m };

    /// <summary>Movement types that need a manager PIN (names of CashMovementType). Null = defaults (PaidOut, Expense, SafeDrop, TipPayout).</summary>
    public List<string>? ManagerRequiredFor { get; set; }

    public static readonly IReadOnlyList<CashMovementType> DefaultManagerRequired = new[]
    { CashMovementType.PaidOut, CashMovementType.Expense, CashMovementType.SafeDrop, CashMovementType.TipPayout };

    public bool MovementRequiresManager(CashMovementType t) =>
        ManagerRequiredFor == null
            ? DefaultManagerRequired.Contains(t)
            : ManagerRequiredFor.Any(n => string.Equals(n, t.ToString(), StringComparison.OrdinalIgnoreCase));
}

public static class CashMovementTypes
{
    /// <summary>+1 adds to expected cash, -1 subtracts.</summary>
    public static int Sign(CashMovementType t) => t is CashMovementType.PaidIn or CashMovementType.BillPayment ? 1 : -1;

    public static string Label(CashMovementType t) => t switch
    {
        CashMovementType.PaidIn => "Entrada de efectivo",
        CashMovementType.PaidOut => "Salida / pago a proveedor",
        CashMovementType.Expense => "Gasto",
        CashMovementType.SafeDrop => "Retiro a bóveda",
        CashMovementType.TipPayout => "Propinas entregadas",
        CashMovementType.BillPayment => "Recaudo (efectivo)",
        CashMovementType.BillPaymentVoid => "Anulación de recaudo",
        _ => t.ToString()
    };

    /// <summary>Types a cashier can create by hand (the bill-payment ones are created by IBillPaymentService).</summary>
    public static readonly IReadOnlyList<CashMovementType> Manual = new[]
    { CashMovementType.PaidIn, CashMovementType.PaidOut, CashMovementType.Expense, CashMovementType.SafeDrop, CashMovementType.TipPayout };
}

public class CashMovementDto
{
    public Guid Id { get; set; }
    public CashMovementType Type { get; set; }
    public string TypeLabel => CashMovementTypes.Label(Type);
    public int Sign => CashMovementTypes.Sign(Type);
    public decimal Amount { get; set; }
    public decimal SignedAmount => Sign * Amount;
    public string Reason { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string UserName { get; set; } = string.Empty;
    public Guid? ManagerOverrideId { get; set; }
    public DateTime OccurredAt { get; set; }
}

public sealed record CashMovementRequest(
    CashMovementType Type, decimal Amount, string Reason, string? Reference,
    Guid? UserId, string UserName, Guid? ManagerOverrideId = null);

public class BillerDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public decimal CommissionFixed { get; set; }
    public decimal CommissionPercent { get; set; }
    public string? ColorHex { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Commission for a bill amount: fixed + percent, rounded to whole pesos (away from zero).</summary>
    public decimal CommissionFor(decimal amount) =>
        Math.Round(CommissionFixed + amount * CommissionPercent, 0, MidpointRounding.AwayFromZero);
}

public sealed record BillPaymentRequest(
    Guid BillerId, string AccountReference, decimal Amount, PaymentMethod Method, decimal TenderedAmount,
    Guid? UserId, string UserName, string? CustomerName = null);

public class BillPaymentDto
{
    public Guid Id { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public Guid BillerId { get; set; }
    public string BillerName { get; set; } = string.Empty;
    public string AccountReference { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public decimal Amount { get; set; }
    public decimal Commission { get; set; }
    public decimal Total => Amount + Commission;
    public PaymentMethod Method { get; set; }
    public decimal TenderedAmount { get; set; }
    public decimal ChangeAmount { get; set; }
    public BillPaymentStatus Status { get; set; }
    public string UserName { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
}

/// <summary>
/// Full reconciliation of a shift (open or closed). Expected cash =
/// opening + cash sales + cash bill payments (amount + commission) + paid-ins − paid-outs − expenses − safe drops − tip payouts.
/// Voided recaudos simply drop out of the bill-payment sums; BillPaymentVoidsCash is kept for compatibility and is always 0.
/// </summary>
public class ShiftReconciliationDto
{
    public Guid ShiftId { get; set; }
    public int ShiftNumber { get; set; }
    /// <summary>Register the shift belongs to ("CAJA-02"); null on legacy rows / single-register installs.</summary>
    public string? TerminalCode { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public CashShiftStatus Status { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ClosedByName { get; set; }

    public decimal OpeningAmount { get; set; }

    // Sales by method
    public decimal CashSales { get; set; }
    public decimal CardSales { get; set; }
    public decimal TransferSales { get; set; }
    public decimal OtherSales { get; set; }
    public int OrderCount { get; set; }
    public decimal TotalSales => CashSales + CardSales + TransferSales + OtherSales;

    // Bill payments (recaudos), completed only
    public int BillPaymentCount { get; set; }
    public decimal BillPaymentsCash { get; set; }      // amount + commission paid in cash
    public decimal BillPaymentsCard { get; set; }
    public decimal BillPaymentsTransfer { get; set; }
    public decimal BillPaymentCommissions { get; set; } // store income, all methods
    public decimal BillPaymentsToRemit { get; set; }     // amounts owed to billers, all methods

    // Movements by type (positive amounts)
    public decimal PaidIn { get; set; }
    public decimal PaidOut { get; set; }
    public decimal Expenses { get; set; }
    public decimal SafeDrops { get; set; }
    public decimal TipPayouts { get; set; }
    public decimal BillPaymentVoidsCash { get; set; }
    public List<CashMovementDto> Movements { get; set; } = new();

    public decimal ExpectedCash =>
        OpeningAmount + CashSales + BillPaymentsCash + PaidIn - PaidOut - Expenses - SafeDrops - TipPayouts - BillPaymentVoidsCash;

    /// <summary>Expected non-cash totals to compare with the card terminal / bank report.</summary>
    public decimal ExpectedCard => CardSales + BillPaymentsCard;
    public decimal ExpectedTransfer => TransferSales + BillPaymentsTransfer;

    // Filled for closed shifts
    public decimal? CountedCash { get; set; }
    public decimal? Difference => CountedCash is { } c ? c - ExpectedCash : null;
    public decimal FloatLeftAmount { get; set; }
    public bool WasBlindCount { get; set; }
    public string? DifferenceApprovedBy { get; set; }
    public string? Notes { get; set; }
    public List<CashCountLineDto> CountLines { get; set; } = new();
    public List<PartialCountDto> PartialCounts { get; set; } = new();
}

public class PartialCountDto
{
    public Guid Id { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal CountedAmount { get; set; }
    public decimal Difference => CountedAmount - ExpectedAmount;
    public string UserName { get; set; } = string.Empty;
    public DateTime CountedAt { get; set; }
}

public sealed record CloseShiftRequest(
    IReadOnlyList<CashCountLineDto> CountLines,
    decimal FloatLeftAmount,
    string? Notes,
    Guid? ClosedByUserId,
    string ClosedByName,
    bool WasBlindCount,
    Guid? DifferenceOverrideId = null);

/// <summary>Row of the shift history list.</summary>
public class ShiftHistoryItemDto
{
    public Guid ShiftId { get; set; }
    public int ShiftNumber { get; set; }
    /// <summary>Register the shift belongs to ("CAJA-02"); null on legacy rows / single-register installs.</summary>
    public string? TerminalCode { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal ClosingAmount { get; set; }
    public decimal Difference => ClosingAmount - ExpectedAmount;
    public bool HadManagerApproval { get; set; }
}

/// <summary>Thrown by CloseShiftAsync when |difference| exceeds CashShiftSettings.MaxDifferenceWithoutManager and no override was given.</summary>
public sealed class CashDifferenceRequiresApprovalException : InvalidOperationException
{
    public decimal Difference { get; }
    public decimal Threshold { get; }
    public CashDifferenceRequiresApprovalException(decimal difference, decimal threshold)
        : base($"La diferencia de {difference:N0} supera el máximo permitido ({threshold:N0}). Se requiere autorización de gerente y una nota.")
    { Difference = difference; Threshold = threshold; }
}
