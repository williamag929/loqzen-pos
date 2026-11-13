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

        // Tables are seeded independently so existing DBs also get them.
        if (!await context.DiningTables.AnyAsync())
        {
            for (var i = 1; i <= 12; i++)
                context.DiningTables.Add(new DiningTable { Name = $"Mesa {i}", SortOrder = i });
            context.DiningTables.Add(new DiningTable { Name = "Barra", SortOrder = 100 });
            context.DiningTables.Add(new DiningTable { Name = "Para llevar", SortOrder = 101 });
            await context.SaveChangesAsync();
        }

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

        // 5. Default Active Kitchen Orders (For KDS)
        var order1 = new KitchenOrder
        {
            OrderNumber = "ORD-KDS-101",
            TableNumber = "Mesa 4",
            WaiterName = "Carlos M.",
            Station = "Cocina Caliente",
            Status = Core.Domain.Enums.KitchenOrderStatus.Pending,
            GeneralNotes = "Mesa VIP - Entregar juntos",
            CreatedAt = DateTime.UtcNow.AddMinutes(-3) // Green (< 5 min)
        };
        order1.Items.Add(new KitchenOrderItem
        {
            ProductName = "Hamburguesa Especial",
            Quantity = 2,
            Notes = "1 sin cebolla, 1 con tocineta extra"
        });
        order1.Items.Add(new KitchenOrderItem
        {
            ProductName = "Pizza Personal Artesanal",
            Quantity = 1,
            Notes = "Masa delgada bien dorada"
        });

        var order2 = new KitchenOrder
        {
            OrderNumber = "ORD-KDS-102",
            TableNumber = "Mesa 2",
            WaiterName = "Ana R.",
            Station = "Barra",
            Status = Core.Domain.Enums.KitchenOrderStatus.InPreparation,
            GeneralNotes = "Para llevar",
            CreatedAt = DateTime.UtcNow.AddMinutes(-9) // Yellow (5 - 15 min)
        };
        order2.Items.Add(new KitchenOrderItem
        {
            ProductName = "Jugo Natural de Fruta",
            Quantity = 2,
            Notes = "En agua, sin azúcar"
        });
        order2.Items.Add(new KitchenOrderItem
        {
            ProductName = "Croissant de Mantequilla",
            Quantity = 2,
            Notes = "Calentar 30 segundos"
        });

        var order3 = new KitchenOrder
        {
            OrderNumber = "ORD-KDS-103",
            TableNumber = "Mesa 7",
            WaiterName = "Juan P.",
            Station = "Cocina Caliente",
            Status = Core.Domain.Enums.KitchenOrderStatus.Pending,
            GeneralNotes = "Prioridad urgente",
            CreatedAt = DateTime.UtcNow.AddMinutes(-18) // Red (> 15 min)
        };
        order3.Items.Add(new KitchenOrderItem
        {
            ProductName = "Hamburguesa Especial",
            Quantity = 3,
            Notes = "Término 3/4 con papas rústicas"
        });

        context.KitchenOrders.AddRange(order1, order2, order3);

        // 6. Default Store Configuration
        context.StoreConfigurations.Add(new StoreConfiguration
        {
            StoreName = "LOQZEN POS - Restaurante Demo",
            Address = "Calle 123 #45-67, Centro Comercial Plaza Mayor, Local 203",
            City = "Bogotá D.C., Colombia",
            Phone = "+57 601 234 5678",
            Email = "info@loqzenpos.com",
            TaxId = "NIT 900.123.456-7",
            TaxRegime = "Responsable de IVA - Régimen Común",
            ReceiptFooterMessage = "¡Gracias por su visita! Esperamos volver a verle pronto.",
            ReceiptLogoAscii = @"
   _    ___  ___  _______ _   _ 
  | |  / _ \/ _ \|___  / | | | |
  | |_| | | | | |  / /| |_| | |
  |  _| |_| | |_| / /_|  _  | |
  |_|  \___/ \__\_/____|_| |_|_|
     P O S   S Y S T E M
",
            Currency = "COP",
            CultureCode = "es-CO",
            ReceiptWidth = 32,
            PrintCustomerCopy = true,
            PrintMerchantCopy = false,
            EnableElectronicInvoicing = false,
            IsActive = true
        });

        await context.SaveChangesAsync();
    }
}
