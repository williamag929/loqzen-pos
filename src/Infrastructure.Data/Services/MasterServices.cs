using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.Services;
using Core.Domain.Entities;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

// ─── Products ───────────────────────────────────────────────────────────────

public class ProductService : IProductService
{
    private readonly PosDbContext _db;
    public ProductService(PosDbContext db) => _db = db;

    public Task<List<Product>> GetAllAsync(CancellationToken ct = default)
        => _db.Products.Include(p => p.Category).Include(p => p.Tax)
              .OrderBy(p => p.Name).ToListAsync(ct);

    public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Products.Include(p => p.Category).Include(p => p.Tax)
              .FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Product?> GetByBarcodeOrSkuAsync(string query, CancellationToken ct = default)
        => _db.Products.Include(p => p.Category).Include(p => p.Tax)
              .FirstOrDefaultAsync(p => p.Barcode == query || p.Sku == query, ct);

    public async Task<Product> CreateAsync(Product product, CancellationToken ct = default)
    {
        _db.Products.Add(product);
        await _db.SaveChangesAsync(ct);
        return product;
    }

    public async Task<Product> UpdateAsync(Product product, CancellationToken ct = default)
    {
        product.UpdatedAt = DateTime.UtcNow;
        _db.Products.Update(product);
        await _db.SaveChangesAsync(ct);
        return product;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Products.FindAsync(new object[] { id }, ct);
        if (entity != null) { _db.Products.Remove(entity); await _db.SaveChangesAsync(ct); }
    }

    public async Task<bool> AdjustStockAsync(Guid productId, decimal delta, CancellationToken ct = default)
    {
        var entity = await _db.Products.FindAsync(new object[] { productId }, ct);
        if (entity == null) return false;
        entity.StockQuantity += delta;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

// ─── Categories ─────────────────────────────────────────────────────────────

public class CategoryService : ICategoryService
{
    private readonly PosDbContext _db;
    public CategoryService(PosDbContext db) => _db = db;

    public Task<List<Category>> GetAllAsync(CancellationToken ct = default)
        => _db.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);

    public Task<Category?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<Category> CreateAsync(Category category, CancellationToken ct = default)
    {
        _db.Categories.Add(category);
        await _db.SaveChangesAsync(ct);
        return category;
    }

    public async Task<Category> UpdateAsync(Category category, CancellationToken ct = default)
    {
        category.UpdatedAt = DateTime.UtcNow;
        _db.Categories.Update(category);
        await _db.SaveChangesAsync(ct);
        return category;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Categories.FindAsync(new object[] { id }, ct);
        if (entity != null) { _db.Categories.Remove(entity); await _db.SaveChangesAsync(ct); }
    }
}

// ─── Taxes ──────────────────────────────────────────────────────────────────

public class TaxService : ITaxService
{
    private readonly PosDbContext _db;
    public TaxService(PosDbContext db) => _db = db;

    public Task<List<Tax>> GetAllAsync(CancellationToken ct = default)
        => _db.Taxes.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync(ct);

    public Task<Tax?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Taxes.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<Tax> CreateAsync(Tax tax, CancellationToken ct = default)
    {
        _db.Taxes.Add(tax);
        await _db.SaveChangesAsync(ct);
        return tax;
    }

    public async Task<Tax> UpdateAsync(Tax tax, CancellationToken ct = default)
    {
        tax.UpdatedAt = DateTime.UtcNow;
        _db.Taxes.Update(tax);
        await _db.SaveChangesAsync(ct);
        return tax;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Taxes.FindAsync(new object[] { id }, ct);
        if (entity != null) { _db.Taxes.Remove(entity); await _db.SaveChangesAsync(ct); }
    }
}

// ─── Customers ──────────────────────────────────────────────────────────────

public class CustomerService : ICustomerService
{
    private readonly PosDbContext _db;
    public CustomerService(PosDbContext db) => _db = db;

    public Task<List<Customer>> GetAllAsync(CancellationToken ct = default)
        => _db.Customers.Where(c => c.IsActive).OrderBy(c => c.FullName).ToListAsync(ct);

    public Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Customer?> GetByDocumentIdAsync(string documentId, CancellationToken ct = default)
        => _db.Customers.FirstOrDefaultAsync(c => c.DocumentId == documentId, ct);

    public async Task<Customer> CreateAsync(Customer customer, CancellationToken ct = default)
    {
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<Customer> UpdateAsync(Customer customer, CancellationToken ct = default)
    {
        customer.UpdatedAt = DateTime.UtcNow;
        _db.Customers.Update(customer);
        await _db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Customers.FindAsync(new object[] { id }, ct);
        if (entity != null)
        {
            entity.IsActive = false; // Soft-delete
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
