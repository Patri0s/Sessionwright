using System.ComponentModel;
using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>Who is signed in on a connection's server, without opening a session; sign them out or disconnect them.</summary>
public partial class SignedInWindow : FluentWindow
{
    // The last answer per connection: shown at once next time while a fresh one comes (through Azure that is a minute).
    static readonly Dictionary<Guid, (List<ServerSession> Sessions, DateTime At)> Last = [];

    readonly Connection _connection;
    RemoteSessions.Route? _route; // chosen once the window is open (a quick check of port 445)
    bool _busy;

    public SignedInWindow(Connection connection)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        _connection = connection;
        Title = Bar.Title = Loc.Format("SignedInTitle", connection.Title);
        Loaded += async (_, _) => await LoadAsync();
    }

    void ShowRoute()
    {
        if (_route == null) return;
        RouteText.Text = _route == RemoteSessions.Route.Azure
            ? Loc.Get("SignedInViaAzure")
            : Loc.Format("SignedInDirect", _connection.Host, RemoteSessions.DirectAccount(_connection));
        // An Azure VM can also be asked directly (over a VPN it answers faster); a server without a VM only directly.
        OtherRouteButton.Visibility = _connection.AzureVm != null ? Visibility.Visible : Visibility.Collapsed;
        OtherRouteButton.Content = Loc.Get(_route == RemoteSessions.Route.Azure ? "SignedInTryDirect" : "SignedInTryAzure");
    }

    async Task LoadAsync()
    {
        if (_connection.AzureVm != null && _connection.VmState is VmPowerState.Stopped or VmPowerState.Deallocated)
        {
            _route ??= RemoteSessions.Route.Azure;
            ShowRoute();
            ShowStatus(Loc.Get("SignedInVmOff"), busy: false, SymbolRegular.Power24);
            UpdateLeftRunning([]);
            return;
        }
        SetBusy(true);
        var cached = Last.TryGetValue(_connection.Id, out var last);
        if (cached) Show(last.Sessions);
        AgePanel.Visibility = cached ? Visibility.Visible : Visibility.Collapsed;
        AgeText.Text = cached ? Loc.Format("SignedInAge", Loc.Ago(last.At, DateTime.Now)) : "";
        if (_route == null)
        {
            if (!cached) ShowStatus(Loc.Get("SignedInLoading"), busy: true);
            _route = await RemoteSessions.ChooseRouteAsync(_connection);
        }
        ShowRoute();
        if (!cached) ShowStatus(Loc.Get(_route == RemoteSessions.Route.Azure ? "SignedInLoadingAzure" : "SignedInLoading"), busy: true);
        try
        {
            var sessions = await RemoteSessions.ListAsync(_connection, _route.Value);
            Last[_connection.Id] = (sessions, DateTime.Now);
            Show(sessions);
            UpdateLeftRunning(sessions);
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            Log.Info($"{_connection.Title}: signed-in users ({_route}) unavailable: {ex.Message}");
            List.ItemsSource = null;
            ShowStatus(Describe(ex), busy: false, SymbolRegular.ErrorCircle24);
        }
        finally
        {
            AgePanel.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    void Show(List<ServerSession> sessions)
    {
        var picked = (List.SelectedItem as ServerSession)?.Id;
        List.ItemsSource = sessions;
        List.SelectedItem = sessions.FirstOrDefault(s => s.Id == picked);
        if (sessions.Count == 0) ShowStatus(Loc.Get("SignedInNone"), busy: false);
        else StatusPanel.Visibility = Visibility.Collapsed;
    }

    static bool IsFailure(Exception ex) => ex is Win32Exception or System.Net.Http.HttpRequestException or InvalidOperationException
        or AzureSignInRequiredException or TaskCanceledException or UnauthorizedAccessException;

    string Describe(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => Loc.Get("SignedInNoAzureRights"),
        Win32Exception { NativeErrorCode: 5 } => Loc.Format("SignedInAccessDenied", RemoteSessions.DirectAccount(_connection)),
        Win32Exception { NativeErrorCode: 1722 or 1726 or 53 or 1753 } => Loc.Format("SignedInUnreachable", _connection.Host),
        _ => Loc.Format("SignedInFailed", ex.Message),
    };

    /// <summary>The "left running" mark follows what the server says, for the connection's own account.</summary>
    void UpdateLeftRunning(List<ServerSession> sessions)
    {
        if (App.Sessions.Open.Any(s => s.Connection == _connection && s.IsOpen)) return; // its own session is open: nothing left behind
        if (App.Store.Resolve(_connection).Credential is not { } credential) return;
        var me = RemoteSessions.SplitAccount(credential.User, credential.Domain).User;
        var mine = sessions.Any(s => string.Equals(s.User, me, StringComparison.OrdinalIgnoreCase));
        if (mine == _connection.HasLeftRunning) return;
        _connection.LeftRunningAt = mine ? DateTime.Now : null;
        App.Sessions.NotifyDataChanged();
    }

    void ShowStatus(string text, bool busy, SymbolRegular icon = SymbolRegular.People24) => StatusPanel.Show(text, busy, icon);

    void SetBusy(bool busy)
    {
        _busy = busy;
        // Also while an older list is shown: its session numbers may be stale until the fresh one is in.
        RefreshButton.IsEnabled = OtherRouteButton.IsEnabled = !busy;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var s = List.SelectedItem as ServerSession;
        SignOutButton.IsEnabled = !_busy && s != null;
        DisconnectButton.IsEnabled = !_busy && s is { State: ServerSessionState.Active or ServerSessionState.Connected };
    }

    void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    async void OtherRoute_Click(object sender, RoutedEventArgs e)
    {
        _route = _route == RemoteSessions.Route.Azure ? RemoteSessions.Route.Direct : RemoteSessions.Route.Azure;
        List.ItemsSource = null;
        await LoadAsync();
    }

    async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ServerSession s) return;
        if (!Dialog.Confirm(this, Loc.Format("SignedInConfirmSignOut", s.Account, _connection.Title), Loc.Get("SignOutUser"), DialogKind.Warning, danger: true)) return;
        await EndAsync(s, signOut: true);
    }

    async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is ServerSession s) await EndAsync(s, signOut: false);
    }

    async Task EndAsync(ServerSession s, bool signOut)
    {
        SetBusy(true);
        ShowStatus(Loc.Format(signOut ? "SignedInSigningOut" : "SignedInDisconnecting", s.Account), busy: true);
        try
        {
            await RemoteSessions.EndAsync(_connection, _route!.Value, s, signOut);
            Log.Info($"{_connection.Title}: session {s.Id} of {s.Account} {(signOut ? "signed out" : "disconnected")} ({_route})");
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            Log.Error(ex, $"{_connection.Title}: ending session {s.Id}");
            SetBusy(false);
            Dialog.Show(this, Describe(ex), DialogKind.Error);
        }
        await LoadAsync();
    }
}
