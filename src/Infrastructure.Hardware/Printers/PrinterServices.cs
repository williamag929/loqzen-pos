using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Application.Receipts;
using System.Collections.Generic;

namespace Infrastructure.Hardware.Printers;

public class EscPosNetworkPrinterService : IPrinterService
{
    private readonly string _host;
    private readonly int _port;

    public EscPosNetworkPrinterService(string host = "127.0.0.1", int port = 9100)
    {
        _host = host;
        _port = port;
    }

    public async Task<bool> PrintReceiptAsync(ReceiptDto receipt, CancellationToken cancellationToken = default)
    {
        // Same layout as the on-screen preview (ReceiptFormatter is the single source of truth).
        var lines = ReceiptFormatter.Format(receipt);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, cancellationToken);
            await using var stream = client.GetStream();

            var payload = EscPosRenderer.Render(lines, legalFooter: true);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ESC/POS Network Printer Error]: {ex.Message}");
            await SaveReceiptToFileAsync(receipt, lines);
            return false;
        }
    }

    private static async Task SaveReceiptToFileAsync(ReceiptDto receipt, IReadOnlyList<ReceiptLine> lines)
    {
        try
        {
            var fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LoqzenPos", "FailedReceipts");
            Directory.CreateDirectory(fallbackDir);

            var filepath = Path.Combine(fallbackDir, $"Receipt_{receipt.OrderNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(filepath, ReceiptFormatter.ToPlainText(lines));
            Console.WriteLine($"[PRINTER FALLBACK] Receipt saved to: {filepath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PRINTER FALLBACK ERROR]: {ex.Message}");
        }
    }


    public async Task<bool> PrintKitchenTicketAsync(ReceiptDto receipt, string station, CancellationToken cancellationToken = default)
    {
        // ZPL or ESC/POS kitchen order ticket
        return await PrintReceiptAsync(receipt, cancellationToken);
    }

    public async Task<bool> OpenCashDrawerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, cancellationToken);
            await using var stream = client.GetStream();

            // ESC p 0 25 250 (Cash drawer pulse kick)
            byte[] pulse = { 0x1B, 0x70, 0x00, 0x19, 0xFA };
            await stream.WriteAsync(pulse, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ESC/POS Drawer Kick Error]: {ex.Message}");
            return false;
        }
    }
}

public class MockPrinterService : IPrinterService
{
    public Task<bool> PrintReceiptAsync(ReceiptDto receipt, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("========================================");
        Console.WriteLine($"[MOCK PRINTER] Receipt for Order: {receipt.OrderNumber}");
        Console.WriteLine($"Store: {receipt.StoreName} | Cashier: {receipt.CashierName}");
        Console.WriteLine($"Total: ${receipt.Total:N0} ({receipt.PaymentMethodName})");
        Console.WriteLine("========================================");
        return Task.FromResult(true);
    }

    public Task<bool> PrintKitchenTicketAsync(ReceiptDto receipt, string station, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[MOCK PRINTER - KITCHEN STATION {station}] Ticket #{receipt.OrderNumber}");
        return Task.FromResult(true);
    }

    public Task<bool> OpenCashDrawerAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[MOCK PRINTER] *** CASH DRAWER SOLENOID PULSE FIRED (0x1B 0x70) ***");
        return Task.FromResult(true);
    }
}
