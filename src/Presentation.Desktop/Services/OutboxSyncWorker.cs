using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Core.Application.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Desktop.Services;

/// <summary>
/// Background Outbox → ERP delivery for this terminal. When <see cref="SyncSettings.Enabled"/> a
/// <see cref="DispatcherTimer"/> fires every IntervalSeconds and runs one dispatcher pass on a worker
/// thread, each pass in its own DI scope (CONTRIBUTING §4.1: the DbContext is not thread-safe).
/// Delivery errors never reach the UI as exceptions: they land in <see cref="LastError"/>.
/// <see cref="Changed"/> is raised on the UI thread after every pass / count refresh.
/// </summary>
public sealed class OutboxSyncWorker : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SyncSettings _settings;
    private readonly TimeProvider _clock;
    private DispatcherTimer? _timer;
    private int _running; // 0/1: one pass at a time

    public OutboxSyncWorker(IServiceScopeFactory scopes, SyncSettings settings, TimeProvider clock)
    {
        _scopes = scopes;
        _settings = settings;
        _clock = clock;
    }

    public bool IsEnabled => _settings.Enabled;
    public OutboxDispatchResult? LastResult { get; private set; }
    public (int Pending, int Failed, int Synced) Counts { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastError { get; private set; }

    public event Action? Changed;

    /// <summary>Call once from the UI thread at start-up. Counts are read even when sync is disabled.</summary>
    public void Start()
    {
        _ = RefreshCountsAsync();
        if (!_settings.Enabled || _timer != null) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.IntervalSeconds)) };
        _timer.Tick += (_, _) => _ = RunOnceAsync();
        _timer.Start();
    }

    /// <summary>One dispatcher pass (also used by the timer). Skipped if a pass is still running.</summary>
    public async Task RunOnceAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                LastResult = await dispatcher.DispatchPendingAsync();
                Counts = await dispatcher.GetCountsAsync();
            });
            LastRunAt = _clock.GetUtcNow();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Console.WriteLine($"[Sync] Error en el envío del Outbox: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            RaiseChanged();
        }
    }

    public async Task RefreshCountsAsync()
    {
        try
        {
            Counts = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().GetCountsAsync();
            });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        RaiseChanged();
    }

    /// <summary>Failed → Pending (RetryCount = 0), then refreshes the counts. Returns how many were reset.</summary>
    public async Task<int> RetryFailedAsync()
    {
        int reset;
        try
        {
            reset = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().RetryFailedAsync();
            });
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            reset = 0;
        }
        await RefreshCountsAsync();
        return reset;
    }

    private void RaiseChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke();
        else Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
