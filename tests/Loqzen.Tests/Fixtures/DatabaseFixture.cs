using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Fixtures;

/// <summary>
/// Provides a fresh in-memory database for each test class.
/// Uses EF Core InMemory provider to avoid SQLite decimal sum limitations.
/// </summary>
public class DatabaseFixture : IDisposable
{
    public PosDbContext DbContext { get; }
    private readonly string _dbName;

    public DatabaseFixture()
    {
        _dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;

        DbContext = new PosDbContext(options);
        DbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// Creates a new DbContext instance with its own tracking scope.
    /// </summary>
    public PosDbContext CreateNewContext()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        return new PosDbContext(options);
    }

    public void Dispose()
    {
        DbContext.Dispose();
    }
}

/// <summary>
/// Test database factory for creating isolated in-memory databases per test.
/// Uses EF Core InMemory provider for full LINQ support including decimal aggregates.
/// </summary>
public static class TestDbContextFactory
{
    public static PosDbContext Create()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var context = new PosDbContext(options);
        context.Database.EnsureCreated();

        return context;
    }
}
