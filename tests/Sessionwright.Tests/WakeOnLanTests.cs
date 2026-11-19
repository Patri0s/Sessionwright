using System.IO;
using System.Net;
using Xunit;

namespace Sessionwright.Tests;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("00-11-22-AA-BB-CC")]
    [InlineData("00:11:22:aa:bb:cc")]
    [InlineData("0011.22aa.bbcc")]
    [InlineData(" 001122AABBCC ")]
    public void ReadsAMacAddressHoweverItIsWritten(string text)
    {
        Assert.Equal("00-11-22-AA-BB-CC", WakeOnLan.Format(WakeOnLan.ParseMac(text)!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00-11-22-AA-BB")]       // five pairs
    [InlineData("00-11-22-AA-BB-CC-DD")] // seven
    [InlineData("00-11-22-AA-BB-GG")]    // not hexadecimal
    [InlineData("00-00-00-00-00-00")]    // no card's address
    [InlineData("FF-FF-FF-FF-FF-FF")]    // the broadcast address
    public void WhatIsNoMacAddressIsNotTaken(string? text)
    {
        Assert.Null(WakeOnLan.ParseMac(text));
    }

    [Fact]
    public void TheMagicPacketIsSixTimesFfThenTheAddressSixteenTimes()
    {
        byte[] mac = [0x00, 0x11, 0x22, 0xAA, 0xBB, 0xCC];
        var packet = WakeOnLan.Packet(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(mac, packet[(6 + i * 6)..(12 + i * 6)]);
    }

    [Theory]
    [InlineData("192.168.1.20", "255.255.255.0", "192.168.1.255")]
    [InlineData("10.4.7.9", "255.255.240.0", "10.4.15.255")]
    [InlineData("172.16.0.5", "255.255.255.255", "172.16.0.5")]
    public void TheBroadcastAddressHasTheHostPartAllOnes(string address, string mask, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), WakeOnLan.Broadcast(IPAddress.Parse(address), IPAddress.Parse(mask)));
    }

    [Fact]
    public void TheEditorSavesTheAddressInOneFormAndRefusesWhatIsNone()
    {
        var store = Store.Load(Path.Combine(Path.GetTempPath(), "sessionwright-wol-" + Guid.NewGuid().ToString("N"), "data.json"));
        var c = new Connection();
        Assert.Null(NodeEdit.Save(store, c, new NodeForm { Host = "srv01", Port = "3389", Mac = "00:11:22:aa:bb:cc" }));
        Assert.Equal("00-11-22-AA-BB-CC", c.Mac);

        var error = NodeEdit.Save(store, c, new NodeForm { Host = "srv01", Port = "3389", Mac = "00:11:22" });
        Assert.Equal(NodeField.Mac, error?.Field);
        Assert.Equal("00-11-22-AA-BB-CC", c.Mac); // nothing was written

        Assert.Null(NodeEdit.Save(store, c, new NodeForm { Host = "srv01", Port = "3389", Mac = " " }));
        Assert.Null(c.Mac);
    }

    // The address belongs to the machine, not to this PC: it travels with an export and with a team's shared list.
    [Fact]
    public void TheAddressTravelsWithAnExportAndASharedList()
    {
        static Store New() => Store.Load(Path.Combine(Path.GetTempPath(), "sessionwright-wol-" + Guid.NewGuid().ToString("N"), "data.json"));
        var from = New();
        from.Data.Connections.Add(new Connection { Name = "srv01", Host = "srv01.example.com", Mac = "00-11-22-AA-BB-CC" });
        var json = Importer.Export(from);

        var to = New();
        Importer.ImportSessionwright(to, json, null);
        Assert.Equal("00-11-22-AA-BB-CC", Assert.Single(to.Data.Connections).Mac);

        var (_, shared) = SharedLists.Parse(json, new Group { Name = "Team" }, _ => false);
        Assert.Equal("00-11-22-AA-BB-CC", Assert.Single(shared).Mac);
    }
}
