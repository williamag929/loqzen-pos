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

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Kitchen Display. Cards are in ARRIVAL order. A 1s UI tick updates colours/countdowns; every
/// PollSeconds it re-reads the DB and MERGES (adds new cards, updates existing in place, removes gone)
/// so running animations don't restart. Each operation uses its own DI scope (fresh DbContext).
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
    private int _ticksSincePoll;
    private bool _loading;
    private bool _reloadRequested;

    [ObservableProperty] private string _selectedStation = "Todas";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _inPrepCount;
    [ObservableProperty] private int _readyCount;
    [ObservableProperty] private string _kdsTitle = "KDS - Sistema de Pantallas de Cocina";
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>True while subscribed to the kitchen event bus: cards update instantly, the poll is only a fallback.</summary>
    [ObservableProperty] private bool _isLive;

    public ObservableCollection<string> Stations { get; } = new() { "Todas", "Cocina Caliente", "Barra", "Parrilla" };
    public ObservableCollection<KitchenOrderCardViewModel> FilteredOrders { get; } = new();
    private readonly List<KitchenOrderCardViewModel> _allOrders = new();

    public KdsViewModel(IServiceScopeFactory scopes, IPrinterService printerService, KdsSettings settings, TimeProvider clock,
        IKitchenEventBus events)
    {
        _scopes = scopes;
        _printerService = printerService;
        _settings = settings;
        _clock = clock;

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
        IsLive = true;

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

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _timer.Stop();
        _eventDebounce.Stop();
        _eventSubscription.Dispose();
        IsLive = false;
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
            StatusMessage = $"❌ Error leyendo órdenes: {ex.Message}";
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
            .Where(o => SelectedStation == "Todas" || o.Station.Equals(SelectedStation, StringComparison.OrdinalIgnoreCase))
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
            ? $"✔️ {card.TableNumber} entregada · se archiva en {_settings.ArchiveDelaySeconds}s"
            : null);

    private Task UndoAsync(KitchenOrderCardViewModel card) =>
        RunAsync(card, svc => svc.UndoDeliverAsync(card.Id), _ => $"↩ {card.TableNumber} restaurada");

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
            StatusMessage = $"⚠️ No se pudo imprimir {card.TableNumber} (guardado como TXT)";
            return;
        }
        await RunAsync(card, svc => svc.MarkPrintedAsync(card.Id), _ => $"🖨️ {card.TableNumber} impresa");
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
        catch (Exception ex)
        {
            StatusMessage = $"⚠️ {ex.Message}";
            await LoadOrdersAsync(); // resync with DB
        }
    }
}
