using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>Z report screen (after closing or from the history) and the closed-shift history list.</summary>
public partial class CashShiftViewModel
{
    // ── Z report ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isZReportVisible;
    [ObservableProperty] private string _zReportTitle = string.Empty;
    [ObservableProperty] private string _zReportSummary = string.Empty;
    [ObservableProperty] private string _zReportSummaryColor = ThemePalette.SuccessBright;
    public ObservableCollection<ReceiptLine> ZReportLines { get; } = new();
    private int _zShiftNumber;
    private ShiftReconciliationDto? _zRecon;
    private bool _zFromClose;

    private void ShowZReport(ShiftReconciliationDto r, bool isFromClose)
    {
        _zShiftNumber = r.ShiftNumber;
        _zRecon = r;
        _zFromClose = isFromClose;
        ApplyZReportTexts();

        ZReportLines.Clear();
        try
        {
            foreach (var line in CashReports.ZReport(r, StoreName)) ZReportLines.Add(line);
        }
        catch (Exception ex)
        {
            Notification = T("Cash.Z.GenerateFailed", ex.Message);
        }
        IsZReportVisible = true;
    }

    /// <summary>Title + one-line summary above the (Spanish, printed) Z lines; re-run when the language changes.</summary>
    private void ApplyZReportTexts()
    {
        if (_zRecon is not { } r) return;
        ZReportTitle = _zFromClose ? T("Cash.Z.TitleClosed", r.ShiftNumber) : T("Cash.Z.Title", r.ShiftNumber);
        var diff = r.Difference ?? 0m;
        ZReportSummary = r.CountedCash is null
            ? T("Cash.Z.NoCount")
            : T("Cash.Z.Summary", ReceiptFormatter.Cop(r.ExpectedCash), ReceiptFormatter.Cop(r.CountedCash.Value), SignedCop(diff));
        ZReportSummaryColor = DifferenceColorFor(diff, _settings.MaxDifferenceWithoutManager);
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
        _zRecon = null;
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

    private async Task LoadDayAsync()
    {
        if (IsLoadingDay) return;
        IsLoadingDay = true;
        var today = DateOnly.FromDateTime(DateTime.Now);
        CanGoNextDay = _consolidationDay < today;
        UpdateDayTitle();
        DayWarning = string.Empty;
        try
        {
            var dto = await WithShiftServiceAsync(s => s.GetDayConsolidationAsync(_consolidationDay));
            _dayDto = dto;
            RenderDay();
        }
        catch (Exception ex)
        {
            _dayDto = null;
            DayRows.Clear();
            DaySubtitle = string.Empty;
            DayWarning = T("Cash.Day.LoadFailed", ex.Message);
        }
        finally
        {
            IsLoadingDay = false;
            OnPropertyChanged(nameof(HasDayRows));
        }
    }

    /// <summary>Day name comes from the UI culture (es-CO "miércoles" / en-US "Wednesday"); the date keeps dd/MM/yyyy.</summary>
    private void UpdateDayTitle()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var date = _consolidationDay.ToString("dd/MM/yyyy");
        DayTitle = _consolidationDay == today
            ? T("Cash.Day.TitleToday", date)
            : T("Cash.Day.Title", _consolidationDay.ToString("dddd", Localizer.Instance.Culture), date);
    }

    /// <summary>Rows + subtitle + open-shift warning from the loaded day (also re-run on language change).</summary>
    private void RenderDay()
    {
        UpdateDayTitle();
        if (_dayDto is not { } dto) return;
        DayRows.Clear();
        foreach (var t in dto.Terminals) DayRows.Add(new TerminalDayRowViewModel(t, _settings.MaxDifferenceWithoutManager));
        if (dto.Terminals.Count > 0) DayRows.Add(new TerminalDayRowViewModel(dto, _settings.MaxDifferenceWithoutManager));
        DaySubtitle = dto.Terminals.Count == 0
            ? T("Cash.Day.Empty")
            : T("Cash.Day.Totals", dto.OrderCount, ReceiptFormatter.Cop(dto.TotalSales), ReceiptFormatter.Cop(dto.BillPaymentsTotal), SignedCop(dto.Difference));
        DayWarning = dto.OpenShiftCount > 0
            ? T(dto.OpenShiftCount == 1 ? "Cash.Day.OpenShifts.One" : "Cash.Day.OpenShifts.Many", dto.OpenShiftCount)
            : string.Empty;
        OnPropertyChanged(nameof(HasDayRows));
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
            Notification = T("Cash.Day.GenerateFailed", ex.Message);
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
