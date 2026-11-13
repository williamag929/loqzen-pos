using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.Hardware;

namespace Infrastructure.Hardware.CashDrawer;

// ViewModels should depend on ICashDrawerService (not on IPrinterService) to open the cash drawer, so the
// drawer wiring can change (printer DK port, USB/serial trigger, mock) without touching UI code.
// The desktop composition root is expected to register ICashDrawerService -> PrinterKickCashDrawerService.

/// <summary>
/// Cash drawer connected to the receipt printer's DK (drawer kick) port, the reference hardware.
/// Opening delegates to <see cref="IPrinterService.OpenCashDrawerAsync"/>, which sends the ESC p pulse.
/// </summary>
public class PrinterKickCashDrawerService : ICashDrawerService
{
    private readonly IPrinterService _printer;

    public PrinterKickCashDrawerService(IPrinterService printer)
    {
        _printer = printer ?? throw new ArgumentNullException(nameof(printer));
    }

    public Task<bool> OpenDrawerAsync(CancellationToken cancellationToken = default) =>
        _printer.OpenCashDrawerAsync(cancellationToken);
}

/// <summary>Development/test drawer: logs to the console and always succeeds.</summary>
public class MockCashDrawerService : ICashDrawerService
{
    public Task<bool> OpenDrawerAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[MOCK DRAWER] Cajón abierto");
        return Task.FromResult(true);
    }
}
