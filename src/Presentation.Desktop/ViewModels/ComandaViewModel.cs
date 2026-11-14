using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Kitchen;
using Core.Application.Receipts;
using Core.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Restaurant mode: 3 touch panels navigated left/right.
///   Panel 0 (izq)    – open comandas + tables
///   Panel 1 (centro) – current comanda: categories → products, items with yellow/green state
///   Panel 2 (der)    – live receipt preview + close/pay
/// Services are resolved per operation from a fresh scope: the DbContext is not thread-safe and the
/// confirmation timer runs concurrently with user actions.
/// On a SATELLITE register IComandaService is RemoteComandaService (the master owns the comandas): the 1 s timer
/// only drives the yellow countdown (the master's timer confirms), the list refreshes on ComandaChanged events that
/// arrive through MasterKitchenSocketClient, and a master that does not answer shows "Sin conexión con la caja
/// principal" (amber) without leaving the screen.
/// </summary>
public partial class ComandaViewModel : ObservableObject, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IPrinterService _printer;
    private readonly ICashDrawerService _cashDrawer;
    private readonly ComandaSettings _settings;
    private readonly TimeProvider _clock;
    private readonly IManagerAuthPrompt _authPrompt;
    private readonly IUserSession _session;
    private readonly CustomerDisplayViewModel _display;
    private readonly ITerminalChargePrompt _terminalPrompt;
    private readonly PaymentTerminalSettings _terminalSettings;
    private readonly TerminalSettings _register;
    private readonly ITerminalContext _terminal;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eventDebounce;
    private readonly IDisposable _eventSubscription;
    private bool _tickRunning;

    /// <summary>UI text in the current language (Assets/i18n/{lang}.kitchen.json); documents stay Spanish.</summary>
    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);

    public ComandaViewModel(IServiceScopeFactory scopes, IPrinterService printer, ComandaSettings settings, TimeProvider clock,
        IManagerAuthPrompt authPrompt, IUserSession session, IKitchenEventBus events, CustomerDisplayViewModel display,
        ICashDrawerService cashDrawer, ITerminalChargePrompt terminalPrompt, PaymentTerminalSettings terminalSettings,
        TerminalSettings register, ITerminalContext terminal)
    {
        _terminalPrompt = terminalPrompt;
        _terminalSettings = terminalSettings;
        _register = register;
        _terminal = terminal;
        _cashDrawer = cashDrawer;
        _scopes = scopes;
        _printer = printer;
        _settings = settings;
        _clock = clock;
        _authPrompt = authPrompt;
        _session = session;
        _display = display;

        // The waiter is whoever signed in on this terminal (falls back to the generic label).
        ApplySessionWaiter();
        _session.Changed += ApplySessionWaiter;

        // ES/EN switch: texts computed in code (titles, card/tile/row states, banners) are re-evaluated in place.
        Localizer.Instance.Changed += OnLanguageChanged;

        // 1s tick: confirms due items (→ green + KDS) and refreshes yellow countdowns.
        // Runs for the whole app lifetime — NOT only while this screen is visible — otherwise
        // items would never reach the kitchen while the cashier is on another tab.
        // On a satellite only the countdown runs: the master's timer confirms (see TickAsync).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        // Any register (this one, the other cash register, a comandera tablet) may change a comanda: the service
        // publishes ComandaChanged after every commit (on a satellite it arrives through the master's WebSocket).
        // Events within 150 ms coalesce into one refresh of the list and the current comanda.
        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            try { await RefreshAsync(); }
            catch (Exception ex) { ReportError(ex, "Comanda.Doing.Refreshing"); }
        };
        _eventSubscription = events.Subscribe(e =>
        {
            if (e.Type != KitchenEventType.ComandaChanged) return;
            Dispatcher.UIThread.Post(() => { _eventDebounce.Stop(); _eventDebounce.Start(); });
        });
    }

    // ── Register identity (shared comandas between registers) ───────────────
    /// <summary>Satellite: comandas live on the master; this screen talks to it over HTTP.</summary>
    public bool IsSatellite => _register.IsSatellite;

    /// <summary>Code stamped on payment locks ("CAJA-02"); the registered context wins over raw settings.</summary>
    private string OwnTerminalCode => _terminal.IsSet ? _terminal.Code : (_register.Code ?? string.Empty).Trim().ToUpperInvariant();

    private bool IsOwnCode(string? code) => !string.IsNullOrWhiteSpace(code) && string.Equals(code.Trim(), OwnTerminalCode, StringComparison.OrdinalIgnoreCase);

    public const string MasterOfflineMessage = "Sin conexión con la caja principal";

    /// <summary>Satellite: the last call to the master failed (status bar turns amber). Cleared by the next successful call.</summary>
    [ObservableProperty]
    private bool _isMasterOffline; // ComandaView converts it to WarningBright|TextSecondary

    private static bool IsConnectivityError(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException or TimeoutException or System.Net.Sockets.SocketException => true,
        TaskCanceledException => true, // HttpClient timeout
        _ => ex.InnerException is { } inner && IsConnectivityError(inner)
    };

    /// <summary>Master unreachable → one amber line, screen stays usable (the next action retries). Anything else → red.
    /// <paramref name="doingKey"/> is a Comanda.Doing.* key ("actualizando", …); the service message itself stays as-is.</summary>
    private void ReportError(Exception ex, string doingKey)
    {
        if (IsConnectivityError(ex))
        {
            IsMasterOffline = true;
            StatusMessage = T("Status.MasterOffline");
            return;
        }
        StatusMessage = T("Comanda.Error.Doing", doingKey.Length == 0 ? string.Empty : T(doingKey), ex.Message);
    }

    /// <summary>Language switched: recompute every text this screen builds in code. The status bar keeps its last
    /// message until the next action; item rows refresh on the next 1 s tick.</summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(PanelTitle));
        OnPropertyChanged(nameof(DelayHint));
        if (_othersTile != null) _othersTile.Rename(T("Comanda.Catalog.Others"));
        OnPropertyChanged(nameof(CatalogTitle));
        NotifyLockChanged();
        if (IsNotesEditorOpen) NotesEditorTitle = T("Comanda.Notes.Title", _editingItemName);
        foreach (var card in Comandas) card.RefreshTexts();
        foreach (var tile in Tables) tile.RefreshTexts();
        foreach (var row in Items) { row.RefreshTexts(); row.Tick(UtcNow); }
    }

    // ── Navigation (0 = comandas, 1 = comanda actual, 2 = cierre/pago) ──────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListPanel), nameof(IsOrderPanel), nameof(IsPayPanel), nameof(PanelTitle), nameof(CanGoLeft), nameof(CanGoRight))]
    private int _panelIndex;

    public bool IsListPanel => PanelIndex == 0;
    public bool IsOrderPanel => PanelIndex == 1;
    public bool IsPayPanel => PanelIndex == 2;
    public bool CanGoLeft => PanelIndex > 0;
    public bool CanGoRight => PanelIndex < 2 && Current != null;

    public string PanelTitle => PanelIndex switch
    {
        0 => T("Comanda.Title.List"),
        1 => Current == null ? T("Comanda.Title.Order") : T("Comanda.Title.OrderFor", Current.TableName, Current.Number),
        _ => Current == null ? T("Comanda.Title.Pay") : T("Comanda.Title.PayFor", Current.TableName, Current.Number)
    };

    [RelayCommand]
    private void GoLeft()
    {
        if (PanelIndex <= 0) return;
        if (PanelIndex == 2 && Current is { } c && IsOwnCode(c.PaymentLockedByTerminalCode))
            _ = ReleasePaymentLockAsync(c); // leaving the bill without paying: free the table for the other register
        PanelIndex--;
    }

    [RelayCommand]
    private void GoRight()
    {
        if (CanGoRight) PanelIndex++;
    }

    /// <summary>Called by the view's swipe handler.</summary>
    public void Swipe(bool towardsRight)
    {
        if (towardsRight) GoRight(); else GoLeft();
    }

    // ── State ──────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent), nameof(PanelTitle), nameof(CanGoRight), nameof(IsCurrentOpen), nameof(IsCurrentReadyToPay), nameof(CurrentTotalText))]
    private ComandaDto? _current;

    public bool HasCurrent => Current != null;
    public bool IsCurrentOpen => Current?.Status == Core.Domain.Enums.ComandaStatus.Open;
    public bool IsCurrentReadyToPay => Current?.Status == Core.Domain.Enums.ComandaStatus.ReadyToPay;
    public string CurrentTotalText => ReceiptFormatter.Cop(Current?.Total ?? 0);

    [ObservableProperty] private string _waiterName = "Mesero";

    private void ApplySessionWaiter()
    {
        if (_session.CurrentUser is { } user) WaiterName = user.Name;
        else if (string.IsNullOrWhiteSpace(WaiterName)) WaiterName = "Mesero";
    }
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isShiftOpen;
    [ObservableProperty] private string _cashierName = string.Empty;

    public string DelayHint => T("Comanda.DelayHint", _settings.ConfirmationDelaySeconds);

    public ObservableCollection<ComandaCardViewModel> Comandas { get; } = new();
    public ObservableCollection<TableTileViewModel> Tables { get; } = new();
    public ObservableCollection<ComandaItemRowViewModel> Items { get; } = new();
    public ObservableCollection<ReceiptLine> ReceiptPreview { get; } = new();

    private Guid? _shiftId;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Called when the screen is shown: reloads catalog (may have changed in Maestros) and state.</summary>
    public async Task ActivateAsync()
    {
        await LoadCatalogAsync();
        await RefreshAsync();
    }

    public void Dispose()
    {
        _timer.Stop();
        _eventDebounce.Stop();
        _eventSubscription.Dispose();
        Localizer.Instance.Changed -= OnLanguageChanged;
    }

    private async Task TickAsync()
    {
        if (_tickRunning) return;
        _tickRunning = true;
        try
        {
            if (IsSatellite)
            {
                // The master's timer confirms; a request per second would only load the LAN. The confirmed items
                // arrive as a ComandaChanged event — here only the yellow countdown moves.
                foreach (var row in Items) row.Tick(UtcNow);
                return;
            }

            int confirmed;
            using (var scope = _scopes.CreateScope())
                confirmed = await scope.ServiceProvider.GetRequiredService<IComandaService>().ConfirmDueItemsAsync();

            if (confirmed > 0)
            {
                await RefreshAsync();
                StatusMessage = T("Comanda.Status.Confirmed", confirmed);
            }
            else
            {
                foreach (var row in Items) row.Tick(UtcNow);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex, "Comanda.Doing.Confirming");
        }
        finally
        {
            _tickRunning = false;
        }
    }
}
