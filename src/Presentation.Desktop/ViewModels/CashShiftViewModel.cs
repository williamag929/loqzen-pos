using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Receipts;
using Core.Application.Services;
using Core.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// "Turno de Caja": open, live dashboard (expected cash, sales, recaudos, movements), cash movements,
/// partial count, 4-step close wizard, Z report and history. Singleton with a 30 s refresh timer, so every
/// service call resolves its own scope (DbContext is not thread-safe).
/// Partials: .Movements.cs (movements + partial count), .Close.cs (wizard), .History.cs (Z report + history).
/// </summary>
public partial class CashShiftViewModel : ObservableObject
{
    private static readonly string StoreName = new ReceiptDto().StoreName;

    private readonly IServiceScopeFactory _scopes;
    private readonly IUserSession _session;
    private readonly OutboxSyncWorker _sync;
    private readonly CashShiftSettings _settings;
    private readonly IManagerAuthPrompt _authPrompt;
    private readonly IPrinterService _printer;
    private readonly ICashDrawerService _cashDrawer;
    private readonly DispatcherTimer _refreshTimer;

    // ── Current Shift State ──────────────────────────────────────────────────
    [ObservableProperty] private CashShift? _currentShift;
    [ObservableProperty] private bool _isShiftOpen = false;
    [ObservableProperty] private string _shiftStatusText = Localizer.Instance.T("Cash.Status.NoShift.Short");
    [ObservableProperty] private string _shiftStatusColor = ThemePalette.Danger;

    // ── Live dashboard (from GetReconciliationAsync) ────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpectedCashText), nameof(SalesText), nameof(SalesDetailText), nameof(BillPaymentsText),
        nameof(BillPaymentsDetailText), nameof(MovementsText), nameof(MovementsDetailText), nameof(OpeningText),
        nameof(TerminalCardDetailText), nameof(HasTerminalCharges))]
    private ShiftReconciliationDto? _reconciliation;

    public ObservableCollection<CashMovementRowViewModel> Movements { get; } = new();
    public bool HasMovements => Movements.Count > 0;

    /// <summary>Blind count: only a manager sees the expected cash while the shift is open (open terminal = admin).</summary>
    public bool CanSeeExpected => !_settings.BlindCount || _session.CurrentUser == null || _session.CurrentUser.CanAuthorize;
    public bool IsBlindCount => _settings.BlindCount;

    public string ExpectedCashText => !CanSeeExpected ? T("Cash.Dash.Hidden")
        : Reconciliation is { } r ? ReceiptFormatter.Cop(r.ExpectedCash) : "—";
    public string OpeningText => Reconciliation is { } r ? T("Cash.Dash.Opening", ReceiptFormatter.Cop(r.OpeningAmount)) : string.Empty;
    public string SalesText => Reconciliation is { } r ? ReceiptFormatter.Cop(r.TotalSales) : "—";
    public string SalesDetailText => Reconciliation is { } r
        ? T("Cash.Dash.SalesDetail", r.OrderCount, ReceiptFormatter.Cop(r.CashSales), ReceiptFormatter.Cop(r.CardSales), ReceiptFormatter.Cop(r.TransferSales))
        : string.Empty;
    /// <summary>"Tarjeta por datáfono: $x (n)" — approved terminal charges of the shift (subset of card sales).</summary>
    public bool HasTerminalCharges => (Reconciliation?.TerminalChargeCount ?? 0) > 0;
    public string TerminalCardDetailText => Reconciliation is { } r && r.TerminalChargeCount > 0
        ? T("Cash.Dash.TerminalCard", ReceiptFormatter.Cop(r.TerminalCardTotal), r.TerminalChargeCount)
        : string.Empty;
    public string BillPaymentsText => Reconciliation is { } r
        ? ReceiptFormatter.Cop(r.BillPaymentsCash + r.BillPaymentsCard + r.BillPaymentsTransfer) : "—";
    public string BillPaymentsDetailText => Reconciliation is { } r
        ? T("Cash.Dash.BillPaymentsDetail", r.BillPaymentCount, ReceiptFormatter.Cop(r.BillPaymentCommissions)) : string.Empty;
    public string MovementsText => Reconciliation is { } r
        ? SignedCop(r.PaidIn - r.PaidOut - r.Expenses - r.SafeDrops - r.TipPayouts) : "—";
    public string MovementsDetailText => Reconciliation is { } r
        ? T("Cash.Dash.MovementsDetail", ReceiptFormatter.Cop(r.PaidIn), ReceiptFormatter.Cop(r.PaidOut + r.Expenses + r.SafeDrops + r.TipPayouts))
        : string.Empty;

    // ── Open Shift Dialog ────────────────────────────────────────────────────
    [ObservableProperty] private bool _isOpenDialogVisible = false;
    [ObservableProperty] private string _cashierName = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpeningAmountText))]
    private string _openingAmountStr = "0";
    private decimal _suggestedOpening;
    public string SuggestedOpeningText => T("Cash.Open.Suggested", ReceiptFormatter.Cop(_suggestedOpening));
    private bool _openingPristine;
    public string OpeningAmountText => ReceiptFormatter.Cop(ParseAmount(OpeningAmountStr));

    [ObservableProperty] private string _notification = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set by MainViewModel: the 30 s refresh only runs while the tab is on screen.</summary>
    public bool IsViewActive { get; set; }

    // ── Outbox sync indicator (header) ───────────────────────────────────────
    [ObservableProperty] private string _syncStatusText = Localizer.Instance.T("Cash.Sync.Disabled");
    [ObservableProperty] private bool _hasFailedSync;
    [ObservableProperty] private bool _isRetryingSync;

    public CashShiftViewModel(IServiceScopeFactory scopes, IUserSession session, OutboxSyncWorker sync,
        CashShiftSettings settings, IManagerAuthPrompt authPrompt, IPrinterService printer, ICashDrawerService cashDrawer,
        ThemeService? theme = null)
    {
        _scopes = scopes;
        _session = session;
        _sync = sync;
        _settings = settings;
        _authPrompt = authPrompt;
        _printer = printer;
        _cashDrawer = cashDrawer;

        PartialCount = new CashCountViewModel(settings);
        CloseCount = new CashCountViewModel(settings);
        CloseCount.Changed += OnCloseCountChanged;
        Movements.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMovements));

        _sync.Changed += () => Dispatcher.UIThread.Post(UpdateSyncStatus); // worker already posts, this is belt and braces
        _session.Changed += () => Dispatcher.UIThread.Post(OnSessionChanged);
        UpdateSyncStatus();

        // Colours exposed as hex strings come from ThemePalette (current theme); re-read them when the theme flips.
        if (theme != null)
            theme.Changed += () => Dispatcher.UIThread.Post(OnThemeChanged);

        // Texts computed in the VM (status line, dashboard details, wizard texts) are re-read when the UI language flips.
        Localizer.Instance.Changed += () => Dispatcher.UIThread.Post(OnLanguageChanged);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (!IsViewActive || !IsShiftOpen || IsBusy) return;
            UpdateShiftStatusDisplay();
            await RefreshReconciliationAsync();
        };
        _refreshTimer.Start();

        _ = LoadCurrentShiftAsync();
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(CanSeeExpected));
        OnPropertyChanged(nameof(ExpectedCashText));
        OnPropertyChanged(nameof(CanSeeExpectedInCount));
    }

    // ── Helpers shared by the partials ──────────────────────────────────────
    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);

    private async Task<T> WithShiftServiceAsync<T>(Func<ICashShiftService, Task<T>> action)
    {
        using var scope = _scopes.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICashShiftService>());
    }

    internal static decimal ParseAmount(string? s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;

    internal static string AppendDigits(string current, string digits, int maxLength = 10)
    {
        var baseText = current == "0" ? string.Empty : current;
        var next = baseText + digits;
        next = next.TrimStart('0');
        if (next.Length == 0) next = "0";
        return next.Length > maxLength ? current : next;
    }

    public static string SignedCop(decimal amount) =>
        amount > 0 ? "+ " + ReceiptFormatter.Cop(amount)
        : amount < 0 ? "− " + ReceiptFormatter.Cop(-amount)
        : ReceiptFormatter.Cop(0);

    /// <summary>Green = exact, amber = within the threshold, red = above it (current theme's tokens, see ThemePalette).</summary>
    public static string DifferenceColorFor(decimal difference, decimal threshold) =>
        difference == 0 ? ThemePalette.SuccessBright : Math.Abs(difference) <= threshold ? ThemePalette.WarningBright : ThemePalette.DangerBright;

    private string UserName => _session.CurrentUser?.Name ?? (CurrentShift?.CashierName ?? "Cajero");

    private async Task PrintAsync(System.Collections.Generic.IReadOnlyList<ReceiptLine> lines, string documentName)
    {
        try
        {
            if (!await _printer.PrintLinesAsync(lines, documentName))
                Notification = T("Cash.Print.FailedSaved", documentName);
        }
        catch (Exception ex)
        {
            Notification = T("Cash.Print.Failed", documentName, ex.Message);
        }
    }

    private void UpdateSyncStatus()
    {
        if (!_sync.IsEnabled)
        {
            SyncStatusText = T("Cash.Sync.Disabled");
            HasFailedSync = false;
            return;
        }

        var (pending, failed, _) = _sync.Counts;
        var last = _sync.LastRunAt is { } t ? t.ToLocalTime().ToString("HH:mm") : "—";
        var text = T("Cash.Sync.Status", pending, failed, T(failed == 1 ? "Cash.Sync.Failed.One" : "Cash.Sync.Failed.Many"), last);
        if (_sync.LastError != null) text += T("Cash.Sync.SendError");
        SyncStatusText = text;
        HasFailedSync = failed > 0;
    }

    /// <summary>"🔁 Reintentar fallidos": Failed → Pending so the next pass sends them again.</summary>
    [RelayCommand]
    private async Task RetryFailedSyncAsync()
    {
        if (IsRetryingSync) return;
        IsRetryingSync = true;
        try
        {
            var reset = await _sync.RetryFailedAsync();
            Notification = reset > 0 ? T("Cash.Sync.Requeued", reset) : T("Cash.Sync.NoFailed");
            if (reset > 0) _ = _sync.RunOnceAsync(); // don't wait for the next tick
        }
        finally
        {
            IsRetryingSync = false;
            UpdateSyncStatus();
        }
    }

    /// <summary>Called when the tab is shown: shift + dashboard.</summary>
    public async Task LoadCurrentShiftAsync()
    {
        try
        {
            CurrentShift = await WithShiftServiceAsync(s => s.GetCurrentShiftAsync());
            IsShiftOpen = CurrentShift != null;
            UpdateShiftStatusDisplay();
            OnSessionChanged();
            await RefreshReconciliationAsync();
        }
        catch (Exception ex)
        {
            Notification = T("Cash.Error.LoadShift", ex.Message);
        }
    }

    /// <summary>Dashboard figures + today's movements. Silent on failure (keeps the last figures).</summary>
    public async Task RefreshReconciliationAsync()
    {
        if (!IsShiftOpen)
        {
            Reconciliation = null;
            Movements.Clear();
            return;
        }
        try
        {
            var r = await WithShiftServiceAsync(s => s.GetReconciliationAsync());
            Reconciliation = r;
            Movements.Clear();
            foreach (var m in r.Movements) Movements.Insert(0, new CashMovementRowViewModel(m)); // newest first
        }
        catch (Exception ex)
        {
            Notification = T("Cash.Error.RefreshSummary", ex.Message);
        }
    }

    private void OnThemeChanged()
    {
        UpdateShiftStatusDisplay();
        OnPropertyChanged(nameof(CountLiveDifferenceColor));
        OnPropertyChanged(nameof(CloseDifferenceColor));
    }

    private void OnLanguageChanged()
    {
        UpdateShiftStatusDisplay();
        UpdateSyncStatus();
        foreach (var name in new[] { nameof(ExpectedCashText), nameof(OpeningText), nameof(SalesDetailText), nameof(TerminalCardDetailText),
                     nameof(BillPaymentsDetailText), nameof(MovementsDetailText), nameof(SuggestedOpeningText), nameof(StepTitle),
                     nameof(OpenComandasText), nameof(CloseOrderCountText), nameof(CloseCardExpectedText), nameof(CloseTransferExpectedText),
                     nameof(CloseBillPaymentsText), nameof(CloseMovementsText), nameof(CountExpectedText), nameof(CloseVerdictText),
                     nameof(ThresholdText), nameof(DifferenceApprovalText), nameof(FloatError), nameof(MovementDirectionText) })
            OnPropertyChanged(name);
        if (IsMovementDialogVisible) RefreshMovementChips();
        if (IsZReportVisible) ApplyZReportTexts();
        if (IsDayConsolidationVisible) RenderDay();
        // Row VMs (movements, history, day rows other than RenderDay) keep their text until the next refresh.
    }

    private void UpdateShiftStatusDisplay()
    {
        if (IsShiftOpen && CurrentShift != null)
        {
            var elapsed = DateTime.UtcNow - CurrentShift.OpenedAt;
            var terminal = string.IsNullOrWhiteSpace(CurrentShift.TerminalCode) ? string.Empty : $"{CurrentShift.TerminalCode} · ";
            ShiftStatusText = T("Cash.Status.Open", terminal, CurrentShift.ShiftNumber, CurrentShift.CashierName,
                CurrentShift.OpenedAt.ToLocalTime().ToString("HH:mm"), (int)elapsed.TotalHours, elapsed.Minutes);
            ShiftStatusColor = ThemePalette.Success;
        }
        else
        {
            ShiftStatusText = "🔴 " + T("Status.NoShift");
            ShiftStatusColor = ThemePalette.Danger;
        }
    }

    // ── Open Shift Flow ──────────────────────────────────────────────────────
    [RelayCommand]
    public async Task ShowOpenDialogAsync()
    {
        CashierName = _session.CurrentUser?.Name ?? string.Empty; // pre-filled with the signed-in user
        try
        {
            _suggestedOpening = await WithShiftServiceAsync(s => s.GetSuggestedOpeningAmountAsync());
        }
        catch
        {
            _suggestedOpening = _settings.DefaultFloat;
        }
        OpeningAmountStr = _suggestedOpening.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        _openingPristine = true;
        OnPropertyChanged(nameof(SuggestedOpeningText));
        IsOpenDialogVisible = true;
    }

    [RelayCommand]
    public void AppendOpeningNumpad(string digit)
    {
        // The first key replaces the pre-filled suggestion instead of appending to it.
        OpeningAmountStr = AppendDigits(_openingPristine ? "0" : OpeningAmountStr, digit);
        _openingPristine = false;
    }

    [RelayCommand]
    public void ClearOpeningNumpad() { OpeningAmountStr = "0"; _openingPristine = false; }

    [RelayCommand]
    public void UseSuggestedOpening()
    {
        OpeningAmountStr = _suggestedOpening.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        _openingPristine = true;
    }

    [RelayCommand]
    public async Task ConfirmOpenShiftAsync()
    {
        if (string.IsNullOrWhiteSpace(CashierName))
        {
            Notification = T("Cash.Open.NeedCashier");
            return;
        }
        var amount = ParseAmount(OpeningAmountStr);
        if (amount < 0)
        {
            Notification = T("Cash.Open.InvalidAmount");
            return;
        }
        try
        {
            IsBusy = true;
            var userId = _session.CurrentUser?.Id;
            CurrentShift = await WithShiftServiceAsync(s => s.OpenShiftAsync(CashierName.Trim(), amount, userId));
            IsShiftOpen = true;
            UpdateShiftStatusDisplay();
            IsOpenDialogVisible = false;
            Notification = T("Cash.Open.Done", CurrentShift.ShiftNumber, ReceiptFormatter.Cop(amount));
            await RefreshReconciliationAsync();
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void CancelOpenDialog() => IsOpenDialogVisible = false;
}
