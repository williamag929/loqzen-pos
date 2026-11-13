using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Core.Application.Receipts;

namespace Presentation.Desktop.Converters;

/// <summary>Maps ReceiptLine styling to on-screen properties so the preview mirrors ESC/POS output.</summary>
public static class ReceiptLineConverters
{
    public const double BaseFontSize = 15;

    public static readonly IValueConverter Align = new FuncValueConverter<ReceiptAlign, HorizontalAlignment>(
        a => a == ReceiptAlign.Center ? HorizontalAlignment.Center : HorizontalAlignment.Left);

    /// <summary>ESC/POS "Large" is double width + height; the line has half the columns, so 2x font keeps paper width.</summary>
    public static readonly IValueConverter FontSize = new FuncValueConverter<ReceiptLineStyle, double>(
        s => s == ReceiptLineStyle.Large ? BaseFontSize * 2 : BaseFontSize);

    public static readonly IValueConverter Weight = new FuncValueConverter<ReceiptLineStyle, FontWeight>(
        s => s == ReceiptLineStyle.Normal ? FontWeight.Normal : FontWeight.Bold);
}
