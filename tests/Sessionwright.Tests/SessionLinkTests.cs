using System.IO;
using Microsoft.Win32;
using Xunit;

namespace Sessionwright.Tests;

public class SessionLinkTests
{
    // A link copied from a connection opens the same one again: by its id, and by its address for anyone else.
    [Theory]
    [InlineData("srv01.example.com", 3389, "sessionwright://connect/srv01.example.com?id=")]
    [InlineData("10.0.0.5", 3390, "sessionwright://connect/10.0.0.5:3390?id=")]
    [InlineData("fe80::1", 3389, "sessionwright://connect/[fe80::1]?id=")]
    public void ALinkLeadsBackToItsConnection(string host, int port, string start)
    {
        var c = new Connection { Name = "Server", Host = host, Port = port };
        var link = SessionLink.For(c);
        Assert.StartsWith(start, link);
        Assert.Equal(new SessionLink.Target(host, port, c.Id), SessionLink.Parse(link));
    }

    // A link comes from any web page: nothing but a host name or an IP address becomes the server.
    [Theory]
    [InlineData("sessionwright://connect/srv01 /admin")]
    [InlineData("sessionwright://connect/srv01%20%2Fadmin")]
    [InlineData("sessionwright://connect/srv01:99999")]
    [InlineData("sessionwright://connect/")]
    [InlineData("sessionwright://delete/srv01")]
    [InlineData("https://connect/srv01")]
    [InlineData("sessionwright://connect/\"srv01\"")]
    public void RefusesAnythingButAnAddress(string link) => Assert.Null(SessionLink.Parse(link));

    [Fact]
    public void ReadsAnIdOnlyWhenItIsOne()
    {
        Assert.Null(SessionLink.Parse("sessionwright://connect/srv01?id=not-a-guid")!.Id);
        Assert.Equal(new SessionLink.Target("srv01", 3389, null), SessionLink.Parse("SessionWright://Connect/srv01/"));
    }

    // The id wins, so a link still opens the connection after its host moved; else the first saved one to the address.
    [Fact]
    public void FindsTheConnectionByIdThenByAddress()
    {
        var moved = new Connection { Host = "new-host", Port = 3389 };
        var other = new Connection { Host = "srv01", Port = 3389 };
        var list = new[] { other, moved };
        Assert.Same(moved, SessionLink.Find(list, new SessionLink.Target("old-host", 3389, moved.Id)));
        Assert.Same(other, SessionLink.Find(list, new SessionLink.Target("SRV01", 3389, Guid.NewGuid())));
        Assert.Null(SessionLink.Find(list, new SessionLink.Target("srv01", 3390, null)));
    }

    // Registered for this user only, with the link passed to the exe as one argument.
    [Fact]
    public void RegistersAndRemovesTheScheme()
    {
        var key = $@"Software\Sessionwright.Tests\{Guid.NewGuid()}";
        try
        {
            SessionLink.Register(@"C:\Apps\Sessionwright\current\Sessionwright.exe", Registry.CurrentUser, key);
            using (var scheme = Registry.CurrentUser.OpenSubKey(key)!)
            {
                Assert.Equal("", scheme.GetValue("URL Protocol"));
                Assert.Equal(@"""C:\Apps\Sessionwright\current\Sessionwright.exe"" ""%1""", scheme.OpenSubKey(@"shell\open\command")!.GetValue(""));
            }
            SessionLink.Unregister(Registry.CurrentUser, key);
            Assert.Null(Registry.CurrentUser.OpenSubKey(key));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Sessionwright.Tests", throwOnMissingSubKey: false); }
    }
}

public class PortableLinkTests
{
    // A portable copy (Velopack puts a .portable file next to its current folder) must not take the links from the install.
    [Fact]
    public void APortableCopyRegistersNothing()
    {
        var root = Directory.CreateTempSubdirectory("sessionwright-portable").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "current"));
            File.WriteAllText(Path.Combine(root, ".portable"), "");
            var before = Registry.CurrentUser.OpenSubKey(@"Software\Classes\sessionwright\shell\open\command")?.GetValue("") as string;
            SessionLink.RegisterInstalled(Path.Combine(root, "current", "Sessionwright.exe"));
            Assert.Equal(before, Registry.CurrentUser.OpenSubKey(@"Software\Classes\sessionwright\shell\open\command")?.GetValue("") as string);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
