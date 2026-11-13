using Core.Application.DTOs;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Integration;

/// <summary>
/// Users + manager authorization against REAL SQLite (migrated by DbInitializer): proves the seeded
/// users exist and the Users/ManagerOverrides queries translate to SQL (InMemory does not validate that).
/// </summary>
public class UsersSqliteTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();

    public UsersSqliteTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_conn).Options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task SeededAdmin_SignsIn_AuthorizesReturn_AndOverrideIsReadBack_OnSqlite()
    {
        await DbInitializer.InitializeAsync(_db);
        (await _db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        var users = new UserService(_db, _clock);
        var auth = new ManagerAuthorizationService(_db, new AuthorizationSettings(), _clock);

        // Seeded users (ordered by role desc → SQL ORDER BY on an enum column).
        var all = await users.GetAllAsync();
        all.Select(u => u.Name).Should().Equal("Administrador", "Cajero", "Mesero");

        // PIN login of the seeded Admin.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var admin = await users.AuthenticateByPinAsync("1234");
        admin.Should().NotBeNull();
        admin!.Role.Should().Be(UserRole.Admin);
        admin.LastLoginAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        (await users.AuthenticateByPinAsync("1111"))!.Role.Should().Be(UserRole.Cashier);
        (await users.AuthenticateByPinAsync("0000")).Should().BeNull();

        // Cashier PIN cannot authorize; Admin PIN can.
        var request = new AuthorizationRequest(AuthorizedAction.ReturnItem, "Devolución Hamburguesa", Amount: 23800m, Reference: "CMD-1",
            RequestedByUserId: all.Single(u => u.Name == "Cajero").Id, RequestedByName: "Cajero");
        var denied = () => auth.AuthorizeWithPinAsync(request, "1111");
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();

        _clock.Advance(TimeSpan.FromMinutes(1));
        var dto = await auth.AuthorizeWithPinAsync(request, "1234");

        // Read back through a fresh query (real SELECT with decimal + enum + nullable Guid columns).
        var stored = await _db.ManagerOverrides.AsNoTracking().SingleAsync(o => o.Id == dto.Id);
        stored.Action.Should().Be(AuthorizedAction.ReturnItem);
        stored.ManagerUserId.Should().Be(admin.Id);
        stored.ManagerName.Should().Be("Administrador");
        stored.RequestedByName.Should().Be("Cajero");
        stored.Amount.Should().Be(23800m);
        stored.Reference.Should().Be("CMD-1");
        stored.AuthorizedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);

        var recent = await auth.GetRecentAsync();
        recent.Should().ContainSingle().Which.Id.Should().Be(dto.Id);
        (await _db.ManagerOverrides.CountAsync(o => o.Action == AuthorizedAction.ReturnItem)).Should().Be(1);
    }

    [Fact]
    public async Task LastAuthorizerGuard_And_PinUniqueness_TranslateToSql()
    {
        await DbInitializer.InitializeAsync(_db);
        var users = new UserService(_db, _clock);
        var admin = (await users.GetAllAsync()).Single(u => u.Role == UserRole.Admin);

        // "u.Role >= UserRole.Manager" comparison on the enum column.
        var act = () => users.UpdateAsync(admin.Id, admin.Name, admin.Role, isActive: false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorizar*");

        // Duplicate PIN check reads hashes of active users.
        var dup = () => users.CreateAsync("Otro", "1111", UserRole.Waiter);
        await dup.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PIN*");

        var gerente = await users.CreateAsync("Gerente", "5555", UserRole.Manager);
        (await users.UpdateAsync(admin.Id, admin.Name, admin.Role, isActive: false)).IsActive.Should().BeFalse("another authorizer exists");
        (await users.GetAllAsync()).Select(u => u.Name).Should().Equal("Gerente", "Cajero", "Mesero");
        (await users.GetAllAsync(includeInactive: true)).Should().HaveCount(4);
        (await _db.KitchenOrderItems.CountAsync(i => i.IsCancelled)).Should().Be(0, "IsCancelled column exists after migration");
        gerente.CanAuthorize.Should().BeTrue();
    }
}
