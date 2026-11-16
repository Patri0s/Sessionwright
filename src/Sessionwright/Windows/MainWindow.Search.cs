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

// The palette's rows: connections, commands and quick connect. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Search / quick connect ----------

    const int MaxMatches = 50;

    /// <summary>
    /// The palette's rows for the text typed: connections, commands, quick connect. Within <paramref name="scope"/>
    /// (Tab on a connection's row): what can be done with that one.
    /// </summary>
    internal List<object> PaletteResults(string text, Connection? scope)
    {
        var q = text.Trim();
        if (scope != null) return [.. Matching(ActionsOf(scope), q)];
        if (q.StartsWith('>')) return [.. Matching(Commands(), q[1..])]; // ">" alone: every command
        // Nothing typed: where the user most likely goes next, the open sessions and then the connections used last.
        if (q.Length == 0)
            return [.. Sessions.Open.Where(s => s.IsOpen).Select(s => s.Connection)
                .Concat(Store.Data.Connections.Where(c => c.LastUsed != null).OrderByDescending(c => c.LastUsed)).Distinct().Take(8)];
        var matches = Store.Search(q).Take(MaxMatches).ToList();
        var commands = Matching(Commands(), q).Take(5);

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
        return results;
    }

    static IEnumerable<SearchAction> Matching(IEnumerable<SearchAction> actions, string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Palette.Fold).ToList();
        return actions.Where(a => tokens.All(t => Palette.Fold(a.Name).Contains(t, StringComparison.Ordinal)));
    }

    /// <summary>What can be done with one connection right now, as the commands typed in words would offer it.</summary>
    IEnumerable<SearchAction> ActionsOf(Connection c)
    {
        var kind = Loc.Get("Command");
        foreach (var verb in Enum.GetValues<PaletteVerb>())
            if (verb != PaletteVerb.Link && Applies(verb, c))
                yield return new(Loc.Format($"Palette{verb}", c.Title), VerbIcon(verb), () => Run(verb, c), kind, Dangerous(verb));
        foreach (var l in Store.LinksOf(c))
        {
            yield return new(Loc.Format("PaletteLinkHere", l.Name, c.Title), SymbolRegular.Globe24, () => LinkMenu.OpenHere(l.Url), kind);
            yield return new(Loc.Format("PaletteLinkSession", l.Name, c.Title), SymbolRegular.Desktop24, () => _ = Sessions.OpenLinkInSessionAsync(c, l.Url), kind);
        }
        // The rest of its menu (delete, move, scripts) is one step away. Not for a quick connection: it is not in the list.
        if (Store.Data.Connections.Contains(c))
            yield return new(Loc.Format("PaletteShowInList", c.Title), SymbolRegular.List24, () => ShowInList(c), kind);
    }

    void ShowInList(Connection c)
    {
        ShowFromTray();
        LeaveFavorites();
        RebuildTree(c);
    }

    static bool Dangerous(PaletteVerb verb) => verb is PaletteVerb.ShutDown or PaletteVerb.Restart or PaletteVerb.SignOut or PaletteVerb.Disconnect;

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
            .Select(c => new SearchAction(Loc.Format($"Palette{verb}", c.Title), VerbIcon(verb), () => Run(verb, c), kind, Dangerous(verb)));
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

    /// <summary>Runs a row of the palette, which has closed by now.</summary>
    internal void RunPaletteResult(object item)
    {
        ShowForQuestions();
        if (item is Connection c) Connect(c);
        else if (item is SearchAction a) a.Run();
    }

    /// <summary>Connects to the address typed into the palette when nothing was found for it.</summary>
    internal void QuickConnectTyped(string text)
    {
        ShowForQuestions();
        QuickConnect(text);
    }

    // What the palette runs may ask something (a credential, "sign out?"), and a question owned by a hidden or
    // minimized window would not show. A window that is merely behind (the palette opened from a session) stays there.
    void ShowForQuestions()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) ShowFromTray();
    }

    /// <summary>The palette over <paramref name="over"/> (a session window, the main window; null: where the pointer is).</summary>
    public void OpenPalette(Window? over)
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return; // a dialog waits for its answer first
        PaletteWindow.Toggle(this, over);
    }

    void Palette_Click(object sender, RoutedEventArgs e) => OpenPalette(this);
}
