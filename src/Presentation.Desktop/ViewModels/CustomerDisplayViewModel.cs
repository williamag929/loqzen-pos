using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Application.DTOs;

namespace Presentation.Desktop.ViewModels;

public enum CustomerDisplayState
{
    Idle = 0,
    Scanning = 1,
    Payment = 2,
    Completed = 3
}

public partial class CustomerDisplayViewModel : ObservableObject
{
    [ObservableProperty]
    private string _storeName = "LOQZEN POS & RESTAURANT";

    [ObservableProperty]
    private string _storeTagline = "Terminal Volcora • Experiencia de Compra Transparente";

    [ObservableProperty]
    private CustomerDisplayState _state = CustomerDisplayState.Idle;

    [ObservableProperty]
    private decimal _subtotal = 0m;

    [ObservableProperty]
    private decimal _taxTotal = 0m;

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

    public void UpdateFromCheckout(
        ObservableCollection<CartItemDto> cartItems, 
        decimal subtotal, 
        decimal taxTotal, 
        decimal total, 
        bool isPaymentOpen, 
        decimal tendered, 
        decimal change, 
        string paymentMethod)
    {
        Items.Clear();
        foreach (var item in cartItems)
        {
            Items.Add(item);
        }

        Subtotal = subtotal;
        TaxTotal = taxTotal;
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

    public void NotifySaleCompleted(string orderNumber, decimal total, decimal change)
    {
        LastCompletedOrderNumber = orderNumber;
        Total = total;
        ChangeAmount = change;
        State = CustomerDisplayState.Completed;
        Items.Clear();
    }
}
