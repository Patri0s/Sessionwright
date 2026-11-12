using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sessionwright;

public sealed class ImportResult
{
    public int Added { get; set; }
    /// <summary>Connections already in the list.</summary>
    public int Skipped { get; set; }
    /// <summary>Localized, one line each: imported settings worth a look (e.g. drive redirection from an .rdp file).</summary>
    public List<string> Warnings { get; } = [];
}

public static partial class Importer
{
    /// <summary>
    /// Imports one file by extension: .rdp, .json (Sessionwright export), anything else as mRemoteNG confCons.xml.
    /// All or nothing: a damaged file adds nothing and throws <see cref="InvalidDataException"/> with a readable message;
    /// an unreadable one throws IOException / UnauthorizedAccessException.
    /// </summary>
    /// <param name="askPassword">For a Sessionwright export with passwords: asks for its password (the argument says the last
    /// one was wrong); null = import without the passwords.</param>
    /// <param name="keepCommands">For a Sessionwright export with commands to run before connecting: shown the commands,
    /// says whether to keep them (a file from someone else would otherwise run any program); null = leave them out.</param>
    public static ImportResult ImportFile(Store store, string path, Guid? parentId, Func<bool, string?>? askPassword = null,
                                          Func<IReadOnlyList<string>, bool>? keepCommands = null)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".rdp" => ImportRdp(store, ReadText(path), Path.GetFileNameWithoutExtension(path), parentId),
                ".json" => ImportSessionwright(store, ReadText(path), parentId, askPassword, keepCommands),
                ".rdg" => ImportRdcMan(store, XDocument.Load(path), parentId),
                _ => ImportMRemoteNG(store, XDocument.Load(path), parentId),
            };
        }
        catch (Exception ex) when (Unexpected(ex))
        {
            throw Malformed(ex);
        }
    }

    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // mstsc writes UTF-16 LE with a BOM (detected below); without one, a zero second byte still gives UTF-16 away.
        var fallback = bytes is [_, 0, ..] ? Encoding.Unicode : Encoding.UTF8;
        using var reader = new StreamReader(new MemoryStream(bytes), fallback, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Imports one mstsc .rdp file; <paramref name="name"/> is usually the file name.</summary>
    public static ImportResult ImportRdp(Store store, string text, string name, Guid? parentId) => Run(store, batch =>
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(':', 3);
            if (parts.Length == 3) values[parts[0]] = parts[2];
        }

        string Get(string key) => values.GetValueOrDefault(key, "");
        bool Flag(string key) => Get(key) == "1";

        var address = Get("full address");
        if (!Store.TryParseAddress(address, out var host, out var port)) throw new InvalidDataException(Loc.Get("RdpNoAddress"));
        // "server port" only fills in a port that "full address" does not spell out.
        var explicitPort = address.Trim().Trim('[', ']') != host;
        if (!explicitPort && int.TryParse(Get("server port"), out var p) && p is >= 1 and <= 65535) port = p;

        var options = new RdpOptions
        {
            Resolution = Flag("use multimon") ? ResolutionMode.AllMonitors
                : Get("screen mode id") == "2" ? ResolutionMode.Fullscreen
                : Flag("dynamic resolution") || !values.ContainsKey("desktopwidth") ? ResolutionMode.FitWindow
                : ResolutionMode.Fixed,
            Width = int.TryParse(Get("desktopwidth"), out var w) ? w : 1920,
            Height = int.TryParse(Get("desktopheight"), out var h) ? h : 1080,
            ColorDepth = int.TryParse(Get("session bpp"), out var bpp) ? bpp : 32,
            RedirectClipboard = Get("redirectclipboard") != "0",
            RedirectDrives = Get("drivestoredirect").Length > 0,
            RedirectPrinters = Flag("redirectprinters"),
            RedirectSmartCards = Flag("redirectsmartcards"),
            Audio = int.TryParse(Get("audiomode"), out var audio) && audio is >= 0 and <= 2 ? (AudioMode)audio : AudioMode.Local,
            AdminSession = Flag("administrative session"),
            RemoteShortcuts = Get("keyboardhook") != "0",
            // gatewayusagemethod 0 = direct, 4 = detect locally (treated as direct here)
            Gateway = Get("gatewayhostname") is { Length: > 0 } gw && Get("gatewayusagemethod") is not ("0" or "4") ? gw : null,
        };

        var (domain, user) = SplitUser(Get("username"));
        var connection = new Connection
        {
            Name = name, Host = host, Port = port, ParentId = parentId, Rdp = options,
            CredentialId = batch.Credential(user, domain)?.Id,
        };
        if (!batch.AddUnlessDuplicate(connection)) return;

        // Phishing .rdp files use these to reach local files or route the session (and the password) through their server.
        var title = connection.Title;
        if (options.RedirectDrives) batch.Result.Warnings.Add(Loc.Format("ImportWarnDrives", title));
        if (options.RedirectSmartCards) batch.Result.Warnings.Add(Loc.Format("ImportWarnSmartCards", title));
        if (options.Gateway != null) batch.Result.Warnings.Add(Loc.Format("ImportWarnGateway", title, options.Gateway));
    });

    // mRemoteNG settings mapped to RdpOptions, with the attribute that marks each one as inherited from the folder.
    // Names from mRemoteNG's XmlConnectionNodeSerializer28 / XmlConnectionsDeserializer.
    static readonly (string Name, string Inherit)[] MremotengSettings =
    [
        ("ConnectToConsole", "InheritUseConsoleSession"),
        ("RedirectDiskDrives", "InheritRedirectDiskDrives"),
        ("RedirectPrinters", "InheritRedirectPrinters"),
        ("RedirectSmartCards", "InheritRedirectSmartCards"),
        ("RedirectClipboard", "InheritRedirectClipboard"),
        ("RedirectSound", "InheritRedirectSound"),
        ("Resolution", "InheritResolution"),
        ("RDGatewayUsageMethod", "InheritRDGatewayUsageMethod"),
        ("RDGatewayHostname", "InheritRDGatewayHostname"),
    ];

    /// <summary>
    /// Imports the folder structure, hosts, user names and RDP settings from an mRemoteNG confCons.xml.
    /// Passwords are not imported (mRemoteNG uses AES-GCM with a 128-bit nonce, unsupported by .NET's AesGcm).
    /// </summary>
    public static ImportResult ImportMRemoteNG(Store store, XDocument doc, Guid? parentId) => Run(store, batch =>
    {
        var root = doc.Root ?? throw new InvalidDataException(Loc.Get("ImportEmptyFile"));
        if (IsTrue((string?)root.Attribute("FullFileEncryption"))) throw new InvalidDataException(Loc.Get("MremotengEncrypted"));

        // What a top-level node that inherits gets in Sessionwright: the target group's settings, else the global defaults.
        var target = store.Data.Groups.FirstOrDefault(g => g.Id == parentId);
        var defaults = store.Inherited(target, n => n.Rdp).Value ?? store.Data.Settings.Rdp;

        // Groups are created when the first connection needs them: no empty folders from non-RDP branches.
        Walk(root, new Lazy<Guid?>(() => batch.Group(null, "mRemoteNG", parentId).Id), new Dictionary<string, string>(), "");

        void Walk(XElement parent, Lazy<Guid?> group, Dictionary<string, string> inherited, string inheritedDomain)
        {
            foreach (var e in parent.Elements().Where(x => x.Name.LocalName == "Node"))
            {
                string Attr(string n) => (string?)e.Attribute(n) ?? "";
                var notes = Attr("Descr") is { Length: > 0 } d ? d : null;
                var domain = IsTrue(Attr("InheritDomain")) ? inheritedDomain : Attr("Domain");
                Guid? Credential() => IsTrue(Attr("InheritUsername")) ? null : batch.Credential(Attr("Username"), domain)?.Id;

                // mRemoteNG inherits per setting, Sessionwright the whole set: a node that sets none of them itself inherits,
                // otherwise it gets the full set with the inherited values filled in.
                var settings = new Dictionary<string, string>(inherited);
                var own = false;
                foreach (var (name, inherit) in MremotengSettings)
                    if (!IsTrue(Attr(inherit)) && e.Attribute(name) is { } a)
                    {
                        settings[name] = a.Value;
                        own = true;
                    }
                var rdp = own ? ToOptions(settings, defaults) : null;

                if (Attr("Type") == "Container")
                {
                    var name = Attr("Name") is { Length: > 0 } n ? n : Loc.Get("Unnamed");
                    var g = new Lazy<Guid?>(() => batch.Group(null, name, group.Value, x =>
                    {
                        x.Expanded = IsTrue(Attr("Expanded"));
                        x.Notes = notes;
                        x.CredentialId = Credential();
                        x.Rdp = rdp;
                    }).Id);
                    Walk(e, g, settings, domain);
                }
                else if (Attr("Type") == "Connection" && Attr("Protocol") is "" or "RDP" && Attr("Hostname") is { Length: > 0 } host)
                {
                    batch.AddUnlessDuplicate(new Connection
                    {
                        Name = Attr("Name"), Host = host,
                        Port = int.TryParse(Attr("Port"), out var p) && p is >= 1 and <= 65535 ? p : 3389,
                        ParentId = group.Value, CredentialId = Credential(), Notes = notes, Rdp = rdp,
                        Favorite = IsTrue(Attr("Favorite")),
                    });
                }
            }
        }
    });

    // ---------- Remote Desktop Connection Manager (.rdg) ----------

    // RDCMan inherits per section (inherit="FromParent" / "None"); these sections become Sessionwright's RDP options.
    static readonly string[] RdcManSections = ["remoteDesktop", "localResources", "connectionSettings", "gatewaySettings"];

    /// <summary>
    /// Imports an RDCMan file (schema 1-3, RDCMan 2.2-2.93): its groups as groups under one named after the file, its
    /// servers as connections. Logon credentials (also from credential profiles) become credentials; a saved password
    /// is taken over when this Windows account can read it (RDCMan protects it with DPAPI too).
    /// </summary>
    public static ImportResult ImportRdcMan(Store store, XDocument doc, Guid? parentId) => Run(store, batch =>
    {
        var root = doc.Root;
        if (root?.Name.LocalName != "RDCMan" || root.Element("file") is not { } file) throw new InvalidDataException(Loc.Get("NotRdcManFile"));

        var target = store.Data.Groups.FirstOrDefault(g => g.Id == parentId);
        var defaults = store.Inherited(target, n => n.Rdp).Value ?? store.Data.Settings.Rdp;
        var profiles = file.Element("credentialsProfiles")?.Elements("credentialsProfile")
            .Select(p => (Name: (string?)p.Element("profileName") ?? "", Element: p))
            .Where(p => p.Name.Length > 0).DistinctBy(p => p.Name).ToDictionary(p => p.Name, p => p.Element) ?? [];
        var unreadable = false;

        string? Value(XElement e, string name) => ((string?)e.Element("properties")?.Element(name) ?? (string?)e.Element(name))?.Trim();

        // The file node: its own group, its settings the base for everything in it.
        var fileName = Value(file, "name") is { Length: > 0 } n ? n : "RDCMan";
        var top = new Lazy<Guid?>(() => batch.Group(null, fileName, parentId, x =>
        {
            x.CredentialId = Credential(file);
            x.Rdp = Options(file, [], out _);
        }).Id);
        Walk(file, top, Settings(file, []));
        if (unreadable) batch.Result.Warnings.Add(Loc.Get("RdcManPasswordsUnreadable"));

        void Walk(XElement parent, Lazy<Guid?> group, Dictionary<string, string> inherited)
        {
            foreach (var e in parent.Elements())
            {
                var notes = Value(e, "comment") is { Length: > 0 } c ? c : null;
                if (e.Name.LocalName == "group")
                {
                    var name = Value(e, "name") is { Length: > 0 } groupName ? groupName : Loc.Get("Unnamed");
                    var rdp = Options(e, inherited, out var settings);
                    var g = new Lazy<Guid?>(() => batch.Group(null, name, group.Value, x =>
                    {
                        x.Expanded = !string.Equals(Value(e, "expanded"), "False", StringComparison.OrdinalIgnoreCase);
                        x.Notes = notes;
                        x.CredentialId = Credential(e);
                        x.Rdp = rdp;
                    }).Id);
                    Walk(e, g, settings);
                }
                else if (e.Name.LocalName == "server" && Value(e, "name") is { Length: > 0 } host)
                {
                    var rdp = Options(e, inherited, out var settings);
                    batch.AddUnlessDuplicate(new Connection
                    {
                        Name = Value(e, "displayName") is { Length: > 0 } display && display != host ? display : "",
                        Host = host,
                        Port = int.TryParse(settings.GetValueOrDefault("port"), out var p) && p is >= 1 and <= 65535 ? p : 3389,
                        ParentId = group.Value, CredentialId = Credential(e), Notes = notes, Rdp = rdp,
                    });
                }
            }
        }

        // The node's settings: what it sets itself over what it inherits.
        static Dictionary<string, string> Settings(XElement e, Dictionary<string, string> inherited)
        {
            var settings = new Dictionary<string, string>(inherited);
            foreach (var section in RdcManSections)
                if (e.Element(section) is { } s && (string?)s.Attribute("inherit") == "None")
                    foreach (var setting in s.Elements()) settings[setting.Name.LocalName] = setting.Value.Trim();
            return settings;
        }

        // Sessionwright inherits the whole set: a node that sets nothing itself inherits (null), otherwise it gets all of it.
        RdpOptions? Options(XElement e, Dictionary<string, string> inherited, out Dictionary<string, string> settings)
        {
            settings = Settings(e, inherited);
            var own = RdcManSections.Any(s => (string?)e.Element(s)?.Attribute("inherit") == "None");
            return own ? RdcManOptions(settings, defaults) : null;
        }

        Guid? Credential(XElement e)
        {
            if (e.Element("logonCredentials") is not { } logon || (string?)logon.Attribute("inherit") != "None") return null;
            // A named profile (not "Custom") is defined once in the file's credential profiles.
            var profileName = (string?)logon.Element("profileName") ?? "Custom";
            var source = profileName != "Custom" && profiles.TryGetValue(profileName, out var profile) ? profile : logon;
            var user = ((string?)source.Element("userName"))?.Trim();
            if (string.IsNullOrEmpty(user)) return null;
            var credential = batch.Credential(user, ((string?)source.Element("domain"))?.Trim() is { Length: > 0 } d ? d : null,
                                              profileName != "Custom" ? profileName : null);
            if (credential is { Secret: null } && (string?)source.Element("password") is { Length: > 0 } sealedPassword)
            {
                if (RdcManPassword(sealedPassword) is { } password) credential.SetPassword(password);
                else unreadable = true;
            }
            return credential?.Id;
        }
    });

    // RDCMan stores DPAPI(current user) of the UTF-16 password, Base64.
    static string? RdcManPassword(string base64)
    {
        try
        {
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(base64), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.Unicode.GetString(plain).TrimEnd('\0') is { Length: > 0 } p ? p : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return null; }
    }

    static RdpOptions RdcManOptions(Dictionary<string, string> settings, RdpOptions defaults)
    {
        var o = defaults.Clone();
        string? Get(string name) => settings.GetValueOrDefault(name);
        bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        if (Get("connectToConsole") is { } console) o.AdminSession = IsTrue(console);
        if (Get("redirectClipboard") is { } clipboard) o.RedirectClipboard = IsTrue(clipboard);
        if (Get("redirectDrives") is { } drives) o.RedirectDrives = IsTrue(drives);
        if (Get("redirectPrinters") is { } printers) o.RedirectPrinters = IsTrue(printers);
        if (Get("redirectSmartCards") is { } cards) o.RedirectSmartCards = IsTrue(cards);
        // Client / Remote / NoSound.
        if (Get("audioRedirection") is { } audio)
            o.Audio = Is(audio, "Remote") ? AudioMode.Remote : Is(audio, "NoSound") ? AudioMode.None : AudioMode.Local;
        // Client / Remote / FullScreenClient: Windows shortcuts go to the session unless they stay on this computer.
        if (Get("keyboardHook") is { } hook) o.RemoteShortcuts = !Is(hook, "Client");
        if (Get("colorDepth") is { } depth && int.TryParse(depth, out var bits) && bits is 15 or 16 or 24 or 32) o.ColorDepth = bits;
        if (IsTrue(Get("fullScreen"))) o.Resolution = ResolutionMode.Fullscreen;
        else if (IsTrue(Get("sameSizeAsClientArea"))) o.Resolution = ResolutionMode.FitWindow;
        else if (Get("size") is { } size && RdcManSize().Match(size) is { Success: true } m)
            (o.Resolution, o.Width, o.Height) = (ResolutionMode.Fixed, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        if (Get("enabled") is { } gateway)
            o.Gateway = IsTrue(gateway) && Get("hostName") is { Length: > 0 } hostName ? hostName : null;
        return o;
    }

    [GeneratedRegex(@"^(\d{3,5})\s*x\s*(\d{3,5})$", RegexOptions.IgnoreCase)]
    private static partial Regex RdcManSize();

    static RdpOptions ToOptions(Dictionary<string, string> settings, RdpOptions defaults)
    {
        var o = defaults.Clone();
        string? Get(string name) => settings.GetValueOrDefault(name);
        bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        if (Get("ConnectToConsole") is { } console) o.AdminSession = IsTrue(console);
        // RDPDiskDrives (None/Local/All/Custom) since confCons 2.8, a bool before.
        if (Get("RedirectDiskDrives") is { } drives) o.RedirectDrives = !(Is(drives, "None") || Is(drives, "false") || drives == "");
        if (Get("RedirectPrinters") is { } printers) o.RedirectPrinters = IsTrue(printers);
        if (Get("RedirectSmartCards") is { } cards) o.RedirectSmartCards = IsTrue(cards);
        if (Get("RedirectClipboard") is { } clipboard) o.RedirectClipboard = IsTrue(clipboard);
        if (Get("RedirectSound") is { } sound)
            o.Audio = Is(sound, "LeaveAtRemoteComputer") ? AudioMode.Remote : Is(sound, "DoNotPlay") ? AudioMode.None : AudioMode.Local;
        if (Get("Resolution") is { } resolution)
        {
            // SmartSize scales the remote desktop to the window: closest is fit to window.
            if (Is(resolution, "Fullscreen")) o.Resolution = ResolutionMode.Fullscreen;
            else if (FixedResolution().Match(resolution) is { Success: true } m)
                (o.Resolution, o.Width, o.Height) = (ResolutionMode.Fixed, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            else o.Resolution = ResolutionMode.FitWindow;
        }
        // RDGatewayUsageMethod: Never / Always / Detect.
        if (Get("RDGatewayUsageMethod") is { } usage)
            o.Gateway = (Is(usage, "Always") || Is(usage, "Detect")) && Get("RDGatewayHostname") is { Length: > 0 } gateway ? gateway : null;
        return o;
    }

    [GeneratedRegex(@"^Res(\d{3,5})x(\d{3,5})$", RegexOptions.IgnoreCase)]
    private static partial Regex FixedResolution();

    static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // ---------- Sessionwright's own export format ----------

    public sealed class ExportFile
    {
        public const int CurrentVersion = 1;
        public string Format { get; set; } = "Sessionwright";
        public int Version { get; set; } = CurrentVersion;
        public List<Group> Groups { get; set; } = [];
        public List<Connection> Connections { get; set; } = [];
        public List<Credential> Credentials { get; set; } = [];
        /// <summary>Set when the credentials carry passwords (their Secret), encrypted with a password chosen at export.</summary>
        public ExportProtection? Protection { get; set; }
    }

    /// <summary>
    /// Passwords in an export: AES-256-GCM per password (nonce, tag, ciphertext), the key derived from the export password
    /// with PBKDF2-SHA256. DPAPI secrets only open on the machine and account that made them, so they cannot travel.
    /// </summary>
    public sealed class ExportProtection
    {
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; } = 600_000;
        public byte[] Salt { get; set; } = [];

        public byte[] Key(string password) => Rfc2898DeriveBytes.Pbkdf2(password, Salt, Iterations, HashAlgorithmName.SHA256, 32);

        public static byte[] Seal(byte[] key, string secret)
        {
            var plain = Encoding.UTF8.GetBytes(secret);
            var sealedBytes = new byte[12 + 16 + plain.Length];
            var nonce = sealedBytes.AsSpan(0, 12);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16));
            return sealedBytes;
        }

        /// <summary>The secret, or null for a wrong key (or a damaged value).</summary>
        public static string? Open(byte[] key, byte[] sealedBytes)
        {
            if (sealedBytes.Length < 28) return null;
            var plain = new byte[sealedBytes.Length - 28];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (AuthenticationTagMismatchException) { return null; }
        }
    }

    /// <summary>
    /// Groups, connections and credential names as JSON; with <paramref name="password"/> also the saved passwords,
    /// encrypted with it (see <see cref="ExportProtection"/>). Without it passwords are never exported.
    /// </summary>
    public static string Export(Store store, string? password = null)
    {
        ExportProtection? protection = null;
        byte[]? key = null;
        if (!string.IsNullOrEmpty(password))
        {
            protection = new ExportProtection { Salt = RandomNumberGenerator.GetBytes(16) };
            key = protection.Key(password);
        }
        return JsonSerializer.Serialize(new ExportFile
        {
            Groups = [.. store.Data.Groups],
            Connections = [.. store.Data.Connections],
            Credentials = [.. store.Data.Credentials.Select(c => new Credential
            {
                Id = c.Id, Name = c.Name, User = c.User, Domain = c.Domain,
                Secret = key != null && Readable(c) is { } secret ? ExportProtection.Seal(key, secret) : null,
            })],
            Protection = protection,
        }, Store.Json);
    }

    // A secret saved by another Windows account (copied data file) cannot be read: exported without it.
    static string? Readable(Credential c)
    {
        if (c.Secret == null) return null;
        try { return Store.Unprotect(c.Secret); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>
    /// Adds the connections of a Sessionwright export, recreating their group structure under <paramref name="parentId"/>.
    /// Connections whose id is already in the list are skipped (so duplicates of one host survive a round trip);
    /// groups are reused by id, else by name under the same parent, and created only for imported connections.
    /// </summary>
    public static ImportResult ImportSessionwright(Store store, string json, Guid? parentId, Func<bool, string?>? askPassword = null,
                                                   Func<IReadOnlyList<string>, bool>? keepCommands = null) => Run(store, batch =>
    {
        var file = JsonSerializer.Deserialize<ExportFile>(json, Store.JsonOptions(batch.Result.Warnings));
        if (file is not { Format: "Sessionwright" }) throw new InvalidDataException(Loc.Get("NotSessionwrightFile"));
        if (file.Version > ExportFile.CurrentVersion) throw new InvalidDataException(Loc.Format("ExportTooNew", file.Version));
        var groups = (file.Groups ?? []).Where(g => g != null).DistinctBy(g => g.Id).ToDictionary(g => g.Id);
        var credentials = (file.Credentials ?? []).Where(c => c != null).DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        var passwords = OpenPasswords(file.Protection, credentials.Values, askPassword, batch.Result);
        var commands = groups.Values.Select(g => g.BeforeConnect).Concat((file.Connections ?? []).Select(c => c?.BeforeConnect))
            .Where(b => b is { Command.Length: > 0 }).Select(b => b!.Command).Distinct().ToList();
        var keep = commands.Count > 0 && keepCommands?.Invoke(commands) == true;
        if (commands.Count > 0 && !keep) batch.Result.Warnings.Add(Loc.Get("ImportCommandsLeftOut"));
        // "Nothing" (an empty command) switches a group's off and runs nothing: always safe to take.
        BeforeConnect? Before(BeforeConnect? b) => b is { Command.Length: 0 } || keep ? b?.Clone() : null;
        var mapped = new Dictionary<Guid, Group>();

        Guid? Credential(Guid? id)
        {
            if (id is not { } key) return null;
            if (batch.Credentials.Any(c => c.Id == key)) return key;
            if (!credentials.TryGetValue(key, out var c) || batch.Credential(c.User, c.Domain, c.Name) is not { } target) return null;
            // A password from the file only where there is none yet: an existing saved one stays.
            if (target.Secret == null && passwords.TryGetValue(key, out var password)) target.SetPassword(password);
            return target.Id;
        }

        Group? GroupFor(Guid? id, int depth = 0)
        {
            if (id is not { } key || !groups.TryGetValue(key, out var g) || depth > 64) return null; // depth: cyclic file
            if (mapped.TryGetValue(key, out var done)) return done;
            var parent = GroupFor(g.ParentId, depth + 1);
            return mapped[key] = batch.Group(g.Id, g.Name ?? "", parent?.Id ?? parentId, x =>
            {
                x.Expanded = g.Expanded;
                x.Notes = g.Notes;
                x.Links = g.Links?.Select(l => l.Clone()).ToList();
                x.Rdp = g.Rdp?.Clone();
                x.Color = g.Color;
                x.Order = g.Order;
                x.AutoStart = g.AutoStart?.Clone();
                x.ShutDownVmAfter = g.ShutDownVmAfter;
                x.BeforeConnect = Before(g.BeforeConnect);
                x.CredentialId = Credential(g.CredentialId);
            });
        }

        foreach (var c in file.Connections ?? [])
        {
            if (c == null || string.IsNullOrWhiteSpace(c.Host)) continue;
            if (batch.Connections.Any(x => x.Id == c.Id))
            {
                batch.Result.Skipped++;
                continue;
            }
            // A machine link whose subscription is no id is not one Azure gave out: the connection comes without it.
            var vm = c.AzureVm is { } link && Guid.TryParse(link.SubscriptionId, out _) ? link.Clone() : null;
            // What the exporting PC was doing with the VM (a postponed shutdown to put back, today's auto-start) is not
            // this PC's to finish: it would rewrite the schedule in Azure from stale times.
            if (vm != null) (vm.ShutdownOriginal, vm.ShutdownRestoreAfter, vm.ShutdownSkipped, vm.AutoStartedOn) = (null, null, false, null);
            batch.Add(new Connection
            {
                Id = c.Id, Name = c.Name ?? "", Host = c.Host, Port = c.Port is >= 1 and <= 65535 ? c.Port : 3389,
                ParentId = GroupFor(c.ParentId)?.Id ?? parentId, CredentialId = Credential(c.CredentialId),
                Rdp = c.Rdp?.Clone(), Notes = c.Notes, Links = c.Links?.Select(l => l.Clone()).ToList(), Tags = c.Tags, Favorite = c.Favorite, Color = c.Color, AzureVm = vm, AutoStart = c.AutoStart?.Clone(), ShutDownVmAfter = c.ShutDownVmAfter,
                Order = c.Order, FavoriteOrder = c.FavoriteOrder, BeforeConnect = Before(c.BeforeConnect),
            });
        }
    });

    /// <summary>The file's passwords by credential id; asks for the export password until it fits, or none if cancelled.</summary>
    static Dictionary<Guid, string> OpenPasswords(ExportProtection? protection, IEnumerable<Credential> credentials,
                                                  Func<bool, string?>? askPassword, ImportResult result)
    {
        var sealedSecrets = credentials.Where(c => c.Secret != null).ToList();
        if (protection == null || sealedSecrets.Count == 0) return [];
        if (protection.Kdf != "PBKDF2-SHA256" || protection.Iterations is < 1 or > 10_000_000 || protection.Salt.Length < 8)
            throw new InvalidDataException(Loc.Get("ExportProtectionUnknown"));
        for (var wrong = false; askPassword?.Invoke(wrong) is { } password; wrong = true)
        {
            var key = protection.Key(password);
            if (ExportProtection.Open(key, sealedSecrets[0].Secret!) == null) continue;
            return sealedSecrets.Select(c => (c.Id, Secret: ExportProtection.Open(key, c.Secret!)))
                .Where(x => x.Secret != null).ToDictionary(x => x.Id, x => x.Secret!);
        }
        result.Warnings.Add(Loc.Get("ImportWithoutPasswords"));
        return [];
    }

    static (string? Domain, string User) SplitUser(string value)
    {
        var slash = value.IndexOf('\\');
        return slash > 0 ? (value[..slash], value[(slash + 1)..]) : (null, value);
    }

    // ---------- All-or-nothing import ----------

    static ImportResult Run(Store store, Action<Batch> import)
    {
        var batch = new Batch(store);
        try
        {
            import(batch);
        }
        catch (Exception ex) when (Unexpected(ex))
        {
            throw Malformed(ex);
        }
        return batch.Commit();
    }

    // Anything else thrown while reading a file means malformed input (bad XML/JSON, null where a value belongs, ...).
    static bool Unexpected(Exception ex) => ex is not (IOException or UnauthorizedAccessException or InvalidDataException or OutOfMemoryException);

    static InvalidDataException Malformed(Exception ex) => new(Loc.Format("ImportUnreadable", ex.Message), ex);

    /// <summary>Works on copies of the store's lists; only <see cref="Commit"/> touches the store, so a failing file adds nothing.</summary>
    sealed class Batch(Store store)
    {
        public readonly List<Group> Groups = [.. store.Data.Groups];
        public readonly List<Connection> Connections = [.. store.Data.Connections];
        public readonly List<Credential> Credentials = [.. store.Data.Credentials];
        public readonly ImportResult Result = new();
        readonly int _groups = store.Data.Groups.Count, _connections = store.Data.Connections.Count, _credentials = store.Data.Credentials.Count;
        HashSet<string>? _keys;

        public Credential? Credential(string? user, string? domain, string? name = null) =>
            Store.FindOrCreateCredential(Credentials, user, domain, name);

        /// <summary>The group with this id, else the one with this name under the same parent, else a new one.</summary>
        public Group Group(Guid? id, string name, Guid? parentId, Action<Group>? init = null)
        {
            var group = Groups.FirstOrDefault(g => g.Id == id)
                        ?? Groups.FirstOrDefault(g => g.ParentId == parentId && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (group != null) return group;
            group = new Group { Id = id ?? Guid.NewGuid(), Name = name, ParentId = parentId };
            init?.Invoke(group);
            Groups.Add(group);
            return group;
        }

        public void Add(Connection c)
        {
            Connections.Add(c);
            _keys?.Add(Key(c));
            Result.Added++;
        }

        /// <summary>Adds the connection unless one with the same host, port, user and group path is already there.</summary>
        public bool AddUnlessDuplicate(Connection c)
        {
            _keys ??= Connections.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_keys.Contains(Key(c)))
            {
                Result.Skipped++;
                return false;
            }
            Add(c);
            return true;
        }

        string Key(Connection c)
        {
            var path = new List<string>();
            for (var (id, depth) = (c.ParentId, 0); id is { } key && depth < 64 && Groups.FirstOrDefault(g => g.Id == key) is { } g; id = g.ParentId, depth++)
                path.Add(g.Name);
            var user = Credentials.FirstOrDefault(x => x.Id == c.CredentialId)?.FullUser;
            return $"{c.Host}|{c.Port}|{user}|{string.Join("/", path)}";
        }

        public ImportResult Commit()
        {
            foreach (var c in Credentials.Skip(_credentials)) store.Data.Credentials.Add(c);
            foreach (var g in Groups.Skip(_groups)) store.Data.Groups.Add(g);
            foreach (var c in Connections.Skip(_connections)) store.Data.Connections.Add(c);
            return Result;
        }
    }
}
