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

    // ── Cierre del día (all registers, one date) ─────────────────────────────
    [ObservableProperty] private bool _isDayConsolidationVisible;
    [ObservableProperty] private bool _isLoadingDay;
    [ObservableProperty] private string _dayTitle = string.Empty;
    [ObservableProperty] private string _daySubtitle = string.Empty;
    [ObservableProperty] private string _dayWarning = string.Empty;
    [ObservableProperty] private bool _canGoNextDay;
    private DateOnly _consolidationDay = DateOnly.FromDateTime(DateTime.Now);
    private DayConsolidationDto? _dayDto;

    public ObservableCollection<TerminalDayRowViewModel> DayRows { get; } = new();
    public bool HasDayRows => DayRows.Count > 0;
    public bool HasDayWarning => !string.IsNullOrWhiteSpace(DayWarning);
    partial void OnDayWarningChanged(string value) => OnPropertyChanged(nameof(HasDayWarning));

    /// <summary>"📊 Cierre del día": opens on today (local date).</summary>
    [RelayCommand]
    public async Task ShowDayConsolidationAsync()
    {
        _consolidationDay = DateOnly.FromDateTime(DateTime.Now);
        IsDayConsolidationVisible = true;
        await LoadDayAsync();
    }

    [RelayCommand]
    public async Task PreviousDayAsync()
    {
        _consolidationDay = _consolidationDay.AddDays(-1);
        await LoadDayAsync();
    }

    [RelayCommand]
    public async Task NextDayAsync()
    {
        if (!CanGoNextDay) return;
        _consolidationDay = _consolidationDay.AddDays(1);
        await LoadDayAsync();
    }

    [RelayCommand]
    public void CloseDayConsolidation()
    {
        IsDayConsolidationVisible = false;
        DayRows.Clear();
        _dayDto = null;
        OnPropertyChanged(nameof(HasDayRows));
    }

    private static readonly string[] SpanishDays = { "domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado" };

    private async Task LoadDayAsync()
    {
        if (IsLoadingDay) return;
        IsLoadingDay = true;
        var today = DateOnly.FromDateTime(DateTime.Now);
        CanGoNextDay = _consolidationDay < today;
        DayTitle = _consolidationDay == today
            ? $"📊 Cierre del día · Hoy {_consolidationDay:dd/MM/yyyy}"
            : $"📊 Cierre del día · {SpanishDays[(int)_consolidationDay.DayOfWeek]} {_consolidationDay:dd/MM/yyyy}";
        DayWarning = string.Empty;
        try
        {
            var dto = await WithShiftServiceAsync(s => s.GetDayConsolidationAsync(_consolidationDay));
            _dayDto = dto;
            DayRows.Clear();
            foreach (var t in dto.Terminals) DayRows.Add(new TerminalDayRowViewModel(t, _settings.MaxDifferenceWithoutManager));
            if (dto.Terminals.Count > 0) DayRows.Add(new TerminalDayRowViewModel(dto, _settings.MaxDifferenceWithoutManager));
            DaySubtitle = dto.Terminals.Count == 0
                ? "Sin turnos ni ventas en esta fecha."
                : $"{dto.OrderCount} facturas · Ventas {ReceiptFormatter.Cop(dto.TotalSales)} · Recaudos {ReceiptFormatter.Cop(dto.BillPaymentsTotal)} · Diferencia {SignedCop(dto.Difference)}";
            if (dto.OpenShiftCount > 0)
                DayWarning = $"⚠️ {dto.OpenShiftCount} turno{(dto.OpenShiftCount == 1 ? "" : "s")} todavía abierto{(dto.OpenShiftCount == 1 ? "" : "s")}: las cifras de esa caja son parciales.";
        }
        catch (Exception ex)
        {
            _dayDto = null;
            DayRows.Clear();
            DaySubtitle = string.Empty;
            DayWarning = $"❌ No se pudo consolidar el día: {ex.Message}";
        }
        finally
        {
            IsLoadingDay = false;
            OnPropertyChanged(nameof(HasDayRows));
        }
    }

    /// <summary>"🖨️ Imprimir": same printer path as the Z report.</summary>
    [RelayCommand]
    public async Task PrintDayConsolidationAsync()
    {
        if (_dayDto is not { } dto) return;
        System.Collections.Generic.IReadOnlyList<ReceiptLine> lines;
        try
        {
            lines = CashReports.DayConsolidationReport(dto, StoreName);
        }
        catch (Exception ex)
        {
            Notification = $"⚠️ No se pudo generar el cierre del día: {ex.Message}";
            return;
        }
        await PrintAsync(lines, $"Cierre-{dto.Day:yyyyMMdd}");
    }

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
