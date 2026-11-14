using System;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Entities;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Small item view models: each owns its command, so XAML templates bind directly to the item
/// (works with compiled bindings, no "#Root.DataContext.X" lookups).
/// </summary>
internal static class UiBrush
{
    /// <summary>Parses user data (category colours); anything missing or invalid falls back to the theme's accent.</summary>
    public static IBrush Parse(string? hex, string? fallback = null)
    {
        fallback ??= ThemePalette.AccentBright;
        try { return Brush.Parse(string.IsNullOrWhiteSpace(hex) ? fallback : hex); }
        catch (FormatException) { return Brush.Parse(fallback); }
    }

    /// <summary>The CURRENT theme's brush for a Styles/Theme.axaml key (for the rare VM-side brush with no XAML state).</summary>
    public static IBrush Resource(string key)
    {
        if (Avalonia.Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            && value is IBrush brush)
            return brush;
        return Brush.Parse(ThemePalette.TextFaint);
    }
}

public sealed partial class CategoryTileViewModel : ObservableObject
{
    public CategoryTileViewModel(Category category, Action<CategoryTileViewModel> onSelect)
    {
        Category = category;
        IsNeutral = string.IsNullOrWhiteSpace(category.ColorHex);
        // Category colours are user data; the "Otros" bucket has none and takes the theme's strong border tone
        // (the view switches its text to TextPrimary through the "neutral" class).
        Background = IsNeutral ? UiBrush.Resource("BorderStrong") : UiBrush.Parse(category.ColorHex);
        SelectCommand = new RelayCommand(() => onSelect(this));
    }

    public Category Category { get; }
    public string Name => Category.Name;
    public IBrush Background { get; }

    /// <summary>Only for the synthetic "Otros" tile, whose name follows the UI language (real categories are user data).</summary>
    public void Rename(string name)
    {
        Category.Name = name;
        OnPropertyChanged(nameof(Name));
    }
    /// <summary>No own colour (uncategorised products): the tile uses the theme's neutral fill + TextPrimary.</summary>
    public bool IsNeutral { get; }
    public IRelayCommand SelectCommand { get; }

    /// <summary>Selected tile: XAML class "selected" draws the TextPrimary border.</summary>
    [ObservableProperty] private bool _isSelected;
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

public sealed class TableTileViewModel : ObservableObject
{
    public TableTileViewModel(DiningTableDto dto, Action<TableTileViewModel> onTap)
    {
        Dto = dto;
        TapCommand = new RelayCommand(() => onTap(this));
    }

    public DiningTableDto Dto { get; }
    public string Name => Dto.Name;
    public bool IsBusy => Dto.OpenComandaId != null;
    /// <summary>Free = SuccessSurface, busy = WarningSurface (XAML class "busy").</summary>
    public string StateText => Localizer.Instance.T(IsBusy ? "Comanda.Table.Busy" : "Comanda.Table.Free");
    public IRelayCommand TapCommand { get; }

    public void RefreshTexts() => OnPropertyChanged(nameof(StateText));
}

public sealed class ComandaCardViewModel : ObservableObject
{
    /// <param name="ownTerminalCode">This register's code: a payment lock held by ANOTHER code shows the "💳 CAJA-02" tag.</param>
    public ComandaCardViewModel(ComandaDto dto, bool isCurrent, DateTime utcNow, Action<ComandaCardViewModel> onSelect, string? ownTerminalCode = null)
    {
        Dto = dto;
        IsCurrent = isCurrent;
        Minutes = (int)Math.Max(0, (utcNow - dto.OpenedAt).TotalMinutes);
        SelectCommand = new RelayCommand(() => onSelect(this));
        var locker = dto.PaymentLockedByTerminalCode?.Trim();
        LockTag = !string.IsNullOrWhiteSpace(locker) && !string.Equals(locker, ownTerminalCode?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? $"💳 {locker}" : string.Empty;
    }

    /// <summary>"💳 CAJA-02" when another register is collecting this table (empty otherwise).</summary>
    public string LockTag { get; }
    public bool HasLockTag => LockTag.Length > 0;

    public ComandaDto Dto { get; }
    public bool IsCurrent { get; }
    public int Minutes { get; }
    public string Title => $"{Dto.TableName}  ·  #{Dto.Number}";
    public string Subtitle => Localizer.Instance.T("Comanda.Card.Subtitle", Dto.WaiterName, Minutes, Dto.BillableItems.Count());
    public string TotalText => ReceiptFormatter.Cop(Dto.Total);

    public string StatusText => Dto.Status switch
    {
        Core.Domain.Enums.ComandaStatus.ReadyToPay => Localizer.Instance.T("Comanda.Card.ReadyToPay"),
        _ when Dto.PendingCount > 0 => Localizer.Instance.T("Comanda.Card.PendingCount", Dto.PendingCount),
        _ => Localizer.Instance.T("Comanda.Card.InService")
    };

    /// <summary>Language switch (the list is otherwise rebuilt on every refresh).</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>XAML classes: "ready" → Accent, "pending" → Warning, otherwise Success; "current" → TextPrimary border.</summary>
    public bool IsReadyToPay => Dto.Status == Core.Domain.Enums.ComandaStatus.ReadyToPay;
    public bool HasPending => !IsReadyToPay && Dto.PendingCount > 0;
    public IRelayCommand SelectCommand { get; }
}
