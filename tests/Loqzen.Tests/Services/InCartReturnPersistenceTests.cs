using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Services;

/// <summary>
/// The quick-sale screen sends each cart line (including return lines with negative qty/weight)
/// to SalesService.AddItemAsync. Verifies the persisted invoice equals what the cashier saw on screen.
/// </summary>
public class InCartReturnPersistenceTests
{
    [Fact]
    public async Task UnitReturn_PersistedTotalMatchesCartPreview()
    {
        using var db = TestDbContextFactory.Create();
        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        var soda = new Product { Sku = "S", Name = "Gaseosa", Price = 5000m, Tax = tax };
        var shift = new CashShift { ShiftNumber = 1, Status = CashShiftStatus.Open };
        db.AddRange(tax, soda, shift);
        await db.SaveChangesAsync();

        var cart = new List<CartItemDto>
        {
            new() { ProductId = soda.Id, Name = "Gaseosa", UnitPrice = 5000m, Quantity = 3, TaxRate = 0.19m },
            new() { ProductId = soda.Id, Name = "Gaseosa", UnitPrice = 5000m, Quantity = -1, TaxRate = 0.19m, IsReturn = true }
        };
        var preview = ReceiptDtoFactory.FromCart(cart, 0m, "X", "Caja");

        var svc = new SalesService(db);
        var order = await svc.CreateOrderAsync(null, shift.Id);
        foreach (var i in cart) await svc.AddItemAsync(order.Id, i.ProductId, i.Quantity, i.WeightKg);
        var saved = await svc.GetByIdAsync(order.Id);

        preview.Total.Should().Be(11900m);
        saved!.Total.Should().Be(preview.Total, "paper/screen and database must agree");
    }

    [Fact]
    public async Task UnitReturn_AfterMergeIntoSameLine_DoesNotGoNegative()
    {
        // SalesService merges same-product non-weighed lines: 3 + (-1) = 2 on ONE OrderItem.
        using var db = TestDbContextFactory.Create();
        var soda = new Product { Sku = "S", Name = "Gaseosa", Price = 5000m };
        var shift = new CashShift { ShiftNumber = 1, Status = CashShiftStatus.Open };
        db.AddRange(soda, shift);
        await db.SaveChangesAsync();

        var svc = new SalesService(db);
        var order = await svc.CreateOrderAsync(null, shift.Id);
        await svc.AddItemAsync(order.Id, soda.Id, 3);
        await svc.AddItemAsync(order.Id, soda.Id, -1);
        var saved = await svc.GetByIdAsync(order.Id);

        saved!.Items.Should().ContainSingle().Which.Quantity.Should().Be(2);
        saved.Total.Should().Be(10000m);
    }

    [Fact]
    public async Task WeighedReturn_IsCredited_NotCharged()
    {
        using var db = TestDbContextFactory.Create();
        var apple = new Product { Sku = "A", Name = "Manzana", Price = 9000m, IsWeighed = true };
        var shift = new CashShift { ShiftNumber = 1, Status = CashShiftStatus.Open };
        db.AddRange(apple, shift);
        await db.SaveChangesAsync();

        var cart = new List<CartItemDto>
        {
            new() { ProductId = apple.Id, Name = "Manzana", UnitPrice = 9000m, IsWeighed = true, Quantity = 1, WeightKg = 2m, TaxRate = 0m },
            // what CheckoutViewModel.RemoveItem now creates for a weighed product
            new() { ProductId = apple.Id, Name = "Manzana", UnitPrice = 9000m, IsWeighed = true, Quantity = -1, WeightKg = -2m, TaxRate = 0m, IsReturn = true }
        };
        cart[1].Total.Should().Be(-18000m, "a return line must reduce the total");

        var svc = new SalesService(db);
        var order = await svc.CreateOrderAsync(null, shift.Id);
        foreach (var i in cart) await svc.AddItemAsync(order.Id, i.ProductId, i.Quantity, i.WeightKg);
        (await svc.GetByIdAsync(order.Id))!.Total.Should().Be(0m);
    }
}
