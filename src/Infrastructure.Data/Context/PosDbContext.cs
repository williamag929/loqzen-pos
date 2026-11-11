using System;
using System.IO;
using Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Context;

public class PosDbContext : DbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Tax> Taxes => Set<Tax>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CashShift> CashShifts => Set<CashShift>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public PosDbContext()
    {
    }

    public PosDbContext(DbContextOptions<PosDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dbFolder = Path.Combine(appData, "LoqzenPos");
            Directory.CreateDirectory(dbFolder);
            var dbPath = Path.Combine(dbFolder, "pos.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Category
        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).IsRequired().HasMaxLength(100);
        });

        // Product
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
            entity.Property(p => p.Sku).IsRequired().HasMaxLength(50);
            entity.HasIndex(p => p.Sku).IsUnique();
            entity.HasIndex(p => p.Barcode);
            entity.Property(p => p.Price).HasPrecision(18, 2);
            entity.Property(p => p.Cost).HasPrecision(18, 2);
            entity.Property(p => p.StockQuantity).HasPrecision(18, 3);
        });

        // Customer
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.DocumentId).IsRequired().HasMaxLength(50);
            entity.HasIndex(c => c.DocumentId);
            entity.Property(c => c.FullName).IsRequired().HasMaxLength(200);
        });

        // Tax
        modelBuilder.Entity<Tax>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(50);
            entity.Property(t => t.Rate).HasPrecision(6, 4);
        });

        // Order
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.OrderNumber).IsRequired().HasMaxLength(50);
            entity.HasIndex(o => o.OrderNumber).IsUnique();
            entity.Property(o => o.Subtotal).HasPrecision(18, 2);
            entity.Property(o => o.TaxTotal).HasPrecision(18, 2);
            entity.Property(o => o.DiscountTotal).HasPrecision(18, 2);
            entity.Property(o => o.Total).HasPrecision(18, 2);

            entity.HasMany(o => o.Items)
                  .WithOne(i => i.Order)
                  .HasForeignKey(i => i.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(o => o.Payments)
                  .WithOne(p => p.Order)
                  .HasForeignKey(p => p.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // OrderItem
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.ProductName).IsRequired().HasMaxLength(200);
            entity.Property(i => i.UnitPrice).HasPrecision(18, 2);
            entity.Property(i => i.Quantity).HasPrecision(18, 3);
            entity.Property(i => i.TaxRate).HasPrecision(6, 4);
            entity.Property(i => i.TaxAmount).HasPrecision(18, 2);
            entity.Property(i => i.Subtotal).HasPrecision(18, 2);
            entity.Property(i => i.Total).HasPrecision(18, 2);
        });

        // Payment
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Amount).HasPrecision(18, 2);
            entity.Property(p => p.TenderedAmount).HasPrecision(18, 2);
            entity.Property(p => p.ChangeAmount).HasPrecision(18, 2);
        });

        // CashShift
        modelBuilder.Entity<CashShift>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.OpeningAmount).HasPrecision(18, 2);
            entity.Property(s => s.ClosingAmount).HasPrecision(18, 2);
            entity.Property(s => s.ExpectedAmount).HasPrecision(18, 2);
        });

        // OutboxMessage
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.EventType).IsRequired().HasMaxLength(100);
            entity.HasIndex(m => m.Status);
            entity.HasIndex(m => m.CreatedAt);
        });
    }
}
