using Core.Domain.Entities;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Services;

/// <summary>
/// Tests for ProductService Update, Delete, and Stock operations.
/// </summary>
public class ProductServiceCrudTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly ProductService _service;

    public ProductServiceCrudTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new ProductService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task UpdateAsync_ModifiesExistingProduct()
    {
        var product = await _service.CreateAsync(new Product 
        { 
            Sku = "UPD-001", 
            Name = "Original Name", 
            Price = 10.00m 
        });

        product.Name = "Updated Name";
        product.Price = 15.00m;
        await _service.UpdateAsync(product);

        var updated = await _service.GetByIdAsync(product.Id);
        updated!.Name.Should().Be("Updated Name");
        updated.Price.Should().Be(15.00m);
        updated.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesProduct()
    {
        var product = await _service.CreateAsync(new Product 
        { 
            Sku = "DEL-001", 
            Name = "To Delete", 
            Price = 5m 
        });

        await _service.DeleteAsync(product.Id);

        var deleted = await _service.GetByIdAsync(product.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_DoesNotThrow()
    {
        var act = () => _service.DeleteAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AdjustStockAsync_IncreasesStock()
    {
        var product = await _service.CreateAsync(new Product 
        { 
            Sku = "STK-001", 
            Name = "Stock Test", 
            Price = 10m,
            StockQuantity = 50 
        });

        var result = await _service.AdjustStockAsync(product.Id, 25);

        result.Should().BeTrue();
        var updated = await _service.GetByIdAsync(product.Id);
        updated!.StockQuantity.Should().Be(75);
    }

    [Fact]
    public async Task AdjustStockAsync_DecreasesStock()
    {
        var product = await _service.CreateAsync(new Product 
        { 
            Sku = "STK-002", 
            Name = "Stock Decrease", 
            Price = 10m,
            StockQuantity = 100 
        });

        await _service.AdjustStockAsync(product.Id, -30);

        var updated = await _service.GetByIdAsync(product.Id);
        updated!.StockQuantity.Should().Be(70);
    }

    [Fact]
    public async Task AdjustStockAsync_NonExistent_ReturnsFalse()
    {
        var result = await _service.AdjustStockAsync(Guid.NewGuid(), 10);
        result.Should().BeFalse();
    }
}
