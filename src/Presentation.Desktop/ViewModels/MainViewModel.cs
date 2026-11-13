using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Security;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthorizationSettings _authSettings;

    [ObservableProperty] private CheckoutViewModel _checkout;
    [ObservableProperty] private KdsViewModel _kds;
    [ObservableProperty] private CustomerDisplayViewModel _customerDisplay;
    [ObservableProperty] private CashShiftViewModel _cashShift;
    [ObservableProperty] private MastersViewModel _masters;
    [ObservableProperty] private ComandaViewModel _comanda;

    /// <summary>Signed-in user (nav tabs bind to its CanAccess* flags).</summary>
    public UserSession Session { get; }
    public LoginViewModel Login { get; }
    public ManagerAuthPromptViewModel AuthPrompt { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckoutActive))]
    [NotifyPropertyChangedFor(nameof(IsKdsActive))]
    [NotifyPropertyChangedFor(nameof(IsCashShiftActive))]
    [NotifyPropertyChangedFor(nameof(IsMastersActive))]
    [NotifyPropertyChangedFor(nameof(IsComandaActive))]
    private int _activeViewIndex = 0;

    public bool IsCheckoutActive  => ActiveViewIndex == 0;
    public bool IsKdsActive       => ActiveViewIndex == 1;
    public bool IsCashShiftActive => ActiveViewIndex == 2;
    public bool IsMastersActive   => ActiveViewIndex == 3;
    public bool IsComandaActive   => ActiveViewIndex == 4;

    /// <summary>Login overlay covers everything until someone signs in (unless RequireLogin is off).</summary>
    public bool IsLoginVisible => _authSettings.RequireLogin && !Session.IsLoggedIn;

    [RelayCommand]
    public async Task ShowComandasAsync()
    {
        ActiveViewIndex = 4;
        await Comanda.ActivateAsync();
    }

    public string AppTitle => "Loqzen POS Standalone - Volcora Edition";
    public Action? RequestOpenCustomerDisplayWindow { get; set; }

    public MainViewModel(
        CheckoutViewModel checkout,
        KdsViewModel kds,
        CashShiftViewModel cashShift,
        MastersViewModel masters,
        CustomerDisplayViewModel customerDisplay,
        ComandaViewModel comanda,
        UserSession session,
        LoginViewModel login,
        ManagerAuthPromptViewModel authPrompt,
        AuthorizationSettings authSettings)
    {
        _checkout = checkout;
        _kds = kds;
        _cashShift = cashShift;
        _masters = masters;
        _customerDisplay = customerDisplay;
        _comanda = comanda;
        Session = session;
        Login = login;
        AuthPrompt = authPrompt;
        _authSettings = authSettings;

        Checkout.CustomerDisplay = CustomerDisplay;
        Session.Changed += OnSessionChanged;
    }

    [RelayCommand] public void ShowCheckout()  => ActiveViewIndex = 0;

    private async Task ShowCheckoutAndReloadAsync()
    {
        ShowCheckout();
        await Checkout.CheckCashShiftStatusAsync(); // cashier label / shift banner follow the new user
    }

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

    /// <summary>"Cambiar usuario": back to the PIN screen.</summary>
    [RelayCommand]
    public void Logout() => Session.SignOut();

    // ── Role-aware navigation ────────────────────────────────────────────────
    private static PosView ViewOf(int index) => index switch
    {
        0 => PosView.Checkout,
        1 => PosView.Kds,
        2 => PosView.CashShift,
        3 => PosView.Masters,
        _ => PosView.Comandas
    };

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(IsLoginVisible));
        if (!Session.IsLoggedIn) return;

        // Land on a screen the role may use, and (re)load it so it reflects the new user.
        var target = Session.CanAccess(ViewOf(ActiveViewIndex)) ? ActiveViewIndex : FirstAllowedIndex();
        _ = ShowIndexAsync(target);
    }

    private int FirstAllowedIndex()
    {
        foreach (var index in new[] { 0, 4, 1, 2, 3 }) // Checkout, Comandas, KDS, Caja, Maestros
            if (Session.CanAccess(ViewOf(index))) return index;
        return 1; // KDS is open to every role
    }

    private Task ShowIndexAsync(int index) => index switch
    {
        0 => ShowCheckoutAndReloadAsync(),
        1 => ShowKdsAsync(),
        2 => ShowCashShiftAsync(),
        3 => ShowMastersAsync(),
        _ => ShowComandasAsync()
    };
}
