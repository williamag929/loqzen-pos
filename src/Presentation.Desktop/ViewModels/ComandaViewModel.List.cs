using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
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
        if (!await RefreshListOnlyAsync()) return; // master unreachable: keep what is on screen
        if (Current == null) return;

        try
        {
            ComandaDto? fresh;
            using (var scope = _scopes.CreateScope())
                fresh = await scope.ServiceProvider.GetRequiredService<IComandaService>().GetByIdAsync(Current.Id);

            if (fresh == null || fresh.Status is ComandaStatus.Closed or ComandaStatus.Cancelled)
            {
                var table = Current.TableName;
                ClearCurrent();
                // Closed on the other register while we were looking at it: say so instead of silently vanishing.
                if (fresh is { Status: ComandaStatus.Closed, PaidByTerminalCode: { } paidBy } && !IsOwnCode(paidBy))
                    StatusMessage = $"💳 {table} ya fue cobrada en {paidBy}";
            }
            else
            {
                Apply(fresh);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex, "actualizando");
        }
    }

    /// <summary>Reloads shift (local), comandas and tables. Returns false when the master could not be reached (satellite).</summary>
    private async Task<bool> RefreshListOnlyAsync()
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;

        // The shift is always local (this register's drawer): read it first so "Sin turno" stays right even offline.
        var shift = await sp.GetRequiredService<ICashShiftService>().GetCurrentShiftAsync();
        IsShiftOpen = shift != null;
        _shiftId = shift?.Id;
        CashierName = shift?.CashierName ?? string.Empty;

        System.Collections.Generic.IReadOnlyList<ComandaDto> comandas;
        System.Collections.Generic.IReadOnlyList<DiningTableDto> tables;
        try
        {
            var svc = sp.GetRequiredService<IComandaService>();
            comandas = await svc.GetActiveComandasAsync();
            tables = await svc.GetTablesAsync();
            IsMasterOffline = false;
        }
        catch (Exception ex)
        {
            ReportError(ex, "leyendo comandas");
            return false;
        }

        // Only Open + ReadyToPay come back: a comanda closed on another register disappears here.
        var own = OwnTerminalCode;
        Comandas.Clear();
        foreach (var c in comandas)
            Comandas.Add(new ComandaCardViewModel(c, c.Id == Current?.Id, UtcNow, SelectComanda, own));

        Tables.Clear();
        foreach (var t in tables)
            Tables.Add(new TableTileViewModel(t, TapTable));
        return true;
    }

    private async void SelectComanda(ComandaCardViewModel card)
    {
        var previous = Current;
        Apply(card.Dto);
        PanelIndex = card.Dto.Status == ComandaStatus.ReadyToPay ? 2 : 1;
        if (previous != null && previous.Id != card.Dto.Id && IsOwnCode(previous.PaymentLockedByTerminalCode))
            _ = ReleasePaymentLockAsync(previous);
        if (card.Dto.Status == ComandaStatus.ReadyToPay)
            await ReservePaymentAsync(); // opening the bill reserves the table; another register's lock shows the banner
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
