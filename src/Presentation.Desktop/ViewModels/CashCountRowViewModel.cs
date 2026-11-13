using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Receipts;

namespace Presentation.Desktop.ViewModels;

/// <summary>One denomination row of the cash count (arqueo): [–] qty [+] with live subtotal.</summary>
public sealed partial class CashCountRowViewModel : ObservableObject
{
    private readonly System.Action _onChanged;

    public CashCountRowViewModel(decimal denomination, bool isCoin, System.Action onChanged)
    {
        Denomination = denomination;
        IsCoin = isCoin;
        _onChanged = onChanged;
    }

    public decimal Denomination { get; }
    public bool IsCoin { get; }
    public string Label => (IsCoin ? "🪙 " : "💵 ") + ReceiptFormatter.Cop(Denomination);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtotalText))]
    private int _quantity;

    public decimal Subtotal => Denomination * Quantity;
    public string SubtotalText => ReceiptFormatter.Cop(Subtotal);

    partial void OnQuantityChanged(int value) => _onChanged();

    [RelayCommand] private void Plus() => Quantity++;
    [RelayCommand] private void PlusTen() => Quantity += 10;
    [RelayCommand] private void Minus() { if (Quantity > 0) Quantity--; }
}
