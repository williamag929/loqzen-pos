using Core.Application.Services;
using Presentation.Server.Dtos;

namespace Presentation.Server.Endpoints;

/// <summary>
/// Read-only catalog for the comandera tablet (/api/catalog/products, /categories: active rows only) and the
/// full replication snapshot for satellite registers (/api/catalog/snapshot: active AND inactive, behind the API key).
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app, IServiceProvider appServices)
    {
        var group = app.MapGroup("/api/catalog");

        group.MapGet("/products", () => appServices.WithScopedAsync<IProductService>(async products =>
        {
            // Category names are joined here so the result is complete even if the product query does not Include() it.
            using var scope = appServices.CreateScope();
            var categories = (await scope.ServiceProvider.GetRequiredService<ICategoryService>().GetAllAsync())
                .ToDictionary(c => c.Id, c => c.Name);

            var all = await products.GetAllAsync();
            var list = all
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.ToDto(p.CategoryId is { } cid && categories.TryGetValue(cid, out var name) ? name : null))
                .ToList();
            return Results.Ok(list);
        }));

        group.MapGet("/categories", () => appServices.WithScopedAsync<ICategoryService>(async categories =>
        {
            var all = await categories.GetAllAsync();
            var list = all
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.ToDto())
                .ToList();
            return Results.Ok(list);
        }));

        // GET /api/catalog/snapshot → CatalogSnapshotDto (master → satellite; ids preserved, includes PinHash — LAN only).
        group.MapGet("/snapshot", () => appServices.WithScopedAsync<ICatalogSyncService>(async catalog =>
            Results.Ok(await catalog.BuildSnapshotAsync())))
            .RequireTerminalApiKey(appServices);

        return app;
    }
}
