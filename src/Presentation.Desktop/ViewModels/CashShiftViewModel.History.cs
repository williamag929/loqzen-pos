using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;

namespace Presentation.Desktop.ViewModels;

/// <summary>Z report screen (after closing or from the history) and the closed-shift history list.</summary>
public partial class CashShiftViewModel
{
    // ── Z report ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isZReportVisible;
    [ObservableProperty] private string _zReportTitle = string.Empty;
    [ObservableProperty] private string _zReportSummary = string.Empty;
    [ObservableProperty] private string _zReportSummaryColor = "#34D399";
    public ObservableCollection<ReceiptLine> ZReportLines { get; } = new();
    private int _zShiftNumber;

    private void ShowZReport(ShiftReconciliationDto r, bool isFromClose)
    {
        _zShiftNumber = r.ShiftNumber;
        ZReportTitle = isFromClose ? $"📊 Turno #{r.ShiftNumber} cerrado · Reporte Z" : $"📊 Reporte Z · Turno #{r.ShiftNumber}";
        var diff = r.Difference ?? 0m;
        ZReportSummary = r.CountedCash is null
            ? "Turno sin conteo registrado"
            : $"Esperado {ReceiptFormatter.Cop(r.ExpectedCash)} · Contado {ReceiptFormatter.Cop(r.CountedCash.Value)} · Diferencia {SignedCop(diff)}";
        ZReportSummaryColor = DifferenceColorFor(diff, _settings.MaxDifferenceWithoutManager);

        ZReportLines.Clear();
        try
        {
            foreach (var line in CashReports.ZReport(r, StoreName)) ZReportLines.Add(line);
        }
        catch (Exception ex)
        {
            Notification = $"⚠️ No se pudo generar el reporte Z: {ex.Message}";
        }
        IsZReportVisible = true;
    }

    [RelayCommand]
    public async Task PrintZReportAsync()
    {
        if (ZReportLines.Count == 0) return;
        await PrintAsync(ZReportLines, $"Z-{_zShiftNumber}");
    }

    [RelayCommand]
    public void DismissZReport()
    {
        IsZReportVisible = false;
        ZReportLines.Clear();
    }

    // ── History ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isHistoryVisible;
    public ObservableCollection<ShiftHistoryRowViewModel> History { get; } = new();
    public bool HasHistory => History.Count > 0;

    [RelayCommand]
    public async Task ShowHistoryAsync()
    {
        IsHistoryVisible = true;
        try
        {
            var items = await WithShiftServiceAsync(s => s.GetHistoryAsync());
            History.Clear();
            foreach (var i in items) History.Add(new ShiftHistoryRowViewModel(i, _settings.MaxDifferenceWithoutManager, OpenHistoryItemAsync));
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
        OnPropertyChanged(nameof(HasHistory));
    }

    [RelayCommand] public void CloseHistory() => IsHistoryVisible = false;

    private async Task OpenHistoryItemAsync(ShiftHistoryRowViewModel row)
    {
        try
        {
            var r = await WithShiftServiceAsync(s => s.GetReconciliationAsync(row.Dto.ShiftId));
            ShowZReport(r, isFromClose: false);
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
    }
}
