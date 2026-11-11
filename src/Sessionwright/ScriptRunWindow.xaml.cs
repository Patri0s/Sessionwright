using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// One run of a library script on a machine: says that it runs (Azure takes up to a minute), then shows what the
/// script wrote and its errors. Not a dialog: the work elsewhere goes on while it runs. Closing it stops waiting for
/// the answer; what already runs on the machine is not stopped by that.
/// </summary>
public partial class ScriptRunWindow : FluentWindow
{
    readonly Connection _connection;
    readonly RemoteScript _script;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch _running = new();
    CancellationTokenSource? _cancel;

    public ScriptRunWindow(Connection connection, RemoteScript script)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        (_connection, _script) = (connection, script);
        Title = Bar.Title = $"{script.Name} — {connection.Title}";
        _clock.Tick += (_, _) => ShowRunning();
        Loaded += (_, _) => _ = RunAsync();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _cancel?.Cancel();
        };
    }

    void ShowRunning() => StatusText.Text = Loc.Format("ScriptRunning", _connection.AzureVm?.Name, (int)_running.Elapsed.TotalSeconds);

    async Task RunAsync()
    {
        if (_connection.AzureVm is not { } vm) return;
        var cancel = (_cancel = new CancellationTokenSource()).Token;
        AgainButton.IsEnabled = CopyButton.IsEnabled = false;
        FixButton.Visibility = Visibility.Collapsed;
        OutputBox.TextWrapping = TextWrapping.NoWrap; // a script's columns stay columns
        OutputBox.Text = "";
        (Ring.Visibility, Glyph.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        _running.Restart();
        ShowRunning();
        _clock.Start();
        Log.Info($"Azure: script \"{_script.Name}\" on {vm.Name}");
        try
        {
            var (output, errors) = await AzureVms.RunScriptAsync(vm, _script.Body, cancel);
            OutputBox.Text = Text(output, errors);
            Done(errors.Length > 0 ? SymbolRegular.Warning24 : SymbolRegular.CheckmarkCircle24,
                Loc.Format(errors.Length > 0 ? "ScriptDoneErrors" : "ScriptDone", vm.Name, (int)_running.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } // the window was closed
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or AzureSignInRequiredException or TaskCanceledException
                                       or System.Text.Json.JsonException)
        {
            Log.Error(ex, $"Azure: script \"{_script.Name}\" on {vm.Name}");
            // A sentence, not columns: wrapped, so its end (where to go) is not cut off behind a scroll bar.
            OutputBox.TextWrapping = TextWrapping.Wrap;
            OutputBox.Text = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => Loc.Get("ScriptNoRights"),
                // Azure runs one command at a time on a machine; seen live: a command that hangs blocks every next one.
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict } when ex.Message.Contains("in progress", StringComparison.OrdinalIgnoreCase)
                    => Loc.Get("ScriptBusy") + Environment.NewLine + Environment.NewLine + ex.Message,
                _ => ex.Message,
            };
            Done(SymbolRegular.ErrorCircle24, Loc.Format("ScriptFailed", vm.Name));
            // The two failures with a way out: the account is not in Sessionwright (add it), or has to sign in again.
            var account = AzureVms.Account(vm);
            _fix = account == null ? OpenAccounts : ex is AzureSignInRequiredException ? () => SignInAgainAsync(account, vm.TenantId) : null;
            FixButton.Content = Loc.Get(account == null ? "ScriptAddAccount" : "AzureSignInAgain");
            FixButton.Visibility = _fix != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>What the script wrote, then its errors under a line of their own; a word when it wrote nothing.</summary>
    internal static string Text(string output, string errors)
    {
        output = output.TrimEnd();
        if (errors.Length == 0) return output.Length > 0 ? output : Loc.Get("ScriptNoOutput");
        return (output.Length > 0 ? output + Environment.NewLine + Environment.NewLine : "") + Loc.Get("ScriptErrors") + Environment.NewLine + errors;
    }

    void Done(SymbolRegular icon, string text)
    {
        _clock.Stop();
        _running.Stop();
        if (!IsLoaded) return;
        (Ring.Visibility, Glyph.Visibility) = (Visibility.Collapsed, Visibility.Visible);
        Glyph.Symbol = icon;
        Glyph.SetResourceReference(ForegroundProperty, icon switch
        {
            SymbolRegular.CheckmarkCircle24 => "SystemFillColorSuccessBrush",
            SymbolRegular.Warning24 => "SystemFillColorCautionBrush",
            _ => "SystemFillColorCriticalBrush",
        });
        StatusText.Text = text;
        AgainButton.IsEnabled = CopyButton.IsEnabled = true;
    }

    // Asked again for a script that changes something: "run again" is as easy to hit as the first time.
    void Again_Click(object sender, RoutedEventArgs e)
    {
        if (_script.Confirm && !Dialog.Confirm(this, Loc.Format("ScriptConfirm", _script.Name, _connection.AzureVm?.Name), Loc.Get("ScriptRun"), DialogKind.Warning)) return;
        _ = RunAsync();
    }

    Func<Task>? _fix;

    async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (_fix is not { } fix) return;
        FixButton.IsEnabled = false;
        try
        {
            await fix();
            if (IsLoaded && AzureVms.Account(_connection.AzureVm!) != null) _ = RunAsync(); // signed in: the script right away
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or AzureSignInRequiredException or OperationCanceledException
                                       or Azure.Identity.AuthenticationFailedException)
        {
            Log.Info($"Azure: signing in for a script failed: {ex.Message}");
            StatusText.Text = ex.Message;
        }
        finally { FixButton.IsEnabled = true; }
    }

    Task OpenAccounts()
    {
        (Application.Current.MainWindow as MainWindow)?.OpenSettings();
        return Task.CompletedTask;
    }

    Task SignInAgainAsync(AzureAccount account, string? tenant) =>
        AzureVms.ReauthenticateAsync(account, tenant, Native.Handle(this), default);

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(OutputBox.Text); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Log.Info($"Clipboard busy: {ex.Message}"); } // another program holds it
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
