using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Sessionwright;

public partial class App : Application
{
    public static Store Store { get; private set; } = null!;
    public static SessionManager Sessions { get; } = new();
    /// <summary>The app is going away (exit confirmed, update restart, Windows sign-out): windows close without asking.</summary>
    public static bool IsExiting { get; set; }
    public const string RestartArgument = "--restart";

    // One instance per data file: two instances would each save their own copy and the last one would silently
    // discard the other's changes. The key also names the pipe a second launch hands its arguments over.
    static readonly string InstanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Store.DefaultPath.ToLowerInvariant())))[..16];
    static Mutex? _instance; // held for the whole process lifetime

    /// <summary>
    /// Set while uninstalling: the question asked then needs the app's styles, not the app. WPF raises Startup as soon as
    /// any window pumps messages, so without this the data would load and the main window open behind the question.
    /// </summary>
    internal static bool ResourcesOnly;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ResourcesOnly) return;
        DispatcherUnhandledException += (_, a) =>
        {
            // Keep the app (and every open session) alive on a UI bug.
            Log.Error(a.Exception, "Unhandled UI exception");
            a.Handled = true;
            // One dialog at a time: an error that comes with every focus change came again for the dialog itself, and
            // again for the next one, a stack of them. The rest is in the log.
            if (IsExiting || _showingError) return;
            _showingError = true;
            try { Dialog.Show(null, Loc.Format("UnexpectedError", a.Exception.Message, Log.File), DialogKind.Error); }
            finally { _showingError = false; }
        };
        // WinForms hosts the RDP ActiveX and draws the covers: its exceptions go the same way, not to its own dialog.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, a) => Log.Error(a.Exception, "Unhandled WinForms exception");
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error((Exception)a.ExceptionObject, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error(a.Exception, "Unobserved task exception");
            a.SetObserved();
        };
        SessionEnding += (_, _) => IsExiting = true;

        // A restart (language change) waits for the old process: it still holds the single-instance mutex.
        if (e.Args is [RestartArgument, var pid] && int.TryParse(pid, out var oldId))
        {
            try
            {
                using var old = System.Diagnostics.Process.GetProcessById(oldId);
                old.WaitForExit(10_000);
            }
            catch (ArgumentException) { } // already gone
        }
        // A jump list entry starts the exe with "--connect <id>".
        Guid? connect = e.Args is [Taskbar.ConnectArgument, var id] && Guid.TryParse(id, out var guid) ? guid : null;
        // A sessionwright: link clicked in a browser, a wiki or a chat: Windows passes it as the only argument.
        var link = e.Args is [var arg] && SessionLink.IsLink(arg) ? arg : null;
        // Before a possible hand-over to a running copy, which may be one that is not installed and registers nothing.
        SessionLink.RegisterThisExe();
        if (!ClaimSingleInstance(connect?.ToString() ?? link))
        {
            Shutdown();
            return;
        }

        // Written by versions before sessionwright.log (until October 2026) and no longer read by anything.
        try { System.IO.File.Delete(System.IO.Path.Combine(Log.Folder, "error.log")); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        Log.Info($"Sessionwright {Updater.CurrentVersion} on {Environment.OSVersion.VersionString}, monitors: " +
                 string.Join("; ", System.Windows.Forms.Screen.AllScreens.Select(s => $"{s.Bounds} {Dpi(s)} dpi{(s.Primary ? " primary" : "")}")));
        if (!ClaimDataFile())
        {
            Shutdown(1);
            return;
        }
        try
        {
            Store = Store.Load(Store.DefaultPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading data failed");
            Dialog.Show(null, Loc.Format("LoadFailed", Store.DefaultPath, ex.Message), DialogKind.Error);
            Shutdown(1);
            return;
        }

        SharedLists.LoadCached(Store);

        if (Store.Data.Settings.Language is { Length: > 0 } lang)
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo(lang);

        // A release downloaded before and not installed yet goes in now, in the app's own window. Not while another
        // Sessionwright runs from this install: the updater stops every process in it, sessions and all.
        if (!Program.OtherInstanceRunning() && Updater.PendingRestart() is { } pending)
        {
            Log.Info($"Update: installing {pending.Version} downloaded earlier");
            var updating = UpdatingWindow.ShowFor(pending.Version.ToString(), applyTheme: true);
            // WPF made it the main window, being the first: closing it after a failed start would end the app.
            MainWindow = null;
            if (Updater.ApplyAndRestart(pending, e.Args))
            {
                updating.HoldUntilRead();
                Shutdown();
                return;
            }
            updating.Close();
        }

        ArrowOverControls();
        var window = new MainWindow();
        MainWindow = window;
        ApplyTheme(window);
        // Started by Windows at sign-in: only the tray icon (the window's handle still carries the quick-connect key).
        if (e.Args.Contains(Tray.BackgroundArgument) && Store.Data.Settings.RunInTray) new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        else window.Show();
        Tray.Apply(window);
        foreach (var warning in Store.Warnings) Log.Warn(warning);
        if (Store.Recovered is { } recovered)
        {
            Log.Warn($"Data recovered from {recovered.Source} (damaged copy: {recovered.CorruptCopy ?? "none"})");
            Dialog.Show(window, recovered.Message, DialogKind.Warning);
        }
        if (connect is { } c) window.ConnectById(c);
        else if (link != null) window.OpenLink(link);
        // Not on top of a connect asked for from the jump list: the next plain start tells what is new.
        else window.Dispatcher.BeginInvoke(() => WhatsNewWindow.ShowAfterUpdate(window), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// The pointer as in Windows 11: an arrow over every control, a hand only over links. WPF-UI gives check boxes and
    /// drop-down items a hand of their own (buttons, radio buttons and tabs it does not): taken back here, on WPF-UI's
    /// own styles. In code: the same styles in App.xaml, based on {x:Type CheckBox}, got the classic Windows look.
    /// </summary>
    void ArrowOverControls()
    {
        foreach (var type in new[] { typeof(System.Windows.Controls.CheckBox), typeof(System.Windows.Controls.ComboBoxItem) })
        {
            var style = new Style(type, (Style)FindResource(type));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Arrow));
            Resources[type] = style;
        }
    }

    internal static uint Dpi(System.Windows.Forms.Screen s)
    {
        var monitor = MonitorFromPoint(new System.Drawing.Point(s.Bounds.Left + 1, s.Bounds.Top + 1), 2);
        return GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : 96;
    }

    static bool _showingError;
    static FileStream? _dataLock; // held for the whole process lifetime

    /// <summary>
    /// The single-instance mutex and pipe reach only as far as this Windows session; the data file is the account's (or
    /// on a share). The same account signed in twice (a terminal server) or two PCs on one file would each save their
    /// own copy, the last one silently dropping the other's changes: a lock file next to the data says it is taken.
    /// </summary>
    bool ClaimDataFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store.DefaultPath)!);
            _dataLock = new FileStream(Store.DefaultPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // sharing / lock violation: someone holds it
        {
            Log.Error(ex, "Data file in use");
            Dialog.Show(null, Loc.Format("DataInUse", Store.DefaultPath), DialogKind.Warning);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error(ex, "Data lock file"); // a folder that cannot be written to: loading or saving will say so
            return true;
        }
    }

    bool ClaimSingleInstance(string? request)
    {
        bool first;
        try
        {
            _instance = new Mutex(true, $@"Local\Sessionwright-{InstanceKey}", out first);
        }
        catch (UnauthorizedAccessException ex) // created by an instance at another elevation level
        {
            Log.Error(ex, "Single-instance mutex");
            Dialog.Show(null, Loc.Get("AlreadyRunningElevated"), DialogKind.Info);
            return false;
        }
        if (!first)
        {
            if (HandOver(request ?? "activate")) return false;
            // Not taken: the running instance is exiting (or hangs). Take over once it is gone; the request is then
            // handled by this process's own startup.
            try { first = _instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
            if (!first) return false;
        }
        _ = ListenAsync();
        return true;
    }

    static string PipeName => $"Sessionwright-{InstanceKey}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// A second launch passes its request to the running instance, which brings itself forward. False when it was
    /// not taken (the running instance is exiting or does not answer).
    /// </summary>
    static bool HandOver(string request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            AllowSetForegroundWindow(-1); // ASFW_ANY: let the running instance take the foreground
            pipe.Write(System.Text.Encoding.UTF8.GetBytes(request + "\n"));
            return pipe.ReadByte() == 1; // acknowledged: handled before this process exits
        }
        catch (UnauthorizedAccessException ex) // the pipe belongs to an instance at another elevation level
        {
            Log.Error(ex, "Handing over to the running instance failed");
            Dialog.Show(null, Loc.Get("AlreadyRunningElevated"), DialogKind.Info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log.Error(ex, "Handing over to the running instance failed");
            return false;
        }
    }

    async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                string? request;
                // A client that connects and sends nothing must not hold the only listener for good.
                using (var silent = new CancellationTokenSource(2000))
                using (var reader = new StreamReader(pipe, leaveOpen: true)) request = await reader.ReadLineAsync(silent.Token);
                if (IsExiting) continue; // no ack: the new process waits and takes over once this one is gone
                await Dispatcher.InvokeAsync(() =>
                {
                    if (MainWindow is not MainWindow w) return;
                    w.ShowFromTray();
                    // A dialog is open (e.g. the credential prompt of the same jump-list entry): its nested loop must
                    // not start another connect.
                    if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return;
                    if (Guid.TryParse(request, out var id)) w.ConnectById(id); // the session window comes forward by itself
                    // After the acknowledgement: a link may ask first, and the launch that brought it must not wait on that.
                    else if (SessionLink.IsLink(request)) w.Dispatcher.BeginInvoke(() => w.OpenLink(request!));
                });
                await pipe.WriteAsync(new byte[] { 1 });
            }
            catch (Exception ex) // whatever one request did (also a failed connect): the next launch must still be heard
            {
                Log.Error(ex, "Single-instance pipe");
                await Task.Delay(1000);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray.Dispose(); // no icon left behind in the notification area
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(System.Drawing.Point p, uint flags);
    [System.Runtime.InteropServices.DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    static bool _watching, _reapplying;
    // The app icon's blue (its gradient runs #36B4F5 to #2870E8), in shades set by hand: derived ones come out pale in Dark.
    static void ApplyDefaultAccent(ApplicationTheme theme)
    {
        static System.Windows.Media.Color C(uint rgb) => System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (theme == ApplicationTheme.Dark) ApplicationAccentColorManager.Apply(C(0x2B8AF2), C(0x3AA2F5), C(0x45ACF7), C(0x5CB8F8));
        else ApplicationAccentColorManager.Apply(C(0x2B8AF2), C(0x1F74E0), C(0x1A66CC), C(0x1559B3));
    }

    public static void ApplyTheme(Window main)
    {
        if (_watching) SystemThemeWatcher.UnWatch(main);
        _watching = false;
        var systemAccent = Store.Data.Settings.UseSystemAccent;
        switch (Store.Data.Settings.Theme)
        {
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, systemAccent);
                break;
            case "Dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, systemAccent);
                break;
            default:
                // Also maps the Windows high-contrast themes (which a light/dark choice of our own would ignore).
                ApplicationThemeManager.ApplySystemTheme(systemAccent);
                SystemThemeWatcher.Watch(main, WindowBackdropType.Mica, systemAccent);
                _watching = true;
                break;
        }
        OnThemeChanged(ApplicationThemeManager.GetAppTheme(), default);
        // Also when Windows switches light/dark while the app follows it.
        ApplicationThemeManager.Changed -= OnThemeChanged;
        ApplicationThemeManager.Changed += OnThemeChanged;
    }

    static void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color _)
    {
        SyncNativeMenus(theme);
        // Without the Windows accent: the app's own blue, in this theme's shades. The theme dictionary copies the accent when
        // it loads (the primary button kept the old one otherwise), so it is loaded again after the accent is set.
        if (!Store.Data.Settings.UseSystemAccent && !_reapplying)
        {
            _reapplying = true;
            try
            {
                ApplyDefaultAccent(theme);
                ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);
            }
            finally { _reapplying = false; }
        }
        var stage = Cover.Stage;
        Current.Resources["StageBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(stage.R, stage.G, stage.B));
        // WPF-UI updates the backdrop of the main window and its owned windows only; session windows are top-level.
        foreach (var w in Current.Windows.OfType<SessionWindow>())
        {
            w.Session.ApplyStage();
            WindowBackgroundManager.UpdateBackground(w, theme, WindowBackdropType.Mica);
            w.Dispatcher.BeginInvoke(w.HideBorderInFullScreen); // after FluentWindow's own handler of this change
        }
    }

    // The title-bar system menu (right-click / Alt+Space) is a native Win32 menu: it follows the process "app mode",
    // not WPF styles. uxtheme ordinals 135/136 (SetPreferredAppMode, FlushMenuThemes) are undocumented but stable
    // since Windows 10 1903 - the same calls Chromium and Explorer use for dark context menus.
    static void SyncNativeMenus(ApplicationTheme theme)
    {
        try
        {
            SetPreferredAppMode(theme == ApplicationTheme.Dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { } // older Windows: menus stay light
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
}
