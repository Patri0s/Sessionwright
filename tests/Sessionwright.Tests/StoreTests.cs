using System.IO;
using System.Xml.Linq;
using Sessionwright;
using Xunit;

public class StoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "SessionwrightTests", Guid.NewGuid().ToString("N"));
    string File => Path.Combine(_dir, "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Order_ByNameFirst_ThenAsMovedAndSaved()
    {
        var store = Store.Load(File);
        var g = new Group { Name = "Prod" };
        store.Data.Groups.Add(g);
        Connection C(string name, Guid? parent = null) { var c = new Connection { Name = name, Host = name, ParentId = parent }; store.Data.Connections.Add(c); return c; }
        var b = C("b"); var a = C("a"); var c10 = C("srv10"); var c2 = C("srv2");
        store.BuildTree();
        Assert.Equal(["Prod", "a", "b", "srv2", "srv10"], store.Roots.Select(n => n.Name)); // groups first, natural order

        store.Move(c10, null, a); // before "a"
        store.BuildTree();
        Assert.Equal(["Prod", "srv10", "a", "b", "srv2"], store.Roots.Select(n => n.Name));

        store.Move(a, g, null); // into the group
        var d = C("d", g.Id);   // added later: after the placed ones
        store.BuildTree();
        Assert.Equal(["a", "d"], g.Children.Select(n => n.Name));
        store.Save();

        var loaded = Store.Load(File);
        loaded.BuildTree();
        Assert.Equal(["Prod", "srv10", "b", "srv2"], loaded.Roots.Select(n => n.Name));

        loaded.SortByName(null);
        loaded.BuildTree();
        Assert.Equal(["Prod", "b", "srv2", "srv10"], loaded.Roots.Select(n => n.Name));
    }

    [Fact]
    public void LeftRunning_IsSavedAndShownWithItsAge()
    {
        var store = Store.Load(File);
        var at = new DateTime(2026, 10, 26, 8, 0, 0);
        store.Data.Connections.Add(new Connection { Host = "srv", LeftRunningAt = at });
        store.Save();

        var c = Assert.Single(Store.Load(File).Data.Connections);
        Assert.Equal(at, c.LeftRunningAt);
        Assert.True(c.HasLeftRunning);
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
        try
        {
            Assert.Equal("3 h ago", Loc.Ago(at, at.AddHours(3.5)));
            Assert.Equal("yesterday", Loc.Ago(at, at.AddHours(30)));
            Assert.Equal("4 d ago", Loc.Ago(at, at.AddDays(4)));
            Assert.Equal("just now", Loc.Ago(at, at.AddSeconds(20)));
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = culture; }

        c.LeftRunningAt = null;
        Assert.False(c.HasLeftRunning);
        Assert.Null(c.LeftRunningTip);
    }

    [Fact]
    public void SaveLoad_RoundTrip_KeepsTreeAndEncryptedPassword()
    {
        var store = Store.Load(File);
        var cred = new Credential { Name = "admin", User = "administrator", Domain = "CORP", Secret = Store.Protect("P@ss!") };
        var group = new Group { Name = "Prod", CredentialId = cred.Id };
        store.Data.Credentials.Add(cred);
        store.Data.Groups.Add(group);
        store.Data.Connections.Add(new Connection { Name = "SQL01", Host = "sql01.corp", Port = 3390, ParentId = group.Id, Favorite = true });
        store.Save();

        Assert.DoesNotContain("P@ss!", System.IO.File.ReadAllText(File));

        var loaded = Store.Load(File);
        var g = Assert.IsType<Group>(Assert.Single(loaded.Roots));
        var c = Assert.IsType<Connection>(Assert.Single(g.Children));
        Assert.Equal(("sql01.corp", 3390, true), (c.Host, c.Port, c.Favorite));
        Assert.Equal("P@ss!", Store.Unprotect(loaded.Resolve(c).Credential!.Secret!));
    }

    [Fact]
    public void Load_CorruptFile_KeepsCopy_RecoversFromBackup_AndNextSaveKeepsTheBackup()
    {
        var store = Store.Load(File);
        store.Data.Connections.Add(new Connection { Name = "a", Host = "a" });
        store.Save();
        store.Save(); // second save creates .bak
        System.IO.File.WriteAllText(File, "{ corrupt");

        var loaded = Store.Load(File);

        Assert.Single(loaded.Data.Connections);
        var recovery = Assert.IsType<Store.Recovery>(loaded.Recovered);
        Assert.Equal(File + ".bak", recovery.Source);
        Assert.StartsWith(File + ".corrupt-", recovery.CorruptCopy);
        Assert.Equal("{ corrupt", System.IO.File.ReadAllText(recovery.CorruptCopy!));
        Assert.Contains(recovery.CorruptCopy!, recovery.Message);

        loaded.Save(); // the damaged data.json must not be rotated into .bak
        Assert.DoesNotContain("corrupt", System.IO.File.ReadAllText(File + ".bak"));
        Assert.Null(Store.Load(File).Recovered);
    }

    [Fact]
    public void Load_CorruptWithoutBackup_Throws_AndLeavesFileAndCopy()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ corrupt");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Store.Load(File));
        Assert.Equal("{ corrupt", System.IO.File.ReadAllText(File));
        Assert.Single(Directory.GetFiles(_dir, "data.json.corrupt-*"));
    }

    [Fact]
    public void Resolve_InheritsFromNearestGroup_ConnectionOverrides()
    {
        var store = Store.Load(File);
        var credTop = new Credential { Name = "top" };
        var credOwn = new Credential { Name = "own" };
        var top = new Group { Name = "top", CredentialId = credTop.Id, Rdp = new RdpOptions { ColorDepth = 16 } };
        var mid = new Group { Name = "mid", ParentId = top.Id };
        var inherits = new Connection { Host = "a", ParentId = mid.Id };
        var overrides = new Connection { Host = "b", ParentId = mid.Id, CredentialId = credOwn.Id, Rdp = new RdpOptions { ColorDepth = 24 } };
        store.Data.Credentials.Add(credTop);
        store.Data.Credentials.Add(credOwn);
        store.Data.Groups.Add(top);
        store.Data.Groups.Add(mid);
        store.Data.Connections.Add(inherits);
        store.Data.Connections.Add(overrides);

        Assert.Equal(("top", 16), (store.Resolve(inherits).Credential!.Name, store.Resolve(inherits).Options.ColorDepth));
        Assert.Equal(("own", 24), (store.Resolve(overrides).Credential!.Name, store.Resolve(overrides).Options.ColorDepth));

        var orphan = new Connection { Host = "c" };
        Assert.Null(store.Resolve(orphan).Credential);
        Assert.Same(store.Data.Settings.Rdp, store.Resolve(orphan).Options);
    }

    [Fact]
    public void CanMove_RejectsCycles_DeleteRemovesSubtree()
    {
        var store = Store.Load(File);
        var a = new Group { Name = "a" };
        var b = new Group { Name = "b", ParentId = a.Id };
        store.Data.Groups.Add(a);
        store.Data.Groups.Add(b);
        store.Data.Connections.Add(new Connection { Host = "x", ParentId = b.Id });

        Assert.False(store.CanMove(a, b));
        Assert.False(store.CanMove(a, a));
        Assert.True(store.CanMove(b, null));

        store.Delete(a);
        Assert.Empty(store.Data.Groups);
        Assert.Empty(store.Data.Connections);
    }

    [Theory]
    [InlineData("srv01", true, "srv01", 3389)]
    [InlineData("srv01.corp.local:3390", true, "srv01.corp.local", 3390)]
    [InlineData("10.0.0.5", true, "10.0.0.5", 3389)]
    [InlineData("[fe80::1]:4000", true, "fe80::1", 4000)]
    [InlineData("fe80::1", true, "fe80::1", 3389)]
    [InlineData("srv:99999", false, "", 0)]
    [InlineData("bad host", false, "", 0)]
    public void TryParseAddress(string text, bool ok, string host, int port)
    {
        Assert.Equal(ok, Store.TryParseAddress(text, out var h, out var p));
        if (ok) Assert.Equal((host, port), (h, p));
    }

    [Fact]
    public void BuildTree_SortsGroupsFirstThenNaturally()
    {
        var store = Store.Load(File);
        foreach (var n in new[] { "SRV-10", "SRV-2", "srv-1" }) store.Data.Connections.Add(new Connection { Name = n, Host = n });
        store.Data.Groups.Add(new Group { Name = "Z" });
        store.BuildTree();

        Assert.Equal(["Z", "srv-1", "SRV-2", "SRV-10"], store.Roots.Select(n => n.Name));
    }

    [Fact]
    public void Search_MatchesAllTokensAcrossNameHostAndGroup()
    {
        var store = Store.Load(File);
        var g = new Group { Name = "Production" };
        store.Data.Groups.Add(g);
        store.Data.Connections.Add(new Connection { Name = "SQL01", Host = "10.0.0.1", ParentId = g.Id });
        store.Data.Connections.Add(new Connection { Name = "SQL02", Host = "10.0.0.2" });

        Assert.Equal("SQL01", Assert.Single(store.Search("prod sql")).Name);
        Assert.Equal(2, store.Search("sql").Count());
    }

    [Fact]
    public void ImportRdp_ParsesAddressOptionsAndUser()
    {
        var store = Store.Load(File);
        const string rdp = "full address:s:jump.corp:3391\r\nusername:s:CORP\\jdoe\r\nscreen mode id:i:1\r\n" +
                           "desktopwidth:i:1600\r\ndesktopheight:i:900\r\nredirectclipboard:i:0\r\ndrivestoredirect:s:*\r\n" +
                           "audiomode:i:2\r\nadministrative session:i:1\r\ngatewayhostname:s:gw.corp\r\ngatewayusagemethod:i:1\r\n";

        var result = Importer.ImportRdp(store, rdp, "Jump", null);

        Assert.Equal((1, 0), (result.Added, result.Skipped));
        Assert.Equal(2, result.Warnings.Count); // drive redirection and a gateway from a file are worth a look
        var c = Assert.Single(store.Data.Connections);
        Assert.Equal(("Jump", "jump.corp", 3391), (c.Name, c.Host, c.Port));
        var o = c.Rdp!;
        Assert.Equal((ResolutionMode.Fixed, 1600, 900), (o.Resolution, o.Width, o.Height));
        Assert.Equal((false, true, AudioMode.None, true, "gw.corp"), (o.RedirectClipboard, o.RedirectDrives, o.Audio, o.AdminSession, o.Gateway));
        var cred = store.Resolve(c).Credential!;
        Assert.Equal(("jdoe", "CORP", null), (cred.User, cred.Domain, cred.Secret));
    }

    [Fact]
    public void ImportMRemoteNG_BuildsTreeAndSharesCredentials()
    {
        var store = Store.Load(File);
        var xml = XDocument.Parse("""
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" FullFileEncryption="false">
              <Node Name="Prod" Type="Container" Expanded="true" Username="admin" Domain="CORP">
                <Node Name="Web" Type="Connection" Hostname="web01" Port="3389" Protocol="RDP" InheritUsername="true" />
                <Node Name="Ssh" Type="Connection" Hostname="lnx01" Port="22" Protocol="SSH2" />
              </Node>
              <Node Name="Lab" Type="Connection" Hostname="lab01" Port="3390" Protocol="RDP" Username="admin" Domain="corp" />
            </mrng:Connections>
            """);

        Assert.Equal(2, Importer.ImportMRemoteNG(store, xml, null).Added);

        Assert.Single(store.Data.Credentials); // same user/domain reused (case-insensitive)
        var web = store.Data.Connections.Single(c => c.Name == "Web");
        Assert.Null(web.CredentialId);
        Assert.Equal("admin", store.Resolve(web).Credential!.User);
        Assert.Equal("mRemoteNG / Prod", store.PathOf(web));
        Assert.Equal(3390, store.Data.Connections.Single(c => c.Name == "Lab").Port);
    }

    [Fact]
    public void Import_Twice_AddsNothing_AndLeavesNoEmptyGroupsOrCredentials()
    {
        var store = Store.Load(File);
        var xml = XDocument.Parse("""
            <Connections>
              <Node Name="Linux" Type="Container" Username="root"><Node Name="Ssh" Type="Connection" Hostname="lnx" Protocol="SSH2" /></Node>
              <Node Name="" Type="Connection" Hostname="web01" Protocol="RDP" />
            </Connections>
            """);

        Assert.Equal(1, Importer.ImportMRemoteNG(store, xml, null).Added);
        var web = Assert.Single(store.Data.Connections);
        Assert.Equal(("", "web01"), (web.Name, web.Title));            // no name in the file: Title shows the host
        Assert.Equal("mRemoteNG", Assert.Single(store.Data.Groups).Name); // SSH-only folder not created
        Assert.Empty(store.Data.Credentials);                         // its "root" credential neither

        var again = Importer.ImportMRemoteNG(store, xml, null);        // re-import
        Assert.Equal((0, 1), (again.Added, again.Skipped));
        Assert.Single(store.Data.Groups);
        Assert.Equal(1, Importer.ImportRdp(store, "full address:s:web01\n", "", null).Added); // other group: not a duplicate
        Assert.Equal(0, Importer.ImportRdp(store, "full address:s:web01\n", "Web again", null).Added);
        Assert.Equal(2, store.Data.Connections.Count);
    }

    [Fact]
    public void Export_ThenImport_RecreatesTreeWithoutPasswords_AndSkipsDuplicates()
    {
        var source = Store.Load(File);
        var cred = new Credential { Name = "Admin", User = "admin", Domain = "CORP", Secret = Store.Protect("S3cret!") };
        var prod = new Group { Name = "Prod", CredentialId = cred.Id };
        var sql = new Group { Name = "SQL", ParentId = prod.Id, Rdp = new RdpOptions { ColorDepth = 16 } };
        source.Data.Credentials.Add(cred);
        source.Data.Groups.Add(prod);
        source.Data.Groups.Add(sql);
        source.Data.Groups.Add(new Group { Name = "Empty" });
        source.Data.Connections.Add(new Connection { Name = "SQL01", Host = "sql01", Port = 3390, ParentId = sql.Id, Tags = "db", Favorite = true });

        var json = Importer.Export(source);
        Assert.DoesNotContain("Secret", json);

        var target = Store.Load(Path.Combine(_dir, "other.json"));
        Assert.Equal(1, Importer.ImportSessionwright(target, json, null).Added);
        var c = Assert.Single(target.Data.Connections);
        Assert.Equal(("SQL01", "sql01", 3390, "db", true), (c.Name, c.Host, c.Port, c.Tags, c.Favorite));
        Assert.Equal("Prod / SQL", target.PathOf(c));
        Assert.Equal(2, target.Data.Groups.Count);              // "Empty" has no connections: not recreated
        var (credential, options) = target.Resolve(c);
        Assert.Equal(("Admin", "admin", null), (credential!.Name, credential.User, credential.Secret)); // inherited, no password
        Assert.Equal(16, options.ColorDepth);

        var again = Importer.ImportSessionwright(target, json, null); // second import adds nothing
        Assert.Equal((0, 1), (again.Added, again.Skipped));
        Assert.Equal(2, target.Data.Groups.Count);
        Assert.Throws<InvalidDataException>(() => Importer.ImportSessionwright(target, """{"Format":"Other"}""", null));
    }

    [Fact]
    public void Connection_RaisesTitleAndAddressChanges()
    {
        var c = new Connection { Host = "a" };
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.Name = "Server";
        c.Port = 3390;

        Assert.Contains(nameof(Connection.Title), changed);
        Assert.Contains(nameof(Connection.Address), changed);
        Assert.Equal(("Server", "a:3390"), (c.Title, c.Address));
    }

    [Fact]
    public void ImportMRemoteNG_FullFileEncryption_Throws()
    {
        var xml = XDocument.Parse("""<Connections FullFileEncryption="true">abc</Connections>""");
        Assert.Throws<InvalidDataException>(() => Importer.ImportMRemoteNG(Store.Load(File), xml, null));
    }

    [Fact]
    public void EnvironmentColor_InheritsFromNearestGroup_AndSurvivesExportImport()
    {
        var store = Store.Load(File);
        var prod = new Group { Name = "Prod", Color = "#E5534B" };
        var sql = new Group { Name = "SQL", ParentId = prod.Id };
        var own = new Connection { Name = "web", Host = "web", ParentId = sql.Id, Color = "#3B82F6" };
        var plain = new Connection { Name = "sql01", Host = "sql01", ParentId = sql.Id };
        store.Data.Groups.Add(prod);
        store.Data.Groups.Add(sql);
        store.Data.Connections.Add(own);
        store.Data.Connections.Add(plain);

        Assert.Equal("#E5534B", store.ColorOf(plain)); // from the grandparent
        Assert.Equal("#3B82F6", store.ColorOf(own));   // own color wins
        var (source, color) = store.Inherited(sql, n => n.Color);
        Assert.Equal((prod, "#E5534B"), (source, color));
        Assert.Equal((null, null), store.Inherited<string>(null, n => n.Color));

        var target = Store.Load(Path.Combine(_dir, "target.json"));
        Importer.ImportSessionwright(target, Importer.Export(store), null);
        var imported = target.Data.Connections.Single(c => c.Name == "sql01");
        Assert.Equal("#E5534B", target.ColorOf(imported));
        Assert.Equal("#3B82F6", target.Data.Connections.Single(c => c.Name == "web").Color);
    }
}
