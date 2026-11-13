using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Integration;

/// <summary>
/// Same flows against REAL SQLite (in-memory connection): catches query translation issues
/// (Contains over arrays, DateTime comparisons, decimal handling) that the InMemory provider hides.
/// </summary>
public class ComandaSqliteTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();

    public ComandaSqliteTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task FullFlow_OpenAddConfirmPay_And_CashCount_OnSqlite()
    {
        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        var burger = new Product { Sku = "B1", Name = "Hamburguesa", Price = 20000m, Tax = tax };
        _db.AddRange(tax, burger, new DiningTable { Name = "Mesa 1" });
        await _db.SaveChangesAsync();

        var shifts = new CashShiftService(_db);
        var shift = await shifts.OpenShiftAsync("Caja", 50000m);
        var svc = new ComandaService(_db, new ComandaSettings { ConfirmationDelaySeconds = 30 }, _clock);

        var tables = await svc.GetTablesAsync();
        var c = await svc.OpenComandaAsync("Mesa 1", "Ana", tables.Single().Id);
        (await svc.GetTablesAsync()).Single().OpenComandaId.Should().Be(c.Id);

        await svc.AddItemAsync(c.Id, burger.Id);
        _clock.Advance(TimeSpan.FromSeconds(31));
        (await svc.ConfirmDueItemsAsync()).Should().Be(1);
        (await svc.GetActiveComandasAsync()).Should().ContainSingle();

        var pay = await svc.CloseAndPayAsync(c.Id, shift.Id, PaymentMethod.Cash, 30000m);
        pay.Success.Should().BeTrue(pay.ErrorMessage);

        var summary = await shifts.GetCloseSummaryAsync();
        summary.CashSales.Should().Be(23800m);
        summary.ExpectedCash.Should().Be(73800m);

        var closed = await shifts.CloseShiftWithCountAsync(new[] { new CashCountLineDto { Denomination = 50000m, Quantity = 1 }, new CashCountLineDto { Denomination = 20000m, Quantity = 1 } });
        closed.Difference.Should().Be(-3800m);
    }
}
