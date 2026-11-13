using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Services;
using Presentation.Desktop.ViewModels;
using Presentation.Desktop.Views;
using Presentation.Server;

namespace Presentation.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Informational version of this build ("1.2.0"), reported to the master in heartbeats.</summary>
    public static string AppVersion { get; } = ResolveAppVersion();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ── Build Configuration ──────────────────────────────────────────────────
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        var basePath = AppContext.BaseDirectory;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false)
            .Build();

        // ── Build DI Container ───────────────────────────────────────────────────
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLoqzenPosServices(configuration);
        Services = serviceCollection.BuildServiceProvider();

        // ── Initialize Database ──────────────────────────────────────────────────
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            DbInitializer.InitializeAsync(db).GetAwaiter().GetResult();
        }

        // ── This register's identity (master / satellite) ────────────────────────
        // Upserts the Terminals row and fills TerminalContext so every service stamps TerminalId/Code.
        // A failure must never keep the POS closed: it is logged and shown in the 🖥️ nav-bar chip.
        RegisterTerminal();

        // ── Embedded LAN server (browser KDS / comandera tablets) ───────────────
        // Started in the background so the window opens immediately. A port already in use must
        // never prevent the POS from opening: the failure is logged and shown in the nav-bar chip.
        var serverSettings = Services.GetRequiredService<Core.Application.DTOs.ServerSettings>();
        var serverStatus = Services.GetRequiredService<LanServerStatus>();
        LoqzenServer? server = null;
        if (serverSettings.Enabled)
        {
            server = Services.GetRequiredService<LoqzenServer>();
            StartLanServer(server, serverStatus);
        }

        // ── Outbox sync worker (ERP webhook; off by default, still reads the counts for the UI) ──
        var syncWorker = Services.GetRequiredService<OutboxSyncWorker>();
        syncWorker.Start();

        // ── Satellite worker (heartbeat + catalog pull from the master; no-op on a master) ──
        var satelliteWorker = Services.GetRequiredService<SatelliteSyncWorker>();
        satelliteWorker.Start();

        // ── Create Main Window with DI-resolved ViewModel ───────────────────────
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => syncWorker.Dispose();
            desktop.Exit += (_, _) => satelliteWorker.Dispose();
            if (server != null)
                desktop.Exit += (_, _) => StopLanServer(server);

            var main = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = main };

            // Optional start tab (kiosk screens / demos): LOQZEN_START_VIEW=kds|comandas|caja|recaudos|maestros
            // kds / comandas / maestros are ignored on a satellite (those screens live on the master).
            switch (Environment.GetEnvironmentVariable("LOQZEN_START_VIEW")?.ToLowerInvariant())
            {
                case "kds" when main.IsMaster: _ = main.ShowKdsCommand.ExecuteAsync(null); break;
                case "comandas" when main.IsMaster: _ = main.ShowComandasCommand.ExecuteAsync(null); break;
                case "caja": _ = main.ShowCashShiftCommand.ExecuteAsync(null); break;
                case "recaudos": _ = main.ShowBillPaymentsCommand.ExecuteAsync(null); break;
                case "maestros" when main.IsMaster: _ = main.ShowMastersCommand.ExecuteAsync(null); break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Synchronous on purpose: services resolved later in this same start-up must already see the context.</summary>
    private static void RegisterTerminal()
    {
        var settings = Services.GetRequiredService<Core.Application.DTOs.TerminalSettings>();
        var context = Services.GetRequiredService<Core.Application.Services.TerminalContext>();
        var status = Services.GetRequiredService<TerminalStatus>();

        if (settings.IsSatellite && string.IsNullOrWhiteSpace(settings.MasterUrl))
            status.SetConfigError("Falta Terminal:MasterUrl");

        try
        {
            using var scope = Services.CreateScope();
            var terminals = scope.ServiceProvider.GetRequiredService<Core.Application.Services.ITerminalService>();
            var dto = Task.Run(() => terminals.EnsureRegisteredAsync(settings.Code, settings.Name, settings.Mode, AppVersion))
                .GetAwaiter().GetResult();
            context.Set(dto);
            status.MarkRegistered();
            Console.WriteLine($"[Terminal] {dto.Code} · {dto.KindLabel} ({dto.Name})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Terminal] No se pudo registrar la caja {settings.Code}: {ex.Message}");
            status.MarkRegistrationFailed(ex.Message);
        }
    }

    private static string ResolveAppVersion()
    {
        var asm = typeof(App).Assembly;
        var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static async void StartLanServer(LoqzenServer server, LanServerStatus status)
    {
        try
        {
            // Task.Run: Kestrel start-up never waits on the (not yet pumping) UI dispatcher.
            await Task.Run(() => server.StartAsync());
            status.MarkRunning();
            Console.WriteLine($"[LAN] Servidor iniciado: {server.KdsUrl}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LAN] No se pudo iniciar el servidor en el puerto {server.Port}: {ex.Message}");
            status.MarkFailed(ex.Message);
        }
    }

    private static void StopLanServer(LoqzenServer server)
    {
        try
        {
            // Short bounded wait: a stuck shutdown must not keep the process alive.
            Task.Run(() => server.StopAsync()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LAN] Error al detener el servidor: {ex.Message}");
        }
    }
}
