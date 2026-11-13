using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Core.Application.DTOs;
using Core.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Desktop.Services;

/// <summary>
/// Satellite-side background work against the master register (started only when
/// <see cref="TerminalSettings.IsSatellite"/>): a heartbeat every HeartbeatSeconds and a catalog pull
/// (snapshot → ICatalogSyncService.ApplySnapshotAsync → ITerminalService.MarkCatalogPulledAsync) every
/// CatalogPullSeconds, plus one pull right at start. Each pass runs on a worker thread in its own DI scope
/// (CONTRIBUTING §4.1), one pass at a time. Errors never reach the UI as exceptions: they land in
/// <see cref="LastError"/> and flip <see cref="IsMasterReachable"/>. Selling never waits on this worker.
/// <see cref="Changed"/> and <see cref="CatalogChanged"/> are raised on the UI thread.
/// </summary>
public sealed class SatelliteSyncWorker : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TerminalSettings _settings;
    private readonly TimeProvider _clock;
    private readonly string? _appVersion;
    private DispatcherTimer? _heartbeatTimer;
    private DispatcherTimer? _pullTimer;
    private int _running; // 0/1: one pass at a time (heartbeat or pull)

    public SatelliteSyncWorker(IServiceScopeFactory scopes, TerminalSettings settings, TimeProvider clock)
    {
        _scopes = scopes;
        _settings = settings;
        _clock = clock;
        _appVersion = App.AppVersion;
    }

    public bool IsEnabled => _settings.IsSatellite && !string.IsNullOrWhiteSpace(_settings.MasterUrl);
    public bool IsMasterReachable { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
    public DateTimeOffset? LastPullAt { get; private set; }
    public CatalogApplyResult? LastApply { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Any state change (UI thread).</summary>
    public event Action? Changed;

    /// <summary>A pull changed at least one catalog row (UI thread): screens reload their product/biller lists.</summary>
    public event Action? CatalogChanged;

    /// <summary>Call once from the UI thread at start-up. No-op unless this is a satellite with a MasterUrl.</summary>
    public void Start()
    {
        if (!IsEnabled || _pullTimer != null) return;

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.HeartbeatSeconds)) };
        _heartbeatTimer.Tick += (_, _) => _ = HeartbeatOnceAsync();
        _heartbeatTimer.Start();

        _pullTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(10, _settings.CatalogPullSeconds)) };
        _pullTimer.Tick += (_, _) => _ = PullOnceAsync();
        _pullTimer.Start();

        // First contact right away: heartbeat (so the master lists us) then the catalog.
        _ = StartupPassAsync();
    }

    private async Task StartupPassAsync()
    {
        await HeartbeatOnceAsync();
        await PullOnceAsync();
    }

    /// <summary>POST /api/terminals/heartbeat. Skipped if another pass is running.</summary>
    public async Task HeartbeatOnceAsync()
    {
        if (!IsEnabled) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                var api = scope.ServiceProvider.GetRequiredService<IMasterApiClient>();
                var request = new TerminalHeartbeatRequest(TerminalSettings.NormalizeCode(_settings.Code), _settings.Name, _settings.Mode, _appVersion);
                await api.HeartbeatAsync(request);
            });
            LastHeartbeatAt = _clock.GetUtcNow();
            IsMasterReachable = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            IsMasterReachable = false;
            LastError = Describe(ex);
            Console.WriteLine($"[Satélite] Sin conexión con la caja principal (heartbeat): {LastError}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            RaiseChanged();
        }
    }

    /// <summary>GET snapshot → apply → mark pulled. Skipped if another pass is running.</summary>
    public async Task PullOnceAsync()
    {
        if (!IsEnabled) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        CatalogApplyResult? applied = null;
        try
        {
            applied = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                var api = scope.ServiceProvider.GetRequiredService<IMasterApiClient>();
                var snapshot = await api.GetSnapshotAsync();
                var result = await scope.ServiceProvider.GetRequiredService<ICatalogSyncService>().ApplySnapshotAsync(snapshot);
                await scope.ServiceProvider.GetRequiredService<ITerminalService>().MarkCatalogPulledAsync();
                return result;
            });
            LastPullAt = _clock.GetUtcNow();
            LastApply = applied;
            IsMasterReachable = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            IsMasterReachable = false;
            LastError = Describe(ex);
            Console.WriteLine($"[Satélite] No se pudo actualizar el catálogo: {LastError}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            RaiseChanged();
            if (applied is { Total: > 0 }) RaiseCatalogChanged();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "tiempo de espera agotado",
        System.Net.Http.HttpRequestException http => string.IsNullOrWhiteSpace(http.Message) ? "sin respuesta" : http.Message,
        _ => ex.Message
    };

    private void RaiseChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke();
        else Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    private void RaiseCatalogChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) CatalogChanged?.Invoke();
        else Dispatcher.UIThread.Post(() => CatalogChanged?.Invoke());
    }

    public void Dispose()
    {
        _heartbeatTimer?.Stop();
        _pullTimer?.Stop();
        _heartbeatTimer = null;
        _pullTimer = null;
    }
}
