using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Receipts;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// "🧾 Recaudos": bill payments at the counter (energía, agua, gas, recargas…). Biller tiles on the left,
/// form in the middle (reference — also from the barcode scanner while this tab is active —, amount,
/// method, tendered), today's payments of the open shift on the right with reprint / void.
/// Singleton: services are resolved per operation from a scope.
/// </summary>
public partial class BillPaymentsViewModel : ObservableObject
{
    private static readonly string StoreName = new ReceiptDto().StoreName;
    private static Localizer L => Localizer.Instance;

    private readonly IServiceScopeFactory _scopes;
    private readonly IUserSession _session;
    private readonly IManagerAuthPrompt _authPrompt;
    private readonly IPrinterService _printer;
    private readonly ICashDrawerService _cashDrawer;
    private readonly IBarcodeScannerService _scanner;
    private readonly ITerminalChargePrompt _terminalPrompt;
    private bool _isActive;

    public ObservableCollection<BillerTileViewModel> Billers { get; } = new();
    public ObservableCollection<BillPaymentRowViewModel> Payments { get; } = new();
    public ObservableCollection<ChipViewModel> VoidReasonChips { get; } = new();

    // ── Shift ────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled), nameof(CanCharge), nameof(ChargeHint))]
    private bool _hasOpenShift;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled), nameof(CanCharge), nameof(ChargeHint))]
    private bool _isBusy;

    public bool IsFormEnabled => HasOpenShift && !IsBusy;
    public bool HasBillers => Billers.Count > 0;
    public bool HasPayments => Payments.Count > 0;

    [ObservableProperty] private string _notification = string.Empty;
    [ObservableProperty] private string _paymentsSummary = string.Empty;

    // ── Form ─────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedBiller), nameof(SelectedBillerText), nameof(Commission), nameof(CommissionText),
        nameof(Total), nameof(TotalText), nameof(BreakdownText), nameof(Change), nameof(ChangeText), nameof(CanCharge), nameof(ChargeHint))]
    private BillerTileViewModel? _selectedBiller;
    public bool HasSelectedBiller => SelectedBiller != null;
    public string SelectedBillerText => SelectedBiller is { } b ? $"{b.Name} · {b.CommissionText}" : L.T("Bills.ChooseBiller");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCharge), nameof(ChargeHint))]
    private string _reference = string.Empty;

    [ObservableProperty] private string _customerName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount), nameof(AmountText), nameof(Commission), nameof(CommissionText), nameof(Total),
        nameof(TotalText), nameof(BreakdownText), nameof(Change), nameof(ChangeText), nameof(CanCharge), nameof(ChargeHint))]
    private string _amountStr = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tendered), nameof(TenderedText), nameof(Change), nameof(ChangeText), nameof(CanCharge), nameof(ChargeHint))]
    private string _tenderedStr = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsCard), nameof(IsTransfer), nameof(CanCharge), nameof(ChargeHint), nameof(Change), nameof(ChangeText), nameof(IsTerminalChargeVisible))]
    private PaymentMethod _method = PaymentMethod.Cash;
    public bool IsCash => Method == PaymentMethod.Cash;
    public bool IsCard => Method == PaymentMethod.Card;
    public bool IsTransfer => Method == PaymentMethod.Transfer;

    /// <summary>"💳 Cobrar en datáfono" above "Cobrar recaudo" (manual path) when Card is selected and terminals are enabled.</summary>
    public bool IsTerminalChargeVisible => IsCard && _terminalPrompt.IsEnabled;

    private void OnThemeChanged()
    {
        OnPropertyChanged(nameof(AmountBorder));
        OnPropertyChanged(nameof(TenderedBorder));
        foreach (var b in Billers) b.RefreshTheme();
    }

    /// <summary>
    /// ES/EN switch: re-render every string this VM composes. XAML labels re-bind on their own; rows and chips are
    /// immutable, so they are rebuilt from their DTOs. The last Notification / VoidError keep their language until
    /// the next message (they are one-off events, not state).
    /// </summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(SelectedBillerText));
        OnPropertyChanged(nameof(BreakdownText));
        OnPropertyChanged(nameof(ChargeHint));
        OnPropertyChanged(nameof(NumpadExactLabel));
        OnPropertyChanged(nameof(VoidTitle));
        foreach (var b in Billers) b.RefreshLanguage();
        BuildVoidReasonChips();
        var rows = Payments.Select(r => r.Dto).ToList();
        if (!string.IsNullOrEmpty(PaymentsSummary)) PaymentsSummary = SummaryText(rows);
        Payments.Clear();
        foreach (var dto in rows) Payments.Add(new BillPaymentRowViewModel(dto, ReprintAsync, StartVoid));
    }

    private void BuildVoidReasonChips()
    {
        VoidReasonChips.Clear();
        foreach (var key in new[] { "Bills.Void.Reason.Typo", "Bills.Void.Reason.WrongReference", "Bills.Void.Reason.WrongBiller", "Bills.Void.Reason.CustomerLeft" })
            VoidReasonChips.Add(new ChipViewModel(L.T(key), v => VoidReason = v));
    }

    private static string SummaryText(System.Collections.Generic.IReadOnlyList<BillPaymentDto> list)
    {
        var ok = list.Where(p => p.Status == BillPaymentStatus.Completed).ToList();
        return L.T("Bills.Summary", ok.Count, ReceiptFormatter.Cop(ok.Sum(p => p.Total)), ReceiptFormatter.Cop(ok.Sum(p => p.Commission)));
    }

    /// <summary>Which figure the numpad edits: the bill amount or (cash) the tendered money.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingAmount), nameof(NumpadExactLabel), nameof(AmountBorder), nameof(TenderedBorder))]
    private bool _isEditingTendered;
    public bool IsEditingAmount => !IsEditingTendered;
    public string NumpadExactLabel => IsEditingTendered ? L.T("Bills.Numpad.Exact") : L.T("Bills.Numpad.ToTendered");
    /// <summary>Border of the figure the numpad edits (theme Info) vs. the idle one (theme Border); read from the current theme.</summary>
    public string AmountBorder => IsEditingTendered ? ThemePalette.Border : ThemePalette.Info;
    public string TenderedBorder => IsEditingTendered ? ThemePalette.Info : ThemePalette.Border;

    public decimal Amount => CashShiftViewModel.ParseAmount(AmountStr);
    public decimal Commission => SelectedBiller is { } b && Amount > 0 ? b.Dto.CommissionFor(Amount) : 0m;
    public decimal Total => Amount + Commission;
    public decimal Tendered => CashShiftViewModel.ParseAmount(TenderedStr);
    public decimal Change => IsCash ? Math.Max(0, Tendered - Total) : 0m;

    public string AmountText => ReceiptFormatter.Cop(Amount);
    public string CommissionText => ReceiptFormatter.Cop(Commission);
    public string TotalText => ReceiptFormatter.Cop(Total);
    public string TenderedText => ReceiptFormatter.Cop(Tendered);
    public string ChangeText => ReceiptFormatter.Cop(Change);
    public string BreakdownText => L.T("Bills.Breakdown", AmountText, CommissionText, TotalText);

    public bool CanCharge => IsFormEnabled && SelectedBiller != null && !string.IsNullOrWhiteSpace(Reference)
                             && Amount > 0 && (!IsCash || Tendered >= Total);

    public string ChargeHint =>
        !HasOpenShift ? L.T("Bills.Hint.OpenShift")
        : SelectedBiller == null ? L.T("Bills.ChooseBiller")
        : string.IsNullOrWhiteSpace(Reference) ? L.T("Bills.Hint.Reference")
        : Amount <= 0 ? L.T("Bills.Hint.Amount")
        : IsCash && Tendered < Total ? L.T("Bills.Hint.Missing", ReceiptFormatter.Cop(Total - Tendered))
        : string.Empty;

    // ── Change screen ────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummaryVisible))]
    private SaleSummaryViewModel? _lastPayment;
    public bool IsSummaryVisible => LastPayment != null;

    // ── Void dialog ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isVoidDialogVisible;
    [ObservableProperty] private BillPaymentRowViewModel? _voidTarget;
    [ObservableProperty] private string _voidReason = string.Empty;
    [ObservableProperty] private string _voidError = string.Empty;
    public string VoidTitle => VoidTarget is { } v ? L.T("Bills.Void.TitleFor", v.Dto.ReceiptNumber, v.Dto.BillerName, v.TotalText) : string.Empty;
    partial void OnVoidTargetChanged(BillPaymentRowViewModel? value) => OnPropertyChanged(nameof(VoidTitle));

    public BillPaymentsViewModel(IServiceScopeFactory scopes, IUserSession session, IManagerAuthPrompt authPrompt,
        IPrinterService printer, ICashDrawerService cashDrawer, IBarcodeScannerService scanner, ITerminalChargePrompt terminalPrompt,
        ThemeService? theme = null)
    {
        _terminalPrompt = terminalPrompt;
        // Colours exposed as strings (AmountBorder, tile borders) are read from ThemePalette at call time:
        // re-raise them when the user switches Dark/Light so the open tab repaints without a reload.
        if (theme != null) theme.Changed += OnThemeChanged;
        L.Changed += OnLanguageChanged;
        _scopes = scopes;
        _session = session;
        _authPrompt = authPrompt;
        _printer = printer;
        _cashDrawer = cashDrawer;
        _scanner = scanner;

        Billers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBillers));
        Payments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPayments));
        BuildVoidReasonChips();
    }

    // ── Activation (tab shown / hidden) ─────────────────────────────────────
    /// <summary>Called by MainViewModel when the tab is shown: subscribe to the scanner and reload.</summary>
    public async Task ActivateAsync()
    {
        if (!_isActive)
        {
            _isActive = true;
            _scanner.BarcodeScanned += OnBarcodeScanned;
        }
        await LoadAsync();
    }

    /// <summary>Tab hidden: the scanner goes back to the sales screen only.</summary>
    public void Deactivate()
    {
        if (!_isActive) return;
        _isActive = false;
        _scanner.BarcodeScanned -= OnBarcodeScanned;
    }

    private void OnBarcodeScanned(object? sender, string code) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isActive || string.IsNullOrWhiteSpace(code)) return;
            Reference = code.Trim();
            Notification = L.T("Bills.Notify.ReferenceRead", Reference);
        });

    /// <summary>Satellite: billers changed on the master. Same loader (selection is kept by id); nothing typed is lost.</summary>
    public Task ReloadBillersAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var shift = await scope.ServiceProvider.GetRequiredService<ICashShiftService>().GetCurrentShiftAsync();
            HasOpenShift = shift != null;

            var billers = await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().GetBillersAsync();
            var selectedId = SelectedBiller?.Dto.Id;
            Billers.Clear();
            foreach (var b in billers.OrderBy(b => b.SortOrder).ThenBy(b => b.Name))
                Billers.Add(new BillerTileViewModel(b, SelectBiller));
            SelectedBiller = null;
            if (selectedId is { } id && Billers.FirstOrDefault(t => t.Dto.Id == id) is { } again) SelectBiller(again);
        }
        catch (Exception ex)
        {
            Notification = L.T("Bills.Notify.Error", ex.Message);
        }
        await LoadPaymentsAsync();
    }

    private async Task LoadPaymentsAsync()
    {
        Payments.Clear();
        PaymentsSummary = string.Empty;
        if (!HasOpenShift) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var list = await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().GetForShiftAsync();
            foreach (var p in list) Payments.Add(new BillPaymentRowViewModel(p, ReprintAsync, StartVoid));
            PaymentsSummary = SummaryText(list);
        }
        catch (Exception ex)
        {
            Notification = L.T("Bills.Notify.Error", ex.Message);
        }
    }

    private void SelectBiller(BillerTileViewModel tile)
    {
        foreach (var t in Billers) t.IsSelected = ReferenceEquals(t, tile);
        SelectedBiller = tile;
    }

    // ── Numpad ───────────────────────────────────────────────────────────────
    [RelayCommand]
    public void NumpadDigit(string digit)
    {
        if (IsEditingTendered) TenderedStr = CashShiftViewModel.AppendDigits(TenderedStr, digit);
        else AmountStr = CashShiftViewModel.AppendDigits(AmountStr, digit);
    }

    [RelayCommand]
    public void NumpadClear()
    {
        if (IsEditingTendered) TenderedStr = "0";
        else AmountStr = "0";
    }

    /// <summary>Blue key: on the amount → jump to "Recibido" (cash) ; on "Recibido" → exact amount.</summary>
    [RelayCommand]
    public void NumpadExact()
    {
        if (!IsEditingTendered)
        {
            if (IsCash) IsEditingTendered = true;
            return;
        }
        TenderedStr = Total.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    [RelayCommand] public void EditAmount() => IsEditingTendered = false;
    [RelayCommand] public void EditTendered() { if (IsCash) IsEditingTendered = true; }

    [RelayCommand]
    public void SetMethod(string method)
    {
        if (!Enum.TryParse<PaymentMethod>(method, out var m)) return;
        Method = m;
        if (!IsCash) IsEditingTendered = false;
    }

    [RelayCommand]
    public void ResetForm()
    {
        Reference = string.Empty;
        CustomerName = string.Empty;
        AmountStr = "0";
        TenderedStr = "0";
        Method = PaymentMethod.Cash;
        IsEditingTendered = false;
    }

    // ── Charge ───────────────────────────────────────────────────────────────
    /// <summary>Manual path (cash / card typed by hand / transfer).</summary>
    [RelayCommand]
    public async Task ChargeAsync()
    {
        if (!CanCharge || SelectedBiller == null) { if (!string.IsNullOrEmpty(ChargeHint)) Notification = L.T("Bills.Notify.Warn", ChargeHint); return; }
        IsBusy = true;
        try { await ChargeCoreAsync(null); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// "💳 Cobrar en datáfono": the total (bill + commission) goes to the terminal; approved → the recaudo is recorded
    /// with the terminal reference. Declined / timeout / cancelled keep the form; "Cobrar manual" continues the manual path.
    /// </summary>
    [RelayCommand]
    public async Task ChargeOnTerminalAsync()
    {
        if (Method != PaymentMethod.Card) Method = PaymentMethod.Card;
        if (!CanCharge || SelectedBiller == null) { if (!string.IsNullOrEmpty(ChargeHint)) Notification = L.T("Bills.Notify.Warn", ChargeHint); return; }
        IsBusy = true;
        try
        {
            var biller = SelectedBiller;
            var outcome = await _terminalPrompt.RequestAsync(Total, TerminalPaymentPurpose.BillPayment, Reference.Trim(),
                description: L.T("Bills.Terminal.Description", biller.Name));

            if (outcome.IsApproved)
            {
                await ChargeCoreAsync(outcome.Charge);
            }
            else if (outcome.FallbackToManual)
            {
                Notification = string.IsNullOrWhiteSpace(outcome.Message) ? L.T("Bills.Notify.CardManual") : L.T("Bills.Notify.TerminalFallback", outcome.Message);
                await ChargeCoreAsync(null);
            }
            else
            {
                Notification = L.T("Bills.Notify.TerminalDeclined", outcome.Message ?? L.T("Bills.Terminal.NotApproved"));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChargeCoreAsync(TerminalPaymentDto? charge)
    {
        if (SelectedBiller == null) return;
        try
        {
            var user = _session.CurrentUser;
            var tendered = IsCash ? Tendered : Total;
            var reference = charge is { IsApproved: true } ? charge.ReferenceText : null;
            var request = new BillPaymentRequest(SelectedBiller.Dto.Id, Reference.Trim(), Amount, Method, tendered,
                user?.Id, user?.Name ?? "Cajero", string.IsNullOrWhiteSpace(CustomerName) ? null : CustomerName.Trim(),
                reference, charge?.Id);

            BillPaymentDto paid;
            using (var scope = _scopes.CreateScope())
                paid = await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().RecordAsync(request);

            await PrintReceiptAsync(paid);
            if (paid.Method == PaymentMethod.Cash)
            {
                try { await _cashDrawer.OpenDrawerAsync(); } catch { /* best effort */ }
            }

            LastPayment = new SaleSummaryViewModel(paid.ReceiptNumber, paid.Total, paid.TenderedAmount, paid.ChangeAmount,
                paid.Method, null, () => LastPayment = null, title: L.T("Bills.Summary.Title", paid.ReceiptNumber, paid.BillerName),
                dismissLabel: L.T("Bills.Summary.Dismiss"));
            Notification = L.T("Bills.Notify.Recorded", paid.ReceiptNumber, reference ?? ReceiptFormatter.Cop(paid.Total));
            ResetForm();
            await LoadPaymentsAsync();
        }
        catch (Exception ex)
        {
            Notification = charge is { IsApproved: true }
                ? L.T("Bills.Notify.ErrorApproved", ex.Message, charge.ReferenceText)
                : L.T("Bills.Notify.Error", ex.Message);
        }
    }

    private async Task PrintReceiptAsync(BillPaymentDto p)
    {
        try
        {
            if (!await _printer.PrintLinesAsync(CashReports.BillPaymentReceipt(p, StoreName), p.ReceiptNumber))
                Notification = L.T("Bills.Notify.PrintFailed", p.ReceiptNumber);
        }
        catch (Exception ex)
        {
            Notification = L.T("Bills.Notify.PrintError", p.ReceiptNumber, ex.Message);
        }
    }

    private async Task ReprintAsync(BillPaymentRowViewModel row)
    {
        await PrintReceiptAsync(row.Dto);
        Notification = L.T("Bills.Notify.Reprinted", row.Dto.ReceiptNumber);
    }

    // ── Void ─────────────────────────────────────────────────────────────────
    private void StartVoid(BillPaymentRowViewModel row)
    {
        if (row.IsVoided) return;
        VoidTarget = row;
        VoidReason = string.Empty;
        VoidError = string.Empty;
        IsVoidDialogVisible = true;
    }

    [RelayCommand] public void CancelVoid() { IsVoidDialogVisible = false; VoidTarget = null; }

    [RelayCommand]
    public async Task ConfirmVoidAsync()
    {
        if (VoidTarget is not { } row) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { VoidError = L.T("Bills.Void.NeedReason"); return; }

        var approval = await RequiredOverride.RequestAsync(_authPrompt, _scopes, _session, AuthorizedAction.VoidBillPayment,
            L.T("Bills.Void.AuthDescription", row.Dto.ReceiptNumber, row.Dto.BillerName, VoidReason.Trim()), row.Dto.Total, row.Dto.ReceiptNumber);
        if (!approval.IsApproved) { VoidError = approval.Error ?? L.T("Bills.Void.Denied"); return; }

        IsBusy = true;
        try
        {
            using (var scope = _scopes.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().VoidAsync(row.Dto.Id, VoidReason.Trim(), approval.Override!.Id);
            IsVoidDialogVisible = false;
            VoidTarget = null;
            Notification = row.Dto.Method == PaymentMethod.Cash
                ? L.T("Bills.Notify.VoidedCash", row.Dto.ReceiptNumber, ReceiptFormatter.Cop(row.Dto.Total))
                : L.T("Bills.Notify.Voided", row.Dto.ReceiptNumber);
            if (row.Dto.Method == PaymentMethod.Cash)
            {
                try { await _cashDrawer.OpenDrawerAsync(); } catch { /* best effort */ }
            }
            await LoadPaymentsAsync();
        }
        catch (Exception ex)
        {
            VoidError = L.T("Bills.Notify.Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
