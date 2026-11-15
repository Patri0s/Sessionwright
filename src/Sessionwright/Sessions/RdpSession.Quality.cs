using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinForms = System.Windows.Forms;

namespace Sessionwright;

// How good the connection is: round trip time and the bars shown for it. Part of RdpSession (see RdpSession.cs).
public sealed partial class RdpSession
{
    // ---------- Connection quality ----------

    // The RDP client's own measurement arrives when the server runs its network detection: 4-40 s after connecting on
    // DEV-12/13, on some connections never. A TCP handshake with the RDP port gives the latency at once and then every
    // 30 s; the bandwidth (and the server's quality level) are added when the client reports them.
    int? _serverQuality, _bandwidth, _rtt;
    CancellationTokenSource? _latency;

    void StartLatencyProbe()
    {
        _latency?.Cancel();
        if (!string.IsNullOrWhiteSpace(_options.Gateway)) return; // the handshake would time the gateway, not the server
        var cancel = (_latency = new CancellationTokenSource()).Token;
        _ = LatencyLoopAsync(cancel);
    }

    async Task LatencyLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && !_disposed)
        {
            if (await MeasureRttAsync(Connection.Host, Connection.Port) is { } rtt && !cancel.IsCancellationRequested)
            {
                if (_rtt == null) Log.Info($"{Title}: latency {rtt} ms");
                _rtt = rtt;
                UpdateNetworkInfo();
            }
            try { await Task.Delay(Timings.LatencyProbe, cancel); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Time of a TCP handshake with the server (name resolved first, so it is not counted); null if it fails.</summary>
    internal static async Task<int?> MeasureRttAsync(string host, int port)
    {
        try
        {
            using var timeout = new CancellationTokenSource(3000);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);
            using var tcp = new System.Net.Sockets.TcpClient();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await tcp.ConnectAsync(addresses, port, timeout.Token);
            return Math.Max(1, (int)clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    void UpdateNetworkInfo()
    {
        if (_rtt is not { } rtt) return;
        Quality = _serverQuality ?? (rtt < 60 ? 4 : rtt < 120 ? 3 : rtt < 250 ? 2 : 1);
        NetworkInfo = _bandwidth is { } bandwidth
            ? Loc.Format("NetworkInfo", rtt, bandwidth >= 1000 ? $"{bandwidth / 1000.0:0.#} Mb/s" : $"{bandwidth} kb/s")
            : Loc.Format("NetworkLatency", rtt);
    }

    /// <summary>Called by the window once it left full screen: the control's own flag (set by its "Go") goes off.</summary>
    public void LeftFullScreen()
    {
        if (_disposed || _rdp.GetOcx() == null) return;
        try
        {
            if ((bool)_rdp.Ocx.FullScreen) _rdp.Ocx.FullScreen = false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
    }
}
