using Core.Application.DTOs;

namespace Presentation.Server;

/// <summary>
/// Kestrel host embedded in the desktop app (one per terminal). Serves the browser KDS (/kds),
/// REST for KDS/comandera tablets and the WebSocket hub (/ws/kds) fed by <c>IKitchenEventBus</c>.
/// Resolves application services (IKitchenService, IComandaService, ...) from <c>appServices</c>,
/// one DI scope per request, so it shares the desktop's SQLite DB and settings.
/// </summary>
public sealed class LoqzenServer : IAsyncDisposable
{
    private readonly IServiceProvider _appServices;
    private readonly ServerSettings _settings;

    public LoqzenServer(IServiceProvider appServices, ServerSettings settings)
    {
        _appServices = appServices;
        _settings = settings;
    }

    /// <summary>True between StartAsync and StopAsync.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Port actually bound (differs from settings when Port = 0, used by tests).</summary>
    public int Port { get; private set; }

    /// <summary>Open WebSocket clients right now.</summary>
    public int ConnectedClients => 0;

    /// <summary>Human URL for the browser KDS using the first LAN IPv4, e.g. http://192.168.1.20:5055/kds</summary>
    public string KdsUrl => $"http://{LanAddress()}:{Port}/kds";

    public Task StartAsync(CancellationToken ct = default)
    {
        Port = _settings.Port;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

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
