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

// A team's shared lists shown under the user's groups. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Shared lists ----------

    void Menu_RefreshShared(object sender, RoutedEventArgs e)
    {
        if (Selected is Group { SharedList.Length: > 0 } group) _ = RefreshSharedAsync(group, manual: true);
    }

    /// <summary>At start the lists show from their copies on this PC; a moment later each is read again from its source.</summary>
    async Task RefreshSharedListsAsync()
    {
        var roots = Store.Data.Groups.Where(g => !g.Shared && !string.IsNullOrWhiteSpace(g.SharedList)).ToList();
        if (roots.Count == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(3));
        foreach (var root in roots) await RefreshSharedAsync(root, manual: false);
    }

    async Task RefreshSharedAsync(Group root, bool manual)
    {
        SharedChanges? changes;
        try { changes = await SharedLists.RefreshAsync(Store, root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or ArgumentException or NotSupportedException)
        {
            // Off the network or the share: the copy on this PC keeps showing.
            Log.Info($"Shared list of {root.Name}: not refreshed: {ex.Message}");
            if (manual) Notification.Problem(this, Loc.Format("SharedListFailedTitle", root.Name), Loc.Format("SharedListFailed", root.Name, ex.Message), warning: true);
            return;
        }
        if (!Store.Data.Groups.Contains(root) || App.IsExiting) return;
        var count = SharedLists.ItemsOf(Store, root).OfType<Connection>().Count();
        Log.Info($"Shared list of {root.Name}: {count} connections");
        Refresh();
        // The team's lead changed the list: said, so a server that moved or went is no surprise.
        var said = changes is { Any: true } ? Loc.Format("SharedListChanged", changes.Added, changes.Changed, changes.Removed) : null;
        if (said != null) Log.Info($"Shared list of {root.Name}: {changes!.Added} added, {changes.Changed} changed, {changes.Removed} removed");
        if (manual) Notification.Show(this, SymbolRegular.PeopleTeam24, Loc.Get("SharedListTitle"),
            Loc.Format("SharedListDone", root.Name, count) + (said != null ? "\n" + said : ""), TimeSpan.FromSeconds(12));
        else if (said != null) Notification.Show(this, SymbolRegular.PeopleTeam24, Loc.Format("SharedListChangedTitle", root.Name), said, TimeSpan.FromSeconds(15));
    }

    void Menu_ExportShared(object sender, RoutedEventArgs e)
    {
        if (Selected is not Group { Shared: false } group) return;
        if (!MasterPassword.TryOpenNotes(Store.Data)) return; // the team's file takes the notes in plain, never sealed
        var dialog = new SaveFileDialog { Filter = Loc.Get("ExportFilter"), FileName = $"{string.Concat(group.Name.Split(Path.GetInvalidFileNameChars()))}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, Importer.ExportShared(Store, group));
            Dialog.Show(this, Loc.Format("SharedListExported", dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialog.Show(this, Loc.Format("SaveFailed", ex.Message), DialogKind.Error);
        }
    }

    void Delete(Node n)
    {
        if (IsSharedItem(n)) return;
        var text = n is Group g
            ? Loc.Format("ConfirmDeleteGroup", g.Name, Descendants(g).Count())
            : Loc.Format("ConfirmDelete", n is Connection c ? c.Title : n.Name);
        if (!Dialog.Confirm(this, text, Loc.Get("Delete"), DialogKind.Warning, danger: true)) return;
        var removed = WithContent(n).ToList(); // before it is deleted: a group's content is read from the tree
        var deleted = Loc.Format("DeletedOne", n is Connection gone ? gone.Title : n.Name);
        if (Results.IsVisible)
        {
            // Same in the search results: the keyboard stays on the row that took the deleted one's place.
            var row = Results.SelectedIndex;
            Store.Delete(n);
            Refresh();
            Results.SelectedIndex = Math.Min(row, SearchResults.Count - 1);
            Dispatcher.BeginInvoke(() => FocusListItem(Results), DispatcherPriority.Loaded);
            OfferUndo(removed, deleted);
            return;
        }
        // Keep the keyboard in the tree on a neighbour, so several items can be deleted in a row.
        var siblings = (Store.Parent(n)?.Children ?? Store.Roots).ToList();
        var index = siblings.IndexOf(n);
        var next = siblings.ElementAtOrDefault(index + 1) ?? siblings.ElementAtOrDefault(index - 1) ?? Store.Parent(n);
        Store.Delete(n);
        Refresh(tree: false);
        RebuildTree(next);
        OfferUndo(removed, deleted);
    }
}
