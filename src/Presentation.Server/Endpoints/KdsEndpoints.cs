using Core.Application.Services;
using Core.Domain.Entities;
using Presentation.Server.Dtos;
using Presentation.Server.Realtime;

namespace Presentation.Server.Endpoints;

/// <summary>REST for the KDS tablets (/api/kds/*) and the realtime hub (/ws/kds).</summary>
public static class KdsEndpoints
{
    private static readonly string[] DefaultStations = { "Cocina Caliente", "Barra" };

    public static IEndpointRouteBuilder MapKdsEndpoints(this IEndpointRouteBuilder app, IServiceProvider appServices, KdsSocketHub hub)
    {
        var group = app.MapGroup("/api/kds");

        group.MapGet("/settings", () => appServices.WithScopedAsync<IKitchenService>(async kitchen =>
        {
            var settings = appServices.ResolveKdsSettings();
            var active = await kitchen.GetActiveAsync(null);
            var stations = DefaultStations
                .Concat(active.Select(o => o.Station)
                               .Where(s => !string.IsNullOrWhiteSpace(s))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Results.Ok(new KdsSettingsDto(settings.WarningMinutes, settings.MaxPrepMinutes, settings.ArchiveDelaySeconds, stations));
        }));

        group.MapGet("/orders", (string? station) => appServices.WithScopedAsync<IKitchenService>(async kitchen =>
        {
            var orders = await kitchen.GetActiveAsync(string.IsNullOrWhiteSpace(station) ? null : station);
            return Results.Ok(orders.Select(o => o.ToDto()).ToList());
        }));

        group.MapGet("/orders/{id:guid}", (Guid id) => appServices.WithScopedAsync<IKitchenService>(async kitchen =>
        {
            var order = await kitchen.GetByIdAsync(id);
            return order is null ? EndpointSupport.NotFound("Orden de cocina no encontrada.") : Results.Ok(order.ToDto());
        }));

        group.MapPost("/orders/{id:guid}/advance", (Guid id) => Mutate(appServices, id, (k, oid) => k.AdvanceAsync(oid)));
        group.MapPost("/orders/{id:guid}/printed", (Guid id) => Mutate(appServices, id, (k, oid) => k.MarkPrintedAsync(oid)));
        group.MapPost("/orders/{id:guid}/undo", (Guid id) => Mutate(appServices, id, (k, oid) => k.UndoDeliverAsync(oid)));

        // Realtime hub. Non-WebSocket requests get a 400 so a browser typo is obvious.
        app.MapGet("/ws/kds", async (HttpContext ctx, string? station) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket request expected.");
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.HandleAsync(socket, station, ctx.RequestAborted);
        });

        return app;
    }

    /// <summary>404 when the ticket does not exist (the service reports that as InvalidOperationException), 409 on a rule violation.</summary>
    private static Task<IResult> Mutate(IServiceProvider appServices, Guid id, Func<IKitchenService, Guid, Task<KitchenOrder>> mutation) =>
        appServices.WithScopedAsync<IKitchenService>(async kitchen =>
        {
            if (await kitchen.GetByIdAsync(id) is null)
                return EndpointSupport.NotFound("Orden de cocina no encontrada.");
            var updated = await mutation(kitchen, id);
            return Results.Ok(updated.ToDto());
        });
}
