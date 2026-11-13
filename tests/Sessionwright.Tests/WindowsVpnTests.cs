using System.IO;
using Xunit;

namespace Sessionwright.Tests;

public class WindowsVpnTests
{
    // An entry as Windows writes one for a VPN set up in Settings (the keys it needs to count it as one).
    static string Entry(string name, string server) => $"""
        [{name}]
        Encoding=1
        PBVersion=8
        Type=2
        AutoLogon=0
        UseRasCredentials=1
        VpnStrategy=1
        MEDIA=rastapi
        Port=VPN2-0
        Device=WAN Miniport (L2TP)

        DEVICE=vpn
        PhoneNumber={server}
        AreaCode=
        CountryCode=0
        CountryID=0
        UseDialingRules=0
        Comment=
        FriendlyName=
        LastSelectedPhone=0
        PromoteAlternates=0
        TryNextAlternateOnFail=1

        """;

    // The Remote Access API fills the list only when the entry structure has the size it expects.
    [Fact]
    public void ReadsTheVpnsOfAPhonebook()
    {
        var dir = Directory.CreateTempSubdirectory("sessionwright-vpn").FullName;
        try
        {
            var phonebook = Path.Combine(dir, "test.pbk");
            File.WriteAllText(phonebook, Entry("Second VPN", "vpn2.example.test") + Entry("Company VPN", "vpn.example.test"));
            Assert.Equal(["Company VPN", "Second VPN"], WindowsVpn.Entries(phonebook).Select(e => e.Name));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A name that is not there says so, before anything is dialed.
    [Fact]
    public void SaysWhenTheVpnIsNotThere()
    {
        var dir = Directory.CreateTempSubdirectory("sessionwright-vpn").FullName;
        try
        {
            var phonebook = Path.Combine(dir, "test.pbk");
            File.WriteAllText(phonebook, Entry("Company VPN", "vpn.example.test"));
            Assert.Equal(Loc.Get("VpnNotFound"), WindowsVpn.Connect("Nowhere VPN", "user", null, "x", phonebook));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
