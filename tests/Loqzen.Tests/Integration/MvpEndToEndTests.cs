using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Integration;

/// <summary>
/// End-to-end integration tests verifying the complete MVP flow:
/// Cash Shift → Order Creation → Add Items → Payment Processing → Persistence
/// </summary>
public class MvpEndToEndTests : IDisposable
{
    private readonly DatabaseFixture _fixture;
    private readonly ICashShiftService _cashShiftService;
    private readonly ISalesService _salesService;

    public MvpEndToEndTests()
    {
        _fixture = new DatabaseFixture();
        _cashShiftService = new CashShiftService(_fixture.DbContext);
        _salesService = new SalesService(_fixture.DbContext);
        SeedTestData();
    }

    private void SeedTestData()
    {
        var tax = new Tax { Name = "IVA 19%", Rate = 0.19m };
        var category = new Category { Name = "Bebidas", SortOrder = 1 };
        _fixture.DbContext.Taxes.Add(tax);
        _fixture.DbContext.Categories.Add(category);
        _fixture.DbContext.SaveChanges();

        _fixture.DbContext.Products.AddRange(
            new Product { Sku = "COLA-001", Name = "Coca-Cola", Price = 2500m, Cost = 1800m, StockQuantity = 100, IsActive = true, CategoryId = category.Id, TaxId = tax.Id },
            new Product { Sku = "CAFE-001", Name = "Café", Price = 1500m, Cost = 500m, StockQuantity = 100, IsActive = true, CategoryId = category.Id, TaxId = tax.Id },
            new Product { Sku = "PESO-001", Name = "Queso (kg)", Price = 15000m, Cost = 10000m, StockQuantity = 50, IsActive = true, IsWeighed = true, CategoryId = category.Id, TaxId = tax.Id }
        );
        _fixture.DbContext.SaveChanges();
    }

    [Fact]
    public async Task OpenCashShift_ShouldCreateOpenShift()
    {
        var shift = await _cashShiftService.OpenShiftAsync("Juan", 50000m);
        shift.ShiftNumber.Should().Be(1);
        shift.Status.Should().Be(CashShiftStatus.Open);
    }

    [Fact]
    public async Task CreateOrderWithItems_ShouldCalculateTotalsCorrectly()
    {
        var shift = await _cashShiftService.OpenShiftAsync("Test", 50000m);
        var cola = await _fixture.DbContext.Products.FirstAsync(p => p.Sku == "COLA-001");

        var order = await _salesService.CreateOrderAsync(null, shift.Id);
        order = await _salesService.AddItemAsync(order.Id, cola.Id, 2);

        // 2 x 2500 = 5000, Tax: 950, Total: 5950
        order.Subtotal.Should().Be(5000m);
        order.TaxTotal.Should().Be(950m);
        order.Total.Should().Be(5950m);
    }

    [Fact]
    public async Task ProcessPayment_ShouldCalculateChangeAndCreateOutbox()
    {
        var shift = await _cashShiftService.OpenShiftAsync("Test", 50000m);
        var cola = await _fixture.DbContext.Products.FirstAsync(p => p.Sku == "COLA-001");
        
        var order = await _salesService.CreateOrderAsync(null, shift.Id);
        order = await _salesService.AddItemAsync(order.Id, cola.Id, 2);

        var result = await _salesService.ProcessPaymentAsync(order.Id, PaymentMethod.Cash, order.Total, 10000m);

        result.Success.Should().BeTrue();
        result.ChangeAmount.Should().Be(4050m); // 10000 - 5950

        var outbox = await _fixture.DbContext.OutboxMessages.FirstOrDefaultAsync(m => m.AggregateId == order.Id);
        outbox.Should().NotBeNull();
        outbox!.EventType.Should().Be("OrderCompleted");
    }

    [Fact]
    public async Task CloseCashShift_ShouldCalculateExpectedFromCashSales()
    {
        var shift = await _cashShiftService.OpenShiftAsync("Test", 50000m);
        var cola = await _fixture.DbContext.Products.FirstAsync(p => p.Sku == "COLA-001");

        var order = await _salesService.CreateOrderAsync(null, shift.Id);
        order = await _salesService.AddItemAsync(order.Id, cola.Id, 2);
        await _salesService.ProcessPaymentAsync(order.Id, PaymentMethod.Cash, order.Total, 10000m);

        // Expected: 50000 + 5950 = 55950
        var closed = await _cashShiftService.CloseShiftAsync(55950m, "OK");
        closed.ExpectedAmount.Should().Be(55950m);
        closed.Difference.Should().Be(0m);
    }

    public void Dispose() => _fixture.Dispose();
}
