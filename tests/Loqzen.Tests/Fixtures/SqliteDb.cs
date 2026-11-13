using Core.Application.Services;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Fixtures;

/// <summary>
/// One REAL SQLite ":memory:" database (unique indexes, FKs, JSON columns all enforced) kept alive by a single
/// open connection. <see cref="NewContext"/> opens as many DbContexts as the test needs over the same data.
/// Use <see cref="Create"/> for an empty schema (EnsureCreated) or <see cref="CreateMigratedAsync"/> for the
/// production path (DbInitializer: migrations + seed).
/// </summary>
public sealed class SqliteDb : IDisposable
{
    private readonly SqliteConnection _conn;

    private SqliteDb()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
    }

    public static SqliteDb Create()
    {
        var db = new SqliteDb();
        using var ctx = db.NewContext();
        ctx.Database.EnsureCreated();
        return db;
    }

    public static async Task<SqliteDb> CreateMigratedAsync()
    {
        var db = new SqliteDb();
        await using var ctx = db.NewContext();
        await DbInitializer.InitializeAsync(ctx);
        return db;
    }

    public PosDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_conn).Options);

    public void Dispose() => _conn.Dispose();
}

/// <summary>Ready-made terminal identities for multi-register tests.</summary>
public static class Terminals
{
    public static TerminalContext Master(string code = "CAJA-01", string name = "Caja principal", Guid? id = null) =>
        TerminalContext.For(id ?? Guid.NewGuid(), code, name, TerminalKind.Master);

    public static TerminalContext Satellite(string code = "CAJA-02", string name = "Caja 2", Guid? id = null) =>
        TerminalContext.For(id ?? Guid.NewGuid(), code, name, TerminalKind.Satellite);
}
