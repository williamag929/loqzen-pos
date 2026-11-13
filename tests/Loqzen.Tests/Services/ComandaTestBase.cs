using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Services;

/// <summary>Shared setup: in-memory DB, controllable clock (30s delay), two products.</summary>
public abstract class ComandaTestBase : IDisposable
{
    protected readonly PosDbContext Db;
    protected readonly TestClock Clock = new();
    protected readonly ComandaService Service;
    protected readonly Product Burger;
    protected readonly Product Soda;

    protected ComandaTestBase()
    {
        Db = TestDbContextFactory.Create();
        Service = CreateService();

        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        var food = new Category { Name = "Comidas" };
        var drinks = new Category { Name = "Bebidas" };
        Burger = new Product { Sku = "B1", Name = "Hamburguesa", Price = 20000m, Tax = tax, Category = food };
        Soda = new Product { Sku = "S1", Name = "Gaseosa", Price = 5000m, Tax = tax, Category = drinks };
        Db.AddRange(tax, food, drinks, Burger, Soda);
        Db.SaveChanges();
    }

    /// <summary>Same DB/clock/settings as <see cref="Service"/>, optionally wired to a kitchen event bus.</summary>
    protected ComandaService CreateService(IKitchenEventBus? events = null) =>
        new(Db, new ComandaSettings { ConfirmationDelaySeconds = 30 }, Clock, events);

    protected async Task<Guid> OpenShiftAsync()
    {
        var shift = new CashShift { ShiftNumber = 1, CashierName = "Caja", Status = CashShiftStatus.Open };
        Db.CashShifts.Add(shift);
        await Db.SaveChangesAsync();
        return shift.Id;
    }

    public void Dispose() => Db.Dispose();
}
