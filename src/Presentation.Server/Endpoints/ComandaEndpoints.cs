using Core.Application.DTOs;
using Core.Application.Services;
using Presentation.Server.Dtos;

namespace Presentation.Server.Endpoints;

/// <summary>
/// Minimal comandera (waiter tablet) API: open a comanda, add items, send pending items to the kitchen.
/// Returns <c>ComandaDto</c> as produced by the application layer.
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

        group.MapPost("/{id:guid}/send", (Guid id) => Mutate(appServices, id, c => c.ConfirmPendingNowAsync(id)));

        group.MapPost("/{id:guid}/ready", (Guid id) => Mutate(appServices, id, c => c.MarkReadyToPayAsync(id)));

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
}
