using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Sync;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Sync;

/// <summary>
/// Catalog replication master → satellite on two REAL SQLite databases: snapshot contents, upsert by preserved id,
/// deactivation of rows the master no longer lists, local-only fields kept, counts and idempotency.
/// </summary>
public class CatalogSyncServiceTests : IDisposable
{
    private readonly SqliteDb _masterDb = SqliteDb.Create();
    private readonly SqliteDb _satDb = SqliteDb.Create();
    private readonly TestClock _clock = new();
    private readonly Core.Application.Services.TerminalContext _master = Terminals.Master("CAJA-01");
    private readonly Core.Application.Services.TerminalContext _sat = Terminals.Satellite("CAJA-02");

    public void Dispose()
    {
        _masterDb.Dispose();
        _satDb.Dispose();
    }

    private CatalogSyncService MasterSvc(PosDbContext db) => new(db, _clock, _master);
    private CatalogSyncService SatSvc(PosDbContext db) => new(db, _clock, _sat);

    /// <summary>Master catalog: 2 categories (1 inactive), 2 taxes (1 inactive), 3 products (1 inactive), 2 billers (1 inactive), 2 users (1 inactive).</summary>
    private async Task SeedMasterAsync()
    {
        await using var db = _masterDb.NewContext();
        var food = new Category { Name = "Comidas", SortOrder = 1 };
        var old = new Category { Name = "Descontinuados", SortOrder = 2, IsActive = false };
        var iva = new Tax { Name = "IVA 19%", Rate = 0.19m };
        var oldTax = new Tax { Name = "IVA 16%", Rate = 0.16m, IsActive = false };
        var burger = new Product { Sku = "BUR-001", Barcode = "770", Name = "Hamburguesa", Price = 22000m, Cost = 11000m, StockQuantity = 100, Category = food, Tax = iva };
        var weighed = new Product { Sku = "FRU-001", Name = "Manzana", Price = 8000m, IsWeighed = true, Category = food };
        var retired = new Product { Sku = "OLD-001", Name = "Retirado", Price = 1m, IsActive = false, Category = old, Tax = oldTax };
        var energia = new Biller { Name = "Energía", Code = "ENERGIA", CommissionFixed = 1500m, SortOrder = 1 };
        var cerrado = new Biller { Name = "Convenio cerrado", CommissionPercent = 0.01m, SortOrder = 2, IsActive = false };
        var admin = new User { Name = "Administrador", PinHash = "hash-admin", Role = UserRole.Admin };
        var exEmployee = new User { Name = "Ex empleado", PinHash = "hash-ex", Role = UserRole.Cashier, IsActive = false };
        db.AddRange(food, old, iva, oldTax, burger, weighed, retired, energia, cerrado, admin, exEmployee);
        await db.SaveChangesAsync();
    }

    private async Task<CatalogSnapshotDto> MasterSnapshotAsync()
    {
        await using var db = _masterDb.NewContext();
        return await MasterSvc(db).BuildSnapshotAsync();
    }

    [Fact]
    public async Task BuildSnapshot_IncludesInactiveRows_PreservesIdsAndFks_StampsMasterCode()
    {
        await SeedMasterAsync();

        var snap = await MasterSnapshotAsync();

        snap.MasterCode.Should().Be("CAJA-01");
        snap.GeneratedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        snap.Categories.Should().HaveCount(2).And.Contain(c => !c.IsActive && c.Name == "Descontinuados");
        snap.Taxes.Should().HaveCount(2).And.Contain(t => !t.IsActive && t.Name == "IVA 16%");
        snap.Products.Should().HaveCount(3).And.Contain(p => !p.IsActive && p.Sku == "OLD-001");
        snap.Billers.Should().HaveCount(2).And.Contain(b => !b.IsActive);
        snap.Users.Should().HaveCount(2).And.Contain(u => !u.IsActive && u.Name == "Ex empleado");

        await using var db = _masterDb.NewContext();
        var burger = await db.Products.SingleAsync(p => p.Sku == "BUR-001");
        var s = snap.Products.Single(p => p.Sku == "BUR-001");
        s.Id.Should().Be(burger.Id);
        s.CategoryId.Should().Be(burger.CategoryId);
        s.TaxId.Should().Be(burger.TaxId);
        s.Price.Should().Be(22000m);
        s.Cost.Should().Be(11000m);
        s.Barcode.Should().Be("770");
        snap.Users.Single(u => u.Name == "Administrador").PinHash.Should().Be("hash-admin", "PINs must work on every register");
        snap.Billers.Single(b => b.Name == "Energía").CommissionFixed.Should().Be(1500m);
    }

    [Fact]
    public async Task BuildSnapshot_WithoutContext_HasEmptyMasterCode()
    {
        await using var db = _masterDb.NewContext();
        var snap = await new CatalogSyncService(db, _clock).BuildSnapshotAsync();
        snap.MasterCode.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplySnapshot_OnMaster_Throws_AndChangesNothing()
    {
        await SeedMasterAsync();
        var snap = await MasterSnapshotAsync();

        await using var db = _masterDb.NewContext();
        var act = () => MasterSvc(db).ApplySnapshotAsync(snap);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ApplySnapshot_OnEmptySatellite_InsertsEverything_ByPreservedId()
    {
        await SeedMasterAsync();
        var snap = await MasterSnapshotAsync();

        CatalogApplyResult result;
        await using (var db = _satDb.NewContext())
            result = await SatSvc(db).ApplySnapshotAsync(snap);

        result.Inserted.Should().Be(2 + 2 + 3 + 2 + 2);
        result.Updated.Should().Be(0);
        result.Deactivated.Should().Be(0);
        result.Total.Should().Be(11);

        await using var check = _satDb.NewContext();
        (await check.Categories.Select(c => c.Id).ToListAsync()).Should().BeEquivalentTo(snap.Categories.Select(c => c.Id));
        (await check.Taxes.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(snap.Taxes.Select(t => t.Id));
        (await check.Products.Select(p => p.Id).ToListAsync()).Should().BeEquivalentTo(snap.Products.Select(p => p.Id));
        (await check.Billers.Select(b => b.Id).ToListAsync()).Should().BeEquivalentTo(snap.Billers.Select(b => b.Id));
        (await check.Users.Select(u => u.Id).ToListAsync()).Should().BeEquivalentTo(snap.Users.Select(u => u.Id));

        var burger = await check.Products.Include(p => p.Category).Include(p => p.Tax).SingleAsync(p => p.Sku == "BUR-001");
        burger.Category!.Name.Should().Be("Comidas");
        burger.Tax!.Rate.Should().Be(0.19m);
        burger.StockQuantity.Should().Be(0m, "stock is not replicated");
        burger.CreatedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        (await check.Products.SingleAsync(p => p.Sku == "OLD-001")).IsActive.Should().BeFalse("inactive rows replicate as inactive");
        (await check.Users.SingleAsync(u => u.Name == "Administrador")).PinHash.Should().Be("hash-admin");
        (await check.Users.SingleAsync(u => u.Name == "Ex empleado")).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySnapshot_WithoutContext_ActsAsSatellite()
    {
        await SeedMasterAsync();
        var snap = await MasterSnapshotAsync();
        await using var db = _satDb.NewContext();
        var r = await new CatalogSyncService(db, _clock).ApplySnapshotAsync(snap);
        r.Inserted.Should().Be(11);
    }

    [Fact]
    public async Task ApplySnapshot_UpdatesById_DeactivatesMissing_NeverDeletes_KeepsLocalFields()
    {
        await SeedMasterAsync();
        var snap = await MasterSnapshotAsync();
        var burgerId = snap.Products.Single(p => p.Sku == "BUR-001").Id;
        var adminId = snap.Users.Single(u => u.Name == "Administrador").Id;
        var lastLogin = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        Guid localOnlyProductId, localOnlyUserId, localOnlyCategoryId, localOnlyBillerId, localOnlyTaxId;

        // Satellite already has the burger (old price, local stock) and the admin (local LastLoginAt), plus rows the master never had.
        await using (var db = _satDb.NewContext())
        {
            var localCat = new Category { Name = "Solo local" };
            var localTax = new Tax { Name = "Local tax", Rate = 0.05m };
            var localBiller = new Biller { Name = "Convenio local" };
            var localProduct = new Product { Sku = "LOC-001", Name = "Producto local", Price = 1m, Category = localCat };
            var localUser = new User { Name = "Usuario local", PinHash = "x", Role = UserRole.Waiter };
            db.AddRange(
                new Product { Id = burgerId, Sku = "BUR-001", Name = "Hamburguesa vieja", Price = 1000m, StockQuantity = 7m },
                new User { Id = adminId, Name = "Admin viejo", PinHash = "old", Role = UserRole.Cashier, LastLoginAt = lastLogin },
                localCat, localTax, localBiller, localProduct, localUser);
            await db.SaveChangesAsync();
            localOnlyProductId = localProduct.Id; localOnlyUserId = localUser.Id; localOnlyCategoryId = localCat.Id;
            localOnlyBillerId = localBiller.Id; localOnlyTaxId = localTax.Id;
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        CatalogApplyResult result;
        await using (var db = _satDb.NewContext())
            result = await SatSvc(db).ApplySnapshotAsync(snap);

        result.Inserted.Should().Be(11 - 2, "burger and admin already existed");
        result.Updated.Should().Be(2);
        result.Deactivated.Should().Be(5, "local-only category, tax, product, biller and user");

        await using var check = _satDb.NewContext();
        var burger = await check.Products.SingleAsync(p => p.Id == burgerId);
        burger.Name.Should().Be("Hamburguesa");
        burger.Price.Should().Be(22000m);
        burger.StockQuantity.Should().Be(7m, "stock is local to the register");
        burger.CategoryId.Should().Be(snap.Products.Single(p => p.Id == burgerId).CategoryId);
        burger.UpdatedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);

        var admin = await check.Users.SingleAsync(u => u.Id == adminId);
        admin.Name.Should().Be("Administrador");
        admin.PinHash.Should().Be("hash-admin");
        admin.Role.Should().Be(UserRole.Admin);
        admin.LastLoginAt.Should().Be(lastLogin, "LastLoginAt is local and untouched");

        foreach (var (set, id) in new (IQueryable<Core.Domain.Common.BaseEntity>, Guid)[]
                 {
                     (check.Products, localOnlyProductId), (check.Users, localOnlyUserId), (check.Categories, localOnlyCategoryId),
                     (check.Billers, localOnlyBillerId), (check.Taxes, localOnlyTaxId)
                 })
            (await set.AnyAsync(e => e.Id == id)).Should().BeTrue("rows absent from the snapshot are kept (sales may reference them)");
        (await check.Products.SingleAsync(p => p.Id == localOnlyProductId)).IsActive.Should().BeFalse();
        (await check.Users.SingleAsync(u => u.Id == localOnlyUserId)).IsActive.Should().BeFalse();
        (await check.Categories.SingleAsync(c => c.Id == localOnlyCategoryId)).IsActive.Should().BeFalse();
        (await check.Billers.SingleAsync(b => b.Id == localOnlyBillerId)).IsActive.Should().BeFalse();
        (await check.Taxes.SingleAsync(t => t.Id == localOnlyTaxId)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySnapshot_Twice_IsIdempotentOnData()
    {
        await SeedMasterAsync();
        var snap = await MasterSnapshotAsync();

        await using (var db = _satDb.NewContext())
            await SatSvc(db).ApplySnapshotAsync(snap);
        var before = await DumpAsync();

        _clock.Advance(TimeSpan.FromMinutes(5));
        CatalogApplyResult second;
        await using (var db = _satDb.NewContext())
            second = await SatSvc(db).ApplySnapshotAsync(snap);

        second.Inserted.Should().Be(0);
        second.Deactivated.Should().Be(0);
        second.Updated.Should().Be(11, "every row present in the snapshot is re-copied (the implementation counts unchanged rows as updated)");

        var after = await DumpAsync();
        after.Should().Equal(before, "no data changed on the second apply");
        await using var check = _satDb.NewContext();
        (await check.Products.CountAsync()).Should().Be(3);
        (await check.Users.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ApplySnapshot_ReactivatesRow_WhenMasterReactivatesIt()
    {
        await SeedMasterAsync();
        var snap = await MasterSnapshotAsync();
        await using (var db = _satDb.NewContext())
            await SatSvc(db).ApplySnapshotAsync(snap);

        // Master reactivates the retired product and renames it.
        await using (var m = _masterDb.NewContext())
        {
            var p = await m.Products.SingleAsync(x => x.Sku == "OLD-001");
            p.IsActive = true; p.Name = "Vuelve";
            await m.SaveChangesAsync();
        }
        var snap2 = await MasterSnapshotAsync();
        await using (var db = _satDb.NewContext())
            await SatSvc(db).ApplySnapshotAsync(snap2);

        await using var check = _satDb.NewContext();
        var retired = await check.Products.SingleAsync(x => x.Sku == "OLD-001");
        retired.IsActive.Should().BeTrue();
        retired.Name.Should().Be("Vuelve");
    }

    [Fact]
    public async Task ApplySnapshot_RemovedOnMaster_IsDeactivatedOnSatellite_EvenIfReferencedBySales()
    {
        await SeedMasterAsync();
        var snap = await MasterSnapshotAsync();
        var appleId = snap.Products.Single(p => p.Sku == "FRU-001").Id;
        await using (var db = _satDb.NewContext())
        {
            await SatSvc(db).ApplySnapshotAsync(snap);
            var order = new Order { OrderNumber = "ORD-1", Status = OrderStatus.Completed };
            order.Items.Add(new OrderItem { ProductId = appleId, ProductName = "Manzana", UnitPrice = 8000m, Quantity = 1, Subtotal = 8000m, Total = 8000m });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
        }

        // Master hard-deletes the apple (should not happen, but the satellite must survive it).
        await using (var m = _masterDb.NewContext())
        {
            m.Products.Remove(await m.Products.SingleAsync(x => x.Id == appleId));
            await m.SaveChangesAsync();
        }
        var snap2 = await MasterSnapshotAsync();
        CatalogApplyResult r;
        await using (var db = _satDb.NewContext())
            r = await SatSvc(db).ApplySnapshotAsync(snap2);

        r.Deactivated.Should().Be(1);
        await using var check = _satDb.NewContext();
        var apple = await check.Products.SingleAsync(x => x.Id == appleId);
        apple.IsActive.Should().BeFalse();
        (await check.OrderItems.CountAsync(i => i.ProductId == appleId)).Should().Be(1, "the sale still references the product");
    }

    /// <summary>Stable textual dump of the satellite catalog (ids + replicated fields) for before/after comparison.</summary>
    private async Task<List<string>> DumpAsync()
    {
        await using var db = _satDb.NewContext();
        var lines = new List<string>();
        lines.AddRange((await db.Categories.AsNoTracking().ToListAsync()).Select(c => $"C|{c.Id}|{c.Name}|{c.ColorHex}|{c.Icon}|{c.SortOrder}|{c.IsActive}"));
        lines.AddRange((await db.Taxes.AsNoTracking().ToListAsync()).Select(t => $"T|{t.Id}|{t.Name}|{t.Rate}|{t.IsActive}"));
        lines.AddRange((await db.Products.AsNoTracking().ToListAsync()).Select(p => $"P|{p.Id}|{p.Sku}|{p.Barcode}|{p.Name}|{p.Description}|{p.Price}|{p.Cost}|{p.IsWeighed}|{p.IsActive}|{p.CategoryId}|{p.TaxId}|{p.StockQuantity}"));
        lines.AddRange((await db.Billers.AsNoTracking().ToListAsync()).Select(b => $"B|{b.Id}|{b.Name}|{b.Code}|{b.CommissionFixed}|{b.CommissionPercent}|{b.ColorHex}|{b.SortOrder}|{b.IsActive}"));
        lines.AddRange((await db.Users.AsNoTracking().ToListAsync()).Select(u => $"U|{u.Id}|{u.Name}|{u.PinHash}|{u.Role}|{u.IsActive}|{u.LastLoginAt}"));
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }
}
