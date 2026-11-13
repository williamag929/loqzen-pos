using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Infrastructure.Data.Sync;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Sync;

/// <summary>
/// The live-run bug: a fresh satellite is seeded by DbInitializer with the demo catalog under its OWN Guids, so the
/// master's snapshot (same SKUs / names, other ids) used to collide on the unique indexes. Two REAL SQLite databases,
/// both migrated + seeded the production way.
/// </summary>
public class CatalogSyncSqliteTests : IAsyncLifetime
{
    private SqliteDb _masterDb = null!;
    private SqliteDb _satDb = null!;
    private readonly TestClock _clock = new();
    private readonly Core.Application.Services.TerminalContext _master = Terminals.Master("CAJA-01");
    private readonly Core.Application.Services.TerminalContext _sat = Terminals.Satellite("CAJA-02");

    public async Task InitializeAsync()
    {
        _masterDb = await SqliteDb.CreateMigratedAsync();
        _satDb = await SqliteDb.CreateMigratedAsync();
    }

    public Task DisposeAsync()
    {
        _masterDb.Dispose();
        _satDb.Dispose();
        return Task.CompletedTask;
    }

    private async Task<CatalogSnapshotDto> MasterSnapshotAsync()
    {
        await using var db = _masterDb.NewContext();
        return await new CatalogSyncService(db, _clock, _master).BuildSnapshotAsync();
    }

    private async Task<CatalogApplyResult> ApplyOnSatelliteAsync(CatalogSnapshotDto snap)
    {
        await using var db = _satDb.NewContext();
        return await new CatalogSyncService(db, _clock, _sat).ApplySnapshotAsync(snap);
    }

    /// <summary>Opens a shift on the satellite and sells ONE seeded product (by SKU) through the real checkout service.</summary>
    private async Task<(Guid productId, string sku, Guid orderId, Guid? categoryId, Guid? taxId)> SellSeededProductAsync(string sku = "BUR-001")
    {
        await using var db = _satDb.NewContext();
        var shift = await new CashShiftService(db, _clock, null, _sat).OpenShiftAsync("Cajero", 50000m);
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Sku == sku);
        var sales = new SalesService(db, _sat);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 1);
        var paid = await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Cash, order.Total, order.Total);
        paid.Success.Should().BeTrue();
        db.ChangeTracker.Clear();
        return (product.Id, product.Sku, order.Id, product.CategoryId, product.TaxId);
    }

    [Fact]
    public async Task SeededSatellite_WithOneSale_AppliesMasterSnapshot_WithoutCollisions()
    {
        var sold = await SellSeededProductAsync();
        var snap = await MasterSnapshotAsync();
        snap.Products.Should().NotBeEmpty();
        snap.Products.Select(p => p.Id).Should().NotContain(sold.productId, "the two seeds use different Guids");
        snap.Products.Should().Contain(p => p.Sku == sold.sku, "but the same SKUs");

        CatalogApplyResult result = null!;
        var act = async () => result = await ApplyOnSatelliteAsync(snap);
        await act.Should().NotThrowAsync("the seeded twins must be resolved before the master's rows are inserted");

        var totalRows = snap.Categories.Count + snap.Taxes.Count + snap.Products.Count + snap.Billers.Count + snap.Users.Count;
        result.Inserted.Should().Be(totalRows, "none of the master's ids existed locally");

        await using var check = _satDb.NewContext();

        // Every master row is now present, by id, with the master's data.
        foreach (var s in snap.Products)
        {
            var p = await check.Products.SingleAsync(x => x.Id == s.Id);
            p.Sku.Should().Be(s.Sku); p.Name.Should().Be(s.Name); p.IsActive.Should().Be(s.IsActive);
            p.CategoryId.Should().Be(s.CategoryId); p.TaxId.Should().Be(s.TaxId);
        }
        foreach (var s in snap.Categories)
        {
            var c = await check.Categories.SingleAsync(x => x.Id == s.Id);
            c.Name.Should().Be(s.Name); c.IsActive.Should().Be(s.IsActive);
        }
        foreach (var s in snap.Taxes)
        {
            var t = await check.Taxes.SingleAsync(x => x.Id == s.Id);
            t.Name.Should().Be(s.Name); t.Rate.Should().Be(s.Rate); t.IsActive.Should().Be(s.IsActive);
        }
        foreach (var s in snap.Billers)
        {
            var b = await check.Billers.SingleAsync(x => x.Id == s.Id);
            b.Name.Should().Be(s.Name); b.IsActive.Should().Be(s.IsActive);
        }
        foreach (var s in snap.Users)
        {
            var u = await check.Users.SingleAsync(x => x.Id == s.Id);
            u.Name.Should().Be(s.Name); u.PinHash.Should().Be(s.PinHash); u.IsActive.Should().Be(s.IsActive);
        }

        // Products: the sold twin survives (inactive, SKU mangled); the unreferenced seeded twins are gone.
        (await check.Products.CountAsync()).Should().Be(snap.Products.Count + 1);
        var twin = await check.Products.SingleAsync(p => p.Id == sold.productId);
        twin.IsActive.Should().BeFalse();
        twin.Sku.Should().StartWith(sold.sku + "~");
        twin.Sku.Should().NotBe(sold.sku);
        (await check.Products.CountAsync(p => p.Sku == sold.sku)).Should().Be(1, "the master's row owns the SKU now");
        (await check.Products.Where(p => p.IsActive).Select(p => p.Sku).ToListAsync()).Should().OnlyHaveUniqueItems();

        // Its category and tax are still referenced by the twin: kept, inactive, renamed "(local)".
        var twinCat = await check.Categories.SingleAsync(c => c.Id == sold.categoryId!.Value);
        twinCat.IsActive.Should().BeFalse();
        twinCat.Name.Should().EndWith(" (local)");
        var twinTax = await check.Taxes.SingleAsync(t => t.Id == sold.taxId!.Value);
        twinTax.IsActive.Should().BeFalse();
        twinTax.Name.Should().EndWith(" (local)");
        (await check.Categories.CountAsync()).Should().Be(snap.Categories.Count + 1);
        (await check.Taxes.CountAsync()).Should().Be(snap.Taxes.Count + 1);

        // Users: the seeded local users were never referenced → replaced by the master's; PIN login stays unambiguous.
        (await check.Users.CountAsync()).Should().Be(snap.Users.Count);
        (await check.Users.Where(u => u.IsActive).Select(u => u.Name).ToListAsync()).Should().OnlyHaveUniqueItems();
        (await check.Billers.CountAsync()).Should().Be(snap.Billers.Count);

        // The sale is intact and still points at the (now inactive) twin.
        var item = await check.OrderItems.SingleAsync(i => i.OrderId == sold.orderId);
        item.ProductId.Should().Be(sold.productId);
        (await check.Orders.SingleAsync(o => o.Id == sold.orderId)).Status.Should().Be(OrderStatus.Completed);
    }

    [Fact]
    public async Task SeededSatellite_SecondApply_IsIdempotent()
    {
        await SellSeededProductAsync();
        var snap = await MasterSnapshotAsync();
        await ApplyOnSatelliteAsync(snap);

        int products, categories, taxes, users, billers;
        await using (var c = _satDb.NewContext())
        {
            products = await c.Products.CountAsync(); categories = await c.Categories.CountAsync(); taxes = await c.Taxes.CountAsync();
            users = await c.Users.CountAsync(); billers = await c.Billers.CountAsync();
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await ApplyOnSatelliteAsync(snap);

        second.Inserted.Should().Be(0);
        second.Deactivated.Should().Be(0);
        await using var check = _satDb.NewContext();
        (await check.Products.CountAsync()).Should().Be(products);
        (await check.Categories.CountAsync()).Should().Be(categories);
        (await check.Taxes.CountAsync()).Should().Be(taxes);
        (await check.Users.CountAsync()).Should().Be(users);
        (await check.Billers.CountAsync()).Should().Be(billers);
    }

    [Fact]
    public async Task SeededSatellite_NoSales_AllTwinsReplaced_NoLeftovers()
    {
        var snap = await MasterSnapshotAsync();

        var r = await ApplyOnSatelliteAsync(snap);

        r.Inserted.Should().Be(snap.Categories.Count + snap.Taxes.Count + snap.Products.Count + snap.Billers.Count + snap.Users.Count);
        r.Deactivated.Should().Be(0, "nothing referenced the seeded rows, so they were dropped rather than mangled");
        await using var check = _satDb.NewContext();
        (await check.Products.CountAsync()).Should().Be(snap.Products.Count);
        (await check.Categories.CountAsync()).Should().Be(snap.Categories.Count);
        (await check.Taxes.CountAsync()).Should().Be(snap.Taxes.Count);
        (await check.Billers.CountAsync()).Should().Be(snap.Billers.Count);
        (await check.Users.CountAsync()).Should().Be(snap.Users.Count);
    }

    [Fact]
    public async Task Apply_OnMasterContext_Throws()
    {
        var snap = await MasterSnapshotAsync();
        await using var db = _satDb.NewContext();
        var act = () => new CatalogSyncService(db, _clock, _master).ApplySnapshotAsync(snap);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LocalSku_DifferingOnlyByCaseOrAccent_IsTreatedAsTwin()
    {
        // Master: "CAFE-01". Satellite: "café-01" referenced by a sale (so it is mangled, not deleted) and "Cafe-02" unreferenced (deleted).
        Guid masterCafeId, localReferencedId, localUnreferencedId;
        await using (var m = _masterDb.NewContext())
        {
            var p1 = new Product { Sku = "CAFE-01", Name = "Café tinto", Price = 3000m };
            var p2 = new Product { Sku = "CAFE-02", Name = "Café con leche", Price = 4000m };
            m.Products.AddRange(p1, p2);
            await m.SaveChangesAsync();
            masterCafeId = p1.Id;
        }
        await using (var s = _satDb.NewContext())
        {
            var local = new Product { Sku = "café-01", Name = "Cafe tinto local", Price = 2500m };
            var local2 = new Product { Sku = "Cafe-02", Name = "Con leche local", Price = 1m };
            s.Products.AddRange(local, local2);
            var shift = new CashShift { ShiftNumber = 1, CashierName = "x", Status = CashShiftStatus.Open, TerminalId = _sat.Id, TerminalCode = _sat.Code };
            var order = new Order { OrderNumber = "ORD-CAFE", Status = OrderStatus.Completed, CashShiftId = shift.Id };
            order.Items.Add(new OrderItem { ProductId = local.Id, ProductName = local.Name, UnitPrice = 2500m, Quantity = 1, Subtotal = 2500m, Total = 2500m });
            s.AddRange(shift, order);
            await s.SaveChangesAsync();
            localReferencedId = local.Id; localUnreferencedId = local2.Id;
        }

        var snap = await MasterSnapshotAsync();
        var act = () => ApplyOnSatelliteAsync(snap);
        await act.Should().NotThrowAsync();

        await using var check = _satDb.NewContext();
        (await check.Products.SingleAsync(p => p.Id == masterCafeId)).Sku.Should().Be("CAFE-01");
        var twin = await check.Products.SingleAsync(p => p.Id == localReferencedId);
        twin.IsActive.Should().BeFalse();
        twin.Sku.Should().StartWith("café-01~");
        (await check.Products.AnyAsync(p => p.Id == localUnreferencedId)).Should().BeFalse("unreferenced twin is dropped");
        (await check.OrderItems.CountAsync(i => i.ProductId == localReferencedId)).Should().Be(1);
    }

    [Fact]
    public async Task LocalUserName_DifferingOnlyByCaseOrAccent_IsTreatedAsTwin_WhenReferencedItIsMangled()
    {
        // Seeded user "Administrador" exists on both sides. Reference the local one from a shift so it must be kept.
        Guid localAdminId;
        await using (var s = _satDb.NewContext())
        {
            var admin = await s.Users.SingleAsync(u => u.Name == "Administrador");
            admin.Name = "ADMINISTRADOR";
            localAdminId = admin.Id;
            s.CashShifts.Add(new CashShift { ShiftNumber = 1, CashierName = "Admin", CashierId = admin.Id, OpenedByUserId = admin.Id, Status = CashShiftStatus.Closed, TerminalId = _sat.Id, TerminalCode = _sat.Code });
            await s.SaveChangesAsync();
        }

        var snap = await MasterSnapshotAsync();
        await ApplyOnSatelliteAsync(snap);

        await using var check = _satDb.NewContext();
        var masterAdminId = snap.Users.Single(u => u.Name == "Administrador").Id;
        (await check.Users.SingleAsync(u => u.Id == masterAdminId)).IsActive.Should().BeTrue();
        var twin = await check.Users.SingleAsync(u => u.Id == localAdminId);
        twin.IsActive.Should().BeFalse();
        twin.Name.Should().Be("ADMINISTRADOR (local)");
        (await check.Users.CountAsync()).Should().Be(snap.Users.Count + 1);
    }
}
