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

// Where the window was, and putting it back there. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Window placement ----------

    void RestorePlacement()
    {
        SidebarColumn.Width = new GridLength(Math.Clamp(Store.Data.Settings.SidebarWidth, SidebarColumn.MinWidth, SidebarColumn.MaxWidth));
        SourceInitialized += (_, _) =>
        {
            Native.Subclass(this, (_, msg, wParam, _) => // exact bounds across monitors with another scale; the quick-connect key
            {
                if (!SessionKeys.IsQuickConnectMessage(msg, wParam)) return null;
                ShowQuickConnect();
                return IntPtr.Zero;
            });
            SessionKeys.SetQuickConnect(this, Store.Data.Settings.QuickConnectKey);
            AzureVms.MainWindowHandle = Native.Handle(this);
        };
        SessionKeys.QuickConnectPressed += ShowQuickConnect;
        if (Store.Data.Settings.MainPlacement is not { } p || !Native.IsOnScreen(p)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        Native.SetPlacement(this, p, show: false);
        if (!p.Maximized) return;
        // Maximized before it was ever shown, WPF laid it out at the restored size on every such start (the content
        // filled part of the window until it was activated). Maximized once shown instead, hidden until drawn so.
        Native.Cloak(this, true);
        RoutedEventHandler? maximize = null;
        maximize = (_, _) =>
        {
            Loaded -= maximize;
            WindowState = WindowState.Maximized;
            Dispatcher.BeginInvoke(() => Native.Cloak(this, false), DispatcherPriority.Background);
        };
        Loaded += maximize;
    }

    void SavePlacement()
    {
        if (!IsLoaded) return;
        Store.Data.Settings.MainPlacement = Native.GetPlacement(this);
        Store.Data.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        Save();
    }

    void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        Store.Data.Settings.SidebarWidth = SidebarColumn.ActualWidth;
}
