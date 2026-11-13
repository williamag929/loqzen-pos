using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Desktop.ViewModels;

/// <summary>Panel 0: open comandas + table map.</summary>
public partial class ComandaViewModel
{
    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RefreshListOnlyAsync();
        if (Current == null) return;

        using var scope = _scopes.CreateScope();
        var fresh = await scope.ServiceProvider.GetRequiredService<IComandaService>().GetByIdAsync(Current.Id);
        if (fresh == null || fresh.Status is ComandaStatus.Closed or ComandaStatus.Cancelled)
            ClearCurrent();
        else
            Apply(fresh);
    }

    private async Task RefreshListOnlyAsync()
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var svc = sp.GetRequiredService<IComandaService>();
        var comandas = await svc.GetActiveComandasAsync();
        var tables = await svc.GetTablesAsync();
        var shift = await sp.GetRequiredService<ICashShiftService>().GetCurrentShiftAsync();

        IsShiftOpen = shift != null;
        _shiftId = shift?.Id;
        CashierName = shift?.CashierName ?? string.Empty;

        Comandas.Clear();
        foreach (var c in comandas)
            Comandas.Add(new ComandaCardViewModel(c, c.Id == Current?.Id, UtcNow, SelectComanda));

        Tables.Clear();
        foreach (var t in tables)
            Tables.Add(new TableTileViewModel(t, TapTable));
    }

    private async void SelectComanda(ComandaCardViewModel card)
    {
        Apply(card.Dto);
        PanelIndex = card.Dto.Status == ComandaStatus.ReadyToPay ? 2 : 1;
        await RefreshListOnlyAsync();
    }

    /// <summary>Busy table → jump to its comanda. Free table → open a new comanda there.</summary>
    private async void TapTable(TableTileViewModel table)
    {
        if (table.Dto.OpenComandaId is { } id)
        {
            var card = Comandas.FirstOrDefault(c => c.Dto.Id == id);
            if (card != null) SelectComanda(card);
            return;
        }
        await RunAsync(async svc =>
        {
            Apply(await svc.OpenComandaAsync(table.Name, WaiterName, table.Dto.Id));
            BackToCategories();
            PanelIndex = 1;
            StatusMessage = $"✅ Comanda abierta en {table.Name}";
        });
        await RefreshListOnlyAsync();
    }

    private void ClearCurrent()
    {
        Current = null;
        Items.Clear();
        ReceiptPreview.Clear();
        PanelIndex = 0;
        PushToCustomerDisplay(); // Current == null → checkout cart if it has items, otherwise welcome screen
    }
}
