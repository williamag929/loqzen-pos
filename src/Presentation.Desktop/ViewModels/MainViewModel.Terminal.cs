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
/// Multi-register part of the shell: the 🖥️ nav-bar chip (this register + master/satellite link state),
/// the satellite tab rules (Comandas / KDS / Maestros live on the master) and the catalog-changed
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

    // ── Terminal chip ────────────────────────────────────────────────────────
    [ObservableProperty] private string _terminalChipText = "🖥️";
    [ObservableProperty] private string _terminalChipTooltip = string.Empty;
    /// <summary>Green = fine, amber = selling offline / pending, red = configuration or registration error.</summary>
    [ObservableProperty] private string _terminalChipColor = "#34D399";

    /// <summary>Compact: only the icon; the full text moves to the tooltip (same rule as the LAN chip).</summary>
    public string TerminalChipDisplayText => IsCompact ? "🖥️" : TerminalChipText;
    public string TerminalChipDisplayTooltip => IsCompact ? $"{TerminalChipText}\n{TerminalChipTooltip}".TrimEnd() : TerminalChipTooltip;
    public bool IsTerminalChipVisible => Session.IsLoggedIn || !_authSettings.RequireLogin;

    partial void OnTerminalChipTextChanged(string value) => OnPropertyChanged(nameof(TerminalChipDisplayText));
    partial void OnTerminalChipTooltipChanged(string value) => OnPropertyChanged(nameof(TerminalChipDisplayTooltip));

    // ── Tab rules: role permission AND terminal rule ─────────────────────────
    // A satellite only sells (Venta, Caja, Recaudos); comandas, KDS and masters belong to the master
    // (tablets use the master's web pages).
    public bool IsCheckoutTabVisible     => CanShow(0);
    public bool IsComandasTabVisible     => CanShow(4);
    public bool IsKdsTabVisible          => CanShow(1);
    public bool IsCashShiftTabVisible    => CanShow(2);
    public bool IsBillPaymentsTabVisible => CanShow(5);
    public bool IsMastersTabVisible      => CanShow(3);

    private static bool AllowedOnSatellite(PosView view) => view is PosView.Checkout or PosView.CashShift or PosView.BillPayments;

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
            TerminalChipText = $"🖥️ {code} · ⚠️ {cfg}";
            TerminalChipTooltip = "Configure Terminal:MasterUrl en appsettings.json con la dirección de la caja principal (ej. http://192.168.1.10:5055).";
            TerminalChipColor = "#F87171";
            return;
        }
        if (_terminalStatus.RegistrationError is { } reg)
        {
            TerminalChipText = $"🖥️ {code} · ⚠️ caja sin registrar";
            TerminalChipTooltip = $"No se pudo registrar esta caja en la base de datos: {reg}";
            TerminalChipColor = "#F87171";
            return;
        }

        if (IsMaster)
        {
            var online = _satellitesTotal == 0 ? string.Empty
                : $" · {_satellitesOnline} {(_satellitesOnline == 1 ? "satélite" : "satélites")} en línea";
            TerminalChipText = $"🖥️ {code} · Principal{online}";
            TerminalChipTooltip = _satellitesTotal == 0
                ? "Caja principal. Aún no hay cajas satélite registradas (ver Maestros → Cajas)."
                : $"Caja principal · {_satellitesOnline} de {_satellitesTotal} satélites en línea (vistas en los últimos 90 s)";
            TerminalChipColor = "#34D399";
            return;
        }

        var (pending, failed, _) = _outbox.Counts;
        var queued = pending + failed;
        if (_satellite.IsMasterReachable)
        {
            var since = _satellite.LastPullAt ?? _satellite.LastHeartbeatAt;
            var ago = since is { } t ? Ago(DateTimeOffset.UtcNow - t) : "—";
            var tail = queued > 0 ? $" · {queued} pendientes" : string.Empty;
            TerminalChipText = $"🖥️ {code} · Satélite · sincronizado hace {ago}{tail}";
            TerminalChipTooltip = $"Caja principal: {_terminalSettings.MasterUrl}\nCatálogo: {Stamp(_satellite.LastPullAt)} · Latido: {Stamp(_satellite.LastHeartbeatAt)}" +
                                  (_satellite.LastApply is { } a ? $"\nÚltima actualización: {a.Inserted} nuevos · {a.Updated} cambiados · {a.Deactivated} desactivados" : string.Empty);
            TerminalChipColor = queued > 0 ? "#FBBF24" : "#34D399";
        }
        else
        {
            TerminalChipText = $"🖥️ {code} · Satélite · sin conexión con la caja principal · vendiendo sin conexión · {queued} pendientes";
            TerminalChipTooltip = $"No responde {_terminalSettings.MasterUrl}" +
                                  (_satellite.LastError is { } e ? $" ({e})" : string.Empty) +
                                  "\nLas ventas se guardan aquí y se envían cuando la caja principal vuelva.";
            TerminalChipColor = "#FBBF24";
        }
    }

    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)}s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}min"
        : $"{(int)t.TotalHours}h";

    private static string Stamp(DateTimeOffset? t) => t is { } v ? v.ToLocalTime().ToString("HH:mm:ss") : "nunca";

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
