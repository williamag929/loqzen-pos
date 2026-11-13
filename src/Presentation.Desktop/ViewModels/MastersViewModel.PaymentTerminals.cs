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
    public string AddressText => string.IsNullOrWhiteSpace(Dto.Address) ? "Sin dirección" : Dto.Address!;
    public string SerialText => string.IsNullOrWhiteSpace(Dto.SerialNumber) ? string.Empty : $"S/N {Dto.SerialNumber}";
    public string OrderText => $"Orden {Dto.SortOrder}";
    public string AssignmentLabel => Dto.AssignmentLabel;
    public bool IsActive => Dto.IsActive;
    public bool IsInactive => !Dto.IsActive;

    /// <summary>● green = last ping ok, red = failed, grey = never tested.</summary>
    public string DotColor => Dto.LastPingOk switch { true => "#34D399", false => "#F87171", null => "#64748B" };
    public string LastPingText => Dto.LastSeenAt is { } s
        ? (Dto.LastPingOk == true ? "respondió hace " : "falló hace ") + TerminalRowViewModel.Ago(_nowUtc - s)
        : "sin probar";

    /// <summary>"Probar conexión" in flight / its last answer (shown under the row).</summary>
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string _testResultText = string.Empty;
    [ObservableProperty] private string _testResultColor = "#94A3B8";
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
}

/// <summary>Row of the recent charges list (newest first): status colour, amount, reference, terminal, who, when.</summary>
public sealed class TerminalChargeRowViewModel
{
    public TerminalChargeRowViewModel(TerminalPaymentDto dto) => Dto = dto;

    public TerminalPaymentDto Dto { get; }
    public string TimeText => Dto.RequestedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public string AmountText => ReceiptFormatter.Cop(Dto.Amount);
    public string StatusLabel => Dto.StatusLabel;
    public string StatusColor => Dto.Status switch
    {
        TerminalPaymentStatus.Approved => "#34D399",
        TerminalPaymentStatus.Declined => "#F87171",
        TerminalPaymentStatus.Timeout or TerminalPaymentStatus.Error => "#FBBF24",
        TerminalPaymentStatus.Pending => "#38BDF8",
        _ => "#94A3B8"
    };
    public string ReferenceText => Dto.IsApproved ? Dto.ReferenceText : (Dto.Message ?? string.Empty);
    public string PurposeText => Dto.Purpose switch
    {
        TerminalPaymentPurpose.Sale => "Venta",
        TerminalPaymentPurpose.Comanda => "Comanda",
        TerminalPaymentPurpose.BillPayment => "Recaudo",
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
    [ObservableProperty] private string _paymentTerminalsSummary = string.Empty;

    // ── Terminal form (modal like the other Maestros forms) ──────────────────
    [ObservableProperty] private bool _isPaymentTerminalFormVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentTerminalFormTitle))]
    private bool _isEditingPaymentTerminal;
    public string PaymentTerminalFormTitle => IsEditingPaymentTerminal ? "💳 Editar datáfono" : "💳 Nuevo datáfono";

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
        PaymentTerminalProvider.Mock => "Simulado: no necesita dirección. Montos terminados en 99 se rechazan, en 98 no responden.",
        PaymentTerminalProvider.JsonTcp => "Dirección IP:puerto del emulador o del datáfono (ej. 192.168.1.50:9100).",
        null => "Elija el proveedor.",
        _ => "Dirección IP:puerto (o puerto serie) del datáfono, según el proveedor."
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
            PaymentTerminalsSummary = active.Count == 0
                ? "No hay datáfonos activos: las tarjetas se registran a mano. Agregue uno para cobrar desde el POS."
                : $"{active.Count} datáfono(s) activo(s) · {active.Count(t => t.Dto.LastPingOk == true)} respondieron en la última prueba";
        }
        catch (Exception ex)
        {
            Notification = $"❌ Datáfonos: {ex.Message}";
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
            Notification = $"❌ Cobros en datáfono: {ex.Message}";
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
        PaymentTerminalName = PaymentTerminals.Count == 0 ? "Datáfono 1" : $"Datáfono {PaymentTerminals.Count + 1}";
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
        if (string.IsNullOrWhiteSpace(PaymentTerminalName)) { Notification = "⚠️ El nombre del datáfono es obligatorio."; return; }
        if (PaymentTerminalProviderOption is not { } provider) { Notification = "⚠️ Elija el proveedor."; return; }
        var address = string.IsNullOrWhiteSpace(PaymentTerminalAddress) ? null : PaymentTerminalAddress.Trim();
        if (address == null && provider.Provider != Core.Domain.Enums.PaymentTerminalProvider.Mock)
        {
            Notification = "⚠️ La dirección es obligatoria para este proveedor.";
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
            Notification = $"✅ Datáfono \"{request.Name}\" guardado";
            await LoadPaymentTerminalsAsync();
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
    }

    [RelayCommand] public void CancelPaymentTerminalForm() => IsPaymentTerminalFormVisible = false;

    // ── Test connection ──────────────────────────────────────────────────────
    private async Task TestPaymentTerminalAsync(PaymentTerminalRowViewModel row)
    {
        if (row.IsTesting) return;
        row.IsTesting = true;
        row.TestResultText = "Probando conexión…";
        row.TestResultColor = "#94A3B8";
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
            row.TestResultText = ok ? "✅ El datáfono respondió" : "❌ Sin respuesta del datáfono (revise cable, red o dirección)";
            row.TestResultColor = ok ? "#34D399" : "#F87171";
        }
        catch (Exception ex)
        {
            row.TestResultText = $"❌ {ex.Message}";
            row.TestResultColor = "#F87171";
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
        TerminalAssignOptions.Add(new ChipViewModel("🏪 En caja", v => _ = AssignPaymentTerminalAsync(row, null, null), string.Empty));
        try
        {
            var users = await _users.GetAllAsync();
            foreach (var u in users.Where(u => u.IsActive).OrderBy(u => u.Name))
            {
                var user = u;
                TerminalAssignOptions.Add(new ChipViewModel($"👤 {user.Name}", v => _ = AssignPaymentTerminalAsync(row, user.Id, user.Name), user.Id.ToString("N")));
            }
        }
        catch (Exception ex) { Notification = $"❌ Usuarios: {ex.Message}"; }
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
            Notification = userName is null ? $"✅ {fresh.Name} queda en caja" : $"✅ {fresh.Name} asignado a {userName}";
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
        row.IsAssigning = false;
        _assignTarget = null;
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
            Notification = $"🗑️ Datáfono \"{row.Name}\" desactivado";
            await LoadPaymentTerminalsAsync();
        }
        catch (Exception ex)
        {
            row.IsConfirmingDeactivate = false;
            Notification = $"❌ {ex.Message}";
        }
    }
}
