using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Application.Sync;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Infrastructure.Data.Seed;
using Infrastructure.Data.Services;
using Infrastructure.Data.Sync;
using Infrastructure.Hardware.PaymentTerminals;
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
    /// <summary>Datáfonos: Enabled, a 5 s charge timeout and an instant Mock driver unless the test passes its own.</summary>
    public PaymentTerminalSettings PaymentTerminalSettings { get; }
    /// <summary>The real Mock driver, wrapped so tests can wait until a charge reached it / left it (see <see cref="DriverProbe"/>).</summary>
    public DriverProbe MockDriver { get; }
    /// <summary>The real JSON-TCP driver (Docker emulator protocol), wrapped like <see cref="MockDriver"/>.</summary>
    public DriverProbe JsonTcpDriver { get; }
    public TerminalContext Terminal { get; } = new();

    public ServiceProvider ServiceProvider { get; private set; } = null!;
    public LoqzenServer Server { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public int Port => Server.Port;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Same wire shape the server uses: camelCase + enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = CreateJson();

    /// <param name="apiKey">When set, the master requires <c>Authorization: Bearer &lt;apiKey&gt;</c> on the terminal routes.</param>
    /// <param name="paymentTerminals">Datáfono settings; defaults to Enabled, ChargeTimeoutSeconds = 5, MockDelaySeconds = 0.</param>
    public ServerFixture(string? apiKey = null, PaymentTerminalSettings? paymentTerminals = null)
    {
        TerminalSettings = new TerminalSettings { Code = MasterCode, Name = MasterName, Mode = TerminalKind.Master, ApiKey = apiKey };
        PaymentTerminalSettings = paymentTerminals ?? new PaymentTerminalSettings { Enabled = true, ChargeTimeoutSeconds = 5, MockDelaySeconds = 0 };
        MockDriver = new DriverProbe(new MockPaymentTerminalDriver(PaymentTerminalSettings, Clock));
        JsonTcpDriver = new DriverProbe(new JsonTcpPaymentTerminalDriver(PaymentTerminalSettings));
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
        services.AddSingleton(PaymentTerminalSettings);
        services.AddSingleton(Terminal);
        services.AddSingleton<ITerminalContext>(Terminal);
        services.AddSingleton<IKitchenEventBus>(Bus);
        services.AddDbContext<PosDbContext>(o => o.UseSqlite(_connection));
        // Datáfonos, registered like ServiceCollectionExtensions does: both drivers + factory as singletons, service scoped.
        services.AddSingleton<IPaymentTerminalDriver>(MockDriver);
        services.AddSingleton<IPaymentTerminalDriver>(JsonTcpDriver);
        services.AddSingleton<IPaymentTerminalDriverFactory, PaymentTerminalDriverFactory>();
        services.AddScoped<IPaymentTerminalService, PaymentTerminalService>();
        services.AddScoped<IKitchenService, KitchenService>();
        services.AddScoped<IComandaService, ComandaService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ITerminalService, TerminalService>();
        services.AddScoped<ICatalogSyncService, CatalogSyncService>();
        // The inbox applies ComandaClosedRemotely through the REAL comanda service of this scope (same DbContext, same
        // bus), exactly what the desktop's container resolves; explicit so the wiring cannot silently fall back to the
        // bus-less internal ComandaService.
        services.AddScoped<ISyncInboxService>(sp => new SyncInboxService(
            sp.GetRequiredService<PosDbContext>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ITerminalContext>(), sp.GetRequiredService<IComandaService>()));
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

    public async Task WithServiceAsync<TService>(Func<TService, Task> work) where TService : notnull
    {
        using var scope = ServiceProvider.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<TService>());
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
    /// Polls the database until <paramref name="condition"/> holds (real-time deadline). For observing a request that is
    /// still in flight (a charge waiting on the terminal). The fixture shares ONE SQLite connection between every
    /// DbContext, so a poll that lands while the request is executing a statement can fail with SQLITE_BUSY
    /// ("database is locked") when EF initialises the new context — that is treated as "not yet" and retried.
    /// </summary>
    public async Task<bool> WaitForDbAsync(Func<PosDbContext, Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (await WithDbAsync(condition)) return true;
            }
            catch (SqliteException)
            {
                // Collided with the in-flight request on the shared connection; retry.
            }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(50);
        }
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
/// Pass-through <see cref="IPaymentTerminalDriver"/> that counts charges and lets a test await the moment a charge
/// ENTERED the device driver (by then <c>PaymentTerminalService.ChargeAsync</c> has committed the Pending audit row
/// and will not touch the DbContext until the driver returns) and the moment it LEFT it. The wrapped driver is the real
/// one; nothing about the outcome is altered.
/// </summary>
public sealed class DriverProbe : IPaymentTerminalDriver
{
    private readonly IPaymentTerminalDriver _inner;
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _finished = new(0);
    private int _chargesStarted;
    private int _chargesFinished;

    public DriverProbe(IPaymentTerminalDriver inner) => _inner = inner;

    public PaymentTerminalProvider Provider => _inner.Provider;
    public int ChargesStarted => Volatile.Read(ref _chargesStarted);
    public int ChargesFinished => Volatile.Read(ref _chargesFinished);

    public async Task<TerminalChargeResult> ChargeAsync(TerminalChargeRequest request, IProgress<TerminalChargeProgress>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _chargesStarted);
        _started.Release();
        try
        {
            return await _inner.ChargeAsync(request, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Increment(ref _chargesFinished);
            _finished.Release();
        }
    }

    public Task<bool> PingAsync(string? address, CancellationToken ct = default) => _inner.PingAsync(address, ct);

    /// <summary>Waits (real time) for the next charge to reach the driver. Each call consumes one start.</summary>
    public Task<bool> WaitForChargeStartAsync(TimeSpan timeout) => _started.WaitAsync(timeout);

    /// <summary>Waits (real time) for the next charge to leave the driver. Each call consumes one end.</summary>
    public Task<bool> WaitForChargeEndAsync(TimeSpan timeout) => _finished.WaitAsync(timeout);
}

/// <summary>
/// Second register ("CAJA-02") with its own database and container, wired to a <see cref="ServerFixture"/> master
/// over real HTTP. Mirrors ServiceCollectionExtensions for a satellite: SyncSettings forced to the master's inbox,
/// HttpOutboxSink as the IOutboxSink, MasterApiClient bound to TerminalSettings.MasterUrl/ApiKey, and the shared
/// comandas/KDS wiring: <see cref="IComandaService"/> → <see cref="RemoteComandaService"/>, <see cref="IKitchenService"/> →
/// <see cref="RemoteKitchenService"/>, its OWN <see cref="KitchenEventBus"/> and a <see cref="MasterKitchenSocketClient"/>
/// (registered, NOT started: tests call <c>SocketClient.Start()</c> when they want the /ws/kds feed).
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
    /// <summary>This register's own in-process bus (the master's events reach it only through <see cref="SocketClient"/>).</summary>
    public KitchenEventBus Bus { get; } = new();
    public HttpClient Http { get; }
    public ServiceProvider ServiceProvider { get; private set; } = null!;

    public IMasterApiClient MasterApi => ServiceProvider.GetRequiredService<IMasterApiClient>();
    /// <summary>/ws/kds client bound to the master's URL; idle until a test calls <c>Start()</c>.</summary>
    public MasterKitchenSocketClient SocketClient => ServiceProvider.GetRequiredService<MasterKitchenSocketClient>();

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
        services.AddSingleton<IKitchenEventBus>(sat.Bus);
        services.AddSingleton(sat.Http);
        services.AddDbContext<PosDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<IOutboxSink>(sp => new HttpOutboxSink(sp.GetRequiredService<HttpClient>(), sat.SyncSettings, sat.Terminal));
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        services.AddSingleton<IMasterApiClient>(sp => new MasterApiClient(sp.GetRequiredService<HttpClient>(), sat.TerminalSettings));
        services.AddScoped<ITerminalService, TerminalService>();
        services.AddScoped<ICatalogSyncService, CatalogSyncService>();
        // Registered everywhere, like the desktop; a satellite Rejects every envelope. No comanda service handed in: the
        // satellite's IComandaService is the remote one and must never be reached from an inbox.
        services.AddScoped<ISyncInboxService>(sp => new SyncInboxService(
            sp.GetRequiredService<PosDbContext>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ITerminalContext>()));
        services.AddScoped<ICashShiftService, CashShiftService>();
        services.AddScoped<ISalesService, SalesService>();
        services.AddScoped<IBillPaymentService, BillPaymentService>();
        // Shared comandas / KDS (ServiceCollectionExtensions, IsSatellite branch): the master owns them.
        services.AddScoped<IComandaService, RemoteComandaService>();
        services.AddScoped<IKitchenService, RemoteKitchenService>();
        services.AddSingleton(sp => new MasterKitchenSocketClient(sat.TerminalSettings, sp.GetRequiredService<IKitchenEventBus>(), sp.GetRequiredService<TimeProvider>()));
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

    /// <summary>
    /// Waits (real time) until an event matching <paramref name="predicate"/> lands on THIS register's bus. Subscribes
    /// first, then runs <paramref name="trigger"/>, so an event published synchronously inside the trigger is not missed.
    /// </summary>
    public async Task<KitchenEvent?> WaitForBusEventAsync(Func<KitchenEvent, bool> predicate, Func<Task> trigger, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<KitchenEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = Bus.Subscribe(e => { if (predicate(e)) tcs.TrySetResult(e); });
        await trigger();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the /ws/kds client if a test started it (StopAsync is a no-op otherwise).
        if (ServiceProvider is not null)
            await SocketClient.StopAsync();
        Http.Dispose();
        if (ServiceProvider is not null) await ServiceProvider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
