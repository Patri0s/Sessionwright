using CommunityToolkit.Mvvm.ComponentModel;

namespace Sessionwright;

public enum ScriptRunState { Waiting, Running, Done, DoneWithErrors, Failed }

/// <summary>One machine of a script run on several: where it stands and what it wrote.</summary>
public sealed partial class ScriptRunRow : ObservableObject
{
    public required Connection Connection { get; init; }
    public string Machine => Connection.Title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] private ScriptRunState _state;
    /// <summary>The first line that says something: what the table shows.</summary>
    [ObservableProperty] private string _summary = "";
    /// <summary>Everything it wrote, or why it did not run.</summary>
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _time = "";

    public string StateText => Loc.Get($"ScriptState{State}");
}

/// <summary>A script run on several machines at once: what the table and the clipboard say about it.</summary>
static class ScriptBatch
{
    /// <summary>A few at a time: Azure and WinRM both take a while per machine, and a whole group at once would flood them.</summary>
    public const int AtOnce = 6;

    /// <summary>The first line with something to read: not blank, not a table's rule of dashes.</summary>
    internal static string Summary(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.All(c => c is '-' or '=' or ' ')) ?? "";

    /// <summary>The whole run as text: a line per machine, then what each one wrote.</summary>
    internal static string Report(string script, IReadOnlyList<ScriptRunRow> rows)
    {
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Machine.Length);
        var lines = new List<string> { script, "" };
        lines.AddRange(rows.Select(r => $"{r.Machine.PadRight(width)}  {r.StateText}{(r.Summary.Length > 0 ? ": " + r.Summary : "")}"));
        foreach (var r in rows.Where(r => r.Output.Trim().Length > 0))
        {
            lines.Add("");
            lines.Add($"--- {r.Machine} ---");
            lines.Add(r.Output.TrimEnd());
        }
        return string.Join(Environment.NewLine, lines);
    }
}
