using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// GET /api/catalog/snapshot (master → satellite): EVERY category, tax, product, biller and user, active and
/// inactive, with the master's ids and the users' PinHash, camelCase on the wire.
/// </summary>
public class CatalogSnapshotEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static IEnumerable<string> Keys(JsonNode? node) => node!.AsObject().Select(kv => kv.Key);

    [Fact]
    public async Task Snapshot_ContainsEverySeededRow_IncludingInactiveOnes_WithMasterIds()
    {
        Guid hiddenCategory = Guid.Empty, hiddenProduct = Guid.Empty, hiddenBiller = Guid.Empty, hiddenUser = Guid.Empty, hiddenTax = Guid.Empty;
        await _fx.WithDbAsync(async db =>
        {
            var cat = new Category { Name = "Oculta", IsActive = false, SortOrder = 99 };
            var tax = new Tax { Name = "Exento viejo", Rate = 0m, IsActive = false };
            var product = new Product { Sku = "ZZ-OFF", Name = "Producto Descontinuado", Price = 1000m, Cost = 500m, IsActive = false, Category = cat, Tax = tax };
            var biller = new Biller { Name = "Convenio cerrado", Code = "CERRADO", IsActive = false, SortOrder = 50 };
            var user = new User { Name = "Ex empleado", PinHash = "hash-ex", Role = UserRole.Waiter, IsActive = false };
            db.AddRange(cat, tax, product, biller, user);
            await db.SaveChangesAsync();
            (hiddenCategory, hiddenProduct, hiddenBiller, hiddenUser, hiddenTax) = (cat.Id, product.Id, biller.Id, user.Id, tax.Id);
        });

        var counts = await _fx.WithDbAsync(async db => (
            Categories: await db.Categories.CountAsync(), Taxes: await db.Taxes.CountAsync(), Products: await db.Products.CountAsync(),
            Billers: await db.Billers.CountAsync(), Users: await db.Users.CountAsync()));

        var response = await _fx.Client.GetAsync("/api/catalog/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);

        body["masterCode"]!.GetValue<string>().Should().Be("CAJA-01");
        DateTime.Parse(body["generatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            .Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);

        var categories = body["categories"]!.AsArray();
        var taxes = body["taxes"]!.AsArray();
        var products = body["products"]!.AsArray();
        var billers = body["billers"]!.AsArray();
        var users = body["users"]!.AsArray();

        categories.Should().HaveCount(counts.Categories).And.HaveCountGreaterThan(1);
        taxes.Should().HaveCount(counts.Taxes).And.HaveCountGreaterThan(1);
        products.Should().HaveCount(counts.Products).And.HaveCountGreaterThan(1);
        billers.Should().HaveCount(counts.Billers).And.HaveCountGreaterThan(1);
        users.Should().HaveCount(counts.Users).And.HaveCountGreaterThan(1);

        // Inactive rows travel too (so a deactivation on the master propagates), flagged.
        categories.Single(c => c!["id"]!.GetValue<Guid>() == hiddenCategory)!["isActive"]!.GetValue<bool>().Should().BeFalse();
        taxes.Single(t => t!["id"]!.GetValue<Guid>() == hiddenTax)!["isActive"]!.GetValue<bool>().Should().BeFalse();
        var off = products.Single(p => p!["id"]!.GetValue<Guid>() == hiddenProduct)!;
        off["isActive"]!.GetValue<bool>().Should().BeFalse();
        off["categoryId"]!.GetValue<Guid>().Should().Be(hiddenCategory);
        off["taxId"]!.GetValue<Guid>().Should().Be(hiddenTax);
        billers.Single(b => b!["id"]!.GetValue<Guid>() == hiddenBiller)!["isActive"]!.GetValue<bool>().Should().BeFalse();
        users.Single(u => u!["id"]!.GetValue<Guid>() == hiddenUser)!["isActive"]!.GetValue<bool>().Should().BeFalse();

        // Ids are the master's (what the satellite must preserve).
        var masterProductIds = await _fx.WithDbAsync(db => db.Products.Select(p => p.Id).ToListAsync());
        products.Select(p => p!["id"]!.GetValue<Guid>()).Should().BeEquivalentTo(masterProductIds);
        products.Select(p => p!["id"]!.GetValue<Guid>()).Should().OnlyContain(id => id != Guid.Empty);
    }

    [Fact]
    public async Task Snapshot_Products_CarryPriceCostTaxAndCategory_AsCamelCase()
    {
        var body = await _fx.GetJsonAsync("/api/catalog/snapshot");
        var products = body["products"]!.AsArray();

        var burger = products.Single(p => p!["sku"]!.GetValue<string>() == "BUR-001")!;
        burger["name"]!.GetValue<string>().Should().Be("Hamburguesa Especial");
        burger["price"]!.GetValue<decimal>().Should().Be(22000m);
        burger["cost"]!.GetValue<decimal>().Should().Be(11000m);
        burger["isWeighed"]!.GetValue<bool>().Should().BeFalse();
        burger["isActive"]!.GetValue<bool>().Should().BeTrue();
        burger["barcode"]!.GetValue<string>().Should().Be("7701234567890");
        burger["categoryId"]!.GetValue<Guid>().Should().NotBeEmpty();
        burger["taxId"]!.GetValue<Guid>().Should().NotBeEmpty();
        Keys(burger).Should().BeEquivalentTo("id", "sku", "barcode", "name", "description", "price", "cost", "isWeighed", "isActive", "categoryId", "taxId");

        var apples = products.Single(p => p!["sku"]!.GetValue<string>() == "FRU-001")!;
        apples["isWeighed"]!.GetValue<bool>().Should().BeTrue();

        Keys(body).Should().BeEquivalentTo("generatedAt", "masterCode", "categories", "taxes", "products", "billers", "users");
        foreach (var section in new[] { "categories", "taxes", "products", "billers", "users" })
            foreach (var row in body[section]!.AsArray())
                Keys(row).Should().OnlyContain(k => char.IsLower(k[0]), $"{section} must be camelCase");
    }

    [Fact]
    public async Task Snapshot_Users_CarryPinHashAndRole_SoTheSamePinsWorkOnEveryRegister()
    {
        var expected = await _fx.WithDbAsync(db => db.Users.AsNoTracking().ToListAsync());

        var users = (await _fx.GetJsonAsync("/api/catalog/snapshot"))["users"]!.AsArray();

        users.Should().HaveCount(expected.Count);
        foreach (var u in expected)
        {
            var wire = users.Single(x => x!["id"]!.GetValue<Guid>() == u.Id)!;
            wire["name"]!.GetValue<string>().Should().Be(u.Name);
            wire["pinHash"]!.GetValue<string>().Should().Be(u.PinHash).And.NotBeNullOrWhiteSpace();
            wire["role"]!.GetValue<string>().Should().Be(u.Role.ToString(), "enums travel as names");
            wire["isActive"]!.GetValue<bool>().Should().Be(u.IsActive);
        }
        Keys(users[0]).Should().BeEquivalentTo("id", "name", "pinHash", "role", "isActive");
    }

    [Fact]
    public async Task Snapshot_Billers_CarryCommissions()
    {
        var billers = (await _fx.GetJsonAsync("/api/catalog/snapshot"))["billers"]!.AsArray();

        var energy = billers.Single(b => b!["code"]!.GetValue<string>() == "ENERGIA")!;
        energy["name"]!.GetValue<string>().Should().Be("Energía");
        energy["commissionFixed"]!.GetValue<decimal>().Should().Be(1500m);
        energy["commissionPercent"]!.GetValue<decimal>().Should().Be(0m);
        energy["isActive"]!.GetValue<bool>().Should().BeTrue();
        var recargas = billers.Single(b => b!["code"]!.GetValue<string>() == "RECARGAS")!;
        recargas["commissionPercent"]!.GetValue<decimal>().Should().Be(0.03m);
        Keys(energy).Should().BeEquivalentTo("id", "name", "code", "commissionFixed", "commissionPercent", "colorHex", "sortOrder", "isActive");
    }
}
