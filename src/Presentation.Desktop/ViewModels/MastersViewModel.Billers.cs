using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;

namespace Presentation.Desktop.ViewModels;

/// <summary>Row of the billers list (own Edit command, no #Root binding).</summary>
public sealed class BillerRowViewModel
{
    public BillerRowViewModel(BillerDto dto, Action<BillerDto> onEdit)
    {
        Dto = dto;
        EditCommand = new RelayCommand(() => onEdit(dto));
    }

    public BillerDto Dto { get; }
    public IRelayCommand EditCommand { get; }
    public string Name => Dto.Name;
    public string CodeText => string.IsNullOrWhiteSpace(Dto.Code) ? "Sin código" : $"Código {Dto.Code}";
    public string CommissionText => BillerTileViewModel.CommissionLabel(Dto);
    public string Color => string.IsNullOrWhiteSpace(Dto.ColorHex) ? "#334155" : Dto.ColorHex!;
    public bool IsActive => Dto.IsActive;
    public string OrderText => $"Orden {Dto.SortOrder}";
}

/// <summary>"🏢 Convenios": companies whose bills the store collects (recaudos) and their commission.</summary>
public partial class MastersViewModel
{
    public bool IsBillersTab => ActiveTab == MastersTab.Billers;
    public ObservableCollection<BillerRowViewModel> Billers { get; } = new();
    public ObservableCollection<ChipViewModel> BillerColorChips { get; } = new();

    [ObservableProperty] private bool _isBillerFormVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BillerFormTitle))]
    private bool _isEditingBiller;
    public string BillerFormTitle => IsEditingBiller ? "🏢 Editar convenio" : "🏢 Nuevo convenio";

    [ObservableProperty] private Guid _editBillerId;
    [ObservableProperty] private string _billerName = string.Empty;
    [ObservableProperty] private string _billerCode = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BillerExampleText))]
    private string _billerFixedStr = "0";
    /// <summary>Percent as typed by the user ("3" = 3 %); stored as a fraction (0.03).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BillerExampleText))]
    private string _billerPercentStr = "0";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BillerColorPreview))]
    private string _billerColorHex = "#2563EB";
    [ObservableProperty] private string _billerSortOrderStr = "0";
    [ObservableProperty] private bool _billerIsActive = true;

    public string BillerColorPreview => IsHexColor(BillerColorHex) ? BillerColorHex : "#334155";

    /// <summary>Live example so the cashier understands the commission before saving.</summary>
    public string BillerExampleText
    {
        get
        {
            var dto = new BillerDto { CommissionFixed = ParseMoney(BillerFixedStr) ?? 0, CommissionPercent = (ParseMoney(BillerPercentStr) ?? 0) / 100m };
            return $"Ejemplo: factura de {ReceiptFormatter.Cop(100000)} → comisión {ReceiptFormatter.Cop(dto.CommissionFor(100000))}";
        }
    }

    private static readonly string[] BillerColors = { "#2563EB", "#059669", "#DC2626", "#D97706", "#7C3AED", "#DB2777", "#0891B2", "#475569" };

    private static bool IsHexColor(string? s) =>
        !string.IsNullOrWhiteSpace(s) && System.Text.RegularExpressions.Regex.IsMatch(s, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$");

    private static decimal? ParseMoney(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        var t = s.Trim().Replace("%", "").Replace("$", "").Replace(" ", "").Replace(",", ".");
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [RelayCommand]
    public void ShowBillers()
    {
        ActiveTab = MastersTab.Billers;
        _ = LoadBillersAsync();
    }

    private async Task LoadBillersAsync()
    {
        try
        {
            var items = await _billPayments.GetBillersAsync(includeInactive: true);
            Billers.Clear();
            foreach (var b in items.OrderBy(b => b.SortOrder).ThenBy(b => b.Name)) Billers.Add(new BillerRowViewModel(b, EditBiller));
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
    }

    private void EnsureColorChips()
    {
        if (BillerColorChips.Count > 0) return;
        foreach (var c in BillerColors) BillerColorChips.Add(new ChipViewModel(c, v => BillerColorHex = v));
    }

    [RelayCommand]
    public void NewBiller()
    {
        EnsureColorChips();
        IsEditingBiller = false;
        EditBillerId = Guid.Empty;
        BillerName = string.Empty;
        BillerCode = string.Empty;
        BillerFixedStr = "0";
        BillerPercentStr = "0";
        BillerColorHex = BillerColors[Billers.Count % BillerColors.Length];
        BillerSortOrderStr = (Billers.Count == 0 ? 0 : Billers.Max(b => b.Dto.SortOrder) + 1).ToString(CultureInfo.InvariantCulture);
        BillerIsActive = true;
        IsBillerFormVisible = true;
    }

    private void EditBiller(BillerDto b)
    {
        EnsureColorChips();
        IsEditingBiller = true;
        EditBillerId = b.Id;
        BillerName = b.Name;
        BillerCode = b.Code ?? string.Empty;
        BillerFixedStr = b.CommissionFixed.ToString("0", CultureInfo.InvariantCulture);
        BillerPercentStr = (b.CommissionPercent * 100m).ToString("0.##", CultureInfo.InvariantCulture);
        BillerColorHex = string.IsNullOrWhiteSpace(b.ColorHex) ? "#2563EB" : b.ColorHex!;
        BillerSortOrderStr = b.SortOrder.ToString(CultureInfo.InvariantCulture);
        BillerIsActive = b.IsActive;
        IsBillerFormVisible = true;
    }

    [RelayCommand]
    public async Task SaveBillerAsync()
    {
        if (string.IsNullOrWhiteSpace(BillerName)) { Notification = "⚠️ El nombre del convenio es obligatorio."; return; }
        var fixedAmount = ParseMoney(BillerFixedStr);
        var percent = ParseMoney(BillerPercentStr);
        if (fixedAmount is null or < 0) { Notification = "⚠️ Comisión fija inválida."; return; }
        if (percent is null or < 0 or > 20) { Notification = "⚠️ El porcentaje debe estar entre 0 y 20 %."; return; }
        if (!int.TryParse(BillerSortOrderStr, out var order)) order = 0;
        if (!IsHexColor(BillerColorHex)) { Notification = "⚠️ Color inválido (use #RRGGBB)."; return; }

        try
        {
            await _billPayments.SaveBillerAsync(new BillerDto
            {
                Id = IsEditingBiller ? EditBillerId : Guid.Empty,
                Name = BillerName.Trim(),
                Code = string.IsNullOrWhiteSpace(BillerCode) ? null : BillerCode.Trim(),
                CommissionFixed = fixedAmount.Value,
                CommissionPercent = percent.Value / 100m,
                ColorHex = BillerColorHex.Trim(),
                SortOrder = order,
                IsActive = BillerIsActive
            });
            IsBillerFormVisible = false;
            Notification = $"✅ Convenio \"{BillerName.Trim()}\" guardado";
            await LoadBillersAsync();
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
    }

    [RelayCommand] public void CancelBillerForm() => IsBillerFormVisible = false;
}
