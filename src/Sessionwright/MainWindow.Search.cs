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

// The search box: connections, commands and quick connect. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
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
}
