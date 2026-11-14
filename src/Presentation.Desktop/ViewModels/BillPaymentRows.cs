using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Enums;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>Biller tile in the Recaudos tab (colour, name, commission).</summary>
public sealed partial class BillerTileViewModel : ObservableObject
{
    public BillerTileViewModel(BillerDto dto, Action<BillerTileViewModel> onSelect)
    {
        Dto = dto;
        SelectCommand = new RelayCommand(() => onSelect(this));
    }

    public BillerDto Dto { get; }
    public IRelayCommand SelectCommand { get; }
    public string Name => Dto.Name;
    /// <summary>The biller's own colour; falls back to the theme accent so white text stays readable in both themes.</summary>
    public string Color => string.IsNullOrWhiteSpace(Dto.ColorHex) ? ThemePalette.Accent : Dto.ColorHex!;
    public string CommissionText => CommissionLabel(Dto);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderColor))]
    private bool _isSelected;
    public string BorderColor => IsSelected ? ThemePalette.WarningBright : "Transparent";

    /// <summary>Re-reads the theme-dependent colours after a Dark/Light switch.</summary>
    public void RefreshTheme()
    {
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(BorderColor));
    }

    /// <summary>Re-renders the translated commission line after an ES/EN switch.</summary>
    public void RefreshLanguage() => OnPropertyChanged(nameof(CommissionText));

    /// <summary>"Comisión $1.500", "3 %", "$1.000 + 2 %" or "Sin comisión" (translated; amounts keep the COP format).</summary>
    public static string CommissionLabel(BillerDto b)
    {
        var l = Localizer.Instance;
        var pct = b.CommissionPercent * 100m;
        var pctText = $"{pct:0.##} %";
        if (b.CommissionFixed > 0 && b.CommissionPercent > 0) return l.T("Bills.Row.CommissionBoth", ReceiptFormatter.Cop(b.CommissionFixed), pctText);
        if (b.CommissionFixed > 0) return l.T("Bills.Row.Commission", ReceiptFormatter.Cop(b.CommissionFixed));
        if (b.CommissionPercent > 0) return l.T("Bills.Row.Commission", pctText);
        return l.T("Bills.Row.NoCommission");
    }

    /// <summary>UI label of a payment method (shared Common.* keys); receipts keep <see cref="ReceiptDtoFactory.PaymentName"/>.</summary>
    public static string MethodLabel(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => Localizer.Instance.T("Common.Cash"),
        PaymentMethod.Card => Localizer.Instance.T("Common.Card"),
        PaymentMethod.Transfer => Localizer.Instance.T("Common.Transfer"),
        _ => Localizer.Instance.T("Bills.Method.Other")
    };
}

/// <summary>Bill payment of the open shift, with reprint / void. Immutable: the owner rebuilds the rows on an ES/EN switch.</summary>
public sealed class BillPaymentRowViewModel
{
    public BillPaymentRowViewModel(BillPaymentDto dto, Func<BillPaymentRowViewModel, Task> onReprint, Action<BillPaymentRowViewModel> onVoid)
    {
        Dto = dto;
        ReprintCommand = new AsyncRelayCommand(() => onReprint(this));
        VoidCommand = new RelayCommand(() => onVoid(this), () => !IsVoided);
    }

    public BillPaymentDto Dto { get; }
    public IAsyncRelayCommand ReprintCommand { get; }
    public IRelayCommand VoidCommand { get; }

    public bool IsVoided => Dto.Status == BillPaymentStatus.Voided;
    public bool CanVoid => !IsVoided;
    public string Header => $"{Dto.ReceiptNumber} · {Dto.BillerName}";
    public string Detail => Localizer.Instance.T("Bills.Row.Ref", Dto.AccountReference) + (string.IsNullOrWhiteSpace(Dto.CustomerName) ? "" : $" · {Dto.CustomerName}");
    public string TimeText => $"{Dto.PaidAt.ToLocalTime():HH:mm} · {BillerTileViewModel.MethodLabel(Dto.Method)} · {Dto.UserName}";
    public string TotalText => ReceiptFormatter.Cop(Dto.Total);
    public string StatusText => IsVoided ? Localizer.Instance.T("Bills.Row.Voided") + (string.IsNullOrWhiteSpace(Dto.VoidReason) ? "" : " · " + Dto.VoidReason) : string.Empty;
    public string TextColor => IsVoided ? ThemePalette.TextFaint : ThemePalette.TextPrimary;
}
