using System;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Presentation.Desktop.Services;

/// <summary>
/// Result of asking the cashier to charge an amount on a card terminal (datáfono).
/// <list type="bullet">
/// <item><see cref="Charge"/> approved → record the sale with <c>Charge.ReferenceText</c> / <c>Charge.Id</c>.</item>
/// <item><see cref="FallbackToManual"/> → terminals disabled / none configured / cashier chose "Cobrar manual":
/// continue the existing typed-by-hand card path.</item>
/// <item>Neither → the charge was declined, timed out, errored or the cashier cancelled: keep the order open and
/// show <see cref="Message"/>. Nothing was collected.</item>
/// </list>
/// </summary>
public sealed record TerminalChargeOutcome(TerminalPaymentDto? Charge, bool FallbackToManual, string? Message = null)
{
    public bool IsApproved => Charge is { IsApproved: true };
    public static readonly TerminalChargeOutcome Manual = new(null, true);
    public static TerminalChargeOutcome Aborted(string message) => new(null, false, message);
}

/// <summary>
/// UI-side gate for card terminals: one shared modal (same pattern as <see cref="IManagerAuthPrompt"/>).
/// Never throws for business outcomes; every failure path ends in a message plus the manual option so the
/// terminal being down never blocks selling.
/// </summary>
public interface ITerminalChargePrompt
{
    /// <summary>PaymentTerminalSettings.Enabled: screens show "Cobrar en datáfono" only when true.</summary>
    bool IsEnabled { get; }

    Task<TerminalChargeOutcome> RequestAsync(decimal amount, TerminalPaymentPurpose purpose, string invoiceReference,
        Guid? orderId = null, Guid? comandaId = null, Guid? billPaymentId = null, Guid? cashShiftId = null, string? description = null);
}
