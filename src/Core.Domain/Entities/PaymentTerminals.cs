using System;
using Core.Domain.Common;
using Core.Domain.Enums;

namespace Core.Domain.Entities;

/// <summary>
/// A card terminal (datáfono) the POS can send amounts to. Wireless terminals can be assigned to a waiter so the
/// comandera offers "Cobrar en mesa" on that terminal. Address format depends on the provider
/// (JsonTcp: "host:port"; Mock: ignored).
/// </summary>
public class PaymentTerminal : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public PaymentTerminalProvider Provider { get; set; } = PaymentTerminalProvider.Mock;
    public string? Address { get; set; }
    public string? SerialNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>Waiter currently carrying this terminal (null = at the register / unassigned).</summary>
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }

    public DateTime? LastSeenAt { get; set; }
    public bool? LastPingOk { get; set; }
}

/// <summary>
/// Audit row for every charge sent to a terminal, written BEFORE the driver is called (Pending) and updated with the
/// answer, so a crash mid-charge leaves a trace to reconcile against the terminal's own batch report.
/// </summary>
public class TerminalPayment : BaseEntity
{
    public Guid PaymentTerminalId { get; set; }
    public PaymentTerminal? PaymentTerminal { get; set; }
    public string TerminalName { get; set; } = string.Empty;

    public TerminalPaymentPurpose Purpose { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "COP";

    /// <summary>What the POS sent as its reference (order number, comanda number, bill receipt number).</summary>
    public string? InvoiceReference { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? ComandaId { get; set; }
    public Guid? BillPaymentId { get; set; }
    public Guid? CashShiftId { get; set; }

    public TerminalPaymentStatus Status { get; set; } = TerminalPaymentStatus.Pending;
    public string? AuthorizationCode { get; set; }
    public string? CardBrand { get; set; }
    public string? CardLast4 { get; set; }
    /// <summary>Provider-side transaction id / RRN.</summary>
    public string? ProviderReference { get; set; }
    public string? Message { get; set; }
    /// <summary>Voucher text returned by the terminal, one line per entry, joined with '\n' (printed under the receipt).</summary>
    public string? VoucherText { get; set; }

    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>"AUTH 123456 · VISA ****4242" — what goes into Payment.Reference / BillPayment.PaymentReference.</summary>
    public string ReferenceText =>
        string.IsNullOrWhiteSpace(AuthorizationCode) ? string.Empty
        : $"AUTH {AuthorizationCode}" + (string.IsNullOrWhiteSpace(CardBrand) ? "" : $" · {CardBrand}") + (string.IsNullOrWhiteSpace(CardLast4) ? "" : $" ****{CardLast4}");
}
