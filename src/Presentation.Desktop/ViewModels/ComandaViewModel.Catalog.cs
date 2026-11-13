using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Desktop.ViewModels;

public partial class ComandaViewModel
{
    // ── Category navigation (touch): categories grid → products of that category → back ──
    private readonly List<Product> _allProducts = new();

    public ObservableCollection<CategoryTileViewModel> CategoryTiles { get; } = new();
    public ObservableCollection<ProductTileViewModel> ProductTiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsingCategories), nameof(IsBrowsingProducts), nameof(CatalogTitle))]
    private CategoryTileViewModel? _selectedCategory;

    public bool IsBrowsingCategories => SelectedCategory == null;
    public bool IsBrowsingProducts => SelectedCategory != null;
    public string CatalogTitle => SelectedCategory == null ? "Categorías" : $"◀  {SelectedCategory.Name}";

    private async Task LoadCatalogAsync()
    {
        using var scope = _scopes.CreateScope();
        var categories = await scope.ServiceProvider.GetRequiredService<ICategoryService>().GetAllAsync();
        var products = await scope.ServiceProvider.GetRequiredService<IProductService>().GetAllAsync();

        _allProducts.Clear();
        _allProducts.AddRange(products.Where(p => p.IsActive));

        CategoryTiles.Clear();
        foreach (var c in categories.Where(c => c.IsActive && _allProducts.Any(p => p.CategoryId == c.Id)))
            CategoryTiles.Add(new CategoryTileViewModel(c, OpenCategory));

        // Products without category stay reachable.
        if (_allProducts.Any(p => p.CategoryId == null))
            CategoryTiles.Add(new CategoryTileViewModel(new Category { Name = "Otros", ColorHex = "#475569" }, OpenCategory));

        SelectedCategory = null;
        ProductTiles.Clear();
    }

    private void OpenCategory(CategoryTileViewModel tile)
    {
        SelectedCategory = tile;
        var isOther = tile.Name == "Otros" && !_allProducts.Any(p => p.CategoryId == tile.Category.Id);
        ProductTiles.Clear();
        foreach (var p in _allProducts.Where(p => isOther ? p.CategoryId == null : p.CategoryId == tile.Category.Id).OrderBy(p => p.Name))
            ProductTiles.Add(new ProductTileViewModel(p, AddProduct));
    }

    [RelayCommand]
    private void BackToCategories()
    {
        SelectedCategory = null;
        ProductTiles.Clear();
    }

    // ── Items ───────────────────────────────────────────────────────────────
    private async void AddProduct(ProductTileViewModel tile)
    {
        if (Current == null || !IsCurrentOpen)
        {
            StatusMessage = "⚠️ Abra o seleccione una comanda abierta primero";
            return;
        }
        await RunAsync(async svc =>
        {
            Apply(await svc.AddItemAsync(Current.Id, tile.Product.Id));
            StatusMessage = $"🟡 {tile.Name} agregado – se envía a cocina en {_settings.ConfirmationDelaySeconds}s";
        });
    }

    private async void CancelItem(ComandaItemRowViewModel row)
    {
        if (Current == null) return;
        if (row.IsConfirmed)
        {
            await CancelConfirmedItemAsync(row);
            return;
        }
        if (!row.IsPending)
        {
            StatusMessage = "⚠️ Este producto ya está anulado.";
            return;
        }
        await RunAsync(async svc =>
        {
            Apply(await svc.CancelPendingItemAsync(Current.Id, row.Dto.Id));
            StatusMessage = $"Anulado: {row.Name}";
        });
    }

    /// <summary>Item already in the kitchen: a manager must approve; the override id is stored with the cancellation.</summary>
    private async Task CancelConfirmedItemAsync(ComandaItemRowViewModel row)
    {
        if (Current == null) return;
        var comanda = Current;
        var description = $"Anular {row.Name} (ya en cocina) · {comanda.TableName}";
        var reference = $"Comanda #{comanda.Number}";

        var auth = await _authPrompt.RequestAsync(AuthorizedAction.CancelKitchenItem, description, row.Dto.Total, reference);
        if (!auth.IsAllowed)
        {
            StatusMessage = "❌ Anulación no autorizada";
            return;
        }

        var overrideDto = auth.Override;
        if (overrideDto == null)
        {
            // Action not configured to need approval, but the cancellation still needs an audit record:
            // only a signed-in manager can produce one silently.
            var user = _session.CurrentUser;
            if (user is not { CanAuthorize: true })
            {
                StatusMessage = "⚠️ Requiere gerente";
                return;
            }
            try
            {
                using var scope = _scopes.CreateScope();
                overrideDto = await scope.ServiceProvider.GetRequiredService<IManagerAuthorizationService>()
                    .AuthorizeAsUserAsync(new AuthorizationRequest(AuthorizedAction.CancelKitchenItem, description, row.Dto.Total, reference, user.Id, user.Name), user.Id);
            }
            catch (UnauthorizedAccessException)
            {
                StatusMessage = "⚠️ Requiere gerente";
                return;
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Error: {ex.Message}";
                return;
            }
        }

        await RunAsync(async svc =>
        {
            Apply(await svc.CancelConfirmedItemAsync(comanda.Id, row.Dto.Id, overrideDto.Id));
            StatusMessage = $"🗑️ Anulado en cocina: {row.Name} · autorizó {overrideDto.ManagerName}";
        });
    }

    [RelayCommand]
    private async Task SendToKitchenNowAsync()
    {
        if (Current == null) return;
        await RunAsync(async svc =>
        {
            Apply(await svc.ConfirmPendingNowAsync(Current.Id));
            StatusMessage = "🟢 Pendientes enviados a cocina";
        });
        await RefreshListOnlyAsync();
    }

    /// <summary>Replaces the current comanda state and rebuilds item rows + receipt preview.</summary>
    private void Apply(ComandaDto dto)
    {
        Current = dto;
        Items.Clear();
        foreach (var i in dto.Items.Where(i => i.Status != Core.Domain.Enums.ComandaItemStatus.Cancelled))
            Items.Add(new ComandaItemRowViewModel(i, _settings.ConfirmationDelaySeconds, UtcNow, CancelItem, OpenNotes));
        RebuildPreview();
        OnPropertyChanged(nameof(PanelTitle));
        PushToCustomerDisplay();
    }

    private void RebuildPreview()
    {
        ReceiptPreview.Clear();
        if (Current == null) return;
        var cashier = string.IsNullOrWhiteSpace(CashierName) ? WaiterName : CashierName;
        foreach (var line in ReceiptFormatter.Format(ReceiptDtoFactory.FromComanda(Current, cashier), isPreview: true))
            ReceiptPreview.Add(line);
    }

    /// <summary>Runs a service call in its own scope and surfaces errors in the status bar.</summary>
    private async Task RunAsync(Func<IComandaService, Task> action)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<IComandaService>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"⚠️ {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error: {ex.Message}";
        }
    }
}
