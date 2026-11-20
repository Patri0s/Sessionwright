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

// Import, export, the windows opened from the toolbar. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Tools ----------

    void Credentials_Click(object sender, RoutedEventArgs e)
    {
        new CredentialsWindow { Owner = this }.ShowDialog();
        Refresh();
    }

    /// <summary>The quick-connect key: the palette over the window the user is in, a session included (pressed again: closes it).</summary>
    void ShowQuickConnect() => OpenPalette(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive));

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

    // Open sessions keep their own copy of the password; the next use of a saved one asks for the master password.
    void LockPasswords_Click(object sender, RoutedEventArgs e)
    {
        if (MasterPassword.Unlocked) MasterPassword.Lock(Store.Data);
        else MasterPassword.Ask?.Invoke();
    }

    /// <summary>The lock in the title bar: there with a master password, open while its key is in memory.</summary>
    void ShowLock()
    {
        var open = MasterPassword.Unlocked;
        LockButton.Visibility = Store.Data.Settings.Master != null ? Visibility.Visible : Visibility.Collapsed;
        LockButton.Icon = new SymbolIcon { Symbol = open ? SymbolRegular.LockOpen24 : SymbolRegular.LockClosed24, FontSize = 16 };
        LockButton.ToolTip = Loc.Get(open ? "MasterLockTip" : "MasterUnlockTip");
        System.Windows.Automation.AutomationProperties.SetName(LockButton, (string)LockButton.ToolTip);
    }

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
        // In a team's shared list: into the user's own group around it, as a new connection goes (the list's groups are
        // not saved, and what was put in them would land at the top level at the next start).
        var target = Store.OwnGroup(Tree.SelectedItem as Group);
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
        MasterPassword.TryOpenNotes(Store.Data); // an export carries the notes; without the master password, those it can read
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
}
