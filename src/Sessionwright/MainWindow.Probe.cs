using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MenuItem = System.Windows.Controls.MenuItem;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace Sessionwright;

// Which servers answer on their RDP port. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Host availability ----------

    /// <summary>
    /// TCP test of the RDP port, at most 16 at a time. Runs from the UI thread, so results are set there. Hosts behind an
    /// RD Gateway are not probed: they are usually not reachable directly, and would always look offline.
    /// </summary>
    async Task ProbeAsync(IEnumerable<Connection>? only = null)
    {
        _ = RefreshVmStatesAsync(only);
        if (!Store.Data.Settings.ProbeHosts) return;
        if (only == null && _probing)
        {
            _probeAgain = true; // new hosts (import) or cleared results (setting toggled): once this run is done
            return;
        }
        _probing |= only == null;
        if (only == null)
        {
            _lastProbe = DateTime.Now;
            RefreshProbeButton.IsEnabled = false;
        }
        try
        {
            using var gate = new SemaphoreSlim(16);
            await Task.WhenAll((only ?? Store.Data.Connections).ToList().Select(async c =>
            {
                if (!string.IsNullOrWhiteSpace(Store.Resolve(c).Options.Gateway))
                {
                    c.Online = null;
                    return;
                }
                await gate.WaitAsync();
                try
                {
                    var online = await Reachable(c.Host, c.Port);
                    if (Store.Data.Settings.ProbeHosts) c.Online = online;
                }
                finally { gate.Release(); }
            }));
        }
        finally
        {
            if (only == null)
            {
                _probing = false;
                RefreshProbeButton.IsEnabled = true;
                if (_probeAgain)
                {
                    _probeAgain = false;
                    _ = ProbeAsync();
                }
            }
        }
    }

    /// <summary>Power states of the linked Azure VMs (one call per subscription); unknown when Azure cannot be asked.</summary>
    async Task RefreshVmStatesAsync(IEnumerable<Connection>? only)
    {
        var linked = (only ?? Store.Data.Connections).Where(c => c.AzureVm != null).ToList();
        if (linked.Count == 0 || only == null && _vmRefreshing) return;
        _vmRefreshing |= only == null;
        try
        {
            var states = await AzureVms.StatesAsync(linked.Select(c => c.AzureVm!).ToList());
            var cleared = false;
            foreach (var c in linked)
            {
                if (c.AzureVm == null || !states.TryGetValue(c.AzureVm, out var state)) continue;
                c.VmState = state;
                // A machine that is off has no sessions left on it.
                if (state is not (VmPowerState.Stopped or VmPowerState.Deallocated) || !c.HasLeftRunning) continue;
                c.LeftRunningAt = null;
                cleared = true;
            }
            if (cleared) Refresh(tree: false); // saved, and gone from the "left running" list and counter
            await AzureShutdown.RefreshAsync(linked);
        }
        finally
        {
            if (only == null) _vmRefreshing = false;
        }
    }

    static async Task<bool> Reachable(string host, int port)
    {
        using var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
