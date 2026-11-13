using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sessionwright;

public sealed class Store
{
    /// <summary>Explorer-style ordering: "SRV-2" before "SRV-10".</summary>
    public static IComparer<string> NaturalOrder { get; } = Comparer<string>.Create(StrCmpLogicalW);

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int StrCmpLogicalW(string x, string y);

    static readonly byte[] Entropy = "Sessionwright.v1"u8.ToArray();

    internal static readonly JsonSerializerOptions Json = JsonOptions(null);

    /// <param name="warnings">Collects unknown enum values that were read as the default; null ignores them.</param>
    internal static JsonSerializerOptions JsonOptions(List<string>? warnings) => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new TolerantEnumConverter(warnings) },
    };

    /// <summary>Format of data.json written by this build.</summary>
    public const int CurrentSchema = 1;

    // Step i upgrades schema i to i + 1. 0 = written before SchemaVersion existed: same shape, nothing to change.
    static readonly Action<AppData>[] Migrations = [_ => { }];

    /// <summary>%APPDATA%\Sessionwright\data.json, or the SESSIONWRIGHT_DATA environment variable (portable / test data).</summary>
    public static string DefaultPath { get; } = Environment.GetEnvironmentVariable("SESSIONWRIGHT_DATA") is { Length: > 0 } custom
        ? System.IO.Path.GetFullPath(custom)
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sessionwright", "data.json");

    public string Path { get; }
    public AppData Data { get; }
    /// <summary>Top level of the connection tree (groups first, then connections).</summary>
    public ObservableCollection<Node> Roots { get; } = [];
    /// <summary>Set when data.json was damaged or missing and a .tmp/.bak copy was loaded instead: tell the user.</summary>
    public Recovery? Recovered { get; }
    /// <summary>What loading had to repair (unknown enum values, broken references, duplicates); for the log.</summary>
    public List<string> Warnings { get; }

    // After a recovery the damaged data.json must not become the .bak on the next save: it would replace the good copy.
    bool _skipBackup;

    /// <param name="Source">The .tmp or .bak file the data was loaded from.</param>
    /// <param name="CorruptCopy">Where the damaged data.json was copied; null when it was missing.</param>
    public sealed record Recovery(string Source, string? CorruptCopy)
    {
        public string Message => CorruptCopy == null
            ? Loc.Format("DataRestored", Source)
            : Loc.Format("DataRecovered", Source, CorruptCopy);
    }

    Store(string path, AppData data, List<string> warnings, Recovery? recovered)
    {
        Path = path;
        Data = data;
        Warnings = warnings;
        Recovered = recovered;
        _skipBackup = recovered?.CorruptCopy != null;
        Normalize();
        BuildTree();
    }

    /// <summary>
    /// Loads data.json. When it cannot be parsed it is copied to data.json.corrupt-&lt;time&gt; (never rotated) and the
    /// .tmp, then the .bak copy is loaded instead; a missing data.json is restored from them too (see <see cref="Recovered"/>).
    /// I/O errors, data from a newer version and a damaged file with no readable copy throw: nothing is silently replaced.
    /// </summary>
    public static Store Load(string path)
    {
        var warnings = new List<string>();
        Exception? error = null;

        AppData? TryRead(string file)
        {
            if (!File.Exists(file)) return null;
            warnings.Clear();
            try
            {
                return JsonSerializer.Deserialize<AppData>(File.ReadAllText(file), JsonOptions(warnings)) ?? throw new InvalidDataException(file);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                error ??= ex;
                return null;
            }
        }

        var exists = File.Exists(path);
        if (TryRead(path) is { } data) return new Store(path, Migrate(data), warnings, null);

        string? corrupt = null;
        if (exists)
        {
            corrupt = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, corrupt, overwrite: true);
        }
        // .tmp is newer than .bak when present: a save that was interrupted before the swap.
        foreach (var copy in new[] { path + ".tmp", path + ".bak" })
            if (TryRead(copy) is { } recovered) return new Store(path, Migrate(recovered), warnings, new Recovery(copy, corrupt));

        if (error != null) ExceptionDispatchInfo.Throw(error);
        return new Store(path, Migrate(new AppData()), warnings, null);
    }

    static AppData Migrate(AppData data)
    {
        // A newer build may have stored things this one would drop on the next save.
        if (data.SchemaVersion > CurrentSchema) throw new InvalidDataException(Loc.Format("DataTooNew", data.SchemaVersion));
        for (data.SchemaVersion = Math.Max(data.SchemaVersion, 0); data.SchemaVersion < CurrentSchema; data.SchemaVersion++)
            Migrations[data.SchemaVersion](data);
        return data;
    }

    /// <summary>Repairs what a hand-edited or damaged file can contain, so the tree and lookups never see broken references.</summary>
    void Normalize()
    {
        Data.Settings ??= new();
        Data.Settings.Rdp ??= new();
        Data.Groups = Unique(Data.Groups, g => g.Id, "groups");
        Data.Connections = Unique(Data.Connections, c => c.Id, "connections");
        Data.Credentials = Unique(Data.Credentials, c => c.Id, "credentials");

        var groups = Data.Groups.ToDictionary(g => g.Id);
        var credentials = Data.Credentials.Select(c => c.Id).ToHashSet();
        foreach (var n in Data.Groups.Cast<Node>().Concat(Data.Connections))
        {
            n.Name ??= ""; // natural sort (StrCmpLogicalW) must not get null
            if (n is Connection c) c.Host ??= "";
            if (n.ParentId is { } p && !groups.ContainsKey(p))
            {
                n.ParentId = null;
                Warnings.Add($"\"{n.Name}\": parent group {p} not found, moved to the top level.");
            }
            if (n.CredentialId is { } id && !credentials.Contains(id))
            {
                n.CredentialId = null;
                Warnings.Add($"\"{n.Name}\": credential {id} not found, reference removed.");
            }
        }

        // A cycle hides its groups from the tree: cut the link that closes it.
        foreach (var g in Data.Groups)
        {
            var path = new HashSet<Guid> { g.Id };
            for (var n = g; n.ParentId is { } p && groups.TryGetValue(p, out var parent); n = parent)
                if (!path.Add(p))
                {
                    n.ParentId = null;
                    Warnings.Add($"Group \"{n.Name}\": parent cycle broken, moved to the top level.");
                    break;
                }
        }

        ObservableCollection<T> Unique<T>(ObservableCollection<T>? items, Func<T, Guid> id, string kind)
        {
            var unique = new ObservableCollection<T>(((IEnumerable<T>?)items ?? []).Where(x => x != null).DistinctBy(id));
            if (items != null && unique.Count != items.Count)
                Warnings.Add($"Dropped {items.Count - unique.Count} {kind} with a duplicate id or no data.");
            return unique;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // A shared list's items are the team's, not this file's: only what the user set on them is kept, with the group.
            foreach (var root in Data.Groups.Where(g => g.SharedLoaded)) root.SharedState = SharedLists.StateOf(this, root);
            var own = !Data.Groups.Any(g => g.Shared) && !Data.Connections.Any(c => c.Shared) ? Data : new AppData
            {
                SchemaVersion = Data.SchemaVersion, Credentials = Data.Credentials, Settings = Data.Settings,
                Groups = new(Data.Groups.Where(g => !g.Shared)), Connections = new(Data.Connections.Where(c => !c.Shared)),
            };
            JsonSerializer.Serialize(stream, own, Json);
            stream.Flush(flushToDisk: true); // the swap below must never publish a file that is not on disk yet
        }
        if (File.Exists(Path)) File.Replace(tmp, Path, _skipBackup ? null : Path + ".bak");
        else File.Move(tmp, Path);
        _skipBackup = false;
    }

    /// <summary>
    /// Rebuilds the tree: in each group (and the top level) the groups first, then the connections, each in their manual
    /// order; nodes not placed yet (new, imported, files from before ordering) follow by name. The positions are then
    /// renumbered 1..n, so every node has one.
    /// </summary>
    public void BuildTree()
    {
        var groups = Data.Groups.ToDictionary(g => g.Id);
        foreach (var g in groups.Values) g.Children.Clear();
        Roots.Clear();

        var buckets = Data.Groups.Cast<Node>().Concat(Data.Connections)
            .GroupBy(n => n.ParentId is { } p && groups.ContainsKey(p) ? p : (Guid?)null);
        foreach (var bucket in buckets)
        {
            var children = bucket.Key is { } id ? groups[id].Children : Roots;
            foreach (var kind in new[] { bucket.OfType<Group>().Cast<Node>(), bucket.OfType<Connection>() })
            {
                var position = 0;
                foreach (var n in InOrder(kind))
                {
                    n.Order = ++position;
                    children.Add(n);
                }
            }
        }
    }

    static IOrderedEnumerable<Node> InOrder(IEnumerable<Node> nodes) =>
        nodes.OrderBy(n => n.Order ?? double.MaxValue).ThenBy(n => n is Connection c ? c.Title : n.Name, NaturalOrder);

    /// <summary>
    /// Moves <paramref name="n"/> into <paramref name="parent"/> (null = top level), just before <paramref name="before"/>
    /// when that is a node of the same kind there, else after all of its kind. Call <see cref="BuildTree"/> after.
    /// </summary>
    public void Move(Node n, Group? parent, Node? before)
    {
        n.ParentId = parent?.Id;
        var siblings = InOrder((n is Group ? Data.Groups.Cast<Node>() : Data.Connections).Where(s => s != n && s.ParentId == n.ParentId)).ToList();
        var at = before != null && before != n ? siblings.IndexOf(before) : -1;
        siblings.Insert(at < 0 ? siblings.Count : at, n);
        for (var i = 0; i < siblings.Count; i++) siblings[i].Order = i + 1;
    }

    /// <summary>Puts the content of <paramref name="g"/> (null = top level) back in order by name. Call <see cref="BuildTree"/> after.</summary>
    /// <summary>The favorites in the user's order (dragged in the favorites list), then by name.</summary>
    public List<Connection> Favorites() =>
        [.. Data.Connections.Where(c => c.Favorite).OrderBy(c => c.FavoriteOrder).ThenBy(c => c.Title, NaturalOrder)];

    /// <summary>On: at the end of the favorites. Off: off them.</summary>
    public void SetFavorite(Connection c, bool on)
    {
        if (on && !c.Favorite) c.FavoriteOrder = Favorites().Select(f => f.FavoriteOrder).DefaultIfEmpty(0).Max() + 1;
        c.Favorite = on;
    }

    /// <summary>Puts <paramref name="c"/> at <paramref name="index"/> of the favorites (making it one), the others numbered after it.</summary>
    public void MoveFavorite(Connection c, int index)
    {
        var order = Favorites().Where(f => f != c).ToList();
        order.Insert(Math.Clamp(index, 0, order.Count), c);
        c.Favorite = true;
        for (var i = 0; i < order.Count; i++) order[i].FavoriteOrder = i + 1;
    }

    public void SortByName(Group? g)
    {
        foreach (var n in Data.Groups.Cast<Node>().Concat(Data.Connections).Where(n => n.ParentId == g?.Id)) n.Order = null;
    }

    public Group? Parent(Node n) => n.ParentId is { } p ? Data.Groups.FirstOrDefault(g => g.Id == p) : null;

    public IEnumerable<Group> Ancestors(Node n)
    {
        // Depth guard protects against a hand-edited file with a parent cycle.
        var depth = 0;
        for (var g = Parent(n); g != null && depth++ < 64; g = Parent(g)) yield return g;
    }

    /// <summary>
    /// A connection's links: its own, then its groups' from the nearest, templates filled in for it. One that is not a
    /// safe web address once filled in is left out (see <see cref="Sessionwright.Links.IsSafe"/>).
    /// </summary>
    public List<ResolvedLink> LinksOf(Connection c)
    {
        var own = (c.Links ?? []).Select(l => (Link: l, From: (Group?)null));
        var inherited = Ancestors(c).SelectMany(g => (g.Links ?? []).Select(l => (Link: l, From: (Group?)g)));
        return [.. own.Concat(inherited)
            .Select(x => new ResolvedLink(Sessionwright.Links.NameOf(x.Link), Sessionwright.Links.Expand(x.Link.Url, c), x.From))
            .Where(l => Sessionwright.Links.IsSafe(l.Url))];
    }

    /// <summary>A group's links to open as they are (with its parents'); templates belong to its connections, not to it.</summary>
    public List<ResolvedLink> LinksOf(Group g) =>
        [.. new[] { g }.Concat(Ancestors(g)).SelectMany(x => (x.Links ?? []).Select(l => new ResolvedLink(Sessionwright.Links.NameOf(l), l.Url.Trim(), x == g ? null : x)))
            .Where(l => !Sessionwright.Links.HasPlaceholders(l.Url) && Sessionwright.Links.IsSafe(l.Url))];

    public string PathOf(Node n) => string.Join(" / ", Ancestors(n).Reverse().Select(g => g.Name));

    /// <summary>The node's own color, else the nearest group's.</summary>
    public string? ColorOf(Node n) => n.Color ?? Ancestors(n).Select(g => g.Color).FirstOrDefault(c => c != null);

    /// <summary>
    /// What a node inherits from <paramref name="parent"/> and its ancestors: the first group that sets the value
    /// (null source = none sets it).
    /// </summary>
    public (Group? Source, T? Value) Inherited<T>(Group? parent, Func<Node, T?> value) where T : class
    {
        var chain = parent == null ? [] : Ancestors(parent).Prepend(parent);
        foreach (var g in chain)
            if (value(g) is { } v) return (g, v);
        return (null, null);
    }

    /// <summary>Its own auto-start, else its nearest group's; null when none (or switched off).</summary>
    /// <summary>Whether its sessions start with "shut the machine down after disconnecting" on: its own choice, else its nearest group's.</summary>
    public bool ShutDownVmAfterOf(Connection c) =>
        c.ShutDownVmAfter ?? Ancestors(c).Select(g => g.ShutDownVmAfter).FirstOrDefault(v => v != null) ?? false;

    /// <summary>Its own command to run before connecting, else its nearest group's; null when none (or switched off).</summary>
    public BeforeConnect? BeforeConnectOf(Connection c) =>
        (c.BeforeConnect ?? Ancestors(c).Select(g => g.BeforeConnect).FirstOrDefault(b => b != null)) is { IsSet: true } before ? before : null;

    public VmAutoStart? AutoStartOf(Connection c) =>
        (c.AutoStart ?? Ancestors(c).Select(g => g.AutoStart).FirstOrDefault(a => a != null)) is { Days: not AutoStartDays.Off } plan ? plan : null;

    /// <summary>Not into itself or its own content; a shared list's items stay where the list puts them, and nothing moves into them.</summary>
    public bool CanMove(Node n, Group? target) =>
        !n.Shared && (target == null || (target != n && !target.Shared && !Ancestors(target).Contains(n)));

    /// <summary>The user's own group a new item goes into: the one given, or the shared list's own group when it is one of the list's.</summary>
    public Group? OwnGroup(Group? g) => g is { Shared: true } ? Ancestors(g).FirstOrDefault(a => !a.Shared) : g;

    /// <summary>Effective credential and RDP options: connection → nearest group → global defaults.</summary>
    public (Credential? Credential, RdpOptions Options) Resolve(Connection c)
    {
        var chain = Ancestors(c).Prepend<Node>(c).ToList();
        var credId = chain.Select(n => n.CredentialId).FirstOrDefault(id => id != null);
        var options = chain.Select(n => n.Rdp).FirstOrDefault(o => o != null) ?? Data.Settings.Rdp;
        return (Data.Credentials.FirstOrDefault(x => x.Id == credId), options);
    }

    public void Delete(Node n)
    {
        if (n is Group g)
        {
            foreach (var child in Data.Groups.Where(x => x.ParentId == g.Id).Cast<Node>()
                         .Concat(Data.Connections.Where(x => x.ParentId == g.Id)).ToList())
                Delete(child);
            Data.Groups.Remove(g);
        }
        else Data.Connections.Remove((Connection)n);
    }

    public void DeleteCredential(Credential c)
    {
        foreach (var n in Data.Groups.Cast<Node>().Concat(Data.Connections).Where(n => n.CredentialId == c.Id))
            n.CredentialId = null;
        Data.Credentials.Remove(c);
    }

    public IEnumerable<Connection> Search(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Data.Connections
            .Where(c => tokens.All(t => $"{c.Name} {c.Address} {c.Tags} {PathOf(c)} {c.Notes}".Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(c => c.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(c => c.LastUsed)
            .ThenBy(c => c.Title, NaturalOrder);
    }

    /// <summary>Parses "host", "host:port" or "[ipv6]:port" typed into quick connect.</summary>
    public static bool TryParseAddress(string text, out string host, out int port)
    {
        host = text.Trim();
        port = 3389;
        var colon = host.LastIndexOf(':');
        // A single colon (or one after "]") separates the port; more colons = bare IPv6.
        if (colon > 0 && (host.IndexOf(':') == colon || host[colon - 1] == ']'))
        {
            if (!int.TryParse(host[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
            host = host[..colon];
        }
        host = host.Trim('[', ']');
        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    /// <summary>
    /// The library credential with this user and domain (case-insensitive), else a new one added to the library
    /// (named <paramref name="name"/>, else after the user). Null for an empty user.
    /// </summary>
    public Credential? FindOrCreateCredential(string? user, string? domain, string? name = null) =>
        FindOrCreateCredential(Data.Credentials, user, domain, name);

    internal static Credential? FindOrCreateCredential(IList<Credential> credentials, string? user, string? domain, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(user)) return null;
        domain = string.IsNullOrWhiteSpace(domain) ? null : domain;
        var existing = credentials.FirstOrDefault(c =>
            string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Domain ?? "", domain ?? "", StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var created = new Credential { User = user, Domain = domain };
        created.Name = name is { Length: > 0 } ? name : created.FullUser;
        credentials.Add(created);
        return created;
    }

    // The plaintext byte copies are wiped: strings cannot be, but these buffers would otherwise linger in the heap.
    public static byte[] Protect(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        try { return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string Unprotect(byte[] secret)
    {
        var plain = ProtectedData.Unprotect(secret, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

/// <summary>
/// Enums as names. An unknown value (e.g. written by a newer version) reads as the default plus a warning instead of
/// failing the whole file.
/// </summary>
sealed class TolerantEnumConverter(List<string>? warnings) : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsEnum;

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type), warnings)!;

    sealed class Converter<T>(List<string>? warnings) : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            string text;
            if (reader.TokenType == JsonTokenType.String) text = reader.GetString()!;
            else
            {
                using var value = JsonDocument.ParseValue(ref reader); // numbers, and consumes objects/arrays whole
                text = value.RootElement.GetRawText();
            }
            if (Enum.TryParse<T>(text, ignoreCase: true, out var result) && Enum.IsDefined(result)) return result;
            warnings?.Add($"Unknown {typeof(T).Name} value {text}, using {default(T)}.");
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
