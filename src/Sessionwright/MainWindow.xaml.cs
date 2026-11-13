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

/// <summary>The connection manager: tree, search and quick connect, open sessions (each in its own window), recent.</summary>
public partial class MainWindow : FluentWindow
{
    const string DragFormat = "Sessionwright.Node";
    static readonly IComparer<string> ByName = Store.NaturalOrder;

    public Store Store => App.Store;
    public SessionManager Sessions => App.Sessions;
    public ObservableCollection<Connection> Favorites { get; } = [];
    /// <summary>Connections whose session was left running (disconnected, not signed out), newest first.</summary>
    public ObservableCollection<Connection> LeftRunning { get; } = [];
    /// <summary>Connections and commands matching the search box.</summary>
    public ObservableCollection<object> SearchResults { get; } = [];

    Point _dragStart;
    bool _probing, _probeAgain, _vmRefreshing;
    DateTime _lastProbe;
    static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(2), UnreachableProbeInterval = TimeSpan.FromSeconds(20);
    Velopack.UpdateInfo? _update;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        RestorePlacement();
        Sessions.Open.CollectionChanged += (_, _) =>
        {
            UpdateOpenMarkers();
            UpdateHomeLists();
            UpdateChrome();
        };
        Sessions.DataChanged += tree => Refresh(tree: tree);
        PreviewKeyUp += (_, _) => UpdateFavoriteNumbers();
        Deactivated += (_, _) => ShowFavoriteNumbers = false;
        Machines.Loaded += UpdateSearch;
        Refresh(save: false);

        Loaded += (_, _) =>
        {
            if (!_probing) _ = ProbeAsync(); // activation may have started it already
            _ = CheckForUpdatesAsync();
            _ = SyncAzureGroupsAsync();
            _ = RefreshSharedListsAsync();
        };
        // Every host every 2 minutes; an unreachable one every 20 s, so a server that was just started turns green soon.
        var probeTimer = new DispatcherTimer { Interval = UnreachableProbeInterval };
        probeTimer.Tick += (_, _) =>
        {
            AzureShutdown.CheckWarnings(Sessions); // also while minimized (or in the tray): the sessions are what matters
            _ = AzureAutoStart.CheckAsync(DateTime.Now);
            if (WindowState == WindowState.Minimized) return;
            if (DateTime.Now - _lastProbe >= ProbeInterval)
            {
                _ = ProbeAsync();
                foreach (var c in Store.Data.Connections) c.RefreshAge();
            }
            // Also VMs on their way up or down, so the dot follows them.
            else if (!_probing) _ = ProbeAsync(Store.Data.Connections.Where(c => c.Online == false
                || c.VmState is VmPowerState.Starting or VmPowerState.Stopping or VmPowerState.Deallocating));
        };
        probeTimer.Start();
        // Back in the app after a while (e.g. having started a server elsewhere): check again at once.
        Activated += (_, _) =>
        {
            if (DateTime.Now - _lastProbe > UnreachableProbeInterval) _ = ProbeAsync();
            SyncLayoutSize();
        };
        StateChanged += (_, _) => Dispatcher.BeginInvoke(SyncLayoutSize, DispatcherPriority.Loaded);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Dispatcher.BeginInvoke(SyncLayoutSize, DispatcherPriority.Loaded); };
    }

    // Seen once: the window maximized on the second monitor, its content laid out at the restored 1280 x 800, the rest
    // of the window empty (not reproduced: a start, a background start and the tray all laid it out right). When WPF's
    // size and the window's differ, the window's size is sent again; if that does not take, the window state round-trips.
    void SyncLayoutSize()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || Native.Handle(this) is var hwnd && hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        bool Stale(out double width, out double height)
        {
            GetWindowRect(hwnd, out var r);
            (width, height) = ((r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
            return Math.Abs(width - ActualWidth) > 32 || Math.Abs(height - ActualHeight) > 32;
        }
        if (!Stale(out var w, out var h)) return;
        Log.Warn($"Main window laid out at {ActualWidth:0}x{ActualHeight:0} in a {w:0}x{h:0} window ({WindowState}): size sent again");
        GetClientRect(hwnd, out var client);
        SendMessage(hwnd, 0x0005 /* WM_SIZE */, WindowState == WindowState.Maximized ? 2 /* SIZE_MAXIMIZED */ : 0,
            (client.Bottom << 16) | (client.Right & 0xFFFF));
        UpdateLayout();
        if (!Stale(out _, out _) || WindowState != WindowState.Maximized) return;
        Log.Warn("Main window still laid out at the old size: maximized again");
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    Node? Selected => _favoritesActive ? FavoritesList.SelectedItem as Node : Results.IsVisible ? Results.SelectedItem as Node : Tree.SelectedItem as Node;
    Group? TargetGroup => Selected as Group ?? (Selected is { } n ? Store.Parent(n) : null);

    void Refresh(bool save = true, bool tree = true)
    {
        if (save) Save();
        foreach (var n in Store.Data.Groups.Cast<Node>().Concat(Store.Data.Connections)) n.EffectiveColor = Store.ColorOf(n);
        Sessions.UpdateGroupPaths();
        if (tree) RebuildTree();
        Reset(Favorites, Store.Favorites());
        UpdateOpenMarkers();
        UpdateHomeLists();
        UpdateSearch();
        UpdateChrome();
        Taskbar.UpdateJumpList(Store);
    }

    // Open sessions are listed above, then the ones left running.
    void UpdateHomeLists() =>
        Reset(LeftRunning, Store.Data.Connections.Where(c => c.HasLeftRunning && !c.IsOpen).OrderByDescending(c => c.LeftRunningAt));

    void UpdateOpenMarkers()
    {
        var open = Sessions.Open.Select(s => s.Connection).ToHashSet();
        foreach (var c in Store.Data.Connections) c.IsOpen = open.Contains(c);
    }

    // BuildTree regenerates every TreeViewItem, which drops the selection, keyboard focus and scroll position.
    void RebuildTree(Node? select = null)
    {
        var selected = select ?? Tree.SelectedItem;
        var hadFocus = Tree.IsKeyboardFocusWithin;
        var viewer = FindChild<ScrollViewer>(Tree);
        var offset = viewer?.VerticalOffset ?? 0;
        Store.BuildTree();
        if (select != null) foreach (var g in Store.Ancestors(select)) g.Expanded = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (select == null) viewer?.ScrollToVerticalOffset(offset);
            if (selected == null || ContainerOf(Tree, selected) is not { } item) return;
            item.IsSelected = true;
            if (select != null) item.BringIntoView();
            if (hadFocus || select != null) item.Focus();
        }, DispatcherPriority.Loaded);
    }

    static TreeViewItem? ContainerOf(ItemsControl parent, object node)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem direct) return direct;
        foreach (var child in parent.Items)
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem { IsExpanded: true } sub && ContainerOf(sub, node) is { } found)
                return found;
        return null;
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    void Save()
    {
        try
        {
            Store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving data failed");
            Dialog.Show(this, Loc.Format("SaveFailed", ex.Message), DialogKind.Error);
        }
    }

    static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return; // no flicker of the item containers when nothing changed
        target.Clear();
        foreach (var i in list) target.Add(i);
    }

    void UpdateChrome()
    {
        var searching = SearchBox.Text.Trim().Length > 0;
        var hasConnections = Store.Data.Connections.Count > 0;
        FavoritesPanel.Visibility = Favorites.Count > 0 && !searching ? Visibility.Visible : Visibility.Collapsed;
        var folded = Store.Data.Settings.FavoritesCollapsed;
        if (!_favoritesFolding)
        {
            FavoritesBody.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            FavoritesChevronTurn.Angle = folded ? -90 : 0;
        }
        FavoritesHeaderText.Text = $"{Loc.Get("Favorites")} ({Favorites.Count})";
        System.Windows.Automation.AutomationProperties.SetName(FavoritesHeader,
            $"{FavoritesHeaderText.Text}, {Loc.Get(folded ? "FavoritesExpand" : "FavoritesCollapse")}");
        RefreshProbeButton.Visibility = Store.Data.Settings.ProbeHosts && hasConnections ? Visibility.Visible : Visibility.Collapsed;
        ExpandButtons.Visibility = !searching && Store.Data.Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Tree.Visibility = !searching && Store.Roots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        EmptyTree.Visibility = !searching && Store.Roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TreeHeader.Text = searching ? Loc.Format("ResultsCount", SearchResults.Count + (_moreMatches ? "+" : "")) : Loc.Get("Connections");

        var open = Sessions.Open.Count > 0;
        OpenSessionsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LeftRunningPanel.Visibility = LeftRunning.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The greeting only while no session is open: the space goes to the sessions.
        Welcome.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        HomeContent.VerticalAlignment = open ? VerticalAlignment.Top : VerticalAlignment.Center;
        // Lined up on the left under each other with sessions open; centered under the greeting otherwise.
        foreach (var panel in new[] { OpenSessionsPanel, LeftRunningPanel })
            panel.HorizontalAlignment = open ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        LeftRunningHeader.Text = Loc.Format("LeftRunningSessions", LeftRunning.Count);
        FirstRun.Visibility = hasConnections ? Visibility.Collapsed : Visibility.Visible;
        (WelcomeTitle.Text, WelcomeHint.Text) = (hasConnections, open) switch
        {
            (false, _) => (Loc.Get("FirstRunTitle"), Loc.Get("FirstRunHint")),
            (_, true) => (Loc.Get("WelcomeWindowsTitle"), Loc.Get("WelcomeWindowsHint")),
            _ => (Loc.Get("WelcomeTitle"), Loc.Get("WelcomeHint")),
        };
    }

    // ---------- Connecting ----------

    void Connect(Connection c, bool askCredentials = false) => Sessions.Connect(c, askCredentials);

    /// <summary>Connects a saved connection by id (taskbar jump list, also via a second launch).</summary>
    public void ConnectById(Guid id)
    {
        if (Store.Data.Connections.FirstOrDefault(c => c.Id == id) is { } c) Connect(c);
    }

    /// <summary>
    /// Opens a sessionwright: link: the saved connection it means, else its address after asking, because a link can
    /// come from anywhere and a server the user never added could be one that collects passwords.
    /// </summary>
    public void OpenLink(string link)
    {
        if (SessionLink.Parse(link) is not { } target)
        {
            Log.Info("Link not understood");
            Dialog.Show(this, Loc.Get("SessionLinkInvalid"), DialogKind.Warning);
            return;
        }
        if (SessionLink.Find(Store.Data.Connections, target) is { } saved)
        {
            Connect(saved);
            return;
        }
        if (Dialog.Confirm(this, Loc.Format("SessionLinkUnknown", target.Address), Loc.Get("Connect"), DialogKind.Warning))
            Connect(new Connection { Host = target.Host, Port = target.Port });
    }

    void Menu_CopySessionLink(object sender, RoutedEventArgs e)
    {
        if (Selected is not Connection c) return;
        // The clipboard may be held by another program for a moment.
        try { Clipboard.SetText(SessionLink.For(c)); }
        catch (System.Runtime.InteropServices.COMException ex) { Dialog.Show(this, Loc.Format("ClipboardBusy", ex.Message), DialogKind.Warning); }
    }

    void QuickConnect(string text)
    {
        if (!Store.TryParseAddress(text, out var host, out var port))
        {
            Dialog.Show(this, Loc.Format("InvalidAddress", text), DialogKind.Warning);
            return;
        }
        // An address that is already saved connects with its saved settings and credential.
        var saved = Store.Data.Connections.FirstOrDefault(c => string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase) && c.Port == port);
        Connect(saved ?? new Connection { Host = host, Port = port });
    }

    // The cards on the home page open on a double-click, like the rows of the list: a single click, meant to bring the
    // window forward or just landing there, opened a session. Enter or Space on a card (and a screen reader's "invoke")
    // still open it: those arrive as a Click with no mouse press before it.
    bool _cardPressed;

    void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cardPressed = e.ClickCount != 2;
        if (_cardPressed) return;
        e.Handled = true; // the button takes no press: no Click follows the second release
        OpenCard(sender);
    }

    // Cleared once the Click this release raises has been ignored (also when it raises none: released off the card).
    void Card_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        Dispatcher.BeginInvoke(() => _cardPressed = false, DispatcherPriority.Background);

    void OpenCard(object sender)
    {
        if ((sender as FrameworkElement)?.Tag is Connection c) Connect(c);
        else if ((sender as FrameworkElement)?.Tag is RdpSession s) Sessions.Show(s);
    }

    void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_cardPressed) OpenCard(sender);
    }

    void OpenSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_cardPressed) OpenCard(sender);
    }

    // A click anywhere but on a marked row (and not on a scroll bar) ends a multiple selection, as in Explorer: the home
    // page, the favorites, the toolbar, the empty part of the list. The menu over the marked rows is a window of its own.
    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource) != null && FindAncestor<System.Windows.Controls.TreeView>(e.OriginalSource) == Tree) return; // the tree's own handler
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource) != null) return;
        ClearMarks();
    }

    void OpenSessionMini_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RdpSession s) MiniView.Toggle(s);
    }

    void OpenSessionFullScreen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RdpSession s || Sessions.WindowOf(s) is not { } w) return;
        Sessions.Show(s);
        w.RequestFullScreen(true);
    }

    void OpenSessionDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RdpSession s) Sessions.WindowOf(s)?.Close(); // asks, disconnects, closes
    }

    // While the menu is open the button takes no clicks: a click on it only closes the menu (as a click outside it)
    // instead of the button opening it again right after.
    void MoreMenu_Closed(object sender, RoutedEventArgs e) => MoreButton.IsHitTestVisible = true;

    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu!;
        MoreButton.IsHitTestVisible = false;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void DisconnectAll_Click(object sender, RoutedEventArgs e) => DisconnectAll();

    async void SignOutAll_Click(object sender, RoutedEventArgs e)
    {
        var ready = Sessions.Open.Count(s => s.IsReady);
        if (ready == 0) return;
        if (!Store.Data.Settings.SkipSessionConfirmations
            && !Dialog.Confirm(this, Loc.Format("ConfirmSignOutAll", ready), Loc.Get("SignOutAll"), DialogKind.Warning, danger: true)) return;
        await Sessions.SignOutAllAsync();
    }

    void RefreshProbe_Click(object sender, RoutedEventArgs e) => _ = ProbeAsync();

    void ExpandAll_Click(object sender, RoutedEventArgs e) => ExpandAll(true);
    void CollapseAll_Click(object sender, RoutedEventArgs e) => ExpandAll(false);

    void ExpandAll(bool expanded)
    {
        foreach (var g in Store.Data.Groups) g.Expanded = expanded;
        Save();
    }

    void DisconnectAll()
    {
        var active = Sessions.Open.Count;
        if (active == 0) return;
        if (!Store.Data.Settings.SkipSessionConfirmations && !Dialog.Confirm(this, Loc.Format("ConfirmDisconnectAll", active), Loc.Get("DisconnectAll"), DialogKind.Warning)) return;
        _ = Sessions.CloseAllAsync();
    }

    // ---------- Search / quick connect ----------

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Length > 0) ClearMarks(); // the tree is hidden: its marks would take Enter and Delete from the results
        UpdateSearch();
        // Enter runs the first row: the exact match, quick connect to a full address, the best name match, a command.
        Results.SelectedIndex = SearchResults.Count > 0 ? 0 : -1;
        UpdateChrome();
    }

    void UpdateSearch()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            SearchResults.Clear();
            return;
        }
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Store.Search(q).Take(MaxMatches + 1).ToList();
        _moreMatches = matches.Count > MaxMatches; // the header says so ("50+"), instead of a count that looks complete
        if (_moreMatches) matches.RemoveAt(MaxMatches);
        var commands = Commands().Where(a => tokens.All(t => Palette.Fold(a.Name).Contains(Palette.Fold(t), StringComparison.Ordinal))).Take(5);

        // Typing an address that is not saved must connect to exactly that address: a saved host that merely contains
        // it ("10.0.0.15" for "10.0.0.1") is not it. Quick connect is a row of its own, first for a full address.
        var exact = matches.FirstOrDefault(c => string.Equals(c.Host, q, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(c.Address, q, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(c.Title, q, StringComparison.CurrentCultureIgnoreCase));
        var looksLikeAddress = q.Contains('.') || q.Contains(':');
        // Not for a command word: "stop" offered "Connect to stop".
        SearchAction? quick = exact == null && !q.Contains(' ') && Palette.Parse(q) == null && Store.TryParseAddress(q, out _, out _)
            ? new SearchAction(Loc.Format("QuickConnectTo", q), SymbolRegular.PlugConnected24, () => QuickConnect(q), Loc.Get("QuickConnect"))
            : null;

        var results = new List<object>();
        // Typed as a command ("stop DEV-12"): what was asked for comes first. The verb alone may as well be the start
        // of a name ("link" for LinkedServer01): the connections it finds stay first then, Enter must not run a command.
        var verbs = VerbCommands(q).ToList();
        var verbFirst = matches.Count == 0 || Palette.Parse(q) is (_, { Length: > 0 });
        if (verbFirst) results.AddRange(verbs);
        if (quick != null && looksLikeAddress) results.Add(quick);
        if (exact != null) results.Add(exact);
        results.AddRange(matches.Where(c => c != exact));
        if (!verbFirst) results.AddRange(verbs);
        // A single word is more likely a command ("settings") than a host: the host stays one row further down.
        results.AddRange(commands);
        if (quick != null && !looksLikeAddress) results.Add(quick);
        var keep = Results.SelectedItem; // a refresh (data changed) keeps the row the user is on
        Reset(SearchResults, results);
        var index = keep == null ? -1 : SearchResults.IndexOf(keep);
        Results.SelectedIndex = index >= 0 ? index : SearchResults.Count > 0 ? 0 : -1;
    }

    const int MaxMatches = 50;
    bool _moreMatches;

    IEnumerable<SearchAction> Commands()
    {
        var kind = Loc.Get("Command");
        return
        [
            new(Loc.Get("NewConnection"), SymbolRegular.Add24, () => NewConnection_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("NewGroup"), SymbolRegular.FolderAdd24, () => NewGroup_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("Credentials"), SymbolRegular.Key24, () => Credentials_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("Settings"), SymbolRegular.Settings24, () => Settings_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("Import"), SymbolRegular.ArrowImport24, () => Import_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("Export"), SymbolRegular.ArrowExportLtr24, () => Export_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("DisconnectAll"), SymbolRegular.PlugDisconnected24, DisconnectAll, kind, Danger: true),
            new(Loc.Get("SignOutAll"), SymbolRegular.SignOut24, () => SignOutAll_Click(this, new RoutedEventArgs()), kind, Danger: true),
            new(Loc.Get("PaletteRefresh"), SymbolRegular.ArrowSync24, () => _ = ProbeAsync(), kind),
            new(Loc.Get("ShortcutsTitle"), SymbolRegular.QuestionCircle24, () => Shortcuts_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("MachinesTitle"), SymbolRegular.Cloud24, MachinesWindow.ShowOrActivate, kind),
            new(Loc.Get("Scripts"), SymbolRegular.Code24, () => Scripts_Click(this, new RoutedEventArgs()), kind),
            new(Loc.Get("WhatsNew"), SymbolRegular.Sparkle24, () => WhatsNewWindow.ShowAll(this), kind),
        ];
    }

    /// <summary>
    /// "uruchom DEV-12", "zalogowani na DEV-13": the verb for each matching connection it applies to (a stopped VM
    /// starts, an open session signs out). Only the verb ("wyloguj"): every connection it applies to.
    /// </summary>
    IEnumerable<SearchAction> VerbCommands(string q)
    {
        if (Palette.Parse(q) is not var (verb, rest)) return [];
        var kind = Loc.Get("Command");
        // Machines no connection leads to: once listed in this run (a list in the background on the first command).
        if (verb is PaletteVerb.Start or PaletteVerb.ShutDown or PaletteVerb.Restart or PaletteVerb.Postpone
            && Machines.Last == null && Store.Data.Settings.AzureAccounts.Count > 0) _ = Machines.LoadAsync();
        // "link DEV-12": each of its links, on this PC and in its session.
        if (verb == PaletteVerb.Link)
            return Store.Search(rest).SelectMany(c => Store.LinksOf(c).SelectMany(l => new[]
            {
                new SearchAction(Loc.Format("PaletteLinkHere", l.Name, c.Title), SymbolRegular.Globe24, () => LinkMenu.OpenHere(l.Url), kind),
                new SearchAction(Loc.Format("PaletteLinkSession", l.Name, c.Title), SymbolRegular.Desktop24, () => _ = Sessions.OpenLinkInSessionAsync(c, l.Url), kind),
            })).Take(8);
        var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var unlinked = Machines.Unlinked.Where(m => !Machines.IsHidden(m.AzureVm!) && tokens.All(t => m.Title.Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(m => m.Title, ByName);
        return Store.Search(rest).Concat(unlinked).Where(c => Applies(verb, c)).Take(8)
            .Select(c => new SearchAction(Loc.Format($"Palette{verb}", c.Title), VerbIcon(verb), () => Run(verb, c), kind,
                Danger: verb is PaletteVerb.ShutDown or PaletteVerb.Restart or PaletteVerb.SignOut or PaletteVerb.Disconnect));
    }

    RdpSession? OpenSession(Connection c) => Sessions.Open.FirstOrDefault(s => s.Connection == c && s.IsOpen);

    bool Applies(PaletteVerb verb, Connection c)
    {
        var running = c.VmState is VmPowerState.Running or VmPowerState.Starting;
        // A machine without a connection (the machines window's row): only what is done to the machine itself.
        if (c.Host.Length == 0 && verb is not (PaletteVerb.Start or PaletteVerb.ShutDown or PaletteVerb.Restart or PaletteVerb.Postpone)) return false;
        return verb switch
        {
            PaletteVerb.Start => c.AzureVm != null && !running,
            PaletteVerb.ShutDown or PaletteVerb.Restart => c.AzureVm == null || running,
            PaletteVerb.SignOut => OpenSession(c) is { IsReady: true, IsClosing: false },
            PaletteVerb.Disconnect => OpenSession(c) != null,
            PaletteVerb.Postpone => c is { VmShutdownAt: not null, VmShutdownSkipped: false, VmShutdownReadOnly: false },
            _ => true,
        };
    }

    static SymbolRegular VerbIcon(PaletteVerb verb) => verb switch
    {
        PaletteVerb.Connect or PaletteVerb.Start => SymbolRegular.Play24,
        PaletteVerb.ConnectAs => SymbolRegular.Person24,
        PaletteVerb.Edit => SymbolRegular.Edit24,
        PaletteVerb.ShutDown => SymbolRegular.Power24,
        PaletteVerb.Restart => SymbolRegular.ArrowReset24,
        PaletteVerb.SignedIn => SymbolRegular.People24,
        PaletteVerb.SignOut => SymbolRegular.SignOut24,
        PaletteVerb.Disconnect => SymbolRegular.PlugDisconnected24,
        _ => SymbolRegular.Clock24,
    };

    void Run(PaletteVerb verb, Connection c)
    {
        switch (verb)
        {
            case PaletteVerb.Connect: Connect(c); break;
            case PaletteVerb.ConnectAs: Connect(c, askCredentials: true); break;
            case PaletteVerb.Edit: Edit(c); break;
            case PaletteVerb.Start: _ = Machines.StartAsync(this, [c]); break;
            case PaletteVerb.ShutDown when c.AzureVm != null: _ = Machines.StopAsync(this, [c]); break;
            case PaletteVerb.ShutDown: _ = Machines.PowerAsync(this, [c], restart: false); break;
            case PaletteVerb.Restart: _ = Machines.PowerAsync(this, [c], restart: true); break;
            case PaletteVerb.SignedIn: new SignedInWindow(c) { Owner = this }.ShowDialog(); break;
            case PaletteVerb.SignOut when OpenSession(c) is { } s && Sessions.WindowOf(s) is { } w: _ = w.SignOutAsync(); break;
            case PaletteVerb.Disconnect when OpenSession(c) is { } s: Sessions.WindowOf(s)?.Close(); break; // asks, disconnects, closes
            case PaletteVerb.Postpone: _ = AzureShutdown.WithFeedbackAsync(this, c, () => AzureShutdown.PostponeAsync(c, 1)); break;
        }
    }

    void RunResult(object? item)
    {
        if (item is Connection c) Connect(c);
        else if (item is SearchAction a)
        {
            SearchBox.Text = "";
            a.Run();
        }
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var q = SearchBox.Text.Trim();
                if (q.Length == 0) break;
                if (Results.SelectedItem is { } item) RunResult(item);
                // Nothing found and no address either ("my old server"): the words stay, to be corrected.
                else if (Store.TryParseAddress(q, out _, out _)) QuickConnect(q);
                else
                {
                    e.Handled = true;
                    break;
                }
                SearchBox.Text = "";
                e.Handled = true;
                break;
            case Key.Down when SearchResults.Count > 0:
                FocusListItem(Results);
                e.Handled = true;
                break;
            case Key.Escape:
                SearchBox.Text = "";
                e.Handled = true;
                break;
        }
    }

    static void FocusListItem(ListBox list)
    {
        if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem)?.Focus();
    }

    void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is { } item) RunResult(item);
    }

    void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Results.SelectedItem is SearchAction a)
        {
            RunResult(a);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
            e.Handled = true;
        }
        else NodeKeyDown(e);
    }

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

    void Results_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        LeaveFavorites();
        if (FindAncestor<ListBoxItem>(e.OriginalSource) is { } item) item.IsSelected = true;
        else Results.SelectedItem = null;
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

    // ---------- Azure sync ----------

    void Menu_SyncAzure(object sender, RoutedEventArgs e)
    {
        if (Selected is Group { AzureSync: not null } group) _ = SyncAzureAsync(group, manual: true);
    }

    /// <summary>At start, quietly: every group with a rule, one after another, a moment after the window is up.</summary>
    async Task SyncAzureGroupsAsync()
    {
        if (Store.Data.Settings.AzureAccounts.Count == 0 || !Store.Data.Groups.Any(g => g.AzureSync != null)) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        foreach (var group in Store.Data.Groups.Where(g => g.AzureSync != null).ToList()) await SyncAzureAsync(group, manual: false);
    }

    /// <summary>
    /// Brings a group in step with Azure: adds connections for new VMs and readdresses the ones the sync made. A
    /// connection whose VM is gone is removed only when the user, asked after a sync they started, says so.
    /// </summary>
    async Task SyncAzureAsync(Group group, bool manual)
    {
        AzureSyncPlan plan;
        try { plan = await AzureSync.PlanAsync(Store, group); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException
                                       or AzureSignInRequiredException or TaskCanceledException)
        {
            Log.Info($"Azure sync of {group.Name}: {ex.Message}");
            if (manual) Dialog.Show(this, Loc.Format("AzureSyncFailed", group.Name, ex.Message), DialogKind.Warning);
            return;
        }
        if (!Store.Data.Groups.Contains(group) || App.IsExiting) return; // deleted while Azure was asked
        AzureSync.Apply(Store, plan);
        Log.Info($"Azure sync of {group.Name}: {plan.Add.Count} added, {plan.Update.Count} readdressed, {plan.Missing.Count} no longer in Azure");
        if (plan.Add.Count + plan.Update.Count > 0) Refresh();
        if (!manual) return;

        var summary = Loc.Format("AzureSyncDone", group.Name, plan.Add.Count, plan.Update.Count);
        if (plan.NoAddresses) summary += "\n\n" + Loc.Get("AzureSyncNoAddresses");
        // An open session's connection stays: its window would lose what it belongs to.
        var removable = plan.Missing.Where(c => !c.IsOpen).ToList();
        if (removable.Count == 0)
        {
            Dialog.Show(this, summary);
            return;
        }
        var names = string.Join("\n", removable.Select(c => c.Title));
        if (!Dialog.Confirm(this, summary + "\n\n" + Loc.Format("AzureSyncMissing", names), Loc.Get("AzureSyncRemove"), DialogKind.Warning,
                danger: true, cancelText: Loc.Get("AzureSyncKeep"))) return;
        foreach (var c in removable) Store.Delete(c);
        Refresh();
    }

    void Menu_SortByName(object sender, RoutedEventArgs e)
    {
        Store.SortByName(TargetGroup);
        Refresh();
    }

    static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d != null && d is not T)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ---------- Node commands ----------

    void NodeMenu_Opened(object sender, RoutedEventArgs e)
    {
        var n = Selected;
        var marked = Marked.Count;
        var many = marked > 1; // a multiple selection: only what works on all of it
        var menu = (ContextMenu)sender;
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
                "sort" => !many && n is not Connection && !_favoritesActive && !Results.IsVisible,
                "conn" or "fav" => !many && n is Connection,
                "group" => !many && n is Group,
                "sync" => !many && n is Group { AzureSync: not null },
                "node" => !many && n is { Shared: false },
                "shared" => !many && n is Group { Shared: false, SharedList.Length: > 0 },
                "share" => !many && n is Group { Shared: false, SharedList: null },
                "links" => !many && LinksOf(n).Count > 0,
                "scripts" => !many && Scripts.CanRun(n as Connection),
                "vm" => VmTargets(running: false).Count > 0,
                "vmstop" => VmTargets(running: true).Count > 0,
                "power" => PowerTargets().Count > 0,
                "vmpostpone" => !many && n is Connection { VmShutdownAt: not null, VmShutdownSkipped: false, VmShutdownReadOnly: false },
                "vmskip" => !many && n is Connection { VmShutdownAt: not null, VmShutdownReadOnly: false },
                _ => true,
            };
            item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (item.Tag is "fav" && n is Connection c) item.Header = Loc.Get(c.Favorite ? "RemoveFavorite" : "AddFavorite");
            if (item.Tag is "links" && item.Visibility == Visibility.Visible)
            {
                item.Items.Clear();
                foreach (var link in LinkMenu.Items(LinksOf(n), n as Connection)) item.Items.Add(link);
            }
            if (item.Tag is "scripts" && item.Visibility == Visibility.Visible && n is Connection target)
            {
                item.Items.Clear();
                foreach (var script in Scripts.MenuItems(this, target)) item.Items.Add(script);
            }
            if (item.Tag is "connect") item.Header = many ? Loc.Format("ConnectMarked", marked) : Loc.Get("Connect");
            if (item.Tag is "delete")
            {
                item.Header = many ? Loc.Format("DeleteMarked", marked) : Loc.Get("Delete");
                item.InputGestureText = _favoritesActive ? "" : "Del"; // among the favorites Del only takes the star away
            }
            if (item.Tag is "vmskip" && n is Connection sc)
                item.Header = sc.VmShutdownSkipped ? Loc.Get("AzureUnskipShutdown") : Loc.Format("AzureSkipShutdown", sc.VmShutdownWhen);
            if (item.Tag is "vm" or "vmstop" && VmTargets(item.Tag is "vmstop") is { Count: var vms } && item.Visibility == Visibility.Visible)
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

    static IEnumerable<Node> Descendants(Group g) => g.Children.SelectMany(c => c is Group sub ? Descendants(sub).Prepend(c) : [c]);

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

    /// <summary>The machines a restart / shut down acts on: the marked connections, a group's, or the selected one; each machine once.</summary>
    List<Connection> PowerTargets()
    {
        IEnumerable<Node> nodes = Marked.Count > 1 ? MarkedTops() : Selected is { } n ? [n] : [];
        return [.. nodes.SelectMany(n => n is Group g ? Descendants(g).OfType<Connection>() : n is Connection c ? [c] : [])
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
        var copy = new Connection
        {
            // A copy of a shared list's connection is the user's own: next to the list, in their group.
            Name = c.Title + Loc.Get("CopySuffix"), Host = c.Host, Port = c.Port, ParentId = c.Shared ? Store.OwnGroup(Store.Parent(c))?.Id : c.ParentId,
            CredentialId = c.CredentialId, Rdp = c.Rdp?.Clone(), Notes = c.Notes, Links = c.Links?.Select(l => l.Clone()).ToList(), Tags = c.Tags, Color = c.Color, AzureVm = c.AzureVm?.Clone(), AutoStart = c.AutoStart?.Clone(), ShutDownVmAfter = c.ShutDownVmAfter,
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
        try { await SharedLists.RefreshAsync(Store, root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or ArgumentException or NotSupportedException)
        {
            // Off the network or the share: the copy on this PC keeps showing.
            Log.Info($"Shared list of {root.Name}: not refreshed: {ex.Message}");
            if (manual) Dialog.Show(this, Loc.Format("SharedListFailed", root.Name, ex.Message), DialogKind.Warning);
            return;
        }
        if (!Store.Data.Groups.Contains(root) || App.IsExiting) return;
        var count = SharedLists.ItemsOf(Store, root).OfType<Connection>().Count();
        Log.Info($"Shared list of {root.Name}: {count} connections");
        Refresh();
        if (manual) Dialog.Show(this, Loc.Format("SharedListDone", root.Name, count));
    }

    void Menu_ExportShared(object sender, RoutedEventArgs e)
    {
        if (Selected is not Group { Shared: false } group) return;
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

    // ---------- Tools ----------

    void Credentials_Click(object sender, RoutedEventArgs e)
    {
        new CredentialsWindow { Owner = this }.ShowDialog();
        Refresh();
    }

    /// <summary>The quick-connect key: Sessionwright forward with the search box ready (a name or an address, then Enter).</summary>
    void ShowQuickConnect()
    {
        ShowFromTray();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    void Shortcuts_Click(object sender, RoutedEventArgs e) => new ShortcutsWindow { Owner = this }.ShowDialog();

    void Machines_Click(object sender, RoutedEventArgs e) => MachinesWindow.ShowOrActivate();

    IReadOnlyList<(string Day, (string En, string Pl)[] Items)> _news = [];

    /// <summary>After an update: a bar saying so; the list on request. Closing the bar (or opening the list) marks it seen.</summary>
    internal void OfferNews(IReadOnlyList<(string Day, (string En, string Pl)[] Items)> news, string? updatedTo = null)
    {
        if (updatedTo != null) NewsBar.Title = Loc.Format("UpdatedTo", updatedTo);
        if (_news.Count == 0)
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar)).AddValueChanged(NewsBar, (_, _) =>
            {
                if (!NewsBar.IsOpen) WhatsNewWindow.MarkSeen();
            });
        _news = news;
        NewsBar.IsOpen = true;
    }

    void NewsShow_Click(object sender, RoutedEventArgs e)
    {
        NewsBar.IsOpen = false;
        new WhatsNewWindow(_news) { Owner = this }.ShowDialog();
    }

    void Scripts_Click(object sender, RoutedEventArgs e) => new ScriptsWindow { Owner = this }.ShowDialog();

    /// <summary>The settings, from another window (a script that needs an Azure account added).</summary>
    internal void OpenSettings() => Settings_Click(this, new RoutedEventArgs());

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog(); // every change is saved as it is made
        if (App.IsExiting) return; // restarting for another language
        Tray.Apply(this);
        if (!SessionKeys.SetQuickConnect(this, Store.Data.Settings.QuickConnectKey))
            Dialog.Show(this, Loc.Format("QuickConnectKeyTaken", Store.Data.Settings.QuickConnectKey), DialogKind.Warning);
        App.ApplyTheme(this);
        Refresh();
        if (Store.Data.Settings.ProbeHosts) _ = ProbeAsync();
        else foreach (var c in Store.Data.Connections) c.Online = null;
        if (!Updater.IsEnabled)
        {
            _update = null;
            UpdateBar.IsOpen = false;
        }
        _ = CheckForUpdatesAsync();
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.Get("ImportFilter"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        // Into the selected group only when the user picked one: an imported host must not silently inherit the
        // credential and color of whatever was last clicked.
        var target = Tree.SelectedItem as Group;
        int added = 0, skipped = 0;
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var file in dlg.FileNames)
        {
            // One bad file must not stop the others; all problems are reported in a single summary.
            try
            {
                var result = Importer.ImportFile(Store, file, target?.Id, wrong => PasswordPrompt.Ask(this,
                    Loc.Format("ImportEnterPassword", Path.GetFileName(file)), confirm: false, wrong ? Loc.Get("ImportWrongPassword") : null),
                    commands => Dialog.Confirm(this, Loc.Format("ImportKeepCommands", Path.GetFileName(file), string.Join("\n", commands)),
                        Loc.Get("ImportKeepCommandsYes"), DialogKind.Warning, cancelText: Loc.Get("ImportKeepCommandsNo")));
                added += result.Added;
                skipped += result.Skipped;
                warnings.AddRange(result.Warnings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        Refresh();
        _ = ProbeAsync();
        var summary = Loc.Format("ImportSummary", added, skipped);
        if (target != null) summary += "\n" + Loc.Format("ImportedInto", Store.PathOf(target) is { Length: > 0 } p ? $"{p} / {target.Name}" : target.Name);
        if (warnings.Count > 0) summary += "\n\n" + Loc.Format("ImportWarnings", string.Join("\n", warnings));
        if (errors.Count > 0) summary += "\n\n" + Loc.Format("ImportFailed", string.Join("\n", errors));
        Dialog.Show(this, summary, errors.Count + warnings.Count > 0 ? DialogKind.Warning : DialogKind.Info);
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        // Passwords only on request, and then encrypted with a password of the user's choice (see Importer.ExportProtection).
        string? password = null;
        if (Store.Data.Credentials.Any(c => c.Secret != null)
            && Dialog.Confirm(this, Loc.Get("ExportWithPasswords"), Loc.Get("ExportWithPasswordsYes"), cancelText: Loc.Get("ExportWithPasswordsNo")))
        {
            password = PasswordPrompt.Ask(this, Loc.Get("ExportSetPassword"), confirm: true);
            if (password == null) return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = Loc.Get("ExportFilter"),
            FileName = $"Sessionwright-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, Importer.Export(Store, password));
            Dialog.Show(this, Loc.Format(password != null ? "ExportDoneWithPasswords" : "ExportDone", Store.Data.Connections.Count, dlg.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialog.Show(this, Loc.Format("SaveFailed", ex.Message), DialogKind.Error);
        }
    }

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
        var open = Sessions.Open.Count(s => s.IsOpen);
        if (open > 0 && !Dialog.Confirm(this, Loc.Format("ConfirmExit", open), Loc.Get("UpdateRestart"), DialogKind.Warning)) return;
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

    // ---------- Window ----------

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control; // not AltGr (Ctrl+Alt): AltGr+N types "ń"
        UpdateFavoriteNumbers();
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            ConnectFavorite(e.Key >= Key.NumPad1 ? e.Key - Key.NumPad0 : e.Key - Key.D0);
            e.Handled = true;
            return;
        }
        // Ctrl+Alt+Down: the session switcher, as in a session window (Alt makes it a "system" key).
        if (e.Key == Key.System && e.SystemKey == Key.Down && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            SessionSwitcher.Toggle(Sessions, null);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.F or Key.K when ctrl: // Ctrl+K kept as an alias (command-palette convention)
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.OemComma when ctrl: // Ctrl+, as in Windows Terminal and VS Code
                Settings_Click(this, e);
                break;
            // The delete just made (while its notification shows); in a text box Ctrl+Z stays the typing's own.
            case Key.Z when ctrl && _undo is { } undo && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase:
                undo.Note.Close();
                undo.Undo();
                break;
            case Key.M when ctrl:
                MachinesWindow.ShowOrActivate();
                break;
            case Key.F1:
                Shortcuts_Click(this, e);
                break;
            case Key.N when ctrl:
                NewConnection_Click(this, e);
                break;
            case Key.F5:
                _ = ProbeAsync();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    bool _exitRequested;

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        Native.Restore(this);
    }

    public void ExitFromTray()
    {
        _exitRequested = true;
        if (Sessions.Open.Count > 0) ShowFromTray(); // the "disconnect and exit?" question needs a visible owner
        Close();
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacement();
        // In the tray: the window goes, Sessionwright (and every session) keeps running.
        if (Tray.Enabled && !_exitRequested && !App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            Tray.HintOnce();
            return;
        }
        if (Sessions.Open.Count == 0 || App.IsExiting)
        {
            App.IsExiting = true; // a second launch now waits for this process instead of handing over to it
            return;
        }
        // Closing the main window exits: the sessions go first, each window waiting for its disconnect.
        e.Cancel = true;
        var open = Sessions.Open.Count(s => s.IsOpen);
        var signOut = false;
        if (open > 0 && !Store.Data.Settings.SkipSessionConfirmations)
        {
            var choice = Dialog.Choose(this, Loc.Format("ConfirmExit", open), Loc.Get("DisconnectAndExit"), Loc.Get("SignOutAndExit"));
            if (choice == 0)
            {
                _exitRequested = false;
                return;
            }
            signOut = choice == 2;
        }
        App.IsExiting = true;
        Hide();
        if (signOut)
        {
            // The window is gone and signing out takes a while (one session after another): said, so Sessionwright is not
            // started again, or the PC switched off, in the middle of it.
            var progress = Notification.Show(null, SymbolRegular.SignOut24, Loc.Format("SigningOutAll", open), Loc.Get("SigningOutAllText"));
            await Sessions.SignOutAllAsync();
            progress.Close();
        }
        await Sessions.CloseAllAsync(); // what is left: still connecting, ended, or not signed out after all
        Application.Current.Shutdown();
    }

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

/// <summary>A command (or quick connect) offered by the search box next to the matching connections.</summary>
/// <param name="Danger">It ends or stops something (shut down, restart, sign out, disconnect): shown in the caution color.</param>
public sealed record SearchAction(string Name, SymbolRegular Icon, Action Run, string Kind, bool Danger = false)
{
    public override string ToString() => Name;
    public string Spoken => $"{Name}, {Kind}"; // what a screen reader announces for the row
}
