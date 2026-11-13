using Core.Domain.Entities;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Services;

/// <summary>
/// Integration tests for ProductService CRUD operations with in-memory database.
/// </summary>
public class ProductServiceTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly ProductService _service;

    public ProductServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new ProductService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CreateAsync_PersistsProduct()
    {
        var product = new Product
        {
            Sku = "PROD-001",
            Name = "Coca-Cola 600ml",
            Price = 15.00m,
            Cost = 10.00m,
            StockQuantity = 100
        };

        var created = await _service.CreateAsync(product);

        created.Id.Should().NotBeEmpty();
        var fetched = await _service.GetByIdAsync(created.Id);
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Coca-Cola 600ml");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllProducts()
    {
        await _service.CreateAsync(new Product { Sku = "P1", Name = "Product 1", Price = 10m });
        await _service.CreateAsync(new Product { Sku = "P2", Name = "Product 2", Price = 20m });

        var all = await _service.GetAllAsync();

        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByBarcodeOrSkuAsync_FindsBySku()
    {
        await _service.CreateAsync(new Product { Sku = "SKU-123", Name = "Test", Price = 10m });

        var found = await _service.GetByBarcodeOrSkuAsync("SKU-123");

        found.Should().NotBeNull();
        found!.Sku.Should().Be("SKU-123");
    }

    [Fact]
    public async Task GetByBarcodeOrSkuAsync_FindsByBarcode()
    {
        await _service.CreateAsync(new Product 
        { 
            Sku = "SKU-456", 
            Barcode = "7501234567890", 
            Name = "Scanned Product", 
            Price = 25m 
        });

        var found = await _service.GetByBarcodeOrSkuAsync("7501234567890");

        found.Should().NotBeNull();
        found!.Name.Should().Be("Scanned Product");
    }

    [Fact]
    public async Task GetByBarcodeOrSkuAsync_ReturnsNullWhenNotFound()
    {
        var found = await _service.GetByBarcodeOrSkuAsync("NONEXISTENT");
        found.Should().BeNull();
    }
}
