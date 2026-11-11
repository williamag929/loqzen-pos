using System;
using System.Collections.Generic;
using Core.Domain.Common;

namespace Core.Domain.Entities;

public class Category : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? ColorHex { get; set; } = "#3B82F6";
    public string? Icon { get; set; }
    public int SortOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    public ICollection<Product> Products { get; set; } = new List<Product>();
}

public class Product : BaseEntity
{
    public string Sku { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public decimal Cost { get; set; }
    public decimal StockQuantity { get; set; } = 0;
    
    // Peripherals flag: True if sold by weight (requires CAS SW-20RS scale)
    public bool IsWeighed { get; set; } = false;
    public bool IsActive { get; set; } = true;

    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    public Guid? TaxId { get; set; }
    public Tax? Tax { get; set; }
}
