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

    // An SSH connection: its usual port is left out of the address, its protocol is in the file; a remote desktop
    // is written as before there was a choice.
    [Fact]
    public void ProtocolIsSavedOnlyForSsh()
    {
        var store = Store.Load(File);
        store.Data.Connections.Add(new Connection { Name = "Desktop", Host = "srv01" });
        store.Data.Connections.Add(new Connection { Name = "Shell", Host = "lnx01", Port = 22, Protocol = ConnectionProtocol.Ssh });
        store.Save();
        var text = System.IO.File.ReadAllText(File);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"Protocol\""));
        var shell = Store.Load(File).Data.Connections.Single(c => c.Name == "Shell");
        Assert.Equal((ConnectionProtocol.Ssh, "lnx01"), (shell.Protocol, shell.Address));
        Assert.Equal("lnx01:2222", new Connection { Host = "lnx01", Port = 2222, Protocol = ConnectionProtocol.Ssh }.Address);
    }

    // The command line for ssh.exe: a host that is no host name or address (from an import, a team's list) starts nothing.
    [Theory]
    [InlineData("lnx01", 22, "deploy", "\"ssh.exe\" -p 22 -o ServerAliveInterval=30 -l \"deploy\" -- lnx01")]
    [InlineData("10.0.0.5", 2222, null, "\"ssh.exe\" -p 2222 -o ServerAliveInterval=30 -- 10.0.0.5")]
    [InlineData("lnx01", 22, "CORP\\john smith", "\"ssh.exe\" -p 22 -o ServerAliveInterval=30 -l \"CORP\\john smith\" -- lnx01")]
    [InlineData("x -oProxyCommand=calc.exe", 22, "deploy", null)]
    [InlineData("-oProxyCommand=calc.exe", 22, "deploy", null)]
    [InlineData("lnx01", 22, "a\" -oProxyCommand=calc.exe \"", null)]
    [InlineData("lnx01", 0, "deploy", null)]
    public void SshCommandLine(string host, int port, string? user, string? expected) =>
        Assert.Equal(expected, Ssh.CommandLine("ssh.exe", host, port, user));

    // The saved password answers only its own host's question: a jump host's is asked of the user.
    [Theory]
    [InlineData("deploy@lnx01's password: ", "lnx01", true)]
    [InlineData("(demo@test.rebex.net) Password: ", "test.rebex.net", true)]
    [InlineData("deploy@bastion's password: ", "lnx01", false)]
    [InlineData("deploy@lnx01-jump's password: ", "lnx01", false)]
    [InlineData("Enter passphrase for key 'C:/Users/x/.ssh/id_ed25519': ", "lnx01", false)]
    [InlineData("Are you sure you want to continue connecting (yes/no/[fingerprint])? ", "lnx01", false)]
    public void SavedPasswordOnlyForItsHost(string question, string host, bool own) => Assert.Equal(own, Ssh.AsksPasswordOf(question, host));

    // OpenSSH's question about a new server, taken apart for the window that asks it.
    [Fact]
    public void ReadsTheHostKeyQuestion()
    {
        var question = "The authenticity of host 'test.rebex.net (194.108.117.16)' can't be established.\n"
                       + "ED25519 key fingerprint is SHA256:d7Te2DHmvBNSWJNBWik2KbDTjmWtYHe2bvXTMM9lVg4.\n"
                       + "This key is not known by any other names.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ";
        Assert.Equal(new HostKeyQuestion("test.rebex.net (194.108.117.16)", "ED25519", "SHA256:d7Te2DHmvBNSWJNBWik2KbDTjmWtYHe2bvXTMM9lVg4"), Ssh.ParseHostKey(question));
        Assert.Null(Ssh.ParseHostKey("Continue (yes/no)?"));
        Assert.Equal("deploy", Ssh.AccountAsked("deploy@lnx01's password: "));
        Assert.Equal("demo", Ssh.AccountAsked("(demo@test.rebex.net) Password: "));
    }

    // Why ssh ended, from its own last words.
    [Theory]
    [InlineData("logout", 0, SshEnd.Ended)]
    [InlineData("deploy@lnx01: Permission denied (publickey,password).", 255, SshEnd.Denied)]
    [InlineData("@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @ ... Host key verification failed.", 255, SshEnd.KeyChanged)]
    [InlineData("Host key verification failed.", 255, SshEnd.NotTrusted)]
    [InlineData("ssh: Could not resolve hostname nope: No such host is known.", 255, SshEnd.NoHost)]
    [InlineData("ssh: connect to host 127.0.0.1 port 9: Connection refused", 255, SshEnd.Refused)]
    [InlineData("ssh: connect to host 10.9.9.9 port 22: Connection timed out", 255, SshEnd.TimedOut)]
    [InlineData("client_loop: send disconnect: Connection reset", 255, SshEnd.Lost)]
    [InlineData("something else", 3, SshEnd.Failed)]
    public void NamesWhySshEnded(string said, int code, SshEnd why) => Assert.Equal(why, Ssh.Explain(said, code));

    // The shell's name for itself goes into a window's title: cleaned, and never the console's own first one.
    [Fact]
    public void TakesTheShellsTitle()
    {
        Assert.Equal("demo@web01: ~/www", Ssh.ShellTitle(" demo@web01: ~/www\u0007\r\n"));
        Assert.Null(Ssh.ShellTitle(@"C:\WINDOWS\System32\OpenSSH\ssh.exe"));
        Assert.Null(Ssh.ShellTitle("  "));
        Assert.Null(Ssh.ShellTitle(null));
        Assert.Equal(71, Ssh.ShellTitle(new string('a', 200))!.Length);
    }

    // A console's first words are control sequences only: nothing to show a terminal for yet.
    [Fact]
    public void TellsWhenAConsoleHasSomethingToRead()
    {
        Assert.False(Ssh.Visible("\u001b[?9001h\u001b[?1004h\u001b[?25l\u001b[2J\u001b[m\u001b[H\u001b]0;C:\\WINDOWS\\System32\\OpenSSH\\ssh.exe\u0007\u001b[?25h"));
        Assert.False(Ssh.Visible("\r\n  \u001b[1;1H"));
        Assert.True(Ssh.Visible("\u001b[2J\u001b[HWelcome"));
        Assert.True(Ssh.Visible("ssh: connect to host 10.0.0.1 port 22: Connection refused"));
    }

    // scp in its own console: its arguments quoted for it, its meter read, its last words kept as the reason.
    [Fact]
    public void ReadsScpsProgressAndQuotesItsArguments()
    {
        Assert.Equal("-r", Ssh.Quote("-r"));
        Assert.Equal("\"C:\\My files\\a.txt\"", Ssh.Quote(@"C:\My files\a.txt"));
        Assert.Equal("\"C:\\My files\\\\\"", Ssh.Quote(@"C:\My files\"));
        Assert.Equal("\"a\\\"b\"", Ssh.Quote("a\"b"));
        Assert.Equal("\"\"", Ssh.Quote(""));
        Assert.Equal(("report.pdf", 45), Ssh.ScpProgress("\u001b[2K\rreport.pdf                45%  512KB   1.2MB/s   00:01 ETA"));
        Assert.Equal(("b.zip", 100), Ssh.ScpProgress("a.txt 100% 1KB 1.0KB/s 00:00\r\nb.zip 100% 9MB 3.0MB/s 00:03\r\n"));
        Assert.Null(Ssh.ScpProgress("\u001b]0;C:\\WINDOWS\\System32\\OpenSSH\\scp.exe\u0007"));
        Assert.Equal("dest open \"./a.txt\": Permission denied", Ssh.ScpError("\u001b]0;C:\\WINDOWS\\System32\\OpenSSH\\scp.exe\u0007a.txt 0% 0 0.0KB/s --:-- ETA\r\n"
            + "C:\\WINDOWS\\system32\\OpenSSH\\scp.exe: dest open \"./a.txt\": Permission denied\r\n"
            + "C:\\WINDOWS\\system32\\OpenSSH\\scp.exe: failed to upload file C:/a.txt to .\r\n\u001b[?25h"));
    }

    // What a paste would run: its lines, a line break at the end not counted as one more.
    [Fact]
    public void CountsTheLinesOfAPaste()
    {
        Assert.Single(Ssh.PasteLines("ls -la"));
        Assert.Single(Ssh.PasteLines("ls -la\r\n"));
        Assert.Equal(["cd /tmp", "rm -rf *"], Ssh.PasteLines("cd /tmp\r\nrm -rf *\n"));
        var many = Enumerable.Range(1, 9).Select(i => $"line {i}").ToList();
        Assert.Equal("line 1\nline 2\nline 3\nline 4\nline 5\nline 6\n…", Ssh.PastePreview(many));
    }

    // scp's arguments: the files, then where to; an IPv6 address in brackets, no host that is none.
    [Fact]
    public void ScpArguments()
    {
        Assert.Equal(["-P", "22", "-r", "--", "C:/a b.txt", "C:/dir", "deploy@lnx01:"], Ssh.ScpArguments("lnx01", 22, "deploy", ["C:/a b.txt", "C:/dir"]));
        Assert.Equal(["-P", "2222", "-r", "--", "C:/a.txt", "[fd00::5]:"], Ssh.ScpArguments("fd00::5", 2222, null, ["C:/a.txt"]));
        Assert.Null(Ssh.ScpArguments("x -oProxyCommand=calc.exe", 22, "deploy", ["C:/a.txt"]));
        Assert.Null(Ssh.ScpArguments("lnx01", 22, "deploy -oProxyCommand=calc.exe", ["C:/a.txt"]));
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

    // Someone else's file names the user and picks the host: the password saved for that user does not go with it,
    // and the file's group named like the user's own (with its credential) is not merged into it.
    [Fact]
    public void ImportKeepsSavedPasswordsAwayFromTheFilesHosts()
    {
        var store = Store.Load(File);
        var saved = new Credential { Name = "Mine", User = "jdoe", Domain = "CORP" };
        saved.SetPassword("secret");
        store.Data.Credentials.Add(saved);
        var production = new Group { Name = "Production", CredentialId = saved.Id };
        store.Data.Groups.Add(production);

        Importer.ImportRdp(store, "full address:s:rdp.attacker.example\r\nusername:s:CORP\\jdoe\r\n", "PROD-SQL01", null);
        var c = Assert.Single(store.Data.Connections);
        var used = store.Resolve(c).Credential!;
        Assert.NotEqual(saved.Id, used.Id);
        Assert.Equal(("jdoe", "CORP", null), (used.User, used.Domain, used.Secret));

        var lead = Store.Load(Path.Combine(Path.GetDirectoryName(File)!, "lead.json"));
        var theirs = new Group { Name = "Production" };
        lead.Data.Groups.Add(theirs);
        lead.Data.Connections.Add(new Connection { Name = "PROD-APP01", Host = "app.attacker.example", ParentId = theirs.Id });
        Importer.ImportSessionwright(store, Importer.Export(lead), null);
        var imported = store.Data.Connections.Single(x => x.Host == "app.attacker.example");
        Assert.NotEqual(production.Id, imported.ParentId);
        Assert.Null(store.Resolve(imported).Credential);

        // The same files again: everything is there already, and nothing empty is added beside it.
        var (credentials, groups) = (store.Data.Credentials.Count, store.Data.Groups.Count);
        Importer.ImportRdp(store, "full address:s:rdp.attacker.example\r\nusername:s:CORP\\jdoe\r\n", "PROD-SQL01", null);
        Importer.ImportSessionwright(store, Importer.Export(lead), null);
        Assert.Equal((credentials, groups), (store.Data.Credentials.Count, store.Data.Groups.Count));
    }

    // A password in someone's export goes into a credential the import makes, never into the user's own.
    [Fact]
    public void ImportNeverWritesIntoTheUsersCredential()
    {
        var store = Store.Load(File);
        var mine = new Credential { Name = "Mine", User = "jdoe", Domain = "CORP" };
        store.Data.Credentials.Add(mine);
        var lead = Store.Load(Path.Combine(Path.GetDirectoryName(File)!, "lead2.json"));
        var theirs = new Credential { Name = "Theirs", User = "jdoe", Domain = "CORP" };
        theirs.SetPassword("wrong");
        lead.Data.Credentials.Add(theirs);
        lead.Data.Connections.Add(new Connection { Name = "APP", Host = "app.example", CredentialId = theirs.Id });
        Importer.ImportSessionwright(store, Importer.Export(lead, "export-pass-1"), null, _ => "export-pass-1");
        Assert.Null(mine.Secret);
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
                <Node Name="Vnc" Type="Connection" Hostname="vnc01" Port="5900" Protocol="VNC" />
              </Node>
              <Node Name="Lab" Type="Connection" Hostname="lab01" Port="3390" Protocol="RDP" Username="admin" Domain="corp" />
            </mrng:Connections>
            """);

        Assert.Equal(3, Importer.ImportMRemoteNG(store, xml, null).Added); // the remote desktops and the terminal, not VNC

        Assert.Single(store.Data.Credentials); // same user/domain reused (case-insensitive)
        var web = store.Data.Connections.Single(c => c.Name == "Web");
        Assert.Null(web.CredentialId);
        Assert.Equal("admin", store.Resolve(web).Credential!.User);
        Assert.Equal("mRemoteNG / Prod", store.PathOf(web));
        Assert.Equal(3390, store.Data.Connections.Single(c => c.Name == "Lab").Port);
        var ssh = store.Data.Connections.Single(c => c.Name == "Ssh");
        Assert.Equal((ConnectionProtocol.Ssh, "lnx01", null), (ssh.Protocol, ssh.Address, ssh.Rdp));
        Assert.Equal("admin", store.Resolve(ssh).Credential!.User); // its folder's account
    }

    [Fact]
    public void Import_Twice_AddsNothing_AndLeavesNoEmptyGroupsOrCredentials()
    {
        var store = Store.Load(File);
        var xml = XDocument.Parse("""
            <Connections>
              <Node Name="Screens" Type="Container" Username="root"><Node Name="Vnc" Type="Connection" Hostname="lnx" Protocol="VNC" /></Node>
              <Node Name="" Type="Connection" Hostname="web01" Protocol="RDP" />
            </Connections>
            """);

        Assert.Equal(1, Importer.ImportMRemoteNG(store, xml, null).Added);
        var web = Assert.Single(store.Data.Connections);
        Assert.Equal(("", "web01"), (web.Name, web.Title));            // no name in the file: Title shows the host
        Assert.Equal("mRemoteNG", Assert.Single(store.Data.Groups).Name); // a folder with nothing Sessionwright connects to (VNC) is not created
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
