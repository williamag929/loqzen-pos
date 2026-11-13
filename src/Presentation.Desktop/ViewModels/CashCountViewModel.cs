using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Denomination count (arqueo) used by the close wizard and the partial count. Denominations come from
/// <see cref="CashShiftSettings"/> (never hard-coded). The total is always the sum of the rows: no free typing.
/// </summary>
public sealed partial class CashCountViewModel : ObservableObject
{
    public CashCountViewModel(CashShiftSettings settings)
    {
        foreach (var d in settings.Bills.Distinct().OrderByDescending(d => d))
            Rows.Add(new CashCountRowViewModel(d, isCoin: false, OnRowChanged));
        foreach (var d in settings.Coins.Distinct().OrderByDescending(d => d))
            Rows.Add(new CashCountRowViewModel(d, isCoin: true, OnRowChanged));
    }

    public ObservableCollection<CashCountRowViewModel> Rows { get; } = new();

    public decimal Total => Rows.Sum(r => r.Subtotal);
    public string TotalText => ReceiptFormatter.Cop(Total);
    public int PieceCount => Rows.Sum(r => r.Quantity);

    /// <summary>Raised after any quantity changes (owner recomputes differences / invalidates approvals).</summary>
    public event Action? Changed;

    private void OnRowChanged()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(PieceCount));
        Changed?.Invoke();
    }

    [RelayCommand]
    public void ResetCount()
    {
        foreach (var r in Rows) r.Quantity = 0;
    }

    public IReadOnlyList<CashCountLineDto> ToLines() => Rows
        .Where(r => r.Quantity > 0)
        .Select(r => new CashCountLineDto { Denomination = r.Denomination, Quantity = r.Quantity })
        .ToList();
}
