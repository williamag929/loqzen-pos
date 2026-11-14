using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Sync;

/// <summary>
/// IComandaService for a SATELLITE register: the master owns every comanda, so reads and mutations are forwarded to its
/// <c>/api/tables</c> and <c>/api/comandas/*</c> routes. <see cref="CloseAndPayAsync(Guid, Guid, PaymentMethod, decimal, string?, Guid?, CancellationToken)"/>
/// invoices on THIS database: lock on the master → local Order + Payment on the satellite shift (ISalesService) →
/// Outbox ComandaClosedRemotely → best-effort POST close-remote (the Outbox guarantees delivery anyway).
/// Errors: 409/404/400 from the master become InvalidOperationException / KeyNotFoundException / ArgumentException with
/// the server's message; an unreachable master is HttpRequestException("Sin conexión con la caja principal: …").
/// </summary>
public class RemoteComandaService : IComandaService
{
    private readonly MasterHttp _master;
    private readonly ITerminalContext _terminal;
    private readonly PosDbContext _db;
    private readonly ISalesService _sales;
    private readonly TimeProvider _clock;

    public RemoteComandaService(HttpClient http, TerminalSettings settings, ITerminalContext terminal, PosDbContext db, ISalesService sales, TimeProvider? clock = null)
    {
        _master = new MasterHttp(http, settings);
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sales = sales ?? throw new ArgumentNullException(nameof(sales));
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>This register's code as the master knows it (ITerminalContext once set, else TerminalSettings.Code).</summary>
    private string TerminalCode => TerminalSettings.NormalizeCode(_terminal is { IsSet: true } ? _terminal.Code : _master.Settings.Code);

    // ── reads ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<DiningTableDto>> GetTablesAsync(CancellationToken ct = default) =>
        ReadListAsync<DiningTableDto>("/api/tables", ct);

    public Task<IReadOnlyList<ComandaDto>> GetActiveComandasAsync(CancellationToken ct = default) =>
        ReadListAsync<ComandaDto>("/api/comandas", ct);

    public async Task<ComandaDto?> GetByIdAsync(Guid comandaId, CancellationToken ct = default)
    {
        try
        {
            return await _master.GetAsync<ComandaDto>($"/api/comandas/{comandaId}", ct);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(string path, CancellationToken ct) =>
        await _master.GetAsync<List<T>>(path, ct);

    // ── mutations forwarded as-is ────────────────────────────────────────────

    public Task<ComandaDto> OpenComandaAsync(string tableName, string waiterName, Guid? diningTableId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("La mesa es obligatoria.", nameof(tableName));
        return Post($"/api/comandas", new { tableName = tableName.Trim(), waiterName, diningTableId }, ct);
    }

    public Task<ComandaDto> AddItemAsync(Guid comandaId, Guid productId, decimal quantity = 1, decimal? weightKg = null, string? notes = null, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad debe ser positiva.");
        if (weightKg is <= 0) throw new ArgumentOutOfRangeException(nameof(weightKg), "El peso debe ser positivo.");
        return Post($"/api/comandas/{comandaId}/items", new { productId, quantity, weightKg, notes }, ct);
    }

    public Task<ComandaDto> CancelPendingItemAsync(Guid comandaId, Guid itemId, CancellationToken ct = default) =>
        Post($"/api/comandas/{comandaId}/items/{itemId}/cancel", null, ct);

    /// <summary>
    /// The manager approved on THIS register (local ManagerOverride). The master records and consumes its own override
    /// from the manager's name; the local one is consumed here afterwards so a single PIN cannot strike two items.
    /// </summary>
    public async Task<ComandaDto> CancelConfirmedItemAsync(Guid comandaId, Guid itemId, Guid managerOverrideId, CancellationToken ct = default)
    {
        var approval = await _db.ManagerOverrides.FirstOrDefaultAsync(o => o.Id == managerOverrideId, ct);
        if (approval == null || approval.Action != AuthorizedAction.CancelKitchenItem || approval.ConsumedAt != null)
            throw new InvalidOperationException("Se requiere autorización de gerente.");

        var dto = await Post($"/api/comandas/{comandaId}/items/{itemId}/cancel-confirmed",
            new { managerName = approval.ManagerName, terminalCode = TerminalCode, reason = approval.Reference }, ct);

        approval.ConsumedAt = UtcNow;
        approval.ConsumedFor = $"ComandaItem {itemId} (caja principal)";
        approval.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return dto;
    }

    public Task<ComandaDto> SetItemNotesAsync(Guid comandaId, Guid itemId, string? notes, CancellationToken ct = default)
    {
        var clean = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (clean?.Length > ComandaSettings.MaxNotesLength)
            throw new ArgumentException($"La nota no puede superar {ComandaSettings.MaxNotesLength} caracteres.", nameof(notes));
        return Post($"/api/comandas/{comandaId}/items/{itemId}/notes", new { notes = clean }, ct);
    }

    public Task<ComandaDto> SetItemHoldAsync(Guid comandaId, Guid itemId, bool onHold, CancellationToken ct = default) =>
        Post($"/api/comandas/{comandaId}/items/{itemId}/hold", new { onHold }, ct);

    public Task<ComandaDto> ConfirmPendingNowAsync(Guid comandaId, CancellationToken ct = default) =>
        Post($"/api/comandas/{comandaId}/send", null, ct);

    /// <summary>The master's timer confirms due items for every register; a satellite never does.</summary>
    public Task<int> ConfirmDueItemsAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<ComandaDto> MarkReadyToPayAsync(Guid comandaId, CancellationToken ct = default) =>
        Post($"/api/comandas/{comandaId}/ready", null, ct);

    public Task<ComandaDto> ReopenAsync(Guid comandaId, CancellationToken ct = default) =>
        Post($"/api/comandas/{comandaId}/reopen", null, ct);

    public Task<ComandaDto> LockForPaymentAsync(Guid comandaId, string terminalCode, CancellationToken ct = default) =>
        Post($"/api/comandas/{comandaId}/payment-lock", new { terminalCode = TerminalSettings.NormalizeCode(terminalCode) }, ct);

    public Task<ComandaDto> UnlockPaymentAsync(Guid comandaId, string terminalCode, CancellationToken ct = default) =>
        Post($"/api/comandas/{comandaId}/payment-unlock", new { terminalCode = TerminalSettings.NormalizeCode(terminalCode) }, ct);

    public Task<ComandaDto> CloseRemotelyAsync(ComandaRemoteCloseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Post($"/api/comandas/{request.ComandaId}/close-remote", request, ct);
    }

    private Task<ComandaDto> Post(string path, object? body, CancellationToken ct) => _master.PostAsync<ComandaDto>(path, body, ct);

    // ── close + pay on THIS register ─────────────────────────────────────────

    public Task<PaymentResultDto> CloseAndPayAsync(Guid comandaId, Guid cashShiftId, PaymentMethod method, decimal tenderedAmount, CancellationToken ct = default)
        => CloseAndPayAsync(comandaId, cashShiftId, method, tenderedAmount, null, null, ct);

    public async Task<PaymentResultDto> CloseAndPayAsync(Guid comandaId, Guid cashShiftId, PaymentMethod method, decimal tenderedAmount, string? reference, Guid? terminalPaymentId, CancellationToken ct = default)
    {
        var code = TerminalCode;

        // Local preconditions first: no point reserving the table on the master when this register cannot invoice.
        var shiftOpen = await _db.CashShifts.AnyAsync(s => s.Id == cashShiftId && s.Status == CashShiftStatus.Open, ct);
        if (!shiftOpen)
            return PaymentResultDto.Failed(Guid.Empty, "No hay turno de caja abierto.");

        // (a) Reserve the table on the master (Open → ReadyToPay, pending items go to the kitchen). 409 propagates.
        var comanda = await LockForPaymentAsync(comandaId, code, ct);

        var paid = false;
        Guid? draftOrderId = null;
        try
        {
            // Validate everything BEFORE creating the local order.
            if (comanda.Status is ComandaStatus.Closed or ComandaStatus.Cancelled)
                return await UnlockAndFailAsync(comandaId, code, "La comanda ya está cerrada.");

            var billable = comanda.BillableItems.ToList();
            if (billable.Count == 0)
                return await UnlockAndFailAsync(comandaId, code, "La comanda no tiene productos.");

            var total = Math.Round(comanda.Total, 2);
            if (method == PaymentMethod.Cash && tenderedAmount < total)
                return await UnlockAndFailAsync(comandaId, code, $"Monto recibido insuficiente. Total: {total:N0}");

            var productIds = billable.Select(i => i.ProductId).Distinct().ToList();
            var known = await _db.Products.Where(p => productIds.Contains(p.Id) && p.IsActive).Select(p => p.Id).ToListAsync(ct);
            var missing = billable.Where(i => !known.Contains(i.ProductId)).Select(i => i.ProductName).Distinct().ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Catálogo desactualizado: {string.Join(", ", missing)} no existe(n) en esta caja. Sincronice el catálogo e intente de nuevo.");

            // (b) Local invoice on the satellite shift. Lines are added one by one (ISalesService merges same-product lines).
            var order = await _sales.CreateOrderAsync(null, cashShiftId, ct);
            draftOrderId = order.Id;
            foreach (var i in billable)
                await _sales.AddItemAsync(order.Id, i.ProductId, i.Quantity, i.WeightKg, ct);

            var invoice = await _sales.GetByIdAsync(order.Id, ct)
                ?? throw new InvalidOperationException("La factura local no se pudo leer después de crearla.");
            var tendered = method == PaymentMethod.Cash ? tenderedAmount : invoice.Total;
            var result = await _sales.ProcessPaymentAsync(order.Id, method, invoice.Total, tendered, reference, terminalPaymentId, ct);
            if (!result.Success)
            {
                await TryDeleteDraftAsync(order.Id);
                return await UnlockAndFailAsync(comandaId, code, result.ErrorMessage ?? "No se pudo registrar el pago.");
            }
            paid = true;

            // (c) Outbox: the master learns about the close even if the direct call below fails.
            var request = new ComandaRemoteCloseRequest(comandaId, code, order.Id, invoice.OrderNumber, method, invoice.Total,
                string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), UtcNow);
            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = SyncEventTypes.ComandaClosedRemotely,
                AggregateType = "Comanda",
                AggregateId = comandaId,
                PayloadJson = OutboxPayloads.ComandaClosedRemotely(request),
                Status = SyncStatus.Pending,
                UpdatedAt = UtcNow
            });
            await _db.SaveChangesAsync(ct);

            // (d) Best effort: tell the master right away so the table frees instantly on every register.
            try
            {
                await CloseRemotelyAsync(request, ct);
            }
            catch (HttpRequestException) { /* offline: the Outbox dispatcher delivers it */ }
            catch (TimeoutException) { /* same */ }
            catch (InvalidOperationException) { /* already closed / lock lost: the inbox will judge the Outbox copy */ }

            return result;
        }
        catch when (!paid)
        {
            // Nothing was invoiced here (a draft order at most): drop the draft and give the table back.
            if (draftOrderId is { } draft) await TryDeleteDraftAsync(draft);
            await TryUnlockAsync(comandaId, code);
            throw;
        }
    }

    /// <summary>Removes a Draft order (and its items) left behind by a failed remote payment. Never throws.</summary>
    private async Task TryDeleteDraftAsync(Guid orderId)
    {
        try
        {
            var order = await _db.Orders.Include(o => o.Items).Include(o => o.Payments)
                .FirstOrDefaultAsync(o => o.Id == orderId && o.Status == OrderStatus.Draft && !o.Payments.Any());
            if (order == null) return;
            _db.OrderItems.RemoveRange(order.Items);
            _db.Orders.Remove(order);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch { /* best effort: an orphan draft is harmless, a thrown exception here would hide the real error */ }
    }

    private async Task<PaymentResultDto> UnlockAndFailAsync(Guid comandaId, string code, string message)
    {
        await TryUnlockAsync(comandaId, code);
        return PaymentResultDto.Failed(Guid.Empty, message);
    }

    /// <summary>Best effort, never throws, ignores the caller's cancellation: a lock nobody releases expires after ComandaSettings.PaymentLockMinutes.</summary>
    private async Task TryUnlockAsync(Guid comandaId, string code)
    {
        try
        {
            await UnlockPaymentAsync(comandaId, code, CancellationToken.None);
        }
        catch
        {
            // offline or already released
        }
    }
}
