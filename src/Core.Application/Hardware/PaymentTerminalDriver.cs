using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Domain.Enums;

namespace Core.Application.Hardware;

/// <summary>What the POS sends to a terminal. Amount in whole currency units (COP has no cents in practice).</summary>
public sealed record TerminalChargeRequest(
    string? Address,
    decimal Amount,
    string Currency,
    /// <summary>Order / comanda / receipt number shown on the terminal and echoed back.</summary>
    string InvoiceReference,
    string? Description = null);

/// <summary>Stages reported while a charge is in flight (shown to the cashier: "Acerque o inserte la tarjeta…").</summary>
public enum TerminalChargeStage { Connecting, WaitingForCard, Processing, Done }

public sealed record TerminalChargeProgress(TerminalChargeStage Stage, string Message);

/// <summary>Answer of one charge. For Approved, AuthorizationCode is required; CardBrand/CardLast4/ProviderReference when known.</summary>
public sealed record TerminalChargeResult(
    TerminalPaymentStatus Status,
    string? AuthorizationCode = null,
    string? CardBrand = null,
    string? CardLast4 = null,
    string? ProviderReference = null,
    string? Message = null,
    IReadOnlyList<string>? VoucherLines = null)
{
    public bool IsApproved => Status == TerminalPaymentStatus.Approved;
    public static TerminalChargeResult Declined(string? message = null) => new(TerminalPaymentStatus.Declined, Message: message ?? "Rechazada por el emisor.");
    public static TerminalChargeResult Cancelled() => new(TerminalPaymentStatus.Cancelled, Message: "Cancelada desde el POS.");
    public static TerminalChargeResult Timeout() => new(TerminalPaymentStatus.Timeout, Message: "El datáfono no respondió a tiempo.");
    public static TerminalChargeResult Error(string message) => new(TerminalPaymentStatus.Error, Message: message);
}

/// <summary>
/// One driver per <see cref="PaymentTerminalProvider"/> (same strategy as printers: real driver + Mock + Docker
/// emulator). Drivers are stateless singletons; the address of the specific terminal travels in the request.
/// A driver NEVER throws for business outcomes (declined, timeout, cancelled): it returns the status. It may throw
/// only for programming errors (null request). Cancellation via <paramref name="ct"/> must return
/// <see cref="TerminalChargeResult.Cancelled"/> (and tell the terminal to abort when the protocol allows it).
/// </summary>
public interface IPaymentTerminalDriver
{
    PaymentTerminalProvider Provider { get; }

    /// <summary>Sends the amount and waits for the terminal's answer (card tap/insert happens on the device).</summary>
    Task<TerminalChargeResult> ChargeAsync(TerminalChargeRequest request, IProgress<TerminalChargeProgress>? progress, CancellationToken ct);

    /// <summary>Quick reachability test for "Probar conexión" (true/false, never throws).</summary>
    Task<bool> PingAsync(string? address, CancellationToken ct = default);
}

/// <summary>Resolves the driver for a provider (registered in DI with every available driver).</summary>
public interface IPaymentTerminalDriverFactory
{
    IPaymentTerminalDriver GetDriver(PaymentTerminalProvider provider);
    IReadOnlyList<PaymentTerminalProvider> AvailableProviders { get; }
}
