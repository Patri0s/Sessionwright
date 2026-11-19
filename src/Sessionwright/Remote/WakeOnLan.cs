using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Sessionwright;

/// <summary>
/// Wakes a machine that is off and not in Azure: the "magic packet" of Wake-on-LAN, sent to the network card's MAC
/// address. It is a broadcast, so it reaches machines in the same local network as this PC (or one whose router passes
/// directed broadcasts on); the machine has to allow it in its firmware and network card settings. Nothing answers a
/// magic packet: whether it worked shows only when the machine's RDP port answers.
/// </summary>
static class WakeOnLan
{
    const int Port = 9; // "discard": the usual one, and nothing listens there on the way

    /// <summary>Six bytes from "00-11-22-33-44-55", "00:11:22:33:44:55", "0011.2233.4455" or "001122334455".</summary>
    internal static byte[]? ParseMac(string? text)
    {
        var digits = new string([.. (text ?? "").Where(ch => ch is not ('-' or ':' or '.' or ' '))]);
        if (digits.Length != 12 || !digits.All(Uri.IsHexDigit)) return null;
        var mac = Convert.FromHexString(digits);
        // All zeros or all ones is no card's address (the second is the broadcast address itself).
        return mac.All(b => b == 0) || mac.All(b => b == 0xFF) ? null : mac;
    }

    /// <summary>As Windows writes it: "00-11-22-33-44-55".</summary>
    internal static string Format(byte[] mac) => string.Join("-", mac.Select(b => b.ToString("X2")));

    /// <summary>Six times FF, then the address sixteen times.</summary>
    internal static byte[] Packet(byte[] mac) => [.. Enumerable.Repeat((byte)0xFF, 6), .. Enumerable.Repeat(mac, 16).SelectMany(m => m)];

    /// <summary>The network's broadcast address: the host part all ones.</summary>
    internal static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        var (a, m) = (address.GetAddressBytes(), mask.GetAddressBytes());
        return new IPAddress([.. a.Select((b, i) => (byte)(b | ~m[i]))]);
    }

    /// <summary>
    /// Sends the packet out of every network card that is up, to its network's broadcast address and the general one,
    /// and to the machine's own address when its name still resolves (a router may know where that is). Returns how
    /// many went out: none means this PC has no network to send it on.
    /// </summary>
    public static async Task<int> SendAsync(byte[] mac, string? host)
    {
        var packet = Packet(mac);
        var sent = 0;
        foreach (var card in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        foreach (var local in card.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            try
            {
                // Bound to the card's address: an unbound broadcast leaves only through the card of the default route.
                using var udp = new UdpClient(new IPEndPoint(local.Address, 0)) { EnableBroadcast = true };
                foreach (var to in new[] { IPAddress.Broadcast, Broadcast(local.Address, local.IPv4Mask) }.Distinct())
                    sent += await udp.SendAsync(packet, packet.Length, new IPEndPoint(to, Port)) == packet.Length ? 1 : 0;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { Log.Info($"Wake-on-LAN: nothing sent from {local.Address}: {ex.Message}"); }
        }
        if (!string.IsNullOrWhiteSpace(host))
        {
            try
            {
                using var timeout = new CancellationTokenSource(2000);
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                foreach (var to in (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token)).Take(4))
                    sent += await udp.SendAsync(packet, packet.Length, new IPEndPoint(to, Port)) == packet.Length ? 1 : 0;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { } // the broadcasts are what counts
        }
        return sent;
    }

    /// <summary>
    /// The MAC address of <paramref name="host"/>, asked from the local network (ARP): there only while the machine is
    /// on and in the same local network as this PC; null otherwise (behind a router the answer would be the router's,
    /// which Windows does not give for an address outside the network).
    /// </summary>
    public static async Task<byte[]?> DetectAsync(string host)
    {
        try
        {
            using var timeout = new CancellationTokenSource(3000);
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token);
            foreach (var address in addresses.Take(4))
                if (await Task.Run(() => Ask(address)) is { } mac) return mac;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { }
        return null;

        static byte[]? Ask(IPAddress address)
        {
            var mac = new byte[6];
            var length = mac.Length;
            var found = SendARP(BitConverter.ToUInt32(address.GetAddressBytes()), 0, mac, ref length) == 0 && length == 6;
            return found && ParseMac(Format(mac)) != null ? mac : null;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destination, uint source, byte[] mac, ref int length);
}
