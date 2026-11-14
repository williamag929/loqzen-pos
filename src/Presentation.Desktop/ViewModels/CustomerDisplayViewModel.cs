using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Application.DTOs;
using Presentation.Desktop.Localization;

namespace Presentation.Desktop.ViewModels;

public enum CustomerDisplayState
{
    Idle = 0,
    Scanning = 1,
    Payment = 2,
    Completed = 3
}

/// <summary>
/// Second-screen ViewModel (singleton). Fed by TWO screens: the quick-sale checkout
/// (<see cref="UpdateFromCheckout"/>) and the restaurant comanda (<see cref="UpdateFromComanda"/>).
/// Arbitration is deliberately simple: the display shows whichever screen updated it LAST.
/// The only nuance: when a comanda is cleared while the checkout cart still has items, the
/// checkout's last snapshot is replayed so a sale in progress is not blanked out.
/// </summary>
public partial class CustomerDisplayViewModel : ObservableObject
{
    private static Localizer L => Localizer.Instance;

    /// <summary>"Su compra" / "Your purchase" (quick sale header), in the cashier's current language.</summary>
    private static string DefaultContextTitle => L.T("Display.YourPurchase");

    /// <summary>Comanda shown right now (table, number); null = quick-sale header. Kept so the title can be re-rendered on a language switch.</summary>
    private (string TableName, int Number)? _comanda;

    /// <summary>Last checkout call, replayed when a comanda is cleared (see class remarks).</summary>
    private sealed record CheckoutSnapshot(ObservableCollection<CartItemDto> Items, decimal Subtotal, decimal TaxTotal,
        decimal Total, bool IsPaymentOpen, decimal Tendered, decimal Change, string PaymentMethod, decimal DiscountTotal);
    private CheckoutSnapshot? _checkout;

    /// <summary>Header above the item list: "Su compra" (quick sale) or "Mesa 4 · Comanda #12".</summary>
    [ObservableProperty]
    private string _contextTitle = L.T("Display.YourPurchase");

    [ObservableProperty]
    private string _storeName = "LOQZEN POS & RESTAURANT";

    /// <summary>Marketing line under the store name; translated (the store name itself is data/brand and stays as-is).</summary>
    public string StoreTagline => L.T("Display.Tagline");

    [ObservableProperty]
    private CustomerDisplayState _state = CustomerDisplayState.Idle;

    [ObservableProperty]
    private decimal _subtotal = 0m;

    [ObservableProperty]
    private decimal _taxTotal = 0m;

    [ObservableProperty]
    private decimal _discountTotal = 0m;

    [ObservableProperty]
    private decimal _total = 0m;

    [ObservableProperty]
    private int _itemCount = 0;

    [ObservableProperty]
    private decimal _tenderedAmount = 0m;

    [ObservableProperty]
    private decimal _changeAmount = 0m;

    [ObservableProperty]
    private string _paymentMethodName = "Efectivo / Transferencia";

    [ObservableProperty]
    private string _lastCompletedOrderNumber = string.Empty;

    public ObservableCollection<CartItemDto> Items { get; } = new();

    public CustomerDisplayViewModel()
    {
        // Second screen the customer reads: follows the cashier's ES/EN choice live (XAML labels re-bind on their own;
        // the strings this VM composes are re-rendered here).
        L.Changed += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(StoreTagline));
        ContextTitle = _comanda is { } c ? L.T("Display.ComandaTitle", c.TableName, c.Number) : DefaultContextTitle;
    }

    public void UpdateFromCheckout(
        ObservableCollection<CartItemDto> cartItems, 
        decimal subtotal, 
        decimal taxTotal, 
        decimal total, 
        bool isPaymentOpen, 
        decimal tendered, 
        decimal change, 
        string paymentMethod,
        decimal discountTotal = 0m)
    {
        _checkout = new CheckoutSnapshot(cartItems, subtotal, taxTotal, total, isPaymentOpen, tendered, change, paymentMethod, discountTotal);
        _comanda = null;
        ContextTitle = DefaultContextTitle;

        Items.Clear();
        foreach (var item in cartItems)
        {
            Items.Add(item);
        }

        Subtotal = subtotal;
        TaxTotal = taxTotal;
        DiscountTotal = discountTotal;
        Total = total;
        ItemCount = cartItems.Count;
        TenderedAmount = tendered;
        ChangeAmount = change;
        PaymentMethodName = paymentMethod;

        if (cartItems.Count == 0)
        {
            State = CustomerDisplayState.Idle;
        }
        else if (isPaymentOpen)
        {
            State = CustomerDisplayState.Payment;
        }
        else
        {
            State = CustomerDisplayState.Scanning;
        }
    }

    /// <summary>
    /// Mirror the current comanda (billable items only, notes appended to the name). <paramref name="comanda"/> null =
    /// the comanda was cleared: fall back to the checkout cart if it has items, otherwise the idle/welcome state.
    /// </summary>
    public void UpdateFromComanda(ComandaDto? comanda, bool isPaying, decimal tendered, decimal change, string method)
    {
        if (comanda == null)
        {
            if (_checkout is { Items.Count: > 0 } c)
            {
                UpdateFromCheckout(c.Items, c.Subtotal, c.TaxTotal, c.Total, c.IsPaymentOpen, c.Tendered, c.Change, c.PaymentMethod, c.DiscountTotal);
                return;
            }
            ShowIdle();
            return;
        }

        Items.Clear();
        foreach (var i in comanda.BillableItems)
        {
            Items.Add(new CartItemDto
            {
                ProductId = i.ProductId,
                Name = string.IsNullOrWhiteSpace(i.Notes) ? i.ProductName : $"{i.ProductName} · {i.Notes}",
                UnitPrice = i.UnitPrice,
                Quantity = i.Quantity,
                IsWeighed = i.WeightKg.HasValue,
                WeightKg = i.WeightKg,
                TaxRate = i.TaxRate
            });
        }

        _comanda = (comanda.TableName, comanda.Number);
        ContextTitle = L.T("Display.ComandaTitle", comanda.TableName, comanda.Number);
        Subtotal = comanda.Subtotal;
        TaxTotal = comanda.TaxTotal;
        DiscountTotal = 0m;
        Total = comanda.Total;
        ItemCount = Items.Count;
        TenderedAmount = tendered;
        ChangeAmount = change;
        PaymentMethodName = method;
        State = isPaying ? CustomerDisplayState.Payment : CustomerDisplayState.Scanning;
    }

    /// <summary>Welcome screen: no items, zero totals (same look an empty checkout cart produces).</summary>
    private void ShowIdle()
    {
        Items.Clear();
        _comanda = null;
        ContextTitle = DefaultContextTitle;
        Subtotal = TaxTotal = DiscountTotal = Total = 0m;
        ItemCount = 0;
        TenderedAmount = ChangeAmount = 0m;
        State = CustomerDisplayState.Idle;
    }

    public void NotifySaleCompleted(string orderNumber, decimal total, decimal change)
    {
        LastCompletedOrderNumber = orderNumber;
        Total = total;
        ChangeAmount = change;
        State = CustomerDisplayState.Completed;
        Items.Clear();
    }
}
