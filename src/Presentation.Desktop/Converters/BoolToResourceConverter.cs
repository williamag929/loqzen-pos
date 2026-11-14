using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Presentation.Desktop.Converters;

/// <summary>
/// Converts a boolean to one of two THEME RESOURCES (Styles/Theme.axaml keys), so views never carry hex literals.
/// Usage: <c>Converter={x:Static conv:BoolToResourceConverter.Instance}, ConverterParameter='Accent|BgSurface'</c>
/// (TrueKey|FalseKey).
/// <para>
/// Theme switching: the converter does not return the dictionary brush itself but a per-key proxy
/// <see cref="SolidColorBrush"/> whose <c>Color</c> is re-read from the ACTUAL theme variant every time
/// <see cref="Application.ActualThemeVariantChanged"/> fires. A binding converted once therefore follows
/// 🌙/☀️ toggles live, without the ViewModel having to raise PropertyChanged. The cache is bounded by the number
/// of distinct token keys, so nothing leaks.
/// </para>
/// Prefer <c>Classes.active="{Binding IsX}"</c> + a style when the two states are a well-known pair (tabs, chips);
/// use this converter when a one-off pair does not deserve a style. <see cref="BoolToColorConverter"/> (hex
/// parameters) is kept only for legacy call sites and must not be used in new views.
/// </summary>
public class BoolToResourceConverter : IValueConverter
{
    public static readonly BoolToResourceConverter Instance = new();

    private static readonly ConcurrentDictionary<string, SolidColorBrush> Proxies = new();
    private static bool _subscribed;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string paramStr)
            return Brushes.Transparent;

        var keys = paramStr.Split('|');
        if (keys.Length != 2)
            return Brushes.Transparent;

        var key = (boolValue ? keys[0] : keys[1]).Trim();
        if (Application.Current is not { } app)
            return Brushes.Transparent;

        EnsureSubscribed(app);
        if (!Proxies.TryGetValue(key, out var proxy))
        {
            // Unknown key or non-brush resource: give the raw resource back (e.g. a Thickness) or Transparent.
            if (!TryResolve(app, key, out var color))
                return app.TryGetResource(key, app.ActualThemeVariant, out var raw) && raw != null ? raw : Brushes.Transparent;
            proxy = Proxies.GetOrAdd(key, _ => new SolidColorBrush(color));
        }
        return proxy;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static void EnsureSubscribed(Application app)
    {
        if (_subscribed) return;
        _subscribed = true;
        app.ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var (key, brush) in Proxies)
                if (TryResolve(app, key, out var color))
                    brush.Color = color;
        };
    }

    private static bool TryResolve(Application app, string key, out Color color)
    {
        if (app.TryGetResource(key, app.ActualThemeVariant, out var resource) && resource is ISolidColorBrush b)
        {
            color = b.Color;
            return true;
        }
        color = default;
        return false;
    }
}
