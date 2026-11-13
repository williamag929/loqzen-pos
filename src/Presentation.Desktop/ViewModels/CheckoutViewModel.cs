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
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

public partial class CheckoutViewModel : ObservableObject
{
    private readonly ICategoryService _categoryService;
    private readonly IProductService _productService;
    private readonly ISalesService _salesService;
    private readonly IKitchenService _kitchenService;
    private readonly ICashShiftService _cashShiftService;
    private readonly IPrinterService _printerService;
    private readonly IScaleService _scaleService;
    private readonly IBarcodeScannerService _scannerService;
    private readonly IManagerAuthPrompt _authPrompt;
    private readonly IUserSession _session;

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

    /// <summary>"Cambio a entregar" screen after a paid sale; null = hidden. Cleared by Nueva venta or by adding a product.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaleCompletedVisible))]
    private SaleSummaryViewModel? _lastSale;

    public bool IsSaleCompletedVisible => LastSale != null;

    [RelayCommand]
    public void DismissSale() => LastSale = null;

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
        ICategoryService categoryService,
        IProductService productService,
        ISalesService salesService,
        IKitchenService kitchenService,
        ICashShiftService cashShiftService,
        IPrinterService printerService,
        IScaleService scaleService,
        IBarcodeScannerService scannerService,
        IManagerAuthPrompt authPrompt,
        IUserSession session)
    {
        _categoryService = categoryService;
        _productService = productService;
        _salesService = salesService;
        _kitchenService = kitchenService;
        _cashShiftService = cashShiftService;
        _printerService = printerService;
        _scaleService = scaleService;
        _scannerService = scannerService;
        _authPrompt = authPrompt;
        _session = session;
        _session.Changed += () => RebuildReceiptPreview(); // cashier name on the live receipt

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
            _currentCashierName = currentShift?.CashierName;
            
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
        // Through the master services (no DbContext in the ViewModel): categories come ordered by
        // SortOrder; products come with Category and Tax loaded (needed for tiles and tax lines).
        var categories = await _categoryService.GetAllAsync();
        Categories.Clear();
        foreach (var c in categories)
        {
            Categories.Add(c);
        }

        var products = await _productService.GetAllAsync();
        _allProducts.Clear();
        _allProducts.AddRange(products.Where(p => p.IsActive));

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
        LastSale = null; // next sale starts: hide the change screen (also when the scanner fires)

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

    /// <summary>Adds a return (credit) line for the item. Needs manager approval when configured.</summary>
    [RelayCommand]
    public async Task RemoveItemAsync(CartItemDto item)
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

        var lineValue = item.UnitPrice * (item.IsWeighed ? Math.Abs(item.WeightKg ?? 1m) : 1m);
        var auth = await _authPrompt.RequestAsync(AuthorizedAction.ReturnItem, $"Devolver {item.Name}", lineValue, item.Name);
        if (!auth.IsAllowed)
        {
            StatusMessage = "❌ Devolución no autorizada";
            return;
        }

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
        StatusMessage = auth.ManagerName is { } manager
            ? $"🔄 Devolución agregada: {item.Name} · autorizó {manager}"
            : $"🔄 Devolución agregada: {item.Name}";
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
    public async Task OpenDiscountModalAsync()
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
            Reason = "Descuento promocional"
        };
        var discountAmount = Math.Min(discount.CalculateDiscount(Subtotal), Subtotal);

        var auth = await _authPrompt.RequestAsync(AuthorizedAction.ApplyDiscount, "Descuento 10%", discountAmount);
        if (!auth.IsAllowed)
        {
            StatusMessage = "❌ Descuento no autorizado";
            return;
        }
        discount.AuthorizedBy = auth.ManagerName ?? _session.CurrentUser?.Name ?? "Cajero";

        DiscountTotal = discountAmount;
        NotifyCartTotalsChanged();
        StatusMessage = auth.ManagerName is { } manager
            ? $"Descuento aplicado: ${DiscountTotal:N0} · autorizó {manager}"
            : $"Descuento aplicado: ${DiscountTotal:N0}";
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
        // Not in the default ManagerRequiredFor list → normally no prompt.
        var auth = await _authPrompt.RequestAsync(AuthorizedAction.OpenCashDrawer, "Abrir cajón sin venta");
        if (!auth.IsAllowed)
        {
            StatusMessage = "❌ Apertura de cajón no autorizada";
            return;
        }
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

    /// <summary>"Exacto" key: customer pays the exact total (no change).</summary>
    [RelayCommand]
    public void ExactAmount()
    {
        TenderedAmount = Total;
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

            // 4. Create KitchenOrder for KDS (through the service: Outbox + live event for the KDS/LAN hub).
            //    Returns never go to the kitchen; a returns-only sale creates no ticket (CreateAsync rejects empty orders).
            var kitchenItems = CartItems.Where(i => !i.IsReturn).ToList();
            if (kitchenItems.Count > 0)
            {
                var kitchenOrder = new KitchenOrder
                {
                    OrderNumber = paymentResult.OrderNumber,
                    OrderId = paymentResult.OrderId,
                    TableNumber = "Mostrador Principal",
                    WaiterName = "Cajero",
                    Station = "Cocina Caliente",
                    Status = KitchenOrderStatus.Pending
                };

                foreach (var item in kitchenItems)
                {
                    kitchenOrder.Items.Add(new KitchenOrderItem
                    {
                        ProductId = item.ProductId,
                        ProductName = item.Name,
                        Quantity = item.Quantity,
                        Notes = item.IsWeighed ? $"Peso: {item.WeightKg:F2}kg" : null
                    });
                }

                await _kitchenService.CreateAsync(kitchenOrder);
            }

            // 5. Print receipt
            // Same builder as the on-screen preview, so the paper matches what the cashier saw.
            var receipt = Core.Application.Receipts.ReceiptDtoFactory.FromCart(CartItems, DiscountTotal, paymentResult.OrderNumber, CashierLabel);
            receipt.PaymentMethodName = Core.Application.Receipts.ReceiptDtoFactory.PaymentName(SelectedPaymentMethod);
            receipt.TenderedAmount = SelectedPaymentMethod == PaymentMethod.Cash ? TenderedAmount : 0;
            receipt.ChangeAmount = paymentResult.ChangeAmount;

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
            // Keep the change on screen until the cashier taps "Nueva venta": closing the modal used to
            // hide it immediately and she had to read it off the receipt.
            LastSale = new SaleSummaryViewModel(completedOrderNumber, completedTotal,
                SelectedPaymentMethod == PaymentMethod.Cash ? TenderedAmount : completedTotal,
                completedChange, SelectedPaymentMethod, null, DismissSale);
            StatusMessage = $"✅ Venta {completedOrderNumber} completada · Cambio {Core.Application.Receipts.ReceiptFormatter.Cop(completedChange)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error procesando venta: {ex.Message}";
        }
    }

    /// <summary>Live on-screen receipt: same formatter as the printer → what you see is what prints.</summary>
    public ObservableCollection<Core.Application.Receipts.ReceiptLine> ReceiptPreview { get; } = new();

    [ObservableProperty] private bool _isReceiptPreviewVisible = true;

    /// <summary>What the cashier chose with the 🧾 button; the view may hide the column on narrow windows.</summary>
    public bool ReceiptPreviewWanted { get; private set; } = true;

    [RelayCommand]
    private void ToggleReceiptPreview()
    {
        ReceiptPreviewWanted = !ReceiptPreviewWanted;
        IsReceiptPreviewVisible = ReceiptPreviewWanted;
    }

    /// <summary>Called by the view on resize: below the threshold the tirilla column is hidden so the
    /// catalog + ticket (and the payment modal) always fit; above it, the cashier's choice is restored.</summary>
    public void ApplyViewportWidth(double width)
    {
        IsReceiptPreviewVisible = width >= ReceiptPreviewMinWidth && ReceiptPreviewWanted;
    }

    public const double ReceiptPreviewMinWidth = 1150;

    private void RebuildReceiptPreview()
    {
        ReceiptPreview.Clear();
        if (CartItems.Count == 0) return;
        var dto = Core.Application.Receipts.ReceiptDtoFactory.FromCart(CartItems, DiscountTotal, "(en curso)", CashierLabel);
        foreach (var line in Core.Application.Receipts.ReceiptFormatter.Format(dto, isPreview: true))
            ReceiptPreview.Add(line);
    }

    private string CashierLabel => _session.CurrentUser?.Name ?? _currentCashierName ?? "Cajero";
    private string? _currentCashierName;

    private void NotifyCartTotalsChanged()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(TaxTotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalItems));
        RebuildReceiptPreview();

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
