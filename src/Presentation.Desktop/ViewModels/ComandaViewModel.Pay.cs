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
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsCard), nameof(IsTransfer), nameof(IsTerminalChargeVisible))]
    private PaymentMethod _payMethod = PaymentMethod.Cash;

    public bool IsCash => PayMethod == PaymentMethod.Cash;
    public bool IsCard => PayMethod == PaymentMethod.Card;
    public bool IsTransfer => PayMethod == PaymentMethod.Transfer;

    /// <summary>"💳 Cobrar en datáfono" above "COBRAR Y FACTURAR" (manual path) when Card is selected and terminals are enabled.</summary>
    public bool IsTerminalChargeVisible => IsCard && _terminalPrompt.IsEnabled;

    /// <summary>A payment is being recorded (terminal dialog open or invoice being written): pay buttons disabled meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPayIdle), nameof(CanPay))]
    private bool _isPaying;
    public bool IsPayIdle => !IsPaying;

    // ── Payment lock between registers (master owns it; see IComandaService.LockForPaymentAsync) ──
    /// <summary>Another register holds the payment lock: Cobrar is disabled and the banner names it.</summary>
    public bool IsLockedByOtherRegister => Current?.PaymentLockedByTerminalCode is { } code && !string.IsNullOrWhiteSpace(code) && !IsOwnCode(code);
    /// <summary>This register reserved the table when it opened the bill.</summary>
    public bool IsLockedByThisRegister => IsOwnCode(Current?.PaymentLockedByTerminalCode);
    public bool IsLockBannerVisible => IsCurrentReadyToPay && (IsLockedByOtherRegister || IsLockedByThisRegister);
    public string LockBannerText => IsLockedByOtherRegister
        ? $"💳 Cobrando en {Current!.PaymentLockedByTerminalCode!.Trim()}"
        : IsLockedByThisRegister ? "🔒 Reservada para esta caja" : string.Empty;
    public string LockBannerColor => IsLockedByOtherRegister ? "#B45309" : "#1D4ED8";
    /// <summary>Pay buttons: idle AND not being collected on another register.</summary>
    public bool CanPay => IsPayIdle && !IsLockedByOtherRegister;

    private void NotifyLockChanged()
    {
        OnPropertyChanged(nameof(IsLockedByOtherRegister));
        OnPropertyChanged(nameof(IsLockedByThisRegister));
        OnPropertyChanged(nameof(IsLockBannerVisible));
        OnPropertyChanged(nameof(LockBannerText));
        OnPropertyChanged(nameof(LockBannerColor));
        OnPropertyChanged(nameof(CanPay));
    }

    partial void OnCurrentChanged(ComandaDto? value) => NotifyLockChanged();

    /// <summary>
    /// Reserves the current comanda for this register (Open → ReadyToPay like MarkReadyToPayAsync). Another register
    /// holding a fresh lock → "La mesa la está cobrando CAJA-02." in the status bar and the banner from the refreshed DTO.
    /// </summary>
    private async Task<bool> ReservePaymentAsync()
    {
        if (Current == null) return false;
        var id = Current.Id;
        var ok = await RunAsync(async svc => Apply(await svc.LockForPaymentAsync(id, OwnTerminalCode)));
        if (!ok && !IsMasterOffline)
        {
            // Rejected (locked elsewhere): show who holds it.
            try
            {
                using var scope = _scopes.CreateScope();
                var fresh = await scope.ServiceProvider.GetRequiredService<IComandaService>().GetByIdAsync(id);
                if (fresh != null && Current?.Id == id) Apply(fresh);
            }
            catch (Exception ex) { ReportError(ex, "actualizando"); }
        }
        return ok;
    }

    /// <summary>Best effort: frees a lock we hold (leaving the bill, reopening, switching comanda). Never surfaces errors.</summary>
    private async Task ReleasePaymentLockAsync(ComandaDto comanda)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IComandaService>().UnlockPaymentAsync(comanda.Id, OwnTerminalCode);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Comandas] No se pudo liberar la reserva de {comanda.TableName}: {ex.Message}");
        }
    }

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

    /// <summary>
    /// Panel 1 → 2: freezes the comanda (pending items go to kitchen), reserves it for this register and shows the bill.
    /// LockForPaymentAsync does Open → ReadyToPay exactly like MarkReadyToPayAsync, plus the lock the other register sees.
    /// </summary>
    [RelayCommand]
    private async Task ReadyToPayAsync()
    {
        if (Current == null) return;
        if (await ReservePaymentAsync())
        {
            Tendered = 0;
            PayMethod = PaymentMethod.Cash;
            PanelIndex = 2;
        }
        await RefreshListOnlyAsync();
    }

    [RelayCommand]
    private async Task ReopenAsync()
    {
        if (Current == null) return;
        if (IsLockedByOtherRegister)
        {
            StatusMessage = $"⚠️ La mesa la está cobrando {Current.PaymentLockedByTerminalCode}.";
            return;
        }
        var comanda = Current;
        await RunAsync(async svc =>
        {
            if (IsOwnCode(comanda.PaymentLockedByTerminalCode))
                await svc.UnlockPaymentAsync(comanda.Id, OwnTerminalCode); // UnlockPayment does not reopen by itself
            Apply(await svc.ReopenAsync(comanda.Id));
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

    /// <summary>Manual path: generates the invoice, prints it from the PERSISTED order, opens drawer if cash.</summary>
    [RelayCommand]
    private async Task CloseAndPayAsync()
    {
        if (IsPaying) return;
        IsPaying = true;
        try { await CloseAndPayCoreAsync(null); }
        finally { IsPaying = false; }
    }

    /// <summary>
    /// "💳 Cobrar en datáfono": sends the table's total to the terminal; approved → the comanda is closed with the
    /// terminal reference. Declined / timeout / cancelled leave it ReadyToPay; "Cobrar manual" continues the manual path.
    /// </summary>
    [RelayCommand]
    private async Task ChargeOnTerminalAsync()
    {
        if (IsPaying || Current == null) return;
        if (!IsCurrentReadyToPay)
        {
            StatusMessage = "⚠️ Cierre la comanda antes de cobrar en el datáfono.";
            return;
        }
        if (IsLockedByOtherRegister)
        {
            StatusMessage = $"⚠️ La mesa la está cobrando {Current.PaymentLockedByTerminalCode}.";
            return;
        }
        if (_shiftId is not { } shiftId)
        {
            StatusMessage = "❌ No hay turno de caja abierto. Ábralo en 'Turno de Caja'.";
            return;
        }
        if (PayMethod != PaymentMethod.Card) PayMethod = PaymentMethod.Card;

        IsPaying = true;
        try
        {
            var comanda = Current;
            var outcome = await _terminalPrompt.RequestAsync(comanda.Total, TerminalPaymentPurpose.Comanda, $"Comanda #{comanda.Number}",
                comandaId: comanda.Id, cashShiftId: shiftId, description: comanda.TableName);

            if (outcome.IsApproved)
            {
                await CloseAndPayCoreAsync(outcome.Charge);
            }
            else if (outcome.FallbackToManual)
            {
                StatusMessage = string.IsNullOrWhiteSpace(outcome.Message) ? "✍️ Tarjeta registrada manualmente" : $"✍️ Datáfono: {outcome.Message}";
                await CloseAndPayCoreAsync(null);
            }
            else
            {
                StatusMessage = $"⚠️ {outcome.Message ?? "El datáfono no aprobó el cobro."} · {comanda.TableName} sigue pendiente de pago";
            }
        }
        finally
        {
            IsPaying = false;
        }
    }

    private async Task CloseAndPayCoreAsync(TerminalPaymentDto? charge)
    {
        if (Current == null) return;
        if (IsLockedByOtherRegister)
        {
            StatusMessage = $"⚠️ La mesa la está cobrando {Current.PaymentLockedByTerminalCode}.";
            return;
        }
        if (_shiftId is not { } shiftId)
        {
            StatusMessage = "❌ No hay turno de caja abierto. Ábralo en 'Turno de Caja'.";
            return;
        }

        var comanda = Current;
        var method = PayMethod;
        var tendered = Tendered;
        var reference = charge is { IsApproved: true } ? charge.ReferenceText : null;
        PaymentResultDto? result = null;
        OrderDto? order = null;

        await RunAsync(async svc =>
        {
            // Satellite: RemoteComandaService locks on the master and writes the Order + Payment on THIS database,
            // so the receipt below comes from the local ISalesService exactly as on the master.
            result = await svc.CloseAndPayAsync(comanda.Id, shiftId, method, tendered, reference, charge?.Id);
            if (!result.Success) return;
            using var scope = _scopes.CreateScope();
            order = await scope.ServiceProvider.GetRequiredService<ISalesService>().GetByIdAsync(result.OrderId);
        });

        if (result == null) return; // RunAsync already reported the exception
        if (!result.Success)
        {
            StatusMessage = reference is null
                ? $"❌ {result.ErrorMessage}"
                : $"❌ {result.ErrorMessage} · el datáfono SÍ aprobó {reference}: registre la venta manualmente";
            return;
        }

        ClearCurrent();
        _display.NotifySaleCompleted(result.OrderNumber, comanda.Total, result.ChangeAmount); // after ClearCurrent: "Completed" wins
        await RefreshListOnlyAsync();
        StatusMessage = reference is null
            ? $"✅ {comanda.TableName} cerrada · Factura {result.OrderNumber} · Cambio {ReceiptFormatter.Cop(result.ChangeAmount)}"
            : $"✅ {comanda.TableName} cerrada · Factura {result.OrderNumber} · {reference}";
        LastSale = new SaleSummaryViewModel(result.OrderNumber, comanda.Total,
            method == PaymentMethod.Cash ? tendered : comanda.Total, result.ChangeAmount, method, comanda.TableName, DismissSale);

        // Sale is committed. Printer/drawer failures must not undo it — only report.
        if (order != null)
        {
            var receipt = ReceiptDtoFactory.FromOrder(order, CashierName, comanda.TableName, method, tendered, result.ChangeAmount);
            CheckoutViewModel.ApplyTerminalCharge(receipt, charge, _terminalSettings.PrintVoucher);
            if (!await _printer.PrintReceiptAsync(receipt))
                StatusMessage += " · ⚠️ impresora no disponible (TXT de respaldo)";
        }
        if (method == PaymentMethod.Cash) await _cashDrawer.OpenDrawerAsync();
    }
}
