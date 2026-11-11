using System.Globalization;
using System.IO;
using System.Text;
using Regex = System.Text.RegularExpressions.Regex;
using System.Xml.Linq;
using Sessionwright;
using Xunit;

public class DataLayerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "SessionwrightTests", Guid.NewGuid().ToString("N"));
    string DataFile => Path.Combine(_dir, "data.json");

    public DataLayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    string Write(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    // ---------- Favorites ----------

    // A new favorite goes to the end; dragging puts one where it is dropped (also one dragged in from the tree), and the
    // order survives a save and load.
    [Fact]
    public void FavoritesKeepTheOrderTheyWereArrangedIn()
    {
        var store = Store.Load(DataFile);
        Connection Add(string name)
        {
            var c = new Connection { Name = name, Host = name.ToLowerInvariant() };
            store.Data.Connections.Add(c);
            return c;
        }
        var (a, b, c, d) = (Add("Bravo"), Add("Alpha"), Add("Charlie"), Add("Delta"));
        store.SetFavorite(a, true);
        store.SetFavorite(b, true);
        store.SetFavorite(c, true);
        Assert.Equal(["Bravo", "Alpha", "Charlie"], store.Favorites().Select(f => f.Title));

        store.MoveFavorite(c, 0);
        Assert.Equal(["Charlie", "Bravo", "Alpha"], store.Favorites().Select(f => f.Title));
        store.MoveFavorite(d, 1); // dragged in from the tree
        Assert.Equal(["Charlie", "Delta", "Bravo", "Alpha"], store.Favorites().Select(f => f.Title));
        store.SetFavorite(a, false);
        store.SetFavorite(a, true);
        Assert.Equal("Bravo", store.Favorites()[^1].Title); // back at the end

        store.Save();
        Assert.Equal(["Charlie", "Delta", "Alpha", "Bravo"], Store.Load(DataFile).Favorites().Select(f => f.Title));
    }

    // ---------- 1. Load / save ----------

    [Fact]
    public void ImportRdcMan_Schema3_GroupsCredentialsAndInheritedSettings()
    {
        var sealedPassword = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            Encoding.Unicode.GetBytes("Secr3t!"), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        var path = Write("servers.rdg", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.93" schemaVersion="3">
              <file>
                <credentialsProfiles>
                  <credentialsProfile inherit="None">
                    <profileName scope="Local">Admins</profileName>
                    <userName>admin</userName>
                    <password>{sealedPassword}</password>
                    <domain>CORP</domain>
                  </credentialsProfile>
                </credentialsProfiles>
                <properties><expanded>True</expanded><name>Datacenter</name></properties>
                <logonCredentials inherit="None"><profileName scope="File">Admins</profileName></logonCredentials>
                <connectionSettings inherit="None"><connectToConsole>False</connectToConsole><port>3390</port></connectionSettings>
                <group>
                  <properties><expanded>False</expanded><name>Prod</name></properties>
                  <remoteDesktop inherit="None"><sameSizeAsClientArea>False</sameSizeAsClientArea><fullScreen>False</fullScreen><size>1280 x 800</size><colorDepth>24</colorDepth></remoteDesktop>
                  <gatewaySettings inherit="None"><enabled>True</enabled><hostName>gw.corp</hostName></gatewaySettings>
                  <server>
                    <properties><displayName>SQL 01</displayName><name>sql01.corp</name><comment>main db</comment></properties>
                  </server>
                  <server>
                    <properties><name>web01.corp</name></properties>
                    <logonCredentials inherit="None"><profileName scope="Local">Custom</profileName><userName>web</userName><password>not-dpapi</password><domain /></logonCredentials>
                    <connectionSettings inherit="None"><port>3389</port></connectionSettings>
                  </server>
                </group>
                <smartGroup><properties><name>Ignored</name></properties></smartGroup>
              </file>
              <connected /><favorites /><recentlyUsed />
            </RDCMan>
            """);
        var store = Store.Load(DataFile);
        var result = Importer.ImportFile(store, path, null);

        Assert.Equal(2, result.Added);
        var file = Assert.Single(store.Data.Groups, g => g.Name == "Datacenter");
        var prod = Assert.Single(store.Data.Groups, g => g.Name == "Prod");
        Assert.Equal(file.Id, prod.ParentId);
        Assert.False(prod.Expanded);
        Assert.Equal("gw.corp", prod.Rdp!.Gateway);
        Assert.Equal((ResolutionMode.Fixed, 1280, 800, 24), (prod.Rdp.Resolution, prod.Rdp.Width, prod.Rdp.Height, prod.Rdp.ColorDepth));

        var admins = Assert.Single(store.Data.Credentials, c => c.User == "admin");
        Assert.Equal(("CORP", "Admins"), (admins.Domain, admins.Name));
        Assert.Equal("Secr3t!", Store.Unprotect(admins.Secret!)); // same Windows account: the password comes along
        Assert.Equal(admins.Id, file.CredentialId);

        var sql = Assert.Single(store.Data.Connections, c => c.Host == "sql01.corp");
        Assert.Equal(("SQL 01", 3390, "main db", prod.Id), (sql.Name, sql.Port, sql.Notes, sql.ParentId)); // port from the file node
        Assert.Null(sql.CredentialId); // inherits the file's credential, like in RDCMan
        Assert.Null(sql.Rdp);

        var web = Assert.Single(store.Data.Connections, c => c.Host == "web01.corp");
        Assert.Equal(3389, web.Port);
        var webCredential = Assert.Single(store.Data.Credentials, c => c.Id == web.CredentialId);
        Assert.Equal("web", webCredential.User);
        Assert.Null(webCredential.Secret); // unreadable password: asked when connecting
        Assert.Contains(result.Warnings, w => w.Length > 0);
    }

    [Fact]
    public void ImportRdcMan_OldSchema_AndNotAnRdgFile()
    {
        var old = Write("old.rdg", """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan schemaVersion="1">
              <version>2.2</version>
              <file>
                <properties><name>Legacy</name></properties>
                <group>
                  <properties><name>Test</name></properties>
                  <server><name>legacy01</name><displayName>Legacy 01</displayName><logonCredentials inherit="FromParent" /></server>
                </group>
              </file>
            </RDCMan>
            """);
        var store = Store.Load(DataFile);
        Assert.Equal(1, Importer.ImportFile(store, old, null).Added);
        var c = Assert.Single(store.Data.Connections);
        Assert.Equal(("Legacy 01", "legacy01"), (c.Name, c.Host));
        Assert.Equal("Test", store.Parent(c)!.Name);

        var notRdg = Write("other.rdg", "<root><file /></root>");
        Assert.Throws<InvalidDataException>(() => Importer.ImportFile(store, notRdg, null));
    }

    [Fact]
    public void Export_WithPassword_RoundTripsSavedPasswords_AndAsksAgainWhenWrong()
    {
        var source = Store.Load(Path.Combine(_dir, "source.json"));
        var cred = new Credential { Name = "admin", User = "administrator", Domain = "CORP" };
        cred.SetPassword("P@ss!");
        source.Data.Credentials.Add(cred);
        source.Data.Connections.Add(new Connection { Host = "srv01", CredentialId = cred.Id });

        var plain = Importer.Export(source);
        Assert.DoesNotContain("\"Secret\": \"", plain); // without a password no secret leaves the machine

        var json = Importer.Export(source, "correct horse");
        var asked = new List<bool>();
        var target = Store.Load(DataFile);
        Importer.ImportSessionwright(target, json, null, wrong =>
        {
            asked.Add(wrong);
            return asked.Count == 1 ? "wrong password" : "correct horse";
        });
        Assert.Equal([false, true], asked);
        var imported = Assert.Single(target.Data.Credentials);
        Assert.Equal("P@ss!", Store.Unprotect(imported.Secret!));
        Assert.Equal(imported.Id, Assert.Single(target.Data.Connections).CredentialId);

        var cancelled = Store.Load(Path.Combine(_dir, "cancelled.json"));
        var result = Importer.ImportSessionwright(cancelled, json, null, _ => null);
        Assert.Null(Assert.Single(cancelled.Data.Credentials).Secret);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Load_LockedFile_Rethrows_WithoutFallbackOrCopy()
    {
        var store = Store.Load(DataFile);
        store.Save();
        store.Save(); // .bak exists, but an I/O error must not fall back to it
        using var _ = new FileStream(DataFile, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => Store.Load(DataFile));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void Load_MissingFile_RestoresFromBak_SkippingUnparsableTmp()
    {
        Write("data.json.tmp", "{ half written");
        Write("data.json.bak", """{ "Connections": [ { "Host": "from-bak" } ] }""");

        var store = Store.Load(DataFile);

        Assert.Equal("from-bak", Assert.Single(store.Data.Connections).Host);
        Assert.Equal(new Store.Recovery(DataFile + ".bak", null), store.Recovered);
    }

    [Fact]
    public void Load_CorruptFile_PrefersParsableTmpOverBak()
    {
        Write("data.json", "garbage");
        Write("data.json.tmp", """{ "Connections": [ { "Host": "newer" } ] }""");
        Write("data.json.bak", """{ "Connections": [ { "Host": "older" } ] }""");

        var store = Store.Load(DataFile);

        Assert.Equal("newer", Assert.Single(store.Data.Connections).Host);
        Assert.Equal(DataFile + ".tmp", store.Recovered!.Source);
    }

    [Fact]
    public void Load_EmptyStore_WhenNothingExists_AndSaveWritesSchemaVersion()
    {
        var store = Store.Load(DataFile);
        Assert.Null(store.Recovered);
        store.Save();

        Assert.Contains("\"SchemaVersion\": 1", File.ReadAllText(DataFile));
        Assert.False(File.Exists(DataFile + ".tmp"));
    }

    // ---------- 2. Schema and tolerance ----------

    [Fact]
    public void Load_NewerSchema_Throws_WithoutFallingBackToBak()
    {
        Write("data.json", """{ "SchemaVersion": 99 }""");
        Write("data.json.bak", "{}");

        var ex = Assert.Throws<InvalidDataException>(() => Store.Load(DataFile));
        Assert.Contains("99", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void Load_UnversionedFile_IsMigratedToCurrentSchema()
    {
        Write("data.json", "{}");
        Assert.Equal(Store.CurrentSchema, Store.Load(DataFile).Data.SchemaVersion);
    }

    [Fact]
    public void Load_RepairsUnknownEnums_NullsDuplicatesCyclesAndDanglingReferences()
    {
        Write("data.json", """
            {
              "Groups": [
                { "Id": "11111111-1111-1111-1111-111111111111", "Name": "A", "ParentId": "22222222-2222-2222-2222-222222222222" },
                { "Id": "22222222-2222-2222-2222-222222222222", "Name": "B", "ParentId": "11111111-1111-1111-1111-111111111111" },
                { "Id": "11111111-1111-1111-1111-111111111111", "Name": "A duplicate" },
                { "Id": "33333333-3333-3333-3333-333333333333", "Name": "Orphan", "ParentId": "99999999-9999-9999-9999-999999999999" },
                null
              ],
              "Connections": [
                { "Id": "44444444-4444-4444-4444-444444444444", "Host": "h", "ParentId": "11111111-1111-1111-1111-111111111111",
                  "CredentialId": "88888888-8888-8888-8888-888888888888", "Rdp": { "Resolution": "Hologram", "Audio": 1 } }
              ],
              "Credentials": null,
              "Settings": { "Rdp": null }
            }
            """);

        var store = Store.Load(DataFile);
        var d = store.Data;

        Assert.Equal(["A", "B", "Orphan"], d.Groups.Select(g => g.Name));                 // first of a duplicate id wins
        var a = d.Groups[0];
        var b = d.Groups[1];
        Assert.Equal((b.Id, (Guid?)null), (a.ParentId!.Value, b.ParentId));                 // cycle cut where it closes
        Assert.Null(d.Groups[2].ParentId);                                                  // missing parent
        var c = Assert.Single(d.Connections);
        Assert.Equal((a.Id, (Guid?)null), (c.ParentId!.Value, c.CredentialId));             // missing credential
        Assert.Equal((ResolutionMode.FitWindow, AudioMode.Remote), (c.Rdp!.Resolution, c.Rdp.Audio));
        Assert.Empty(d.Credentials);
        Assert.NotNull(d.Settings.Rdp);
        Assert.Equal(["B", "Orphan"], store.Roots.Select(n => n.Name));
        Assert.Contains(store.Warnings, w => w.Contains("Hologram"));
        Assert.True(store.Warnings.Count >= 5);
    }

    [Fact]
    public void Export_RoundTrip_KeepsOrderAndPlans_ButNotWhatThisPcWasDoingWithTheVm()
    {
        var source = Store.Load(Path.Combine(_dir, "source.json"));
        var group = new Group { Name = "Dev", Order = 2, ShutDownVmAfter = true, AutoStart = new VmAutoStart { Days = AutoStartDays.Daily, Time = "07:30" } };
        source.Data.Groups.Add(group);
        source.Data.Connections.Add(new Connection
        {
            Host = "vm01", ParentId = group.Id, Order = 3, Favorite = true, FavoriteOrder = 4,
            AzureVm = new AzureVmLink { Name = "vm01", SubscriptionId = Guid.NewGuid().ToString(), ShutdownOriginal = "1900", ShutdownRestoreAfter = DateTime.UtcNow, ShutdownSkipped = true, AutoStartedOn = DateTime.Today },
        });

        // A machine link that Azure never gave out (its subscription is no id) does not come along.
        source.Data.Connections.Add(new Connection { Host = "odd", AzureVm = new AzureVmLink { Name = "vm02", SubscriptionId = "x/../y?" } });

        var target = Store.Load(DataFile);
        Importer.ImportSessionwright(target, Importer.Export(source), null);
        Assert.Null(target.Data.Connections.Single(x => x.Host == "odd").AzureVm);
        target.Data.Connections.Remove(target.Data.Connections.Single(x => x.Host == "odd"));

        var g = Assert.Single(target.Data.Groups);
        Assert.Equal((2d, true, "07:30"), (g.Order!.Value, g.ShutDownVmAfter!.Value, g.AutoStart!.Time));
        var c = Assert.Single(target.Data.Connections);
        Assert.Equal((3d, 4), (c.Order!.Value, c.FavoriteOrder));
        var vm = c.AzureVm!;
        Assert.Equal(("vm01", (string?)null, (DateTime?)null, false, (DateTime?)null),
            (vm.Name, vm.ShutdownOriginal, vm.ShutdownRestoreAfter, vm.ShutdownSkipped, vm.AutoStartedOn));
    }

    [Fact]
    public void ImportSessionwright_NewerExportVersion_ThrowsClearMessage()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Importer.ImportSessionwright(Store.Load(DataFile), """{ "Format": "Sessionwright", "Version": 7 }""", null));
        Assert.Contains("7", ex.Message);
    }

    // ---------- 3. Credentials ----------

    [Fact]
    public void SetPassword_EmptyMeansNoSecret()
    {
        var c = new Credential { Secret = [1, 2, 3] };
        c.SetPassword("");
        Assert.Null(c.Secret);
        c.SetPassword(null);
        Assert.Null(c.Secret);
        c.SetPassword("P@ss");
        Assert.Equal("P@ss", Store.Unprotect(c.Secret!));
    }

    [Fact]
    public void FindOrCreateCredential_ReusesSameUserAndDomain_CaseInsensitive()
    {
        var store = Store.Load(DataFile);
        var first = store.FindOrCreateCredential("Admin", "CORP");

        Assert.Same(first, store.FindOrCreateCredential("admin", "corp", "ignored"));
        Assert.NotSame(first, store.FindOrCreateCredential("admin", null));
        Assert.Null(store.FindOrCreateCredential(" ", "CORP"));
        Assert.Equal(2, store.Data.Credentials.Count);
        Assert.Equal(@"CORP\Admin", first!.Name);
    }

    // ---------- 6. Import files ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportFile_ReadsUtf16Rdp_WithOrWithoutBom(bool bom)
    {
        var path = Path.Combine(_dir, "Jump host.rdp");
        var text = "full address:s:jump.corp\r\nusername:s:CORP\\jdöe\r\n";
        if (bom) File.WriteAllText(path, text, Encoding.Unicode);
        else File.WriteAllBytes(path, Encoding.Unicode.GetBytes(text));
        var store = Store.Load(DataFile);

        Assert.Equal(1, Importer.ImportFile(store, path, null).Added);

        var c = Assert.Single(store.Data.Connections);
        Assert.Equal(("Jump host", "jump.corp"), (c.Name, c.Host));
        Assert.Equal("jdöe", Assert.Single(store.Data.Credentials).User);
    }

    [Fact]
    public void ImportFile_Malformed_ThrowsInvalidData_AndAddsNothing()
    {
        var store = Store.Load(DataFile);
        var xml = Write("confCons.xml", "<Connections><Node Name=");
        var json = Write("export.json", """{ "Format": "Sessionwright", "Connections": [ { "Host": "a", "Port": "not a number" } ] }""");
        var rdp = Write("bad.rdp", "username:s:CORP\\x\r\n");

        foreach (var file in new[] { xml, json, rdp })
            Assert.Throws<InvalidDataException>(() => Importer.ImportFile(store, file, null));
        Assert.Throws<FileNotFoundException>(() => Importer.ImportFile(store, Path.Combine(_dir, "missing.rdp"), null));

        Assert.Empty(store.Data.Connections);
        Assert.Empty(store.Data.Credentials);
        Assert.Empty(store.Data.Groups);
    }

    // ---------- 7. Dedup ----------

    [Fact]
    public void ImportSessionwright_RoundTripsCopiesOfOneHost_AndReusesGroupsByIdThenName()
    {
        var source = Store.Load(DataFile);
        var prod = new Group { Name = "Prod" };
        source.Data.Groups.Add(prod);
        var original = new Connection { Name = "SQL01", Host = "sql01", ParentId = prod.Id };
        var copy = new Connection { Name = "SQL01 (copy)", Host = "sql01", ParentId = prod.Id };
        source.Data.Connections.Add(original);
        source.Data.Connections.Add(copy);
        var json = Importer.Export(source);

        var target = Store.Load(Path.Combine(_dir, "target.json"));
        var ownProd = new Group { Name = "prod" }; // same name, different id
        target.Data.Groups.Add(ownProd);

        Assert.Equal(2, Importer.ImportSessionwright(target, json, null).Added);
        Assert.Equal([original.Id, copy.Id], target.Data.Connections.Select(c => c.Id)); // original ids kept
        Assert.All(target.Data.Connections, c => Assert.Equal(ownProd.Id, c.ParentId)); // reused by name
        Assert.Single(target.Data.Groups);

        // Back into the source after a rename and a delete: the group is matched by id, the original skipped by id.
        prod.Name = "Production";
        source.Data.Connections.Remove(copy);
        var again = Importer.ImportSessionwright(source, json, null);
        Assert.Equal((1, 1), (again.Added, again.Skipped));
        Assert.Single(source.Data.Groups);
        Assert.Equal(prod.Id, source.Data.Connections.Single(c => c.Id == copy.Id).ParentId);
    }

    [Fact]
    public void ImportMRemoteNG_UpdatedFile_ReusesTree_AndAddsOnlyNewConnections()
    {
        var doc = XDocument.Parse("""
            <Connections>
              <Node Name="Prod" Type="Container" Username="admin"><Node Name="Web" Type="Connection" Hostname="web01" Protocol="RDP" InheritUsername="true" /></Node>
            </Connections>
            """);
        var store = Store.Load(DataFile);

        Assert.Equal(1, Importer.ImportMRemoteNG(store, doc, null).Added);
        doc.Root!.Element("Node")!.Add(XElement.Parse("""<Node Name="Sql" Type="Connection" Hostname="sql01" Protocol="RDP" InheritUsername="true" />"""));
        var result = Importer.ImportMRemoteNG(store, doc, null);

        Assert.Equal((1, 1), (result.Added, result.Skipped));
        Assert.Equal(["mRemoteNG", "Prod"], store.Data.Groups.Select(g => g.Name));
        Assert.Single(store.Data.Credentials);
        Assert.All(store.Data.Connections, c => Assert.Equal("mRemoteNG / Prod", store.PathOf(c)));
    }

    // ---------- 8. mRemoteNG RDP options ----------

    [Fact]
    public void ImportMRemoteNG_MapsRdpOptions_RespectingInheritance()
    {
        var xml = XDocument.Parse("""
            <Connections>
              <Node Name="Prod" Type="Container" ConnectToConsole="false" RedirectDiskDrives="None" RedirectPrinters="true"
                    RedirectSmartCards="false" RedirectClipboard="false" RedirectSound="DoNotPlay" Resolution="Res1600x900"
                    RDGatewayUsageMethod="Always" RDGatewayHostname="gw.corp">
                <Node Name="Inherits" Type="Connection" Hostname="a" Protocol="RDP" ConnectToConsole="true" Resolution="Fullscreen"
                      InheritUseConsoleSession="true" InheritRedirectDiskDrives="true" InheritRedirectPrinters="true"
                      InheritRedirectSmartCards="true" InheritRedirectClipboard="true" InheritRedirectSound="true"
                      InheritResolution="true" InheritRDGatewayUsageMethod="true" InheritRDGatewayHostname="true" />
                <Node Name="Own" Type="Connection" Hostname="b" Protocol="RDP" ConnectToConsole="True" RedirectDiskDrives="True"
                      RedirectSound="LeaveAtRemoteComputer" Resolution="Fullscreen" RDGatewayUsageMethod="Never" />
              </Node>
            </Connections>
            """);
        var store = Store.Load(DataFile);

        Importer.ImportMRemoteNG(store, xml, null);

        var group = store.Data.Groups.Single(g => g.Name == "Prod").Rdp!;
        Assert.Equal((ResolutionMode.Fixed, 1600, 900, AudioMode.None, "gw.corp", false, true, false, false),
            (group.Resolution, group.Width, group.Height, group.Audio, group.Gateway, group.RedirectDrives, group.RedirectPrinters,
             group.RedirectClipboard, group.AdminSession));

        var inherits = store.Data.Connections.Single(c => c.Name == "Inherits");
        Assert.Null(inherits.Rdp);                                       // everything from the folder
        Assert.Same(group, store.Resolve(inherits).Options);

        var own = store.Data.Connections.Single(c => c.Name == "Own").Rdp!;
        Assert.Equal((ResolutionMode.Fullscreen, AudioMode.Remote, null, true, true),
            (own.Resolution, own.Audio, own.Gateway, own.RedirectDrives, own.AdminSession));
        Assert.Equal((true, false), (own.RedirectPrinters, own.RedirectClipboard)); // not set on the node: the folder's
    }

    // ---------- 9. .rdp port and warnings ----------

    [Theory]
    [InlineData("full address:s:srv:3391\r\nserver port:i:4000\r\n", 3391)]
    [InlineData("full address:s:srv\r\nserver port:i:4000\r\n", 4000)]
    [InlineData("full address:s:srv\r\n", 3389)]
    public void ImportRdp_ServerPort_OnlyWhenFullAddressHasNone(string rdp, int port)
    {
        var store = Store.Load(DataFile);
        var result = Importer.ImportRdp(store, rdp, "srv", null);

        Assert.Equal(port, Assert.Single(store.Data.Connections).Port);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ImportRdp_WarnsAboutDrivesSmartCardsAndGateway_ButImportsThem()
    {
        var store = Store.Load(DataFile);
        var result = Importer.ImportRdp(store, "full address:s:srv\r\ndrivestoredirect:s:*\r\nredirectsmartcards:i:1\r\n" +
                                               "gatewayhostname:s:evil.example\r\ngatewayusagemethod:i:1\r\n", "Payroll", null);

        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Contains("Payroll", w));
        Assert.Contains(result.Warnings, w => w.Contains("evil.example"));
        var o = Assert.Single(store.Data.Connections).Rdp!;
        Assert.Equal((true, true, "evil.example"), (o.RedirectDrives, o.RedirectSmartCards, o.Gateway));
    }

    // ---------- 12. Localization ----------

    [Fact]
    public void Loc_EveryKey_HasEnglishAndPolish_WithTheSamePlaceholders()
    {
        static string[] Placeholders(string s) => [.. Regex.Matches(s, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Distinct().Order()];
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var key in Loc.Keys)
            {
                CultureInfo.CurrentUICulture = new CultureInfo("en");
                var en = Loc.Get(key);
                CultureInfo.CurrentUICulture = new CultureInfo("pl");
                var pl = Loc.Get(key);

                Assert.False(string.IsNullOrWhiteSpace(en), $"{key}: EN empty");
                Assert.False(string.IsNullOrWhiteSpace(pl), $"{key}: PL empty");
                Assert.True(Placeholders(en).SequenceEqual(Placeholders(pl)), $"{key}: placeholders differ");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
