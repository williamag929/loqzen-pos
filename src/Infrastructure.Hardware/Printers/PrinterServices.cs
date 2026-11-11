using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Hardware;

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
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, cancellationToken);
            await using var stream = client.GetStream();

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.GetEncoding("ASCII"));

            // ESC @ (Initialize printer)
            writer.Write(new byte[] { 0x1B, 0x40 });

            // Center align: ESC a 1
            writer.Write(new byte[] { 0x1B, 0x61, 0x01 });
            writer.Write(Encoding.ASCII.GetBytes($"*** {receipt.StoreName} ***\n"));
            writer.Write(Encoding.ASCII.GetBytes($"{receipt.StoreAddress}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"{receipt.StoreTaxId}\n"));
            writer.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));

            // Left align: ESC a 0
            writer.Write(new byte[] { 0x1B, 0x61, 0x00 });
            writer.Write(Encoding.ASCII.GetBytes($"Ticket: {receipt.OrderNumber}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Fecha:  {receipt.Date:yyyy-MM-dd HH:mm:ss}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Cajero: {receipt.CashierName}\n"));
            if (!string.IsNullOrEmpty(receipt.CustomerName))
            {
                writer.Write(Encoding.ASCII.GetBytes($"Cliente:{receipt.CustomerName} ({receipt.CustomerDocument})\n"));
            }
            writer.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));
            writer.Write(Encoding.ASCII.GetBytes("Cant Descripcion           Total\n"));
            writer.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));

            foreach (var item in receipt.Items)
            {
                var name = item.Name.Length > 18 ? item.Name[..18] : item.Name.PadRight(18);
                var qty = $"{item.Quantity,4:0.##}";
                var total = $"{item.Total,8:N0}";
                writer.Write(Encoding.ASCII.GetBytes($"{qty} {name} {total}\n"));
            }

            writer.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Subtotal:       ${receipt.Subtotal,14:N0}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Impuestos:      ${receipt.TaxTotal,14:N0}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"TOTAL:          ${receipt.Total,14:N0}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Medio: {receipt.PaymentMethodName}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Recibido:       ${receipt.TenderedAmount,14:N0}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Cambio:         ${receipt.ChangeAmount,14:N0}\n"));
            writer.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));
            
            // Center align: ESC a 1
            writer.Write(new byte[] { 0x1B, 0x61, 0x01 });
            writer.Write(Encoding.ASCII.GetBytes("Gracias por su compra!\n\n\n\n"));

            // Cut paper: GS V 66 0
            writer.Write(new byte[] { 0x1D, 0x56, 0x42, 0x00 });

            var payload = ms.ToArray();
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
