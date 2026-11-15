using System.Text.RegularExpressions;

namespace Sessionwright;

/// <summary>A web address pinned to a connection or group: a Dynamics 365 environment, its LCS page, a DevOps board.</summary>
public sealed class NodeLink
{
    public string Name { get; set; } = "";
    /// <summary>The address; in a group it may hold {name}, {host} and {vm}, filled in for each connection in it.</summary>
    public string Url { get; set; } = "";

    public NodeLink Clone() => new() { Name = Name, Url = Url };
}

/// <summary>A link as one connection opens it: the template filled in, and the group it comes from (null: its own).</summary>
public sealed record ResolvedLink(string Name, string Url, Group? From);

public static class Links
{
    static readonly Regex Placeholder = new(@"\{(name|host|vm)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasPlaceholders(string template) => Placeholder.IsMatch(template);

    /// <summary>The template for <paramref name="c"/>: {name} its name (else the host), {host} its host, {vm} its Azure VM.</summary>
    public static string Expand(string template, Connection c) => Placeholder.Replace(template.Trim(), m => m.Groups[1].Value.ToLowerInvariant() switch
    {
        "name" => c.Title,
        "host" => c.Host,
        _ => c.AzureVm?.Name ?? c.Title,
    });

    /// <summary>
    /// A web address and nothing else: http(s), a host, no spaces, quotes or shell characters. It is typed into the
    /// server's Run box, which runs whatever it is given, so a mistyped or pasted text must never become a command.
    /// </summary>
    public static bool IsSafe(string url) =>
        !url.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '"' or '\'' or '<' or '>' or '|' or '^' or '`')
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
        && uri.UserInfo.Length == 0; // no "user:password@": it would sit in the data file, the menus and the log in plain text

    /// <summary>
    /// The address for a log or a problem report: without "user:password@", the query and the fragment, where tokens and
    /// keys travel. Plain text work, as a group's template ("https://{name}.example.com") is no valid address yet.
    /// </summary>
    public static string WithoutSecrets(string url)
    {
        var end = url.IndexOfAny(['?', '#']);
        if (end >= 0) url = url[..end] + "?…";
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var at = scheme < 0 ? -1 : url.IndexOf('@', scheme);
        var slash = scheme < 0 ? -1 : url.IndexOf('/', scheme + 3);
        return at >= 0 && (slash < 0 || at < slash) ? url[..(scheme + 3)] + url[(at + 1)..] : url;
    }

    /// <summary>A template as the editor checks it: safe once filled in with a sample connection.</summary>
    public static bool IsSafeTemplate(string template) =>
        IsSafe(Expand(template, new Connection { Name = "env", Host = "env.example.com", AzureVm = new AzureVmLink { Name = "vm" } }));

    /// <summary>The name shown: its own, else the address's host.</summary>
    public static string NameOf(NodeLink link) =>
        link.Name.Trim() is { Length: > 0 } name ? name
        : Uri.TryCreate(link.Url.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : link.Url.Trim();
}
