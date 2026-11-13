using System;
using System.IO;
using System.Linq;
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
            
            // ESC E 1 (Bold on)
            writer.Write(new byte[] { 0x1B, 0x45, 0x01 });
            writer.Write(Encoding.ASCII.GetBytes($"*** {receipt.StoreName} ***\n"));
            // ESC E 0 (Bold off)
            writer.Write(new byte[] { 0x1B, 0x45, 0x00 });
            
            writer.Write(Encoding.ASCII.GetBytes($"{receipt.StoreAddress}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"{receipt.StoreTaxId}\n"));
            writer.Write(Encoding.ASCII.GetBytes("================================\n"));

            // Left align: ESC a 0
            writer.Write(new byte[] { 0x1B, 0x61, 0x00 });
            writer.Write(Encoding.ASCII.GetBytes($"Ticket: {receipt.OrderNumber}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Fecha:  {receipt.Date:yyyy-MM-dd HH:mm:ss}\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Cajero: {receipt.CashierName}\n"));
            if (!string.IsNullOrEmpty(receipt.CustomerName))
            {
                writer.Write(Encoding.ASCII.GetBytes($"Cliente: {receipt.CustomerName}\n"));
                writer.Write(Encoding.ASCII.GetBytes($"CC/NIT:  {receipt.CustomerDocument}\n"));
            }
            writer.Write(Encoding.ASCII.GetBytes("================================\n"));
            
            // ESC E 1 (Bold on for header)
            writer.Write(new byte[] { 0x1B, 0x45, 0x01 });
            writer.Write(Encoding.ASCII.GetBytes("Cant Descripcion           Total\n"));
            writer.Write(new byte[] { 0x1B, 0x45, 0x00 });
            writer.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));

            // Separate normal items from returns
            var normalItems = receipt.Items.Where(i => !i.IsReturn).ToList();
            var returnItems = receipt.Items.Where(i => i.IsReturn).ToList();

            // Print normal items
            foreach (var item in normalItems)
            {
                var name = item.Name.Length > 18 ? item.Name[..18] : item.Name.PadRight(18);
                var qty = $"{item.Quantity,4:0.##}";
                var total = $"{item.Total,8:N0}";
                writer.Write(Encoding.ASCII.GetBytes($"{qty} {name} {total}\n"));
                
                if (!string.IsNullOrEmpty(item.Notes))
                {
                    var notes = item.Notes.Length > 30 ? item.Notes[..30] : item.Notes;
                    writer.Write(Encoding.ASCII.GetBytes($"     ({notes})\n"));
                }
            }

            // Print return items (credit notes) in separate section
            if (returnItems.Any())
            {
                writer.Write(Encoding.ASCII.GetBytes("\n"));
                writer.Write(new byte[] { 0x1B, 0x45, 0x01 }); // Bold ON
                writer.Write(Encoding.ASCII.GetBytes("*** NOTA CREDITO ***\n"));
                writer.Write(new byte[] { 0x1B, 0x45, 0x00 }); // Bold OFF
                
                foreach (var item in returnItems)
                {
                    var name = $"DEVOL: {item.Name}";
                    name = name.Length > 18 ? name[..18] : name.PadRight(18);
                    var qty = $"{item.Quantity,4:0.##}";
                    var total = $"{item.Total,8:N0}";
                    writer.Write(Encoding.ASCII.GetBytes($"{qty} {name} {total}\n"));
                    writer.Write(Encoding.ASCII.GetBytes("     (Ajuste en venta)\n"));
                }
                writer.Write(Encoding.ASCII.GetBytes("--------------------------------\n"));
            }

            writer.Write(Encoding.ASCII.GetBytes("================================\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Subtotal:       ${receipt.Subtotal,14:N0}\n"));
            
            if (receipt.DiscountTotal > 0)
            {
                writer.Write(Encoding.ASCII.GetBytes($"Descuento:     -${receipt.DiscountTotal,14:N0}\n"));
            }
            
            writer.Write(Encoding.ASCII.GetBytes($"Impuestos:      ${receipt.TaxTotal,14:N0}\n"));
            
            writer.Write(new byte[] { 0x1B, 0x45, 0x01 });
            writer.Write(new byte[] { 0x1D, 0x21, 0x11 });
            writer.Write(Encoding.ASCII.GetBytes($"TOTAL: ${receipt.Total,12:N0}\n"));
            writer.Write(new byte[] { 0x1D, 0x21, 0x00 });
            writer.Write(new byte[] { 0x1B, 0x45, 0x00 });
            
            writer.Write(Encoding.ASCII.GetBytes("================================\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Medio: {receipt.PaymentMethodName}\n"));
            
            if (receipt.TenderedAmount > 0)
            {
                writer.Write(Encoding.ASCII.GetBytes($"Recibido:       ${receipt.TenderedAmount,14:N0}\n"));
                writer.Write(Encoding.ASCII.GetBytes($"Cambio:         ${receipt.ChangeAmount,14:N0}\n"));
            }
            
            writer.Write(Encoding.ASCII.GetBytes("================================\n\n"));
            
            writer.Write(new byte[] { 0x1B, 0x61, 0x01 });
            writer.Write(Encoding.ASCII.GetBytes("Gracias por su compra!\n"));
            writer.Write(Encoding.ASCII.GetBytes("Vuelva pronto\n\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Factura valida como\n"));
            writer.Write(Encoding.ASCII.GetBytes($"documento equivalente\n"));
            writer.Write(Encoding.ASCII.GetBytes($"Resolucion DIAN 12345\n\n\n"));

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
            await SaveReceiptToFileAsync(receipt);
            return false;
        }
    }

    private async Task SaveReceiptToFileAsync(ReceiptDto receipt)
    {
        try
        {
            var fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LoqzenPos", "FailedReceipts");
            Directory.CreateDirectory(fallbackDir);
            
            var filename = $"Receipt_{receipt.OrderNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var filepath = Path.Combine(fallbackDir, filename);
            
            var items = string.Join("\n", receipt.Items.Select(i => 
                $"{i.Quantity,4:0.##}  {i.Name,-20} ${i.Total,8:N0}"));
            
            var content = $@"
=======================================
    {receipt.StoreName}
    {receipt.StoreAddress}
    {receipt.StoreTaxId}
=======================================
Ticket: {receipt.OrderNumber}
Fecha:  {receipt.Date:yyyy-MM-dd HH:mm:ss}
Cajero: {receipt.CashierName}
{(string.IsNullOrEmpty(receipt.CustomerName) ? "" : $"Cliente: {receipt.CustomerName} ({receipt.CustomerDocument})\n")}=======================================
{items}
=======================================
Subtotal:       ${receipt.Subtotal,14:N0}
{(receipt.DiscountTotal > 0 ? $"Descuento:     -${receipt.DiscountTotal,14:N0}\n" : "")}Impuestos:      ${receipt.TaxTotal,14:N0}
TOTAL:          ${receipt.Total,14:N0}
=======================================
Medio: {receipt.PaymentMethodName}
{(receipt.TenderedAmount > 0 ? $"Recibido:       ${receipt.TenderedAmount,14:N0}\nCambio:         ${receipt.ChangeAmount,14:N0}\n" : "")}=======================================
Gracias por su compra!
";
            
            await File.WriteAllTextAsync(filepath, content);
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
