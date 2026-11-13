using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Sessionwright;

/// <summary>What the user set on an item of a shared list: theirs, kept in their own data file, not in the shared one.</summary>
public class SharedItemState
{
    public Guid Id { get; set; }
    public bool Favorite { get; set; }
    public int FavoriteOrder { get; set; }
    public DateTime? LastUsed { get; set; }
    public WindowPlacement? Placement { get; set; }
    public DateTime? LeftRunningAt { get; set; }
    /// <summary>A group folded in the tree.</summary>
    public bool Collapsed { get; set; }
}

/// <summary>
/// A team's list of connections, read from a file (OneDrive, a network share) or an https address into one of the user's
/// own groups. The group is theirs: its credential, color, VPN and settings are what the shared items inherit, so the
/// file carries no passwords and everyone signs in as themselves. The items are read-only and never written to the
/// user's data file; only <see cref="SharedItemState"/> is. The file is an ordinary export.
/// </summary>
static class SharedLists
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>https or a file path: plain http could be rewritten on the way, and the list says where to connect.</summary>
    public static bool IsAllowed(string source) => !source.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    static bool IsWeb(string source) => source.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static async Task<string> ReadAsync(string source, CancellationToken cancel = default)
    {
        source = source.Trim();
        if (!IsAllowed(source)) throw new InvalidDataException(Loc.Get("SharedListInsecure"));
        if (IsWeb(source)) return await Http.GetStringAsync(source, cancel);
        return await File.ReadAllTextAsync(Environment.ExpandEnvironmentVariables(source), cancel);
    }

    /// <summary>
    /// The groups and connections of an export, placed under <paramref name="root"/>. Left out: credentials (everyone
    /// has their own), commands to run before connecting (a shared file must not run programs on other PCs), sync and
    /// shared-list settings, and items whose id <paramref name="taken"/> says is already in the user's list.
    /// </summary>
    internal static (List<Group> Groups, List<Connection> Connections) Parse(string json, Group root, Func<Guid, bool> taken)
    {
        Importer.ExportFile? file;
        try { file = JsonSerializer.Deserialize<Importer.ExportFile>(json, Store.JsonOptions(null)); }
        catch (JsonException ex) { throw new InvalidDataException(Loc.Get("NotSessionwrightFile"), ex); }
        if (file is not { Format: "Sessionwright" }) throw new InvalidDataException(Loc.Get("NotSessionwrightFile"));
        if (file.Version > Importer.ExportFile.CurrentVersion) throw new InvalidDataException(Loc.Format("ExportTooNew", file.Version));

        static BeforeConnect? Vpn(BeforeConnect? b) => b is { Vpn.Length: > 0 } ? new BeforeConnect { Vpn = b.Vpn, WaitMinutes = Math.Clamp(b.WaitMinutes, 1, 60) } : null;

        var source = (file.Groups ?? []).Where(g => g != null && g.Id != root.Id && !taken(g.Id)).DistinctBy(g => g.Id).ToList();
        var ids = source.Select(g => g.Id).ToHashSet();
        var groups = source.Select(g => new Group
        {
            Id = g.Id, Name = g.Name ?? "", ParentId = g.ParentId is { } p && ids.Contains(p) ? p : root.Id, Notes = g.Notes,
            Links = g.Links?.Select(l => l.Clone()).ToList(), Rdp = g.Rdp?.Clone(), Color = g.Color, Order = g.Order, Expanded = g.Expanded,
            BeforeConnect = Vpn(g.BeforeConnect), Shared = true,
        }).ToList();
        // A file whose groups form a ring would hang every walk up the tree: such groups go straight under the root.
        var byId = groups.ToDictionary(g => g.Id);
        foreach (var g in groups)
        {
            var seen = new HashSet<Guid> { g.Id };
            for (var up = g.ParentId; up is { } id && byId.TryGetValue(id, out var parent); up = parent.ParentId)
                if (!seen.Add(id))
                {
                    g.ParentId = root.Id;
                    break;
                }
        }

        var connections = (file.Connections ?? [])
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Host) && !taken(c.Id) && !ids.Contains(c.Id)).DistinctBy(c => c.Id)
            .Select(c => new Connection
            {
                Id = c.Id, Name = c.Name ?? "", Host = c.Host.Trim(), Port = c.Port is >= 1 and <= 65535 ? c.Port : 3389,
                ParentId = c.ParentId is { } p && ids.Contains(p) ? p : root.Id, Rdp = c.Rdp?.Clone(), Notes = c.Notes,
                Links = c.Links?.Select(l => l.Clone()).ToList(), Tags = c.Tags, Color = c.Color, Order = c.Order,
                BeforeConnect = Vpn(c.BeforeConnect), Shared = true,
            }).ToList();
        return (groups, connections);
    }

    /// <summary>The shared items under <paramref name="root"/>, at any depth.</summary>
    public static List<Node> ItemsOf(Store store, Group root) =>
        [.. store.Data.Groups.Cast<Node>().Concat(store.Data.Connections).Where(n => n.Shared && store.Ancestors(n).Contains(root))];

    /// <summary>
    /// Puts the list under <paramref name="root"/>: items already there are updated in place (an open session keeps its
    /// connection object), new ones are added, those gone from the file are removed unless a session of theirs is open.
    /// </summary>
    public static void Apply(Store store, Group root, List<Group> groups, List<Connection> connections)
    {
        var old = ItemsOf(store, root).ToDictionary(n => n.Id);
        var state = (root.SharedState ?? []).ToDictionary(s => s.Id);
        foreach (var g in groups)
        {
            if (old.Remove(g.Id, out var was) && was is Group existing)
                (existing.Name, existing.ParentId, existing.Notes, existing.Links, existing.Rdp, existing.Color, existing.Order, existing.BeforeConnect) =
                    (g.Name, g.ParentId, g.Notes, g.Links, g.Rdp, g.Color, g.Order, g.BeforeConnect);
            else
            {
                if (was != null) store.Delete(was); // a connection in the old file, a group in the new one
                if (state.TryGetValue(g.Id, out var s)) g.Expanded = !s.Collapsed;
                store.Data.Groups.Add(g);
            }
        }
        foreach (var c in connections)
        {
            if (old.Remove(c.Id, out var was) && was is Connection existing)
                (existing.Name, existing.Host, existing.Port, existing.ParentId, existing.Rdp, existing.Notes, existing.Links, existing.Tags, existing.Color, existing.Order,
                        existing.BeforeConnect) =
                    (c.Name, c.Host, c.Port, c.ParentId, c.Rdp, c.Notes, c.Links, c.Tags, c.Color, c.Order, c.BeforeConnect);
            else
            {
                if (was != null) store.Delete(was);
                if (state.TryGetValue(c.Id, out var s))
                    (c.Favorite, c.FavoriteOrder, c.LastUsed, c.Placement, c.LeftRunningAt) = (s.Favorite, s.FavoriteOrder, s.LastUsed, s.Placement, s.LeftRunningAt);
                store.Data.Connections.Add(c);
            }
        }
        // Connections first: deleting a group takes its content with it.
        foreach (var gone in old.Values.OrderBy(n => n is Group))
        {
            if (gone is Connection { IsOpen: true } open) open.ParentId = root.Id; // stays until its session ends, then goes with the next refresh
            else if (store.Data.Groups.Contains(gone) || store.Data.Connections.Contains(gone)) store.Delete(gone);
        }
        root.SharedLoaded = true;
    }

    /// <summary>Removes the list's items (the group stopped being a shared list).</summary>
    public static void Clear(Store store, Group root)
    {
        foreach (var n in ItemsOf(store, root).OrderBy(n => n is Group))
            if (store.Data.Groups.Contains(n) || store.Data.Connections.Contains(n)) store.Delete(n);
        root.SharedLoaded = false;
        root.SharedState = null;
    }

    /// <summary>What the user set on the list's items, to be saved with the group; only what differs from a fresh item.</summary>
    public static List<SharedItemState>? StateOf(Store store, Group root)
    {
        var states = ItemsOf(store, root).Select(n => n switch
        {
            Connection c when c.Favorite || c.LastUsed != null || c.Placement != null || c.LeftRunningAt != null => new SharedItemState
            {
                Id = c.Id, Favorite = c.Favorite, FavoriteOrder = c.FavoriteOrder, LastUsed = c.LastUsed, Placement = c.Placement, LeftRunningAt = c.LeftRunningAt,
            },
            Group { Expanded: false } g => new SharedItemState { Id = g.Id, Collapsed = true },
            _ => null,
        }).OfType<SharedItemState>().ToList();
        return states.Count > 0 ? states : null;
    }

    // ---------- The copy kept on this PC: the list is there at once at start, and without the share or the network ----------

    static string CacheFile(Store store, Group root) => Path.Combine(Path.GetDirectoryName(store.Path)!, "shared", root.Id + ".json");

    public static void WriteCache(Store store, Group root, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile(store, root))!);
            File.WriteAllText(CacheFile(store, root), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex, "Shared list: keeping a copy"); }
    }

    public static void DeleteCache(Store store, Group root)
    {
        try { File.Delete(CacheFile(store, root)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>At start, before the window: every shared list from its last copy (no network, no waiting).</summary>
    public static void LoadCached(Store store)
    {
        foreach (var root in store.Data.Groups.Where(g => !g.Shared && !string.IsNullOrWhiteSpace(g.SharedList)).ToList())
        {
            try
            {
                if (!File.Exists(CacheFile(store, root))) continue;
                var (groups, connections) = Parse(File.ReadAllText(CacheFile(store, root)), root, id => IsTaken(store, root, id));
                Apply(store, root, groups, connections);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Info($"Shared list of {root.Name}: its copy on this PC is unusable: {ex.Message}");
            }
        }
    }

    /// <summary>An id that belongs to something outside this list: the user's own item, or another list's.</summary>
    public static bool IsTaken(Store store, Group root, Guid id) =>
        store.Data.Groups.Cast<Node>().Concat(store.Data.Connections).FirstOrDefault(n => n.Id == id) is { } n
        && !(n.Shared && store.Ancestors(n).Contains(root));

    /// <summary>Reads the list from its source and puts it under the group. Throws what reading and parsing throw.</summary>
    public static async Task RefreshAsync(Store store, Group root, CancellationToken cancel = default)
    {
        var json = await ReadAsync(root.SharedList!, cancel);
        var (groups, connections) = Parse(json, root, id => IsTaken(store, root, id));
        Apply(store, root, groups, connections);
        WriteCache(store, root, json);
    }
}
