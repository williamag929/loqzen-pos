using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Presentation.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private CheckoutViewModel _checkout;
    [ObservableProperty] private KdsViewModel _kds;
    [ObservableProperty] private CustomerDisplayViewModel _customerDisplay;
    [ObservableProperty] private CashShiftViewModel _cashShift;
    [ObservableProperty] private MastersViewModel _masters;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckoutActive))]
    [NotifyPropertyChangedFor(nameof(IsKdsActive))]
    [NotifyPropertyChangedFor(nameof(IsCashShiftActive))]
    [NotifyPropertyChangedFor(nameof(IsMastersActive))]
    private int _activeViewIndex = 0;

    public bool IsCheckoutActive  => ActiveViewIndex == 0;
    public bool IsKdsActive       => ActiveViewIndex == 1;
    public bool IsCashShiftActive => ActiveViewIndex == 2;
    public bool IsMastersActive   => ActiveViewIndex == 3;

    public string AppTitle => "Loqzen POS Standalone - Volcora Edition";
    public Action? RequestOpenCustomerDisplayWindow { get; set; }

    public MainViewModel(
        CheckoutViewModel checkout,
        KdsViewModel kds,
        CashShiftViewModel cashShift,
        MastersViewModel masters,
        CustomerDisplayViewModel customerDisplay)
    {
        _checkout = checkout;
        _kds = kds;
        _cashShift = cashShift;
        _masters = masters;
        _customerDisplay = customerDisplay;

        Checkout.CustomerDisplay = CustomerDisplay;
    }

    [RelayCommand] public void ShowCheckout()  => ActiveViewIndex = 0;

    [RelayCommand]
    public async Task ShowKdsAsync()
    {
        ActiveViewIndex = 1;
        await Kds.LoadOrdersAsync();
    }

    [RelayCommand]
    public async Task ShowCashShiftAsync()
    {
        ActiveViewIndex = 2;
        await CashShift.LoadCurrentShiftAsync();
    }

    [RelayCommand]
    public async Task ShowMastersAsync()
    {
        ActiveViewIndex = 3;
        await Masters.LoadAllAsync();
    }

    [RelayCommand]
    public void OpenCustomerDisplay() => RequestOpenCustomerDisplayWindow?.Invoke();

    [RelayCommand]
    public void TriggerBarcodeScan() => Checkout.TriggerSampleScan();
}
