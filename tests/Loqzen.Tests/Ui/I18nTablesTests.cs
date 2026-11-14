using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Loqzen.Tests.Ui;

/// <summary>
/// Guards the UI language tables (src/Presentation.Desktop/Assets/i18n/*.json): every key present in Spanish exists
/// in English and vice versa (per fragment), no key is defined twice across fragments, every {i18n:Loc Key} and
/// Localizer T("Key") used by the desktop resolves, and placeholders ({0}) match between languages.
/// File-based like XamlCommandBindingTests, so the test project does not need to reference Avalonia.
/// </summary>
public class I18nTablesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoqzenPos.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("LoqzenPos.sln not found above the test directory.");
    }

    private static string I18nDir() => Path.Combine(RepoRoot(), "src", "Presentation.Desktop", "Assets", "i18n");

    private static Dictionary<string, Dictionary<string, string>> LoadLanguage(string lang)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var file in Directory.GetFiles(I18nDir(), $"{lang}*.json").OrderBy(f => f))
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))
                        ?? new Dictionary<string, string>();
            result[Path.GetFileName(file)] = table;
        }
        return result;
    }

    private static string Fragment(string fileName) => fileName.Contains('.') ? fileName[(fileName.IndexOf('.') + 1)..] : fileName;

    [Fact]
    public void BothLanguages_HaveTheSameFragments()
    {
        var es = LoadLanguage("es").Keys.Select(Fragment).OrderBy(x => x).ToList();
        var en = LoadLanguage("en").Keys.Select(Fragment).OrderBy(x => x).ToList();
        es.Should().NotBeEmpty();
        en.Should().BeEquivalentTo(es, "every es.*.json fragment needs its en.*.json twin");
    }

    [Fact]
    public void EveryKey_ExistsInBothLanguages_PerFragment()
    {
        var es = LoadLanguage("es");
        var en = LoadLanguage("en");
        var problems = new List<string>();
        foreach (var (file, table) in es)
        {
            var twin = "en" + file[2..];
            if (!en.TryGetValue(twin, out var enTable)) continue; // covered by the fragments test
            foreach (var key in table.Keys.Except(enTable.Keys)) problems.Add($"{twin} lacks '{key}'");
            foreach (var key in enTable.Keys.Except(table.Keys)) problems.Add($"{file} lacks '{key}'");
        }
        problems.Should().BeEmpty(string.Join("\n", problems));
    }

    [Fact]
    public void NoKey_IsDefinedInTwoFragments()
    {
        foreach (var lang in new[] { "es", "en" })
        {
            var seen = new Dictionary<string, string>();
            var dup = new List<string>();
            foreach (var (file, table) in LoadLanguage(lang))
                foreach (var key in table.Keys)
                {
                    if (seen.TryGetValue(key, out var where)) dup.Add($"'{key}' in {where} and {file}");
                    else seen[key] = file;
                }
            dup.Should().BeEmpty(string.Join("\n", dup));
        }
    }

    [Fact]
    public void Placeholders_Match_BetweenLanguages()
    {
        var es = LoadLanguage("es").SelectMany(t => t.Value).ToDictionary(k => k.Key, k => k.Value);
        var en = LoadLanguage("en").SelectMany(t => t.Value).ToDictionary(k => k.Key, k => k.Value);
        var mismatches = es.Keys.Intersect(en.Keys)
            .Where(k => Placeholders(es[k]) != Placeholders(en[k]))
            .Select(k => $"'{k}': es {{{Placeholders(es[k])}}} vs en {{{Placeholders(en[k])}}}")
            .ToList();
        mismatches.Should().BeEmpty(string.Join("\n", mismatches));

        static string Placeholders(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
    }

    [Fact]
    public void EveryKey_UsedByTheDesktop_IsDefined()
    {
        var defined = LoadLanguage("es").SelectMany(t => t.Value.Keys).ToHashSet();
        var desktop = Path.Combine(RepoRoot(), "src", "Presentation.Desktop");
        var used = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(desktop, "*.axaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{i18n:Loc\s+([A-Za-z0-9_.]+)\}"))
                used.TryAdd(m.Groups[1].Value, Path.GetFileName(file));
        foreach (var file in Directory.GetFiles(desktop, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bT\(\s*""([A-Za-z0-9_.]+)"""))
                used.TryAdd(m.Groups[1].Value, Path.GetFileName(file));

        // A key ending with '.' is a prefix completed at runtime (T("Cash.Movement.Chips." + type)): checked by prefix.
        var prefixes = used.Keys.Where(k => k.EndsWith('.')).ToList();
        foreach (var prefix in prefixes)
        {
            defined.Should().Contain(k => k.StartsWith(prefix), $"no key starts with the runtime prefix '{prefix}'");
            used.Remove(prefix);
        }

        used.Should().NotBeEmpty("the desktop should reference localized keys");
        var missing = used.Where(kv => !defined.Contains(kv.Key)).Select(kv => $"'{kv.Key}' ({kv.Value})").ToList();
        missing.Should().BeEmpty(string.Join("\n", missing));
    }
}
