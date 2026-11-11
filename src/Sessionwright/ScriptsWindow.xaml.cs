using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>The script library: the scripts every Azure-linked connection offers to run on its machine.</summary>
public partial class ScriptsWindow : FluentWindow
{
    readonly ObservableCollection<RemoteScript> _scripts = new(Scripts.All.Select(s => s.Clone()));

    public ScriptsWindow()
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Title = Bar.Title = Loc.Get("Scripts");
        List.ItemsSource = _scripts;
        List.SelectedItem = _scripts.FirstOrDefault();
        UpdateForm();
        // Edited in place, kept on close. A script without a name gets one; one with nothing in it is dropped.
        Closed += (_, _) =>
        {
            foreach (var s in _scripts.Where(s => string.IsNullOrWhiteSpace(s.Body)).ToList()) _scripts.Remove(s);
            foreach (var s in _scripts.Where(s => string.IsNullOrWhiteSpace(s.Name))) s.Name = Loc.Get("ScriptNew");
            // Left as they came: not kept as the user's own, so a later version's built-in scripts still arrive.
            var untouched = _scripts.Select(Key).SequenceEqual(Scripts.Defaults().Select(Key));
            App.Store.Data.Settings.Scripts = untouched ? null : [.. _scripts];
            try { App.Store.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Saving scripts failed");
                Dialog.Show(Owner, Loc.Format("SaveFailed", ex.Message), DialogKind.Error);
            }
        };
    }

    static (Guid, string, string, bool) Key(RemoteScript s) => (s.Id, s.Name, s.Body, s.Confirm);

    void UpdateForm() => Form.IsEnabled = DeleteButton.IsEnabled = List.SelectedItem != null;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateForm();

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var script = new RemoteScript { Name = Loc.Get("ScriptNew") };
        _scripts.Add(script);
        List.SelectedItem = script;
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not RemoteScript s) return;
        if (!Dialog.Confirm(this, Loc.Format("ConfirmDelete", s.Name), Loc.Get("Delete"), DialogKind.Warning, danger: true)) return;
        var index = _scripts.IndexOf(s);
        _scripts.Remove(s);
        List.SelectedItem = _scripts.ElementAtOrDefault(Math.Min(index, _scripts.Count - 1));
    }

    // The built-in scripts back as they came; the user's own stay.
    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm(this, Loc.Get("ScriptsRestoreConfirm"), Loc.Get("ScriptsRestore"))) return;
        var builtIn = Scripts.Defaults();
        foreach (var s in _scripts.Where(s => builtIn.Any(b => b.Id == s.Id)).ToList()) _scripts.Remove(s);
        for (var i = 0; i < builtIn.Count; i++) _scripts.Insert(i, builtIn[i]);
        List.SelectedItem = _scripts.FirstOrDefault();
    }
}
