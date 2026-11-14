using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;

namespace Presentation.Desktop.Services;

/// <summary>Bound from appsettings.json section "Ui".</summary>
public class UiSettings
{
    /// <summary>"Dark" (default) or "Light". The user's last choice (ui.json in the data folder) overrides this.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>"es" (default) or "en". The user's last choice (ui.json) overrides this. Documents (receipts, tickets) stay in Spanish.</summary>
    public string Language { get; set; } = "es";
}

/// <summary>
/// Switches the app between the Dark and Light token dictionaries (Styles/Theme.axaml) by setting
/// <see cref="Application.RequestedThemeVariant"/>, and remembers the choice in &lt;data dir&gt;/LoqzenPos/ui.json
/// (appsettings.json lives in the program folder and is not meant to be edited by cashiers).
/// Singleton; call <see cref="ApplySaved"/> once at start-up (UI thread) and <see cref="Toggle"/> from the chip.
/// </summary>
public sealed class ThemeService
{
    private readonly UiSettings _settings;
    private readonly string _statePath;

    public ThemeService(UiSettings settings, string? dataDirectory = null)
    {
        _settings = settings;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoqzenPos");
        _statePath = Path.Combine(dir, "ui.json");
    }

    public bool IsLight { get; private set; }
    public event Action? Changed;

    public void ApplySaved()
    {
        var name = _settings.Theme;
        try
        {
            if (File.Exists(_statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
                if (doc.RootElement.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String)
                    name = t.GetString() ?? name;
            }
        }
        catch { /* unreadable state file: fall back to appsettings */ }
        Apply(string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase), persist: false);
    }

    public void Toggle() => Apply(!IsLight, persist: true);

    public void Apply(bool light, bool persist = true)
    {
        IsLight = light;
        if (Application.Current is { } app)
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (persist)
        {
            try
            {
                // ui.json is shared with the Localizer ("language"): merge, never overwrite the other keys.
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                var state = new System.Collections.Generic.Dictionary<string, object?>();
                if (File.Exists(_statePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
                    foreach (var p in doc.RootElement.EnumerateObject())
                        state[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                }
                state["theme"] = light ? "Light" : "Dark";
                File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
            }
            catch { /* best effort */ }
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Semantic brushes for ViewModels that expose a colour (status dots, difference colours). Reads the CURRENT theme's
/// token at call time, so a VM never holds a hex literal. Values are hex strings (the views bind them to Brush
/// properties, Avalonia parses them) — refresh the VM after a theme change to pick the new ones.
/// </summary>
public static class ThemePalette
{
    public static string Success => Hex("Success");
    public static string SuccessBright => Hex("SuccessBright");
    public static string Warning => Hex("Warning");
    public static string WarningBright => Hex("WarningBright");
    public static string Danger => Hex("Danger");
    public static string DangerBright => Hex("DangerBright");
    public static string Accent => Hex("Accent");
    public static string AccentBright => Hex("AccentBright");
    public static string Info => Hex("Info");
    public static string Purple => Hex("Purple");
    public static string TextPrimary => Hex("TextPrimary");
    public static string TextSecondary => Hex("TextSecondary");
    public static string TextMuted => Hex("TextMuted");
    public static string TextFaint => Hex("TextFaint");
    public static string Border => Hex("Border");
    public static string BorderStrong => Hex("BorderStrong");
    public static string Warning_Surface => Hex("WarningSurface");
    public static string BgSurface => Hex("BgSurface");

    private static string Hex(string key)
    {
        if (Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            && value is Avalonia.Media.ISolidColorBrush b)
            return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
        return "#94A3B8";
    }
}
