using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Receipts;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// "🧾 Recaudos": bill payments at the counter (energía, agua, gas, recargas…). Biller tiles on the left,
/// form in the middle (reference — also from the barcode scanner while this tab is active —, amount,
/// method, tendered), today's payments of the open shift on the right with reprint / void.
/// Singleton: services are resolved per operation from a scope.
/// </summary>
public partial class BillPaymentsViewModel : ObservableObject
{
    private static readonly string StoreName = new ReceiptDto().StoreName;

    private readonly IServiceScopeFactory _scopes;
    private readonly IUserSession _session;
    private readonly IManagerAuthPrompt _authPrompt;
    private readonly IPrinterService _printer;
    private readonly ICashDrawerService _cashDrawer;
    private readonly IBarcodeScannerService _scanner;
    private bool _isActive;

    public ObservableCollection<BillerTileViewModel> Billers { get; } = new();
    public ObservableCollection<BillPaymentRowViewModel> Payments { get; } = new();
    public ObservableCollection<ChipViewModel> VoidReasonChips { get; } = new();

    // ── Shift ────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled), nameof(CanCharge), nameof(ChargeHint))]
    private bool _hasOpenShift;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled), nameof(CanCharge), nameof(ChargeHint))]
    private bool _isBusy;

    public bool IsFormEnabled => HasOpenShift && !IsBusy;
    public bool HasBillers => Billers.Count > 0;
    public bool HasPayments => Payments.Count > 0;

    [ObservableProperty] private string _notification = string.Empty;
    [ObservableProperty] private string _paymentsSummary = string.Empty;

    // ── Form ─────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedBiller), nameof(SelectedBillerText), nameof(Commission), nameof(CommissionText),
        nameof(Total), nameof(TotalText), nameof(BreakdownText), nameof(Change), nameof(ChangeText), nameof(CanCharge), nameof(ChargeHint))]
    private BillerTileViewModel? _selectedBiller;
    public bool HasSelectedBiller => SelectedBiller != null;
    public string SelectedBillerText => SelectedBiller is { } b ? $"{b.Name} · {b.CommissionText}" : "① Elija el convenio";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCharge), nameof(ChargeHint))]
    private string _reference = string.Empty;

    [ObservableProperty] private string _customerName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount), nameof(AmountText), nameof(Commission), nameof(CommissionText), nameof(Total),
        nameof(TotalText), nameof(BreakdownText), nameof(Change), nameof(ChangeText), nameof(CanCharge), nameof(ChargeHint))]
    private string _amountStr = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tendered), nameof(TenderedText), nameof(Change), nameof(ChangeText), nameof(CanCharge), nameof(ChargeHint))]
    private string _tenderedStr = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsCard), nameof(IsTransfer), nameof(CanCharge), nameof(ChargeHint), nameof(Change), nameof(ChangeText))]
    private PaymentMethod _method = PaymentMethod.Cash;
    public bool IsCash => Method == PaymentMethod.Cash;
    public bool IsCard => Method == PaymentMethod.Card;
    public bool IsTransfer => Method == PaymentMethod.Transfer;

    /// <summary>Which figure the numpad edits: the bill amount or (cash) the tendered money.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingAmount), nameof(NumpadExactLabel), nameof(AmountBorder), nameof(TenderedBorder))]
    private bool _isEditingTendered;
    public bool IsEditingAmount => !IsEditingTendered;
    public string NumpadExactLabel => IsEditingTendered ? "Exacto" : "Recibido ➜";
    public string AmountBorder => IsEditingTendered ? "#334155" : "#38BDF8";
    public string TenderedBorder => IsEditingTendered ? "#38BDF8" : "#334155";

    public decimal Amount => CashShiftViewModel.ParseAmount(AmountStr);
    public decimal Commission => SelectedBiller is { } b && Amount > 0 ? b.Dto.CommissionFor(Amount) : 0m;
    public decimal Total => Amount + Commission;
    public decimal Tendered => CashShiftViewModel.ParseAmount(TenderedStr);
    public decimal Change => IsCash ? Math.Max(0, Tendered - Total) : 0m;

    public string AmountText => ReceiptFormatter.Cop(Amount);
    public string CommissionText => ReceiptFormatter.Cop(Commission);
    public string TotalText => ReceiptFormatter.Cop(Total);
    public string TenderedText => ReceiptFormatter.Cop(Tendered);
    public string ChangeText => ReceiptFormatter.Cop(Change);
    public string BreakdownText => $"Valor {AmountText} + Comisión {CommissionText} = Total {TotalText}";

    public bool CanCharge => IsFormEnabled && SelectedBiller != null && !string.IsNullOrWhiteSpace(Reference)
                             && Amount > 0 && (!IsCash || Tendered >= Total);

    public string ChargeHint =>
        !HasOpenShift ? "Abra un turno para cobrar recaudos"
        : SelectedBiller == null ? "① Elija el convenio"
        : string.IsNullOrWhiteSpace(Reference) ? "② Escriba o escanee la referencia"
        : Amount <= 0 ? "③ Ingrese el valor de la factura"
        : IsCash && Tendered < Total ? $"Faltan {ReceiptFormatter.Cop(Total - Tendered)} por recibir"
        : string.Empty;

    // ── Change screen ────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummaryVisible))]
    private SaleSummaryViewModel? _lastPayment;
    public bool IsSummaryVisible => LastPayment != null;

    // ── Void dialog ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isVoidDialogVisible;
    [ObservableProperty] private BillPaymentRowViewModel? _voidTarget;
    [ObservableProperty] private string _voidReason = string.Empty;
    [ObservableProperty] private string _voidError = string.Empty;
    public string VoidTitle => VoidTarget is { } v ? $"Anular {v.Dto.ReceiptNumber} · {v.Dto.BillerName} · {v.TotalText}" : string.Empty;
    partial void OnVoidTargetChanged(BillPaymentRowViewModel? value) => OnPropertyChanged(nameof(VoidTitle));

    public BillPaymentsViewModel(IServiceScopeFactory scopes, IUserSession session, IManagerAuthPrompt authPrompt,
        IPrinterService printer, ICashDrawerService cashDrawer, IBarcodeScannerService scanner)
    {
        _scopes = scopes;
        _session = session;
        _authPrompt = authPrompt;
        _printer = printer;
        _cashDrawer = cashDrawer;
        _scanner = scanner;

        Billers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBillers));
        Payments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPayments));
        foreach (var r in new[] { "Error de digitación", "Referencia equivocada", "Convenio equivocado", "Cliente desistió" })
            VoidReasonChips.Add(new ChipViewModel(r, v => VoidReason = v));
    }

    // ── Activation (tab shown / hidden) ─────────────────────────────────────
    /// <summary>Called by MainViewModel when the tab is shown: subscribe to the scanner and reload.</summary>
    public async Task ActivateAsync()
    {
        if (!_isActive)
        {
            _isActive = true;
            _scanner.BarcodeScanned += OnBarcodeScanned;
        }
        await LoadAsync();
    }

    /// <summary>Tab hidden: the scanner goes back to the sales screen only.</summary>
    public void Deactivate()
    {
        if (!_isActive) return;
        _isActive = false;
        _scanner.BarcodeScanned -= OnBarcodeScanned;
    }

    private void OnBarcodeScanned(object? sender, string code) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isActive || string.IsNullOrWhiteSpace(code)) return;
            Reference = code.Trim();
            Notification = $"📷 Referencia leída: {Reference}";
        });

    /// <summary>Satellite: billers changed on the master. Same loader (selection is kept by id); nothing typed is lost.</summary>
    public Task ReloadBillersAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var shift = await scope.ServiceProvider.GetRequiredService<ICashShiftService>().GetCurrentShiftAsync();
            HasOpenShift = shift != null;

            var billers = await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().GetBillersAsync();
            var selectedId = SelectedBiller?.Dto.Id;
            Billers.Clear();
            foreach (var b in billers.OrderBy(b => b.SortOrder).ThenBy(b => b.Name))
                Billers.Add(new BillerTileViewModel(b, SelectBiller));
            SelectedBiller = null;
            if (selectedId is { } id && Billers.FirstOrDefault(t => t.Dto.Id == id) is { } again) SelectBiller(again);
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
        await LoadPaymentsAsync();
    }

    private async Task LoadPaymentsAsync()
    {
        Payments.Clear();
        PaymentsSummary = string.Empty;
        if (!HasOpenShift) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var list = await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().GetForShiftAsync();
            foreach (var p in list) Payments.Add(new BillPaymentRowViewModel(p, ReprintAsync, StartVoid));
            var ok = list.Where(p => p.Status == BillPaymentStatus.Completed).ToList();
            PaymentsSummary = $"{ok.Count} recaudos · {ReceiptFormatter.Cop(ok.Sum(p => p.Total))} · comisiones {ReceiptFormatter.Cop(ok.Sum(p => p.Commission))}";
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
    }

    private void SelectBiller(BillerTileViewModel tile)
    {
        foreach (var t in Billers) t.IsSelected = ReferenceEquals(t, tile);
        SelectedBiller = tile;
    }

    // ── Numpad ───────────────────────────────────────────────────────────────
    [RelayCommand]
    public void NumpadDigit(string digit)
    {
        if (IsEditingTendered) TenderedStr = CashShiftViewModel.AppendDigits(TenderedStr, digit);
        else AmountStr = CashShiftViewModel.AppendDigits(AmountStr, digit);
    }

    [RelayCommand]
    public void NumpadClear()
    {
        if (IsEditingTendered) TenderedStr = "0";
        else AmountStr = "0";
    }

    /// <summary>Blue key: on the amount → jump to "Recibido" (cash) ; on "Recibido" → exact amount.</summary>
    [RelayCommand]
    public void NumpadExact()
    {
        if (!IsEditingTendered)
        {
            if (IsCash) IsEditingTendered = true;
            return;
        }
        TenderedStr = Total.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    [RelayCommand] public void EditAmount() => IsEditingTendered = false;
    [RelayCommand] public void EditTendered() { if (IsCash) IsEditingTendered = true; }

    [RelayCommand]
    public void SetMethod(string method)
    {
        if (!Enum.TryParse<PaymentMethod>(method, out var m)) return;
        Method = m;
        if (!IsCash) IsEditingTendered = false;
    }

    [RelayCommand]
    public void ResetForm()
    {
        Reference = string.Empty;
        CustomerName = string.Empty;
        AmountStr = "0";
        TenderedStr = "0";
        Method = PaymentMethod.Cash;
        IsEditingTendered = false;
    }

    // ── Charge ───────────────────────────────────────────────────────────────
    [RelayCommand]
    public async Task ChargeAsync()
    {
        if (!CanCharge || SelectedBiller == null) { if (!string.IsNullOrEmpty(ChargeHint)) Notification = $"⚠️ {ChargeHint}"; return; }
        IsBusy = true;
        try
        {
            var user = _session.CurrentUser;
            var tendered = IsCash ? Tendered : Total;
            var request = new BillPaymentRequest(SelectedBiller.Dto.Id, Reference.Trim(), Amount, Method, tendered,
                user?.Id, user?.Name ?? "Cajero", string.IsNullOrWhiteSpace(CustomerName) ? null : CustomerName.Trim());

            BillPaymentDto paid;
            using (var scope = _scopes.CreateScope())
                paid = await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().RecordAsync(request);

            await PrintReceiptAsync(paid);
            if (paid.Method == PaymentMethod.Cash)
            {
                try { await _cashDrawer.OpenDrawerAsync(); } catch { /* best effort */ }
            }

            LastPayment = new SaleSummaryViewModel(paid.ReceiptNumber, paid.Total, paid.TenderedAmount, paid.ChangeAmount,
                paid.Method, null, () => LastPayment = null, title: $"✅ Recaudo {paid.ReceiptNumber} · {paid.BillerName}",
                dismissLabel: "🧾 Nuevo recaudo");
            Notification = $"✅ Recaudo {paid.ReceiptNumber} registrado · {ReceiptFormatter.Cop(paid.Total)}";
            ResetForm();
            await LoadPaymentsAsync();
        }
        catch (Exception ex)
        {
            Notification = $"❌ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PrintReceiptAsync(BillPaymentDto p)
    {
        try
        {
            if (!await _printer.PrintLinesAsync(CashReports.BillPaymentReceipt(p, StoreName), p.ReceiptNumber))
                Notification = $"⚠️ No se pudo imprimir {p.ReceiptNumber} (copia guardada en FailedReceipts)";
        }
        catch (Exception ex)
        {
            Notification = $"⚠️ No se pudo imprimir {p.ReceiptNumber}: {ex.Message}";
        }
    }

    private async Task ReprintAsync(BillPaymentRowViewModel row)
    {
        await PrintReceiptAsync(row.Dto);
        Notification = $"🖨️ Reimpreso {row.Dto.ReceiptNumber}";
    }

    // ── Void ─────────────────────────────────────────────────────────────────
    private void StartVoid(BillPaymentRowViewModel row)
    {
        if (row.IsVoided) return;
        VoidTarget = row;
        VoidReason = string.Empty;
        VoidError = string.Empty;
        IsVoidDialogVisible = true;
    }

    [RelayCommand] public void CancelVoid() { IsVoidDialogVisible = false; VoidTarget = null; }

    [RelayCommand]
    public async Task ConfirmVoidAsync()
    {
        if (VoidTarget is not { } row) return;
        if (string.IsNullOrWhiteSpace(VoidReason)) { VoidError = "⚠️ Indique el motivo de la anulación."; return; }

        var approval = await RequiredOverride.RequestAsync(_authPrompt, _scopes, _session, AuthorizedAction.VoidBillPayment,
            $"Anular recaudo {row.Dto.ReceiptNumber} ({row.Dto.BillerName}): {VoidReason.Trim()}", row.Dto.Total, row.Dto.ReceiptNumber);
        if (!approval.IsApproved) { VoidError = approval.Error ?? "❌ No autorizado"; return; }

        IsBusy = true;
        try
        {
            using (var scope = _scopes.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IBillPaymentService>().VoidAsync(row.Dto.Id, VoidReason.Trim(), approval.Override!.Id);
            IsVoidDialogVisible = false;
            VoidTarget = null;
            Notification = row.Dto.Method == PaymentMethod.Cash
                ? $"🗑️ Recaudo {row.Dto.ReceiptNumber} anulado · devuelva {ReceiptFormatter.Cop(row.Dto.Total)} al cliente"
                : $"🗑️ Recaudo {row.Dto.ReceiptNumber} anulado";
            if (row.Dto.Method == PaymentMethod.Cash)
            {
                try { await _cashDrawer.OpenDrawerAsync(); } catch { /* best effort */ }
            }
            await LoadPaymentsAsync();
        }
        catch (Exception ex)
        {
            VoidError = $"❌ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
