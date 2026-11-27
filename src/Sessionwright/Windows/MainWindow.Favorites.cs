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

// The favorites list: its rows, its order, what is dragged into it. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Favorites: the tree's rows and menu ----------

    bool _favoritesActive; // the menu and the keys act on the favorite picked, not on the tree's selection

    void List_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => LeaveFavorites();

    // Back in the tree or the search: one selection highlighted, and the menu acts on that one.
    void LeaveFavorites()
    {
        _favoritesActive = false;
        FavoritesList.SelectedItem = null;
    }

    void Favorites_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _favoritesActive = true;
        ClearMarks(); // marks in the tree would make the menu act on them
    }

    void Favorites_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is Connection c) Connect(c);
    }

    bool _favoritesFolding; // the list slides: its visibility is the animation's until it settles

    void FavoritesHeader_Click(object sender, RoutedEventArgs e)
    {
        var fold = Store.Data.Settings.FavoritesCollapsed = !Store.Data.Settings.FavoritesCollapsed;
        Save();
        _favoritesFolding = true;
        FavoritesChevronTurn.BeginAnimation(RotateTransform.AngleProperty, Motion.Animate(null, fold ? -90 : 0, 200));
        Motion.Fold(FavoritesBody, !fold, () => _favoritesFolding = false);
        UpdateChrome();
    }

    // A whole working set in one go (asked first when it is a lot).
    void ConnectAllFavorites_Click(object sender, RoutedEventArgs e)
    {
        var all = Store.Favorites();
        if (all.Count > 5 && !Dialog.Confirm(this, Loc.Format("ConfirmConnectFavorites", all.Count), Loc.Get("Connect"))) return;
        foreach (var c in all) Connect(c);
    }

    /// <summary>Ctrl+1…9: the favorite at that place.</summary>
    void ConnectFavorite(int number)
    {
        var all = Store.Favorites();
        if (number >= 1 && number <= all.Count) Connect(all[number - 1]);
    }

    // The numbers of Ctrl+1…9 show while Ctrl is held, as browsers do for their tabs.
    public static readonly DependencyProperty ShowFavoriteNumbersProperty =
        DependencyProperty.Register(nameof(ShowFavoriteNumbers), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool ShowFavoriteNumbers
    {
        get => (bool)GetValue(ShowFavoriteNumbersProperty);
        set => SetValue(ShowFavoriteNumbersProperty, value);
    }

    void UpdateFavoriteNumbers() => ShowFavoriteNumbers = IsActive && Keyboard.Modifiers == ModifierKeys.Control;

    // ---------- Favorites: their own order by dragging, and connections dragged in from the tree ----------

    const string FavoriteDragFormat = "Sessionwright.Favorite"; // not the tree's: dropped on the tree it must not move the connection
    Point _favoriteDragStart;
    Connection? _favoriteDragged;
    DropAdorner? _favoriteDrop;

    void Favorites_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _favoriteDragStart = e.GetPosition(FavoritesList);
        _favoriteDragged = FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext as Connection;
    }

    void Favorites_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _favoriteDragged is not { } c) return;
        var d = e.GetPosition(FavoritesList) - _favoriteDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _favoriteDragged = null;
        DragDrop.DoDragDrop(FavoritesList, new DataObject(FavoriteDragFormat, c), DragDropEffects.Move);
        ShowFavoriteDrop(null, DropMode.Before);
    }

    static Connection? DraggedConnection(DragEventArgs e) =>
        e.Data.GetData(FavoriteDragFormat) as Connection ?? e.Data.GetData(DragFormat) as Connection;

    /// <summary>Where a drop lands among the favorites (0 = first), and the row to mark it on.</summary>
    (int Index, ListBoxItem? Row, DropMode Mode) FavoriteDropAt(DragEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource) is not { DataContext: Connection over } row)
            return (Favorites.Count, Favorites.Count > 0 ? FavoritesList.ItemContainerGenerator.ContainerFromIndex(Favorites.Count - 1) as ListBoxItem : null, DropMode.After);
        var after = e.GetPosition(row).Y > row.ActualHeight / 2;
        return (Favorites.IndexOf(over) + (after ? 1 : 0), row, after ? DropMode.After : DropMode.Before);
    }

    void Favorites_DragOver(object sender, DragEventArgs e)
    {
        var c = DraggedConnection(e);
        e.Effects = c != null ? DragDropEffects.Move : DragDropEffects.None;
        var (_, row, mode) = FavoriteDropAt(e);
        ShowFavoriteDrop(c != null ? row : null, mode);
        e.Handled = true;
    }

    void Favorites_DragLeave(object sender, DragEventArgs e) => ShowFavoriteDrop(null, DropMode.Before);

    void Favorites_Drop(object sender, DragEventArgs e)
    {
        ShowFavoriteDrop(null, DropMode.Before);
        if (DraggedConnection(e) is not { } c) return;
        var (index, _, _) = FavoriteDropAt(e);
        var current = Favorites.IndexOf(c);
        Store.MoveFavorite(c, current >= 0 && current < index ? index - 1 : index); // counted without itself
        Refresh(tree: false);
        FavoritesList.SelectedItem = c;
        e.Handled = true;
    }

    void ShowFavoriteDrop(ListBoxItem? row, DropMode mode)
    {
        if (_favoriteDrop is { } current && (current.AdornedElement != row || current.Mode != mode))
        {
            AdornerLayer.GetAdornerLayer(current.AdornedElement)?.Remove(current);
            _favoriteDrop = null;
        }
        if (row == null || _favoriteDrop != null || AdornerLayer.GetAdornerLayer(row) is not { } layer) return;
        _favoriteDrop = new DropAdorner(row, mode);
        layer.Add(_favoriteDrop);
    }

    void Favorites_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource) is not { } item) return;
        item.IsSelected = true;
        item.Focus();
    }

    void Favorites_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && FavoritesList.SelectedItem is Connection c)
        {
            Store.SetFavorite(c, false);
            Refresh(tree: false);
            e.Handled = true;
        }
        else NodeKeyDown(e);
    }

    void Menu_Favorite(object sender, RoutedEventArgs e)
    {
        if (Selected is not Connection c) return;
        Store.SetFavorite(c, !c.Favorite); // the tree shows the star through binding
        Refresh(tree: false);
    }

    void Menu_Edit(object sender, RoutedEventArgs e)
    {
        if (Selected is { } n) Edit(n);
    }

    /// <summary>
    /// Connections whose Azure VM the menu would start (off or unknown) or shut down (running or starting): the marked ones,
    /// a group's, or the selected one. One per VM, as several connections may lead to the same machine.
    /// </summary>
    List<Connection> VmTargets(bool running)
    {
        IEnumerable<Node> nodes = Marked.Count > 1 ? MarkedTops() : Selected is { } n ? [n] : [];
        return [.. nodes.SelectMany(n => n is Group g ? Descendants(g).OfType<Connection>() : n is Connection c ? [c] : [])
            .Where(c => c.AzureVm != null && (c.VmState is VmPowerState.Running or VmPowerState.Starting) == running)
            .DistinctBy(c => (c.AzureVm!.SubscriptionId.ToLowerInvariant(), c.AzureVm.ResourceGroup.ToLowerInvariant(), c.AzureVm.Name.ToLowerInvariant()))];
    }

    /// <summary>
    /// Machines the menu would wake (Wake-on-LAN): not in Azure, with a MAC address, not answering now; the marked
    /// connections, a group's, or the selected one, each machine once.
    /// </summary>
    List<Connection> WakeTargets()
    {
        IEnumerable<Node> nodes = Marked.Count > 1 ? MarkedTops() : Selected is { } n ? [n] : [];
        return [.. nodes.SelectMany(n => n is Group g ? Descendants(g).OfType<Connection>() : n is Connection c ? [c] : [])
            .Where(CanWake).DistinctBy(c => c.Mac, StringComparer.OrdinalIgnoreCase)];
    }

    static bool CanWake(Connection c) => c.Online != true && SessionManager.WakeMac(c) != null;

    void Menu_Wake(object sender, RoutedEventArgs e) => _ = Machines.WakeAsync(this, WakeTargets());

    /// <summary>The machines a restart / shut down acts on: the marked connections, a group's, or the selected one; each machine once.</summary>
    List<Connection> PowerTargets()
    {
        IEnumerable<Node> nodes = Marked.Count > 1 ? MarkedTops() : Selected is { } n ? [n] : [];
        return [.. nodes.SelectMany(n => n is Group g ? Descendants(g).OfType<Connection>() : n is Connection c ? [c] : [])
            // A terminal's machine outside Azure is not restarted through Windows' own way of doing it.
            .Where(c => c.Protocol == ConnectionProtocol.Rdp || c.AzureVm != null)
            .DistinctBy(c => c.AzureVm is { } vm ? $"vm:{vm.SubscriptionId}/{vm.ResourceGroup}/{vm.Name}".ToLowerInvariant() : $"host:{c.Host}".ToLowerInvariant())];
    }

    void Menu_Power(object sender, RoutedEventArgs e) => _ = Machines.PowerAsync(this, PowerTargets());

    void Menu_PostponeShutdown(object sender, RoutedEventArgs e)
    {
        if (Selected is Connection c) _ = AzureShutdown.WithFeedbackAsync(this, c, () => AzureShutdown.PostponeAsync(c, 1));
    }

    void Menu_SkipShutdown(object sender, RoutedEventArgs e)
    {
        if (Selected is Connection c)
            _ = AzureShutdown.WithFeedbackAsync(this, c, () => c.VmShutdownSkipped ? AzureShutdown.ResetAsync(c) : AzureShutdown.SkipAsync(c));
    }

    void Menu_StartVm(object sender, RoutedEventArgs e) => _ = Machines.StartAsync(this, VmTargets(running: false));

    void Menu_StopVm(object sender, RoutedEventArgs e) => _ = Machines.StopAsync(this, VmTargets(running: true));

    void Menu_SignedIn(object sender, RoutedEventArgs e)
    {
        if (Selected is Connection c) new SignedInWindow(c) { Owner = this }.ShowDialog();
    }

    void Menu_Duplicate(object sender, RoutedEventArgs e)
    {
        if (Selected is not Connection c) return;
        if (c.SealedNotes != null) MasterPassword.TryOpenNotes(Store.Data);
        var copy = new Connection
        {
            // A copy of a shared list's connection is the user's own: next to the list, in their group.
            Name = c.Title + Loc.Get("CopySuffix"), Host = c.Host, Port = c.Port, ParentId = c.Shared ? Store.OwnGroup(Store.Parent(c))?.Id : c.ParentId,
            CredentialId = c.CredentialId, Rdp = c.Rdp?.Clone(), Notes = c.Notes, Links = c.Links?.Select(l => l.Clone()).ToList(), Tags = c.Tags, Mac = c.Mac, Color = c.Color, Protocol = c.Protocol, SshUser = c.SshUser, SshKeyFile = c.SshKeyFile, AzureVm = c.AzureVm?.Clone(), AutoStart = c.AutoStart?.Clone(), ShutDownVmAfter = c.ShutDownVmAfter,
        };
        Store.Data.Connections.Add(copy);
        Refresh(tree: false);
        RebuildTree(copy);
    }

    void Menu_Delete(object sender, RoutedEventArgs e)
    {
        if (Marked.Count > 1) DeleteMarked();
        else if (Selected is { } n) Delete(n);
    }

    void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        var c = new Connection { ParentId = Store.OwnGroup(TargetGroup)?.Id };
        if (new EditorWindow(c, isNew: true) { Owner = this }.ShowDialog() != true) return;
        AddConnection(c);
    }

    /// <summary>A connection made in the editor goes into the tree, selected.</summary>
    public void AddConnection(Connection c)
    {
        Store.Data.Connections.Add(c);
        Refresh(tree: false);
        RebuildTree(c);
        _ = ProbeAsync([c]);
    }

    void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var g = new Group { ParentId = Store.OwnGroup(TargetGroup)?.Id };
        if (new EditorWindow(g, isNew: true) { Owner = this }.ShowDialog() != true) return;
        Store.Data.Groups.Add(g);
        Refresh(tree: false);
        RebuildTree(g);
        if (g.SharedList != null) _ = RefreshSharedAsync(g, manual: true);
    }

    void Edit(Node n)
    {
        if (IsSharedItem(n)) return;
        var list = (n as Group)?.SharedList;
        if (new EditorWindow(n, isNew: false) { Owner = this }.ShowDialog() != true) return;
        if (n is Group group && group.SharedList != list)
        {
            // Another list, or none any more: the old one's items go; the new one's come with the refresh.
            SharedLists.Clear(Store, group);
            SharedLists.DeleteCache(Store, group);
            if (group.SharedList != null) _ = RefreshSharedAsync(group, manual: true);
        }
        Refresh(tree: false);
        RebuildTree(n);
        if (n is Connection c) _ = ProbeAsync([c]);
    }

    /// <summary>Says so, once, when something of a shared list was asked to be changed.</summary>
    bool IsSharedItem(params Node[] nodes)
    {
        if (nodes.FirstOrDefault(n => n.Shared) is not { } shared) return false;
        Dialog.Show(this, Loc.Format("SharedListReadOnly", shared is Connection c ? c.Title : shared.Name, Store.OwnGroup(Store.Parent(shared))?.Name ?? ""));
        return true;
    }
}
