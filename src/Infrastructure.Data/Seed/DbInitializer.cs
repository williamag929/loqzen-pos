using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Domain.Entities;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Seed;

public static class DbInitializer
{
    public static async Task InitializeAsync(PosDbContext context)
    {
        await context.Database.EnsureCreatedAsync();

        if (await context.Categories.AnyAsync())
        {
            return; // DB has already been seeded
        }

        // 1. Default Tax
        var vat19 = new Tax
        {
            Name = "IVA 19%",
            Rate = 0.19m,
            IsActive = true
        };
        context.Taxes.Add(vat19);

        // 2. Default Categories
        var catFood = new Category { Name = "Comidas", ColorHex = "#EF4444", SortOrder = 1 };
        var catDrinks = new Category { Name = "Bebidas", ColorHex = "#3B82F6", SortOrder = 2 };
        var catBakery = new Category { Name = "Panadería", ColorHex = "#F59E0B", SortOrder = 3 };
        var catWeighed = new Category { Name = "Frutas / Balanza", ColorHex = "#10B981", SortOrder = 4 };

        context.Categories.AddRange(catFood, catDrinks, catBakery, catWeighed);

        // 3. Default Products
        context.Products.AddRange(
            new Product
            {
                Sku = "BUR-001",
                Barcode = "7701234567890",
                Name = "Hamburguesa Especial",
                Price = 22000m,
                Cost = 11000m,
                StockQuantity = 100,
                IsWeighed = false,
                Category = catFood,
                Tax = vat19
            },
            new Product
            {
                Sku = "PIZ-001",
                Barcode = "7701234567891",
                Name = "Pizza Personal Artesanal",
                Price = 18000m,
                Cost = 8000m,
                StockQuantity = 80,
                IsWeighed = false,
                Category = catFood,
                Tax = vat19
            },
            new Product
            {
                Sku = "BEB-001",
                Barcode = "7701234567892",
                Name = "Gaseosa 400ml",
                Price = 5000m,
                Cost = 2500m,
                StockQuantity = 200,
                IsWeighed = false,
                Category = catDrinks,
                Tax = vat19
            },
            new Product
            {
                Sku = "BEB-002",
                Barcode = "7701234567893",
                Name = "Jugo Natural de Fruta",
                Price = 7500m,
                Cost = 3000m,
                StockQuantity = 50,
                IsWeighed = false,
                Category = catDrinks,
                Tax = vat19
            },
            new Product
            {
                Sku = "PAN-001",
                Barcode = "7701234567894",
                Name = "Croissant de Mantequilla",
                Price = 4500m,
                Cost = 2000m,
                StockQuantity = 60,
                IsWeighed = false,
                Category = catBakery,
                Tax = vat19
            },
            new Product
            {
                Sku = "FRU-001",
                Barcode = "7701234567895",
                Name = "Manzana Roja (Kg)",
                Price = 9000m, // Price per Kg
                Cost = 4500m,
                StockQuantity = 50,
                IsWeighed = true, // Weighed via CAS SW-20RS scale
                Category = catWeighed,
                Tax = vat19
            },
            new Product
            {
                Sku = "FRU-002",
                Barcode = "7701234567896",
                Name = "Uvas Importadas (Kg)",
                Price = 16000m, // Price per Kg
                Cost = 9000m,
                StockQuantity = 30,
                IsWeighed = true, // Weighed via CAS SW-20RS scale
                Category = catWeighed,
                Tax = vat19
            }
        );

        // 4. Default Generic Customer
        context.Customers.Add(new Customer
        {
            DocumentId = "222222222222",
            FullName = "Consumidor Final",
            Email = "consumidor@loqzen.local",
            PhoneNumber = "0000000000",
            Address = "Local"
        });

        await context.SaveChangesAsync();
    }
}
