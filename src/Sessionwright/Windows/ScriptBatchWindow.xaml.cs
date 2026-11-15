using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// One library script run on several machines at once: a row per machine with where it stands and the first line it
/// wrote, the picked machine's whole output below. Not a dialog: the work elsewhere goes on while it runs. Closing it
/// stops waiting; what already runs on the machines is not stopped by that.
/// </summary>
public partial class ScriptBatchWindow : FluentWindow
{
    readonly RemoteScript _script;
    readonly ObservableCollection<ScriptRunRow> _rows;
    CancellationTokenSource? _cancel;

    public ScriptBatchWindow(IReadOnlyList<Connection> connections, RemoteScript script)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        _script = script;
        _rows = [.. connections.Select(c => new ScriptRunRow { Connection = c })];
        Title = Bar.Title = Loc.Format("ScriptBatchTitle", script.Name, _rows.Count);
        List.ItemsSource = _rows;
        OutputBox.Text = Loc.Get("ScriptBatchPick");
        Loaded += (_, _) => _ = RunAsync();
        Closed += (_, _) => _cancel?.Cancel();
    }

    async Task RunAsync()
    {
        var cancel = (_cancel = new CancellationTokenSource()).Token;
        AgainButton.IsEnabled = CopyButton.IsEnabled = false;
        Ring.Visibility = Visibility.Visible;
        foreach (var row in _rows) (row.State, row.Summary, row.Output, row.Time) = (ScriptRunState.Waiting, "", "", "");
        ShowStatus();
        Log.Info($"Script \"{_script.Name}\" on {_rows.Count} machines");

        // The sign-ins first, here: a master password is asked for once, before anything runs, not from the middle of it.
        var signIns = new Dictionary<ScriptRunRow, (string? User, string? Password)>();
        foreach (var row in _rows)
        {
            try { signIns[row] = Scripts.SignIn(row.Connection); }
            catch (System.Security.Cryptography.CryptographicException ex) { Fail(row, ex.Message, ex.Message); }
        }

        using var gate = new SemaphoreSlim(ScriptBatch.AtOnce);
        await Task.WhenAll(_rows.Where(signIns.ContainsKey).Select(async row =>
        {
            try { await gate.WaitAsync(cancel); }
            catch (OperationCanceledException) { return; }
            var clock = Stopwatch.StartNew();
            try
            {
                row.State = ScriptRunState.Running;
                ShowStatus();
                var (output, errors) = await Scripts.RunOnAsync(row.Connection, _script, signIns[row], cancel);
                row.Output = ScriptRunWindow.Text(output, errors);
                row.Summary = ScriptBatch.Summary(output.Trim().Length > 0 ? output : errors);
                row.State = errors.Length > 0 ? ScriptRunState.DoneWithErrors : ScriptRunState.Done;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } // the window was closed
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or AzureSignInRequiredException or TaskCanceledException
                                           or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                var target = Scripts.Target(row.Connection);
                Log.Info($"Script \"{_script.Name}\" did not run on {target}: {ex.Message.Split('\n')[0].Trim()}");
                if (ex is RemotingFailedException remoting)
                    Fail(row, Loc.Format("ScriptNotReached", target), Loc.Format("ScriptNotReached", target) + Environment.NewLine + Environment.NewLine + remoting.Hint
                        + Environment.NewLine + Environment.NewLine + Loc.Get("ScriptDetailsHeader") + Environment.NewLine + remoting.Details);
                else Fail(row, ScriptBatch.Summary(ex.Message), ex.Message);
            }
            finally
            {
                row.Time = $"{(int)clock.Elapsed.TotalSeconds} s";
                gate.Release();
                if (!cancel.IsCancellationRequested) ShowStatus();
            }
        }));
        if (cancel.IsCancellationRequested || !IsLoaded) return;
        Ring.Visibility = Visibility.Collapsed;
        AgainButton.IsEnabled = CopyButton.IsEnabled = true;
        ShowStatus();
        ShowOutput();
    }

    static void Fail(ScriptRunRow row, string summary, string output) =>
        (row.State, row.Summary, row.Output) = (ScriptRunState.Failed, summary, output);

    void ShowStatus()
    {
        int Count(ScriptRunState state) => _rows.Count(r => r.State == state);
        var left = Count(ScriptRunState.Waiting) + Count(ScriptRunState.Running);
        StatusText.Text = left > 0
            ? Loc.Format("ScriptBatchRunning", _rows.Count, _rows.Count - left)
            : Loc.Format("ScriptBatchDone", Count(ScriptRunState.Done), Count(ScriptRunState.DoneWithErrors), Count(ScriptRunState.Failed));
    }

    void ShowOutput()
    {
        var row = List.SelectedItem as ScriptRunRow;
        // A script's columns stay columns; why it did not run is sentences, wrapped so their end is not behind a scroll bar.
        OutputBox.TextWrapping = row?.State == ScriptRunState.Failed ? TextWrapping.Wrap : TextWrapping.NoWrap;
        OutputBox.Text = row is { Output.Length: > 0 } ? row.Output : Loc.Get("ScriptBatchPick");
    }

    void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowOutput();

    // Asked again for a script that changes something: "run again" is as easy to hit as the first time.
    void Again_Click(object sender, RoutedEventArgs e)
    {
        if (_script.Confirm && !Dialog.Confirm(this, Loc.Format("ScriptConfirmMany", _script.Name, _rows.Count), Loc.Get("ScriptRun"), DialogKind.Warning)) return;
        _ = RunAsync();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ScriptBatch.Report(_script.Name, _rows)); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Log.Info($"Clipboard busy: {ex.Message}"); } // another program holds it
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
