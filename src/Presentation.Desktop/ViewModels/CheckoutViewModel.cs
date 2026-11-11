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
using Infrastructure.Data.Seed;
using Infrastructure.Hardware.Printers;
using Infrastructure.Hardware.Scales;
using Microsoft.EntityFrameworkCore;

namespace Presentation.Desktop.ViewModels;

public partial class CheckoutViewModel : ObservableObject
{
    private readonly PosDbContext _dbContext;
    private readonly IPrinterService _printerService;
    private readonly IScaleService _scaleService;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private decimal _scaleWeightKg = 0m;

    [ObservableProperty]
    private string _statusMessage = "Listo para vender";

    [ObservableProperty]
    private bool _isPaymentModalOpen = false;

    [ObservableProperty]
    private decimal _tenderedAmount = 0m;

    [ObservableProperty]
    private decimal _changeAmount = 0m;

    [ObservableProperty]
    private PaymentMethod _selectedPaymentMethod = PaymentMethod.Cash;

    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<Product> FilteredProducts { get; } = new();
    public ObservableCollection<CartItemDto> CartItems { get; } = new();

    private readonly System.Collections.Generic.List<Product> _allProducts = new();

    public decimal Subtotal => CartItems.Sum(x => x.Subtotal);
    public decimal TaxTotal => CartItems.Sum(x => x.TaxAmount);
    public decimal Total => CartItems.Sum(x => x.Total);
    public int TotalItems => CartItems.Count;

    public CheckoutViewModel()
    {
        _dbContext = new PosDbContext();
        _printerService = new MockPrinterService();
        _scaleService = new MockScaleService();

        _scaleService.WeightChanged += (s, e) =>
        {
            ScaleWeightKg = e.WeightKg;
        };
        _scaleService.StartListening();

        _ = InitializeAsync();
    }

    public async Task InitializeAsync()
    {
        await DbInitializer.InitializeAsync(_dbContext);
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        var categories = await _dbContext.Categories.OrderBy(c => c.SortOrder).ToListAsync();
        Categories.Clear();
        foreach (var c in categories)
        {
            Categories.Add(c);
        }

        _allProducts.Clear();
        _allProducts.AddRange(await _dbContext.Products.Include(p => p.Category).Where(p => p.IsActive).ToListAsync());
        
        FilterProducts();
    }

    partial void OnSearchQueryChanged(string value) => FilterProducts();
    partial void OnSelectedCategoryChanged(Category? value) => FilterProducts();

    private void FilterProducts()
    {
        var filtered = _allProducts.AsEnumerable();

        if (SelectedCategory != null)
        {
            filtered = filtered.Where(p => p.CategoryId == SelectedCategory.Id);
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.ToLower();
            filtered = filtered.Where(p => p.Name.ToLower().Contains(q) || (p.Barcode != null && p.Barcode.Contains(q)) || p.Sku.ToLower().Contains(q));
        }

        FilteredProducts.Clear();
        foreach (var p in filtered)
        {
            FilteredProducts.Add(p);
        }
    }

    [RelayCommand]
    public void SelectCategory(Category? category)
    {
        SelectedCategory = SelectedCategory == category ? null : category;
    }

    [RelayCommand]
    public void AddProduct(Product product)
    {
        var existing = CartItems.FirstOrDefault(x => x.ProductId == product.Id);
        if (existing != null)
        {
            if (product.IsWeighed)
            {
                existing.WeightKg = (existing.WeightKg ?? 1) + (ScaleWeightKg > 0 ? ScaleWeightKg : 0.5m);
            }
            else
            {
                existing.Quantity += 1;
            }
        }
        else
        {
            CartItems.Add(new CartItemDto
            {
                ProductId = product.Id,
                Sku = product.Sku,
                Name = product.Name,
                UnitPrice = product.Price,
                Quantity = 1,
                IsWeighed = product.IsWeighed,
                WeightKg = product.IsWeighed ? (ScaleWeightKg > 0 ? ScaleWeightKg : 1.0m) : null,
                TaxRate = 0.19m
            });
        }

        NotifyCartTotalsChanged();
        StatusMessage = $"Agregado: {product.Name}";
    }

    [RelayCommand]
    public void IncrementItem(CartItemDto item)
    {
        if (item.IsWeighed)
        {
            item.WeightKg = (item.WeightKg ?? 1) + 0.5m;
        }
        else
        {
            item.Quantity += 1;
        }
        NotifyCartTotalsChanged();
    }

    [RelayCommand]
    public void DecrementItem(CartItemDto item)
    {
        if (item.IsWeighed)
        {
            if ((item.WeightKg ?? 1) > 0.5m)
                item.WeightKg -= 0.5m;
            else
                CartItems.Remove(item);
        }
        else
        {
            if (item.Quantity > 1)
                item.Quantity -= 1;
            else
                CartItems.Remove(item);
        }
        NotifyCartTotalsChanged();
    }

    [RelayCommand]
    public void RemoveItem(CartItemDto item)
    {
        CartItems.Remove(item);
        NotifyCartTotalsChanged();
    }

    [RelayCommand]
    public void ClearCart()
    {
        CartItems.Clear();
        NotifyCartTotalsChanged();
        StatusMessage = "Venta cancelada / carrito limpio";
    }

    [RelayCommand]
    public async Task OpenCashDrawerAsync()
    {
        await _printerService.OpenCashDrawerAsync();
        StatusMessage = "Cajón monedero abierto";
    }

    [RelayCommand]
    public void StartPayment()
    {
        if (!CartItems.Any())
        {
            StatusMessage = "Agregue productos antes de cobrar";
            return;
        }

        TenderedAmount = Total;
        ChangeAmount = 0;
        IsPaymentModalOpen = true;
    }

    [RelayCommand]
    public void ClosePaymentModal()
    {
        IsPaymentModalOpen = false;
    }

    [RelayCommand]
    public void AppendNumpad(string digit)
    {
        var currentStr = TenderedAmount == 0 ? "" : TenderedAmount.ToString("0");
        if (currentStr.Length < 9)
        {
            currentStr += digit;
            if (decimal.TryParse(currentStr, out var val))
            {
                TenderedAmount = val;
                ChangeAmount = Math.Max(0, TenderedAmount - Total);
            }
        }
    }

    [RelayCommand]
    public void ClearNumpad()
    {
        TenderedAmount = 0;
        ChangeAmount = 0;
    }

    [RelayCommand]
    public async Task CompleteSaleAsync()
    {
        if (TenderedAmount < Total && SelectedPaymentMethod == PaymentMethod.Cash)
        {
            StatusMessage = "El monto ingresado es menor al total";
            return;
        }

        var order = new Order
        {
            OrderNumber = $"T-{DateTime.UtcNow:yyyyMMdd}-{DateTime.UtcNow.Ticks % 10000:0000}",
            Status = OrderStatus.Completed,
            Subtotal = Subtotal,
            TaxTotal = TaxTotal,
            Total = Total,
            Notes = "Venta POS Standalone"
        };

        foreach (var item in CartItems)
        {
            order.Items.Add(new OrderItem
            {
                ProductId = item.ProductId,
                ProductName = item.Name,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                WeightKg = item.WeightKg,
                TaxRate = item.TaxRate,
                TaxAmount = item.TaxAmount,
                Subtotal = item.Subtotal,
                Total = item.Total
            });
        }

        order.Payments.Add(new Payment
        {
            Method = SelectedPaymentMethod,
            Amount = Total,
            TenderedAmount = TenderedAmount,
            ChangeAmount = ChangeAmount
        });

        // Add Outbox Message for Offline-First sync
        var outboxMsg = new OutboxMessage
        {
            EventType = "OrderCompleted",
            AggregateType = "Order",
            AggregateId = order.Id,
            PayloadJson = $"{{\"orderId\":\"{order.Id}\", \"orderNumber\":\"{order.OrderNumber}\", \"total\":{order.Total}}}",
            Status = SyncStatus.Pending
        };

        _dbContext.Orders.Add(order);
        _dbContext.OutboxMessages.Add(outboxMsg);
        await _dbContext.SaveChangesAsync();

        // Print receipt
        var receipt = new ReceiptDto
        {
            OrderNumber = order.OrderNumber,
            CashierName = "Cajero Principal",
            Subtotal = order.Subtotal,
            TaxTotal = order.TaxTotal,
            Total = order.Total,
            PaymentMethodName = SelectedPaymentMethod.ToString(),
            TenderedAmount = TenderedAmount,
            ChangeAmount = ChangeAmount,
            Items = CartItems.Select(i => new ReceiptItemDto
            {
                Name = i.Name,
                Quantity = i.IsWeighed ? (i.WeightKg ?? 1) : i.Quantity,
                UnitPrice = i.UnitPrice,
                Total = i.Total
            }).ToList()
        };

        await _printerService.PrintReceiptAsync(receipt);
        if (SelectedPaymentMethod == PaymentMethod.Cash)
        {
            await _printerService.OpenCashDrawerAsync();
        }

        IsPaymentModalOpen = false;
        CartItems.Clear();
        NotifyCartTotalsChanged();
        StatusMessage = $"¡Venta {order.OrderNumber} completada con éxito!";
    }

    private void NotifyCartTotalsChanged()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(TaxTotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalItems));
    }
}
