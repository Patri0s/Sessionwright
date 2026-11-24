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
    readonly HashSet<int> _children = []; // the ssh and scp this window started: only their questions are answered

    public Connection Connection { get; }

    internal SshWindow(Connection c)
    {
        Connection = c;
        InitializeComponent();
        DataContext = this; // the connection's color in the strip under the title bar
        UpdateTitle();
        c.LastUsed = DateTime.Now;
        c.PropertyChanged += OnConnectionChanged;
        Loaded += async (_, _) => await StartViewAsync();
        _ = SshAskPass.ServeAsync(_pipe, Answer, () => _children, _closed.Token); // for as long as the window is there, over every reconnect
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
        if (e.PropertyName is nameof(Connection.Title) or nameof(Connection.Host) or nameof(Connection.Port)) UpdateTitle();
    }

    /// <summary>The title as a session window has it (name, then its groups), and who signs in where next to the buttons.</summary>
    void UpdateTitle()
    {
        var path = App.Sessions.GroupPath(Connection);
        Title = Bar.Title = path.Length == 0 ? Connection.Title : $"{Connection.Title} — {path}";
        var user = App.Store.Resolve(Connection).Credential?.FullUser;
        WhereText.Text = string.IsNullOrWhiteSpace(user) ? Connection.Address : $"{user}@{Connection.Address}";
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

    void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent) => SendTheme();

    /// <summary>The colors, the scroll bar's shade and the text size the user chose, to the page.</summary>
    internal void SendTheme()
    {
        var light = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light;
        // Light text on a dark page is drawn heavier than dark text on a light one: a lighter cut of the font there.
        Post(new
        {
            theme = Theme(light), thumb = light ? "rgba(0, 0, 0, .3)" : "rgba(255, 255, 255, .28)", weight = light ? "400" : "300",
            fontSize = App.Store.Data.Settings.TerminalFontSize ?? 14,
        });
    }

    /// <summary>
    /// The terminal's colors in the app's theme, on the session stage's color (the window around it has the same): the
    /// One Half palettes, as in Windows Terminal, each made for its page (xterm's own yellow and white are unreadable on
    /// a light one, its blue on a dark one). The cursor and the selection in the app's accent.
    /// </summary>
    static object Theme(bool light)
    {
        var accent = (Application.Current.TryFindResource("AccentFillColorDefaultBrush") as System.Windows.Media.SolidColorBrush)?.Color
                     ?? System.Windows.Media.Color.FromRgb(0x2B, 0x8A, 0xF2);
        var (cursor, selection) = ($"#{accent.R:X2}{accent.G:X2}{accent.B:X2}", $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}59");
        return light
        ? new
        {
            background = "#F3F3F3", foreground = "#383A42", cursor, selectionBackground = selection,
            black = "#383A42", red = "#E45649", green = "#50A14F", yellow = "#C18301", blue = "#0184BC", magenta = "#A626A4", cyan = "#0997B3", white = "#A0A1A7",
            brightBlack = "#4F525D", brightRed = "#DF6C75", brightGreen = "#98C379", brightYellow = "#E4C07A", brightBlue = "#61AFEF",
            brightMagenta = "#C577DD", brightCyan = "#56B5C1", brightWhite = "#FFFFFF",
        }
        : new
        {
            background = "#1C1C1C", foreground = "#D4D7DD", cursor, selectionBackground = selection,
            black = "#3B4048", red = "#E06C75", green = "#98C379", yellow = "#E5C07B", blue = "#61AFEF", magenta = "#C678DD", cyan = "#56B6C2", white = "#DCDFE4",
            brightBlack = "#6B7385", brightRed = "#EA868F", brightGreen = "#ADD495", brightYellow = "#EDCF98", brightBlue = "#82C1F5",
            brightMagenta = "#D493E6", brightCyan = "#74C8D3", brightWhite = "#FFFFFF",
        };
    }

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
                SendTheme();
                ShowFacts();
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
                ShowFacts();
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
            case "switch":
                App.Sessions.SwitchFrom(this, root.GetProperty("n").GetInt32());
                break;
            case "switcher":
                SessionSwitcher.Toggle(App.Sessions, this);
                break;
            case "font":
                // Kept for every terminal, as the user set it; the usual size is no setting at all.
                App.Store.Data.Settings.TerminalFontSize = root.GetProperty("n").GetInt32() is var size && size != 14 ? size : null;
                try { App.Store.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"The terminal's text size was not saved: {ex.Message}"); }
                foreach (var other in App.Sessions.Terminals.Where(t => t != this)) other.SendTheme();
                break;
            case "link" when Text() is { } url:
                LinkMenu.OpenHere(url); // web addresses only (Links.IsSafe)
                break;
            case "files":
                _ = SendFilesAsync([.. e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => p.Length > 0)]);
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
            // Reconnect from the title bar while it runs: the running ssh goes first (its end is then not reported).
            if (_console is { } running)
            {
                _console = null;
                running.Dispose();
            }
            (_connectedAt, _latency) = (null, null);
            ShowFacts();
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
        _children.Add(console.ProcessId);
        _connectedAt = StartedAt = DateTime.Now;
        ShowFacts();
        _ = MeasureAsync();
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
        (_connectedAt, _latency) = (null, null);
        ShowFacts();
        Ended(code == 0 ? Loc.Get("SshEnded") : Loc.Format("SshEndedCode", code), canReconnect: true);
    }

    void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        // While ssh runs, a new one ends it and what runs in it: asked first, the button being one click from Full screen.
        if (_console != null && !App.Store.Data.Settings.SkipSessionConfirmations
            && !Dialog.Confirm(this, Loc.Format("SshConfirmReconnect", Connection.Title), Loc.Get("Reconnect"), DialogKind.Warning)) return;
        _ = ConnectAsync();
    }

    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ---------- The line under the terminal: since when, its size, how far the server ----------

    DateTime? _connectedAt;
    int? _latency;
    /// <summary>When its ssh was last started; null before the first.</summary>
    internal DateTime? StartedAt { get; private set; }

    void ShowFacts()
    {
        StateText.Text = _connectedAt is { } at ? Loc.Format("ConnectedSince", at.ToString("t")) : "";
        FactsText.Text = $"{_size.Columns}×{_size.Rows}" + (_latency is { } ms ? $"  ·  {ms} ms" : "");
    }

    /// <summary>The time to the server's port now and every half minute while this ssh runs.</summary>
    async Task MeasureAsync()
    {
        var console = _console;
        try
        {
            while (console != null && _console == console)
            {
                var ms = await RdpSession.MeasureRttAsync(Connection.Host, Connection.Port);
                if (_console != console) return;
                _latency = ms;
                ShowFacts();
                await Task.Delay(Timings.LatencyProbe, _closed.Token);
            }
        }
        catch (OperationCanceledException) { } // the window was closed
    }

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
        if (!_passwordGiven && Ssh.AsksPasswordOf(question, Connection.Host) && App.Store.Resolve(Connection).Credential?.Secret is { } secret)
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

    // ---------- Files dropped on the terminal ----------

    /// <summary>
    /// Copies files and folders to the account's home folder on the server with scp.exe, which signs in as ssh does
    /// (the same keys, the saved password through this window). Said when done, and when not, with scp's own words.
    /// </summary>
    async Task SendFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        var user = App.Store.Resolve(Connection).Credential?.FullUser;
        var scp = Ssh.Program("scp.exe");
        if (scp == null || Ssh.ScpArguments(Connection.Host, Connection.Port, user, files) is not { } arguments)
        {
            Notification.Problem(this, Loc.Format("ScpFailedTitle", Connection.Title), Loc.Get(scp == null ? "SshMissing" : "ScpBadTarget"));
            return;
        }
        Log.Info($"{Connection.Title}: copying {files.Count} dropped item(s) to the server with scp");
        var progress = Notification.Show(this, SymbolRegular.ArrowUpload24, Loc.Format("ScpSendingTitle", files.Count, Connection.Title), Loc.Get("ScpSending"));
        _passwordGiven = false; // scp signs in anew: the saved password once more
        string errors;
        int code;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(scp) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            // No terminal for it to ask in: every question goes to this window (see SshAskPass).
            (start.Environment["SSH_ASKPASS"], start.Environment["SSH_ASKPASS_REQUIRE"], start.Environment["DISPLAY"]) = (Environment.ProcessPath!, "force", "sessionwright");
            start.Environment[SshAskPass.Variable] = _pipe;
            using var process = System.Diagnostics.Process.Start(start)!;
            _children.Add(process.Id);
            var output = process.StandardOutput.ReadToEndAsync(_closed.Token);
            errors = await process.StandardError.ReadToEndAsync(_closed.Token);
            await output;
            await process.WaitForExitAsync(_closed.Token);
            code = process.ExitCode;
        }
        catch (OperationCanceledException) { return; } // the window was closed
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            (errors, code) = (ex.Message, -1);
        }
        finally { progress.Close(); }
        Log.Info($"{Connection.Title}: scp ended with code {code}");
        if (code == 0) Notification.Show(this, SymbolRegular.CheckmarkCircle24, Loc.Format("ScpDoneTitle", files.Count, Connection.Title), Loc.Get("ScpDone"), TimeSpan.FromSeconds(8));
        else Notification.Problem(this, Loc.Format("ScpFailedTitle", Connection.Title), errors.Trim().Split('\n')[^1].Trim() is { Length: > 0 } last ? last : Loc.Format("SshEndedCode", code));
    }


    // ---------- Full screen (F11) ----------

    (WindowState State, Rect Bounds, ResizeMode Resize)? _windowed;

    /// <summary>The whole monitor the window is on, without the title bar; F11 again puts it back as it was.</summary>
    void ToggleFullScreen()
    {
        if (_windowed is { } before)
        {
            _windowed = null;
            (Bar.Visibility, ResizeMode, WindowCornerPreference) = (Visibility.Visible, before.Resize, WindowCornerPreference.Round);
            StatusBar.Visibility = EnvStrip.Visibility = Visibility.Visible;
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
            StatusBar.Visibility = EnvStrip.Visibility = Visibility.Collapsed; // the whole monitor is the terminal's
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
    /// <summary>ssh.exe (or scp.exe): the one that comes with Windows, else the first on the PATH; null when there is none.</summary>
    public static string? Program(string name = "ssh.exe")
    {
        var folders = new[] { Path.Combine(Environment.SystemDirectory, "OpenSSH") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var folder in folders)
        {
            try
            {
                if (Path.Combine(folder, name) is var exe && File.Exists(exe)) return exe;
            }
            catch (ArgumentException) { } // a malformed PATH entry
        }
        return null;
    }

    /// <summary>
    /// "ssh -p port [-l user] -- host". Null when the host is no host name or address: it comes from the data (an import,
    /// a team's list), and "--" alone would not stop a name with spaces from being more than one argument.
    /// </summary>
    /// <summary>
    /// scp's arguments for copying <paramref name="files"/> (folders too: -r) to the home folder of the account on the
    /// server: "-P port -r -- file… [user@]host:". Null when the host is no host name or address.
    /// </summary>
    internal static List<string>? ScpArguments(string host, int port, string? user, IReadOnlyList<string> files)
    {
        if (!Store.TryParseAddress(host, out var parsed, out _) || parsed != host.Trim().Trim('[', ']') || port is < 1 or > 65535) return null;
        if (user != null && user.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '"' or ':' or '@')) return null;
        var target = (string.IsNullOrWhiteSpace(user) ? "" : user + "@") + (parsed.Contains(':') ? $"[{parsed}]" : parsed) + ":";
        return ["-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-r", "--", .. files, target];
    }

    /// <summary>
    /// ssh asks for the password of an account on <paramref name="host"/> ("user@host's password:", or "(user@host)
    /// Password:" when the server asks itself). Another host's question, a jump host's from ~/.ssh/config, is not it: the
    /// connection's saved password is not that machine's.
    /// </summary>
    internal static bool AsksPasswordOf(string question, string host) =>
        host.Trim().Trim('[', ']') is { Length: > 0 } name
        && Regex.IsMatch(question, $@"@{Regex.Escape(name)}['\)].*password:\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

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
