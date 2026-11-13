using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Desktop.ViewModels;

/// <summary>Panel 2: close comanda → invoice → print → drawer.</summary>
public partial class ComandaViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsCard), nameof(IsTransfer))]
    private PaymentMethod _payMethod = PaymentMethod.Cash;

    public bool IsCash => PayMethod == PaymentMethod.Cash;
    public bool IsCard => PayMethod == PaymentMethod.Card;
    public bool IsTransfer => PayMethod == PaymentMethod.Transfer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TenderedText), nameof(ChangeText))]
    private decimal _tendered;

    public string TenderedText => ReceiptFormatter.Cop(Tendered);
    public string ChangeText => ReceiptFormatter.Cop(Math.Max(0, Tendered - (Current?.Total ?? 0)));

    // ── Customer display (second screen) ─────────────────────────────────────
    // The display shows whichever screen (checkout or comanda) updated it LAST — see CustomerDisplayViewModel.
    partial void OnTenderedChanged(decimal value) => PushToCustomerDisplay();
    partial void OnPayMethodChanged(PaymentMethod value) => PushToCustomerDisplay();
    partial void OnPanelIndexChanged(int value) => PushToCustomerDisplay();

    private void PushToCustomerDisplay()
    {
        var total = Current?.Total ?? 0;
        var change = PayMethod == PaymentMethod.Cash ? Math.Max(0, Tendered - total) : 0;
        _display.UpdateFromComanda(Current, IsPayPanel, Tendered, change, PayMethod.ToString());
    }

    [RelayCommand]
    private void SelectPayMethod(string method)
    {
        if (Enum.TryParse<PaymentMethod>(method, out var m)) PayMethod = m;
    }

    [RelayCommand]
    private void PayDigit(string digit)
    {
        var s = Tendered == 0 ? string.Empty : Tendered.ToString("0");
        if (s.Length + digit.Length > 9) return;
        if (decimal.TryParse(s + digit, out var v)) Tendered = v;
    }

    [RelayCommand]
    private void PayClear() => Tendered = 0;

    [RelayCommand]
    private void PayExact() => Tendered = Current?.Total ?? 0;

    /// <summary>Panel 1 → 2: freezes the comanda (pending items go to kitchen) and shows the bill.</summary>
    [RelayCommand]
    private async Task ReadyToPayAsync()
    {
        if (Current == null) return;
        await RunAsync(async svc =>
        {
            Apply(await svc.MarkReadyToPayAsync(Current.Id));
            Tendered = 0;
            PayMethod = PaymentMethod.Cash;
            PanelIndex = 2;
        });
        await RefreshListOnlyAsync();
    }

    [RelayCommand]
    private async Task ReopenAsync()
    {
        if (Current == null) return;
        await RunAsync(async svc =>
        {
            Apply(await svc.ReopenAsync(Current.Id));
            PanelIndex = 1;
            StatusMessage = "Comanda reabierta para agregar productos";
        });
        await RefreshListOnlyAsync();
    }

    [RelayCommand]
    private async Task PrintPreBillAsync()
    {
        if (Current == null) return;
        var ok = await _printer.PrintReceiptAsync(ReceiptDtoFactory.FromComanda(Current, CashierName));
        StatusMessage = ok ? "🖨️ Pre-cuenta impresa" : "⚠️ Impresora no disponible (guardada como TXT)";
    }

    /// <summary>"Cambio a entregar" screen after paying; null = hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaleCompletedVisible))]
    private SaleSummaryViewModel? _lastSale;

    public bool IsSaleCompletedVisible => LastSale != null;

    [RelayCommand]
    private void DismissSale() => LastSale = null;

    /// <summary>Generates the invoice, prints it from the PERSISTED order, opens drawer if cash.</summary>
    [RelayCommand]
    private async Task CloseAndPayAsync()
    {
        if (Current == null) return;
        if (_shiftId is not { } shiftId)
        {
            StatusMessage = "❌ No hay turno de caja abierto. Ábralo en 'Turno de Caja'.";
            return;
        }

        var comanda = Current;
        var method = PayMethod;
        var tendered = Tendered;
        PaymentResultDto? result = null;
        OrderDto? order = null;

        await RunAsync(async svc =>
        {
            result = await svc.CloseAndPayAsync(comanda.Id, shiftId, method, tendered);
            if (!result.Success) return;
            using var scope = _scopes.CreateScope();
            order = await scope.ServiceProvider.GetRequiredService<ISalesService>().GetByIdAsync(result.OrderId);
        });

        if (result == null) return; // RunAsync already reported the exception
        if (!result.Success)
        {
            StatusMessage = $"❌ {result.ErrorMessage}";
            return;
        }

        ClearCurrent();
        _display.NotifySaleCompleted(result.OrderNumber, comanda.Total, result.ChangeAmount); // after ClearCurrent: "Completed" wins
        await RefreshListOnlyAsync();
        StatusMessage = $"✅ {comanda.TableName} cerrada · Factura {result.OrderNumber} · Cambio {ReceiptFormatter.Cop(result.ChangeAmount)}";
        LastSale = new SaleSummaryViewModel(result.OrderNumber, comanda.Total,
            method == PaymentMethod.Cash ? tendered : comanda.Total, result.ChangeAmount, method, comanda.TableName, DismissSale);

        // Sale is committed. Printer/drawer failures must not undo it — only report.
        if (order != null)
        {
            var receipt = ReceiptDtoFactory.FromOrder(order, CashierName, comanda.TableName, method, tendered, result.ChangeAmount);
            if (!await _printer.PrintReceiptAsync(receipt))
                StatusMessage += " · ⚠️ impresora no disponible (TXT de respaldo)";
        }
        if (method == PaymentMethod.Cash) await _printer.OpenCashDrawerAsync();
    }
}
