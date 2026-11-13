using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.ViewModels;
using Presentation.Desktop.Views;

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

        // ── Create Main Window with DI-resolved ViewModel ───────────────────────
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}