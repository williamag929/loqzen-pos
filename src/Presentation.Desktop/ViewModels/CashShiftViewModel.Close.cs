using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Close wizard: 1 Revisión → 2 Conteo (blind) → 3 Resultado (reveal, manager + note above the threshold)
/// → 4 Fondo (cash left for the next shift) → CloseShiftAsync → Z report (printed once automatically).
/// </summary>
public partial class CashShiftViewModel
{
    public CashCountViewModel CloseCount { get; }

    [ObservableProperty] private bool _isCloseWizardVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4),
        nameof(IsStep2Reached), nameof(IsStep3Reached), nameof(IsStep4Reached), nameof(CanGoBack), nameof(StepTitle))]
    private int _closeStep = 1;

    public bool IsStep1 => CloseStep == 1;
    public bool IsStep2 => CloseStep == 2;
    public bool IsStep3 => CloseStep == 3;
    public bool IsStep4 => CloseStep == 4;
    public bool IsStep2Reached => CloseStep >= 2;
    public bool IsStep3Reached => CloseStep >= 3;
    public bool IsStep4Reached => CloseStep >= 4;
    public bool CanGoBack => CloseStep > 1;
    public string StepTitle => CloseStep switch
    {
        1 => "Paso 1 de 4 · Revisión",
        2 => "Paso 2 de 4 · Conteo del efectivo",
        3 => "Paso 3 de 4 · Resultado",
        _ => "Paso 4 de 4 · Fondo para el siguiente turno"
    };

    [ObservableProperty] private string _closeError = string.Empty;

    // ── Step 1: review ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseCashSalesText), nameof(CloseCardSalesText), nameof(CloseTransferSalesText),
        nameof(CloseOtherSalesText), nameof(CloseTotalSalesText), nameof(CloseOrderCountText), nameof(CloseCardExpectedText),
        nameof(CloseTransferExpectedText), nameof(CloseBillPaymentsText), nameof(CloseMovementsText), nameof(HasOtherSales))]
    private ShiftReconciliationDto? _closeRecon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenComandas), nameof(CanStartCount))]
    private int _openComandasCount;
    [ObservableProperty] private string _openComandasText = string.Empty;
    public bool HasOpenComandas => OpenComandasCount > 0;
    public bool CanStartCount => !HasOpenComandas && CloseRecon != null;

    public string CloseCashSalesText => Cop(CloseRecon?.CashSales);
    public string CloseCardSalesText => Cop(CloseRecon?.CardSales);
    public string CloseTransferSalesText => Cop(CloseRecon?.TransferSales);
    public string CloseOtherSalesText => Cop(CloseRecon?.OtherSales);
    public bool HasOtherSales => (CloseRecon?.OtherSales ?? 0) != 0;
    public string CloseTotalSalesText => Cop(CloseRecon?.TotalSales);
    public string CloseOrderCountText => $"{CloseRecon?.OrderCount ?? 0} facturas";
    public string CloseCardExpectedText => $"Compare con el cierre del datáfono: {Cop(CloseRecon?.ExpectedCard)}";
    public string CloseTransferExpectedText => $"Compare con las transferencias recibidas: {Cop(CloseRecon?.ExpectedTransfer)}";
    public string CloseBillPaymentsText => CloseRecon is { } r
        ? $"{r.BillPaymentCount} recaudos · efectivo {Cop(r.BillPaymentsCash)} · por girar {Cop(r.BillPaymentsToRemit)}" : string.Empty;
    public string CloseMovementsText => CloseRecon is { } r
        ? $"Entradas {Cop(r.PaidIn)} · Salidas {Cop(r.PaidOut + r.Expenses + r.SafeDrops + r.TipPayouts + r.BillPaymentVoidsCash)}" : string.Empty;

    private static string Cop(decimal? v) => ReceiptFormatter.Cop(v ?? 0m);

    // ── Step 2: count (blind) ────────────────────────────────────────────────
    /// <summary>Expected / difference on the count step only when blind count is OFF.</summary>
    public bool CanSeeExpectedInCount => !_settings.BlindCount;
    public string CountExpectedText => Cop(CloseRecon?.ExpectedCash);
    public string CountLiveDifferenceText => SignedCop(CloseCount.Total - (CloseRecon?.ExpectedCash ?? 0));
    public string CountLiveDifferenceColor => DifferenceColorFor(CloseCount.Total - (CloseRecon?.ExpectedCash ?? 0), _settings.MaxDifferenceWithoutManager);

    // ── Step 3: result ───────────────────────────────────────────────────────
    public decimal CloseExpected => CloseRecon?.ExpectedCash ?? 0m;
    public decimal CloseDifference => CloseCount.Total - CloseExpected;
    public string CloseExpectedText => Cop(CloseExpected);
    public string CloseCountedText => CloseCount.TotalText;
    public string CloseDifferenceText => SignedCop(CloseDifference);
    public string CloseDifferenceColor => DifferenceColorFor(CloseDifference, _settings.MaxDifferenceWithoutManager);
    public string CloseVerdictText => CloseDifference == 0 ? "✅ Cuadre perfecto"
        : !NeedsDifferenceApproval ? (CloseDifference > 0 ? "🟡 Sobrante dentro del margen permitido" : "🟡 Faltante dentro del margen permitido")
        : (CloseDifference > 0 ? "🔴 Sobrante mayor al permitido" : "🔴 Faltante mayor al permitido");
    public bool NeedsDifferenceApproval => Math.Abs(CloseDifference) > _settings.MaxDifferenceWithoutManager;
    public string ThresholdText => $"Máximo sin gerente: {Cop(_settings.MaxDifferenceWithoutManager)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDifferenceApproval), nameof(DifferenceApprovalText), nameof(CanLeaveResult))]
    private ManagerOverrideDto? _differenceApproval;
    public bool HasDifferenceApproval => DifferenceApproval != null;
    public string DifferenceApprovalText => DifferenceApproval is { } a ? $"✔ Autorizado por {a.ManagerName}" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLeaveResult))]
    private string _closeNotes = string.Empty;

    public bool CanLeaveResult => !NeedsDifferenceApproval || (HasDifferenceApproval && !string.IsNullOrWhiteSpace(CloseNotes));

    // ── Step 4: float ────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FloatAmountText), nameof(WithdrawText), nameof(FloatError))]
    private string _floatAmountStr = "0";
    private bool _floatPristine;
    public decimal FloatAmount => ParseAmount(FloatAmountStr);
    public string FloatAmountText => Cop(FloatAmount);
    public string WithdrawText => Cop(Math.Max(0, CloseCount.Total - FloatAmount));
    public string FloatError => FloatAmount > CloseCount.Total ? $"⚠️ El fondo no puede superar lo contado ({CloseCount.TotalText})" : string.Empty;

    private void OnCloseCountChanged()
    {
        // A different count means a different difference: an earlier approval no longer applies.
        DifferenceApproval = null;
        OnPropertyChanged(nameof(CountLiveDifferenceText));
        OnPropertyChanged(nameof(CountLiveDifferenceColor));
        NotifyResult();
    }

    private void NotifyResult()
    {
        foreach (var name in new[] { nameof(CloseExpected), nameof(CloseDifference), nameof(CloseExpectedText), nameof(CloseCountedText),
                     nameof(CloseDifferenceText), nameof(CloseDifferenceColor), nameof(CloseVerdictText), nameof(NeedsDifferenceApproval),
                     nameof(CanLeaveResult), nameof(CountExpectedText), nameof(WithdrawText), nameof(FloatError) })
            OnPropertyChanged(name);
    }

    // ── Commands ─────────────────────────────────────────────────────────────
    [RelayCommand]
    public async Task ShowCloseWizardAsync()
    {
        if (!IsShiftOpen) return;
        CloseError = string.Empty;
        CloseNotes = string.Empty;
        CloseCount.ResetCount();
        DifferenceApproval = null;
        CloseStep = 1;
        IsCloseWizardVisible = true;
        await LoadCloseReviewAsync();
    }

    private async Task LoadCloseReviewAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            CloseRecon = await scope.ServiceProvider.GetRequiredService<ICashShiftService>().GetReconciliationAsync();
            var active = await scope.ServiceProvider.GetRequiredService<IComandaService>().GetActiveComandasAsync();
            OpenComandasCount = active.Count;
            OpenComandasText = active.Count == 0 ? string.Empty
                : $"🍽️ Hay {active.Count} {(active.Count == 1 ? "comanda abierta" : "comandas abiertas")}: " +
                  string.Join(", ", active.Take(6).Select(c => c.TableName)) + (active.Count > 6 ? "…" : "") +
                  ". Cóbrelas o anúlelas antes de cerrar el turno.";
            NotifyResult();
        }
        catch (Exception ex)
        {
            CloseError = $"❌ {ex.Message}";
        }
    }

    [RelayCommand] public Task RefreshCloseReviewAsync() => LoadCloseReviewAsync();

    [RelayCommand]
    public void CloseWizardNext()
    {
        CloseError = string.Empty;
        switch (CloseStep)
        {
            case 1:
                if (!CanStartCount) { CloseError = HasOpenComandas ? OpenComandasText : "⚠️ No se pudo cargar el resumen."; return; }
                CloseStep = 2;
                break;
            case 2:
                if (CloseCount.Total <= 0 && CloseExpected > 0)
                {
                    CloseError = "⚠️ Cuente el efectivo del cajón (toque + en cada billete / moneda).";
                    return;
                }
                NotifyResult();
                CloseStep = 3;
                break;
            case 3:
                if (!CanLeaveResult) { CloseError = "⚠️ La diferencia requiere autorización de gerente y una nota."; return; }
                var suggested = Math.Min(_settings.DefaultFloat, CloseCount.Total);
                FloatAmountStr = suggested.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                _floatPristine = true;
                CloseStep = 4;
                break;
        }
    }

    [RelayCommand]
    public void CloseWizardBack()
    {
        CloseError = string.Empty;
        if (CloseStep > 1) CloseStep--;
    }

    /// <summary>"Volver a contar": back to step 2 keeping the quantities.</summary>
    [RelayCommand] public void Recount() { CloseError = string.Empty; CloseStep = 2; }

    [RelayCommand] public void CancelCloseWizard() => IsCloseWizardVisible = false;

    [RelayCommand]
    public async Task RequestDifferenceApprovalAsync()
    {
        var diff = CloseDifference;
        var shift = CloseRecon?.ShiftNumber ?? 0;
        var result = await RequiredOverride.RequestAsync(_authPrompt, _scopes, _session, AuthorizedAction.CashDifference,
            $"Cierre turno #{shift} con diferencia {SignedCop(diff)}", diff, $"Turno #{shift}");
        if (result.IsApproved) DifferenceApproval = result.Override;
        else CloseError = result.Error ?? "❌ No autorizado";
    }

    [RelayCommand]
    public void AppendFloatDigit(string digit)
    {
        FloatAmountStr = AppendDigits(_floatPristine ? "0" : FloatAmountStr, digit);
        _floatPristine = false;
    }

    [RelayCommand] public void ClearFloat() { FloatAmountStr = "0"; _floatPristine = false; }

    [RelayCommand]
    public void UseDefaultFloat()
    {
        FloatAmountStr = Math.Min(_settings.DefaultFloat, CloseCount.Total).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        _floatPristine = true;
    }

    [RelayCommand]
    public async Task ConfirmCloseShiftAsync()
    {
        if (IsBusy) return;
        CloseError = string.Empty;
        if (FloatAmount > CloseCount.Total) { CloseError = FloatError; return; }
        if (!CanLeaveResult) { CloseStep = 3; CloseError = "⚠️ La diferencia requiere autorización de gerente y una nota."; return; }

        IsBusy = true;
        try
        {
            var user = _session.CurrentUser;
            var request = new CloseShiftRequest(
                CloseCount.ToLines(), FloatAmount,
                string.IsNullOrWhiteSpace(CloseNotes) ? null : CloseNotes.Trim(),
                user?.Id, UserName, _settings.BlindCount, DifferenceApproval?.Id);

            var final = await WithShiftServiceAsync(s => s.CloseShiftAsync(request));

            IsCloseWizardVisible = false;
            CurrentShift = null;
            IsShiftOpen = false;
            UpdateShiftStatusDisplay();
            Reconciliation = null;
            Movements.Clear();
            Notification = $"✅ Turno #{final.ShiftNumber} cerrado. Diferencia: {SignedCop(final.Difference ?? 0)}";

            ShowZReport(final, isFromClose: true);
            await PrintZReportAsync(); // printed once automatically; "Imprimir Z" reprints
        }
        catch (CashDifferenceRequiresApprovalException ex)
        {
            DifferenceApproval = null;
            CloseStep = 3;
            CloseError = $"⚠️ {ex.Message}";
        }
        catch (Exception ex)
        {
            CloseError = $"❌ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
