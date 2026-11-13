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
        if (await TrySendAsync(EscPosRenderer.Render(lines, legalFooter: true), cancellationToken))
            return true;

        await SaveToFallbackFileAsync($"Receipt_{receipt.OrderNumber}", lines, "Receipt");
        return false;
    }

    /// <summary>Prints a pre-formatted document (Z report, bill-payment receipt, cash movement slip).</summary>
    public async Task<bool> PrintLinesAsync(IReadOnlyList<ReceiptLine> lines, string documentName, CancellationToken cancellationToken = default)
    {
        if (await TrySendAsync(EscPosRenderer.Render(lines, legalFooter: false), cancellationToken))
            return true;

        await SaveToFallbackFileAsync(SanitizeFileName(documentName), lines, "Document");
        return false;
    }

    /// <summary>Sends raw ESC/POS bytes over TCP. Returns false (and logs) if the printer is unreachable.</summary>
    private async Task<bool> TrySendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, cancellationToken);
            await using var stream = client.GetStream();

            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ESC/POS Network Printer Error]: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes the plain-text rendering to ~/.local/share/LoqzenPos/FailedReceipts/{stem}_{yyyyMMdd_HHmmss}.txt.</summary>
    private static async Task SaveToFallbackFileAsync(string fileStem, IReadOnlyList<ReceiptLine> lines, string kind)
    {
        try
        {
            var fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LoqzenPos", "FailedReceipts");
            Directory.CreateDirectory(fallbackDir);

            var filepath = Path.Combine(fallbackDir, $"{fileStem}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(filepath, ReceiptFormatter.ToPlainText(lines));
            Console.WriteLine($"[PRINTER FALLBACK] {kind} saved to: {filepath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PRINTER FALLBACK ERROR]: {ex.Message}");
        }
    }

    /// <summary>Keeps only ASCII letters, digits and '-'; any other run of characters becomes a single '-'.</summary>
    internal static string SanitizeFileName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? string.Empty)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "Document" : result;
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

    public Task<bool> PrintLinesAsync(IReadOnlyList<ReceiptLine> lines, string documentName, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("========================================");
        Console.WriteLine($"[MOCK PRINTER] Document: {documentName}");
        Console.WriteLine(ReceiptFormatter.ToPlainText(lines));
        Console.WriteLine("========================================");
        return Task.FromResult(true);
    }

    public Task<bool> OpenCashDrawerAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[MOCK PRINTER] *** CASH DRAWER SOLENOID PULSE FIRED (0x1B 0x70) ***");
        return Task.FromResult(true);
    }
}
