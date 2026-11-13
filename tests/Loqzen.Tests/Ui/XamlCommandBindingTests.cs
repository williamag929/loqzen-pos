using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Loqzen.Tests.Ui;

/// <summary>
/// XAML command bindings are reflection-based: a wrong name compiles fine and the button silently
/// does nothing. CommunityToolkit strips "Async" (FooAsync() -> FooCommand), which caused 13 dead
/// buttons. This test checks every "XxxCommand" referenced in .axaml exists on some ViewModel.
/// Reads the built Presentation.Desktop assembly by path to avoid referencing the UI project.
/// </summary>
public class XamlCommandBindingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoqzenPos.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }

    [Fact]
    public void EveryCommandReferencedInXaml_ExistsOnAViewModel()
    {
        var root = RepoRoot();
        var viewsDir = Path.Combine(root, "src", "Presentation.Desktop", "Views");
        var dll = Directory.GetFiles(Path.Combine(root, "src", "Presentation.Desktop", "bin"), "Presentation.Desktop.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (dll == null) return; // UI project not built in this run (e.g. test-only build) — nothing to check.

        var asm = Assembly.LoadFrom(dll);
        // ViewModels (generated RelayCommands) + reusable controls that expose their own
        // ICommand properties (e.g. Numpad.DigitCommand bound via #Root).
        var available = asm.GetTypes()
            .Where(t => t.Namespace is "Presentation.Desktop.ViewModels" or "Presentation.Desktop.Views")
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.Name.EndsWith("Command"))
            .Select(p => p.Name)
            .ToHashSet();

        var referenced = Directory.GetFiles(viewsDir, "*.axaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"([A-Za-z]+Command)\}")
                .Select(m => (File: Path.GetFileName(f), Name: m.Groups[1].Value)))
            .Distinct()
            .ToList();

        referenced.Should().NotBeEmpty();
        referenced.Where(r => !available.Contains(r.Name))
            .Select(r => $"{r.File}: {r.Name}")
            .Should().BeEmpty("every XAML command binding must resolve to a generated ViewModel command");
    }
}
