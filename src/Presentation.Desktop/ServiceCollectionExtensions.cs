using System;
using System.IO;
using Core.Application.Hardware;
using Core.Application.Services;
using Core.Application.Sync;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Infrastructure.Data.Services;
using Infrastructure.Hardware.Printers;
using Infrastructure.Hardware.Scales;
using Infrastructure.Hardware.Scanners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Services;
using Presentation.Desktop.ViewModels;
using Presentation.Server;

namespace Presentation.Desktop;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLoqzenPosServices(this IServiceCollection services, IConfiguration configuration)
    {
        // ── Configuration ────────────────────────────────────────────────────────
        var hwSettings = configuration.GetSection("Hardware").Get<HardwareSettings>() ?? new HardwareSettings();
        services.AddSingleton(hwSettings);

        var comandaSettings = configuration.GetSection("Comanda").Get<Core.Application.DTOs.ComandaSettings>()
                              ?? new Core.Application.DTOs.ComandaSettings();
        services.AddSingleton(comandaSettings);
        services.AddSingleton(TimeProvider.System);

        var kdsSettings = configuration.GetSection("Kds").Get<Core.Application.Kitchen.KdsSettings>()
                          ?? new Core.Application.Kitchen.KdsSettings();
        services.AddSingleton(kdsSettings);

        var authSettings = configuration.GetSection("Authorization").Get<Core.Application.DTOs.AuthorizationSettings>()
                           ?? new Core.Application.DTOs.AuthorizationSettings();
        services.AddSingleton(authSettings);

        var syncSettings = configuration.GetSection("Sync").Get<SyncSettings>() ?? new SyncSettings();
        services.AddSingleton(syncSettings);

        var serverSettings = configuration.GetSection("Server").Get<Core.Application.DTOs.ServerSettings>()
                             ?? new Core.Application.DTOs.ServerSettings();
        services.AddSingleton(serverSettings);

        // ── Database ─────────────────────────────────────────────────────────────
        services.AddDbContext<PosDbContext>(options =>
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dbFolder = Path.Combine(appData, "LoqzenPos");
            Directory.CreateDirectory(dbFolder);
            var dbPath = Path.Combine(dbFolder, "pos.db");
            options.UseSqlite($"Data Source={dbPath}");
        }, ServiceLifetime.Transient);

        // ── Master Services ──────────────────────────────────────────────────────
        services.AddTransient<IProductService, ProductService>();
        services.AddTransient<ICategoryService, CategoryService>();
        services.AddTransient<ITaxService, TaxService>();
        services.AddTransient<ICustomerService, CustomerService>();
        services.AddTransient<ICashShiftService, CashShiftService>();

        // ── Sales Services ───────────────────────────────────────────────────────
        services.AddTransient<ISalesService, SalesService>();
        services.AddTransient<IReportService, ReportService>();
        services.AddTransient<IComandaService, ComandaService>();
        services.AddTransient<IKitchenService, KitchenService>();

        // ── Kitchen events + embedded LAN server (browser KDS, comandera REST, WebSocket) ──
        // KitchenService/ComandaService publish on the bus after SaveChanges; the desktop KDS and
        // the Kestrel hub subscribe. The server resolves app services from this same container.
        services.AddSingleton<Core.Application.Kitchen.IKitchenEventBus, Core.Application.Kitchen.KitchenEventBus>();
        services.AddSingleton<LanServerStatus>();
        services.AddSingleton(sp => new LoqzenServer(sp, sp.GetRequiredService<Core.Application.DTOs.ServerSettings>()));

        // ── Outbox sync (ERP webhook). Disabled by default: messages stay Pending, nothing leaves the store. ──
        services.AddSingleton(new System.Net.Http.HttpClient());
        if (syncSettings.Enabled)
            services.AddSingleton<IOutboxSink, HttpOutboxSink>();
        else
            services.AddSingleton<IOutboxSink, NullOutboxSink>();
        services.AddTransient<IOutboxDispatcher, OutboxDispatcher>(); // owns a DbContext: resolve per tick in a scope
        services.AddSingleton<OutboxSyncWorker>();

        // ── Users / authorization ────────────────────────────────────────────────
        services.AddTransient<IUserService, UserService>();
        services.AddTransient<IManagerAuthorizationService, ManagerAuthorizationService>();
        services.AddSingleton<Core.Application.Security.IPinAttemptLimiter, Core.Application.Security.PinAttemptLimiter>(); // shared lockout state
        services.AddSingleton<UserSession>();                       // one terminal = one session
        services.AddSingleton<IUserSession>(sp => sp.GetRequiredService<UserSession>());
        services.AddSingleton<ManagerAuthPromptViewModel>();        // single modal shared by every screen
        services.AddSingleton<IManagerAuthPrompt>(sp => sp.GetRequiredService<ManagerAuthPromptViewModel>());
        services.AddSingleton<LoginViewModel>();

        // ── Hardware Services (Mock vs Real based on config) ─────────────────────
        if (hwSettings.UseMockPrinter)
            services.AddSingleton<IPrinterService, MockPrinterService>();
        else
            services.AddSingleton<IPrinterService>(sp =>
                new EscPosNetworkPrinterService(hwSettings.PrinterHost, hwSettings.PrinterPort));

        if (hwSettings.UseMockScale)
            services.AddSingleton<IScaleService, MockScaleService>();
        else
            services.AddSingleton<IScaleService>(sp =>
                new CasSerialScaleService(hwSettings.ScalePortName, hwSettings.ScaleBaudRate));

        if (hwSettings.UseMockScanner)
            services.AddSingleton<IBarcodeScannerService, MockBarcodeScannerService>();
        else
            services.AddSingleton<IBarcodeScannerService, UsbHidBarcodeScannerService>();

        // ── ViewModels ───────────────────────────────────────────────────────────
        services.AddTransient<CheckoutViewModel>();
        services.AddSingleton<KdsViewModel>(); // owns a timer: exactly one instance
        services.AddTransient<CashShiftViewModel>();
        services.AddTransient<MastersViewModel>();
        services.AddSingleton<CustomerDisplayViewModel>();
        services.AddSingleton<ComandaViewModel>();
        services.AddTransient<MainViewModel>();

        return services;
    }
}
