using System;
using Core.Domain.Enums;

namespace Core.Application.DTOs;

/// <summary>Bound from appsettings.json section "PaymentTerminals".</summary>
public class PaymentTerminalSettings
{
    /// <summary>Show "Cobrar en datáfono" buttons and the Maestros tab. Off = card payments are typed by hand as before.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds the POS waits for the terminal before giving up (Timeout).</summary>
    public int ChargeTimeoutSeconds { get; set; } = 90;

    /// <summary>Default currency sent to terminals.</summary>
    public string Currency { get; set; } = "COP";

    /// <summary>Mock driver: seconds before it answers (so the UI's progress states are visible).</summary>
    public int MockDelaySeconds { get; set; } = 2;

    /// <summary>Print the terminal voucher lines under the receipt when the terminal returns them.</summary>
    public bool PrintVoucher { get; set; } = true;
}

public class PaymentTerminalDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public PaymentTerminalProvider Provider { get; set; }
    public string? Address { get; set; }
    public string? SerialNumber { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool? LastPingOk { get; set; }

    public string ProviderLabel => PaymentTerminalProviders.Label(Provider);
    public string AssignmentLabel => string.IsNullOrWhiteSpace(AssignedUserName) ? "En caja" : $"Con {AssignedUserName}";
}

public static class PaymentTerminalProviders
{
    public static string Label(PaymentTerminalProvider p) => p switch
    {
        PaymentTerminalProvider.Mock => "Simulado",
        PaymentTerminalProvider.JsonTcp => "Emulador / JSON TCP",
        PaymentTerminalProvider.Redeban => "Redeban",
        PaymentTerminalProvider.Credibanco => "Credibanco",
        PaymentTerminalProvider.Bold => "Bold",
        PaymentTerminalProvider.MercadoPagoPoint => "Mercado Pago Point",
        _ => p.ToString()
    };
}

public sealed record SavePaymentTerminalRequest(
    Guid? Id, string Name, PaymentTerminalProvider Provider, string? Address, string? SerialNumber, bool IsActive, int SortOrder);

/// <summary>Charge request from a screen. Exactly one of OrderId / ComandaId / BillPaymentId may be set (or none for a pre-sale charge).</summary>
public sealed record ChargeOnTerminalRequest(
    Guid TerminalId,
    decimal Amount,
    TerminalPaymentPurpose Purpose,
    string InvoiceReference,
    Guid? RequestedByUserId,
    string RequestedByName,
    Guid? OrderId = null,
    Guid? ComandaId = null,
    Guid? BillPaymentId = null,
    Guid? CashShiftId = null,
    string? Description = null);

public class TerminalPaymentDto
{
    public Guid Id { get; set; }
    public Guid PaymentTerminalId { get; set; }
    public string TerminalName { get; set; } = string.Empty;
    public TerminalPaymentPurpose Purpose { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "COP";
    public string? InvoiceReference { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? ComandaId { get; set; }
    public Guid? BillPaymentId { get; set; }
    public TerminalPaymentStatus Status { get; set; }
    public string? AuthorizationCode { get; set; }
    public string? CardBrand { get; set; }
    public string? CardLast4 { get; set; }
    public string? ProviderReference { get; set; }
    public string? Message { get; set; }
    public string? VoucherText { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public bool IsApproved => Status == TerminalPaymentStatus.Approved;

    /// <summary>"AUTH 123456 · VISA ****4242" (empty unless approved).</summary>
    public string ReferenceText =>
        string.IsNullOrWhiteSpace(AuthorizationCode) ? string.Empty
        : $"AUTH {AuthorizationCode}" + (string.IsNullOrWhiteSpace(CardBrand) ? "" : $" · {CardBrand}") + (string.IsNullOrWhiteSpace(CardLast4) ? "" : $" ****{CardLast4}");

    public string StatusLabel => Status switch
    {
        TerminalPaymentStatus.Pending => "En proceso",
        TerminalPaymentStatus.Approved => "Aprobada",
        TerminalPaymentStatus.Declined => "Rechazada",
        TerminalPaymentStatus.Cancelled => "Cancelada",
        TerminalPaymentStatus.Timeout => "Sin respuesta",
        TerminalPaymentStatus.Error => "Error",
        _ => Status.ToString()
    };
}

/// <summary>Result of "pay this comanda on terminal X" (server / comandera flow).</summary>
public sealed record PayComandaOnTerminalResult(TerminalPaymentDto Charge, PaymentResultDto? Payment)
{
    public bool Success => Charge.IsApproved && Payment is { Success: true };
}
