using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Application.Sync;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Infrastructure.Data.Seed;
using Infrastructure.Data.Services;
using Infrastructure.Data.Sync;
using Loqzen.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Server;

namespace Loqzen.Tests.Server;

/// <summary>
/// Boots the embedded Kestrel server (<see cref="LoqzenServer"/>) on an ephemeral loopback port against a
/// REAL SQLite ":memory:" database (one open connection for the lifetime of the fixture, migrated and
/// seeded by <see cref="DbInitializer"/>). The application container mirrors the desktop's registrations:
/// controlled <see cref="TestClock"/>, settings singletons, the in-process <see cref="KitchenEventBus"/>
/// and the scoped EF-backed services the endpoints resolve per request.
/// The container is the MASTER register "CAJA-01" (<see cref="TerminalContext"/> set the way App.axaml.cs does
/// it, through <see cref="ITerminalService.EnsureRegisteredAsync"/>). <see cref="CreateSatelliteAsync"/> builds a
/// second, independent container (own SQLite) configured as a satellite pointing at this master.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public const string MasterCode = "CAJA-01";
    public const string MasterName = "Caja principal";

    private SqliteConnection? _connection;

    public TestClock Clock { get; } = new();
    public KitchenEventBus Bus { get; } = new();
    public KdsSettings KdsSettings { get; } = new() { WarningMinutes = 4, MaxPrepMinutes = 12, ArchiveDelaySeconds = 7, PollSeconds = 1 };
    public ComandaSettings ComandaSettings { get; } = new() { ConfirmationDelaySeconds = 30 };
    public CashShiftSettings CashShiftSettings { get; } = new();
    public TerminalSettings TerminalSettings { get; }
    public TerminalContext Terminal { get; } = new();

    public ServiceProvider ServiceProvider { get; private set; } = null!;
    public LoqzenServer Server { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public int Port => Server.Port;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Same wire shape the server uses: camelCase + enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = CreateJson();

    /// <param name="apiKey">When set, the master requires <c>Authorization: Bearer &lt;apiKey&gt;</c> on the terminal routes.</param>
    public ServerFixture(string? apiKey = null)
    {
        TerminalSettings = new TerminalSettings { Code = MasterCode, Name = MasterName, Mode = TerminalKind.Master, ApiKey = apiKey };
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(KdsSettings);
        services.AddSingleton(ComandaSettings);
        services.AddSingleton(CashShiftSettings);
        services.AddSingleton(TerminalSettings);
        services.AddSingleton(Terminal);
        services.AddSingleton<ITerminalContext>(Terminal);
        services.AddSingleton<IKitchenEventBus>(Bus);
        services.AddDbContext<PosDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IKitchenService, KitchenService>();
        services.AddScoped<IComandaService, ComandaService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ITerminalService, TerminalService>();
        services.AddScoped<ICatalogSyncService, CatalogSyncService>();
        services.AddScoped<ISyncInboxService, SyncInboxService>();
        services.AddScoped<ICashShiftService, CashShiftService>();
        services.AddScoped<ISalesService, SalesService>();
        services.AddScoped<IBillPaymentService, BillPaymentService>();
        ServiceProvider = services.BuildServiceProvider();

        using (var scope = ServiceProvider.CreateScope())
        {
            await DbInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>());
            // Same as App.axaml.cs RegisterTerminal(): upsert the own row and fill the context.
            var terminals = scope.ServiceProvider.GetRequiredService<ITerminalService>();
            Terminal.Set(await terminals.EnsureRegisteredAsync(TerminalSettings.Code, TerminalSettings.Name, TerminalSettings.Mode, "tests"));
        }

        Server = new LoqzenServer(ServiceProvider, new ServerSettings { Enabled = true, Port = 0, BindAddress = "127.0.0.1" });
        await Server.StartAsync();

        Client = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(TerminalSettings.ApiKey))
            Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TerminalSettings.ApiKey);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Server is not null) await Server.DisposeAsync();
        if (ServiceProvider is not null) await ServiceProvider.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="work"/> against a fresh DbContext scope (the same SQLite the server uses).</summary>
    public async Task<T> WithDbAsync<T>(Func<PosDbContext, Task<T>> work)
    {
        using var scope = ServiceProvider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    public async Task WithDbAsync(Func<PosDbContext, Task> work)
    {
        using var scope = ServiceProvider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    /// <summary>Resolves <typeparamref name="TService"/> from the master container in a fresh scope.</summary>
    public async Task<T> WithServiceAsync<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        using var scope = ServiceProvider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"Empty JSON body (HTTP {(int)response.StatusCode}).");
    }

    public async Task<JsonNode> GetJsonAsync(string path)
    {
        var response = await Client.GetAsync(path);
        return await ReadJsonAsync(response);
    }

    public Task<HttpResponseMessage> PostJsonAsync(string path, object? body) =>
        body is null
            ? Client.PostAsync(path, content: null)
            : Client.PostAsJsonAsync(path, body, Json);

    /// <summary>A client with no Authorization header (or a wrong one) for the API-key tests.</summary>
    public HttpClient AnonymousClient(string? bearer = null)
    {
        var c = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        if (bearer is not null)
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    public Uri WsUri(string? station = null) =>
        new($"ws://127.0.0.1:{Port}/ws/kds" + (station is null ? "" : "?station=" + Uri.EscapeDataString(station)));

    /// <summary>
    /// Short poll with a deadline. Only for observing the real socket/hub state (connection counts), which
    /// is driven by the OS and Kestrel, not by the application clock.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>
    /// Builds a SATELLITE register container pointing at this master: its own SQLite ":memory:" (migrated; seeded
    /// with the demo data like the desktop does when <paramref name="seedDemoData"/> is true), the SAME TestClock,
    /// <see cref="TerminalSettings"/> in Satellite mode with MasterUrl = this BaseUrl, <see cref="SyncSettings"/> aimed at
    /// <c>/api/sync/inbox</c>, the real <see cref="HttpOutboxSink"/> + <see cref="OutboxDispatcher"/> and <see cref="MasterApiClient"/>.
    /// </summary>
    public Task<SatelliteContainer> CreateSatelliteAsync(string code = SatelliteContainer.DefaultCode, string name = SatelliteContainer.DefaultName, bool seedDemoData = false) =>
        SatelliteContainer.CreateAsync(this, code, name, seedDemoData);

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>
/// Second register ("CAJA-02") with its own database and container, wired to a <see cref="ServerFixture"/> master
/// over real HTTP. Mirrors ServiceCollectionExtensions for a satellite: SyncSettings forced to the master's inbox,
/// HttpOutboxSink as the IOutboxSink, MasterApiClient bound to TerminalSettings.MasterUrl/ApiKey.
/// </summary>
public sealed class SatelliteContainer : IAsyncDisposable
{
    public const string DefaultCode = "CAJA-02";
    public const string DefaultName = "Caja 2";

    private readonly SqliteConnection _connection;

    public ServerFixture Master { get; }
    public TestClock Clock => Master.Clock;
    public TerminalSettings TerminalSettings { get; }
    public SyncSettings SyncSettings { get; }
    public TerminalContext Terminal { get; } = new();
    public CashShiftSettings CashShiftSettings { get; } = new();
    public HttpClient Http { get; }
    public ServiceProvider ServiceProvider { get; private set; } = null!;

    public IMasterApiClient MasterApi => ServiceProvider.GetRequiredService<IMasterApiClient>();

    private SatelliteContainer(ServerFixture master, string code, string name, SqliteConnection connection)
    {
        Master = master;
        _connection = connection;
        TerminalSettings = new TerminalSettings
        {
            Code = code, Name = name, Mode = TerminalKind.Satellite,
            MasterUrl = master.BaseUrl, ApiKey = master.TerminalSettings.ApiKey
        };
        SyncSettings = new SyncSettings
        {
            Enabled = true,
            WebhookUrl = master.BaseUrl + "/api/sync/inbox",
            ApiKey = master.TerminalSettings.ApiKey,
            BatchSize = 50,
            MaxRetries = 10,
            TimeoutSeconds = 5
        };
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    internal static async Task<SatelliteContainer> CreateAsync(ServerFixture master, string code, string name, bool seedDemoData)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var sat = new SatelliteContainer(master, code, name, connection);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(master.Clock);
        services.AddSingleton(sat.CashShiftSettings);
        services.AddSingleton(sat.TerminalSettings);
        services.AddSingleton(sat.SyncSettings);
        services.AddSingleton(sat.Terminal);
        services.AddSingleton<ITerminalContext>(sat.Terminal);
        services.AddSingleton(sat.Http);
        services.AddDbContext<PosDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<IOutboxSink>(sp => new HttpOutboxSink(sp.GetRequiredService<HttpClient>(), sat.SyncSettings, sat.Terminal));
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        services.AddSingleton<IMasterApiClient>(sp => new MasterApiClient(sp.GetRequiredService<HttpClient>(), sat.TerminalSettings));
        services.AddScoped<ITerminalService, TerminalService>();
        services.AddScoped<ICatalogSyncService, CatalogSyncService>();
        services.AddScoped<ISyncInboxService, SyncInboxService>(); // registered everywhere, like the desktop; a satellite Rejects
        services.AddScoped<ICashShiftService, CashShiftService>();
        services.AddScoped<ISalesService, SalesService>();
        services.AddScoped<IBillPaymentService, BillPaymentService>();
        sat.ServiceProvider = services.BuildServiceProvider();

        using (var scope = sat.ServiceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            if (seedDemoData)
                await DbInitializer.InitializeAsync(db); // exactly what the desktop does on every install
            else
                await db.Database.MigrateAsync();        // empty satellite: the catalog comes from the master
            var terminals = scope.ServiceProvider.GetRequiredService<ITerminalService>();
            sat.Terminal.Set(await terminals.EnsureRegisteredAsync(code, name, TerminalKind.Satellite, "tests"));
        }
        return sat;
    }

    public async Task<T> WithDbAsync<T>(Func<PosDbContext, Task<T>> work)
    {
        using var scope = ServiceProvider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    public async Task WithDbAsync(Func<PosDbContext, Task> work)
    {
        using var scope = ServiceProvider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    public async Task<T> WithServiceAsync<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        using var scope = ServiceProvider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task WithServiceAsync<TService>(Func<TService, Task> work) where TService : notnull
    {
        using var scope = ServiceProvider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>GET /api/catalog/snapshot from the master and apply it locally (what SatelliteSyncWorker does each tick).</summary>
    public async Task<CatalogApplyResult> PullCatalogAsync()
    {
        var snapshot = await MasterApi.GetSnapshotAsync();
        using var scope = ServiceProvider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICatalogSyncService>().ApplySnapshotAsync(snapshot);
        await scope.ServiceProvider.GetRequiredService<ITerminalService>().MarkCatalogPulledAsync();
        return result;
    }

    public Task<TerminalDto> HeartbeatAsync(string? appVersion = "tests") =>
        MasterApi.HeartbeatAsync(new TerminalHeartbeatRequest(TerminalSettings.Code, TerminalSettings.Name, TerminalKind.Satellite, appVersion));

    /// <summary>One dispatcher pass over this register's Outbox (fresh scope: the dispatcher owns a DbContext).</summary>
    public async Task<OutboxDispatchResult> DispatchAsync(CancellationToken ct = default)
    {
        using var scope = ServiceProvider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchPendingAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        if (ServiceProvider is not null) await ServiceProvider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
