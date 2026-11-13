using System.Net;
using Core.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>/api/catalog/* for the comandera tablet: active rows only, category name joined.</summary>
public class CatalogEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    [Fact]
    public async Task Products_ReturnsOnlyActive_SortedByName_WithCategoryName()
    {
        // Seed adds only active rows; add an inactive product and an inactive category to prove the filter.
        await _fx.WithDbAsync(async db =>
        {
            var hidden = new Category { Name = "Oculta", IsActive = false, SortOrder = 99 };
            db.Categories.Add(hidden);
            db.Products.Add(new Product { Sku = "ZZ-OFF", Name = "Producto Descontinuado", Price = 1000m, IsActive = false, CategoryId = hidden.Id });
            await db.SaveChangesAsync();
        });
        var activeCount = await _fx.WithDbAsync(db => db.Products.CountAsync(p => p.IsActive));

        var response = await _fx.Client.GetAsync("/api/catalog/products");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var products = (await _fx.ReadJsonAsync(response)).AsArray();
        products.Should().HaveCount(activeCount).And.NotBeEmpty();
        products.Select(p => p!["name"]!.GetValue<string>()).Should().NotContain("Producto Descontinuado");
        products.Select(p => p!["name"]!.GetValue<string>()).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);

        var burger = products.Single(p => p!["name"]!.GetValue<string>() == "Hamburguesa Especial")!;
        burger["id"]!.GetValue<Guid>().Should().NotBeEmpty();
        burger["sku"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        burger["price"]!.GetValue<decimal>().Should().BeGreaterThan(0);
        burger["isWeighed"]!.GetValue<bool>().Should().BeFalse();
        burger["categoryId"]!.GetValue<Guid>().Should().NotBeEmpty();
        burger["categoryName"]!.GetValue<string>().Should().Be("Comidas");

        var apples = products.Single(p => p!["name"]!.GetValue<string>() == "Manzana Roja (Kg)")!;
        apples["isWeighed"]!.GetValue<bool>().Should().BeTrue();
        apples["categoryName"]!.GetValue<string>().Should().Be("Frutas / Balanza");

        // Wire shape: camelCase keys only.
        burger.AsObject().Select(kv => kv.Key).Should().OnlyContain(k => char.IsLower(k[0]));
    }

    [Fact]
    public async Task Categories_ReturnsOnlyActive_InSortOrder()
    {
        await _fx.WithDbAsync(async db =>
        {
            db.Categories.Add(new Category { Name = "Inactiva", IsActive = false, SortOrder = 0 });
            await db.SaveChangesAsync();
        });

        var response = await _fx.Client.GetAsync("/api/catalog/categories");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var categories = (await _fx.ReadJsonAsync(response)).AsArray();
        var names = categories.Select(c => c!["name"]!.GetValue<string>()).ToList();
        names.Should().Equal("Comidas", "Bebidas", "Panadería", "Frutas / Balanza");
        categories.Select(c => c!["sortOrder"]!.GetValue<int>()).Should().BeInAscendingOrder();

        var first = categories[0]!;
        first["id"]!.GetValue<Guid>().Should().NotBeEmpty();
        first["colorHex"]!.GetValue<string>().Should().StartWith("#");
    }
}
