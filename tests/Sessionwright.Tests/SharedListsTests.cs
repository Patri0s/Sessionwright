using System.IO;
using Xunit;

namespace Sessionwright.Tests;

public sealed class SharedListsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sessionwright-shared").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string DataFile => Path.Combine(_dir, "data.json");

    /// <summary>A team lead's list: a group with a credential and a command, a connection in it, one at the top.</summary>
    string TeamFile(out Guid inGroup, out Guid atTop, string host = "srv01.example.com")
    {
        var lead = Store.Load(Path.Combine(_dir, "lead.json"));
        var credential = new Credential { Name = "Lead", User = "lead" };
        credential.SetPassword("secret");
        lead.Data.Credentials.Add(credential);
        var team = new Group { Name = "Team" };
        var servers = new Group { Name = "Servers", ParentId = team.Id, CredentialId = credential.Id, Color = "#E5534B",
            BeforeConnect = new BeforeConnect { Command = "evil.exe --run" } };
        lead.Data.Groups.Add(team);
        lead.Data.Groups.Add(servers);
        var first = new Connection { Name = "SRV01", Host = host, Port = 3390, ParentId = servers.Id, CredentialId = credential.Id,
            BeforeConnect = new BeforeConnect { Vpn = "Company VPN", VpnCredentialId = credential.Id } };
        var second = new Connection { Name = "SRV02", Host = "srv02.example.com", ParentId = team.Id };
        lead.Data.Connections.Add(first);
        lead.Data.Connections.Add(second);
        (inGroup, atTop) = (first.Id, second.Id);
        var file = Path.Combine(_dir, "team.json");
        File.WriteAllText(file, Importer.ExportShared(lead, team));
        return file;
    }

    // The file's items hang under the user's own group, read-only; credentials and commands stay out, a VPN to
    // connect comes along without its credential.
    [Fact]
    public async Task ReadsAListUnderTheUsersGroupWithoutCredentialsOrCommands()
    {
        var file = TeamFile(out var inGroup, out var atTop);
        var store = Store.Load(DataFile);
        var mine = new Group { Name = "From the team", SharedList = file };
        store.Data.Groups.Add(mine);

        await SharedLists.RefreshAsync(store, mine);

        var servers = store.Data.Groups.Single(g => g.Name == "Servers");
        Assert.True(servers.Shared);
        Assert.Equal((mine.Id, (Guid?)null, "#E5534B", (BeforeConnect?)null), (servers.ParentId!.Value, servers.CredentialId, servers.Color, servers.BeforeConnect));
        var first = store.Data.Connections.Single(c => c.Id == inGroup);
        Assert.Equal(("srv01.example.com", 3390, servers.Id, (Guid?)null, true), (first.Host, first.Port, first.ParentId!.Value, first.CredentialId, first.Shared));
        Assert.Equal(("Company VPN", (Guid?)null), (first.BeforeConnect!.Vpn, first.BeforeConnect.VpnCredentialId));
        Assert.Equal(mine.Id, store.Data.Connections.Single(c => c.Id == atTop).ParentId);
        Assert.Empty(store.Data.Credentials);
        Assert.False(store.CanMove(first, null));
        Assert.False(store.CanMove(new Connection(), servers));
        Assert.Same(mine, store.OwnGroup(servers));
    }

    // Saving writes the user's group and what they set on the list's items, never the items; the next start shows
    // the list from its copy on this PC with that state back.
    [Fact]
    public async Task SavesOnlyTheUsersOwnStateAndStartsFromTheCopy()
    {
        var file = TeamFile(out var inGroup, out _);
        var store = Store.Load(DataFile);
        var mine = new Group { Name = "From the team", SharedList = file };
        store.Data.Groups.Add(mine);
        await SharedLists.RefreshAsync(store, mine);
        var used = new DateTime(2026, 11, 13, 9, 0, 0);
        (store.Data.Connections.Single(c => c.Id == inGroup).Favorite, store.Data.Connections.Single(c => c.Id == inGroup).LastUsed) = (true, used);
        store.Save();

        var saved = File.ReadAllText(DataFile);
        Assert.DoesNotContain("srv01.example.com", saved);
        Assert.DoesNotContain("Servers", saved);
        Assert.Equal(4, store.Data.Groups.Count + store.Data.Connections.Count); // still in memory: the group and the three items

        File.Delete(file); // the share is away: the copy is enough
        var again = Store.Load(DataFile);
        Assert.Single(again.Data.Groups);
        SharedLists.LoadCached(again);
        var first = again.Data.Connections.Single(c => c.Id == inGroup);
        Assert.Equal((true, used, true), (first.Favorite, first.LastUsed!.Value, first.Shared));
    }

    // A refresh changes items in place (an open session keeps its connection), adds new ones, removes those that left
    // the file, and leaves alone anything of the user's own with the same id.
    [Fact]
    public async Task ARefreshUpdatesInPlaceAndRemovesWhatLeftTheFile()
    {
        var file = TeamFile(out var inGroup, out var atTop);
        var store = Store.Load(DataFile);
        var mine = new Group { Name = "From the team", SharedList = file };
        store.Data.Groups.Add(mine);
        await SharedLists.RefreshAsync(store, mine);
        var before = store.Data.Connections.Single(c => c.Id == inGroup);

        var lead = Store.Load(Path.Combine(_dir, "lead2.json"));
        var team = new Group { Name = "Team" };
        lead.Data.Groups.Add(team);
        lead.Data.Connections.Add(new Connection { Id = inGroup, Name = "SRV01", Host = "moved.example.com", ParentId = team.Id });
        File.WriteAllText(file, Importer.ExportShared(lead, team));
        await SharedLists.RefreshAsync(store, mine);

        Assert.Same(before, store.Data.Connections.Single(c => c.Id == inGroup));
        Assert.Equal(("moved.example.com", 3389, mine.Id), (before.Host, before.Port, before.ParentId!.Value));
        Assert.DoesNotContain(store.Data.Connections, c => c.Id == atTop);
        Assert.DoesNotContain(store.Data.Groups, g => g.Name == "Servers");

        // The user's own connection with an id from the file is not the file's to replace.
        var own = new Connection { Id = atTop, Host = "own.example.com" };
        store.Data.Connections.Add(own);
        lead.Data.Connections.Add(new Connection { Id = atTop, Host = "theirs.example.com", ParentId = team.Id });
        File.WriteAllText(file, Importer.ExportShared(lead, team));
        await SharedLists.RefreshAsync(store, mine);
        Assert.Equal("own.example.com", store.Data.Connections.Single(c => c.Id == atTop).Host);
    }

    [Theory]
    [InlineData("http://example.com/team.json", false)]
    [InlineData("https://example.com/team.json", true)]
    [InlineData(@"\\server\share\team.json", true)]
    public void TakesHttpsAndFilesButNotPlainHttp(string source, bool allowed) => Assert.Equal(allowed, SharedLists.IsAllowed(source));

    [Theory]
    [InlineData("""{ "Format": "SomethingElse", "Groups": [] }""")]
    [InlineData("<html>Sign in to continue</html>")]
    public void RefusesAFileThatIsNoExport(string content) =>
        Assert.Throws<InvalidDataException>(() => SharedLists.Parse(content, new Group(), _ => false));
}
