using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;

namespace Presentation.Desktop.ViewModels;

/// <summary>Quick-pick chip (movement reasons, void reasons, colours). Tapping it runs the owner's action.</summary>
public sealed class ChipViewModel
{
    public ChipViewModel(string text, Action<string> onSelect, string? value = null)
    {
        Text = text;
        Value = value ?? text;
        SelectCommand = new RelayCommand(() => onSelect(Value));
    }

    public string Text { get; }
    public string Value { get; }
    public IRelayCommand SelectCommand { get; }
}

/// <summary>Row of the shift's movements list: green for cash in, red for cash out.</summary>
public sealed class CashMovementRowViewModel
{
    public CashMovementRowViewModel(CashMovementDto dto) => Dto = dto;

    public CashMovementDto Dto { get; }
    public string TypeLabel => Dto.TypeLabel;
    public string Reason => string.IsNullOrWhiteSpace(Dto.Reference) ? Dto.Reason : $"{Dto.Reason} · Ref. {Dto.Reference}";
    public string Detail => $"{Dto.OccurredAt.ToLocalTime():HH:mm} · {Dto.UserName}" + (Dto.ManagerOverrideId != null ? " · ✔ gerente" : "");
    public string AmountText => (Dto.Sign > 0 ? "+ " : "− ") + ReceiptFormatter.Cop(Dto.Amount);
    public string AmountColor => Dto.Sign > 0 ? "#34D399" : "#F87171";
}

/// <summary>Row of the shift history; tap opens that shift's Z report.</summary>
public sealed class ShiftHistoryRowViewModel
{
    public ShiftHistoryRowViewModel(ShiftHistoryItemDto dto, decimal threshold, Func<ShiftHistoryRowViewModel, System.Threading.Tasks.Task> onOpen)
    {
        Dto = dto;
        _threshold = threshold;
        OpenCommand = new AsyncRelayCommand(() => onOpen(this));
    }

    private readonly decimal _threshold;
    public ShiftHistoryItemDto Dto { get; }
    public IAsyncRelayCommand OpenCommand { get; }

    public string NumberText => $"#{Dto.ShiftNumber}";
    public string CashierName => Dto.CashierName;
    public string DatesText => Dto.ClosedAt is { } c
        ? $"{Dto.OpenedAt.ToLocalTime():dd/MM HH:mm} → {c.ToLocalTime():dd/MM HH:mm}"
        : $"{Dto.OpenedAt.ToLocalTime():dd/MM HH:mm} → (abierto)";
    public string ExpectedText => ReceiptFormatter.Cop(Dto.ExpectedAmount);
    public string CountedText => ReceiptFormatter.Cop(Dto.ClosingAmount);
    public string DifferenceText => CashShiftViewModel.SignedCop(Dto.Difference);
    public string DifferenceColor => CashShiftViewModel.DifferenceColorFor(Dto.Difference, _threshold);
    public bool HadManagerApproval => Dto.HadManagerApproval;
}

/// <summary>Row of the "Cierre del día" table: one register's slice of the day (or the totals row).</summary>
public sealed class TerminalDayRowViewModel
{
    public TerminalDayRowViewModel(TerminalDayDto dto, decimal threshold)
    {
        Code = string.IsNullOrWhiteSpace(dto.TerminalCode) ? "—" : dto.TerminalCode;
        Name = dto.TerminalName;
        ShiftCount = dto.ShiftCount;
        OpenShiftCount = dto.OpenShiftCount;
        OrderCount = dto.OrderCount;
        Cash = dto.CashSales; Card = dto.CardSales; Transfer = dto.TransferSales; Other = dto.OtherSales;
        BillPayments = dto.BillPaymentsTotal;
        Difference = dto.Difference;
        DifferenceColor = CashShiftViewModel.DifferenceColorFor(dto.Difference, threshold);
    }

    /// <summary>Totals row.</summary>
    public TerminalDayRowViewModel(DayConsolidationDto day, decimal threshold)
    {
        Code = "TOTAL";
        Name = $"{day.Terminals.Count} {(day.Terminals.Count == 1 ? "caja" : "cajas")}";
        ShiftCount = day.Terminals.Sum(t => t.ShiftCount);
        OpenShiftCount = day.OpenShiftCount;
        OrderCount = day.OrderCount;
        Cash = day.CashSales; Card = day.CardSales; Transfer = day.TransferSales; Other = day.OtherSales;
        BillPayments = day.BillPaymentsTotal;
        Difference = day.Difference;
        DifferenceColor = CashShiftViewModel.DifferenceColorFor(day.Difference, threshold);
        IsTotal = true;
    }

    public string Code { get; }
    public string Name { get; }
    public int ShiftCount { get; }
    public int OpenShiftCount { get; }
    public int OrderCount { get; }
    public decimal Cash { get; }
    public decimal Card { get; }
    public decimal Transfer { get; }
    public decimal Other { get; }
    public decimal BillPayments { get; }
    public decimal Difference { get; }
    public string DifferenceColor { get; }
    public bool IsTotal { get; }

    public decimal Total => Cash + Card + Transfer + Other;
    public bool HasOpenShifts => OpenShiftCount > 0;
    /// <summary>"2 turnos" / "1 turno · 1 abierto" (open ones in amber via HasOpenShifts).</summary>
    public string ShiftsText => $"{ShiftCount} {(ShiftCount == 1 ? "turno" : "turnos")}" + (OpenShiftCount > 0 ? $" · {OpenShiftCount} abierto{(OpenShiftCount == 1 ? "" : "s")}" : "");
    public string ShiftsColor => OpenShiftCount > 0 ? "#FBBF24" : "#CBD5E1";
    public string OrdersText => $"{OrderCount} {(OrderCount == 1 ? "factura" : "facturas")}";
    public string CashText => ReceiptFormatter.Cop(Cash);
    public string CardText => ReceiptFormatter.Cop(Card);
    public string TransferText => ReceiptFormatter.Cop(Transfer);
    public string OtherText => ReceiptFormatter.Cop(Other);
    public string BillPaymentsText => ReceiptFormatter.Cop(BillPayments);
    public string TotalText => ReceiptFormatter.Cop(Total);
    public string DifferenceText => CashShiftViewModel.SignedCop(Difference);
    /// <summary>Compact (narrow window) second line.</summary>
    public string CompactDetailText =>
        $"Efectivo {CashText} · Tarjeta {CardText} · Transf. {TransferText} · Otros {OtherText} · Recaudos {BillPaymentsText}";
    public string Background => IsTotal ? "#1E3A5F" : "#1E293B";
    public string CodeColor => IsTotal ? "#93C5FD" : "White";
}
