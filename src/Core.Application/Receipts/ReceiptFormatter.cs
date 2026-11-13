using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Core.Application.DTOs;

namespace Core.Application.Receipts;

public enum ReceiptLineStyle { Normal = 0, Bold = 1, /* Bold + double size (GS ! 0x11) */ Large = 2 }
public enum ReceiptAlign { Left = 0, Center = 1 }

/// <summary>One printable line. Text is already padded/truncated to the receipt width.</summary>
public sealed record ReceiptLine(string Text, ReceiptLineStyle Style = ReceiptLineStyle.Normal, ReceiptAlign Align = ReceiptAlign.Left)
{
    public bool IsBold => Style != ReceiptLineStyle.Normal;
    public bool IsLarge => Style == ReceiptLineStyle.Large;
}

/// <summary>
/// Single source of truth for receipt layout. ESC/POS printer, TXT fallback and the on-screen
/// preview all render these lines, so what the cashier sees is what gets printed.
/// Output is ASCII-safe (accents stripped) because the printer path encodes ASCII.
/// </summary>
public static partial class ReceiptFormatter
{
    public const int DefaultWidth = 32;
    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("es-CO");

    public static IReadOnlyList<ReceiptLine> Format(ReceiptDto r, int width = DefaultWidth, bool isPreview = false)
    {
        var lines = new List<ReceiptLine>();
        var sep = new string('=', width);
        var thin = new string('-', width);

        lines.Add(new ReceiptLine(Fit($"*** {r.StoreName} ***", width), ReceiptLineStyle.Bold, ReceiptAlign.Center));
        if (!string.IsNullOrWhiteSpace(r.StoreAddress))
            foreach (var l in Wrap(r.StoreAddress, width)) lines.Add(new ReceiptLine(l, Align: ReceiptAlign.Center));
        if (!string.IsNullOrWhiteSpace(r.StoreTaxId))
            lines.Add(new ReceiptLine(Fit(r.StoreTaxId, width), Align: ReceiptAlign.Center));
        lines.Add(new ReceiptLine(sep));

        if (isPreview)
            lines.Add(new ReceiptLine(Fit("** PRE-CUENTA (NO VALIDA) **", width), ReceiptLineStyle.Bold, ReceiptAlign.Center));

        lines.Add(new ReceiptLine(Fit($"Ticket: {r.OrderNumber}", width)));
        lines.Add(new ReceiptLine(Fit($"Fecha:  {r.Date:yyyy-MM-dd HH:mm:ss}", width)));
        lines.Add(new ReceiptLine(Fit($"Cajero: {r.CashierName}", width)));
        if (!string.IsNullOrEmpty(r.CustomerName))
        {
            lines.Add(new ReceiptLine(Fit($"Cliente: {r.CustomerName}", width)));
            lines.Add(new ReceiptLine(Fit($"CC/NIT:  {r.CustomerDocument}", width)));
        }
        lines.Add(new ReceiptLine(sep));
        lines.Add(new ReceiptLine(Columns("Cant Descripcion", "Total", width), ReceiptLineStyle.Bold));
        lines.Add(new ReceiptLine(thin));

        foreach (var item in r.Items.Where(i => !i.IsReturn))
            AddItem(lines, item, item.Name, width);

        var returns = r.Items.Where(i => i.IsReturn).ToList();
        if (returns.Count > 0)
        {
            lines.Add(new ReceiptLine(string.Empty));
            lines.Add(new ReceiptLine(Fit("*** NOTA CREDITO ***", width), ReceiptLineStyle.Bold));
            foreach (var item in returns)
            {
                AddItem(lines, item, $"DEVOL: {item.Name}", width);
                lines.Add(new ReceiptLine(Fit("     (Ajuste en venta)", width)));
            }
            lines.Add(new ReceiptLine(thin));
        }

        lines.Add(new ReceiptLine(sep));
        lines.Add(new ReceiptLine(Columns("Subtotal:", Cop(r.Subtotal), width)));
        if (r.DiscountTotal > 0)
            lines.Add(new ReceiptLine(Columns("Descuento:", "-" + Cop(r.DiscountTotal), width)));
        lines.Add(new ReceiptLine(Columns("Impuestos:", Cop(r.TaxTotal), width)));
        // Large = double width -> only half the columns fit.
        lines.Add(new ReceiptLine(Columns("TOTAL:", Cop(r.Total), width / 2), ReceiptLineStyle.Large));
        lines.Add(new ReceiptLine(sep));

        if (!isPreview)
        {
            lines.Add(new ReceiptLine(Fit($"Medio: {r.PaymentMethodName}", width)));
            if (r.TenderedAmount > 0)
            {
                lines.Add(new ReceiptLine(Columns("Recibido:", Cop(r.TenderedAmount), width)));
                lines.Add(new ReceiptLine(Columns("Cambio:", Cop(r.ChangeAmount), width)));
            }
            lines.Add(new ReceiptLine(sep));
        }

        lines.Add(new ReceiptLine(string.Empty));
        lines.Add(new ReceiptLine(Fit(isPreview ? "Pre-cuenta - no es factura" : "Gracias por su compra!", width), Align: ReceiptAlign.Center));
        return lines;
    }

    /// <summary>Plain text for TXT fallback / logs. Centered lines get left padding.</summary>
    public static string ToPlainText(IEnumerable<ReceiptLine> lines, int width = DefaultWidth) =>
        string.Join("\n", lines.Select(l => l.Align == ReceiptAlign.Center ? Center(l.Text, l.IsLarge ? width / 2 : width) : l.Text)) + "\n";
}
