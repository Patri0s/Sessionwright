using System.Diagnostics;
using System.IO;
using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

public partial class SettingsWindow : FluentWindow
{
    static AppSettings Settings => App.Store.Data.Settings;
    readonly System.Collections.ObjectModel.ObservableCollection<AzureAccount> _azure = new(Settings.AzureAccounts);

    public SettingsWindow()
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Dialog.FitToScreen(this);
        Title = Bar.Title = Loc.Get("Settings");

        LanguageBox.ItemsSource = new List<Choice> { new("", Loc.Get("FollowSystem")), new("en", "English"), new("pl", "Polski") };
        Choice.Select(LanguageBox, Settings.Language);
        ThemeBox.ItemsSource = new[] { "System", "Light", "Dark" }.Select(t => new Choice(t, Loc.Get($"Theme{t}"))).ToList();
        Choice.Select(ThemeBox, Settings.Theme);
        ProbeBox.IsChecked = Settings.ProbeHosts;
        AccentBox.IsChecked = Settings.UseSystemAccent;
        SkipConfirmBox.IsChecked = Settings.SkipSessionConfirmations;
        HotkeyBox.Text = Settings.QuickConnectKey;
        TrayBox.IsChecked = Settings.RunInTray;
        AzureList.ItemsSource = _azure;
        FeedBox.Text = Settings.UpdateFeed ?? "";
        VersionText.Text = Loc.Format("VersionInfo", Updater.CurrentVersion);
        FeedHint.Text = !Updater.IsInstalled ? Loc.Get("UpdatesNotInstalled")
            : Settings.UpdateFeed == null && Updater.Feed is { } builtIn ? Loc.Format("UpdateFeedDefault", builtIn) : "";
        FeedHint.Visibility = FeedHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = Updater.IsInstalled;
        Options.Load(Settings.Rdp);
        PathText.Text = App.Store.Path;
        _apply.Tick += (_, _) => Apply();
        Loaded += (_, _) => _loaded = true;
        // Whatever was typed last (the timer may not have run yet) counts on close too.
        Closing += (_, _) => Apply();
    }

    // Changes apply by themselves, a moment after the last one (typing an address is many changes).
    readonly System.Windows.Threading.DispatcherTimer _apply = new() { Interval = TimeSpan.FromMilliseconds(400) };
    bool _loaded;

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(App.Store.Path)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Log.File)) Process.Start(new ProcessStartInfo(Log.File) { UseShellExecute = true });
    }

    void ReportProblem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Sessionwright-{Loc.Get("ReportFileName")}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = Loc.Get("ReportFilter"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName); // the dialog already asked to replace it
            ProblemReport.Save(dialog.FileName);
            Log.Info($"Problem report saved to {dialog.FileName}");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Error(ex, "Problem report");
            Dialog.Show(this, Loc.Format("ReportFailed", ex.Message), DialogKind.Error);
        }
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        // Checks the feed typed here, before saving (empty: the built-in one). Passed along, the saved setting untouched:
        // swapped in and put back afterwards, it undid a Save pressed while the check ran.
        var feed = FeedBox.Text.Trim() is { Length: > 0 } typed ? typed : Updater.BuiltInFeed;
        CheckButton.IsEnabled = false;
        CheckResult.Text = Loc.Get("CheckingForUpdates");
        try
        {
            CheckResult.Text = feed != null && !Updater.IsTrusted(feed) ? Loc.Get("UpdateFeedInsecure")
                : !Updater.IsInstalled || feed == null ? Loc.Get("UpdateFeedMissing")
                : await Updater.CheckAsync(feed: feed) is { } update ? Loc.Format("UpdateAvailable", update.TargetFullRelease.Version)
                : Loc.Get("UpToDate");
        }
        finally { CheckButton.IsEnabled = true; }
    }

    void WhatsNew_Click(object sender, RoutedEventArgs e) => WhatsNewWindow.ShowAll(this);

    // A field, a list or a check box changed (not the section picked on the left, nor a plain button).
    void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || e.OriginalSource is System.Windows.Controls.TabControl
            || e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase and not System.Windows.Controls.Primitives.ToggleButton) return;
        _apply.Stop();
        _apply.Start();
    }

    /// <summary>
    /// Every setting as the window shows it now, saved and in effect at once (the theme too; what the main window owns
    /// - the tray icon, the quick-connect key - it takes up when this window closes). A value that cannot be used (a
    /// screen size out of range, a plain http feed) is said under the sections and left as it was.
    /// </summary>
    void Apply()
    {
        _apply.Stop();
        if (!_loaded || App.IsExiting) return;
        var error = "";
        if (Options.Read() is { } rdp) Settings.Rdp = rdp;
        else error = Loc.Get("InvalidSize");
        if (Updater.IsTrusted(FeedBox.Text)) Settings.UpdateFeed = FeedBox.Text.Trim() is { Length: > 0 } feed ? feed : null;
        else error = Loc.Get("UpdateFeedInsecure");
        ErrorText.Text = error;

        var language = (string)Choice.Get(LanguageBox)!;
        var languageChanged = language != Settings.Language;
        var themeChanged = (string)Choice.Get(ThemeBox)! != Settings.Theme || (AccentBox.IsChecked == true) != Settings.UseSystemAccent;
        Settings.Language = language;
        Settings.Theme = (string)Choice.Get(ThemeBox)!;
        Settings.ProbeHosts = ProbeBox.IsChecked == true;
        Settings.UseSystemAccent = AccentBox.IsChecked == true;
        Settings.SkipSessionConfirmations = SkipConfirmBox.IsChecked == true;
        Settings.QuickConnectKey = HotkeyBox.Text;
        Settings.RunInTray = TrayBox.IsChecked == true;
        foreach (var gone in Settings.AzureAccounts.Except(_azure)) AzureVms.Forget(gone);
        Settings.AzureAccounts = [.. _azure];
        if (!TrySave()) return;
        if (themeChanged && Owner != null) App.ApplyTheme(Owner);

        if (!languageChanged) return;
        // A restart would drop open sessions without asking: with sessions, the language waits for a restart later.
        if (App.Sessions.Open.Count > 0) Dialog.Show(this, Loc.Get("RestartForLanguage"));
        else if (Dialog.Confirm(this, Loc.Get("RestartForLanguageNow"), Loc.Get("RestartNow"), cancelText: Loc.Get("Later")))
        {
            Process.Start(Environment.ProcessPath!, [App.RestartArgument, Environment.ProcessId.ToString()]);
            App.IsExiting = true;
            Application.Current.Shutdown();
        }
    }

    // The box takes the combination pressed in it; Tab alone still moves on.
    void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (key == System.Windows.Input.Key.Tab && mods is System.Windows.Input.ModifierKeys.None or System.Windows.Input.ModifierKeys.Shift) return;
        e.Handled = true;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt
            or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
        if ((mods & (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Windows)) == 0)
        {
            HotkeyHint.Text = Loc.Get("QuickConnectKeyNeedsModifier");
            return;
        }
        try
        {
            HotkeyBox.Text = SessionKeys.FormatKey(new System.Windows.Input.KeyGesture(key, mods));
            HotkeyHint.Text = Loc.Get("QuickConnectKeyHint");
        }
        catch (NotSupportedException) { HotkeyHint.Text = Loc.Get("QuickConnectKeyNeedsModifier"); }
    }

    void HotkeyClear_Click(object sender, RoutedEventArgs e) => HotkeyBox.Text = ""; // a text change: applied like one

    void HotkeyBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => SessionKeys.SuspendQuickConnect(true);

    // Also when the window closes with the box focused (it loses the focus then).
    void HotkeyBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => SessionKeys.SuspendQuickConnect(false);

    // Signing in happens in the browser; the account is kept at once.
    async void AddAzureAccount_Click(object sender, RoutedEventArgs e)
    {
        AddAzureButton.IsEnabled = false;
        AzureStatus.Text = Loc.Get("AzureSignInBrowser");
        try
        {
            var account = await AzureVms.SignInAsync(Native.Handle(this));
            if (_azure.FirstOrDefault(a => string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase)) is { } same)
                _azure.Remove(same); // signed in again: the new record replaces the old one
            _azure.Add(account);
            AzureVms.Forget(account);
            // Kept at once, also without Save: a sign-in done in the browser should not be lost by closing the window.
            Settings.AzureAccounts.RemoveAll(a => string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));
            Settings.AzureAccounts.Add(account);
            App.Store.Save();
            AzureStatus.Text = Loc.Format("AzureSignedIn", account.Username);
        }
        catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException or OperationCanceledException)
        {
            Log.Error(ex, "Azure sign-in");
            AzureStatus.Text = Loc.Format("AzureSignInFailed", ex.Message);
        }
        finally { AddAzureButton.IsEnabled = true; }
    }

    void RemoveAzureAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AzureAccount account) return;
        _azure.Remove(account);
        Apply();
    }

    bool TrySave()
    {
        try
        {
            App.Store.Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving settings failed");
            Dialog.Show(this, Loc.Format("SaveFailed", ex.Message), DialogKind.Error);
            return false;
        }
    }
}
