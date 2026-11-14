using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>One terminal button in the charge dialog (own command, no #Root binding).</summary>
public sealed partial class TerminalChoiceViewModel : ObservableObject
{
    public TerminalChoiceViewModel(PaymentTerminalDto dto, Action<TerminalChoiceViewModel> onSelect)
    {
        Dto = dto;
        SelectCommand = new RelayCommand(() => onSelect(this));
    }

    public PaymentTerminalDto Dto { get; }
    public IRelayCommand SelectCommand { get; }
    public string Name => Dto.Name;
    public string Detail => $"{Dto.ProviderLabel} · {Dto.AssignmentLabel}";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// "💳 Cobrar en datáfono" modal shared by Checkout, Comandas and Recaudos (singleton, hosted in MainWindow).
/// <see cref="RequestAsync"/> loads the active terminals, preselects the one assigned to the signed-in user (else the
/// first one "En caja"), and awaits the cashier: Cobrar → IPaymentTerminalService.ChargeAsync on a worker thread
/// (own scope) with progress marshalled to the UI; Approved auto-closes after 1.5 s; Declined / Timeout / Error offer
/// Reintentar and Cobrar manual. Terminals disabled or none configured → FallbackToManual at once.
/// </summary>
public partial class TerminalChargePromptViewModel : ObservableObject, ITerminalChargePrompt
{
    private enum ChargePhase { Select, Charging, Approved, Failed }

    private static readonly TimeSpan ApprovedAutoClose = TimeSpan.FromSeconds(1.5);

    private readonly IServiceScopeFactory _scopes;
    private readonly IUserSession _session;
    private readonly PaymentTerminalSettings _settings;
    private TaskCompletionSource<TerminalChargeOutcome>? _pending;
    private CancellationTokenSource? _cts;
    private TerminalPaymentDto? _lastCharge;

    // Request being served
    private decimal _amount;
    private TerminalPaymentPurpose _purpose;
    private string _invoiceReference = string.Empty;
    private Guid? _orderId, _comandaId, _billPaymentId, _cashShiftId;
    private string? _description;

    public TerminalChargePromptViewModel(IServiceScopeFactory scopes, IUserSession session, PaymentTerminalSettings settings)
    {
        _scopes = scopes;
        _session = session;
        _settings = settings;
    }

    public bool IsEnabled => _settings.Enabled;

    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);

    public string Title => T("Terminal.Title");

    /// <summary>After a 🌐 toggle (called by MainViewModel): re-read the texts this VM computes in C#.
    /// A result already on screen keeps its wording; the stage text is rebuilt from the last stage.</summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SelectedTerminalText));
        if (IsCharging && _lastStage is { } st) ShowStage(st, null);
    }
    private TerminalChargeStage? _lastStage;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _amountText = string.Empty;
    [ObservableProperty] private string _referenceText = string.Empty;

    public ObservableCollection<TerminalChoiceViewModel> Terminals { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCharge), nameof(SelectedTerminalText))]
    private TerminalChoiceViewModel? _selectedTerminal;

    public bool CanCharge => SelectedTerminal != null && IsSelecting;
    public string SelectedTerminalText => SelectedTerminal is { } t ? $"{t.Name} · {t.Detail}" : T("Terminal.Choose");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting), nameof(IsCharging), nameof(IsApproved), nameof(IsFailed), nameof(CanCharge), nameof(BorderColor))]
    private int _phase;

    public bool IsSelecting => Phase == (int)ChargePhase.Select;
    public bool IsCharging => Phase == (int)ChargePhase.Charging;
    public bool IsApproved => Phase == (int)ChargePhase.Approved;
    public bool IsFailed => Phase == (int)ChargePhase.Failed;

    /// <summary>"Conectando…" / "Acerque o inserte la tarjeta…" / "Procesando…" while in flight.</summary>
    [ObservableProperty] private string _stageText = string.Empty;
    [ObservableProperty] private string _stageIcon = "📡";

    /// <summary>Approved: "AUTH 123456 · VISA ****4242". Failed: the terminal's message.</summary>
    [ObservableProperty] private string _resultTitle = string.Empty;
    [ObservableProperty] private string _resultMessage = string.Empty;
    /// <summary>Hex from ThemePalette (never a literal): muted while idle/cancelled, danger declined, warning timeout/error, success approved.</summary>
    [ObservableProperty] private string _resultColor = ThemePalette.TextFaint;
    private TerminalPaymentStatus? _lastStatus;

    public string BorderColor => IsApproved ? ThemePalette.Success : IsFailed ? ResultColor : ThemePalette.Info;
    partial void OnResultColorChanged(string value) => OnPropertyChanged(nameof(BorderColor));

    /// <summary>After a 🌙/☀️ toggle (called by MainViewModel): re-read the palette for the colours this VM exposes.</summary>
    public void RefreshTheme()
    {
        ResultColor = IsApproved ? ThemePalette.Success : _lastStatus is { } st ? ColorFor(st) : ThemePalette.TextFaint;
        OnPropertyChanged(nameof(BorderColor));
    }

    private static string ColorFor(TerminalPaymentStatus status) => status switch
    {
        TerminalPaymentStatus.Declined => ThemePalette.Danger,
        TerminalPaymentStatus.Cancelled => ThemePalette.TextFaint,
        _ => ThemePalette.Warning
    };

    /// <summary>Last loading / charge problem shown under the terminal list (the dialog still works).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public async Task<TerminalChargeOutcome> RequestAsync(decimal amount, TerminalPaymentPurpose purpose, string invoiceReference,
        Guid? orderId = null, Guid? comandaId = null, Guid? billPaymentId = null, Guid? cashShiftId = null, string? description = null)
    {
        if (!_settings.Enabled) return TerminalChargeOutcome.Manual;
        if (amount <= 0) return TerminalChargeOutcome.Aborted(T("Terminal.Error.Amount"));
        if (IsOpen) return TerminalChargeOutcome.Aborted(T("Terminal.Error.Busy")); // one charge at a time

        // Registry read on a worker thread with its own scope (DbContext is not thread-safe; singleton VM).
        System.Collections.Generic.IReadOnlyList<PaymentTerminalDto> terminals;
        try
        {
            terminals = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>().GetTerminalsAsync();
            });
        }
        catch (Exception)
        {
            // Registry unavailable (stub / migration pending): never block the sale.
            return TerminalChargeOutcome.Manual;
        }

        var active = terminals.Where(t => t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToList();
        if (active.Count == 0) return TerminalChargeOutcome.Manual;

        _amount = amount;
        _purpose = purpose;
        _invoiceReference = invoiceReference;
        _orderId = orderId;
        _comandaId = comandaId;
        _billPaymentId = billPaymentId;
        _cashShiftId = cashShiftId;
        _description = description;
        _lastCharge = null;

        AmountText = ReceiptFormatter.Cop(amount);
        ReferenceText = string.IsNullOrWhiteSpace(description) ? invoiceReference : $"{invoiceReference} · {description}";
        ErrorMessage = string.Empty;
        ResultTitle = string.Empty;
        ResultMessage = string.Empty;
        ResultColor = ThemePalette.TextFaint;
        _lastStatus = null;
        StageText = string.Empty;

        Terminals.Clear();
        foreach (var t in active) Terminals.Add(new TerminalChoiceViewModel(t, SelectTerminal));
        var userId = _session.CurrentUser?.Id;
        var preselected = (userId is { } uid ? Terminals.FirstOrDefault(t => t.Dto.AssignedUserId == uid) : null)
                          ?? Terminals.FirstOrDefault(t => t.Dto.AssignedUserId == null)
                          ?? Terminals[0];
        SelectTerminal(preselected);

        Phase = (int)ChargePhase.Select;
        _pending = new TaskCompletionSource<TerminalChargeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        return await _pending.Task;
    }

    private void SelectTerminal(TerminalChoiceViewModel choice)
    {
        if (!IsSelecting && IsOpen) return; // locked while a charge is in flight
        foreach (var t in Terminals) t.IsSelected = ReferenceEquals(t, choice);
        SelectedTerminal = choice;
    }

    /// <summary>"Cobrar": sends the amount to the selected terminal and waits for its answer.</summary>
    [RelayCommand]
    private async Task ChargeAsync()
    {
        if (!IsSelecting || SelectedTerminal is not { } terminal || _pending == null) return;

        var user = _session.CurrentUser;
        var request = new ChargeOnTerminalRequest(
            terminal.Dto.Id, _amount, _purpose, _invoiceReference, user?.Id, user?.Name ?? "Cajero",
            _orderId, _comandaId, _billPaymentId, _cashShiftId, _description);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        ErrorMessage = string.Empty;
        ShowStage(TerminalChargeStage.Connecting, T("Terminal.Stage.ConnectingTo"));
        Phase = (int)ChargePhase.Charging;

        var progress = new Progress<TerminalChargeProgress>(p =>
            Dispatcher.UIThread.Post(() => { if (IsCharging) ShowStage(p.Stage, p.Message); }));

        TerminalPaymentDto? charge = null;
        string? failure = null;
        try
        {
            charge = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>().ChargeAsync(request, progress, token);
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            failure = T("Terminal.Cancelled.FromPos");
        }
        catch (InvalidOperationException ex)
        {
            failure = ex.Message; // "El datáfono está ocupado", terminal inactive…
        }
        catch (Exception ex)
        {
            failure = T("Terminal.Error.Charge", ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        if (_pending == null) return; // dialog was torn down meanwhile

        _lastCharge = charge;
        if (charge is { IsApproved: true })
        {
            ResultTitle = T("Terminal.Result.Approved");
            ResultMessage = charge.ReferenceText;
            ResultColor = ThemePalette.Success;
            Phase = (int)ChargePhase.Approved;
            await Task.Delay(ApprovedAutoClose);
            Complete(new TerminalChargeOutcome(charge, false));
            return;
        }

        var status = charge?.Status ?? (token.IsCancellationRequested ? TerminalPaymentStatus.Cancelled : TerminalPaymentStatus.Error);
        ResultTitle = status switch
        {
            TerminalPaymentStatus.Declined => T("Terminal.Result.Declined"),
            TerminalPaymentStatus.Cancelled => T("Terminal.Result.Cancelled"),
            TerminalPaymentStatus.Timeout => T("Terminal.Result.Timeout"),
            _ => T("Terminal.Result.Error")
        };
        ResultMessage = charge?.Message ?? failure ?? T("Terminal.Result.Incomplete");
        _lastStatus = status;
        ResultColor = ColorFor(status);
        if (status == TerminalPaymentStatus.Error)
            ResultMessage += T("Terminal.Result.VerifyBeforeRetry");
        Phase = (int)ChargePhase.Failed;
    }

    private void ShowStage(TerminalChargeStage stage, string? message)
    {
        _lastStage = stage;
        StageIcon = stage switch
        {
            TerminalChargeStage.Connecting => "📡",
            TerminalChargeStage.WaitingForCard => "💳",
            TerminalChargeStage.Processing => "⏳",
            _ => "✅"
        };
        StageText = string.IsNullOrWhiteSpace(message)
            ? stage switch
            {
                TerminalChargeStage.Connecting => T("Terminal.Stage.Connecting"),
                TerminalChargeStage.WaitingForCard => T("Terminal.Stage.WaitingForCard"),
                TerminalChargeStage.Processing => T("Terminal.Stage.Processing"),
                _ => T("Terminal.Stage.Done")
            }
            : message;
    }

    /// <summary>64 px "Cancelar" while charging: cancels the token (driver answers Cancelled).</summary>
    [RelayCommand]
    private void CancelCharge()
    {
        if (!IsCharging) return;
        StageText = T("Terminal.Stage.Cancelling");
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Back to the terminal list to try again (same terminal preselected).</summary>
    [RelayCommand]
    private void Retry()
    {
        if (!IsFailed) return;
        ResultTitle = string.Empty;
        ResultMessage = string.Empty;
        Phase = (int)ChargePhase.Select;
    }

    /// <summary>"Cobrar manual": the caller continues with the typed-by-hand card path.</summary>
    [RelayCommand]
    private void UseManual()
    {
        if (IsCharging) return;
        Complete(new TerminalChargeOutcome(null, true, ResultMessage));
    }

    /// <summary>"Cancelar" before / after a charge: nothing collected, caller keeps its order open.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsCharging) { CancelCharge(); return; }
        if (IsApproved) return; // auto-closes with the approved charge
        var msg = IsFailed && !string.IsNullOrWhiteSpace(ResultMessage)
            ? $"{ResultTitle} · {ResultMessage}"
            : T("Terminal.Cancelled.Message");
        Complete(new TerminalChargeOutcome(_lastCharge, false, msg));
    }

    private void Complete(TerminalChargeOutcome outcome)
    {
        var tcs = _pending;
        _pending = null;
        IsOpen = false;
        Phase = (int)ChargePhase.Select;
        StageText = string.Empty;
        tcs?.TrySetResult(outcome);
    }
}
