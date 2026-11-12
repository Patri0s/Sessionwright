using System.IO;
using Sessionwright;
using Xunit;

public class LinksTests
{
    // Only a web address is ever typed into the server's Run box: never something that would run as a command.
    [Theory]
    [InlineData("https://contoso-dev12.axcloud.dynamics.com/", true)]
    [InlineData("http://intranet/reports?id=5&x=y#top", true)]
    [InlineData("HTTPS://lcs.dynamics.com/v2", true)]
    [InlineData("cmd /c del C:\\x", false)]
    [InlineData("https://example.com/a b", false)]
    [InlineData("https://example.com/\"&calc", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("powershell", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    [InlineData("https://admin:hunter2@example.com/", false)] // a password in the address would be kept and shown in plain text
    public void TypesOnlyWebAddresses(string url, bool safe) => Assert.Equal(safe, Links.IsSafe(url));

    // What goes into a log or a problem report: where the link leads, not what may carry a token or a password.
    [Theory]
    [InlineData("https://example.com/a/b", "https://example.com/a/b")]
    [InlineData("https://example.com/blob?sv=2024&sig=SECRET#frag", "https://example.com/blob?…")]
    [InlineData("https://admin:hunter2@example.com/x?token=SECRET", "https://example.com/x?…")]
    [InlineData("https://{name}.example.com/path@v2", "https://{name}.example.com/path@v2")]
    public void LogsAndReportsGetTheAddressWithoutSecrets(string url, string shown) => Assert.Equal(shown, Links.WithoutSecrets(url));

    [Theory]
    [InlineData("http://updates.example.com/sessionwright", false)]
    [InlineData(" HTTP://updates.example.com", false)]
    [InlineData("https://github.com/owner/repo", true)]
    [InlineData(@"\\fileserver\apps\Sessionwright", true)]
    public void UpdatesAreNotTakenOverPlainHttp(string feed, bool trusted) => Assert.Equal(trusted, Updater.IsTrusted(feed));

    [Fact]
    public void FillsTheTemplateForTheConnection()
    {
        var c = new Connection { Name = "DEV-12", Host = "dev12.westeurope.cloudapp.azure.com", AzureVm = new AzureVmLink { Name = "DEV12-VM-1" } };
        Assert.Equal("https://DEV-12.axcloud.dynamics.com/", Links.Expand("https://{name}.axcloud.dynamics.com/", c));
        Assert.Equal("https://dev12.westeurope.cloudapp.azure.com/namespaces/AXSF", Links.Expand(" https://{HOST}/namespaces/AXSF ", c));
        Assert.Equal("https://portal/vm/DEV12-VM-1", Links.Expand("https://portal/vm/{vm}", c));
        Assert.True(Links.IsSafeTemplate("https://{name}.axcloud.dynamics.com/"));
        Assert.False(Links.IsSafeTemplate("{name} /c calc"));
    }

    // A connection opens its own links first, then its groups' from the nearest, each filled in for it; a group opens
    // its links as they are, without the templates meant for its connections.
    [Fact]
    public void InheritsTheGroupsLinks()
    {
        var dir = Directory.CreateTempSubdirectory("sessionwright-links-").FullName;
        try
        {
            var store = Store.Load(Path.Combine(dir, "data.json"));
            var top = new Group { Name = "D365", Links = [new NodeLink { Name = "LCS", Url = "https://lcs.dynamics.com/" }] };
            var dev = new Group { Name = "DEV", ParentId = top.Id, Links = [new NodeLink { Name = "F&O", Url = "https://{name}.axcloud.dynamics.com/" }] };
            var c = new Connection { Name = "dev12", Host = "h", ParentId = dev.Id, Links = [new NodeLink { Url = "https://own.example.com/x" }] };
            store.Data.Groups.Add(top);
            store.Data.Groups.Add(dev);
            store.Data.Connections.Add(c);

            var links = store.LinksOf(c);
            Assert.Equal(["own.example.com", "F&O", "LCS"], links.Select(l => l.Name));
            Assert.Equal("https://dev12.axcloud.dynamics.com/", links[1].Url);
            Assert.Equal([null, dev, top], links.Select(l => l.From));

            Assert.Equal(["LCS"], store.LinksOf(dev).Select(l => l.Name)); // its own template is for its connections
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class WhatsNewTests
{
    // After an update only what the user has not seen yet is told; the list is kept newest first, in both languages.
    [Fact]
    public void TellsOnlyWhatIsNewerThanTheDayAlreadySeen()
    {
        Assert.Equal(WhatsNew.Entries.Length, WhatsNew.Since(null).Count);
        Assert.Empty(WhatsNew.Since(WhatsNew.Latest));
        Assert.Single(WhatsNew.Since(WhatsNew.Entries[1].Day)); // a day already seen: only the days after it
        Assert.Equal(WhatsNew.Entries.Select(e => e.Day).OrderByDescending(d => d, StringComparer.Ordinal), WhatsNew.Entries.Select(e => e.Day));
        Assert.All(WhatsNew.Entries.SelectMany(e => e.Items), i => Assert.True(i.En.Length > 0 && i.Pl.Length > 0));
        Assert.All(WhatsNew.Entries, e => Assert.True(DateTime.TryParseExact(e.Day, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _)));
    }
}
