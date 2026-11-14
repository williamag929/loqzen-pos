using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace Presentation.Desktop.Localization;

/// <summary>
/// Runtime UI language (es / en). Strings live in <c>Assets/i18n/{lang}.json</c> plus per-area fragments
/// <c>{lang}.{area}.json</c> (flat "Key": "text" maps, embedded as AvaloniaResource, merged at load). XAML binds through <see cref="LocExtension"/> (<c>Text="{i18n:Loc Tab.Checkout}"</c>) or
/// <c>{Binding [Tab.Checkout], Source={x:Static i18n:Localizer.Instance}}</c>; ViewModels call <see cref="T"/>.
/// Switching the language raises <c>PropertyChanged("Item[]")</c>, so every bound text updates in place.
/// Missing key → falls back to Spanish, then to the key itself (never throws), and is logged once.
/// Receipts, kitchen tickets and server pages keep the STORE language (Spanish) on purpose: they are documents.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public static readonly IReadOnlyList<string> Available = new[] { "es", "en" };
    public const string Default = "es";

    public static Localizer Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new();
    private string _language = Default;
    private string? _statePath;

    private Localizer() { }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public string Language => _language;
    /// <summary>"ES" / "EN" for the chip.</summary>
    public string LanguageLabel => _language.ToUpperInvariant();
    public string NextLanguage => _language == "es" ? "en" : "es";
    public CultureInfo Culture => _language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("es-CO");

    /// <summary>Indexer for XAML bindings.</summary>
    public string this[string key] => T(key);

    /// <summary>Translated text; <paramref name="args"/> are applied with string.Format when present.</summary>
    public string T(string key, params object?[] args)
    {
        var text = Lookup(_language, key) ?? Lookup(Default, key);
        if (text == null)
        {
            if (_reported.Add(key)) Console.WriteLine($"[i18n] Falta la clave '{key}' ({_language}).");
            text = key;
        }
        if (args.Length == 0) return text;
        try { return string.Format(Culture, text, args); }
        catch (FormatException) { return text; }
    }

    /// <summary>Reads Ui:Language from settings, then the user's last choice from &lt;data dir&gt;/LoqzenPos/ui.json.</summary>
    public void ApplySaved(string? configuredLanguage, string? dataDirectory = null)
    {
        var dir = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoqzenPos");
        _statePath = Path.Combine(dir, "ui.json");
        var lang = configuredLanguage;
        try
        {
            if (File.Exists(_statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
                if (doc.RootElement.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String)
                    lang = l.GetString();
            }
        }
        catch { /* unreadable: keep configured */ }
        SetLanguage(lang, persist: false);
    }

    public void Toggle() => SetLanguage(NextLanguage, persist: true);

    public void SetLanguage(string? language, bool persist = true)
    {
        var lang = (language ?? Default).Trim().ToLowerInvariant();
        if (lang.Length > 2) lang = lang[..2];
        if (Array.IndexOf((string[])Available, lang) < 0) lang = Default;

        EnsureLoaded(lang);
        EnsureLoaded(Default);
        _language = lang;
        CultureInfo.CurrentUICulture = Culture;

        if (persist && _statePath != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                var state = new Dictionary<string, object?>();
                if (File.Exists(_statePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
                    foreach (var p in doc.RootElement.EnumerateObject()) state[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                }
                state["language"] = lang;
                File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
            }
            catch { /* best effort */ }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageLabel)));
        Changed?.Invoke();
    }

    /// <summary>Loads a table from an in-memory JSON string (tests) — replaces the embedded one for that language.</summary>
    public void LoadFromJson(string language, string json)
    {
        _tables[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private string? Lookup(string lang, string key)
    {
        EnsureLoaded(lang);
        return _tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v) ? v : null;
    }

    private void EnsureLoaded(string lang)
    {
        if (_tables.ContainsKey(lang)) return;
        // One language = every Assets/i18n/{lang}*.json file merged (es.json, es.masters.json, es.cash.json, ...):
        // each screen family keeps its own fragment so several people can add keys without merge conflicts.
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var folder = new Uri("avares://Presentation.Desktop/Assets/i18n/");
            foreach (var uri in AssetLoader.GetAssets(folder, null))
            {
                var name = Path.GetFileName(uri.LocalPath);
                if (!name.StartsWith(lang + ".", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var part = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
                if (part == null) continue;
                foreach (var kv in part) table[kv.Key] = kv.Value; // later fragments win; keys should not repeat
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[i18n] No se pudo cargar las tablas de '{lang}': {ex.Message}");
        }
        _tables[lang] = table;
    }
}

/// <summary>
/// <c>{i18n:Loc Key}</c> → a OneWay binding to <see cref="Localizer.Instance"/>[Key] that updates when the language changes.
/// Works inside <c>x:CompileBindings</c> views (it returns a classic Binding with an explicit Source).
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key) { Key = key; }
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay };
}
