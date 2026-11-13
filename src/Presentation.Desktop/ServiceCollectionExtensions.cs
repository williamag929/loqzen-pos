using System;
using System.IO;
using Core.Application.Hardware;
using Core.Application.Services;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Infrastructure.Hardware.Printers;
using Infrastructure.Hardware.Scales;
using Infrastructure.Hardware.Scanners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.ViewModels;

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
        services.AddTransient<KdsViewModel>();
        services.AddTransient<CashShiftViewModel>();
        services.AddTransient<MastersViewModel>();
        services.AddSingleton<CustomerDisplayViewModel>();
        services.AddSingleton<ComandaViewModel>();
        services.AddTransient<MainViewModel>();

        return services;
    }
}
