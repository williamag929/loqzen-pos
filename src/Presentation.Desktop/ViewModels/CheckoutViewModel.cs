using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Presentation.Desktop.ViewModels;

public partial class CheckoutViewModel : ObservableObject
{
    private readonly PosDbContext _dbContext;
    private readonly ISalesService _salesService;
    private readonly ICashShiftService _cashShiftService;
    private readonly IPrinterService _printerService;
    private readonly IScaleService _scaleService;
    private readonly IBarcodeScannerService _scannerService;

    // Current order being built
    private Guid? _currentOrderId;
    private Guid? _currentCashShiftId;

    public CustomerDisplayViewModel? CustomerDisplay { get; set; }

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
    [NotifyPropertyChangedFor(nameof(IsCashPayment))]
    [NotifyPropertyChangedFor(nameof(IsCardPayment))]
    [NotifyPropertyChangedFor(nameof(IsTransferPayment))]
    private PaymentMethod _selectedPaymentMethod = PaymentMethod.Cash;

    [ObservableProperty]
    private bool _isShiftOpen = false;

    [ObservableProperty]
    private string _shiftWarning = string.Empty;

    [ObservableProperty]
    private decimal _discountTotal = 0m;

    public bool IsCashPayment => SelectedPaymentMethod == PaymentMethod.Cash;
    public bool IsCardPayment => SelectedPaymentMethod == PaymentMethod.Card;
    public bool IsTransferPayment => SelectedPaymentMethod == PaymentMethod.Transfer;

    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<Product> FilteredProducts { get; } = new();
    public ObservableCollection<CartItemDto> CartItems { get; } = new();

    private readonly System.Collections.Generic.List<Product> _allProducts = new();

    public decimal Subtotal => CartItems.Sum(x => x.Subtotal);
    public decimal TaxTotal => CartItems.Sum(x => x.TaxAmount);
    public decimal Total => Math.Max(0, Subtotal + TaxTotal - DiscountTotal);
    public int TotalItems => CartItems.Count;

    public CheckoutViewModel(
        PosDbContext dbContext,
        ISalesService salesService,
        ICashShiftService cashShiftService,
        IPrinterService printerService,
        IScaleService scaleService,
        IBarcodeScannerService scannerService)
    {
        _dbContext = dbContext;
        _salesService = salesService;
        _cashShiftService = cashShiftService;
        _printerService = printerService;
        _scaleService = scaleService;
        _scannerService = scannerService;

        _scaleService.WeightChanged += (s, e) =>
        {
            ScaleWeightKg = e.WeightKg;
        };
        _scaleService.StartListening();

        _scannerService.BarcodeScanned += (s, barcode) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AddProductByBarcode(barcode);
            });
        };
        _scannerService.StartListening();

        _ = InitializeAsync();
    }

    [RelayCommand]
    public void ScanBarcode(string barcode)
    {
        AddProductByBarcode(barcode);
    }

    [RelayCommand]
    public void TriggerSampleScan()
    {
        var sampleBarcodes = new[] { "7701234567890", "7701234567891", "7701234567892", "7701234567894" };
        var randomBarcode = sampleBarcodes[Random.Shared.Next(sampleBarcodes.Length)];
        AddProductByBarcode(randomBarcode);
    }

    public void AddProductByBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;

        var product = _allProducts.FirstOrDefault(p => 
            (p.Barcode != null && p.Barcode.Equals(barcode.Trim(), StringComparison.OrdinalIgnoreCase)) || 
            p.Sku.Equals(barcode.Trim(), StringComparison.OrdinalIgnoreCase));

        if (product != null)
        {
            AddProduct(product);
            StatusMessage = $"⚡ Escaneado: {product.Name} (Código: {barcode})";
        }
        else
        {
            StatusMessage = $"⚠️ Código de barras no encontrado: {barcode}";
        }
    }

    public async Task InitializeAsync()
    {
        await LoadDataAsync();
        await CheckCashShiftStatusAsync();
    }

    /// <summary>
    /// Checks if there's an open cash shift and updates status accordingly.
    /// </summary>
    public async Task CheckCashShiftStatusAsync()
    {
        try
        {
            var currentShift = await _cashShiftService.GetCurrentShiftAsync();
            IsShiftOpen = currentShift != null;
            _currentCashShiftId = currentShift?.Id;
            
            if (IsShiftOpen && currentShift != null)
            {
                ShiftWarning = string.Empty;
                StatusMessage = $"✅ Turno #{currentShift.ShiftNumber} abierto – {currentShift.CashierName}";
            }
            else
            {
                ShiftWarning = "⚠️ Debe abrir un turno antes de registrar ventas";
                StatusMessage = "🔴 Sin turno abierto";
            }
        }
        catch (Exception ex)
        {
            ShiftWarning = $"Error verificando turno: {ex.Message}";
        }
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
        _allProducts.AddRange(await _dbContext.Products.Include(p => p.Category).Include(p => p.Tax).Where(p => p.IsActive).ToListAsync());
        
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
        // Warn if no shift is open but still allow adding
        if (!IsShiftOpen)
        {
            ShiftWarning = "⚠️ Sin turno abierto – abra un turno antes de cobrar";
        }

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
            // Use actual tax rate from product
            var taxRate = product.Tax?.Rate ?? 0.19m;
            CartItems.Add(new CartItemDto
            {
                ProductId = product.Id,
                Sku = product.Sku,
                Name = product.Name,
                UnitPrice = product.Price,
                Quantity = 1,
                IsWeighed = product.IsWeighed,
                WeightKg = product.IsWeighed ? (ScaleWeightKg > 0 ? ScaleWeightKg : 1.0m) : null,
                TaxRate = taxRate
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
        // If item is already a return, just remove it
        if (item.IsReturn)
        {
            CartItems.Remove(item);
            NotifyCartTotalsChanged();
            StatusMessage = "Devolución removida";
            return;
        }

        // Validate: can't return more than added
        var normalQty = CartItems.Where(i => i.ProductId == item.ProductId && !i.IsReturn)
                                 .Sum(i => i.IsWeighed ? (i.WeightKg ?? i.Quantity) : i.Quantity);
        var returnedQty = CartItems.Where(i => i.ProductId == item.ProductId && i.IsReturn)
                                    .Sum(i => Math.Abs(i.IsWeighed ? (i.WeightKg ?? i.Quantity) : i.Quantity));

        if (returnedQty >= normalQty)
        {
            StatusMessage = "❌ No puede devolver más de lo agregado en esta venta";
            return;
        }

        // TODO: Manager authorization modal (Fase 2)
        // For now, just add return item

        // Add return item with negative quantity
        var returnItem = new CartItemDto
        {
            ProductId = item.ProductId,
            Sku = item.Sku,
            Name = item.Name,
            UnitPrice = item.UnitPrice,
            Quantity = -1m,
            IsWeighed = item.IsWeighed,
            // Weighed lines are priced by WeightKg, so the weight itself must be negative
            // (previously positive -> the "return" was CHARGED instead of credited).
            WeightKg = item.IsWeighed ? -Math.Abs(item.WeightKg ?? 1m) : null,
            TaxRate = item.TaxRate,
            IsReturn = true
        };

        CartItems.Add(returnItem);
        NotifyCartTotalsChanged();
        StatusMessage = $"🔄 Devolución agregada: {item.Name}";
    }

    [RelayCommand]
    public void ClearCart()
    {
        CartItems.Clear();
        DiscountTotal = 0m;
        NotifyCartTotalsChanged();
        StatusMessage = "Venta cancelada / carrito limpio";
    }

    [RelayCommand]
    public void OpenDiscountModal()
    {
        // Simple implementation - applies 10% discount
        // TODO: Implement full modal with percentage/fixed amount UI
        if (Subtotal == 0)
        {
            StatusMessage = "Agregue productos antes de aplicar descuento";
            return;
        }

        var discount = new DiscountDto
        {
            Type = DiscountType.Percentage,
            Value = 10m,
            Reason = "Descuento promocional",
            AuthorizedBy = "Cajero"
        };

        var discountAmount = discount.CalculateDiscount(Subtotal);
        DiscountTotal = Math.Min(discountAmount, Subtotal);
        NotifyCartTotalsChanged();
        StatusMessage = $"Descuento aplicado: ${DiscountTotal:N0}";
    }

    [RelayCommand]
    public void RemoveDiscount()
    {
        DiscountTotal = 0m;
        NotifyCartTotalsChanged();
        StatusMessage = "Descuento removido";
    }

    [RelayCommand]
    public async Task OpenCashDrawerAsync()
    {
        await _printerService.OpenCashDrawerAsync();
        StatusMessage = "Cajón monedero abierto";
    }

    [RelayCommand]
    public async Task StartPaymentAsync()
    {
        if (!CartItems.Any())
        {
            StatusMessage = "Agregue productos antes de cobrar";
            return;
        }

        // Check for open cash shift
        await CheckCashShiftStatusAsync();
        if (!IsShiftOpen || _currentCashShiftId == null)
        {
            StatusMessage = "❌ Debe abrir un turno de caja antes de procesar ventas";
            ShiftWarning = "⚠️ Vaya a 'Caja' y abra un turno primero";
            return;
        }

        TenderedAmount = Total;
        ChangeAmount = 0;
        IsPaymentModalOpen = true;
        NotifyCartTotalsChanged();
    }

    [RelayCommand]
    public void ClosePaymentModal()
    {
        IsPaymentModalOpen = false;
        NotifyCartTotalsChanged();
    }

    [RelayCommand]
    public void SelectPaymentMethod(string method)
    {
        if (Enum.TryParse<PaymentMethod>(method, out var pm))
        {
            SelectedPaymentMethod = pm;
            // For non-cash, set tendered to exact total (no change)
            if (pm != PaymentMethod.Cash)
            {
                TenderedAmount = Total;
                ChangeAmount = 0;
            }
        }
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
                NotifyCartTotalsChanged();
            }
        }
    }

    [RelayCommand]
    public void ClearNumpad()
    {
        TenderedAmount = 0;
        ChangeAmount = 0;
        NotifyCartTotalsChanged();
    }

    [RelayCommand]
    public async Task CompleteSaleAsync()
    {
        if (TenderedAmount < Total && SelectedPaymentMethod == PaymentMethod.Cash)
        {
            StatusMessage = "El monto ingresado es menor al total";
            return;
        }

        // Ensure we have an open cash shift
        if (_currentCashShiftId == null)
        {
            StatusMessage = "❌ Error: No hay turno de caja abierto";
            return;
        }

        try
        {
            // 1. Create order via ISalesService
            var orderDto = await _salesService.CreateOrderAsync(null, _currentCashShiftId.Value);
            _currentOrderId = orderDto.Id;

            // 2. Add all items to the order
            foreach (var item in CartItems)
            {
                await _salesService.AddItemAsync(
                    orderDto.Id,
                    item.ProductId,
                    item.Quantity,
                    item.WeightKg);
            }

            // 2.5. Apply discount if any
            if (DiscountTotal > 0)
            {
                await _salesService.ApplyDiscountAsync(orderDto.Id, DiscountTotal);
            }

            // 3. Process payment (this also creates OutboxMessage in same transaction)
            var paymentResult = await _salesService.ProcessPaymentAsync(
                orderDto.Id,
                SelectedPaymentMethod,
                Total,
                TenderedAmount);

            if (!paymentResult.Success)
            {
                StatusMessage = $"❌ Error en pago: {paymentResult.ErrorMessage}";
                return;
            }

            // 4. Create KitchenOrder for KDS
            var kitchenOrder = new KitchenOrder
            {
                OrderNumber = paymentResult.OrderNumber,
                OrderId = paymentResult.OrderId,
                TableNumber = "Mostrador Principal",
                WaiterName = "Cajero",
                Station = "Cocina Caliente",
                Status = KitchenOrderStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            foreach (var item in CartItems.Where(i => !i.IsReturn)) // returns never go to the kitchen
            {
                kitchenOrder.Items.Add(new KitchenOrderItem
                {
                    ProductId = item.ProductId,
                    ProductName = item.Name,
                    Quantity = item.Quantity,
                    Notes = item.IsWeighed ? $"Peso: {item.WeightKg:F2}kg" : null
                });
            }

            _dbContext.KitchenOrders.Add(kitchenOrder);
            await _dbContext.SaveChangesAsync();

            // 5. Print receipt
            var receipt = new ReceiptDto
            {
                OrderNumber = paymentResult.OrderNumber,
                CashierName = "Cajero Principal",
                Subtotal = Subtotal,
                TaxTotal = TaxTotal,
                Total = Total,
                PaymentMethodName = SelectedPaymentMethod.ToString(),
                TenderedAmount = TenderedAmount,
                ChangeAmount = paymentResult.ChangeAmount,
                Items = CartItems.Select(i => new ReceiptItemDto
                {
                    Name = i.Name,
                    Quantity = i.IsWeighed ? (i.WeightKg ?? 1) : i.Quantity,
                    UnitPrice = i.UnitPrice,
                    Total = i.Total,
                    IsReturn = i.IsReturn
                }).ToList()
            };

            await _printerService.PrintReceiptAsync(receipt);
            
            // 6. Open cash drawer for cash payments
            if (SelectedPaymentMethod == PaymentMethod.Cash)
            {
                await _printerService.OpenCashDrawerAsync();
            }

            var completedOrderNumber = paymentResult.OrderNumber;
            var completedTotal = Total;
            var completedChange = paymentResult.ChangeAmount;

            IsPaymentModalOpen = false;
            CartItems.Clear();
            _currentOrderId = null;
            NotifyCartTotalsChanged();
            
            CustomerDisplay?.NotifySaleCompleted(completedOrderNumber, completedTotal, completedChange);
            StatusMessage = $"✅ ¡Venta {completedOrderNumber} completada y enviada a Cocina (KDS)!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error procesando venta: {ex.Message}";
        }
    }

    private void NotifyCartTotalsChanged()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(TaxTotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalItems));

        CustomerDisplay?.UpdateFromCheckout(
            CartItems,
            Subtotal,
            TaxTotal,
            Total,
            IsPaymentModalOpen,
            TenderedAmount,
            ChangeAmount,
            SelectedPaymentMethod.ToString(),
            DiscountTotal);
    }
}
