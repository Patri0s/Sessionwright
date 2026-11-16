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
        Machines.Loaded += PaletteWindow.Refresh;
        MasterPassword.Changed += () => Dispatcher.BeginInvoke(ShowLock);
        ShowLock();
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

    Node? Selected => _favoritesActive ? FavoritesList.SelectedItem as Node : Tree.SelectedItem as Node;
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
        PaletteWindow.Refresh();
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
        var hasConnections = Store.Data.Connections.Count > 0;
        FavoritesPanel.Visibility = Favorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        ExpandButtons.Visibility = Store.Data.Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Tree.Visibility = Store.Roots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTree.Visibility = Store.Roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TreeHeader.Text = Loc.Get("Connections");

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
        ShowRecent(open ? [] : [.. Store.Data.Connections.Where(c => c.LastUsed != null).OrderByDescending(c => c.LastUsed).Take(6)]);
        (WelcomeTitle.Text, WelcomeHint.Text) = (hasConnections, open) switch
        {
            (false, _) => (Loc.Get("FirstRunTitle"), Loc.Get("FirstRunHint")),
            (_, true) => (Loc.Get("WelcomeWindowsTitle"), Loc.Get("WelcomeWindowsHint")),
            _ => (Loc.Get("WelcomeTitle"), Loc.Get("WelcomeHint")),
        };
    }

    /// <summary>The connections used last, as buttons under the greeting; rebuilt only when the list changed.</summary>
    void ShowRecent(List<Connection> recent)
    {
        RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (recent.SequenceEqual(RecentList.Children.OfType<FrameworkElement>().Select(b => (Connection)b.Tag))) return;
        RecentList.Children.Clear();
        foreach (var c in recent)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Tag = c, Margin = new Thickness(4), Padding = new Thickness(12, 8, 12, 8), Width = 220, HorizontalContentAlignment = HorizontalAlignment.Left,
                Icon = new SymbolIcon { Symbol = c.AzureVm != null ? SymbolRegular.Cloud24 : SymbolRegular.Desktop24 },
                Content = new StackPanel
                {
                    Children =
                    {
                        new System.Windows.Controls.TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new System.Windows.Controls.TextBlock { Text = c.Address, Style = (Style)FindResource("Caption") },
                    },
                },
            };
            // Their own color, as on the cards: the button darkens its content while pressed, which made the name
            // unreadable and the icon nearly black.
            ((StackPanel)button.Content).SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            ((SymbolIcon)button.Icon).SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            System.Windows.Automation.AutomationProperties.SetName(button, c.Title);
            // On a double-click, like the list's rows and the other cards here (see Card_PreviewMouseLeftButtonDown): a
            // single click that only meant to bring the window forward opened a session. Enter and Space still connect.
            button.ToolTip = Loc.Get("CardConnectTip");
            button.Click += ConnectButton_Click;
            button.PreviewMouseLeftButtonDown += Card_PreviewMouseLeftButtonDown;
            button.PreviewMouseLeftButtonUp += Card_PreviewMouseLeftButtonUp;
            RecentList.Children.Add(button);
        }
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
            case Key.K or Key.F when ctrl: // Ctrl+F kept: it was the search box's
                OpenPalette(this);
                e.Handled = true;
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
}

/// <summary>A command (or quick connect) offered by the palette next to the matching connections.</summary>
/// <param name="Danger">It ends or stops something (shut down, restart, sign out, disconnect): shown in the caution color.</param>
public sealed record SearchAction(string Name, SymbolRegular Icon, Action Run, string Kind, bool Danger = false)
{
    public override string ToString() => Name;
    public string Spoken => $"{Name}, {Kind}"; // what a screen reader announces for the row
}
