using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>
/// Real SQLite database, manual clock (drives the charge timeout), scriptable drivers (Mock + JsonTcp providers), real
/// ComandaService / CashShiftService behind the PaymentTerminalService.
/// </summary>
public abstract class PaymentTerminalServiceTestBase : IDisposable
{
    protected readonly SqliteDb Sqlite = SqliteDb.Create();
    protected readonly PosDbContext Db;
    protected readonly ManualTimeProvider Clock = new();
    protected readonly FakeTerminalDriver Driver = new(PaymentTerminalProvider.Mock);
    protected readonly FakeTerminalDriver TcpDriver = new(PaymentTerminalProvider.JsonTcp);
    protected readonly PaymentTerminalSettings Settings = new() { ChargeTimeoutSeconds = 60, MockDelaySeconds = 0, Currency = "COP" };
    protected readonly ComandaService Comandas;
    protected readonly CashShiftService Shifts;
    protected readonly PaymentTerminalService Service;
    protected readonly Product Burger;
    protected readonly Product Soda;

    protected PaymentTerminalServiceTestBase()
    {
        Db = Sqlite.NewContext();
        Comandas = new ComandaService(Db, new ComandaSettings { ConfirmationDelaySeconds = 30 }, Clock);
        Shifts = new CashShiftService(Db, Clock, new CashShiftSettings { ManagerRequiredFor = new List<string>() });
        Service = new PaymentTerminalService(Db, new FakeDriverFactory(Driver, TcpDriver), Settings, Comandas, Shifts, Clock);

        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        var food = new Category { Name = "Comidas" };
        Burger = new Product { Sku = "B1", Name = "Hamburguesa", Price = 20000m, Tax = tax, Category = food };
        Soda = new Product { Sku = "S1", Name = "Gaseosa", Price = 5000m, Tax = tax, Category = food };
        Db.AddRange(tax, food, Burger, Soda);
        Db.SaveChanges();
    }

    protected DateTime Now => Clock.GetUtcNow().UtcDateTime;

    protected Task<PaymentTerminalDto> AddTerminalAsync(string name = "Datafono 1", PaymentTerminalProvider provider = PaymentTerminalProvider.Mock,
        string? address = null, bool isActive = true, int sortOrder = 0) =>
        Service.SaveAsync(new SavePaymentTerminalRequest(null, name, provider, address, null, isActive, sortOrder));

    protected static ChargeOnTerminalRequest Charge(Guid terminalId, decimal amount = 12500m, string reference = "ORD-000001",
        TerminalPaymentPurpose purpose = TerminalPaymentPurpose.Sale, Guid? orderId = null, Guid? comandaId = null, Guid? billPaymentId = null,
        Guid? cashShiftId = null, string requestedBy = "Ana", string? description = null) =>
        new(terminalId, amount, purpose, reference, Guid.NewGuid(), requestedBy, orderId, comandaId, billPaymentId, cashShiftId, description);

    /// <summary>Reads the audit row through a fresh context (what a crash-recovery or another screen would see).</summary>
    protected async Task<TerminalPayment> RowAsync(Guid id)
    {
        await using var ctx = Sqlite.NewContext();
        return await ctx.TerminalPayments.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    protected async Task<PaymentTerminal> TerminalRowAsync(Guid id)
    {
        await using var ctx = Sqlite.NewContext();
        return await ctx.PaymentTerminals.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    /// <summary>Inserts a Pending audit row directly (simulates a charge whose process died mid-flight).</summary>
    protected async Task<TerminalPayment> InsertPendingAsync(Guid terminalId, DateTime requestedAt, decimal amount = 1000m)
    {
        await using var ctx = Sqlite.NewContext();
        var row = new TerminalPayment
        {
            PaymentTerminalId = terminalId, TerminalName = "x", Purpose = TerminalPaymentPurpose.Sale, Amount = amount, Currency = "COP",
            Status = TerminalPaymentStatus.Pending, RequestedByName = "Ana", RequestedAt = requestedAt, CreatedAt = requestedAt
        };
        ctx.TerminalPayments.Add(row);
        await ctx.SaveChangesAsync();
        return row;
    }

    public void Dispose()
    {
        Db.Dispose();
        Sqlite.Dispose();
    }
}
