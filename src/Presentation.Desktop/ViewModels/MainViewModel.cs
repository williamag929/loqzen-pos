using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Presentation.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private CheckoutViewModel _checkout;

    [ObservableProperty]
    private KdsViewModel _kds;

    [ObservableProperty]
    private CustomerDisplayViewModel _customerDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckoutActive))]
    [NotifyPropertyChangedFor(nameof(IsKdsActive))]
    private int _activeViewIndex = 0;

    public bool IsCheckoutActive => ActiveViewIndex == 0;
    public bool IsKdsActive => ActiveViewIndex == 1;

    public string AppTitle => "Loqzen POS Standalone - Volcora Edition";

    public Action? RequestOpenCustomerDisplayWindow { get; set; }

    public MainViewModel()
    {
        _customerDisplay = new CustomerDisplayViewModel();
        _checkout = new CheckoutViewModel();
        _kds = new KdsViewModel();

        // Connect customer display with checkout
        Checkout.CustomerDisplay = CustomerDisplay;
    }

    [RelayCommand]
    public void ShowCheckout()
    {
        ActiveViewIndex = 0;
    }

    [RelayCommand]
    public async Task ShowKdsAsync()
    {
        ActiveViewIndex = 1;
        await Kds.LoadOrdersAsync();
    }

    [RelayCommand]
    public void OpenCustomerDisplay()
    {
        RequestOpenCustomerDisplayWindow?.Invoke();
    }

    [RelayCommand]
    public void TriggerBarcodeScan()
    {
        Checkout.TriggerSampleScan();
    }
}
