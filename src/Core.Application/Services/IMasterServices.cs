using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Domain.Entities;

namespace Core.Application.Services;

// ─── Products ───────────────────────────────────────────────────────────────

public interface IProductService
{
    Task<List<Product>> GetAllAsync(CancellationToken ct = default);
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Product?> GetByBarcodeOrSkuAsync(string query, CancellationToken ct = default);
    Task<Product> CreateAsync(Product product, CancellationToken ct = default);
    Task<Product> UpdateAsync(Product product, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> AdjustStockAsync(Guid productId, decimal delta, CancellationToken ct = default);
}

// ─── Categories ─────────────────────────────────────────────────────────────

public interface ICategoryService
{
    Task<List<Category>> GetAllAsync(CancellationToken ct = default);
    Task<Category?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Category> CreateAsync(Category category, CancellationToken ct = default);
    Task<Category> UpdateAsync(Category category, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

// ─── Taxes ──────────────────────────────────────────────────────────────────

public interface ITaxService
{
    Task<List<Tax>> GetAllAsync(CancellationToken ct = default);
    Task<Tax?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Tax> CreateAsync(Tax tax, CancellationToken ct = default);
    Task<Tax> UpdateAsync(Tax tax, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

// ─── Customers ──────────────────────────────────────────────────────────────

public interface ICustomerService
{
    Task<List<Customer>> GetAllAsync(CancellationToken ct = default);
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Customer?> GetByDocumentIdAsync(string documentId, CancellationToken ct = default);
    Task<Customer> CreateAsync(Customer customer, CancellationToken ct = default);
    Task<Customer> UpdateAsync(Customer customer, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
