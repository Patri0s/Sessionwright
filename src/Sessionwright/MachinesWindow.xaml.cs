using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>One machine in the machines window: what Azure listed and the row that carries its state.</summary>
public sealed class MachineRow(VmInfo info, Connection row, string linked, bool hidden)
{
    public VmInfo Info { get; } = info;
    public Connection Row { get; } = row;
    public string Name => Info.Name;
    public string Linked { get; } = linked;
    public bool HasConnection => Linked.Length > 0;
    public bool Hidden { get; } = hidden;
    public string Group => Info.SubscriptionName;
    public string Details => $"{Info.ResourceGroup} · {AzureVms.RegionName(Info.Location)}";
    public Visibility LinkedVisibility => HasConnection ? Visibility.Visible : Visibility.Collapsed;
    public double Opacity => Hidden ? 0.55 : 1;
    public bool IsRunning => Row.VmState is VmPowerState.Running or VmPowerState.Starting;
    public bool IsOff => Row.VmState is VmPowerState.Stopped or VmPowerState.Deallocated or VmPowerState.Unknown or null;
    public bool CanPostpone => Row is { VmShutdownAt: not null, VmShutdownSkipped: false, VmShutdownReadOnly: false };
}

/// <summary>
/// Every Azure machine of the signed-in accounts, with or without a connection: start, shut down, restart, postpone the
/// auto-shutdown, connect, make a connection of one. One window, not modal: it can stay open next to the sessions.
/// </summary>
public partial class MachinesWindow : FluentWindow
{
    static MachinesWindow? _open;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    List<MachineRow> _rows = [];
    bool _refreshing;

    MachinesWindow()
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        // Where it was last (often a second monitor next to the sessions), at its size; maximized once shown (see MainWindow).
        if (App.Store.Data.Settings.MachinesPlacement is { } placement && Native.IsOnScreen(placement))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            Native.SetPlacement(this, placement, show: false);
            if (placement.Maximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
        Closing += (_, _) =>
        {
            App.Store.Data.Settings.MachinesPlacement = Native.GetPlacement(this);
            App.Store.Save();
        };
        Title = Bar.Title = Loc.Get("MachinesTitle");
        Machines.Loaded += OnListLoaded;
        // States change by themselves (someone else, the auto-shutdown): followed while the window is open.
        _timer.Tick += (_, _) => { if (WindowState != WindowState.Minimized) _ = RefreshStatesAsync(); };
        Loaded += (_, _) =>
        {
            _timer.Start();
            SearchBox.Focus();
            _ = LoadAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Machines.Loaded -= OnListLoaded;
            _open = null;
        };
        UpdateActions();
    }

    /// <summary>Brings the window forward, opening it once.</summary>
    public static void ShowOrActivate()
    {
        if (_open is { } open)
        {
            Native.Restore(open);
            return;
        }
        _open = new MachinesWindow();
        _open.Show();
    }

    async Task LoadAsync()
    {
        AddAccountButton.Visibility = Visibility.Collapsed;
        // Without an account there is nothing to search or to act on: only the way to add one shows.
        Filters.Visibility = Actions.Visibility = CountText.Visibility =
            App.Store.Data.Settings.AzureAccounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (App.Store.Data.Settings.AzureAccounts.Count == 0)
        {
            Build();
            ShowStatus(Loc.Get("AzureNoAccounts"), busy: false, SymbolRegular.PersonAdd24);
            AddAccountButton.Visibility = Visibility.Visible;
            return;
        }
        if (Machines.Last != null) Build(); // the last list at once: going through every subscription takes seconds
        else ShowStatus(Loc.Get("AzureVmLoading"), busy: true);
        RefreshRing.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        try { await Machines.LoadAsync(); }
        finally
        {
            RefreshRing.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    void OnListLoaded()
    {
        if (!IsLoaded) return;
        Build();
        _ = RefreshStatesAsync(); // the auto-shutdown times are not in the list
    }

    void Build()
    {
        _rows = [.. (Machines.Last ?? []).Select(v => new MachineRow(v, Machines.Row(v),
            string.Join(", ", Machines.LinkedTo(v.Link).Select(c => c.Title)), Machines.IsHidden(v.Link)))];
        ProblemsText.Text = string.Join("  ", Machines.Problems);
        SignInList.ItemsSource = Machines.SignIns;
        Filter();
    }

    List<MachineRow> _shown = [];

    void Filter()
    {
        var tokens = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _rows
            .Where(r => ShowHiddenBox.IsChecked == true || !r.Hidden)
            .Where(r => RunningOnlyBox.IsChecked != true || r.IsRunning)
            .Where(r => tokens.All(t => $"{r.Name} {r.Info.ResourceGroup} {r.Info.SubscriptionName} {r.Info.Location} {r.Details} {r.Linked}"
                .Contains(t, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        // The same rows again (the 20 s state refresh with "running only" on): the list stays, with its scroll position
        // and keyboard focus.
        if (!shown.SequenceEqual(_shown))
        {
            var selected = Selected().Select(r => Machines.Key(r.Info.Link)).ToHashSet();
            var view = new ListCollectionView(shown);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MachineRow.Group)));
            List.ItemsSource = view;
            foreach (var r in shown.Where(r => selected.Contains(Machines.Key(r.Info.Link)))) List.SelectedItems.Add(r);
            _shown = shown;
        }
        var hidden = _rows.Count(r => r.Hidden);
        CountText.Text = Loc.Format(hidden > 0 ? "MachinesCountHidden" : "MachinesCount", shown.Count, _rows.Count, hidden);
        if (shown.Count > 0) StatusPanel.Visibility = Visibility.Collapsed;
        else if (Machines.Last != null) ShowStatus(Loc.Get(_rows.Count == 0 ? "AzureVmNone" : "AzureVmNoMatch"), busy: false, _rows.Count == 0 ? SymbolRegular.Cloud24 : SymbolRegular.Search24);
        UpdateActions();
    }

    void ShowStatus(string text, bool busy, SymbolRegular icon = SymbolRegular.Cloud24) => StatusPanel.Show(text, busy, icon);

    /// <summary>Power states (one call per subscription) and the next auto-shutdown of every listed machine.</summary>
    async Task RefreshStatesAsync()
    {
        if (_refreshing || _rows.Count == 0) return;
        _refreshing = true;
        try
        {
            var rows = _rows.Select(r => r.Row).Distinct().ToList();
            var states = await AzureVms.StatesAsync(rows.Select(r => r.AzureVm!).ToList());
            foreach (var (link, state) in states)
                if (state != null) foreach (var x in Machines.SameVm(link)) x.VmState = state;
            await AzureShutdown.RefreshAsync(rows);
            if (RunningOnlyBox.IsChecked == true) Filter();
            else UpdateActions();
        }
        finally { _refreshing = false; }
    }

    List<MachineRow> Selected() => [.. List.SelectedItems.OfType<MachineRow>()];

    void UpdateActions()
    {
        var selected = Selected();
        StartButton.IsEnabled = selected.Any(r => r.IsOff);
        StopButton.IsEnabled = RestartButton.IsEnabled = selected.Any(r => r.IsRunning);
        PostponeButton.IsEnabled = selected.Any(r => r.CanPostpone);
        ConnectButton.IsEnabled = selected is [{ HasConnection: true }];
        CreateButton.IsEnabled = selected is [{ HasConnection: false }];
        HideButton.IsEnabled = selected.Count > 0;
        var unhide = selected.Count > 0 && selected.All(r => r.Hidden);
        HideButton.Content = Loc.Get(unhide ? "MachinesUnhide" : "MachinesHide");
        HideButton.Icon = new SymbolIcon { Symbol = unhide ? SymbolRegular.Eye24 : SymbolRegular.EyeOff24 };
    }

    // On a selected machine the menu acts on all selected; elsewhere on the one pressed.
    void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) is System.Windows.Controls.ListBoxItem { IsSelected: false } item)
        {
            List.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    void Menu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateActions();
        static Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        ConnectItem.Visibility = Shown(ConnectButton.IsEnabled);
        StartItem.Visibility = Shown(StartButton.IsEnabled);
        StopItem.Visibility = Shown(StopButton.IsEnabled);
        RestartItem.Visibility = Shown(RestartButton.IsEnabled);
        PostponeItem.Visibility = Shown(PostponeButton.IsEnabled);
        CreateItem.Visibility = Shown(CreateButton.IsEnabled);
        HideItem.Visibility = Shown(HideButton.IsEnabled);
        HideItem.Header = HideButton.Content;
        if (HideButton.Icon is SymbolIcon icon) HideItem.Icon = new SymbolIcon { Symbol = icon.Symbol };
        if (Selected().Count == 0) ((System.Windows.Controls.ContextMenu)sender).IsOpen = false;
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Filter();
    }

    void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateActions();

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // On a row: not on the scroll bar or a group header, which would connect to whatever is selected.
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) is not System.Windows.Controls.ListBoxItem) return;
        if (Selected() is [{ HasConnection: true } row]) App.Sessions.Connect(row.Row, false);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    // Each action only on the selected machines it fits (a running one is not started again).
    async void Start_Click(object sender, RoutedEventArgs e)
    {
        await Machines.StartAsync(this, [.. Selected().Where(r => r.IsOff).Select(r => r.Row)]);
        UpdateActions();
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await Machines.StopAsync(this, [.. Selected().Where(r => r.IsRunning).Select(r => r.Row)]);
        UpdateActions();
    }

    void Restart_Click(object sender, RoutedEventArgs e) =>
        _ = Machines.PowerAsync(this, [.. Selected().Where(r => r.IsRunning).Select(r => r.Row)], restart: true);

    async void Postpone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in Selected().Where(r => r.CanPostpone))
            await AzureShutdown.WithFeedbackAsync(this, r.Row, () => AzureShutdown.PostponeAsync(r.Row, 1));
        UpdateActions();
    }

    void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is [{ HasConnection: true } row]) App.Sessions.Connect(row.Row, false);
    }

    // A connection of it, filled in: the VM's name as the host (an Azure DNS label is usually the VM name).
    void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not [{ HasConnection: false } row] || Application.Current.MainWindow is not MainWindow main) return;
        var c = new Connection { Name = row.Name, Host = row.Name, AzureVm = row.Row.AzureVm };
        if (new EditorWindow(c, isNew: true) { Owner = this }.ShowDialog() != true) return;
        main.AddConnection(c);
        Build();
    }

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        var hide = !selected.All(r => r.Hidden);
        foreach (var r in selected) Machines.SetHidden(r.Info.Link, hide);
        Build();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.F5:
                _ = LoadAsync();
                break;
            case Key.Enter when List.IsKeyboardFocusWithin && Selected() is [{ HasConnection: true } row]:
                App.Sessions.Connect(row.Row, false);
                break;
            case Key.Escape when SearchBox.Text.Length > 0:
                SearchBox.Text = "";
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        AddAccountButton.IsEnabled = false;
        ShowStatus(Loc.Get("AzureSignInBrowser"), busy: true);
        try
        {
            var account = await AzureVms.SignInAsync(Native.Handle(this));
            App.Store.Data.Settings.AzureAccounts.RemoveAll(a => string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));
            App.Store.Data.Settings.AzureAccounts.Add(account);
            App.Store.Save();
        }
        catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException or OperationCanceledException or System.IO.IOException)
        {
            Log.Error(ex, "Azure sign-in");
            ProblemsText.Text = Loc.Format("AzureSignInFailed", ex.Message);
        }
        finally { AddAccountButton.IsEnabled = true; }
        await LoadAsync();
    }

    async void TenantSignIn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AzureTenantSignIn target) return;
        SignInList.IsEnabled = false;
        try { await AzureVms.ReauthenticateAsync(target.Account, target.TenantId, Native.Handle(this)); }
        catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException or OperationCanceledException)
        {
            Log.Error(ex, $"Azure sign-in to tenant {target.TenantId}");
            ProblemsText.Text = Loc.Format("AzureSignInFailed", ex.Message);
        }
        finally { SignInList.IsEnabled = true; }
        await LoadAsync();
    }
}
