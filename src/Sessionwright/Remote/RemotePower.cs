using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sessionwright;

/// <summary>Restart or shut down, after a delay, with a message to whoever is signed in; closing their programs if forced.</summary>
public sealed record PowerRequest(bool Restart, TimeSpan Delay, string? Message, bool Force);

/// <summary>
/// Restarts or shuts down the machines behind connections:
/// - an Azure VM through Azure (restart; shut down = deallocate, so it costs nothing while off). The message reaches the
///   signed-in users directly (port 445) or through Azure Run Command; Sessionwright itself waits out the delay, and the
///   notification it shows can cancel it.
/// - any other server through Windows (InitiateSystemShutdownEx over port 445, as the saved account): the server shows
///   the message and counts down itself; a delayed one can be cancelled (AbortSystemShutdown).
/// </summary>
static class RemotePower
{
    /// <summary>Starts it for one connection. Null when under way; else why it failed (for the user).</summary>
    public static async Task<string?> RunAsync(Connection c, PowerRequest r)
    {
        try
        {
            if (c.AzureVm is { } vm) await AzureAsync(c, vm, r);
            else await DirectAsync(c, r);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or System.Net.Http.HttpRequestException or InvalidOperationException
                                       or AzureSignInRequiredException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Error(ex, $"{c.Title}: {(r.Restart ? "restart" : "shut down")}");
            return $"{c.Title}: {Describe(c, ex)}";
        }
    }

    static string Describe(Connection c, Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => Loc.Get("PowerNoAzureRights"),
        Win32Exception { NativeErrorCode: 5 } => Loc.Format("PowerAccessDenied", RemoteSessions.DirectAccount(c)),
        Win32Exception { NativeErrorCode: 1722 or 1726 or 53 or 1753 } => Loc.Format("SignedInUnreachable", c.Host),
        Win32Exception { NativeErrorCode: 1115 or 1190 } => Loc.Get("PowerAlreadyPending"),
        _ => ex.Message,
    };

    static async Task DirectAsync(Connection c, PowerRequest r)
    {
        var seconds = (int)r.Delay.TotalSeconds;
        await Task.Run(() => RemoteSessions.AsSavedAccount(c, () =>
        {
            if (!InitiateSystemShutdownEx(c.Host, r.Message ?? "", seconds, r.Force, r.Restart, 0x80000000 /* SHTDN_REASON_FLAG_PLANNED */))
                throw new Win32Exception();
            return 0;
        }));
        Log.Info($"{c.Title}: {(r.Restart ? "restart" : "shut down")} in {seconds} s requested from Windows");
        if (seconds > 0) Remind(c, r, async () =>
        {
            await Task.Run(() => RemoteSessions.AsSavedAccount(c, () =>
            {
                if (!AbortSystemShutdown(c.Host)) throw new Win32Exception();
                return 0;
            }));
            _ = TellCancelledAsync(c, null, r); // through Azure it takes up to a minute: the "cancelled" note does not wait
        });
    }

    static async Task AzureAsync(Connection c, AzureVmLink vm, PowerRequest r)
    {
        if (r.Message is { Length: > 0 } message) await TellAsync(c, vm, message, Math.Max(60, (int)r.Delay.TotalSeconds));
        if (r.Delay <= TimeSpan.Zero)
        {
            await ActAsync(c, vm, r);
            return;
        }
        var cancel = new CancellationTokenSource();
        Remind(c, r, () =>
        {
            cancel.Cancel();
            _ = TellCancelledAsync(c, vm, r);
            return Task.CompletedTask;
        });
        try { await Task.Delay(r.Delay, cancel.Token); }
        catch (OperationCanceledException) { return; }
        var failed = await RunAsyncGuarded(() => ActAsync(c, vm, r));
        if (failed != null) Notification.Show(null, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, Loc.Format("PowerFailedTitle", c.Title), failed, TimeSpan.FromMinutes(1));
    }

    static async Task<string?> RunAsyncGuarded(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or AzureSignInRequiredException or TaskCanceledException)
        {
            return ex.Message;
        }
    }

    static async Task ActAsync(Connection c, AzureVmLink vm, PowerRequest r)
    {
        if (r.Restart) await AzureVms.RestartAsync(vm);
        else
        {
            await AzureVms.DeallocateAsync(vm);
            c.VmState = VmPowerState.Deallocating;
        }
    }

    /// <summary>
    /// Those who were warned learn that it is off: else they kept closing their work for a restart that never came. Only
    /// when a message went out; one that does not get through is only logged, the cancel itself stands.
    /// </summary>
    static Task TellCancelledAsync(Connection c, AzureVmLink? vm, PowerRequest r) => r.Message is { Length: > 0 }
        ? TellAsync(c, vm, Loc.Get(r.Restart ? "PowerCancelledMessageRestart" : "PowerCancelledMessageShutdown"), 60)
        : Task.CompletedTask;

    // The signed-in users are told directly when the machine answers on port 445, else through Azure (a machine without
    // one: only directly); a message that cannot get through does not stop the restart (the user asked for it), it is
    // only logged.
    static async Task TellAsync(Connection c, AzureVmLink? vm, string message, int seconds)
    {
        try
        {
            // Who sends it, as the title: the people signed in know whom to ask for a few more minutes.
            var sender = vm?.Account is { Length: > 0 } account
                ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(SharedUse.PersonName(account))
                : Environment.UserName;
            var title = $"Sessionwright · {sender}";
            if (vm == null || await RemoteSessions.ChooseRouteAsync(c) == RemoteSessions.Route.Direct) await RemoteSessions.SendMessageAsync(c, title, message, seconds);
            else await RemoteSessions.SendMessageThroughAzureAsync(vm, title, message, seconds);
        }
        catch (Exception ex) when (ex is Win32Exception or System.Net.Http.HttpRequestException or InvalidOperationException
                                       or AzureSignInRequiredException or TaskCanceledException or UnauthorizedAccessException)
        {
            Log.Info($"{c.Title}: the message did not get through: {ex.Message}");
        }
    }

    /// <summary>A notification for the time until it happens, with a way to cancel it.</summary>
    static void Remind(Connection c, PowerRequest r, Func<Task> cancel)
    {
        var at = DateTime.Now + r.Delay;
        Notification.Show(null, Wpf.Ui.Controls.SymbolRegular.Clock24,
            Loc.Format(r.Restart ? "PowerPendingRestart" : "PowerPendingShutdown", c.Title, at.ToString("t")), Loc.Get("PowerPendingText"), r.Delay,
            (Loc.Get("PowerCancel"), () => _ = CancelAsync()));

        async Task CancelAsync()
        {
            try
            {
                await cancel();
                Log.Info($"{c.Title}: {(r.Restart ? "restart" : "shut down")} cancelled");
                Notification.Show(null, Wpf.Ui.Controls.SymbolRegular.Checkmark24, Loc.Format("PowerCancelled", c.Title), "", TimeSpan.FromSeconds(8));
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                Notification.Show(null, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, Loc.Format("PowerCancelFailed", c.Title), ex.Message, TimeSpan.FromMinutes(1));
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool InitiateSystemShutdownEx(string machine, string message, int timeoutSeconds, bool force, bool restart, uint reason);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool AbortSystemShutdown(string machine);
}
