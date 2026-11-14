using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Enums;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>Manual cash movements (entrada, salida, gasto, retiro a bóveda, propinas) and the partial count.</summary>
public partial class CashShiftViewModel
{
    // ── Movement dialog ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _isMovementDialogVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MovementTitle), nameof(MovementIsIn), nameof(MovementAccent), nameof(MovementRequiresManager))]
    private CashMovementType _movementType = CashMovementType.PaidIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MovementAmountText))]
    private string _movementAmountStr = "0";
    [ObservableProperty] private string _movementReason = string.Empty;
    [ObservableProperty] private string _movementReference = string.Empty;
    [ObservableProperty] private string _movementError = string.Empty;

    public ObservableCollection<ChipViewModel> MovementChips { get; } = new();

    public string MovementTitle => $"{MovementIcon(MovementType)} {CashMovementTypes.Label(MovementType)}";
    public bool MovementIsIn => CashMovementTypes.Sign(MovementType) > 0;
    public string MovementAccent => MovementIsIn ? ThemePalette.Success : ThemePalette.DangerBright;
    public string MovementAmountText => ReceiptFormatter.Cop(ParseAmount(MovementAmountStr));
    public bool MovementRequiresManager => _settings.MovementRequiresManager(MovementType);
    public string MovementDirectionText => T(MovementIsIn ? "Cash.Movement.In" : "Cash.Movement.Out");

    private static string MovementIcon(CashMovementType t) => t switch
    {
        CashMovementType.PaidIn => "➕",
        CashMovementType.PaidOut => "➖",
        CashMovementType.Expense => "🧾",
        CashMovementType.SafeDrop => "🏦",
        CashMovementType.TipPayout => "💁",
        _ => "💵"
    };

    /// <summary>Quick-pick reasons per type: one i18n key per type, options separated by '|' (Cash.Movement.Chips.PaidIn …).</summary>
    private static string[] ReasonChips(CashMovementType t) => CashMovementTypes.Manual.Contains(t)
        ? T("Cash.Movement.Chips." + t).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>();

    private void RefreshMovementChips()
    {
        MovementChips.Clear();
        foreach (var c in ReasonChips(MovementType)) MovementChips.Add(new ChipViewModel(c, v => MovementReason = v));
    }

    /// <summary>Movement buttons pass the CashMovementType name as a string parameter.</summary>
    [RelayCommand]
    public void StartMovement(string type)
    {
        if (!IsShiftOpen) { Notification = T("Cash.OpenFirst"); return; }
        if (!Enum.TryParse<CashMovementType>(type, out var t) || !CashMovementTypes.Manual.Contains(t)) return;
        MovementType = t;
        OnPropertyChanged(nameof(MovementDirectionText));
        MovementAmountStr = "0";
        MovementReason = string.Empty;
        MovementReference = string.Empty;
        MovementError = string.Empty;
        RefreshMovementChips();
        IsMovementDialogVisible = true;
    }

    [RelayCommand] public void AppendMovementDigit(string digit) => MovementAmountStr = AppendDigits(MovementAmountStr, digit);
    [RelayCommand] public void ClearMovementAmount() => MovementAmountStr = "0";
    [RelayCommand] public void CancelMovement() => IsMovementDialogVisible = false;

    [RelayCommand]
    public async Task SaveMovementAsync()
    {
        if (IsBusy) return;
        var amount = ParseAmount(MovementAmountStr);
        if (amount <= 0) { MovementError = T("Cash.Movement.NeedAmount"); return; }
        if (string.IsNullOrWhiteSpace(MovementReason)) { MovementError = T("Cash.Movement.NeedReason"); return; }

        var type = MovementType;
        var reason = MovementReason.Trim();
        var reference = string.IsNullOrWhiteSpace(MovementReference) ? null : MovementReference.Trim();

        Guid? overrideId = null;
        if (_settings.MovementRequiresManager(type))
        {
            var approval = await RequiredOverride.RequestAsync(_authPrompt, _scopes, _session,
                AuthorizedAction.CashMovement, $"{CashMovementTypes.Label(type)}: {reason}", amount, reference);
            if (!approval.IsApproved) { MovementError = approval.Error ?? T("Cash.NotAuthorized"); return; }
            overrideId = approval.Override!.Id;
        }

        IsBusy = true;
        try
        {
            var user = _session.CurrentUser;
            var request = new CashMovementRequest(type, amount, reason, reference, user?.Id, UserName, overrideId);
            var dto = await WithShiftServiceAsync(s => s.RecordMovementAsync(request));
            IsMovementDialogVisible = false;
            Notification = T("Cash.Movement.Saved", CashMovementTypes.Label(type), ReceiptFormatter.Cop(amount));

            // Always print the slip (signature / envelope); a printer failure only warns.
            var shiftNumber = Reconciliation?.ShiftNumber ?? CurrentShift?.ShiftNumber ?? 0;
            await PrintAsync(CashReports.MovementSlip(dto, shiftNumber, StoreName), $"MOV-{dto.OccurredAt.ToLocalTime():HHmmss}");
            try { await _cashDrawer.OpenDrawerAsync(); } catch { /* drawer is best effort */ }
        }
        catch (Exception ex)
        {
            MovementError = $"❌ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshReconciliationAsync();
    }

    // ── Partial count (arqueo parcial) ───────────────────────────────────────
    public CashCountViewModel PartialCount { get; }

    [ObservableProperty] private bool _isPartialCountVisible;
    [ObservableProperty] private bool _hasPartialResult;
    [ObservableProperty] private string _partialExpectedText = string.Empty;
    [ObservableProperty] private string _partialCountedText = string.Empty;
    [ObservableProperty] private string _partialDifferenceText = string.Empty;
    [ObservableProperty] private string _partialDifferenceColor = ThemePalette.SuccessBright;

    [RelayCommand]
    public void ShowPartialCount()
    {
        if (!IsShiftOpen) { Notification = T("Cash.OpenFirst"); return; }
        PartialCount.ResetCount();
        HasPartialResult = false;
        IsPartialCountVisible = true;
    }

    [RelayCommand]
    public async Task SavePartialCountAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var lines = PartialCount.ToLines();
            var name = UserName;
            var result = await WithShiftServiceAsync(s => s.RecordPartialCountAsync(lines, name));
            PartialExpectedText = ReceiptFormatter.Cop(result.ExpectedAmount);
            PartialCountedText = ReceiptFormatter.Cop(result.CountedAmount);
            PartialDifferenceText = SignedCop(result.Difference);
            PartialDifferenceColor = DifferenceColorFor(result.Difference, _settings.MaxDifferenceWithoutManager);
            HasPartialResult = true;
            Notification = T("Cash.Partial.Saved", PartialDifferenceText);
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
    public async Task ClosePartialCountAsync()
    {
        IsPartialCountVisible = false;
        HasPartialResult = false;
        await RefreshReconciliationAsync();
    }
}
