using System;
using System.IO;
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

        // ── Create Main Window with DI-resolved ViewModel ───────────────────────
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => syncWorker.Dispose();
            if (server != null)
                desktop.Exit += (_, _) => StopLanServer(server);

            var main = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = main };

            // Optional start tab (kiosk screens / demos): LOQZEN_START_VIEW=kds|comandas|caja|maestros
            switch (Environment.GetEnvironmentVariable("LOQZEN_START_VIEW")?.ToLowerInvariant())
            {
                case "kds": _ = main.ShowKdsCommand.ExecuteAsync(null); break;
                case "comandas": _ = main.ShowComandasCommand.ExecuteAsync(null); break;
                case "caja": _ = main.ShowCashShiftCommand.ExecuteAsync(null); break;
                case "maestros": _ = main.ShowMastersCommand.ExecuteAsync(null); break;
            }
        }

        base.OnFrameworkInitializationCompleted();
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