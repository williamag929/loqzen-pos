using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Security;
using Presentation.Desktop.Services;
using Presentation.Server;

namespace Presentation.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthorizationSettings _authSettings;
    private readonly ServerSettings _serverSettings;
    private readonly LoqzenServer _server;
    private readonly LanServerStatus _serverStatus;
    private readonly DispatcherTimer? _lanChipTimer;
    private readonly DispatcherTimer _copiedTimer;

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

    /// <summary>Set by MainWindow: copies the given text to the OS clipboard (needs a TopLevel).</summary>
    public Action<string>? RequestCopyKdsUrl { get; set; }

    // ── LAN server chip (browser KDS URL + connected tablets) ────────────────
    [ObservableProperty] private string _lanChipText = "🌐 Iniciando servidor LAN…";
    [ObservableProperty] private string _lanChipTooltip = string.Empty;

    /// <summary>Chip hidden when the server is disabled in config; cashier+ only.</summary>
    public bool IsLanChipVisible => _serverSettings.Enabled && Session.CanAccessCashShift;

    // ── Compact nav bar (window narrower than 1200 px; set by MainWindow.SizeChanged) ───
    // Tabs get short labels and the right-hand tools become icon-only so nothing overlaps.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckoutTabLabel), nameof(ComandasTabLabel), nameof(KdsTabLabel),
        nameof(CashShiftTabLabel), nameof(MastersTabLabel), nameof(ScanLabel), nameof(CustomerDisplayLabel),
        nameof(LogoutLabel), nameof(LanChipDisplayText), nameof(LanChipDisplayTooltip), nameof(IsWide))]
    private bool _isCompact;
    public bool IsWide => !IsCompact;

    public string CheckoutTabLabel  => IsCompact ? "🛒 Venta"   : "🛒 Punto de Venta";
    public string ComandasTabLabel  => IsCompact ? "🍽️ Mesas"   : "🍽️ Mesas / Comandas";
    public string KdsTabLabel       => IsCompact ? "👨‍🍳 KDS"     : "👨‍🍳 KDS Cocina";
    public string CashShiftTabLabel => IsCompact ? "💼 Caja"    : "💼 Turno de Caja";
    public string MastersTabLabel   => "🗂️ Maestros";
    public string ScanLabel            => IsCompact ? "⚡"  : "⚡ Scan";
    public string CustomerDisplayLabel => IsCompact ? "🖥️" : "🖥️ Visor Cliente";
    public string LogoutLabel          => IsCompact ? "🔄" : "🔄 Cambiar usuario";

    /// <summary>Compact: only the leading icon (🌐 / ✅); the full text moves to the tooltip.</summary>
    public string LanChipDisplayText => IsCompact ? LanChipText.Split(' ')[0] : LanChipText;
    public string LanChipDisplayTooltip => IsCompact ? $"{LanChipText}\n{LanChipTooltip}" : LanChipTooltip;

    partial void OnLanChipTextChanged(string value) => OnPropertyChanged(nameof(LanChipDisplayText));
    partial void OnLanChipTooltipChanged(string value) => OnPropertyChanged(nameof(LanChipDisplayTooltip));

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
        AuthorizationSettings authSettings,
        ServerSettings serverSettings,
        LoqzenServer server,
        LanServerStatus serverStatus)
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
        _serverSettings = serverSettings;
        _server = server;
        _serverStatus = serverStatus;

        Checkout.CustomerDisplay = CustomerDisplay;
        Session.Changed += OnSessionChanged;

        // "URL copiada" confirmation reverts after 2 s.
        _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); UpdateLanChip(); };

        if (_serverSettings.Enabled)
        {
            _serverStatus.Changed += UpdateLanChip;
            UpdateLanChip();
            // Cheap property reads (IsRunning / ConnectedClients) every 5 s.
            _lanChipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _lanChipTimer.Tick += (_, _) => { if (!_copiedTimer.IsEnabled) UpdateLanChip(); };
            _lanChipTimer.Start();
        }
    }

    private void UpdateLanChip()
    {
        if (_serverStatus.HasFailed)
        {
            LanChipText = $"🌐 Servidor LAN no disponible (puerto {_serverSettings.Port} en uso)";
            LanChipTooltip = _serverStatus.Error ?? LanChipText;
        }
        else if (!_server.IsRunning)
        {
            LanChipText = "🌐 Iniciando servidor LAN…";
            LanChipTooltip = LanChipText;
        }
        else
        {
            var n = _server.ConnectedClients;
            LanChipText = $"🌐 KDS web: {_server.KdsUrl} · {n} {(n == 1 ? "conectado" : "conectados")}";
            LanChipTooltip = $"{_server.KdsUrl}\nToca para copiar la URL (ábrela en la tablet de cocina)";
        }
    }

    /// <summary>Tap on the chip: copy the browser-KDS URL and confirm in place for 2 s.</summary>
    [RelayCommand]
    public void CopyKdsUrl()
    {
        if (!_server.IsRunning || RequestCopyKdsUrl == null) return;
        RequestCopyKdsUrl(_server.KdsUrl);
        LanChipText = "✅ URL copiada";
        _copiedTimer.Stop();
        _copiedTimer.Start();
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
        OnPropertyChanged(nameof(IsLanChipVisible));
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
