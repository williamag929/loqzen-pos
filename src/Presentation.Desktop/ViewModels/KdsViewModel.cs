using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Hardware.Printers;
using Microsoft.EntityFrameworkCore;

namespace Presentation.Desktop.ViewModels;

public partial class KitchenOrderCardViewModel : ObservableObject
{
    private readonly KitchenOrder _entity;
    private readonly PosDbContext _dbContext;
    private readonly IPrinterService _printerService;
    private readonly Action _onStatusChanged;

    public Guid Id => _entity.Id;
    public string OrderNumber => _entity.OrderNumber;
    public string TableNumber => _entity.TableNumber;
    public string WaiterName => _entity.WaiterName;
    public string Station => _entity.Station;
    public string? GeneralNotes => _entity.GeneralNotes;
    public DateTime CreatedAt => _entity.CreatedAt;

    [ObservableProperty]
    private KitchenOrderStatus _status;

    [ObservableProperty]
    private int _elapsedMinutes;

    [ObservableProperty]
    private string _urgencyColorHex = "#10B981";

    public ObservableCollection<KitchenOrderItem> Items { get; } = new();

    public string StatusText => Status switch
    {
        KitchenOrderStatus.Pending => "PENDIENTE",
        KitchenOrderStatus.InPreparation => "EN PREPARACIÓN",
        KitchenOrderStatus.Ready => "LISTO",
        KitchenOrderStatus.Delivered => "ENTREGADO",
        _ => "CANCELADO"
    };

    public string ActionButtonText => Status switch
    {
        KitchenOrderStatus.Pending => "👨‍🍳 PREPARAR",
        KitchenOrderStatus.InPreparation => "🔔 MARCAR LISTO",
        KitchenOrderStatus.Ready => "✔️ ENTREGAR",
        _ => "ARCHIVAR"
    };

    public string ActionButtonColorHex => Status switch
    {
        KitchenOrderStatus.Pending => "#3B82F6",      // Blue
        KitchenOrderStatus.InPreparation => "#10B981",// Green
        KitchenOrderStatus.Ready => "#6366F1",        // Indigo
        _ => "#64748B"
    };

    public KitchenOrderCardViewModel(KitchenOrder entity, PosDbContext dbContext, IPrinterService printerService, Action onStatusChanged)
    {
        _entity = entity;
        _dbContext = dbContext;
        _printerService = printerService;
        _onStatusChanged = onStatusChanged;
        _status = entity.Status;

        foreach (var item in entity.Items)
        {
            Items.Add(item);
        }

        UpdateElapsed();
    }

    public void UpdateElapsed()
    {
        var elapsed = (DateTime.UtcNow - _entity.CreatedAt).TotalMinutes;
        ElapsedMinutes = (int)Math.Max(0, elapsed);

        if (ElapsedMinutes < 5)
        {
            UrgencyColorHex = "#10B981"; // Verde
        }
        else if (ElapsedMinutes <= 15)
        {
            UrgencyColorHex = "#F59E0B"; // Amarillo
        }
        else
        {
            UrgencyColorHex = "#EF4444"; // Rojo
        }
    }

    [RelayCommand]
    public async Task AdvanceStatusAsync()
    {
        var nextStatus = Status switch
        {
            KitchenOrderStatus.Pending => KitchenOrderStatus.InPreparation,
            KitchenOrderStatus.InPreparation => KitchenOrderStatus.Ready,
            KitchenOrderStatus.Ready => KitchenOrderStatus.Delivered,
            _ => KitchenOrderStatus.Delivered
        };

        Status = nextStatus;
        _entity.Status = nextStatus;

        try
        {
            var dbOrder = await _dbContext.KitchenOrders.FindAsync(_entity.Id);
            if (dbOrder != null)
            {
                dbOrder.Status = nextStatus;
                await _dbContext.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KDS Order Status Update Error]: {ex.Message}");
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionButtonText));
        OnPropertyChanged(nameof(ActionButtonColorHex));
        _onStatusChanged?.Invoke();
    }

    [RelayCommand]
    public async Task PrintTicketAsync()
    {
        var receipt = new ReceiptDto
        {
            OrderNumber = OrderNumber,
            Date = CreatedAt,
            CashierName = WaiterName,
            StoreName = $"KDS: {Station}",
            StoreAddress = TableNumber,
            Items = Items.Select(i => new ReceiptItemDto
            {
                Name = $"{i.ProductName} {(string.IsNullOrEmpty(i.Notes) ? "" : $"[{i.Notes}]")}",
                Quantity = i.Quantity,
                UnitPrice = 0,
                Total = 0
            }).ToList()
        };

        await _printerService.PrintKitchenTicketAsync(receipt, Station);
    }
}

public partial class KdsViewModel : ObservableObject
{
    private readonly PosDbContext _dbContext;
    private readonly IPrinterService _printerService;
    private readonly System.Timers.Timer _refreshTimer;

    [ObservableProperty]
    private string _selectedStation = "Todas";

    [ObservableProperty]
    private int _pendingCount = 0;

    [ObservableProperty]
    private int _inPrepCount = 0;

    [ObservableProperty]
    private int _readyCount = 0;

    [ObservableProperty]
    private string _kdsTitle = "KDS - Sistema de Pantallas de Cocina";

    public ObservableCollection<string> Stations { get; } = new()
    {
        "Todas",
        "Cocina Caliente",
        "Barra",
        "Parrilla"
    };

    public ObservableCollection<KitchenOrderCardViewModel> FilteredOrders { get; } = new();
    private readonly ObservableCollection<KitchenOrderCardViewModel> _allOrders = new();

    public KdsViewModel()
    {
        _dbContext = new PosDbContext();
        _printerService = new MockPrinterService();

        _ = LoadOrdersAsync();

        // Refresh timers every 10 seconds to update green/yellow/red urgency indicators
        _refreshTimer = new System.Timers.Timer(10000);
        _refreshTimer.Elapsed += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var order in _allOrders)
                {
                    order.UpdateElapsed();
                }
            });
        };
        _refreshTimer.Start();
    }

    public async Task LoadOrdersAsync()
    {
        var orders = await _dbContext.KitchenOrders
            .Include(o => o.Items)
            .Where(o => o.Status != KitchenOrderStatus.Delivered && o.Status != KitchenOrderStatus.Cancelled)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync();

        _allOrders.Clear();
        foreach (var order in orders)
        {
            _allOrders.Add(new KitchenOrderCardViewModel(order, _dbContext, _printerService, RecalculateCounts));
        }

        FilterOrders();
        RecalculateCounts();
    }

    partial void OnSelectedStationChanged(string value) => FilterOrders();

    [RelayCommand]
    public void SelectStation(string station)
    {
        SelectedStation = station;
    }

    private void FilterOrders()
    {
        FilteredOrders.Clear();
        var query = _allOrders.AsEnumerable();

        if (SelectedStation != "Todas")
        {
            query = query.Where(o => o.Station.Equals(SelectedStation, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            FilteredOrders.Add(item);
        }
    }

    private void RecalculateCounts()
    {
        PendingCount = _allOrders.Count(o => o.Status == KitchenOrderStatus.Pending);
        InPrepCount = _allOrders.Count(o => o.Status == KitchenOrderStatus.InPreparation);
        ReadyCount = _allOrders.Count(o => o.Status == KitchenOrderStatus.Ready);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await LoadOrdersAsync();
    }
}
