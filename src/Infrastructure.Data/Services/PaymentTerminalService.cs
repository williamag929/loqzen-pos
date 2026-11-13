using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

/// <summary>
/// Card terminal (datáfono) registry + charge flow. A charge is: validate → insert a Pending
/// <see cref="TerminalPayment"/> (SaveChanges) → call the provider driver → update the row (SaveChanges). The row is
/// written BEFORE the driver is called so a crash mid-charge still leaves a trace to reconcile against the terminal's
/// batch report. The DbContext is never touched while the driver call is in flight (it is not thread-safe); the driver
/// runs purely on the request record and the result is applied once it returns.
/// </summary>
public class PaymentTerminalService : IPaymentTerminalService
{
    /// <summary>Grace added to ChargeTimeoutSeconds before a Pending row is considered abandoned (crash / kill).</summary>
    private const int BusyGraceSeconds = 30;
    private const string RecoveredMessage = "Sin respuesta (recuperado)";
    private const string BusyMessage = "El datáfono está ocupado con otro cobro. Espere a que termine o cancélelo.";

    private readonly PosDbContext _db;
    private readonly IPaymentTerminalDriverFactory _drivers;
    private readonly PaymentTerminalSettings _settings;
    private readonly IComandaService _comandas;
    private readonly ICashShiftService _shifts;
    private readonly TimeProvider _clock;
    // Kept for parity with the other services (register scoping); terminals are shared by every register today.
    private readonly ITerminalContext? _terminal;

    public PaymentTerminalService(PosDbContext db, IPaymentTerminalDriverFactory drivers, PaymentTerminalSettings settings,
        IComandaService comandas, ICashShiftService shifts, TimeProvider? clock = null, ITerminalContext? terminal = null)
    {
        _db = db;
        _drivers = drivers;
        _settings = settings ?? new PaymentTerminalSettings();
        _comandas = comandas;
        _shifts = shifts;
        _clock = clock ?? TimeProvider.System;
        _terminal = terminal;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private TimeSpan ChargeTimeout => TimeSpan.FromSeconds(Math.Max(5, _settings.ChargeTimeoutSeconds));

    /// <summary>A Pending row older than this is a leftover of a crashed charge, not a live one.</summary>
    private DateTime StaleBefore => UtcNow - ChargeTimeout - TimeSpan.FromSeconds(BusyGraceSeconds);

    // ── registry ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PaymentTerminalDto>> GetTerminalsAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var query = _db.PaymentTerminals.AsNoTracking();
        if (!includeInactive) query = query.Where(t => t.IsActive);
        var list = await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<PaymentTerminalDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var t = await _db.PaymentTerminals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t == null ? null : ToDto(t);
    }

    public async Task<PaymentTerminalDto> SaveAsync(SavePaymentTerminalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // --- validate (before touching any tracked entity) ---
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 60)
            throw new ArgumentException("El nombre del datáfono es obligatorio (máximo 60 caracteres).", nameof(request));
        var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        if (address is { Length: > 120 })
            throw new ArgumentException("La dirección admite máximo 120 caracteres.", nameof(request));
        if (address == null && request.Provider != PaymentTerminalProvider.Mock)
            throw new ArgumentException("Indique la dirección del datáfono (por ejemplo host:puerto).", nameof(request));
        var serial = string.IsNullOrWhiteSpace(request.SerialNumber) ? null : request.SerialNumber.Trim();
        if (serial is { Length: > 60 })
            throw new ArgumentException("El serial admite máximo 60 caracteres.", nameof(request));
        if (!_drivers.AvailableProviders.Contains(request.Provider))
            throw new ArgumentException($"El proveedor '{PaymentTerminalProviders.Label(request.Provider)}' no está disponible en esta instalación.", nameof(request));

        // Uniqueness among active terminals, case/accent-insensitive. Compared in memory: SQLite lower() only folds
        // ASCII and the registry is tiny.
        if (request.IsActive)
        {
            var others = await _db.PaymentTerminals.AsNoTracking()
                .Where(t => t.IsActive && t.Id != request.Id)
                .Select(t => t.Name)
                .ToListAsync(ct);
            if (others.Any(n => SameName(n, name)))
                throw new InvalidOperationException($"Ya existe un datáfono llamado '{name}'.");
        }

        PaymentTerminal entity;
        if (request.Id is not { } id || id == Guid.Empty)
        {
            entity = new PaymentTerminal { CreatedAt = UtcNow };
            _db.PaymentTerminals.Add(entity);
        }
        else
        {
            entity = await _db.PaymentTerminals.FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new KeyNotFoundException("Datáfono no encontrado.");
            if (entity.IsActive && !request.IsActive)
                await EnsureNoLiveChargeAsync(entity.Id, ct);
            entity.UpdatedAt = UtcNow;
        }

        // --- mutate ---
        entity.Name = name;
        entity.Provider = request.Provider;
        entity.Address = address;
        entity.SerialNumber = serial;
        entity.IsActive = request.IsActive;
        entity.SortOrder = request.SortOrder;
        if (!entity.IsActive)
        {
            entity.AssignedUserId = null;
            entity.AssignedUserName = null;
        }

        await _db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.PaymentTerminals.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new KeyNotFoundException("Datáfono no encontrado.");
        if (!entity.IsActive) return;
        await EnsureNoLiveChargeAsync(entity.Id, ct);

        entity.IsActive = false;
        entity.AssignedUserId = null;
        entity.AssignedUserName = null;
        entity.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PaymentTerminalDto> AssignAsync(Guid terminalId, Guid? userId, string? userName, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
        if (name is { Length: > 100 })
            throw new ArgumentException("El nombre del usuario admite máximo 100 caracteres.", nameof(userName));
        if (userId != null && name == null)
            throw new ArgumentException("Indique el nombre del usuario que recibe el datáfono.", nameof(userName));

        var entity = await _db.PaymentTerminals.FirstOrDefaultAsync(t => t.Id == terminalId, ct)
            ?? throw new KeyNotFoundException("Datáfono no encontrado.");
        if (!entity.IsActive)
            throw new InvalidOperationException("El datáfono está inactivo.");

        entity.AssignedUserId = name == null ? null : userId;
        entity.AssignedUserName = name;
        entity.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> TestConnectionAsync(Guid terminalId, CancellationToken ct = default)
    {
        PaymentTerminal? entity;
        try
        {
            entity = await _db.PaymentTerminals.FirstOrDefaultAsync(t => t.Id == terminalId, ct);
        }
        catch
        {
            return false;
        }
        if (entity == null) return false;

        var ok = false;
        try
        {
            var driver = _drivers.GetDriver(entity.Provider);
            ok = await driver.PingAsync(entity.Address, ct);
        }
        catch
        {
            ok = false;
        }

        try
        {
            entity.LastPingOk = ok;
            if (ok) entity.LastSeenAt = UtcNow;
            entity.UpdatedAt = UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // Diagnostics only: a failed status write must not surface as a connection error.
        }
        return ok;
    }

    // ── charge ───────────────────────────────────────────────────────────────

    public async Task<TerminalPaymentDto> ChargeAsync(ChargeOnTerminalRequest request, IProgress<TerminalChargeProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // --- validate ---
        if (request.Amount <= 0)
            throw new ArgumentException("El monto a cobrar debe ser mayor que cero.", nameof(request));
        var targets = new[] { request.OrderId, request.ComandaId, request.BillPaymentId }.Count(x => x != null);
        if (targets > 1)
            throw new ArgumentException("El cobro solo puede asociarse a una orden, una comanda o un recaudo.", nameof(request));
        var invoiceRef = string.IsNullOrWhiteSpace(request.InvoiceReference) ? null : Truncate(request.InvoiceReference.Trim(), 50);

        var terminal = await _db.PaymentTerminals.FirstOrDefaultAsync(t => t.Id == request.TerminalId, ct)
            ?? throw new KeyNotFoundException("Datáfono no encontrado.");
        if (!terminal.IsActive)
            throw new InvalidOperationException("El datáfono está inactivo.");

        var now = UtcNow;
        var staleBefore = StaleBefore;
        var pending = await _db.TerminalPayments
            .Where(p => p.PaymentTerminalId == terminal.Id && p.Status == TerminalPaymentStatus.Pending)
            .ToListAsync(ct);
        if (pending.Any(p => p.RequestedAt >= staleBefore))
            throw new InvalidOperationException(BusyMessage);

        // --- mutate: recover abandoned rows + write the Pending audit row BEFORE talking to the device ---
        foreach (var stale in pending)
        {
            stale.Status = TerminalPaymentStatus.Error;
            stale.Message = RecoveredMessage;
            stale.CompletedAt = now;
            stale.UpdatedAt = now;
        }

        var currency = string.IsNullOrWhiteSpace(_settings.Currency) ? "COP" : _settings.Currency.Trim().ToUpperInvariant();
        var row = new TerminalPayment
        {
            PaymentTerminalId = terminal.Id,
            TerminalName = terminal.Name,
            Purpose = request.Purpose,
            Amount = request.Amount,
            Currency = Truncate(currency, 3)!,
            InvoiceReference = invoiceRef,
            OrderId = request.OrderId,
            ComandaId = request.ComandaId,
            BillPaymentId = request.BillPaymentId,
            CashShiftId = request.CashShiftId,
            Status = TerminalPaymentStatus.Pending,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByName = Truncate(string.IsNullOrWhiteSpace(request.RequestedByName) ? "Caja" : request.RequestedByName.Trim(), 100)!,
            RequestedAt = now,
            CreatedAt = now
        };
        _db.TerminalPayments.Add(row);
        await _db.SaveChangesAsync(ct);

        // --- talk to the device (no DbContext access while this awaits) ---
        var result = await RunDriverAsync(terminal, row, request.Description, progress, ct);

        // --- persist the answer; never with the caller's token: a cancelled charge must still be recorded ---
        var done = UtcNow;
        row.Status = result.Status;
        row.AuthorizationCode = Truncate(result.AuthorizationCode, 40);
        row.CardBrand = Truncate(result.CardBrand, 30);
        row.CardLast4 = Truncate(result.CardLast4, 4);
        row.ProviderReference = Truncate(result.ProviderReference, 80);
        row.Message = Truncate(result.Message, 300);
        row.VoucherText = result.VoucherLines is { Count: > 0 } v ? Truncate(string.Join('\n', v), 2000) : null;
        row.CompletedAt = done;
        row.UpdatedAt = done;
        if (result.Status == TerminalPaymentStatus.Approved)
        {
            terminal.LastSeenAt = done;
            terminal.LastPingOk = true;
            terminal.UpdatedAt = done;
        }
        await _db.SaveChangesAsync(CancellationToken.None);

        return ToDto(row);
    }

    /// <summary>
    /// Calls the driver with a token linked to the caller's and to the configured timeout, and maps every exit
    /// (timeout, cancel, driver exception) to a business status. Never throws.
    /// </summary>
    private async Task<TerminalChargeResult> RunDriverAsync(PaymentTerminal terminal, TerminalPayment row, string? description,
        IProgress<TerminalChargeProgress>? progress, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(ChargeTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        TerminalChargeResult result;
        try
        {
            progress?.Report(new TerminalChargeProgress(TerminalChargeStage.Connecting, $"Conectando con {terminal.Name}…"));
            var driver = _drivers.GetDriver(terminal.Provider);
            var request = new TerminalChargeRequest(terminal.Address, row.Amount, row.Currency, row.InvoiceReference ?? string.Empty, description);
            result = await driver.ChargeAsync(request, progress, linked.Token).ConfigureAwait(false)
                     ?? TerminalChargeResult.Error("El driver no devolvió respuesta.");
        }
        catch (OperationCanceledException)
        {
            result = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? TerminalChargeResult.Timeout()
                : TerminalChargeResult.Cancelled();
        }
        catch (Exception ex)
        {
            result = TerminalChargeResult.Error(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
        }

        // Drivers answer Cancelled for any token cancellation; tell a timeout apart from the cashier's "Cancelar".
        if (result.Status == TerminalPaymentStatus.Cancelled && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            result = TerminalChargeResult.Timeout();

        return result;
    }

    public async Task<PayComandaOnTerminalResult> PayComandaOnTerminalAsync(Guid comandaId, Guid terminalId, Guid? requestedByUserId, string requestedByName,
        IProgress<TerminalChargeProgress>? progress = null, CancellationToken ct = default)
    {
        // --- validate everything before the comanda is touched ---
        var comanda = await _comandas.GetByIdAsync(comandaId, ct)
            ?? throw new KeyNotFoundException("Comanda no encontrada.");
        if (comanda.Status is ComandaStatus.Closed or ComandaStatus.Cancelled)
            throw new InvalidOperationException("La comanda ya está cerrada.");

        var shift = await _shifts.GetCurrentShiftAsync(ct)
            ?? throw new InvalidOperationException("Abra turno en la caja para cobrar en mesa.");

        var terminalActive = await _db.PaymentTerminals.AsNoTracking()
            .Where(t => t.Id == terminalId).Select(t => (bool?)t.IsActive).FirstOrDefaultAsync(ct);
        if (terminalActive == null) throw new KeyNotFoundException("Datáfono no encontrado.");
        if (terminalActive == false) throw new InvalidOperationException("El datáfono está inactivo.");
        // A busy terminal must not move table B to ReadyToPay (and push its pending items to the kitchen).
        await EnsureNoLiveChargeAsync(terminalId, ct);

        if (comanda.Status == ComandaStatus.Open)
            comanda = await _comandas.MarkReadyToPayAsync(comandaId, ct);

        var total = comanda.Total;
        if (total <= 0)
            throw new InvalidOperationException("La comanda no tiene productos.");

        var charge = await ChargeAsync(new ChargeOnTerminalRequest(
            terminalId, total, TerminalPaymentPurpose.Comanda, $"Comanda #{comanda.Number}",
            requestedByUserId, requestedByName,
            ComandaId: comandaId, CashShiftId: shift.Id,
            Description: string.IsNullOrWhiteSpace(comanda.TableName) ? null : comanda.TableName), progress, ct);

        if (!charge.IsApproved)
            return new PayComandaOnTerminalResult(charge, null);

        // The card is already charged: closing must not be aborted by the caller's token.
        PaymentResultDto payment;
        try
        {
            payment = await _comandas.CloseAndPayAsync(comandaId, shift.Id, PaymentMethod.Card, total, charge.ReferenceText, charge.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            payment = PaymentResultDto.Failed(Guid.Empty, ex.Message);
        }

        if (payment.Success)
        {
            var row = await _db.TerminalPayments.FirstOrDefaultAsync(p => p.Id == charge.Id, CancellationToken.None);
            if (row != null)
            {
                row.OrderId = payment.OrderId;
                row.UpdatedAt = UtcNow;
                await _db.SaveChangesAsync(CancellationToken.None);
                charge = ToDto(row);
            }
        }
        return new PayComandaOnTerminalResult(charge, payment);
    }

    // ── queries ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TerminalPaymentDto>> GetRecentChargesAsync(int take = 50, Guid? terminalId = null, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var query = _db.TerminalPayments.AsNoTracking();
        if (terminalId is { } id) query = query.Where(p => p.PaymentTerminalId == id);
        var list = await query.OrderByDescending(p => p.RequestedAt).Take(take).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<TerminalPaymentDto>> GetApprovedForShiftAsync(Guid cashShiftId, CancellationToken ct = default)
    {
        var list = await _db.TerminalPayments.AsNoTracking()
            .Where(p => p.CashShiftId == cashShiftId && p.Status == TerminalPaymentStatus.Approved)
            .OrderBy(p => p.RequestedAt)
            .ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Throws when a live Pending charge exists on the terminal; abandoned rows are recovered first.</summary>
    private async Task EnsureNoLiveChargeAsync(Guid terminalId, CancellationToken ct)
    {
        var staleBefore = StaleBefore;
        var pending = await _db.TerminalPayments
            .Where(p => p.PaymentTerminalId == terminalId && p.Status == TerminalPaymentStatus.Pending)
            .ToListAsync(ct);
        if (pending.Any(p => p.RequestedAt >= staleBefore))
            throw new InvalidOperationException(BusyMessage);
        var now = UtcNow;
        foreach (var stale in pending)
        {
            stale.Status = TerminalPaymentStatus.Error;
            stale.Message = RecoveredMessage;
            stale.CompletedAt = now;
            stale.UpdatedAt = now;
        }
    }

    private static bool SameName(string a, string b) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    private static string? Truncate(string? s, int max) =>
        s == null ? null : s.Length <= max ? s : s[..max];

    private static PaymentTerminalDto ToDto(PaymentTerminal t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Provider = t.Provider,
        Address = t.Address,
        SerialNumber = t.SerialNumber,
        IsActive = t.IsActive,
        SortOrder = t.SortOrder,
        AssignedUserId = t.AssignedUserId,
        AssignedUserName = t.AssignedUserName,
        LastSeenAt = t.LastSeenAt,
        LastPingOk = t.LastPingOk
    };

    private static TerminalPaymentDto ToDto(TerminalPayment p) => new()
    {
        Id = p.Id,
        PaymentTerminalId = p.PaymentTerminalId,
        TerminalName = p.TerminalName,
        Purpose = p.Purpose,
        Amount = p.Amount,
        Currency = p.Currency,
        InvoiceReference = p.InvoiceReference,
        OrderId = p.OrderId,
        ComandaId = p.ComandaId,
        BillPaymentId = p.BillPaymentId,
        Status = p.Status,
        AuthorizationCode = p.AuthorizationCode,
        CardBrand = p.CardBrand,
        CardLast4 = p.CardLast4,
        ProviderReference = p.ProviderReference,
        Message = p.Message,
        VoucherText = p.VoucherText,
        RequestedByName = p.RequestedByName,
        RequestedAt = p.RequestedAt,
        CompletedAt = p.CompletedAt
    };
}
