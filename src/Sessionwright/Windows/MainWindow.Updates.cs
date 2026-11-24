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

// Looking for a new release and saying so. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Updates ----------

    async Task CheckForUpdatesAsync()
    {
        if (Updater.Feed is { } feed && !Updater.IsTrusted(feed)) Log.Warn("Update: this build's feed is plain http and is not used.");
        if (!Updater.IsEnabled || _update != null) return;
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (await Updater.CheckAsync() is not { } update || !await Updater.DownloadAsync(update)) return;
        _update = update;
        UpdateBar.Message = Loc.Format("UpdateAvailable", update.TargetFullRelease.Version);
        UpdateBar.IsOpen = true;
    }

    async void UpdateRestart_Click(object sender, RoutedEventArgs e)
    {
        // Checked before anything is closed: the feed may have been removed in Settings meanwhile.
        if (_update == null || !Updater.IsEnabled)
        {
            UpdateBar.IsOpen = false;
            _update = null;
            return;
        }
        var open = Sessions.Open.Count(s => s.IsOpen) + Sessions.Terminals.Count;
        if (open > 0 && !Dialog.Confirm(this, Loc.Format("ConfirmExit", open), Loc.Get("UpdateRestart"), DialogKind.Warning)) return;
        if (!RemotePower.MayDropPending(this)) return;
        App.IsExiting = true;
        Hide();
        var updating = UpdatingWindow.ShowFor(_update.TargetFullRelease.Version.ToString());
        await Sessions.CloseAllAsync();
        SavePlacement();
        if (Updater.ApplyAndRestart(_update.TargetFullRelease))
        {
            updating.HoldUntilRead();
            Application.Current.Shutdown();
            return;
        }
        // Never a hidden process holding the single-instance lock: the manager comes back, the sessions are gone.
        updating.Close();
        App.IsExiting = false;
        Show();
        Native.Restore(this);
        Dialog.Show(this, Loc.Format("UpdateFailed", Log.File), DialogKind.Error);
    }
}
