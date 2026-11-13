using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Presentation.Desktop.Converters;

/// <summary>
/// Converts a boolean to one of two colors.
/// Usage: ConverterParameter="TrueColor|FalseColor" (e.g., "#2563EB|#1E293B")
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string paramStr)
            return Brushes.Transparent;

        var colors = paramStr.Split('|');
        if (colors.Length != 2)
            return Brushes.Transparent;

        var colorHex = boolValue ? colors[0] : colors[1];
        
        try
        {
            return new SolidColorBrush(Color.Parse(colorHex));
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
