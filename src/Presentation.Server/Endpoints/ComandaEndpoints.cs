using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Presentation.Server.Dtos;

namespace Presentation.Server.Endpoints;

/// <summary>
/// Comanda API for the comandera (waiter tablet) and for the satellite registers' <c>RemoteComandaService</c>:
/// open a comanda, add items, send pending items to the kitchen, mark ready / reopen, and — register-only, behind the
/// terminal API key — strike a confirmed item, lock/unlock the table for payment and close a comanda invoiced elsewhere.
/// Returns <c>ComandaDto</c> as produced by the application layer. Status codes: 400 bad body, 404 unknown comanda,
/// 409 business rule (InvalidOperationException message), 401 missing/wrong Bearer on the register-only routes.
/// </summary>
public static class ComandaEndpoints
{
    public static IEndpointRouteBuilder MapComandaEndpoints(this IEndpointRouteBuilder app, IServiceProvider appServices)
    {
        app.MapGet("/api/tables", () => appServices.WithScopedAsync<IComandaService>(async comandas =>
        {
            var tables = await comandas.GetTablesAsync();
            return Results.Ok(tables.Select(t => t.ToDto()).ToList());
        }));

        var group = app.MapGroup("/api/comandas");

        // Static segment: no clash with "/{id:guid}" thanks to the guid constraint.
        group.MapGet("/settings", () =>
        {
            var s = appServices.ResolveComandaSettings();
            return Results.Ok(new ComandaSettingsDto(s.ConfirmationDelaySeconds, s.QuickNotes ?? new List<string>(), ComandaSettings.MaxNotesLength));
        });

        group.MapGet("", () => appServices.WithScopedAsync<IComandaService>(async comandas =>
            Results.Ok(await comandas.GetActiveComandasAsync())));

        group.MapGet("/{id:guid}", (Guid id) => appServices.WithScopedAsync<IComandaService>(async comandas =>
        {
            var dto = await comandas.GetByIdAsync(id);
            return dto is null ? EndpointSupport.NotFound("Comanda no encontrada.") : Results.Ok(dto);
        }));

        group.MapPost("", (OpenComandaRequest? body) => appServices.WithScopedAsync<IComandaService>(async comandas =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.TableName))
                return EndpointSupport.Error(StatusCodes.Status400BadRequest, "La mesa es obligatoria.");
            var waiter = string.IsNullOrWhiteSpace(body.WaiterName) ? "Mesero" : body.WaiterName.Trim();
            var dto = await comandas.OpenComandaAsync(body.TableName.Trim(), waiter, body.DiningTableId);
            return Results.Created($"/api/comandas/{dto.Id}", dto);
        }));

        group.MapPost("/{id:guid}/items", (Guid id, AddComandaItemRequest? body) => appServices.WithScopedAsync<IComandaService>(async comandas =>
        {
            if (body is null || body.ProductId == Guid.Empty)
                return EndpointSupport.Error(StatusCodes.Status400BadRequest, "El producto es obligatorio.");
            if (await comandas.GetByIdAsync(id) is null)
                return EndpointSupport.NotFound("Comanda no encontrada.");
            var dto = await comandas.AddItemAsync(id, body.ProductId, body.Quantity ?? 1m, body.WeightKg, body.Notes);
            return Results.Ok(dto);
        }));

        group.MapPost("/{id:guid}/items/{itemId:guid}/cancel", (Guid id, Guid itemId) =>
            Mutate(appServices, id, c => c.CancelPendingItemAsync(id, itemId)));

        group.MapPost("/{id:guid}/items/{itemId:guid}/notes", (Guid id, Guid itemId, SetItemNotesRequest? body) =>
            Mutate(appServices, id, c => c.SetItemNotesAsync(id, itemId, body?.Notes)));

        // POST /{id}/items/{itemId}/hold { onHold: bool } → ComandaDto.
        group.MapPost("/{id:guid}/items/{itemId:guid}/hold", (Guid id, Guid itemId, SetItemHoldRequest? body) =>
            body is null
                ? Task.FromResult(EndpointSupport.Error(StatusCodes.Status400BadRequest, "El cuerpo { onHold } es obligatorio."))
                : Mutate(appServices, id, c => c.SetItemHoldAsync(id, itemId, body.OnHold)));

        group.MapPost("/{id:guid}/send", (Guid id) => Mutate(appServices, id, c => c.ConfirmPendingNowAsync(id)));

        group.MapPost("/{id:guid}/ready", (Guid id) => Mutate(appServices, id, c => c.MarkReadyToPayAsync(id)));

        // POST /{id}/reopen (no body) → ComandaDto (ReadyToPay → Open).
        group.MapPost("/{id:guid}/reopen", (Guid id) => Mutate(appServices, id, c => c.ReopenAsync(id)));

        // ── Register-only routes (satellite → master), Bearer TerminalSettings.ApiKey when configured ──────────────

        // POST /{id}/items/{itemId}/cancel-confirmed { managerName, terminalCode, reason? } → ComandaDto.
        // The manager approved on the satellite; the master writes its own single-use ManagerOverride and consumes it
        // in CancelConfirmedItemAsync (same DbContext scope).
        group.MapPost("/{id:guid}/items/{itemId:guid}/cancel-confirmed", (Guid id, Guid itemId, CancelConfirmedItemRequest? body) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ManagerName) || string.IsNullOrWhiteSpace(body.TerminalCode))
                return Task.FromResult(EndpointSupport.Error(StatusCodes.Status400BadRequest, "managerName y terminalCode son obligatorios."));

            var terminalCode = body.TerminalCode.Trim();
            return appServices.WithScopeAsync(async scope =>
            {
                var comandas = scope.GetRequiredService<IComandaService>();
                if (await comandas.GetByIdAsync(id) is null)
                    return EndpointSupport.NotFound("Comanda no encontrada.");

                var db = scope.GetRequiredService<PosDbContext>();
                var now = appServices.ResolveClock().GetUtcNow().UtcDateTime;
                var approval = new ManagerOverride
                {
                    Action = AuthorizedAction.CancelKitchenItem,
                    ManagerName = Truncate(body.ManagerName.Trim(), 100),
                    ManagerUserId = Guid.Empty,                       // authorized on another register: no local user row
                    Description = Truncate($"Anulación desde {terminalCode}", 300),
                    RequestedByName = Truncate(terminalCode, 100),
                    Reference = string.IsNullOrWhiteSpace(body.Reason) ? null : Truncate(body.Reason.Trim(), 200),
                    AuthorizedAt = now,
                    CreatedAt = now
                };
                db.ManagerOverrides.Add(approval);
                await db.SaveChangesAsync();

                try
                {
                    return Results.Ok(await comandas.CancelConfirmedItemAsync(id, itemId, approval.Id));
                }
                catch
                {
                    // Refused (item still pending, already cancelled, ...): do not leave a usable single-use approval behind.
                    if (approval.ConsumedAt == null)
                    {
                        db.ManagerOverrides.Remove(approval);
                        await db.SaveChangesAsync();
                    }
                    throw;
                }
            });
        }).RequireTerminalApiKey(appServices);

        // POST /{id}/payment-lock { terminalCode } → ComandaDto (409 when another register holds a fresh lock).
        group.MapPost("/{id:guid}/payment-lock", (Guid id, PaymentLockRequest? body) =>
            RequireTerminalCode(body, code => Mutate(appServices, id, c => c.LockForPaymentAsync(id, code))))
            .RequireTerminalApiKey(appServices);

        // POST /{id}/payment-unlock { terminalCode } → ComandaDto (no-op when not the holder).
        group.MapPost("/{id:guid}/payment-unlock", (Guid id, PaymentLockRequest? body) =>
            RequireTerminalCode(body, code => Mutate(appServices, id, c => c.UnlockPaymentAsync(id, code))))
            .RequireTerminalApiKey(appServices);

        // POST /{id}/close-remote (body: ComandaRemoteCloseRequest; comandaId from the route wins) → ComandaDto.
        group.MapPost("/{id:guid}/close-remote", (Guid id, ComandaRemoteCloseRequest? body) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.TerminalCode))
                return Task.FromResult(EndpointSupport.Error(StatusCodes.Status400BadRequest, "terminalCode es obligatorio."));
            if (body.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(body.OrderNumber))
                return Task.FromResult(EndpointSupport.Error(StatusCodes.Status400BadRequest, "orderId y orderNumber son obligatorios."));

            var request = body with { ComandaId = id, TerminalCode = body.TerminalCode.Trim() };
            return Mutate(appServices, id, c => c.CloseRemotelyAsync(request));
        }).RequireTerminalApiKey(appServices);

        return app;
    }

    /// <summary>404 when the comanda does not exist (the service reports that as InvalidOperationException), otherwise the mapped service error.</summary>
    private static Task<IResult> Mutate(IServiceProvider appServices, Guid id, Func<IComandaService, Task<ComandaDto>> mutation) =>
        appServices.WithScopedAsync<IComandaService>(async comandas =>
        {
            if (await comandas.GetByIdAsync(id) is null)
                return EndpointSupport.NotFound("Comanda no encontrada.");
            return Results.Ok(await mutation(comandas));
        });

    private static Task<IResult> RequireTerminalCode(PaymentLockRequest? body, Func<string, Task<IResult>> run) =>
        body is null || string.IsNullOrWhiteSpace(body.TerminalCode)
            ? Task.FromResult(EndpointSupport.Error(StatusCodes.Status400BadRequest, "terminalCode es obligatorio."))
            : run(body.TerminalCode.Trim());

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
