using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;
using SystemInformation = System.Windows.Forms.SystemInformation;

namespace Sessionwright;

// What is done to the session from its window: signing out, links, Ctrl+Alt+Del. Part of SessionWindow (see SessionWindow.xaml.cs).
public partial class SessionWindow
{
    // ---------- Remote actions ----------

    /// <summary>The cover for typing into the session: the whole window (in full screen, everything it covers).</summary>
    Rectangle OperationBounds() => IsFullScreen ? _covered : Native.Bounds(this);

    bool _signingOut, _askingSignOut;

    // Locked meanwhile, the session draws nothing new: the typing could not see the Run dialog open and gave up.
    static bool _locked;
    static int _locks;

    static SessionWindow() => SystemEvents.SessionSwitch += (_, e) =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _locked = true;
            _locks++;
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
    };

    static async Task WaitUntilUnlockedAsync()
    {
        while (_locked) await Task.Delay(250);
    }

    /// <param name="ask">False when the user already chose to sign several sessions out at once.</param>
    internal async Task SignOutAsync(bool ask = true)
    {
        if (!Session.IsReady) return;
        if (ask && !App.Store.Data.Settings.SkipSessionConfirmations)
        {
            // While the question is open the taskbar preview showed a cropped, enlarged part of the remote desktop: the
            // same picture as under the cover instead, asking.
            _askingSignOut = true;
            (Session.CoverTitle, Session.CoverDetail) = CoverText();
            SetCoveredPreview(true);
            var confirmed = false;
            try { confirmed = Dialog.Confirm(this, Loc.Format("ConfirmSignOut", Session.Title), Loc.Get("SignOut"), DialogKind.Warning); }
            finally
            {
                _askingSignOut = false;
                // Confirmed: it stays until the sign-out's own (busy) preview takes over, with no live frame in between.
                if (!confirmed && !Session.IsBusy)
                {
                    SetCoveredPreview(false);
                    (Session.CoverTitle, Session.CoverDetail) = (null, null);
                }
            }
            if (!confirmed)
            {
                Session.FocusRemote();
                return;
            }
        }
        await Enqueue(async () =>
        {
            _signingOut = true;
            (Session.CoverTitle, Session.CoverDetail) = CoverText();
            Session.IsBusy = true;
            // Signed out (the window closes), or disconnected otherwise (its status screen with Reconnect shows).
            var ended = new TaskCompletionSource();
            void OnClosed(RdpSession _) => ended.TrySetResult();
            void OnPhase(object? s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(RdpSession.Phase) && Session.Phase == SessionPhase.Disconnected) ended.TrySetResult();
            }
            Session.Closed += OnClosed;
            Session.PropertyChanged += OnPhase;
            try
            {
                // Through the server's API when it can be asked: nothing typed, the window and keyboard stay the user's.
                if (Session.SignedInUser is { } user)
                {
                    // Covered as the typed sign-out is: Windows' own signing-out screen would show in the window otherwise.
                    // A minimized window shows nothing to cover.
                    if (WindowState != WindowState.Minimized) _cover = await Cover.ShowAsync(this, OperationBounds(), Loc.Get("SigningOut"), Session.Title, 120, MessageMonitor ?? StageBounds());
                    if (_closing) return; // closed while the cover faded in: the finally lifts it
                    Session.ExpectSignOut();
                    if (await RemoteSessions.TrySignOutAsync(Session.Connection, user))
                    {
                        if (await Task.WhenAny(ended.Task, Task.Delay(20000)) == ended.Task) return;
                        Log.Warn($"{Session.Title}: signed out through the API, but the session did not end: typing it");
                    }
                    Session.CancelSignOut();
                }
                await WaitUntilUnlockedAsync();
                // Not pulled forward: the user may have gone to another window (another session) meanwhile, and the
                // typing hands the foreground back after every key (see RdpSession.SendKeysFocused).
                await BringForwardAsync(activate: false);
                // Opaque before the first key: the Run dialog and the typing must never be seen.
                var cover = _cover ??= await Cover.ShowAsync(this, OperationBounds(), Loc.Get("SigningOut"), Session.Title, 120, MessageMonitor ?? StageBounds());
                if (_closing) return; // closed while the cover faded in: the finally lifts it
                EnsureCovers(cover);
                var locks = _locks;
                var typed = await Session.TypeSignOutAsync();
                if (!typed && _locks != locks && !_closing && Session.IsReady)
                {
                    Log.Info($"{Session.Title}: the screen was locked during the sign-out: typing it again once unlocked");
                    await WaitUntilUnlockedAsync();
                    await BringForwardAsync(activate: false);
                    EnsureCovers(cover);
                    typed = await Session.TypeSignOutAsync();
                }
                if (!typed && Session.Connection.AzureVm is { } vm && Session.SignedInUser is { } who && !_closing && Session.IsReady)
                {
                    Log.Info($"{Session.Title}: typing the sign-out failed: signing out through Azure");
                    Session.ExpectSignOut();
                    typed = await RemoteSessions.TrySignOutThroughAzureAsync(vm, who);
                    if (!typed) Session.CancelSignOut();
                }
                if (!typed)
                {
                    await HideCoverAsync();
                    if (!_closing) Notification.Problem(this, Loc.Format("ProblemSignOutTitle", Session.Title), Loc.Format("SignOutFailed", Session.Title));
                    return;
                }
                // The window closes with the session; if the server does not end it, say so.
                if (await Task.WhenAny(ended.Task, Task.Delay(20000)) == ended.Task) return;
                Session.CancelSignOut();
                await HideCoverAsync();
                if (!_closing) Notification.Problem(this, Loc.Format("ProblemSignOutTitle", Session.Title), Loc.Format("SignOutNotConfirmed", Session.Title), warning: true);
            }
            finally
            {
                Session.Closed -= OnClosed;
                Session.PropertyChanged -= OnPhase;
                // Also when the typing threw: no cover left over the session, no sign-out intent left armed.
                if (Session.Phase != SessionPhase.Closed) Session.CancelSignOut();
                await HideCoverAsync();
                Session.IsBusy = false;
                (Session.CoverTitle, Session.CoverDetail) = (null, null);
                _signingOut = false;
            }
        });
    }

    /// <summary>A link opened in this session's browser, the window brought forward to show it.</summary>
    /// <remarks>
    /// RDP has no way to tell the server's Windows to open an address (only keys, mouse, screen and clipboard), so it is
    /// typed into Run: under a cover, as a sign-out's typing, until the browser has come up. Only the browser shows.
    /// </remarks>
    internal Task OpenLinkAsync(string url) => Enqueue(async () =>
    {
        await BringForwardAsync();
        var opened = false;
        var cover = _cover = await Cover.ShowAsync(this, OperationBounds(), Loc.Get("OpeningLink"),
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url, 120, MessageMonitor ?? StageBounds());
        try
        {
            if (_closing) return;
            EnsureCovers(cover);
            opened = await Session.OpenInSessionAsync(url);
            if (opened) await Session.WaitForWindowAsync(4000); // the browser up, not the desktop it opens over
        }
        finally { await HideCoverAsync(); }
        if (!opened && !_closing)
            Notification.Problem(this, Loc.Get("LinkNotOpenedTitle"), Loc.Format("LinkNotOpenedInSession", url, Session.Title));
        Session.FocusRemote();
    });

    // The server switches to its secure desktop through a black frame: faded through the stage color instead.
    internal async Task CtrlAltDelAsync()
    {
        if (!Session.IsReady) return;
        await Enqueue(async () =>
        {
            Session.IsBusy = true;
            var ok = true;
            try
            {
                await BringForwardAsync();
                await CoverWhileAsync(OperationBounds(), async () => ok = await Session.SendCtrlAltDelAsync());
            }
            finally { Session.IsBusy = false; }
            if (!ok && !_closing) Dialog.Show(this, Loc.Get("SendCtrlAltDelFailed"), DialogKind.Warning);
        });
    }

    /// <summary>
    /// Forward, and only once it is really back: right after a restore from the taskbar a minimized window still reports
    /// its off-screen place (or its size is still changing), and a cover measured then went there, leaving the typing seen.
    /// </summary>
    /// <param name="activate">False: only a minimized window is restored (and so activated); one in the background stays there.</param>
    async Task BringForwardAsync(bool activate = true)
    {
        if (activate || WindowState == WindowState.Minimized) Native.Restore(this);
        var last = Rectangle.Empty;
        for (var i = 0; i < 20; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var now = OperationBounds();
            if (WindowState != WindowState.Minimized && now.X > -10000 && now == last) return; // restored and settled
            last = now;
            await Task.Delay(25);
        }
    }

    /// <summary>Right before typing: the cover still over the whole window (it may have moved or grown meanwhile) and on top.</summary>
    void EnsureCovers(Cover cover)
    {
        var target = OperationBounds();
        cover.SetBounds(target);
        if (Native.IsForeground(this)) cover.Raise();
        Log.Info($"{Session.Title}: cover over {target} (window {Native.Bounds(this)}, {WindowState}{(IsFullScreen ? ", full screen" : "")}, " +
                 $"{(Native.IsForeground(this) ? "foreground" : "not foreground")})");
    }

    async Task HideCoverAsync()
    {
        if (_cover is { } cover) await LiftAsync(cover);
    }

    async Task LiftAsync(Cover cover, int fadeMs = 180)
    {
        await cover.HideAsync(fadeMs);
        if (_cover == cover) _cover = null;
    }

    void CtrlAltDel_Click(object sender, RoutedEventArgs e) => _ = CtrlAltDelAsync();

    /// <summary>
    /// What is not in sight in the title bar: the small view, reconnect, Ctrl+Alt+Del, the connection's links, and how
    /// good the link is. Named items, so nothing has to be learned by its icon.
    /// </summary>
    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, SymbolRegular icon, Action run, bool enabled = true)
        {
            var item = new System.Windows.Controls.MenuItem { Header = text, Icon = new SymbolIcon { Symbol = icon }, IsEnabled = enabled };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add(Loc.Get("MiniView"), SymbolRegular.PictureInPicture24, () => MiniView.Toggle(Session));
        Add(Loc.Get("SendCtrlAltDel"), SymbolRegular.ShieldKeyhole24, () => _ = CtrlAltDelAsync(), Session.IsReady);
        Add(Loc.Get("Reconnect"), SymbolRegular.ArrowClockwise24, () => Reconnect_Click(this, new RoutedEventArgs()));
        if (App.Store.LinksOf(Session.Connection) is { Count: > 0 } links)
        {
            var open = new System.Windows.Controls.MenuItem { Header = Loc.Get("OpenLink"), Icon = new SymbolIcon { Symbol = SymbolRegular.Link24 } };
            foreach (var link in LinkMenu.Items(links, Session.Connection)) open.Items.Add(link);
            menu.Items.Add(open);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = Session.NetworkInfo, IsEnabled = false, Icon = new SymbolIcon { Symbol = SymbolRegular.Wifi124 } });
        MoreButton.IsHitTestVisible = false; // a click on it while open only closes the menu
        menu.Closed += (_, _) =>
        {
            MoreButton.IsHitTestVisible = true;
            if (IsActive) Session.FocusRemote();
        };
        menu.IsOpen = true;
    }

    // Next to Ctrl+Alt+Del and sign out: a slip would drop a working session, so that one is asked about first.
    void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Phase == SessionPhase.Ready && !App.Store.Data.Settings.SkipSessionConfirmations
            && !Dialog.Confirm(this, Loc.Format("ConfirmReconnect", Session.Title), Loc.Get("Reconnect"), DialogKind.Warning))
        {
            Session.FocusRemote();
            return;
        }
        Session.ReconnectCommand.Execute(null);
    }

    void SignOut_Click(object sender, RoutedEventArgs e) => _ = SignOutAsync();
    // The session comes forward first: a question owned by a minimized window would stay hidden with it (and the
    // sign-out wait for an answer nobody sees), and one behind other windows would be missed.
    void SignOutFromThumbnail()
    {
        Native.Restore(this);
        Dispatcher.BeginInvoke(() => _ = SignOutAsync(), System.Windows.Threading.DispatcherPriority.Background);
    }
    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    public void ToggleFullScreen() => RequestFullScreen(!_wantFullScreen);
    void CloseWindow_Click(object sender, RoutedEventArgs e) => RequestClose();

    // WPF throws on Close() while a Closing handler runs (the disconnect question is open): that window closes itself
    // once the question is answered.
    void RequestClose()
    {
        if (!_asking) Close();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // with Alt held WPF reports Key.System
        if (key == Key.F11) RequestFullScreen(!_wantFullScreen);
        // Ctrl+Alt+Left / Right while the window chrome has the keyboard; in the session the control raises them itself.
        else if (key is Key.Left or Key.Right && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            App.Sessions.SwitchFrom(Session, key == Key.Left ? -1 : 1);
        else return;
        e.Handled = true;
    }
}
