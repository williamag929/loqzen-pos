using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Loqzen.Tests.Integration;

/// <summary>DbInitializer against real SQLite files: fresh DB, legacy EnsureCreated DB, idempotent restart.</summary>
public class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loqzen-mig-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "pos.db");

    public MigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private PosDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite($"Data Source={DbPath}").Options);

    [Fact]
    public async Task FreshDatabase_IsMigratedAndSeeded()
    {
        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);

        await using var check = NewContext();
        (await check.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await check.DiningTables.CountAsync()).Should().Be(14);
        (await check.Products.AnyAsync()).Should().BeTrue();
        (await check.Users.CountAsync()).Should().Be(3, "default users are seeded");
        (await check.Users.AnyAsync(u => u.Role == UserRole.Admin)).Should().BeTrue("there is always a way to sign in as admin");
    }

    [Fact]
    public async Task SecondStartup_IsIdempotent()
    {
        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);
        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);

        await using var check = NewContext();
        (await check.DiningTables.CountAsync()).Should().Be(14, "seed must not duplicate");
        Directory.GetFiles(_dir, "*.bak").Should().BeEmpty("migrated DBs are not backed up");
    }

    [Fact]
    public async Task DatabaseAtInitialCreate_IsUpgraded_KeepingData_NoBackup()
    {
        // Simulates a user DB created by the previous release (only InitialCreate applied).
        await using (var old = NewContext())
        {
            await old.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .MigrateAsync("20261113033350_InitialCreate");
            old.DiningTables.Add(new Core.Domain.Entities.DiningTable { Name = "Terraza" });
            await old.SaveChangesAsync();
        }

        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);

        await using var check = NewContext();
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await check.DiningTables.AnyAsync(t => t.Name == "Terraza")).Should().BeTrue("existing data is preserved");
        Directory.GetFiles(_dir, "*.bak").Should().BeEmpty("a migrated DB must not be backed up/recreated");
        (await check.ComandaItems.CountAsync(i => i.IsOnHold)).Should().Be(0, "new column exists");
        (await check.Users.CountAsync()).Should().Be(3, "users are seeded on upgraded DBs too");
        (await check.Users.AnyAsync(u => u.Role == UserRole.Admin && u.IsActive)).Should().BeTrue();
        (await check.KitchenOrderItems.CountAsync(i => i.IsCancelled)).Should().Be(0, "IsCancelled column exists after upgrade");
    }

    [Fact]
    public async Task DatabaseAtManagerOverrideConsumed_IsUpgradedToCashOperations_AndGetsBillers()
    {
        // Simulates a user DB from the release before "cuadre de caja de referencia + recaudos".
        await using (var old = NewContext())
        {
            await old.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .MigrateAsync("20261113054830_ManagerOverrideConsumed");
            // Raw SQL: the current model has columns the old schema lacks.
            await old.Database.ExecuteSqlRawAsync(
                "INSERT INTO CashShifts (Id, ShiftNumber, CashierId, CashierName, OpenedAt, ClosedAt, OpeningAmount, ClosingAmount, ExpectedAmount, Status, CreatedAt) " +
                "VALUES ('6F9619FF-8B86-D011-B42D-00C04FC964FF', 1, '00000000-0000-0000-0000-000000000000', 'Legacy', '2026-11-01 08:00:00', '2026-11-01 18:00:00', '50000.0', '50000.0', '50000.0', 1, '2026-11-01 08:00:00')");
        }

        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);

        await using var check = NewContext();
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await check.Database.GetAppliedMigrationsAsync()).Should().Contain("20261113062640_CashOperations");
        (await check.Billers.CountAsync()).Should().Be(5, "default billers are seeded on upgraded DBs");
        (await check.Billers.CountAsync(b => b.IsActive)).Should().Be(5);
        var legacy = await check.CashShifts.SingleAsync();
        legacy.CashierName.Should().Be("Legacy", "existing shifts are preserved");
        legacy.FloatLeftAmount.Should().Be(0m, "new column defaults to 0");
        legacy.WasBlindCount.Should().BeFalse();
        legacy.DifferenceOverrideId.Should().BeNull();
        (await check.CashMovements.CountAsync()).Should().Be(0, "new tables exist");
        (await check.BillPayments.CountAsync()).Should().Be(0);
        (await check.PartialCashCounts.CountAsync()).Should().Be(0);
        Directory.GetFiles(_dir, "*.bak").Should().BeEmpty();

        // Legacy close (no ClosedByName) → suggested opening falls back to the default float.
        (await new Infrastructure.Data.Services.CashShiftService(check).GetSuggestedOpeningAmountAsync()).Should().Be(200000m);
    }

    [Fact]
    public async Task FreshDatabase_SeedsBillers_Once()
    {
        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);
        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);

        await using var check = NewContext();
        (await check.Billers.CountAsync()).Should().Be(5, "seed must not duplicate billers");
    }

    [Fact]
    public async Task LegacyEnsureCreatedDatabase_IsBackedUp_AndRecreated()
    {
        // Simulate a pre-migrations DB: has tables but no __EFMigrationsHistory.
        await using (var legacy = NewContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE Comandas; DROP TABLE ComandaItems;");
        }
        SqliteConnection.ClearAllPools();

        await using (var db = NewContext()) await DbInitializer.InitializeAsync(db);

        Directory.GetFiles(_dir, "pos.db.legacy-*.bak").Should().ContainSingle("old data is preserved, never deleted");
        await using var check = NewContext();
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await check.Comandas.CountAsync()).Should().Be(0, "new tables exist after migrating");
    }
}
