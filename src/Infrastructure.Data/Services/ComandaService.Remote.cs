using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;

namespace Infrastructure.Data.Services;

/// <summary>
/// Shared comandas between registers (master side). The master owns every comanda; a satellite reserves the table
/// (<see cref="LockForPaymentAsync"/>), invoices on ITS OWN database and then reports the close
/// (<see cref="CloseRemotelyAsync"/>, directly over HTTP or through the Outbox → SyncInboxService). Nothing here
/// writes an Order: the satellite's OrderCompleted arrives through the sync inbox.
/// </summary>
public partial class ComandaService
{
    public async Task<ComandaDto> LockForPaymentAsync(Guid comandaId, string terminalCode, CancellationToken ct = default)
    {
        var code = NormalizeTerminalCode(terminalCode);
        var comanda = await RequireAsync(comandaId, ct);

        // Validate everything before touching tracked entities.
        if (comanda.Status is ComandaStatus.Closed or ComandaStatus.Cancelled)
            throw new InvalidOperationException("La comanda ya está cerrada.");
        EnsureNotLockedByOther(comanda, code);
        if (!comanda.Items.Any(i => i.Status != ComandaItemStatus.Cancelled))
            throw new InvalidOperationException("La comanda no tiene productos.");

        // Open → ReadyToPay exactly as MarkReadyToPayAsync: whatever is still waiting goes to the kitchen now.
        if (comanda.Status == ComandaStatus.Open)
        {
            ConfirmItems(comanda, comanda.Items.Where(i => i.Status == ComandaItemStatus.Pending).ToList());
            comanda.Status = ComandaStatus.ReadyToPay;
        }
        comanda.PaymentLockedByTerminalCode = code;
        comanda.PaymentLockedAt = UtcNow; // same holder again just refreshes the lock
        comanda.UpdatedAt = UtcNow;

        await SaveAndPublishAsync(ct, comanda);
        return MapToDto(comanda);
    }

    public async Task<ComandaDto> UnlockPaymentAsync(Guid comandaId, string terminalCode, CancellationToken ct = default)
    {
        var code = NormalizeTerminalCode(terminalCode);
        var comanda = await RequireAsync(comandaId, ct);

        // Only the holder releases; anyone else is a no-op (their lock, if any, expires on its own).
        if (!SameCode(comanda.PaymentLockedByTerminalCode, code))
            return MapToDto(comanda);

        ClearPaymentLock(comanda);
        comanda.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        PublishComandaChanged(comanda);
        return MapToDto(comanda);
    }

    public async Task<ComandaDto> CloseRemotelyAsync(ComandaRemoteCloseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = NormalizeTerminalCode(request.TerminalCode);
        if (request.OrderId == Guid.Empty)
            throw new ArgumentException("El id de la factura remota es obligatorio.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OrderNumber))
            throw new ArgumentException("El número de la factura remota es obligatorio.", nameof(request));

        var comanda = await RequireAsync(request.ComandaId, ct);

        // Idempotent: the satellite may retry (HTTP timeout, Outbox redelivery) after the close already landed.
        if (comanda.Status == ComandaStatus.Closed)
        {
            if (SameCode(comanda.PaidByTerminalCode, code))
                return MapToDto(comanda);
            throw new InvalidOperationException(
                $"La comanda ya fue cerrada por {comanda.PaidByTerminalCode ?? "esta caja"}.");
        }
        if (comanda.Status == ComandaStatus.Cancelled)
            throw new InvalidOperationException("La comanda está anulada.");

        // The lock must be ours or absent (a stale lock from a register that gave up counts as absent).
        EnsureNotLockedByOther(comanda, code);

        var billable = comanda.Items.Where(i => i.Status != ComandaItemStatus.Cancelled).ToList();
        if (billable.Count == 0)
            throw new InvalidOperationException("La comanda no tiene productos.");

        // --- mutate ---
        // Kitchen must receive everything that was billed (same rule as CloseAndPayAsync).
        ConfirmItems(comanda, billable.Where(i => i.Status == ComandaItemStatus.Pending).ToList());

        var closedAt = ToUtc(request.ClosedAt) ?? UtcNow;
        comanda.Status = ComandaStatus.Closed;
        comanda.ClosedAt = closedAt;
        comanda.PaidByTerminalCode = code;
        comanda.RemoteOrderNumber = Truncate(request.OrderNumber.Trim(), 50);
        comanda.RemoteOrderId = request.OrderId;
        ClearPaymentLock(comanda);
        comanda.UpdatedAt = UtcNow;

        await SaveAndPublishAsync(ct, comanda);
        return MapToDto(comanda);
    }

    private static DateTime? ToUtc(DateTime d) => d == default ? null : d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc)
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
