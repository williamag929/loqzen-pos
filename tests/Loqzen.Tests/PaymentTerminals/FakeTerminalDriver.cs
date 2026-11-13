using Core.Application.Hardware;
using Core.Domain.Enums;

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>
/// Scriptable <see cref="IPaymentTerminalDriver"/> for service tests. <see cref="OnCharge"/> decides the answer (default
/// Approved); <see cref="Entered"/> completes when the service has handed the charge to the driver, so a test can act
/// (advance the clock, cancel, fire a second charge) while the first one is in flight.
/// </summary>
public sealed class FakeTerminalDriver : IPaymentTerminalDriver
{
    public static readonly TerminalChargeResult ApprovedVisa = new(
        TerminalPaymentStatus.Approved, "123456", "VISA", "4242", "RRN-1", "Aprobada",
        new[] { "LOQZEN POS", "VENTA", "APROBADA" });

    public FakeTerminalDriver(PaymentTerminalProvider provider = PaymentTerminalProvider.Mock) => Provider = provider;

    public PaymentTerminalProvider Provider { get; }

    public List<TerminalChargeRequest> Requests { get; } = new();
    public List<string?> Pings { get; } = new();
    public bool PingResult { get; set; } = true;
    public Exception? PingThrows { get; set; }

    public Func<TerminalChargeRequest, IProgress<TerminalChargeProgress>?, CancellationToken, Task<TerminalChargeResult>> OnCharge { get; set; }
        = (_, _, _) => Task.FromResult(ApprovedVisa);

    private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when ChargeAsync is called; reset with <see cref="ResetEntered"/>.</summary>
    public Task Entered => _entered.Task;

    public void ResetEntered() => _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TerminalChargeResult> ChargeAsync(TerminalChargeRequest request, IProgress<TerminalChargeProgress>? progress, CancellationToken ct)
    {
        Requests.Add(request);
        _entered.TrySetResult();
        return OnCharge(request, progress, ct);
    }

    public Task<bool> PingAsync(string? address, CancellationToken ct = default)
    {
        Pings.Add(address);
        if (PingThrows != null) throw PingThrows;
        return Task.FromResult(PingResult);
    }

    /// <summary>Waits on the token like a real driver blocked on the device; <paramref name="onCancel"/> decides the exit.</summary>
    public static Func<TerminalChargeRequest, IProgress<TerminalChargeProgress>?, CancellationToken, Task<TerminalChargeResult>> WaitForToken(
        Func<TerminalChargeResult>? onCancel = null) =>
        async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (onCancel != null) { return onCancel(); }
            return TerminalChargeResult.Error("unreachable");
        };
}

public sealed class FakeDriverFactory : IPaymentTerminalDriverFactory
{
    private readonly Dictionary<PaymentTerminalProvider, IPaymentTerminalDriver> _drivers = new();

    public FakeDriverFactory(params IPaymentTerminalDriver[] drivers)
    {
        foreach (var d in drivers) _drivers[d.Provider] = d;
    }

    public IReadOnlyList<PaymentTerminalProvider> AvailableProviders => _drivers.Keys.ToList();

    public IPaymentTerminalDriver GetDriver(PaymentTerminalProvider provider) =>
        _drivers.TryGetValue(provider, out var d) ? d : throw new InvalidOperationException($"No driver for {provider}");
}
