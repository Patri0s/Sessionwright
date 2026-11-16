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

// The connection tree: selection, marking several, dragging, keys. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Tree ----------

    void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext is Connection c)
        {
            e.Handled = true;
            Connect(c);
        }
    }

    void Tree_KeyDown(object sender, KeyEventArgs e) => NodeKeyDown(e);

    // Moving on with the keyboard leaves the marks behind: Enter and Delete would still act on them, not on the row
    // the user went to.
    // Marking without the mouse, as Shift+click does it: Shift+Up/Down marks from the selected row (which stays put, the
    // anchor) to where the keys have got to; Ctrl+A marks every row shown.
    void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            foreach (var n in VisibleNodes()) n.IsMarked = true;
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.Shift && Tree.SelectedItem is Node anchor)
        {
            var visible = VisibleNodes().ToList();
            var from = visible.IndexOf(anchor);
            if (from < 0) return;
            var at = _markCursor != null && visible.IndexOf(_markCursor) is >= 0 and var cursor ? cursor : from;
            var to = Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, visible.Count - 1);
            ClearMarks();
            foreach (var x in visible.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1)) x.IsMarked = true;
            _markCursor = visible[to];
            ContainerOf(Tree, _markCursor)?.BringIntoView();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) ClearMarks();
    }

    Node? _markCursor; // the far end of a Shift+arrow marking

    // WPF-UI lights a row while the mouse is anywhere in its item, children included: a group lit up together with the
    // connection under the mouse. The row's own border decides instead (a local value overrides the template triggers).
    void TreeItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Template?.FindName("Border", item) is not System.Windows.Controls.Border row) return;
        if (System.Windows.Data.BindingOperations.IsDataBound(row, System.Windows.Controls.Border.BackgroundProperty)) return;
        row.SetBinding(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.Data.MultiBinding
        {
            Converter = RowBackground.Instance,
            Bindings =
            {
                new System.Windows.Data.Binding(nameof(TreeViewItem.IsSelected)) { Source = item },
                new System.Windows.Data.Binding(nameof(IsMouseOver)) { Source = row },
                new System.Windows.Data.Binding(nameof(Node.IsMarked)) { Source = item.DataContext },
            },
        });
    }

    sealed class RowBackground : System.Windows.Data.IMultiValueConverter
    {
        public static readonly RowBackground Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            values[0] is true || values[2] is true ? Application.Current.FindResource("TreeViewItemBackgroundSelected")
            : values[1] is true ? Application.Current.FindResource("TreeViewItemBackgroundPointerOver")
            : Brushes.Transparent;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    void NodeKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Marked.Count > 1: ConnectMarked(); break;
            case Key.Enter when Selected is Connection c: Connect(c); break;
            case Key.F2 when Selected is { } n: Edit(n); break;
            case Key.Delete when Marked.Count > 1: DeleteMarked(); break;
            case Key.Delete when Selected is { } n: Delete(n); break;
            case Key.Escape when Marked.Count > 0: ClearMarks(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        LeaveFavorites();
        // On a marked node the menu acts on all marked ones; elsewhere the marks go.
        if (FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext is Node { IsMarked: false }) ClearMarks();
        if (FindAncestor<TreeViewItem>(e.OriginalSource) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
        // Empty space: drop the old selection, otherwise Edit/Delete in the menu would act on an item that was not clicked.
        else if (Tree.SelectedItem is { } old && ContainerOf(Tree, old) is { } current) current.IsSelected = false;
    }

    // The node pressed on: a fast move can be over the next row by the time it counts as a drag.
    Node? _dragNode;

    void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        var item = FindAncestor<TreeViewItem>(e.OriginalSource);
        _dragNode = item?.DataContext as Node;
        if (_dragNode is not { } n || FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource) != null) return; // expander arrow
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift) && Tree.SelectedItem is Node anchor)
        {
            // Shift: everything visible between the selected node and this one.
            var visible = VisibleNodes().ToList();
            int from = visible.IndexOf(anchor), to = visible.IndexOf(n);
            if (from < 0 || to < 0) return;
            foreach (var x in visible) x.IsMarked = false;
            foreach (var x in visible.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1)) x.IsMarked = true;
            e.Handled = true; // the selection (the anchor) stays where it is
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl: this node in or out; the selected one joins the first time.
            if (Marked.Count == 0 && Tree.SelectedItem is Node selected && selected != n) selected.IsMarked = true;
            n.IsMarked = !n.IsMarked;
            if (!n.IsMarked && item!.IsSelected) item.IsSelected = false;
            e.Handled = true;
            if (n.IsMarked) _dragNode = n;
        }
        else if (!n.IsMarked) ClearMarks(); // a plain click on an unmarked node starts over; on a marked one it may drag them all
    }

    // A plain click on a marked node that did not become a drag: like Explorer, only that node stays selected.
    void Tree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragNode is { IsMarked: true } && Keyboard.Modifiers == ModifierKeys.None) ClearMarks();
    }

    /// <summary>Marked nodes in tree order.</summary>
    List<Node> Marked => [.. AllNodes().Where(n => n.IsMarked)];

    IEnumerable<Node> AllNodes() => Flatten(Store.Roots, onlyExpanded: false);
    IEnumerable<Node> VisibleNodes() => Flatten(Store.Roots, onlyExpanded: true);

    static IEnumerable<Node> Flatten(IEnumerable<Node> nodes, bool onlyExpanded)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (n is Group g && (g.Expanded || !onlyExpanded))
                foreach (var child in Flatten(g.Children, onlyExpanded)) yield return child;
        }
    }

    void ClearMarks()
    {
        _markCursor = null;
        foreach (var n in Store.Data.Groups.Cast<Node>().Concat(Store.Data.Connections)) n.IsMarked = false;
    }

    // Marked groups count with their content: the connections in them, and no separate entry for marked nodes inside.
    List<Node> MarkedTops()
    {
        var marked = Marked;
        return [.. marked.Where(n => !Store.Ancestors(n).Any(a => a.IsMarked))];
    }

    void ConnectMarked()
    {
        var all = MarkedTops().SelectMany(n => n is Group g ? Descendants(g).OfType<Connection>() : n is Connection c ? [c] : []).Distinct().ToList();
        if (all.Count == 0) return;
        if (all.Count > 5 && !Dialog.Confirm(this, Loc.Format("ConfirmConnectMarked", all.Count), Loc.Get("Connect"))) return;
        foreach (var c in all) Connect(c);
    }

    void DeleteMarked()
    {
        var tops = MarkedTops();
        if (IsSharedItem([.. tops])) return;
        var count = tops.Sum(n => n is Group g ? 1 + Descendants(g).Count() : 1);
        if (!Dialog.Confirm(this, Loc.Format("ConfirmDeleteMarked", count), Loc.Get("Delete"), DialogKind.Warning, danger: true)) return;
        var removed = tops.SelectMany(WithContent).Distinct().ToList();
        foreach (var n in tops) Store.Delete(n);
        Refresh();
        OfferUndo(removed, Loc.Format("DeletedMany", removed.Count));
    }

    static IEnumerable<Node> WithContent(Node n) => n is Group g ? Descendants(g).Prepend(g) : [n];

    (Notification Note, Action Undo)? _undo;

    /// <summary>
    /// After a delete: for a while a notification (and Ctrl+Z) brings back what went, as it was. The data file's one
    /// backup copy is replaced by the very next save, so there was no way back from a wrong Delete.
    /// </summary>
    void OfferUndo(IReadOnlyList<Node> removed, string title)
    {
        _undo?.Note.Close();
        void Undo()
        {
            var ids = removed.Select(n => n.Id).ToHashSet();
            foreach (var n in removed)
            {
                // Its group may be gone meanwhile (deleted after it): back at the top level then.
                if (n.ParentId is { } parent && !ids.Contains(parent) && Store.Data.Groups.All(g => g.Id != parent)) n.ParentId = null;
                if (n is Group g && !Store.Data.Groups.Contains(g)) Store.Data.Groups.Add(g);
                else if (n is Connection c && !Store.Data.Connections.Contains(c)) Store.Data.Connections.Add(c);
            }
            Refresh(tree: false);
            RebuildTree(removed[0]);
        }
        var note = Notification.Show(this, SymbolRegular.Delete24, title, Loc.Get("UndoHint"), TimeSpan.FromSeconds(20), (Loc.Get("Undo"), Undo));
        note.Closed += (_, _) =>
        {
            if (_undo?.Note == note) _undo = null;
        };
        _undo = (note, Undo);
    }

    void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode is not { } n) return;
        var d = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragNode = null;
        DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, n), DragDropEffects.Move);
    }

    void Tree_DragOver(object sender, DragEventArgs e)
    {
        var plan = PlanDrop(e);
        e.Effects = plan != null ? DragDropEffects.Move : DragDropEffects.None;
        ShowDropIndicator(plan);
        // Near the top or bottom edge the list scrolls, so a long tree can be crossed while dragging.
        var y = e.GetPosition(Tree).Y;
        if (FindChild<ScrollViewer>(Tree) is { } viewer)
        {
            if (y < 24) viewer.LineUp();
            else if (y > Tree.ActualHeight - 24) viewer.LineDown();
        }
        e.Handled = true;
    }

    void Tree_DragLeave(object sender, DragEventArgs e) => ShowDropIndicator(null);

    void Tree_Drop(object sender, DragEventArgs e)
    {
        var plan = PlanDrop(e);
        ShowDropIndicator(null);
        if (plan == null || e.Data.GetData(DragFormat) is not Node node) return;
        // A marked node takes the other marked ones along (in tree order), as long as each can go there.
        var moved = node.IsMarked ? MarkedTops().Where(n => Store.CanMove(n, plan.Parent) && n != plan.Before).ToList() : [node];
        foreach (var n in moved) Store.Move(n, plan.Parent, plan.Before);
        if (plan.Parent != null) plan.Parent.Expanded = true;
        Save();
        Refresh(save: false, tree: false);
        RebuildTree(node);
    }

    enum DropMode { Before, After, Into }

    /// <summary>Where a drop lands: the group (null = top level), the sibling it goes before, and what to show.</summary>
    sealed record DropPlan(Group? Parent, Node? Before, TreeViewItem? Item, DropMode Mode);

    /// <summary>
    /// The upper / lower half of a connection places the dragged connection before / after it. A group takes what is
    /// dropped on its middle; a dragged group goes before / after it near its top / bottom edge (groups stay above the
    /// connections, so a group dropped on a connection goes after the groups there). Empty space: end of the top level.
    /// </summary>
    DropPlan? PlanDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not Node node) return null;
        var item = FindAncestor<TreeViewItem>(e.OriginalSource);
        DropPlan plan;
        if (item?.DataContext is not Node over) plan = new(null, null, null, DropMode.Into);
        else if (over == node) return null;
        else
        {
            var row = RowOf(item);
            var y = e.GetPosition(row).Y / Math.Max(1, row.ActualHeight);
            if (over is Group g)
                plan = node is Group && y < 0.25 ? new(Store.Parent(g), g, item, DropMode.Before)
                    : node is Group && y > 0.75 && !(g.Expanded && g.Children.Count > 0) ? new(Store.Parent(g), NextSibling(g), item, DropMode.After)
                    : new(g, null, item, DropMode.Into);
            else if (node is Group) plan = new(Store.Parent(over), null, null, DropMode.Into);
            else plan = y < 0.5 ? new(Store.Parent(over), over, item, DropMode.Before) : new(Store.Parent(over), NextSibling(over), item, DropMode.After);
        }
        return Store.CanMove(node, plan.Parent) ? plan : null;
    }

    Node? NextSibling(Node n)
    {
        var siblings = (Store.Parent(n)?.Children ?? Store.Roots).Where(s => s.GetType() == n.GetType()).ToList();
        return siblings.ElementAtOrDefault(siblings.IndexOf(n) + 1);
    }

    static FrameworkElement RowOf(TreeViewItem item) => item.Template?.FindName("Border", item) as FrameworkElement ?? item;

    DropAdorner? _dropAdorner;

    void ShowDropIndicator(DropPlan? plan)
    {
        var row = plan?.Item is { } item ? RowOf(item) : null;
        if (_dropAdorner is { } current && (current.AdornedElement != row || current.Mode != plan?.Mode))
        {
            AdornerLayer.GetAdornerLayer(current.AdornedElement)?.Remove(current);
            _dropAdorner = null;
        }
        if (row == null || _dropAdorner != null || AdornerLayer.GetAdornerLayer(row) is not { } layer) return;
        _dropAdorner = new DropAdorner(row, plan!.Mode);
        layer.Add(_dropAdorner);
    }

    /// <summary>A line above / below the row, or a frame around the group that takes the drop.</summary>
    sealed class DropAdorner(UIElement row, DropMode mode) : Adorner(row)
    {
        public DropMode Mode { get; } = mode;

        protected override void OnRender(DrawingContext dc)
        {
            var brush = (Brush)Application.Current.FindResource("AccentTextFillColorPrimaryBrush");
            var size = AdornedElement.RenderSize;
            if (Mode == DropMode.Into)
                dc.DrawRoundedRectangle(null, new Pen(brush, 2), new Rect(1, 1, size.Width - 2, size.Height - 2), 4, 4);
            else
                dc.DrawRoundedRectangle(brush, null, new Rect(4, Mode == DropMode.Before ? -1 : size.Height - 1, size.Width - 8, 2), 1, 1);
        }
    }
}
