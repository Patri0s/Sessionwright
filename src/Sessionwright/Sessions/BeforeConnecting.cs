using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sessionwright;

/// <summary>The command a connection runs before connecting (a VPN, a tunnel): filled in, split, started.</summary>
static class BeforeConnecting
{
    static readonly Regex Port = new(@"\{port\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The command for <paramref name="c"/>: {port} its port; {name}, {host}, {vm} as in link templates. Null when a value
    /// it would take in has a character a command line acts on (cmd's &amp; | &lt; &gt; ^ %, quotes, PowerShell's ; $ `, a
    /// backslash before the closing quote) or an option (- / =) and the connection is from a team's shared list: it takes
    /// the command of the group it sits in, and its name comes from that list. A shared value with spaces goes in quoted,
    /// one argument. The user's own names are the user's ("DEV (old)").
    /// </summary>
    public static string? Expand(string command, Connection c)
    {
        string Value(Match m) => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "name" => c.Title,
            "host" => c.Host,
            _ => c.AzureVm?.Name ?? c.Title,
        };
        var withPort = Port.Replace(command, c.Port.ToString(CultureInfo.InvariantCulture));
        if (!c.Shared) return Links.Expand(withPort, c);
        if (Placeholders.Matches(withPort).Select(Value).Any(v => v.StartsWith('-') || v.StartsWith('/') || v.Any(ch => char.IsControl(ch) || Unsafe.Contains(ch))))
            return null;
        // Spaces: quoted, unless the command already has it in quotes ("{name}").
        return Placeholders.Replace(withPort.Trim(), m => Value(m) is var v && v.Any(char.IsWhiteSpace) && (m.Index == 0 || withPort.Trim()[m.Index - 1] != '"')
            ? $"\"{v}\"" : v);
    }

    static readonly Regex Placeholders = new(@"\{(name|host|vm)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly char[] Unsafe = ['"', '\'', '&', '|', '<', '>', '^', '%', '!', ';', '$', '`', '(', ')', '=', '\\'];

    /// <summary>
    /// The program and its arguments: a quoted first part is the program ("C:\Program Files\vpn.exe" connect), else
    /// everything up to the first space.
    /// </summary>
    internal static (string Program, string Arguments) Split(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"') && command.IndexOf('"', 1) is var close and > 0)
            return (command[1..close], command[(close + 1)..].Trim());
        var space = command.IndexOfAny([' ', '\t']);
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    /// <summary>
    /// Starts it as the user would from Run: with its window, because a VPN client may ask for a sign-in, and without
    /// waiting for it to end, because one may keep running. Throws when it cannot be started.
    /// </summary>
    public static void Start(string command)
    {
        var (program, arguments) = Split(command);
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
    }
}
