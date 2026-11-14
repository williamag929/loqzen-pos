using Presentation.Desktop.Services;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Localization;

namespace Presentation.Desktop.ViewModels;

/// <summary>Provider choice for the terminal form (Spanish label, enum behind it).</summary>
public sealed record TerminalProviderOptionViewModel(PaymentTerminalProvider Provider, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Row of the datáfonos list: ● ping dot, provider, address, assignment, plus its own commands (edit, test, assign,
/// deactivate with inline confirmation). Observable because ping / assign results update the row in place.
/// </summary>
public sealed partial class PaymentTerminalRowViewModel : ObservableObject
{
    private readonly Action<PaymentTerminalRowViewModel> _onEdit;
    private readonly Func<PaymentTerminalRowViewModel, Task> _onTest;
    private readonly Action<PaymentTerminalRowViewModel> _onAssign;
    private readonly Func<PaymentTerminalRowViewModel, Task> _onDeactivate;

    public PaymentTerminalRowViewModel(PaymentTerminalDto dto, DateTime nowUtc,
        Action<PaymentTerminalRowViewModel> onEdit, Func<PaymentTerminalRowViewModel, Task> onTest,
        Action<PaymentTerminalRowViewModel> onAssign, Func<PaymentTerminalRowViewModel, Task> onDeactivate)
    {
        _dto = dto;
        _onEdit = onEdit;
        _onTest = onTest;
        _onAssign = onAssign;
        _onDeactivate = onDeactivate;
        _nowUtc = nowUtc;
        EditCommand = new RelayCommand(() => _onEdit(this));
        TestCommand = new AsyncRelayCommand(() => _onTest(this));
        AssignCommand = new RelayCommand(() => _onAssign(this));
        AskDeactivateCommand = new RelayCommand(() => IsConfirmingDeactivate = true);
        CancelDeactivateCommand = new RelayCommand(() => IsConfirmingDeactivate = false);
        ConfirmDeactivateCommand = new AsyncRelayCommand(() => _onDeactivate(this));
    }

    private DateTime _nowUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(ProviderLabel), nameof(AddressText), nameof(AssignmentLabel), nameof(DotColor),
        nameof(LastPingText), nameof(IsActive), nameof(IsInactive), nameof(SerialText), nameof(OrderText))]
    private PaymentTerminalDto _dto;

    public IRelayCommand EditCommand { get; }
    public IAsyncRelayCommand TestCommand { get; }
    public IRelayCommand AssignCommand { get; }
    public IRelayCommand AskDeactivateCommand { get; }
    public IRelayCommand CancelDeactivateCommand { get; }
    public IAsyncRelayCommand ConfirmDeactivateCommand { get; }

    public string Name => Dto.Name;
    public string ProviderLabel => Dto.ProviderLabel;
    public string AddressText => string.IsNullOrWhiteSpace(Dto.Address) ? T("Masters.PaymentTerminals.NoAddress") : Dto.Address!;
    public string SerialText => string.IsNullOrWhiteSpace(Dto.SerialNumber) ? string.Empty : T("Masters.PaymentTerminals.SerialText", Dto.SerialNumber);
    public string OrderText => T("Masters.OrderText", Dto.SortOrder);
    public string AssignmentLabel => string.IsNullOrWhiteSpace(Dto.AssignedUserName)
        ? T("Masters.PaymentTerminals.Assignment.AtRegister")
        : T("Masters.PaymentTerminals.Assignment.With", Dto.AssignedUserName);

    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);
    public bool IsActive => Dto.IsActive;
    public bool IsInactive => !Dto.IsActive;

    /// <summary>● green = last ping ok, red = failed, grey = never tested.</summary>
    public string DotColor => Dto.LastPingOk switch { true => ThemePalette.SuccessBright, false => ThemePalette.DangerBright, null => ThemePalette.TextFaint };
    public string LastPingText => Dto.LastSeenAt is { } s
        ? T(Dto.LastPingOk == true ? "Masters.PaymentTerminals.PingOk" : "Masters.PaymentTerminals.PingFail", TerminalRowViewModel.Ago(_nowUtc - s))
        : T("Masters.PaymentTerminals.NotTested");

    /// <summary>"Probar conexión" in flight / its last answer (shown under the row).</summary>
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string _testResultText = string.Empty;
    [ObservableProperty] private string _testResultColor = ThemePalette.TextMuted;
    public bool HasTestResult => !string.IsNullOrEmpty(TestResultText);

    /// <summary>Inline "¿Desactivar?" Sí / No instead of a dialog.</summary>
    [ObservableProperty] private bool _isConfirmingDeactivate;

    /// <summary>Inline "Asignar a…" chip strip (one row open at a time, see MastersViewModel).</summary>
    [ObservableProperty] private bool _isAssigning;

    public void Apply(PaymentTerminalDto dto, DateTime nowUtc)
    {
        _nowUtc = nowUtc;
        Dto = dto;
    }

    /// <summary>Language toggle: re-read the translated texts without losing the row state (assign strip, test result).</summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(SerialText));
        OnPropertyChanged(nameof(OrderText));
        OnPropertyChanged(nameof(AssignmentLabel));
        OnPropertyChanged(nameof(LastPingText));
    }
}

/// <summary>Row of the recent charges list (newest first): status colour, amount, reference, terminal, who, when.</summary>
public sealed class TerminalChargeRowViewModel
{
    public TerminalChargeRowViewModel(TerminalPaymentDto dto) => Dto = dto;

    public TerminalPaymentDto Dto { get; }
    public string TimeText => Dto.RequestedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public string AmountText => ReceiptFormatter.Cop(Dto.Amount);
    public string StatusLabel => Dto.Status switch
    {
        TerminalPaymentStatus.Pending => T("Masters.PaymentTerminals.Status.Pending"),
        TerminalPaymentStatus.Approved => T("Masters.PaymentTerminals.Status.Approved"),
        TerminalPaymentStatus.Declined => T("Masters.PaymentTerminals.Status.Declined"),
        TerminalPaymentStatus.Cancelled => T("Masters.PaymentTerminals.Status.Cancelled"),
        TerminalPaymentStatus.Timeout => T("Masters.PaymentTerminals.Status.Timeout"),
        TerminalPaymentStatus.Error => T("Masters.PaymentTerminals.Status.Error"),
        _ => Dto.StatusLabel
    };
    private static string T(string key) => Localizer.Instance.T(key);
    public string StatusColor => Dto.Status switch
    {
        TerminalPaymentStatus.Approved => ThemePalette.SuccessBright,
        TerminalPaymentStatus.Declined => ThemePalette.DangerBright,
        TerminalPaymentStatus.Timeout or TerminalPaymentStatus.Error => ThemePalette.WarningBright,
        TerminalPaymentStatus.Pending => ThemePalette.Info,
        _ => ThemePalette.TextMuted
    };
    public string ReferenceText => Dto.IsApproved ? Dto.ReferenceText : (Dto.Message ?? string.Empty);
    public string PurposeText => Dto.Purpose switch
    {
        TerminalPaymentPurpose.Sale => T("Masters.PaymentTerminals.Purpose.Sale"),
        TerminalPaymentPurpose.Comanda => T("Masters.PaymentTerminals.Purpose.Comanda"),
        TerminalPaymentPurpose.BillPayment => T("Masters.PaymentTerminals.Purpose.BillPayment"),
        _ => Dto.Purpose.ToString()
    };
    public string DetailText => string.Join(" · ", new[]
    {
        PurposeText + (string.IsNullOrWhiteSpace(Dto.InvoiceReference) ? string.Empty : $" {Dto.InvoiceReference}"),
        Dto.TerminalName,
        Dto.RequestedByName
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>"💳 Datáfonos": card terminals registry, assignment to waiters, connection test and the last 50 charges.</summary>
public partial class MastersViewModel
{
    public bool IsPaymentTerminalsTab => ActiveTab == MastersTab.PaymentTerminals;

    /// <summary>Tab button visibility: PaymentTerminals:Enabled in appsettings.json.</summary>
    public bool IsPaymentTerminalsTabVisible => _paymentTerminalSettings.Enabled;

    public ObservableCollection<PaymentTerminalRowViewModel> PaymentTerminals { get; } = new();
    public ObservableCollection<TerminalChargeRowViewModel> TerminalCharges { get; } = new();
    public ObservableCollection<TerminalProviderOptionViewModel> TerminalProviderOptions { get; } = new();
    public ObservableCollection<ChipViewModel> TerminalAssignOptions { get; } = new();
    public bool HasPaymentTerminals => PaymentTerminals.Count > 0;
    public bool HasTerminalCharges => TerminalCharges.Count > 0;

    [ObservableProperty] private bool _isLoadingPaymentTerminals;

    /// <summary>Active / answered counts from the last load (null = not loaded yet); the summary is derived so it follows the language.</summary>
    private (int Active, int Answered)? _paymentTerminalCounts;
    public string PaymentTerminalsSummary => _paymentTerminalCounts switch
    {
        null => string.Empty,
        (0, _) => T("Masters.PaymentTerminals.NoActive"),
        (var active, var answered) => T("Masters.PaymentTerminals.Summary", active, answered)
    };

    // ── Terminal form (modal like the other Maestros forms) ──────────────────
    [ObservableProperty] private bool _isPaymentTerminalFormVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentTerminalFormTitle))]
    private bool _isEditingPaymentTerminal;
    public string PaymentTerminalFormTitle => IsEditingPaymentTerminal ? T("Masters.PaymentTerminals.FormTitle.Edit") : T("Masters.PaymentTerminals.FormTitle.New");

    [ObservableProperty] private Guid _editPaymentTerminalId;
    [ObservableProperty] private string _paymentTerminalName = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentTerminalAddressHint))]
    private TerminalProviderOptionViewModel? _paymentTerminalProviderOption;
    [ObservableProperty] private string _paymentTerminalAddress = string.Empty;
    [ObservableProperty] private string _paymentTerminalSerial = string.Empty;
    [ObservableProperty] private bool _paymentTerminalIsActive = true;
    [ObservableProperty] private string _paymentTerminalSortOrderStr = "0";

    public string PaymentTerminalAddressHint => PaymentTerminalProviderOption?.Provider switch
    {
        PaymentTerminalProvider.Mock => T("Masters.PaymentTerminals.Hint.Mock"),
        PaymentTerminalProvider.JsonTcp => T("Masters.PaymentTerminals.Hint.JsonTcp"),
        null => T("Masters.PaymentTerminals.Hint.None"),
        _ => T("Masters.PaymentTerminals.Hint.Other")
    };

    private PaymentTerminalRowViewModel? _assignTarget;

    [RelayCommand]
    public void ShowPaymentTerminals()
    {
        ActiveTab = MastersTab.PaymentTerminals;
        _ = LoadPaymentTerminalsAsync();
    }

    private void EnsureProviderOptions()
    {
        if (TerminalProviderOptions.Count > 0) return;
        foreach (var p in _terminalDrivers.AvailableProviders.OrderBy(p => (int)p))
            TerminalProviderOptions.Add(new TerminalProviderOptionViewModel(p, PaymentTerminalProviders.Label(p)));
    }

    /// <summary>"↻ Actualizar": terminals + last 50 charges, each in its own scope (never throws to the UI).</summary>
    [RelayCommand]
    public async Task LoadPaymentTerminalsAsync()
    {
        if (IsLoadingPaymentTerminals) return;
        IsLoadingPaymentTerminals = true;
        EnsureProviderOptions();
        try
        {
            var now = DateTime.UtcNow;
            var terminals = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>().GetTerminalsAsync(includeInactive: true);
            });
            var openAssign = _assignTarget?.Dto.Id;
            PaymentTerminals.Clear();
            foreach (var t in terminals.OrderByDescending(t => t.IsActive).ThenBy(t => t.SortOrder).ThenBy(t => t.Name))
                PaymentTerminals.Add(new PaymentTerminalRowViewModel(t, now, EditPaymentTerminal, TestPaymentTerminalAsync, StartAssign, DeactivatePaymentTerminalAsync));
            _assignTarget = openAssign is { } id ? PaymentTerminals.FirstOrDefault(r => r.Dto.Id == id) : null;
            if (_assignTarget != null) _assignTarget.IsAssigning = true;

            var active = PaymentTerminals.Where(t => t.IsActive).ToList();
            _paymentTerminalCounts = (active.Count, active.Count(t => t.Dto.LastPingOk == true));
            OnPropertyChanged(nameof(PaymentTerminalsSummary));
        }
        catch (Exception ex)
        {
            Notification = T("Masters.PaymentTerminals.LoadError", ex.Message);
        }
        OnPropertyChanged(nameof(HasPaymentTerminals));

        try
        {
            var charges = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>().GetRecentChargesAsync(50);
            });
            TerminalCharges.Clear();
            foreach (var c in charges) TerminalCharges.Add(new TerminalChargeRowViewModel(c));
        }
        catch (Exception ex)
        {
            Notification = T("Masters.PaymentTerminals.ChargesError", ex.Message);
        }
        OnPropertyChanged(nameof(HasTerminalCharges));
        IsLoadingPaymentTerminals = false;
    }

    // ── Create / edit ────────────────────────────────────────────────────────
    [RelayCommand]
    public void NewPaymentTerminal()
    {
        EnsureProviderOptions();
        IsEditingPaymentTerminal = false;
        EditPaymentTerminalId = Guid.Empty;
        PaymentTerminalName = T("Masters.PaymentTerminals.DefaultName", PaymentTerminals.Count + 1);
        PaymentTerminalProviderOption = TerminalProviderOptions.FirstOrDefault(o => o.Provider == Core.Domain.Enums.PaymentTerminalProvider.Mock)
                                  ?? TerminalProviderOptions.FirstOrDefault();
        PaymentTerminalAddress = string.Empty;
        PaymentTerminalSerial = string.Empty;
        PaymentTerminalIsActive = true;
        PaymentTerminalSortOrderStr = (PaymentTerminals.Count == 0 ? 0 : PaymentTerminals.Max(t => t.Dto.SortOrder) + 1).ToString(CultureInfo.InvariantCulture);
        IsPaymentTerminalFormVisible = true;
    }

    private void EditPaymentTerminal(PaymentTerminalRowViewModel row)
    {
        EnsureProviderOptions();
        var t = row.Dto;
        IsEditingPaymentTerminal = true;
        EditPaymentTerminalId = t.Id;
        PaymentTerminalName = t.Name;
        PaymentTerminalProviderOption = TerminalProviderOptions.FirstOrDefault(o => o.Provider == t.Provider) ?? TerminalProviderOptions.FirstOrDefault();
        PaymentTerminalAddress = t.Address ?? string.Empty;
        PaymentTerminalSerial = t.SerialNumber ?? string.Empty;
        PaymentTerminalIsActive = t.IsActive;
        PaymentTerminalSortOrderStr = t.SortOrder.ToString(CultureInfo.InvariantCulture);
        IsPaymentTerminalFormVisible = true;
    }

    [RelayCommand]
    public async Task SavePaymentTerminalAsync()
    {
        if (string.IsNullOrWhiteSpace(PaymentTerminalName)) { Notification = T("Masters.PaymentTerminals.NameRequired"); return; }
        if (PaymentTerminalProviderOption is not { } provider) { Notification = T("Masters.PaymentTerminals.ChooseProviderWarn"); return; }
        var address = string.IsNullOrWhiteSpace(PaymentTerminalAddress) ? null : PaymentTerminalAddress.Trim();
        if (address == null && provider.Provider != Core.Domain.Enums.PaymentTerminalProvider.Mock)
        {
            Notification = T("Masters.PaymentTerminals.AddressRequired");
            return;
        }
        if (!int.TryParse(PaymentTerminalSortOrderStr, out var order)) order = 0;

        var request = new SavePaymentTerminalRequest(
            IsEditingPaymentTerminal ? EditPaymentTerminalId : null,
            PaymentTerminalName.Trim(), provider.Provider, address,
            string.IsNullOrWhiteSpace(PaymentTerminalSerial) ? null : PaymentTerminalSerial.Trim(),
            PaymentTerminalIsActive, order);
        try
        {
            await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>().SaveAsync(request);
            });
            IsPaymentTerminalFormVisible = false;
            Notification = T("Masters.PaymentTerminals.Saved", request.Name);
            await LoadPaymentTerminalsAsync();
        }
        catch (Exception ex) { Notification = T("Masters.Error", ex.Message); }
    }

    [RelayCommand] public void CancelPaymentTerminalForm() => IsPaymentTerminalFormVisible = false;

    // ── Test connection ──────────────────────────────────────────────────────
    private async Task TestPaymentTerminalAsync(PaymentTerminalRowViewModel row)
    {
        if (row.IsTesting) return;
        row.IsTesting = true;
        row.TestResultText = T("Masters.PaymentTerminals.Testing");
        row.TestResultColor = ThemePalette.TextMuted;
        try
        {
            var id = row.Dto.Id;
            var (ok, fresh) = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>();
                var result = await svc.TestConnectionAsync(id);
                return (result, await svc.GetByIdAsync(id));
            });
            if (fresh != null) row.Apply(fresh, DateTime.UtcNow);
            row.TestResultText = ok ? T("Masters.PaymentTerminals.TestOk") : T("Masters.PaymentTerminals.TestFail");
            row.TestResultColor = ok ? ThemePalette.SuccessBright : ThemePalette.DangerBright;
        }
        catch (Exception ex)
        {
            row.TestResultText = T("Masters.Error", ex.Message);
            row.TestResultColor = ThemePalette.DangerBright;
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    // ── Assign to a waiter / back to the register ────────────────────────────
    private void StartAssign(PaymentTerminalRowViewModel row)
    {
        if (_assignTarget != null && !ReferenceEquals(_assignTarget, row)) _assignTarget.IsAssigning = false;
        if (row.IsAssigning)
        {
            row.IsAssigning = false;
            _assignTarget = null;
            return;
        }
        _assignTarget = row;
        _ = BuildAssignOptionsAsync(row);
    }

    private async Task BuildAssignOptionsAsync(PaymentTerminalRowViewModel row)
    {
        TerminalAssignOptions.Clear();
        TerminalAssignOptions.Add(new ChipViewModel(T("Masters.PaymentTerminals.AtRegister"), v => _ = AssignPaymentTerminalAsync(row, null, null), string.Empty));
        try
        {
            var users = await _users.GetAllAsync();
            foreach (var u in users.Where(u => u.IsActive).OrderBy(u => u.Name))
            {
                var user = u;
                TerminalAssignOptions.Add(new ChipViewModel(T("Masters.PaymentTerminals.UserChip", user.Name), v => _ = AssignPaymentTerminalAsync(row, user.Id, user.Name), user.Id.ToString("N")));
            }
        }
        catch (Exception ex) { Notification = T("Masters.PaymentTerminals.UsersError", ex.Message); }
        row.IsAssigning = true;
    }

    private async Task AssignPaymentTerminalAsync(PaymentTerminalRowViewModel row, Guid? userId, string? userName)
    {
        try
        {
            var id = row.Dto.Id;
            var fresh = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>().AssignAsync(id, userId, userName);
            });
            row.Apply(fresh, DateTime.UtcNow);
            Notification = userName is null ? T("Masters.PaymentTerminals.StaysAtRegister", fresh.Name) : T("Masters.PaymentTerminals.AssignedTo", fresh.Name, userName);
        }
        catch (Exception ex) { Notification = T("Masters.Error", ex.Message); }
        row.IsAssigning = false;
        _assignTarget = null;
    }

    /// <summary>Language toggle: rows keep their state (assign strip, test result) and only re-read their texts.</summary>
    private void RefreshPaymentTerminalRowsLanguage()
    {
        foreach (var row in PaymentTerminals) row.RefreshLanguage();
        if (TerminalCharges.Count > 0)
        {
            var dtos = TerminalCharges.Select(c => c.Dto).ToList();
            TerminalCharges.Clear();
            foreach (var d in dtos) TerminalCharges.Add(new TerminalChargeRowViewModel(d));
        }
        if (_assignTarget is { } target && TerminalAssignOptions.Count > 0) _ = BuildAssignOptionsAsync(target);
    }

    // ── Deactivate (inline confirmation) ─────────────────────────────────────
    private async Task DeactivatePaymentTerminalAsync(PaymentTerminalRowViewModel row)
    {
        try
        {
            var id = row.Dto.Id;
            await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IPaymentTerminalService>().DeactivateAsync(id);
            });
            Notification = T("Masters.PaymentTerminals.Deactivated", row.Name);
            await LoadPaymentTerminalsAsync();
        }
        catch (Exception ex)
        {
            row.IsConfirmingDeactivate = false;
            Notification = T("Masters.Error", ex.Message);
        }
    }
}
