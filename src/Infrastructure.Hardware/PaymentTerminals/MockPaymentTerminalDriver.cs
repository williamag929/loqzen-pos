using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Enums;

namespace Infrastructure.Hardware.PaymentTerminals;

/// <summary>
/// In-process simulator (no device). Walks through Connecting → WaitingForCard → Processing → Done over
/// <see cref="PaymentTerminalSettings.MockDelaySeconds"/> so the cashier UI can show its progress states, then answers
/// by the last two digits of the amount (whole currency units):
/// <list type="bullet">
///   <item>…99 → Declined ("Fondos insuficientes (simulado)")</item>
///   <item>…98 → hung terminal: never answers. Returns Cancelled when the caller's token fires, otherwise Timeout after
///         <see cref="PaymentTerminalSettings.ChargeTimeoutSeconds"/>.</item>
///   <item>…97 → Error ("Error de comunicación (simulado)")</item>
///   <item>anything else → Approved with a random 6-digit auth code, a card brand cycling VISA/MASTERCARD/AMEX, random
///         last-4, reference "MOCK-&lt;ticks&gt;" and a 32-column voucher.</item>
/// </list>
/// All waits go through the optional <see cref="TimeProvider"/> so tests can use a fake clock.
/// </summary>
public class MockPaymentTerminalDriver : IPaymentTerminalDriver
{
    private static readonly string[] Brands = { "VISA", "MASTERCARD", "AMEX" };
    private static int _brandCounter = -1;

    private readonly PaymentTerminalSettings _settings;
    private readonly TimeProvider _clock;

    public MockPaymentTerminalDriver(PaymentTerminalSettings settings, TimeProvider? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? TimeProvider.System;
    }

    public PaymentTerminalProvider Provider => PaymentTerminalProvider.Mock;

    public async Task<TerminalChargeResult> ChargeAsync(TerminalChargeRequest request, IProgress<TerminalChargeProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var total = TimeSpan.FromSeconds(Math.Max(0, _settings.MockDelaySeconds));
        var lastTwo = LastTwoDigits(request.Amount);

        try
        {
            progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Connecting, "Conectando con el datáfono simulado…"));
            await Task.Delay(total / 4, _clock, ct);

            progress?.Report(new TerminalChargeProgress(TerminalChargeStage.WaitingForCard, "Acerque o inserte la tarjeta en el datáfono…"));

            if (lastTwo == 98)
            {
                // Hung terminal: nobody taps a card and the device never answers. Wait for the caller to give up
                // (Cancelled) or for the configured charge timeout to elapse (Timeout).
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.ChargeTimeoutSeconds)), _clock, ct);
                progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Done, "El datáfono no respondió."));
                return TerminalChargeResult.Timeout();
            }

            await Task.Delay(total / 2, _clock, ct);

            progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Processing, "Procesando la transacción…"));
            await Task.Delay(total / 4, _clock, ct);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Done, "Transacción cancelada."));
            return TerminalChargeResult.Cancelled();
        }

        TerminalChargeResult result = lastTwo switch
        {
            99 => TerminalChargeResult.Declined("Fondos insuficientes (simulado)"),
            97 => TerminalChargeResult.Error("Error de comunicación (simulado)"),
            _ => Approve(request)
        };

        progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Done,
            result.IsApproved ? "Transacción aprobada." : result.Message ?? "Transacción finalizada."));
        return result;
    }

    public Task<bool> PingAsync(string? address, CancellationToken ct = default) => Task.FromResult(true);

    private TerminalChargeResult Approve(TerminalChargeRequest request)
    {
        var rng = Random.Shared;
        var auth = rng.Next(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var brand = Brands[(uint)Interlocked.Increment(ref _brandCounter) % Brands.Length];
        var last4 = rng.Next(0, 10_000).ToString("D4", CultureInfo.InvariantCulture);
        var now = _clock.GetLocalNow();
        var reference = $"MOCK-{now.UtcTicks}";
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _settings.Currency : request.Currency;

        return new TerminalChargeResult(
            TerminalPaymentStatus.Approved,
            AuthorizationCode: auth,
            CardBrand: brand,
            CardLast4: last4,
            ProviderReference: reference,
            Message: "Aprobada",
            VoucherLines: BuildVoucher(request, currency, brand, last4, auth, reference, now));
    }

    /// <summary>Plain ASCII, 32 columns max (fits a 58 mm receipt and the emulator's voucher format).</summary>
    private static IReadOnlyList<string> BuildVoucher(TerminalChargeRequest request, string currency, string brand, string last4, string auth, string reference, DateTimeOffset when)
    {
        const int width = 32;
        var amount = request.Amount.ToString("#,##0.##", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            Center("LOQZEN POS", width),
            Center("DATAFONO SIMULADO", width),
            new string('-', width),
            Center("VENTA", width),
            Pair("TOTAL", $"{amount} {currency}", width),
            Pair("TARJETA", $"{brand} ****{last4}", width),
            string.Empty,
            Center("APROBADA", width),
            Pair("AUTORIZACION", auth, width),
            Pair("REF", Clip(reference, 20), width),
            Pair("FACTURA", Clip(request.InvoiceReference, 20), width),
            Pair("FECHA", when.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), width),
            new string('-', width)
        };
        return lines;
    }

    private static string Center(string text, int width)
    {
        text = Clip(text, width);
        var pad = (width - text.Length) / 2;
        return new string(' ', pad) + text;
    }

    private static string Pair(string label, string value, int width)
    {
        value = Clip(value, width - 1);
        var gap = Math.Max(1, width - label.Length - value.Length);
        return (label + new string(' ', gap) + value)[..Math.Min(width, label.Length + gap + value.Length)];
    }

    private static string Clip(string? text, int max)
    {
        text ??= string.Empty;
        return text.Length <= max ? text : text[..max];
    }

    internal static int LastTwoDigits(decimal amount) => (int)(Math.Abs(Math.Truncate(amount)) % 100);
}
