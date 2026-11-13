using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Presentation.Server.Dtos;
using Presentation.Server.Endpoints;
using Presentation.Server.Logging;
using Presentation.Server.Realtime;

namespace Presentation.Server;

/// <summary>
/// Kestrel host embedded in the desktop app (one per terminal). Serves the browser KDS (/kds),
/// REST for KDS/comandera tablets and the WebSocket hub (/ws/kds) fed by <c>IKitchenEventBus</c>.
/// Resolves application services (IKitchenService, IComandaService, ...) from <c>appServices</c>,
/// one DI scope per request, so it shares the desktop's SQLite DB and settings.
/// </summary>
public sealed class LoqzenServer : IAsyncDisposable
{
    private const string NodeName = "loqzen-pos";
    private static readonly string Version = ResolveVersion();

    private readonly IServiceProvider _appServices;
    private readonly ServerSettings _settings;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private WebApplication? _app;
    private KdsSocketHub? _hub;
    private IDisposable? _busSubscription;

    public LoqzenServer(IServiceProvider appServices, ServerSettings settings)
    {
        _appServices = appServices ?? throw new ArgumentNullException(nameof(appServices));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>True between StartAsync and StopAsync.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Port actually bound (differs from settings when Port = 0, used by tests).</summary>
    public int Port { get; private set; }

    /// <summary>Open WebSocket clients right now.</summary>
    public int ConnectedClients => _hub?.Count ?? 0;

    /// <summary>Human URL for the browser KDS using the first LAN IPv4, e.g. http://192.168.1.20:5055/kds</summary>
    public string KdsUrl => $"http://{LanAddress()}:{Port}/kds";

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;

            var hub = new KdsSocketHub();
            var app = BuildApp(hub);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                hub.Dispose();
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Port = ResolveBoundPort(app) ?? _settings.Port;
            _busSubscription = _appServices.GetService<IKitchenEventBus>()?.Subscribe(hub.OnKitchenEvent);
            _hub = hub;
            _app = app;
            IsRunning = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is null) { IsRunning = false; return; }

            _busSubscription?.Dispose();
            _busSubscription = null;

            if (_hub is not null)
                await _hub.CloseAllAsync().ConfigureAwait(false);

            try { await _app.StopAsync(ct).ConfigureAwait(false); }
            finally
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _hub?.Dispose();
                _app = null;
                _hub = null;
                IsRunning = false;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    // ─── Host construction ────────────────────────────────────────────────────

    private WebApplication BuildApp(KdsSocketHub hub)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(LoqzenServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });

        // The desktop's appsettings.json / environment must not leak in (e.g. a "Kestrel" or "Logging" section).
        builder.Configuration.Sources.Clear();

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.Logging.AddProvider(new ErrorConsoleLoggerProvider());

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(ResolveBindAddress(_settings.BindAddress), _settings.Port);
        });

        // Tablets are long-lived WebSocket requests: don't make StopAsync wait 30 s for them.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));
        // The default ConsoleLifetime hooks SIGTERM/SIGINT and "handles" them by stopping only this web host,
        // so the desktop app silently ignored kill / OS shutdown. The desktop owns the process lifetime.
        builder.Services.AddSingleton<IHostLifetime, EmbeddedHostLifetime>();

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();

        app.UseCors();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.MapGet("/health", () =>
        {
            var now = _appServices.ResolveClock().GetUtcNow().UtcDateTime;
            return Results.Ok(new HealthDto("ok", NodeName, Version, now, hub.Count));
        });

        app.MapGet("/", ServeKdsPage);
        app.MapGet("/kds", ServeKdsPage);
        app.MapGet("/comandera", ServeComanderaPage);

        app.MapKdsEndpoints(_appServices, hub);
        app.MapCatalogEndpoints(_appServices);
        app.MapComandaEndpoints(_appServices);

        return app;
    }

    private static IResult ServeKdsPage() => ServeEmbeddedPage("kds.html");
    private static IResult ServeComanderaPage() => ServeEmbeddedPage("comandera.html");

    private static IResult ServeEmbeddedPage(string fileName) =>
        Results.Text(EmbeddedPages.GetOrAdd(fileName, ReadEmbeddedPage), "text/html; charset=utf-8");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> EmbeddedPages = new(StringComparer.OrdinalIgnoreCase);

    private static string ReadEmbeddedPage(string fileName)
    {
        var asm = typeof(LoqzenServer).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null) return $"<!doctype html><html lang=\"es\"><body>{fileName} no está embebido.</body></html>";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads the address Kestrel actually bound from <see cref="IServerAddressesFeature"/>; with
    /// settings.Port = 0 the OS picks an ephemeral port and this is the only way to learn it.
    /// </summary>
    private static int? ResolveBoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null) return null;
        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }
        return null;
    }

    /// <summary>"0.0.0.0" (default) = every interface; unparsable values also fall back to every interface.</summary>
    private static IPAddress ResolveBindAddress(string? bindAddress) =>
        IPAddress.TryParse(bindAddress, out var ip) ? ip : IPAddress.Any;

    private static string ResolveVersion()
    {
        var asm = typeof(LoqzenServer).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>First non-loopback IPv4 of this machine, or 127.0.0.1.</summary>
    public static string LanAddress()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ua.Address.ToString();
            }
        }
        catch { /* fall through */ }
        return "127.0.0.1";
    }
}

/// <summary>Host lifetime that never touches process signals: the embedding desktop app decides when to stop.</summary>
internal sealed class EmbeddedHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
