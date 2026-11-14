using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Security;
using Core.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;
using Presentation.Server;

namespace Presentation.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthorizationSettings _authSettings;
    private readonly ServerSettings _serverSettings;
    private readonly LoqzenServer _server;
    private readonly LanServerStatus _serverStatus;
    private readonly ThemeService _theme;
    private readonly DispatcherTimer? _lanChipTimer;
    private readonly DispatcherTimer _copiedTimer;

    [ObservableProperty] private CheckoutViewModel _checkout;
    [ObservableProperty] private KdsViewModel _kds;
    [ObservableProperty] private CustomerDisplayViewModel _customerDisplay;
    [ObservableProperty] private CashShiftViewModel _cashShift;
    [ObservableProperty] private MastersViewModel _masters;
    [ObservableProperty] private ComandaViewModel _comanda;
    [ObservableProperty] private BillPaymentsViewModel _billPayments;

    /// <summary>Signed-in user (nav tabs bind to its CanAccess* flags).</summary>
    public UserSession Session { get; }
    public LoginViewModel Login { get; }
    public ManagerAuthPromptViewModel AuthPrompt { get; }
    /// <summary>"Cobrar en datáfono" modal shared by Checkout / Comandas / Recaudos (hosted above every view).</summary>
    public TerminalChargePromptViewModel TerminalCharge { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckoutActive))]
    [NotifyPropertyChangedFor(nameof(IsKdsActive))]
    [NotifyPropertyChangedFor(nameof(IsCashShiftActive))]
    [NotifyPropertyChangedFor(nameof(IsMastersActive))]
    [NotifyPropertyChangedFor(nameof(IsComandaActive))]
    [NotifyPropertyChangedFor(nameof(IsBillPaymentsActive))]
    private int _activeViewIndex = 0;

    public bool IsCheckoutActive  => ActiveViewIndex == 0;
    public bool IsKdsActive       => ActiveViewIndex == 1;
    public bool IsCashShiftActive => ActiveViewIndex == 2;
    public bool IsMastersActive   => ActiveViewIndex == 3;
    public bool IsComandaActive   => ActiveViewIndex == 4;
    public bool IsBillPaymentsActive => ActiveViewIndex == 5;

    /// <summary>Tab changes: the Recaudos tab owns the barcode scanner while visible; the cash dashboard refreshes only on screen.</summary>
    partial void OnActiveViewIndexChanged(int value)
    {
        CashShift.IsViewActive = value == 2;
        Checkout.IsScannerSuspended = value == 5;
        if (value != 5) BillPayments.Deactivate();
    }

    /// <summary>Login overlay covers everything until someone signs in (unless RequireLogin is off).</summary>
    public bool IsLoginVisible => _authSettings.RequireLogin && !Session.IsLoggedIn;

    [RelayCommand]
    public async Task ShowComandasAsync()
    {
        ActiveViewIndex = 4;
        await Comanda.ActivateAsync();
    }

    private static Localizer L => Localizer.Instance;
    private static string T(string key, params object?[] args) => L.T(key, args);

    public string AppTitle => T("App.Title");
    public Action? RequestOpenCustomerDisplayWindow { get; set; }

    /// <summary>Set by MainWindow: copies the given text to the OS clipboard (needs a TopLevel).</summary>
    public Action<string>? RequestCopyKdsUrl { get; set; }

    // ── LAN server chip (browser KDS URL + connected tablets) ────────────────
    [ObservableProperty] private string _lanChipText = Localizer.Instance.T("Lan.Starting");
    [ObservableProperty] private string _lanChipTooltip = string.Empty;

    /// <summary>Chip hidden when the server is disabled in config; cashier+ only.</summary>
    public bool IsLanChipVisible => _serverSettings.Enabled && Session.CanAccessCashShift;

    // ── Responsive nav bar (width tiers set by MainWindow.SizeChanged → SetWidth) ─────────────
    // IsCompact        (< 1200 px): short tab labels + Button.tab.compact; the tool chips collapse into one "⋯" chip.
    // IsToolsCollapsed (< 1750 px): tools in the "⋯" flyout even with full tab labels (six full tabs + the chip
    //                               row do not fit side by side below that width; the WrapPanel is only a fallback).
    // IsWide           (≥ 2100 px): brand subtitle + OFFLINE-FIRST badge.
    public const double CompactWidth = 1200;
    public const double ToolsCollapseWidth = 1750;
    public const double WideWidth = 2100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckoutTabLabel), nameof(ComandasTabLabel), nameof(KdsTabLabel),
        nameof(CashShiftTabLabel), nameof(MastersTabLabel), nameof(BillPaymentsTabLabel))]
    private bool _isCompact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToolsExpanded))]
    private bool _isToolsCollapsed;
    public bool IsToolsExpanded => !IsToolsCollapsed;

    [ObservableProperty] private bool _isWide;

    /// <summary>Called by MainWindow on every resize (and once the DataContext is set).</summary>
    public void SetWidth(double width)
    {
        if (width <= 0) return;
        IsCompact = width < CompactWidth;
        IsToolsCollapsed = width < ToolsCollapseWidth;
        IsWide = width >= WideWidth;
    }

    // Every label below is read from the i18n tables (Assets/i18n/{es,en}.json) and re-raised on language change.
    public string CheckoutTabLabel  => T(IsCompact ? "Tab.Checkout"  : "Tab.Checkout.Long");
    public string ComandasTabLabel  => T(IsCompact ? "Tab.Comandas"  : "Tab.Comandas.Long");
    public string KdsTabLabel       => T(IsCompact ? "Tab.Kds"       : "Tab.Kds.Long");
    public string CashShiftTabLabel => T(IsCompact ? "Tab.CashShift" : "Tab.CashShift.Long");
    public string MastersTabLabel   => T("Tab.Masters");
    public string BillPaymentsTabLabel => T("Tab.BillPayments");

    // Tool chips: a SHORT visible label in the bar (tooltips do not exist on touch) and a FULL label inside the
    // "⋯" flyout, where there is room. Distinct icons: ▣ scanner, 📺 customer display, 🌐 LAN, 🏪 register, 👤 user.
    public string ScanLabel                 => T("Chip.Scan");
    public string ScanFullLabel             => T("Chip.Scan.Full");
    public string CustomerDisplayLabel      => T("Chip.CustomerDisplay");
    public string CustomerDisplayFullLabel  => T("Chip.CustomerDisplay.Full");
    public string LogoutLabel               => T("Chip.ChangeUser");
    public string LogoutFullLabel           => T("Chip.ChangeUser.Full");
    public string MoreChipLabel             => "⋯";
    public string MoreChipTooltip           => T("Chip.Tools.Tooltip");

    /// <summary>"👤 Ana" in the bar; the flyout shows Session.ChipText (name · role).</summary>
    public string UserChipText => T("Chip.User", FirstWord(Session.DisplayName));

    // ── Language chip (🌐 ES / 🌐 EN), visible for every role; tap toggles, persists in ui.json ────
    public string LanguageLabel => T("Chip.Language", L.LanguageLabel);
    public string LanguageFullLabel => T("Chip.Language.Full", L.LanguageLabel, L.NextLanguage.ToUpperInvariant());

    [RelayCommand]
    public void ToggleLanguage() => L.Toggle();

    /// <summary>Language switched: every text this VM computes in C# is re-read ({i18n:Loc} bindings refresh on their own).</summary>
    private void OnLanguageChanged()
    {
        Notify(nameof(AppTitle), nameof(LanguageLabel), nameof(LanguageFullLabel),
            nameof(CheckoutTabLabel), nameof(ComandasTabLabel), nameof(KdsTabLabel),
            nameof(CashShiftTabLabel), nameof(MastersTabLabel), nameof(BillPaymentsTabLabel),
            nameof(ScanLabel), nameof(ScanFullLabel), nameof(CustomerDisplayLabel), nameof(CustomerDisplayFullLabel),
            nameof(LogoutLabel), nameof(LogoutFullLabel), nameof(MoreChipTooltip), nameof(UserChipText),
            nameof(ThemeLabel), nameof(ThemeFullLabel));
        if (!_copiedTimer.IsEnabled) UpdateLanChip(); // LAN texts are stored strings: rebuild them in the new language
        UpdateTerminalChip();
        TerminalCharge.RefreshLanguage();
    }

    private static string FirstWord(string s)
    {
        var t = s.Trim();
        var i = t.IndexOf(' ');
        return i > 0 ? t[..i] : t;
    }

    /// <summary>Bar: "🌐 LAN · 2" (clients) / "🌐 LAN ⚠️" / "🌐 LAN …"; flyout: the full text with the URL.</summary>
    public string LanChipDisplayText => LanChipShortText;
    public string LanChipDisplayTooltip => $"{LanChipText}\n{LanChipTooltip}".Trim();
    [ObservableProperty] private string _lanChipShortText = Localizer.Instance.T("Lan.Starting.Short");

    partial void OnLanChipTextChanged(string value) => Notify(nameof(LanChipDisplayText), nameof(LanChipDisplayTooltip));
    partial void OnLanChipTooltipChanged(string value) => OnPropertyChanged(nameof(LanChipDisplayTooltip));

    /// <summary>Raise several PropertyChanged at once. NOT named OnPropertyChanged: a params overload on the derived
    /// class would hide the base OnPropertyChanged(string) in overload resolution and recurse into itself.</summary>
    private void Notify(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }

    // ── Theme chip (🌙 Oscuro / ☀️ Claro), visible for every role ─────────────────────────────
    public string ThemeLabel => T(_theme.IsLight ? "Theme.Light" : "Theme.Dark");
    public string ThemeFullLabel => T(_theme.IsLight ? "Theme.Light.Full" : "Theme.Dark.Full");

    /// <summary>Raised after the palette switched and this VM re-read ThemePalette (shell/screens may refresh their colours).</summary>
    public event Action? ThemeChanged;

    [RelayCommand]
    public void ToggleTheme() => _theme.Toggle();

    private void OnThemeChanged()
    {
        Notify(nameof(ThemeLabel), nameof(ThemeFullLabel));
        UpdateTerminalChip();          // TerminalChipColor comes from ThemePalette
        TerminalCharge.RefreshTheme(); // BorderColor / ResultColor
        ThemeChanged?.Invoke();
    }

    public MainViewModel(
        CheckoutViewModel checkout,
        KdsViewModel kds,
        CashShiftViewModel cashShift,
        MastersViewModel masters,
        CustomerDisplayViewModel customerDisplay,
        ComandaViewModel comanda,
        BillPaymentsViewModel billPayments,
        UserSession session,
        LoginViewModel login,
        ManagerAuthPromptViewModel authPrompt,
        TerminalChargePromptViewModel terminalCharge,
        AuthorizationSettings authSettings,
        ServerSettings serverSettings,
        LoqzenServer server,
        LanServerStatus serverStatus,
        TerminalSettings terminalSettings,
        ITerminalContext terminalContext,
        TerminalStatus terminalStatus,
        SatelliteSyncWorker satelliteSync,
        OutboxSyncWorker outboxSync,
        IServiceScopeFactory scopes,
        ThemeService theme)
    {
        _checkout = checkout;
        _kds = kds;
        _cashShift = cashShift;
        _masters = masters;
        _customerDisplay = customerDisplay;
        _comanda = comanda;
        _billPayments = billPayments;
        Session = session;
        Login = login;
        AuthPrompt = authPrompt;
        TerminalCharge = terminalCharge;
        _authSettings = authSettings;
        _serverSettings = serverSettings;
        _server = server;
        _serverStatus = serverStatus;
        _theme = theme;
        _theme.Changed += OnThemeChanged;
        L.Changed += OnLanguageChanged;

        Checkout.CustomerDisplay = CustomerDisplay;
        Session.Changed += OnSessionChanged;
        InitTerminal(terminalSettings, terminalContext, terminalStatus, satelliteSync, outboxSync, scopes);

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
            LanChipText = T("Lan.Failed", _serverSettings.Port);
            LanChipTooltip = _serverStatus.Error ?? LanChipText;
            LanChipShortText = T("Lan.Failed.Short");
        }
        else if (!_server.IsRunning)
        {
            LanChipText = T("Lan.Starting");
            LanChipTooltip = LanChipText;
            LanChipShortText = T("Lan.Starting.Short");
        }
        else
        {
            var n = _server.ConnectedClients;
            LanChipText = n == 1 ? T("Lan.Running.One", _server.KdsUrl) : T("Lan.Running.Many", _server.KdsUrl, n);
            LanChipTooltip = T("Lan.Running.Tooltip", _server.KdsUrl);
            LanChipShortText = T("Lan.Running.Short", n);
        }
    }

    /// <summary>Tap on the chip: copy the browser-KDS URL and confirm in place for 2 s.</summary>
    [RelayCommand]
    public void CopyKdsUrl()
    {
        if (!_server.IsRunning || RequestCopyKdsUrl == null) return;
        RequestCopyKdsUrl(_server.KdsUrl);
        LanChipText = T("Lan.Copied");
        LanChipShortText = T("Lan.Copied.Short");
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
    public async Task ShowBillPaymentsAsync()
    {
        ActiveViewIndex = 5;
        await BillPayments.ActivateAsync();
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
        5 => PosView.BillPayments,
        _ => PosView.Comandas
    };

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(IsLoginVisible));
        OnPropertyChanged(nameof(IsLanChipVisible));
        OnPropertyChanged(nameof(UserChipText));
        NotifyTabVisibility();
        if (!Session.IsLoggedIn) return;

        // Land on a screen the role may use ON THIS TERMINAL, and (re)load it so it reflects the new user.
        var target = CanShow(ActiveViewIndex) ? ActiveViewIndex : FirstAllowedIndex();
        _ = ShowIndexAsync(target);
    }

    private int FirstAllowedIndex()
    {
        foreach (var index in new[] { 0, 4, 1, 2, 5, 3 }) // Checkout, Comandas, KDS, Caja, Recaudos, Maestros
            if (CanShow(index)) return index;
        return IsSatellite ? 0 : 1; // KDS is open to every role on a master; a satellite has no KDS
    }

    private Task ShowIndexAsync(int index) => index switch
    {
        0 => ShowCheckoutAndReloadAsync(),
        1 => ShowKdsAsync(),
        2 => ShowCashShiftAsync(),
        3 => ShowMastersAsync(),
        5 => ShowBillPaymentsAsync(),
        _ => ShowComandasAsync()
    };
}
