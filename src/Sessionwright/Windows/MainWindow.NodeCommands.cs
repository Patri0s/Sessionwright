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

// A connection's or a group's menu and what its items do. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Node commands ----------

    void NodeMenu_Opened(object sender, RoutedEventArgs e)
    {
        var n = Selected;
        var marked = Marked.Count;
        var many = marked > 1; // a multiple selection: only what works on all of it
        var menu = (ContextMenu)sender;
        // Once each: every one walks the marked groups' content, and the items ask for them more than once.
        var (scripts, vmsOff, vmsOn, wake, power) = (ScriptTargets(), VmTargets(running: false), VmTargets(running: true), WakeTargets(), PowerTargets());
        foreach (var item in menu.Items.OfType<MenuItem>()) Fit(item);

        void Fit(MenuItem item)
        {
            // A submenu shows while something in it does.
            foreach (var child in item.Tag is "machine" ? item.Items.OfType<MenuItem>() : []) Fit(child);
            var show = item.Tag switch
            {
                "machine" => item.Items.OfType<MenuItem>().Any(child => child.Visibility == Visibility.Visible),
                // New items belong to a group or the list as a whole: on a connection they were two more rows to read past.
                "new" => !many && n is not Connection,
                "connect" => many || n is Connection,
                // A shared list's items are the team's: connected to, never edited or deleted here.
                "delete" => many ? Marked.All(m => !m.Shared) : n is { Shared: false },
                // Sorts the group in the tree: not offered on a favorite or a search result, where it is not what shows.
                "sort" => !many && n is not Connection && !_favoritesActive,
                "conn" or "fav" => !many && n is Connection,
                "group" => !many && n is Group,
                "sync" => !many && n is Group { AzureSync: not null },
                "node" => !many && n is { Shared: false },
                "shared" => !many && n is Group { Shared: false, SharedList.Length: > 0 },
                "share" => !many && n is Group { Shared: false, SharedList: null },
                "links" => !many && LinksOf(n).Count > 0,
                "scripts" => scripts.Count > 0,
                "vm" => vmsOff.Count > 0,
                "vmstop" => vmsOn.Count > 0,
                "wake" => wake.Count > 0,
                "power" => power.Count > 0,
                "vmpostpone" => !many && n is Connection { VmShutdownAt: not null, VmShutdownSkipped: false, VmShutdownReadOnly: false },
                "vmskip" => !many && n is Connection { VmShutdownAt: not null, VmShutdownReadOnly: false },
                _ => true,
            };
            // The team's items: Edit and Delete show, greyed, so the reason is seen before trying.
            if (item.Tag is "node" or "delete")
            {
                var team = !many && n is { Shared: true };
                show |= team;
                item.IsEnabled = !team;
                item.ToolTip = team ? Loc.Get("SharedMarkTip") : null;
                ToolTipService.SetShowOnDisabled(item, true);
            }
            item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (item.Tag is "fav" && n is Connection c) item.Header = Loc.Get(c.Favorite ? "RemoveFavorite" : "AddFavorite");
            if (item.Tag is "links" && item.Visibility == Visibility.Visible)
            {
                item.Items.Clear();
                foreach (var link in LinkMenu.Items(LinksOf(n), n as Connection)) item.Items.Add(link);
            }
            if (item.Tag is "scripts" && item.Visibility == Visibility.Visible)
            {
                var machines = scripts;
                item.Header = machines.Count > 1 ? Loc.Format("ScriptsMenuMany", machines.Count) : Loc.Get("ScriptsMenu");
                item.Items.Clear();
                foreach (var script in Scripts.MenuItems(this, machines)) item.Items.Add(script);
            }
            if (item.Tag is "connect") item.Header = many ? Loc.Format("ConnectMarked", marked) : Loc.Get("Connect");
            if (item.Tag is "delete")
            {
                item.Header = many ? Loc.Format("DeleteMarked", marked) : Loc.Get("Delete");
                item.InputGestureText = _favoritesActive ? "" : "Del"; // among the favorites Del only takes the star away
            }
            if (item.Tag is "vmskip" && n is Connection sc)
                item.Header = sc.VmShutdownSkipped ? Loc.Get("AzureUnskipShutdown") : Loc.Format("AzureSkipShutdown", sc.VmShutdownWhen);
            if (item.Tag is "wake" && item.Visibility == Visibility.Visible && wake is { Count: var asleep })
                item.Header = asleep > 1 ? Loc.Format("WakeMenuMany", asleep) : Loc.Get("WakeMenu");
            if (item.Tag is "vm" or "vmstop" && (item.Tag is "vmstop" ? vmsOn : vmsOff) is { Count: var vms } && item.Visibility == Visibility.Visible)
                item.Header = vms > 1 ? Loc.Format(item.Tag is "vm" ? "AzureVmStartMany" : "AzureVmStopMany", vms) : Loc.Get(item.Tag is "vm" ? "AzureVmStartMenu" : "AzureVmStopMenu");
        }
        // No separator at the edges or next to another one once items are hidden.
        Control? last = null;
        foreach (var item in menu.Items.OfType<Control>())
        {
            if (item is Separator s) s.Visibility = last is MenuItem ? Visibility.Visible : Visibility.Collapsed;
            if (item.Visibility == Visibility.Visible) last = item;
        }
        if (last is Separator trailing) trailing.Visibility = Visibility.Collapsed;
    }

    List<ResolvedLink> LinksOf(Node? n) => n switch
    {
        Connection c => Store.LinksOf(c),
        Group g => Store.LinksOf(g),
        _ => [],
    };

    void Menu_Connect(object sender, RoutedEventArgs e)
    {
        if (Marked.Count > 1) ConnectMarked();
        else if (Selected is Connection c) Connect(c);
    }

    void Menu_ConnectAs(object sender, RoutedEventArgs e)
    {
        if (Selected is Connection c) Connect(c, askCredentials: true);
    }

    void Menu_ConnectAll(object sender, RoutedEventArgs e)
    {
        if (Selected is not Group g) return;
        var all = Descendants(g).OfType<Connection>().ToList();
        if (all.Count == 0) return;
        if (all.Count > 5 && !Dialog.Confirm(this, Loc.Format("ConfirmConnectAll", all.Count, g.Name), Loc.Get("ConnectAll"))) return;
        foreach (var c in all) Connect(c);
    }

    /// <summary>The machines a script from the menu runs on: the marked connections, a group's, or the selected one.</summary>
    List<Connection> ScriptTargets()
    {
        IEnumerable<Node> nodes = Marked.Count > 1 ? MarkedTops() : Selected is { } n ? [n] : [];
        return [.. nodes.SelectMany(n => n is Group g ? Descendants(g).OfType<Connection>() : n is Connection c ? [c] : []).Where(Scripts.CanRun).Distinct()];
    }

    static IEnumerable<Node> Descendants(Group g) => g.Children.SelectMany(c => c is Group sub ? Descendants(sub).Prepend(c) : [c]);
}
