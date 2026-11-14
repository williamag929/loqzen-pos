using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Application.DTOs;
using Core.Application.Security;
using Core.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Multi-register part of the shell: the 🏪 nav-bar chip (this register + master/satellite link state +
/// kitchen socket), the satellite tab rules (only Maestros lives on the master) and the catalog-changed
/// reload of the selling screens.
/// </summary>
public partial class MainViewModel
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    private TerminalSettings _terminalSettings = null!;
    private ITerminalContext _terminalContext = null!;
    private TerminalStatus _terminalStatus = null!;
    private SatelliteSyncWorker _satellite = null!;
    private OutboxSyncWorker _outbox = null!;
    private IServiceScopeFactory _scopes = null!;
    private DispatcherTimer? _terminalChipTimer;
    private DispatcherTimer? _catalogReloadTimer;
    private int _satellitesOnline;
    private int _satellitesTotal;
    private bool _refreshingTerminals;

    public bool IsSatellite => _terminalSettings.IsSatellite;
    public bool IsMaster => !_terminalSettings.IsSatellite;

    // ── Terminal chip (🏪 this register) ─────────────────────────────────────
    /// <summary>Full text ("🏪 CAJA-02 · Satélite · sincronizado hace 5s …"), shown inside the "⋯" flyout.</summary>
    [ObservableProperty] private string _terminalChipText = "🏪";
    /// <summary>Short text for the bar: "🏪 CAJA-01" (+ " ⚠️" / " · sin conexión" / " · N pend." on a satellite).</summary>
    [ObservableProperty] private string _terminalChipShortText = "🏪";
    [ObservableProperty] private string _terminalChipTooltip = string.Empty;
    /// <summary>Hex from ThemePalette (re-read on theme change): green = fine, amber = selling offline / pending,
    /// red = configuration or registration error. Also paints the border of the collapsed "⋯" chip.</summary>
    [ObservableProperty] private string _terminalChipColor = ThemePalette.SuccessBright;

    public string TerminalChipDisplayText => TerminalChipShortText;
    public string TerminalChipDisplayTooltip => $"{TerminalChipText}\n{TerminalChipTooltip}".Trim();
    public bool IsTerminalChipVisible => Session.IsLoggedIn || !_authSettings.RequireLogin;

    partial void OnTerminalChipShortTextChanged(string value) => OnPropertyChanged(nameof(TerminalChipDisplayText));
    partial void OnTerminalChipTextChanged(string value) => OnPropertyChanged(nameof(TerminalChipDisplayTooltip));
    partial void OnTerminalChipTooltipChanged(string value) => OnPropertyChanged(nameof(TerminalChipDisplayTooltip));

    // ── Tab rules: role permission AND terminal rule ─────────────────────────
    // A satellite sells and ALSO works the shared comandas / KDS (through the master's API); only
    // Maestros stays on the master (catalog, users and terminals are replicated from there).
    public bool IsCheckoutTabVisible     => CanShow(0);
    public bool IsComandasTabVisible     => CanShow(4);
    public bool IsKdsTabVisible          => CanShow(1);
    public bool IsCashShiftTabVisible    => CanShow(2);
    public bool IsBillPaymentsTabVisible => CanShow(5);
    public bool IsMastersTabVisible      => CanShow(3);

    private static bool AllowedOnSatellite(PosView view) => view != PosView.Masters;

    private bool CanShow(int index)
    {
        var view = ViewOf(index);
        if (IsSatellite && !AllowedOnSatellite(view)) return false;
        return Session.CanAccess(view);
    }

    private void NotifyTabVisibility()
    {
        OnPropertyChanged(nameof(IsCheckoutTabVisible));
        OnPropertyChanged(nameof(IsComandasTabVisible));
        OnPropertyChanged(nameof(IsKdsTabVisible));
        OnPropertyChanged(nameof(IsCashShiftTabVisible));
        OnPropertyChanged(nameof(IsBillPaymentsTabVisible));
        OnPropertyChanged(nameof(IsMastersTabVisible));
        OnPropertyChanged(nameof(IsTerminalChipVisible));
    }

    private void InitTerminal(TerminalSettings terminalSettings, ITerminalContext terminalContext, TerminalStatus terminalStatus,
        SatelliteSyncWorker satellite, OutboxSyncWorker outbox, IServiceScopeFactory scopes)
    {
        _terminalSettings = terminalSettings;
        _terminalContext = terminalContext;
        _terminalStatus = terminalStatus;
        _satellite = satellite;
        _outbox = outbox;
        _scopes = scopes;

        _terminalStatus.Changed += UpdateTerminalChip;
        if (IsSatellite)
        {
            _satellite.Changed += UpdateTerminalChip;
            _outbox.Changed += UpdateTerminalChip;
            _satellite.CatalogChanged += ScheduleCatalogReload;
            // "sincronizado hace Xs" ticks every 5 s.
            _terminalChipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _terminalChipTimer.Tick += (_, _) => UpdateTerminalChip();
        }
        else
        {
            // Master: how many satellites are online (LastSeenAt within 90 s), refreshed every 30 s.
            _terminalChipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _terminalChipTimer.Tick += (_, _) => _ = RefreshSatellitesAsync();
            _ = RefreshSatellitesAsync();
        }
        _terminalChipTimer.Start();
        UpdateTerminalChip();
    }

    private string CodeLabel => _terminalContext.IsSet ? _terminalContext.Code
        : (_terminalSettings.Code ?? string.Empty).Trim().ToUpperInvariant();

    private void UpdateTerminalChip()
    {
        var code = CodeLabel;
        if (_terminalStatus.ConfigError is { } cfg)
        {
            TerminalChipText = T("Register.ConfigError", code, cfg);
            TerminalChipShortText = T("Register.Short.Warn", code);
            TerminalChipTooltip = T("Register.ConfigError.Tooltip");
            TerminalChipColor = ThemePalette.DangerBright;
            return;
        }
        if (_terminalStatus.RegistrationError is { } reg)
        {
            TerminalChipText = T("Register.Unregistered", code);
            TerminalChipShortText = T("Register.Short.Warn", code);
            TerminalChipTooltip = T("Register.Unregistered.Tooltip", reg);
            TerminalChipColor = ThemePalette.DangerBright;
            return;
        }

        if (IsMaster)
        {
            var online = _satellitesTotal == 0 ? string.Empty
                : _satellitesOnline == 1 ? T("Register.Master.Online.One") : T("Register.Master.Online.Many", _satellitesOnline);
            TerminalChipText = T("Register.Master", code, online);
            TerminalChipShortText = T("Register.Short", code);
            TerminalChipTooltip = _satellitesTotal == 0
                ? T("Register.Master.Tooltip.None")
                : T("Register.Master.Tooltip", _satellitesOnline, _satellitesTotal);
            TerminalChipColor = ThemePalette.SuccessBright;
            return;
        }

        var (pending, failed, _) = _outbox.Counts;
        var queued = pending + failed;
        // Kitchen socket (/ws/kds): comandas and KDS refresh live only while it is open.
        var kitchenLive = _satellite.IsKitchenLive;
        var kitchen = T(kitchenLive ? "Register.Kitchen.Live" : "Register.Kitchen.Offline");
        var kitchenTip = kitchenLive
            ? T("Register.Kitchen.Live.Tip")
            : T("Register.Kitchen.Offline.Tip", _satellite.KitchenSocketError is { } ke ? $" ({ke})" : string.Empty);
        if (_satellite.IsMasterReachable)
        {
            var since = _satellite.LastPullAt ?? _satellite.LastHeartbeatAt;
            var ago = since is { } t ? Ago(DateTimeOffset.UtcNow - t) : "—";
            var tail = queued > 0 ? T("Register.Satellite.Pending", queued) : string.Empty;
            TerminalChipText = T("Register.Satellite.Synced", code, ago, tail, kitchen);
            TerminalChipShortText = queued > 0 ? T("Register.Short.Pending", code, queued) : T("Register.Short", code);
            TerminalChipTooltip = T("Register.Satellite.Tooltip", _terminalSettings.MasterUrl, Stamp(_satellite.LastPullAt), Stamp(_satellite.LastHeartbeatAt)) +
                                  (_satellite.LastApply is { } a ? T("Register.Satellite.LastApply", a.Inserted, a.Updated, a.Deactivated) : string.Empty) +
                                  kitchenTip;
            TerminalChipColor = queued > 0 || !kitchenLive ? ThemePalette.WarningBright : ThemePalette.SuccessBright;
        }
        else
        {
            TerminalChipText = T("Register.Satellite.Offline", code, queued, kitchen);
            TerminalChipShortText = T("Register.Short.Offline", code);
            TerminalChipTooltip = T("Register.Satellite.Offline.Tooltip", _terminalSettings.MasterUrl,
                                      _satellite.LastError is { } e ? $" ({e})" : string.Empty) + kitchenTip;
            TerminalChipColor = ThemePalette.WarningBright;
        }
    }

    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 60 ? T("Register.Ago.Seconds", Math.Max(0, (int)t.TotalSeconds))
        : t.TotalMinutes < 60 ? T("Register.Ago.Minutes", (int)t.TotalMinutes)
        : T("Register.Ago.Hours", (int)t.TotalHours);

    private static string Stamp(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("HH:mm:ss") : T("Register.Never");

    /// <summary>Master only: count satellites seen within the online window (own scope, worker thread).</summary>
    private async Task RefreshSatellitesAsync()
    {
        if (_refreshingTerminals) return;
        _refreshingTerminals = true;
        try
        {
            var now = DateTime.UtcNow;
            var (online, total) = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                var all = await scope.ServiceProvider.GetRequiredService<ITerminalService>().GetAllAsync();
                var satellites = all.Where(t => t.Kind == Core.Domain.Enums.TerminalKind.Satellite && t.IsActive).ToList();
                return (satellites.Count(t => t.IsOnline(now, OnlineWindow)), satellites.Count);
            });
            _satellitesOnline = online;
            _satellitesTotal = total;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Terminal] No se pudo leer la lista de cajas: {ex.Message}");
        }
        finally
        {
            _refreshingTerminals = false;
            UpdateTerminalChip();
        }
    }

    // ── Catalog changed on a satellite → reload selling screens (debounced 300 ms, cart untouched) ──
    private void ScheduleCatalogReload()
    {
        _catalogReloadTimer ??= CreateCatalogReloadTimer();
        _catalogReloadTimer.Stop();
        _catalogReloadTimer.Start();
    }

    private DispatcherTimer CreateCatalogReloadTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                await Checkout.ReloadCatalogAsync();
                await BillPayments.ReloadBillersAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Satélite] No se pudo recargar el catálogo en pantalla: {ex.Message}");
            }
        };
        return timer;
    }
}
