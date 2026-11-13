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
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Restaurant mode: 3 touch panels navigated left/right.
///   Panel 0 (izq)    – open comandas + tables
///   Panel 1 (centro) – current comanda: categories → products, items with yellow/green state
///   Panel 2 (der)    – live receipt preview + close/pay
/// Services are resolved per operation from a fresh scope: the DbContext is not thread-safe and the
/// confirmation timer runs concurrently with user actions.
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
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eventDebounce;
    private readonly IDisposable _eventSubscription;
    private bool _tickRunning;

    public ComandaViewModel(IServiceScopeFactory scopes, IPrinterService printer, ComandaSettings settings, TimeProvider clock,
        IManagerAuthPrompt authPrompt, IUserSession session, IKitchenEventBus events, CustomerDisplayViewModel display,
        ICashDrawerService cashDrawer)
    {
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

        // 1s tick: confirms due items (→ green + KDS) and refreshes yellow countdowns.
        // Runs for the whole app lifetime — NOT only while this screen is visible — otherwise
        // items would never reach the kitchen while the cashier is on another tab.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        // A comandera tablet (REST) may add items / open comandas: new kitchen tickets mean the list
        // and the current comanda are stale. Created events within 150 ms coalesce into one refresh.
        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            try { await RefreshAsync(); }
            catch (Exception ex) { StatusMessage = $"❌ Error actualizando: {ex.Message}"; }
        };
        _eventSubscription = events.Subscribe(e =>
        {
            if (e.Type != KitchenEventType.Created) return;
            Dispatcher.UIThread.Post(() => { _eventDebounce.Stop(); _eventDebounce.Start(); });
        });
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
        0 => "📋 Comandas abiertas",
        1 => Current == null ? "Comanda" : $"🍽️ {Current.TableName} · Comanda #{Current.Number}",
        _ => Current == null ? "Cierre" : $"💳 Cerrar {Current.TableName} · #{Current.Number}"
    };

    [RelayCommand]
    private void GoLeft()
    {
        if (PanelIndex > 0) PanelIndex--;
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

    public string DelayHint => $"Los productos nuevos esperan {_settings.ConfirmationDelaySeconds}s (amarillo) antes de ir a cocina (verde).";

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
    }

    private async Task TickAsync()
    {
        if (_tickRunning) return;
        _tickRunning = true;
        try
        {
            int confirmed;
            using (var scope = _scopes.CreateScope())
                confirmed = await scope.ServiceProvider.GetRequiredService<IComandaService>().ConfirmDueItemsAsync();

            if (confirmed > 0)
            {
                await RefreshAsync();
                StatusMessage = $"🟢 {confirmed} producto(s) confirmados y enviados a cocina";
            }
            else
            {
                foreach (var row in Items) row.Tick(UtcNow);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error confirmando: {ex.Message}";
        }
        finally
        {
            _tickRunning = false;
        }
    }
}
