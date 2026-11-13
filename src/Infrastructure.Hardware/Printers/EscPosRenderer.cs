using System.Collections.Generic;
using System.IO;
using System.Text;
using Core.Application.Receipts;

namespace Infrastructure.Hardware.Printers;

/// <summary>Converts formatter lines to ESC/POS bytes. Pure function — unit-testable without a printer.</summary>
public static class EscPosRenderer
{
    private static readonly byte[] Init = { 0x1B, 0x40 };
    private static readonly byte[] AlignLeft = { 0x1B, 0x61, 0x00 };
    private static readonly byte[] AlignCenter = { 0x1B, 0x61, 0x01 };
    private static readonly byte[] BoldOn = { 0x1B, 0x45, 0x01 };
    private static readonly byte[] BoldOff = { 0x1B, 0x45, 0x00 };
    private static readonly byte[] SizeDouble = { 0x1D, 0x21, 0x11 };
    private static readonly byte[] SizeNormal = { 0x1D, 0x21, 0x00 };
    private static readonly byte[] Cut = { 0x1D, 0x56, 0x42, 0x00 };

    public static byte[] Render(IEnumerable<ReceiptLine> lines, bool legalFooter = false)
    {
        using var ms = new MemoryStream();
        ms.Write(Init);
        foreach (var line in lines)
        {
            ms.Write(line.Align == ReceiptAlign.Center ? AlignCenter : AlignLeft);
            if (line.IsBold) ms.Write(BoldOn);
            if (line.IsLarge) ms.Write(SizeDouble);
            ms.Write(Encoding.ASCII.GetBytes(line.Text + "\n"));
            if (line.IsLarge) ms.Write(SizeNormal);
            if (line.IsBold) ms.Write(BoldOff);
        }

        if (legalFooter)
        {
            ms.Write(AlignCenter);
            ms.Write(Encoding.ASCII.GetBytes("Factura valida como\ndocumento equivalente\nResolucion DIAN 12345\n"));
        }
        ms.Write(Encoding.ASCII.GetBytes("\n\n\n"));
        ms.Write(Cut);
        return ms.ToArray();
    }
}
