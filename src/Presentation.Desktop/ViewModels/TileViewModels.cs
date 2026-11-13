using System;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Entities;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Small item view models: each owns its command, so XAML templates bind directly to the item
/// (works with compiled bindings, no "#Root.DataContext.X" lookups).
/// </summary>
internal static class UiBrush
{
    public static IBrush Parse(string? hex, string fallback = "#3B82F6")
    {
        try { return Brush.Parse(string.IsNullOrWhiteSpace(hex) ? fallback : hex); }
        catch (FormatException) { return Brush.Parse(fallback); }
    }
}

public sealed partial class CategoryTileViewModel : ObservableObject
{
    public CategoryTileViewModel(Category category, Action<CategoryTileViewModel> onSelect)
    {
        Category = category;
        Background = UiBrush.Parse(category.ColorHex);
        SelectCommand = new RelayCommand(() => onSelect(this));
    }

    public Category Category { get; }
    public string Name => Category.Name;
    public IBrush Background { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionBorder))]
    private bool _isSelected;

    public IBrush SelectionBorder => IsSelected ? Brushes.White : Brushes.Transparent;
}

public sealed class ProductTileViewModel
{
    public ProductTileViewModel(Product product, Action<ProductTileViewModel> onAdd)
    {
        Product = product;
        AddCommand = new RelayCommand(() => onAdd(this));
    }

    public Product Product { get; }
    public string Name => Product.Name;
    public string PriceText => ReceiptFormatter.Cop(Product.Price) + (Product.IsWeighed ? " /kg" : "");
    public bool IsWeighed => Product.IsWeighed;
    public IRelayCommand AddCommand { get; }
}

public sealed class TableTileViewModel
{
    public TableTileViewModel(DiningTableDto dto, Action<TableTileViewModel> onTap)
    {
        Dto = dto;
        TapCommand = new RelayCommand(() => onTap(this));
    }

    public DiningTableDto Dto { get; }
    public string Name => Dto.Name;
    public bool IsBusy => Dto.OpenComandaId != null;
    public string StateText => IsBusy ? "Ocupada" : "Libre";
    public IBrush Background => UiBrush.Parse(IsBusy ? "#7C2D12" : "#14532D");
    public IRelayCommand TapCommand { get; }
}

public sealed class ComandaCardViewModel
{
    public ComandaCardViewModel(ComandaDto dto, bool isCurrent, DateTime utcNow, Action<ComandaCardViewModel> onSelect)
    {
        Dto = dto;
        IsCurrent = isCurrent;
        Minutes = (int)Math.Max(0, (utcNow - dto.OpenedAt).TotalMinutes);
        SelectCommand = new RelayCommand(() => onSelect(this));
    }

    public ComandaDto Dto { get; }
    public bool IsCurrent { get; }
    public int Minutes { get; }
    public string Title => $"{Dto.TableName}  ·  #{Dto.Number}";
    public string Subtitle => $"{Dto.WaiterName} · {Minutes} min · {Dto.BillableItems.Count()} prod.";
    public string TotalText => ReceiptFormatter.Cop(Dto.Total);

    public string StatusText => Dto.Status switch
    {
        Core.Domain.Enums.ComandaStatus.ReadyToPay => "💳 Lista para pago",
        _ when Dto.PendingCount > 0 => $"⏳ {Dto.PendingCount} por confirmar",
        _ => "🟢 En servicio"
    };

    public IBrush StatusBrush => UiBrush.Parse(Dto.Status switch
    {
        Core.Domain.Enums.ComandaStatus.ReadyToPay => "#2563EB",
        _ when Dto.PendingCount > 0 => "#D97706",
        _ => "#059669"
    });

    public IBrush Border => IsCurrent ? Brushes.White : Brushes.Transparent;
    public IRelayCommand SelectCommand { get; }
}
