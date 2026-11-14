using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Localization;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Kitchen Display. Cards are in ARRIVAL order. A 1s UI tick updates colours/countdowns; every
/// PollSeconds it re-reads the DB and MERGES (adds new cards, updates existing in place, removes gone)
/// so running animations don't restart. Each operation uses its own DI scope (fresh DbContext).
/// On a SATELLITE IKitchenService is RemoteKitchenService (master's /api/kds) and the bus is fed by
/// MasterKitchenSocketClient; the poll stays as fallback and a master that does not answer shows ONE
/// "Sin conexión con la caja principal" line in the header while the last cards stay on screen.
/// </summary>
public partial class KdsViewModel : ObservableObject, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IPrinterService _printerService;
    private readonly KdsSettings _settings;
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _eventDebounce;
    private readonly IDisposable _eventSubscription;
    private readonly Infrastructure.Data.Sync.MasterKitchenSocketClient? _kitchenSocket;
    private int _ticksSincePoll;
    private bool _loading;
    private bool _reloadRequested;

    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);

    /// <summary>Label of the "all stations" filter in the current language (Stations[0]); station names are data.</summary>
    private string _allStationsLabel = T("Kds.Station.All");

    [ObservableProperty] private string _selectedStation = T("Kds.Station.All");
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _inPrepCount;
    [ObservableProperty] private int _readyCount;
    [ObservableProperty] private string _kdsTitle = T("Kds.WindowTitle");
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>True while subscribed to the kitchen event bus: cards update instantly, the poll is only a fallback.
    /// On a satellite it follows the WebSocket to the master (MasterKitchenSocketClient.IsConnected).</summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>Satellite: the last read from the master failed; header shows "Sin conexión con la caja principal" once.</summary>
    [ObservableProperty] private bool _isMasterOffline;

    public const string MasterOfflineMessage = "Sin conexión con la caja principal";

    public ObservableCollection<string> Stations { get; } = new() { T("Kds.Station.All"), "Cocina Caliente", "Barra", "Parrilla" };
    public ObservableCollection<KitchenOrderCardViewModel> FilteredOrders { get; } = new();
    private readonly List<KitchenOrderCardViewModel> _allOrders = new();

    /// <param name="kitchenSocket">Registered only on a satellite (null on the master: DI uses the default).</param>
    public KdsViewModel(IServiceScopeFactory scopes, IPrinterService printerService, KdsSettings settings, TimeProvider clock,
        IKitchenEventBus events, Infrastructure.Data.Sync.MasterKitchenSocketClient? kitchenSocket = null)
    {
        _scopes = scopes;
        _printerService = printerService;
        _settings = settings;
        _clock = clock;
        _kitchenSocket = kitchenSocket;

        // Events arriving within 150 ms (a ticket with several stations, a burst from the tablets)
        // coalesce into ONE reload: the one-shot timer restarts on every event.
        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _eventDebounce.Tick += (_, _) =>
        {
            _eventDebounce.Stop();
            _ticksSincePoll = 0; // the poll is a fallback; no need to re-read right after an event
            _ = LoadOrdersAsync();
        };
        // Handlers run on the publisher's thread (sale, timer, Kestrel request): marshal to the UI.
        _eventSubscription = events.Subscribe(_ => Dispatcher.UIThread.Post(RestartDebounce));
        if (_kitchenSocket != null)
        {
            _kitchenSocket.Changed += OnKitchenSocketChanged; // worker thread
            IsLive = _kitchenSocket.IsConnected;
        }
        else
        {
            IsLive = true;
        }

        Localizer.Instance.Changed += OnLanguageChanged;

        _ = LoadOrdersAsync();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    private void RestartDebounce()
    {
        _eventDebounce.Stop();
        _eventDebounce.Start();
    }

    private void OnKitchenSocketChanged()
    {
        var socket = _kitchenSocket;
        if (socket == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var wasLive = IsLive;
            IsLive = socket.IsConnected;
            if (IsLive && !wasLive) RestartDebounce(); // back online: catch up on whatever happened meanwhile
        });
    }

    /// <summary>Language switched: "Todas" chip + every card's texts (badges, button, tooltip, waiter line) in place.
    /// The status bar keeps its last message until the next action.</summary>
    private void OnLanguageChanged()
    {
        var wasAll = SelectedStation == _allStationsLabel;
        _allStationsLabel = T("Kds.Station.All");
        Stations[0] = _allStationsLabel;
        if (wasAll) SelectedStation = _allStationsLabel;
        KdsTitle = T("Kds.WindowTitle");
        foreach (var card in _allOrders) card.RefreshTexts();
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        Localizer.Instance.Changed -= OnLanguageChanged;
        _timer.Stop();
        _eventDebounce.Stop();
        _eventSubscription.Dispose();
        if (_kitchenSocket != null) _kitchenSocket.Changed -= OnKitchenSocketChanged;
        IsLive = false;
    }

    private static bool IsConnectivityError(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException or TimeoutException or System.Net.Sockets.SocketException => true,
        TaskCanceledException => true, // HttpClient timeout
        _ => ex.InnerException is { } inner && IsConnectivityError(inner)
    };

    /// <summary>Master unreachable → one header line, cards untouched (no exception, no log spam). Anything else → error line.</summary>
    private void ReportReadError(Exception ex)
    {
        if (IsConnectivityError(ex))
        {
            if (!IsMasterOffline)
            {
                IsMasterOffline = true;
                Console.WriteLine($"[KDS] {MasterOfflineMessage}: {ex.Message}");
            }
            return;
        }
        StatusMessage = T("Kds.Status.ReadError", ex.Message);
    }

    private async Task TickAsync()
    {
        var expired = _allOrders.Where(c => !c.Tick(UtcNow)).ToList();
        if (expired.Count > 0)
        {
            foreach (var c in expired) _allOrders.Remove(c);
            FilterOrders();
            RecalculateCounts();
        }

        if (++_ticksSincePoll >= Math.Max(1, _settings.PollSeconds))
        {
            _ticksSincePoll = 0;
            await LoadOrdersAsync();
        }
    }

    /// <summary>Merge DB state into the card list without recreating existing cards.</summary>
    public async Task LoadOrdersAsync()
    {
        if (_loading)
        {
            _reloadRequested = true; // an event landed mid-read: read again when this one finishes
            return;
        }
        _loading = true;
        try
        {
            IReadOnlyList<Core.Domain.Entities.KitchenOrder> active;
            using (var scope = _scopes.CreateScope())
                active = await scope.ServiceProvider.GetRequiredService<IKitchenService>().GetActiveAsync();
            if (IsMasterOffline)
            {
                IsMasterOffline = false;
                Console.WriteLine("[KDS] Conexión con la caja principal restablecida");
            }

            var byId = active.ToDictionary(k => k.Id);
            var changed = false;

            // Remove cards that are no longer active — EXCEPT archived ones still in their undo window.
            foreach (var card in _allOrders.Where(c => !byId.ContainsKey(c.Id) && !c.IsArchived).ToList())
            {
                _allOrders.Remove(card);
                changed = true;
            }

            foreach (var ko in active)
            {
                var existing = _allOrders.FirstOrDefault(c => c.Id == ko.Id);
                if (existing != null)
                {
                    existing.Apply(ko);
                }
                else
                {
                    _allOrders.Add(new KitchenOrderCardViewModel(ko, _settings, UtcNow, AdvanceAsync, PrintAsync, UndoAsync));
                    changed = true;
                }
            }

            if (changed)
            {
                _allOrders.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt)); // arrival order
                FilterOrders();
            }
            RecalculateCounts();
        }
        catch (Exception ex)
        {
            ReportReadError(ex);
        }
        finally
        {
            _loading = false;
        }

        if (_reloadRequested)
        {
            _reloadRequested = false;
            await LoadOrdersAsync();
        }
    }

    partial void OnSelectedStationChanged(string value) => FilterOrders();

    [RelayCommand]
    public void SelectStation(string station) => SelectedStation = station;

    [RelayCommand]
    public Task RefreshAsync() => LoadOrdersAsync();

    private void FilterOrders()
    {
        var wanted = _allOrders
            .Where(o => SelectedStation == _allStationsLabel || o.Station.Equals(SelectedStation, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Minimal diff keeps existing card visuals alive.
        for (var i = FilteredOrders.Count - 1; i >= 0; i--)
            if (!wanted.Contains(FilteredOrders[i])) FilteredOrders.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var idx = FilteredOrders.IndexOf(wanted[i]);
            if (idx == -1) FilteredOrders.Insert(i, wanted[i]);
            else if (idx != i) FilteredOrders.Move(idx, i);
        }
    }

    private void RecalculateCounts()
    {
        PendingCount = _allOrders.Count(o => o.Status == KitchenOrderStatus.Pending);
        InPrepCount = _allOrders.Count(o => o.Status == KitchenOrderStatus.InPreparation);
        ReadyCount = _allOrders.Count(o => o.Status == KitchenOrderStatus.Ready);
    }

    // ── Card actions ─────────────────────────────────────────────────────────
    private Task AdvanceAsync(KitchenOrderCardViewModel card) =>
        RunAsync(card, svc => svc.AdvanceAsync(card.Id), updated => updated.Status == KitchenOrderStatus.Delivered
            ? T("Kds.Status.DeliveredArchiving", card.TableNumber, _settings.ArchiveDelaySeconds)
            : null);

    private Task UndoAsync(KitchenOrderCardViewModel card) =>
        RunAsync(card, svc => svc.UndoDeliverAsync(card.Id), _ => T("Kds.Status.Restored", card.TableNumber));

    private async Task PrintAsync(KitchenOrderCardViewModel card)
    {
        var receipt = new ReceiptDto
        {
            OrderNumber = card.OrderNumber,
            Date = card.CreatedAt.ToLocalTime(),
            CashierName = card.WaiterName,
            StoreName = $"KDS: {card.Station}",
            StoreAddress = card.TableNumber,
            TableName = card.TableNumber,
            Items = card.Items.Where(i => !i.IsCancelled).Select(i => new ReceiptItemDto { Name = i.ProductName, Quantity = i.Quantity, Notes = i.Notes }).ToList()
        };

        if (!await _printerService.PrintKitchenTicketAsync(receipt, card.Station))
        {
            // Don't mark as printed: the button stays green so the cook knows it didn't come out.
            StatusMessage = T("Kds.Status.PrintFailed", card.TableNumber);
            return;
        }
        await RunAsync(card, svc => svc.MarkPrintedAsync(card.Id), _ => T("Kds.Status.Printed", card.TableNumber));
    }

    private async Task RunAsync(KitchenOrderCardViewModel card,
        Func<IKitchenService, Task<Core.Domain.Entities.KitchenOrder>> op,
        Func<Core.Domain.Entities.KitchenOrder, string?> message)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var updated = await op(scope.ServiceProvider.GetRequiredService<IKitchenService>());
            card.Apply(updated);
            card.Tick(UtcNow);
            RecalculateCounts();
            if (message(updated) is { } msg) StatusMessage = msg;
        }
        catch (Exception ex) when (IsConnectivityError(ex))
        {
            IsMasterOffline = true; // header line; the card keeps its state until the master answers again
            StatusMessage = T("Kds.Status.OfflineNotUpdated", card.TableNumber);
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠️ {ex.Message}";
            await LoadOrdersAsync(); // resync with DB
        }
    }
}
