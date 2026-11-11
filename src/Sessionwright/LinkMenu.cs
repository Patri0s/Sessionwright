using System.Diagnostics;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Sessionwright;

/// <summary>
/// Opening links: in the browser of this PC, or in the session's own on the server (typed into its Run box). One menu
/// for the tree, the session window and the full-screen bar.
/// </summary>
static class LinkMenu
{
    public static void OpenHere(string url)
    {
        if (!Links.IsSafe(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, $"Opening {Links.WithoutSecrets(url)}");
            Notification.Problem(null, Loc.Get("LinkNotOpenedTitle"), ex.Message);
        }
    }

    /// <summary>
    /// The menu's items: the links to open on this PC and, for a connection, the same in its session on the server
    /// (connecting first when it is not open). A group's open on this PC only: its session is any of its connections'.
    /// </summary>
    public static List<Control> Items(IReadOnlyList<ResolvedLink> links, Connection? c)
    {
        var items = new List<Control>();
        if (c != null) items.Add(Header(Loc.Get("LinksHere")));
        foreach (var link in links) items.Add(Item(link, SymbolRegular.Globe24, () => OpenHere(link.Url)));
        if (c == null) return items;
        items.Add(new Separator());
        items.Add(Header(Loc.Get("LinksInSession")));
        foreach (var link in links) items.Add(Item(link, SymbolRegular.Desktop24, () => _ = App.Sessions.OpenLinkInSessionAsync(c, link.Url)));
        return items;
    }

    static MenuItem Item(ResolvedLink link, SymbolRegular icon, Action open)
    {
        var item = new MenuItem
        {
            Header = link.Name, Icon = new SymbolIcon { Symbol = icon },
            ToolTip = link.From is { } g ? Loc.Format("LinkFromGroup", link.Url, g.Name) : link.Url,
        };
        item.Click += (_, _) => open();
        return item;
    }

    // A section's caption: not an action, so not clickable.
    static MenuItem Header(string text) => new() { Header = text, IsEnabled = false, FontSize = 12 };

    /// <summary>A context menu with the items, under <paramref name="target"/>; <paramref name="closed"/> once it closes.</summary>
    public static void Show(System.Windows.FrameworkElement target, IReadOnlyList<ResolvedLink> links, Connection? c, Action? closed = null)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in Items(links, c)) menu.Items.Add(item);
        target.IsHitTestVisible = false; // a click on the button while open only closes the menu
        menu.Closed += (_, _) =>
        {
            target.IsHitTestVisible = true;
            closed?.Invoke();
        };
        menu.IsOpen = true;
    }
}
