using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// One run of a library script on a machine (through Azure when it is linked to a VM, over PowerShell remoting
/// otherwise): says that it runs (Azure takes up to a minute), then shows what the
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

    string Target => Scripts.Target(_connection);

    void ShowRunning() => StatusText.Text =
        Loc.Format(_connection.AzureVm != null ? "ScriptRunning" : "ScriptRunningRemote", Target, (int)_running.Elapsed.TotalSeconds);

    /// <summary>With the connection's saved credential; without one, as the signed-in Windows account (a domain's single sign-on).</summary>
    Task<(string Output, string Errors)> RunRemoteAsync(CancellationToken cancel)
    {
        var credential = App.Store.Resolve(_connection).Credential is { Secret: not null } saved ? saved : null;
        return RemotePowerShell.RunAsync(_connection.Host, credential?.FullUser,
            credential?.Secret is { } secret ? Store.Unprotect(secret) : null, _script.Body, cancel);
    }

    async Task RunAsync()
    {
        var vm = _connection.AzureVm;
        var cancel = (_cancel = new CancellationTokenSource()).Token;
        AgainButton.IsEnabled = CopyButton.IsEnabled = false;
        FixButton.Visibility = Visibility.Collapsed;
        OutputBox.TextWrapping = TextWrapping.NoWrap; // a script's columns stay columns
        OutputBox.Text = "";
        (Ring.Visibility, Glyph.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        _running.Restart();
        ShowRunning();
        _clock.Start();
        Log.Info($"{(vm != null ? "Azure" : "Remoting")}: script \"{_script.Name}\" on {Target}");
        try
        {
            var (output, errors) = vm != null ? await AzureVms.RunScriptAsync(vm, _script.Body, cancel) : await RunRemoteAsync(cancel);
            OutputBox.Text = Text(output, errors);
            Done(errors.Length > 0 ? SymbolRegular.Warning24 : SymbolRegular.CheckmarkCircle24,
                Loc.Format(errors.Length > 0 ? "ScriptDoneErrors" : "ScriptDone", Target, (int)_running.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } // the window was closed
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or AzureSignInRequiredException or TaskCanceledException
                                       or System.Text.Json.JsonException or System.ComponentModel.Win32Exception
                                       or System.Security.Cryptography.CryptographicException)
        {
            Log.Error(ex, $"Script \"{_script.Name}\" on {Target}");
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
            Done(SymbolRegular.ErrorCircle24, Loc.Format("ScriptFailed", Target));
            if (vm == null) return; // the ways out below are Azure's
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
        if (_script.Confirm && !Dialog.Confirm(this, Loc.Format("ScriptConfirm", _script.Name, Target), Loc.Get("ScriptRun"), DialogKind.Warning)) return;
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
            if (IsLoaded && _connection.AzureVm is { } vm && AzureVms.Account(vm) != null) _ = RunAsync(); // signed in: the script right away
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
