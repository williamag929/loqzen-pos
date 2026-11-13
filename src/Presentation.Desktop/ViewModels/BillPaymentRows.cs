using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Enums;

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
    public string Color => string.IsNullOrWhiteSpace(Dto.ColorHex) ? "#334155" : Dto.ColorHex!;
    public string CommissionText => CommissionLabel(Dto);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderColor))]
    private bool _isSelected;
    public string BorderColor => IsSelected ? "#FACC15" : "#00000000";

    /// <summary>"Comisión $1.500", "3 %", "$1.000 + 2 %" or "Sin comisión".</summary>
    public static string CommissionLabel(BillerDto b)
    {
        var pct = b.CommissionPercent * 100m;
        var pctText = $"{pct:0.##} %";
        if (b.CommissionFixed > 0 && b.CommissionPercent > 0) return $"Comisión {ReceiptFormatter.Cop(b.CommissionFixed)} + {pctText}";
        if (b.CommissionFixed > 0) return $"Comisión {ReceiptFormatter.Cop(b.CommissionFixed)}";
        if (b.CommissionPercent > 0) return $"Comisión {pctText}";
        return "Sin comisión";
    }
}

/// <summary>Bill payment of the open shift, with reprint / void.</summary>
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
    public string Detail => $"Ref. {Dto.AccountReference}" + (string.IsNullOrWhiteSpace(Dto.CustomerName) ? "" : $" · {Dto.CustomerName}");
    public string TimeText => $"{Dto.PaidAt.ToLocalTime():HH:mm} · {ReceiptDtoFactory.PaymentName(Dto.Method)} · {Dto.UserName}";
    public string TotalText => ReceiptFormatter.Cop(Dto.Total);
    public string StatusText => IsVoided ? $"ANULADO{(string.IsNullOrWhiteSpace(Dto.VoidReason) ? "" : " · " + Dto.VoidReason)}" : string.Empty;
    public string TextColor => IsVoided ? "#64748B" : "White";
}
