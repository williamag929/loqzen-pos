using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Presentation.Desktop.Converters;

/// <summary>true → strikethrough (voided rows), false → no decoration.</summary>
public sealed class StrikeConverter : IValueConverter
{
    public static readonly StrikeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextDecorations.Strikethrough : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
