using System;
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
