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
    public DbSet<KitchenOrder> KitchenOrders => Set<KitchenOrder>();
    public DbSet<KitchenOrderItem> KitchenOrderItems => Set<KitchenOrderItem>();
    public DbSet<StoreConfiguration> StoreConfigurations => Set<StoreConfiguration>();
    public DbSet<DiningTable> DiningTables => Set<DiningTable>();
    public DbSet<Comanda> Comandas => Set<Comanda>();
    public DbSet<ComandaItem> ComandaItems => Set<ComandaItem>();
    public DbSet<CashCountLine> CashCountLines => Set<CashCountLine>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ManagerOverride> ManagerOverrides => Set<ManagerOverride>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Biller> Billers => Set<Biller>();
    public DbSet<BillPayment> BillPayments => Set<BillPayment>();
    public DbSet<PartialCashCount> PartialCashCounts => Set<PartialCashCount>();

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
            entity.Property(s => s.FloatLeftAmount).HasPrecision(18, 2);
            entity.Property(s => s.ClosedByName).HasMaxLength(100);
            entity.Ignore(s => s.Difference);
        });

        // OutboxMessage
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.EventType).IsRequired().HasMaxLength(100);
            entity.HasIndex(m => m.Status);
            entity.HasIndex(m => m.CreatedAt);
        });

        // KitchenOrder
        modelBuilder.Entity<KitchenOrder>(entity =>
        {
            entity.HasKey(k => k.Id);
            entity.Property(k => k.OrderNumber).IsRequired().HasMaxLength(50);
            entity.Property(k => k.TableNumber).IsRequired().HasMaxLength(50);
            entity.Property(k => k.Station).IsRequired().HasMaxLength(100);
            entity.HasIndex(k => k.Status);
            entity.HasIndex(k => k.CreatedAt);

            entity.HasMany(k => k.Items)
                  .WithOne(i => i.KitchenOrder)
                  .HasForeignKey(i => i.KitchenOrderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // KitchenOrderItem
        modelBuilder.Entity<KitchenOrderItem>(entity =>
        {
            entity.HasKey(ki => ki.Id);
            entity.Property(ki => ki.ProductName).IsRequired().HasMaxLength(200);
            entity.Property(ki => ki.Quantity).HasPrecision(18, 3);
        });

        // StoreConfiguration
        modelBuilder.Entity<StoreConfiguration>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.StoreName).IsRequired().HasMaxLength(200);
            entity.Property(s => s.Address).HasMaxLength(300);
            entity.Property(s => s.City).HasMaxLength(100);
            entity.Property(s => s.Phone).HasMaxLength(50);
            entity.Property(s => s.Email).HasMaxLength(100);
            entity.Property(s => s.TaxId).HasMaxLength(50);
            entity.Property(s => s.TaxRegime).HasMaxLength(100);
            entity.Property(s => s.Currency).HasMaxLength(3);
            entity.Property(s => s.CultureCode).HasMaxLength(10);
        });

        // DiningTable
        modelBuilder.Entity<DiningTable>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(50);
            entity.HasIndex(t => t.Name).IsUnique();
        });

        // Comanda
        modelBuilder.Entity<Comanda>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.TableName).IsRequired().HasMaxLength(50);
            entity.Property(c => c.WaiterName).HasMaxLength(100);
            entity.HasIndex(c => c.Status);
            entity.HasIndex(c => c.Number);
            entity.HasOne(c => c.DiningTable).WithMany().HasForeignKey(c => c.DiningTableId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(c => c.Order).WithMany().HasForeignKey(c => c.OrderId).OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(c => c.Items)
                  .WithOne(i => i.Comanda)
                  .HasForeignKey(i => i.ComandaId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ComandaItem
        modelBuilder.Entity<ComandaItem>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.ProductName).IsRequired().HasMaxLength(200);
            entity.Property(i => i.Station).HasMaxLength(100);
            entity.Property(i => i.Notes).HasMaxLength(Core.Application.DTOs.ComandaSettings.MaxNotesLength);
            entity.Property(i => i.UnitPrice).HasPrecision(18, 2);
            entity.Property(i => i.TaxRate).HasPrecision(6, 4);
            entity.Property(i => i.Quantity).HasPrecision(18, 3);
            entity.Property(i => i.WeightKg).HasPrecision(18, 3);
            entity.HasIndex(i => new { i.Status, i.AddedAt });
        });

        // CashCountLine (arqueo)
        modelBuilder.Entity<CashCountLine>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Denomination).HasPrecision(18, 2);
            entity.Ignore(l => l.Total);
            entity.HasOne(l => l.CashShift)
                  .WithMany(s => s.CountLines)
                  .HasForeignKey(l => l.CashShiftId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // User (PIN login)
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Name).IsRequired().HasMaxLength(100);
            entity.Property(u => u.PinHash).IsRequired().HasMaxLength(200);
            entity.HasIndex(u => u.IsActive);
        });

        // ManagerOverride (audit of manager approvals)
        modelBuilder.Entity<ManagerOverride>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Description).IsRequired().HasMaxLength(300);
            entity.Property(m => m.ManagerName).IsRequired().HasMaxLength(100);
            entity.Property(m => m.RequestedByName).HasMaxLength(100);
            entity.Property(m => m.Reference).HasMaxLength(200);
            entity.Property(m => m.ConsumedFor).HasMaxLength(200);
            entity.Property(m => m.Amount).HasPrecision(18, 2);
            entity.HasIndex(m => m.AuthorizedAt);
            entity.HasIndex(m => m.Action);
        });

        // CashMovement (non-sale cash in/out)
        modelBuilder.Entity<CashMovement>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Amount).HasPrecision(18, 2);
            entity.Property(m => m.Reason).IsRequired().HasMaxLength(200);
            entity.Property(m => m.Reference).HasMaxLength(100);
            entity.Property(m => m.UserName).HasMaxLength(100);
            entity.HasIndex(m => m.CashShiftId);
            entity.HasOne(m => m.CashShift)
                  .WithMany(s => s.Movements)
                  .HasForeignKey(m => m.CashShiftId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Biller (convenios de recaudo)
        modelBuilder.Entity<Biller>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(b => b.Name).IsUnique();
            entity.Property(b => b.Code).HasMaxLength(30);
            entity.Property(b => b.ColorHex).HasMaxLength(9);
            entity.Property(b => b.CommissionFixed).HasPrecision(18, 2);
            entity.Property(b => b.CommissionPercent).HasPrecision(6, 4);
        });

        // BillPayment (recaudo)
        modelBuilder.Entity<BillPayment>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.BillerName).IsRequired().HasMaxLength(100);
            entity.Property(b => b.AccountReference).IsRequired().HasMaxLength(60);
            entity.Property(b => b.CustomerName).HasMaxLength(150);
            entity.Property(b => b.ReceiptNumber).IsRequired().HasMaxLength(30);
            entity.HasIndex(b => b.ReceiptNumber).IsUnique();
            entity.Property(b => b.UserName).HasMaxLength(100);
            entity.Property(b => b.VoidReason).HasMaxLength(200);
            entity.Property(b => b.Amount).HasPrecision(18, 2);
            entity.Property(b => b.Commission).HasPrecision(18, 2);
            entity.Property(b => b.TenderedAmount).HasPrecision(18, 2);
            entity.Property(b => b.ChangeAmount).HasPrecision(18, 2);
            entity.Ignore(b => b.Total);
            entity.HasIndex(b => b.CashShiftId);
        });

        // PartialCashCount (arqueo parcial)
        modelBuilder.Entity<PartialCashCount>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.ExpectedAmount).HasPrecision(18, 2);
            entity.Property(p => p.CountedAmount).HasPrecision(18, 2);
            entity.Property(p => p.UserName).HasMaxLength(100);
            entity.Ignore(p => p.Difference);
            entity.HasIndex(p => p.CashShiftId);
        });
    }
}
