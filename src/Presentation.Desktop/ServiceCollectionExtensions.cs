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

        // Lists are read explicitly: the binder APPENDS to the defaults of an initialized List<decimal>.
        var cashSection = configuration.GetSection("CashShift");
        var cashSettings = cashSection.Get<Core.Application.DTOs.CashShiftSettings>() ?? new Core.Application.DTOs.CashShiftSettings();
        var defaults = new Core.Application.DTOs.CashShiftSettings();
        cashSettings.Bills = cashSection.GetSection("Bills").Get<System.Collections.Generic.List<decimal>>() is { Count: > 0 } bills ? bills : defaults.Bills;
        cashSettings.Coins = cashSection.GetSection("Coins").Get<System.Collections.Generic.List<decimal>>() is { Count: > 0 } coins ? coins : defaults.Coins;
        var movementRoles = cashSection.GetSection("ManagerRequiredFor");
        cashSettings.ManagerRequiredFor = movementRoles.Exists() ? movementRoles.Get<System.Collections.Generic.List<string>>() ?? new() : null;
        services.AddSingleton(cashSettings);

        // ── Terminal identity (master / satellite). See appsettings.Satellite.example.json. ──
        var terminalSettings = configuration.GetSection("Terminal").Get<Core.Application.DTOs.TerminalSettings>()
                               ?? new Core.Application.DTOs.TerminalSettings();
        services.AddSingleton(terminalSettings);
        var terminalContext = new TerminalContext();
        services.AddSingleton(terminalContext);
        services.AddSingleton<ITerminalContext>(terminalContext);
        services.AddSingleton<TerminalStatus>();

        var syncSettings = configuration.GetSection("Sync").Get<SyncSettings>() ?? new SyncSettings();
        if (terminalSettings.IsSatellite)
        {
            // A satellite's Outbox always goes to the master's inbox (never to an ERP webhook):
            // forced BEFORE the singleton / sink registration below so the HttpOutboxSink sees it.
            // Without a MasterUrl nothing is sent (messages stay Pending) and the 🖥️ chip shows the config error.
            var hasMaster = !string.IsNullOrWhiteSpace(terminalSettings.MasterUrl);
            syncSettings.Enabled = hasMaster;
            syncSettings.ApiKey = terminalSettings.ApiKey;
            if (hasMaster)
                syncSettings.WebhookUrl = terminalSettings.MasterUrl!.TrimEnd('/') + "/api/sync/inbox";
        }
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
        services.AddTransient<IBillPaymentService, BillPaymentService>();

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

        // ── Multi-register: terminal registry, catalog replication, master inbox, satellite worker ──
        services.AddScoped<ITerminalService, TerminalService>();
        services.AddScoped<ICatalogSyncService, Infrastructure.Data.Sync.CatalogSyncService>();
        services.AddScoped<ISyncInboxService, Infrastructure.Data.Sync.SyncInboxService>();
        services.AddSingleton<IMasterApiClient>(sp =>
            new Infrastructure.Data.Sync.MasterApiClient(sp.GetRequiredService<System.Net.Http.HttpClient>(), terminalSettings));
        services.AddSingleton<SatelliteSyncWorker>(); // only started when IsSatellite (App.axaml.cs)

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

        // Cash drawer: kicked through the receipt printer (RJ11 port) or mocked with the printer.
        if (hwSettings.UseMockPrinter)
            services.AddSingleton<ICashDrawerService, Infrastructure.Hardware.CashDrawer.MockCashDrawerService>();
        else
            services.AddSingleton<ICashDrawerService>(sp =>
                new Infrastructure.Hardware.CashDrawer.PrinterKickCashDrawerService(sp.GetRequiredService<IPrinterService>()));

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
        services.AddSingleton<CashShiftViewModel>();   // owns a 30 s refresh timer: exactly one instance
        services.AddSingleton<BillPaymentsViewModel>(); // subscribes to the scanner while its tab is active
        services.AddTransient<MastersViewModel>();
        services.AddSingleton<CustomerDisplayViewModel>();
        services.AddSingleton<ComandaViewModel>();
        services.AddTransient<MainViewModel>();

        return services;
    }
}
