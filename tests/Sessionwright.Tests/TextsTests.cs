using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Sessionwright.Tests;

/// <summary>
/// The texts in Loc.cs against the source that names them: a key that is missing shows as its own name in the app, a
/// key entered twice silently loses one of its texts, a key nothing names is a text nobody will ever read or translate.
/// </summary>
public class TextsTests
{
    static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../src/Sessionwright"));

    static List<string> Keys() =>
        [.. Regex.Matches(File.ReadAllText(Path.Combine(Root(), "Loc.cs")), @"^\s*\[""(\w+)""\] = \(", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];

    /// <summary>Every source file but the texts themselves; Loc.cs's own code (after its table) names keys too.</summary>
    static string Source()
    {
        var s = Path.DirectorySeparatorChar;
        var files = Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories).Concat(Directory.GetFiles(Root(), "*.xaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{s}bin{s}") && !f.Contains($"{s}obj{s}") && Path.GetFileName(f) != "Loc.cs").ToList();
        Assert.True(files.Count > 50, $"Only {files.Count} source files under {Root()}");
        var loc = File.ReadAllText(Path.Combine(Root(), "Loc.cs"));
        // Without the table, and without doc comments: one shows how a key is written in XAML, with a made-up key.
        return string.Concat(files.Select(File.ReadAllText)) + Regex.Replace(loc, @"^\s*(\[""\w+""\] = \(|///).*$", "", RegexOptions.Multiline);
    }

    [Fact]
    public void NoKeyIsEnteredTwice() =>
        Assert.Empty(Keys().GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));

    // Loc.Get("X"), Loc.Format("X", ...) and {l:L X}: named outright, so they can be checked.
    [Fact]
    public void EveryKeyNamedInTheSourceHasItsText()
    {
        var keys = Keys().ToHashSet();
        var named = Regex.Matches(Source(), @"Loc\.(?:Get|Format)\(""(\w+)""|\{l:L (\w+)\}")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Distinct().ToList();
        Assert.True(named.Count > 300, $"Only {named.Count} keys found in the source");
        Assert.DoesNotContain(named, k => !keys.Contains(k));
    }

    // Keys put together from a name at run time (Loc.Get($"Theme{t}")) are known by their beginnings.
    [Fact]
    public void EveryTextIsNamedSomewhere()
    {
        var source = Source();
        var built = Regex.Matches(source, @"\$""(\w+)\{").Select(m => m.Groups[1].Value).Distinct().ToList();
        var unused = Keys().Where(k => !Regex.IsMatch(source, $@"\b{k}\b") && !built.Any(k.StartsWith)).ToList();
        Assert.Empty(unused);
    }
}
