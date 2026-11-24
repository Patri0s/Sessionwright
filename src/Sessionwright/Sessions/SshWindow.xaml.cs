using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// An SSH session in a window of its own: Windows' ssh.exe run in a pseudoconsole, shown by xterm.js in a web view. The
/// protocol, the keys, ~/.ssh/config and the checking of host keys are all OpenSSH's; this window is its screen and
/// keyboard, and answers what it asks through <see cref="SshAskPass"/> (the saved password, a new host key's OK).
/// Opened and kept by <see cref="SessionManager"/> (its Terminals).
/// </summary>
public partial class SshWindow : FluentWindow, IConnecting
{
    readonly string _pipe = $"Sessionwright-askpass-{Guid.NewGuid():N}";
    readonly CancellationTokenSource _closed = new();
    PseudoConsole? _console;
    (int Columns, int Rows) _size = (120, 30);
    bool _passwordGiven; // the saved password was handed over once: asked for again, it was wrong
    bool _ready, _connecting;

    public Connection Connection { get; }

    internal SshWindow(Connection c)
    {
        Connection = c;
        InitializeComponent();
        Title = Bar.Title = c.Title;
        c.LastUsed = DateTime.Now;
        c.PropertyChanged += OnConnectionChanged;
        Loaded += async (_, _) => await StartViewAsync();
        _ = SshAskPass.ServeAsync(_pipe, Answer, _closed.Token); // for as long as the window is there, over every reconnect
        Closed += (_, _) =>
        {
            _closed.Cancel();
            _console?.Dispose();
            ApplicationThemeManager.Changed -= OnThemeChanged;
            c.PropertyChanged -= OnConnectionChanged;
        };
        Activated += (_, _) => Post(new { focus = true });
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F11) return;
            e.Handled = true;
            ToggleFullScreen();
        };
    }

    void OnConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Connection.Title)) Title = Bar.Title = Connection.Title;
    }

    // ---------- The web view ----------

    async Task StartViewAsync()
    {
        ShowStatus(Loc.Get("SshStarting"), busy: true);
        try
        {
            // Its own folder under this PC's app data: next to the exe is read-only for an installed copy, and next to the
            // data may be a share or a USB stick.
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sessionwright", "WebView2");
            await View.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, folder));
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "SSH: the web view for the terminal could not be started");
            ShowStatus(Loc.Get("SshNoWebView"), busy: false);
            return;
        }
        var settings = View.CoreWebView2.Settings;
        // A terminal, not a browser: no reload on F5, no zoom, no menu or tools of the browser's own.
        (settings.AreBrowserAcceleratorKeysEnabled, settings.AreDefaultContextMenusEnabled, settings.AreDevToolsEnabled) = (false, false, false);
        (settings.IsStatusBarEnabled, settings.IsZoomControlEnabled, settings.IsPinchZoomEnabled, settings.IsSwipeNavigationEnabled) = (false, false, false, false);
        View.CoreWebView2.WebMessageReceived += OnMessage;
        // The page is all there is to see: nothing may take the view elsewhere.
        View.CoreWebView2.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank";
        View.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        ApplicationThemeManager.Changed += OnThemeChanged;
        View.NavigateToString(Page());
    }

    static string? _page;

    /// <summary>The terminal's page with xterm.js and its style written into it: one string, no files next to the exe.</summary>
    static string Page()
    {
        static string Read(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Sessionwright.Assets.terminal.{name}")
                               ?? throw new FileNotFoundException(name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return _page ??= Read("terminal.html").Replace("/*XTERM_CSS*/", Read("xterm.css"))
            .Replace("/*XTERM_JS*/", Read("xterm.js")).Replace("/*FIT_JS*/", Read("addon-fit.js"));
    }

    void Post(object message)
    {
        if (View.CoreWebView2 != null) View.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent) => Post(new { theme = Theme() });

    /// <summary>
    /// The terminal's colors in the app's theme. Dark: xterm's own palette on the session stage's color. Light: a palette
    /// made for a white page (One Half Light, as in Windows Terminal), xterm's yellow and white being unreadable there.
    /// </summary>
    static object Theme() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light
        ? new
        {
            background = "#FAFAFA", foreground = "#383A42", cursor = "#4F525D", selectionBackground = "#BFCEFF",
            black = "#383A42", red = "#E45649", green = "#50A14F", yellow = "#C18301", blue = "#0184BC", magenta = "#A626A4", cyan = "#0997B3", white = "#A0A1A7",
            brightBlack = "#4F525D", brightRed = "#DF6C75", brightGreen = "#98C379", brightYellow = "#E4C07A", brightBlue = "#61AFEF",
            brightMagenta = "#C577DD", brightCyan = "#56B5C1", brightWhite = "#FFFFFF",
        }
        : new { background = "#1C1C1C", foreground = "#CCCCCC", cursor = "#FFFFFF", selectionBackground = "#264F78" };

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var message = JsonDocument.Parse(e.WebMessageAsJson);
        var root = message.RootElement;
        string? Text() => root.TryGetProperty("d", out var d) ? d.GetString() : null;
        switch (root.GetProperty("t").GetString())
        {
            case "ready":
                _size = (root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                _ready = true;
                Post(new { theme = Theme() });
                _ = ConnectAsync();
                break;
            case "in" when _console is { } console && Text() is { } typed:
                try
                {
                    console.Input.Write(Encoding.UTF8.GetBytes(typed));
                    console.Input.Flush();
                }
                catch (IOException) { } // ssh has just ended: the bar below says so
                break;
            case "size":
                _size = (root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                _console?.Resize(_size.Columns, _size.Rows);
                break;
            case "copy" when Text() is { Length: > 0 } selected:
                try { Clipboard.SetText(selected); }
                catch (System.Runtime.InteropServices.ExternalException) { } // another program holds the clipboard
                break;
            case "paste":
                try { if (Clipboard.ContainsText()) Post(new { p = Clipboard.GetText() }); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                break;
            case "fullscreen":
                ToggleFullScreen();
                break;
        }
    }

    // ---------- What shows instead of the terminal ----------

    // The web view is another window's pixels: WPF cannot draw over it, so the status takes its place.
    void ShowStatus(string text, bool busy, SymbolRegular icon = SymbolRegular.ErrorCircle24)
    {
        View.Visibility = Visibility.Hidden;
        Status.Show(text, busy, icon);
    }

    void ShowTerminal()
    {
        Status.Visibility = Visibility.Collapsed;
        View.Visibility = Visibility.Visible;
        Post(new { focus = true });
    }

    void Ended(string text, bool canReconnect)
    {
        (EndedText.Text, EndedBar.Visibility) = (text, Visibility.Visible);
        ReconnectButton.Visibility = canReconnect ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Before connecting (IConnecting): a VPN or command, the Azure VM started, the machine woken ----------

    string IConnecting.Message
    {
        set => ShowStatus(value, busy: true);
    }

    void IConnecting.Halt(string message)
    {
        ShowStatus(message, busy: false, SymbolRegular.Warning24);
        Ended("", canReconnect: true);
    }

    (string? User, string? Domain, byte[]? Password) IConnecting.SignIn =>
        App.Store.Resolve(Connection).Credential is { } credential ? (credential.User, credential.Domain, credential.Secret) : (null, null, null);

    // ---------- ssh ----------

    async Task ConnectAsync()
    {
        if (!_ready || _connecting) return;
        _connecting = true;
        try
        {
            EndedBar.Visibility = Visibility.Collapsed;
            _passwordGiven = false;
            ShowStatus(Loc.Format("SshConnecting", Connection.Address), busy: true, SymbolRegular.WindowConsole20);
            if (!await App.Sessions.PrepareAsync(this, _closed.Token)) return; // said by Halt
            Start();
        }
        catch (OperationCanceledException) { } // the window was closed meanwhile
        finally { _connecting = false; }
    }

    void Start()
    {
        if (Ssh.Program() is not { } program)
        {
            ShowStatus(Loc.Get("SshMissing"), busy: false);
            return;
        }
        var user = App.Store.Resolve(Connection).Credential?.FullUser;
        if (Ssh.CommandLine(program, Connection.Host, Connection.Port, user) is not { } command)
        {
            ShowStatus(Loc.Format("SshBadAddress", Connection.Address), busy: false);
            return;
        }
        Log.Info($"{Connection.Title}: ssh to {Connection.Address}{(user != null ? " as a saved account" : "")}");
        PseudoConsole console;
        try
        {
            console = PseudoConsole.Start(command, new Dictionary<string, string>
            {
                // ssh asks Sessionwright (this window's pipe) instead of the terminal: see SshAskPass.
                ["SSH_ASKPASS"] = Environment.ProcessPath!, ["SSH_ASKPASS_REQUIRE"] = "prefer", ["DISPLAY"] = "sessionwright",
                [SshAskPass.Variable] = _pipe, ["TERM"] = "xterm-256color",
            }, _size.Columns, _size.Rows);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or EntryPointNotFoundException)
        {
            Log.Error(ex, $"{Connection.Title}: ssh could not be started");
            ShowStatus(Loc.Format("SshNotStarted", ex.Message), busy: false);
            Ended("", canReconnect: true);
            return;
        }
        _console = console;
        ShowTerminal();
        Post(new { o = Convert.ToBase64String("\u001bc"u8) }); // a clean screen: what an earlier ssh said is gone with it
        _ = Task.Run(() => Pump(console));
        _ = WatchAsync(console);
    }

    /// <summary>What ssh prints, on to the page. Bytes as they come: the page puts split characters together.</summary>
    void Pump(PseudoConsole console)
    {
        var buffer = new byte[8192];
        try
        {
            for (int read; (read = console.Output.Read(buffer, 0, buffer.Length)) > 0;)
            {
                var output = Convert.ToBase64String(buffer, 0, read);
                Dispatcher.BeginInvoke(() => Post(new { o = output }));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // the console was closed
    }

    async Task WatchAsync(PseudoConsole console)
    {
        var code = await console.Exit;
        if (_closed.IsCancellationRequested || _console != console) return;
        await Task.Delay(150); // its last lines on the way to the page
        Log.Info($"{Connection.Title}: ssh ended with code {code}");
        _console = null;
        console.Dispose();
        Ended(code == 0 ? Loc.Get("SshEnded") : Loc.Format("SshEndedCode", code), canReconnect: true);
    }

    void Reconnect_Click(object sender, RoutedEventArgs e) => _ = ConnectAsync();

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// What ssh asked through its helper. A host key not seen before (or changed): the user's own yes or no, on OpenSSH's
    /// words. The account's password: the saved one, once; asked again it was wrong, and is asked for. Anything else (a
    /// key's passphrase, a code): asked for. Null declines.
    /// </summary>
    string? Answer(string question)
    {
        if (question.Contains("(yes/no", StringComparison.OrdinalIgnoreCase))
            return Dialog.Confirm(this, question.Trim(), Loc.Get("SshTrustHost"), DialogKind.Warning) ? "yes" : "no";
        if (!_passwordGiven && PasswordQuestion.IsMatch(question) && App.Store.Resolve(Connection).Credential?.Secret is { } secret)
        {
            _passwordGiven = true;
            try { return Store.Unprotect(secret); }
            catch (System.Security.Cryptography.CryptographicException ex) // not readable here, or the master password declined: asked for
            {
                Log.Info($"{Connection.Title}: the saved password could not be used ({ex.GetType().Name})");
            }
        }
        return PasswordPrompt.Ask(this, question.Trim(), confirm: false, button: Loc.Get("Connect"), minLength: 0, label: Loc.Get("Password"));
    }

    static readonly Regex PasswordQuestion = new(@"password:\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---------- Full screen (F11) ----------

    (WindowState State, Rect Bounds, ResizeMode Resize)? _windowed;

    /// <summary>The whole monitor the window is on, without the title bar; F11 again puts it back as it was.</summary>
    void ToggleFullScreen()
    {
        if (_windowed is { } before)
        {
            _windowed = null;
            (Bar.Visibility, ResizeMode, WindowCornerPreference) = (Visibility.Visible, before.Resize, WindowCornerPreference.Round);
            (Left, Top, Width, Height) = (before.Bounds.Left, before.Bounds.Top, before.Bounds.Width, before.Bounds.Height);
            WindowState = before.State;
        }
        else
        {
            var screen = System.Windows.Forms.Screen.FromHandle(Native.Handle(this)).Bounds;
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            _windowed = (WindowState, RestoreBounds, ResizeMode);
            WindowState = WindowState.Normal;
            (Bar.Visibility, ResizeMode, WindowCornerPreference) = (Visibility.Collapsed, ResizeMode.NoResize, WindowCornerPreference.DoNotRound);
            (Left, Top, Width, Height) = (screen.Left / scale, screen.Top / scale, screen.Width / scale, screen.Height / scale);
        }
        Post(new { focus = true });
    }
}

/// <summary>
/// What the preparation before a connect (a VPN or command, the Azure VM started, the machine woken: see
/// <see cref="SessionManager.PrepareAsync"/>) needs of what is being connected: a remote desktop session or a terminal.
/// </summary>
public interface IConnecting
{
    Connection Connection { get; }
    /// <summary>What goes on, shown while waiting.</summary>
    string Message { set; }
    /// <summary>It will not connect, and why.</summary>
    void Halt(string message);
    void Close();
    /// <summary>The account it signs in with, its password still protected: a VPN without one of its own uses the same.</summary>
    (string? User, string? Domain, byte[]? Password) SignIn { get; }
}

/// <summary>Windows' own OpenSSH client and the command line for it.</summary>
static class Ssh
{
    /// <summary>ssh.exe: the one that comes with Windows, else the first on the PATH; null when there is none.</summary>
    public static string? Program()
    {
        var folders = new[] { Path.Combine(Environment.SystemDirectory, "OpenSSH") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var folder in folders)
        {
            try
            {
                if (Path.Combine(folder, "ssh.exe") is var exe && File.Exists(exe)) return exe;
            }
            catch (ArgumentException) { } // a malformed PATH entry
        }
        return null;
    }

    /// <summary>
    /// "ssh -p port [-l user] -- host". Null when the host is no host name or address: it comes from the data (an import,
    /// a team's list), and "--" alone would not stop a name with spaces from being more than one argument.
    /// </summary>
    internal static string? CommandLine(string program, string host, int port, string? user)
    {
        if (!Store.TryParseAddress(host, out var parsed, out _) || parsed != host.Trim().Trim('[', ']') || port is < 1 or > 65535) return null;
        if (user != null && user.Any(ch => char.IsControl(ch) || ch == '"')) return null;
        // An idle session is kept from being dropped by a firewall on the way, and a lost one is noticed.
        var command = new StringBuilder($"\"{program}\" -p {port} -o ServerAliveInterval=30");
        if (!string.IsNullOrWhiteSpace(user)) command.Append($" -l \"{user.TrimEnd('\\')}\"");
        return command.Append($" -- {parsed}").ToString();
    }
}
