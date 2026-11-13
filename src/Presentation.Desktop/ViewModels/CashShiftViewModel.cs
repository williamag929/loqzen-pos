using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;

namespace Presentation.Desktop.ViewModels;

public partial class CashShiftViewModel : ObservableObject
{
    private readonly ICashShiftService _service;
    private readonly IUserSession _session;

    // ── Current Shift State ──────────────────────────────────────────────────
    [ObservableProperty] private CashShift? _currentShift;
    [ObservableProperty] private bool _isShiftOpen = false;
    [ObservableProperty] private string _shiftStatusText = "Sin turno abierto";
    [ObservableProperty] private string _shiftStatusColor = "#EF4444";

    // ── Open Shift Dialog ────────────────────────────────────────────────────
    [ObservableProperty] private bool _isOpenDialogVisible = false;
    [ObservableProperty] private string _cashierName = string.Empty;
    [ObservableProperty] private string _openingAmountStr = "0";

    // ── Close Shift Dialog ───────────────────────────────────────────────────
    [ObservableProperty] private bool _isCloseDialogVisible = false;
    [ObservableProperty] private string _closingAmountStr = "0";
    [ObservableProperty] private string _closeNotes = string.Empty;
    [ObservableProperty] private decimal _expectedAmount = 0m;
    [ObservableProperty] private decimal _differenceAmount = 0m;
    [ObservableProperty] private string _differenceColor = "#10B981";

    // ── Z-Report / Resultado arqueo ──────────────────────────────────────────
    [ObservableProperty] private bool _isZReportVisible = false;
    [ObservableProperty] private CashShift? _lastClosedShift;

    [ObservableProperty] private string _notification = string.Empty;

    public CashShiftViewModel(ICashShiftService cashShiftService, IUserSession session)
    {
        _service = cashShiftService;
        _session = session;
        _ = LoadCurrentShiftAsync();
    }

    public async Task LoadCurrentShiftAsync()
    {
        try
        {
            CurrentShift = await _service.GetCurrentShiftAsync();
            IsShiftOpen = CurrentShift != null;
            UpdateShiftStatusDisplay();
        }
        catch (Exception ex)
        {
            Notification = $"Error al cargar turno: {ex.Message}";
        }
    }

    private void UpdateShiftStatusDisplay()
    {
        if (IsShiftOpen && CurrentShift != null)
        {
            var elapsed = DateTime.UtcNow - CurrentShift.OpenedAt;
            ShiftStatusText = $"🟢 Turno #{CurrentShift.ShiftNumber} abierto – {CurrentShift.CashierName} " +
                              $"| Inicio: {CurrentShift.OpenedAt.ToLocalTime():HH:mm} " +
                              $"| {(int)elapsed.TotalHours}h {elapsed.Minutes}m";
            ShiftStatusColor = "#10B981";
        }
        else
        {
            ShiftStatusText = "🔴 Sin turno abierto – Debe abrir turno para registrar ventas";
            ShiftStatusColor = "#EF4444";
        }
    }

    // ── Open Shift Flow ──────────────────────────────────────────────────────
    [RelayCommand]
    public void ShowOpenDialog()
    {
        CashierName = _session.CurrentUser?.Name ?? string.Empty; // pre-filled with the signed-in user
        OpeningAmountStr = "0";
        IsOpenDialogVisible = true;
    }

    [RelayCommand]
    public void AppendOpeningNumpad(string digit)
    {
        var current = OpeningAmountStr == "0" ? "" : OpeningAmountStr;
        if (current.Length < 10) OpeningAmountStr = string.IsNullOrEmpty(current) ? digit : current + digit;
    }

    [RelayCommand]
    public void ClearOpeningNumpad() => OpeningAmountStr = "0";

    [RelayCommand]
    public async Task ConfirmOpenShiftAsync()
    {
        if (string.IsNullOrWhiteSpace(CashierName))
        {
            Notification = "⚠️ Ingrese el nombre del cajero.";
            return;
        }
        if (!decimal.TryParse(OpeningAmountStr, out var amount))
        {
            Notification = "⚠️ Monto inicial inválido.";
            return;
        }
        try
        {
            CurrentShift = await _service.OpenShiftAsync(CashierName.Trim(), amount);
            IsShiftOpen = true;
            UpdateShiftStatusDisplay();
            IsOpenDialogVisible = false;
            Notification = $"✅ Turno #{CurrentShift.ShiftNumber} abierto con ${amount:N0} de fondo inicial.";
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
    }

    [RelayCommand]
    public void CancelOpenDialog() => IsOpenDialogVisible = false;

    // ── Cuadre de caja (arqueo por denominaciones) ───────────────────────────
    // Colombian peso denominations currently in circulation.
    private static readonly decimal[] Bills = { 100000m, 50000m, 20000m, 10000m, 5000m, 2000m };
    private static readonly decimal[] Coins = { 1000m, 500m, 200m, 100m, 50m };

    public System.Collections.ObjectModel.ObservableCollection<CashCountRowViewModel> CountRows { get; } = new();

    [ObservableProperty] private Core.Application.DTOs.CashCloseSummaryDto? _closeSummary;

    /// <summary>Counted cash = sum of denomination rows (no free typing of the total → fewer errors).</summary>
    public decimal CountedTotal => CountRows.Sum(r => r.Subtotal);

    private void OnCountChanged()
    {
        ClosingAmountStr = CountedTotal.ToString("0");
        OnPropertyChanged(nameof(CountedTotal));
        RecalcDifference();
    }

    // ── Close Shift Flow ─────────────────────────────────────────────────────
    [RelayCommand]
    public async Task ShowCloseDialogAsync()
    {
        if (CurrentShift == null) return;
        try
        {
            CloseSummary = await _service.GetCloseSummaryAsync();
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
            return;
        }

        ExpectedAmount = CloseSummary.ExpectedCash;
        CountRows.Clear();
        foreach (var d in Bills) CountRows.Add(new CashCountRowViewModel(d, isCoin: false, OnCountChanged));
        foreach (var d in Coins) CountRows.Add(new CashCountRowViewModel(d, isCoin: true, OnCountChanged));
        // Start at 0 counted (not pre-filled with the expected amount, which hid real differences).
        OnCountChanged();
        CloseNotes = string.Empty;
        IsCloseDialogVisible = true;
    }

    [RelayCommand]
    public void AppendClosingNumpad(string digit)
    {
        var current = ClosingAmountStr == "0" ? "" : ClosingAmountStr;
        if (current.Length < 10)
        {
            ClosingAmountStr = string.IsNullOrEmpty(current) ? digit : current + digit;
            RecalcDifference();
        }
    }

    [RelayCommand]
    public void ClearClosingNumpad()
    {
        ClosingAmountStr = "0";
        RecalcDifference();
    }

    private void RecalcDifference()
    {
        if (decimal.TryParse(ClosingAmountStr, out var closing))
        {
            DifferenceAmount = closing - ExpectedAmount;
            DifferenceColor = DifferenceAmount >= 0 ? "#10B981" : "#EF4444";
        }
    }

    [RelayCommand]
    public async Task ConfirmCloseShiftAsync()
    {
        try
        {
            var lines = CountRows
                .Where(r => r.Quantity > 0)
                .Select(r => new Core.Application.DTOs.CashCountLineDto { Denomination = r.Denomination, Quantity = r.Quantity })
                .ToList();
            var closed = await _service.CloseShiftWithCountAsync(lines, CloseNotes);
            LastClosedShift = closed;
            CurrentShift = null;
            IsShiftOpen = false;
            UpdateShiftStatusDisplay();
            IsCloseDialogVisible = false;
            IsZReportVisible = true;
            Notification = $"✅ Turno #{closed.ShiftNumber} cerrado. Diferencia: ${closed.Difference:N0}";
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
    }

    [RelayCommand]
    public void CancelCloseDialog() => IsCloseDialogVisible = false;

    [RelayCommand]
    public void DismissZReport()
    {
        IsZReportVisible = false;
        LastClosedShift = null;
    }
}
