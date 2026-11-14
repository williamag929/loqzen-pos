using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Presentation.Desktop.Converters;

/// <summary>
/// LEGACY: converts a boolean to one of two hard-coded colors (ConverterParameter="TrueColor|FalseColor").
/// Hex parameters ignore the theme (they look wrong in Light), so do NOT use this in new or migrated views:
/// use <c>Classes.active="{Binding IsX}"</c> + a style, or <see cref="BoolToResourceConverter"/> with two theme
/// token keys ("Accent|BgSurface"). Kept only until the last call site is migrated; still registered as
/// "BoolToColor" in App.axaml.
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
