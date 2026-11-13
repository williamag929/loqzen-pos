using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Application.Services;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Infrastructure.Data.Services;
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
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private SqliteConnection? _connection;

    public TestClock Clock { get; } = new();
    public KitchenEventBus Bus { get; } = new();
    public KdsSettings KdsSettings { get; } = new() { WarningMinutes = 4, MaxPrepMinutes = 12, ArchiveDelaySeconds = 7, PollSeconds = 1 };
    public ComandaSettings ComandaSettings { get; } = new() { ConfirmationDelaySeconds = 30 };

    public ServiceProvider ServiceProvider { get; private set; } = null!;
    public LoqzenServer Server { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public int Port => Server.Port;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Same wire shape the server uses: camelCase + enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = CreateJson();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(KdsSettings);
        services.AddSingleton(ComandaSettings);
        services.AddSingleton<IKitchenEventBus>(Bus);
        services.AddDbContext<PosDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IKitchenService, KitchenService>();
        services.AddScoped<IComandaService, ComandaService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<ICategoryService, CategoryService>();
        ServiceProvider = services.BuildServiceProvider();

        using (var scope = ServiceProvider.CreateScope())
            await DbInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<PosDbContext>());

        Server = new LoqzenServer(ServiceProvider, new ServerSettings { Enabled = true, Port = 0, BindAddress = "127.0.0.1" });
        await Server.StartAsync();

        Client = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
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

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
