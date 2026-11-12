using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sessionwright;

/// <summary>
/// sessionwright:// links that open a connection: copied from a connection's menu and pasted into a wiki or a chat, a
/// click connects whoever has Sessionwright to the same server, with their own credentials and settings.
/// <c>sessionwright://connect/host[:port]?id=…</c>: the id finds the same connection in a list imported from the same
/// export (also after its host changed); the address finds any saved connection to it, or connects to it as given.
/// </summary>
static class SessionLink
{
    public const string Scheme = "sessionwright";
    const string Key = @"Software\Classes\" + Scheme;

    public sealed record Target(string Host, int Port, Guid? Id)
    {
        public string Address => (Host.Contains(':') ? $"[{Host}]" : Host) + (Port == 3389 ? "" : $":{Port}");
    }

    static readonly Regex IdParameter = new(@"[?&]id=([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(&|$)",
        RegexOptions.CultureInvariant);

    public static string For(Connection c) => $"{Scheme}://connect/{new Target(c.Host, c.Port, null).Address}?id={c.Id}";

    public static bool IsLink(string? text) => text?.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// What a link points at; null when it is no connect link or its address is no host name or IP address. A link can
    /// come from any web page, so nothing but a plain address gets through to become the server to connect to.
    /// </summary>
    public static Target? Parse(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("connect", StringComparison.OrdinalIgnoreCase)) return null;
        var address = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (address.Length is 0 or > 300 || address.Any(char.IsWhiteSpace) || !Store.TryParseAddress(address, out var host, out var port)) return null;
        var id = IdParameter.Match(uri.Query) is { Success: true } m ? Guid.Parse(m.Groups[1].Value) : (Guid?)null;
        return new Target(host, port, id);
    }

    /// <summary>The saved connection a link means: the one with its id, else one to its address; null when none.</summary>
    public static Connection? Find(IEnumerable<Connection> connections, Target target)
    {
        var list = connections.ToList();
        return list.FirstOrDefault(c => target.Id is { } id && c.Id == id)
            ?? list.FirstOrDefault(c => string.Equals(c.Host, target.Host, StringComparison.OrdinalIgnoreCase) && c.Port == target.Port);
    }

    /// <summary>
    /// Makes Windows open sessionwright: links with <paramref name="exe"/>, for this user only (no administrator rights).
    /// Done on every start of an installed copy, so the link keeps working after an update or a reinstall elsewhere.
    /// </summary>
    public static void Register(string exe, RegistryKey? root = null, string key = Key)
    {
        try
        {
            using var scheme = (root ?? Registry.CurrentUser).CreateSubKey(key);
            scheme.SetValue("", "URL:Sessionwright link");
            scheme.SetValue("URL Protocol", "");
            using (var icon = scheme.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using var command = scheme.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Error(ex, "Registering sessionwright: links");
        }
    }

    /// <summary>Registers the running exe, when it is the installed one: a copy run from elsewhere must not take the links.</summary>
    public static void RegisterThisExe()
    {
        if (Updater.IsInstalled && Environment.ProcessPath is { } exe) Register(exe);
    }

    public static void Unregister(RegistryKey? root = null, string key = Key)
    {
        try { (root ?? Registry.CurrentUser).DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Error(ex, "Removing sessionwright: links");
        }
    }
}
