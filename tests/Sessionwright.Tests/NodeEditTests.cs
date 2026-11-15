using System.IO;
using Xunit;

namespace Sessionwright.Tests;

// What the editor checks and writes, without its window.
public sealed class NodeEditTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sessionwright-edit").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    Store NewStore() => Store.Load(Path.Combine(_dir, "data.json"));

    static NodeForm Form(Action<NodeForm>? change = null)
    {
        var form = new NodeForm { Name = "Server", Host = "srv01.example.com", Port = "3389" };
        change?.Invoke(form);
        return form;
    }

    [Fact]
    public void AConnectionIsSavedAsTyped()
    {
        var store = NewStore();
        var c = new Connection();
        var parent = Guid.NewGuid();

        Assert.Null(NodeEdit.Save(store, c, Form(f =>
        {
            (f.Name, f.Tags, f.Notes, f.ParentId, f.Color) = ("  Server  ", " aos, d365 ", " restart after 18:00 ", parent, "#E5534B");
            f.Links = [("F&O", " https://example.com/fo "), ("", "")];
        })));

        Assert.Equal(("Server", "srv01.example.com", 3389, "aos, d365", "restart after 18:00", parent, "#E5534B"),
            (c.Name, c.Host, c.Port, c.Tags, c.Notes, c.ParentId!.Value, c.Color));
        var link = Assert.Single(c.Links!); // the empty row is not a link
        Assert.Equal(("F&O", "https://example.com/fo"), (link.Name, link.Url));
    }

    // "host:port" in the host box wins over the port box; without it the port box counts, and has to be a port.
    [Theory]
    [InlineData("srv01:3390", "3389", 3390)]
    [InlineData("srv01", "50123", 50123)]
    public void ThePortComesFromTheAddressWhenItHasOne(string host, string portBox, int port)
    {
        var c = new Connection();
        Assert.Null(NodeEdit.Save(NewStore(), c, Form(f => (f.Host, f.Port) = (host, portBox))));
        Assert.Equal(("srv01", port), (c.Host, c.Port));
    }

    // After an error nothing was written: cancelling the editor then leaves the connection as it was.
    [Theory]
    [InlineData("", "3389", NodeField.Host)]
    [InlineData("srv01", "0", NodeField.Port)]
    [InlineData("srv01", "abc", NodeField.Port)]
    public void AWrongAddressIsAnErrorAndNothingIsWritten(string host, string port, NodeField field)
    {
        var c = new Connection { Name = "Old", Host = "old.example.com", Port = 3390, Notes = "old notes" };

        var error = NodeEdit.Save(NewStore(), c, Form(f => (f.Name, f.Host, f.Port, f.Notes) = ("New", host, port, "new notes")));

        Assert.Equal(field, error!.Field);
        Assert.Equal(("Old", "old.example.com", 3390, "old notes"), (c.Name, c.Host, c.Port, c.Notes));
    }

    [Fact]
    public void ALinkThatIsNoWebAddressIsRefusedWithItsRow()
    {
        var c = new Connection { Host = "old.example.com" };
        var error = NodeEdit.Save(NewStore(), c, Form(f => f.Links = [("Portal", "https://example.com"), ("Tool", "cmd.exe /c calc")]));
        Assert.Equal((NodeField.Link, 1), (error!.Field, error.Index));
        Assert.Equal("old.example.com", c.Host);
    }

    // Readdressed by hand, a connection the Azure sync made is the user's from then on; left alone, it stays the sync's.
    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("10.0.0.9", false)]
    public void ReaddressingASyncedConnectionTakesItFromTheSync(string host, bool stillSynced)
    {
        var c = new Connection { Host = "10.0.0.5", Synced = true };
        Assert.Null(NodeEdit.Save(NewStore(), c, Form(f => f.Host = host)));
        Assert.Equal(stillSynced, c.Synced);
    }

    [Fact]
    public void AGroupNeedsAName()
    {
        var g = new Group { Name = "Old" };
        Assert.Equal(NodeField.Name, NodeEdit.Save(NewStore(), g, Form(f => f.Name = "  "))!.Field);
        Assert.Equal("Old", g.Name);
    }

    // One source at a time: choosing one clears the other, and "added by you" clears both.
    [Fact]
    public void AGroupsConnectionsComeFromOnePlace()
    {
        var store = NewStore();
        var rule = new AzureSyncRule { SubscriptionId = "sub" };
        var g = new Group { Name = "Team", AzureSync = rule };

        Assert.Null(NodeEdit.Save(store, g, Form(f => (f.Source, f.SharedList, f.Sync) = (NodeForm.SharedSource, @" \\server\share\team.json ", rule))));
        Assert.Equal((@"\\server\share\team.json", (AzureSyncRule?)null), (g.SharedList, g.AzureSync));

        Assert.Null(NodeEdit.Save(store, g, Form(f => (f.Source, f.SharedList, f.Sync) = (NodeForm.AzureSource, @"\\server\share\team.json", rule))));
        Assert.Equal(((string?)null, rule), (g.SharedList, g.AzureSync));

        Assert.Null(NodeEdit.Save(store, g, Form(f => (f.Source, f.SharedList, f.Sync) = (NodeForm.OwnSource, @"\\server\share\team.json", rule))));
        Assert.Equal(((string?)null, (AzureSyncRule?)null), (g.SharedList, g.AzureSync));
    }

    [Fact]
    public void ASharedListOverPlainHttpIsRefused()
    {
        var g = new Group { Name = "Team" };
        var error = NodeEdit.Save(NewStore(), g, Form(f => (f.Source, f.SharedList) = (NodeForm.SharedSource, "http://example.com/team.json")));
        Assert.Equal(NodeField.SharedList, error!.Field);
        Assert.Null(g.SharedList);
    }

    [Theory]
    [InlineData(NodeForm.VpnMode, "", "", "3", NodeField.Vpn)]
    [InlineData(NodeForm.CommandMode, "", " ", "3", NodeField.Command)]
    [InlineData(NodeForm.VpnMode, "Company VPN", "", "0", NodeField.WaitMinutes)]
    [InlineData(NodeForm.CommandMode, "", "vpn.exe", "61", NodeField.WaitMinutes)]
    public void BeforeConnectingNeedsWhatItsModeAsksFor(string mode, string vpn, string command, string wait, NodeField field)
    {
        var c = new Connection();
        var error = NodeEdit.Save(NewStore(), c, Form(f => (f.BeforeMode, f.Vpn, f.Command, f.WaitMinutes) = (mode, vpn, command, wait)));
        Assert.Equal(field, error!.Field);
        Assert.Null(c.BeforeConnect);
    }

    // "As its group" writes nothing; "nothing" writes an empty rule, which switches a group's off; a VPN is kept with its wait.
    [Fact]
    public void BeforeConnectingIsSavedByItsMode()
    {
        var store = NewStore();
        var c = new Connection();

        Assert.Null(NodeEdit.Save(store, c, Form()));
        Assert.Null(c.BeforeConnect);

        Assert.Null(NodeEdit.Save(store, c, Form(f => f.BeforeMode = NodeForm.NoneMode)));
        Assert.False(c.BeforeConnect!.IsSet);

        Assert.Null(NodeEdit.Save(store, c, Form(f => (f.BeforeMode, f.Vpn, f.WaitMinutes) = (NodeForm.VpnMode, " Company VPN ", "5"))));
        Assert.Equal(("Company VPN", 5), (c.BeforeConnect!.Vpn, c.BeforeConnect.WaitMinutes));
    }

    // Notes sealed with a master password that was not entered are not there to edit: saving leaves them as they are.
    [Fact]
    public void LockedNotesAreNotOverwritten()
    {
        var blob = new byte[] { 1, 2, 3 };
        var c = new Connection { SealedNotes = blob };

        Assert.Null(NodeEdit.Save(NewStore(), c, Form(f => (f.Notes, f.NotesLocked) = ("", true))));
        Assert.Same(blob, c.SealedNotes);

        Assert.Null(NodeEdit.Save(NewStore(), c, Form(f => f.Notes = "typed after unlocking")));
        Assert.Equal(("typed after unlocking", (byte[]?)null), (c.Notes, c.SealedNotes));
    }

    [Fact]
    public void AnAutoStartNeedsATimeOfDay()
    {
        var c = new Connection();
        Assert.Equal(NodeField.AutoStartTime, NodeEdit.Save(NewStore(), c, Form(f => (f.AutoStart, f.AutoStartTime) = (AutoStartDays.Weekdays, "25:00")))!.Field);
        Assert.Null(NodeEdit.Save(NewStore(), c, Form(f => (f.AutoStart, f.AutoStartTime) = (AutoStartDays.Weekdays, "7:30"))));
        Assert.Equal((AutoStartDays.Weekdays, "07:30"), (c.AutoStart!.Days, c.AutoStart.Time));
    }
}
