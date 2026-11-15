namespace Sessionwright;

/// <summary>
/// Whether someone else uses a machine, before it is restarted or shut down: others signed in (asked directly, when the
/// machine answers on port 445 quickly; Run Command would take a minute) and, for an Azure VM, others who started or
/// restarted it lately (the activity log, which read rights are enough for).
/// </summary>
static class SharedUse
{
    static readonly TimeSpan AskFor = TimeSpan.FromSeconds(5);

    /// <summary>One line per sign of someone else, for the user; empty when there is none (or nothing could be asked).</summary>
    public static async Task<List<string>> OthersAsync(Connection c, DateTime since)
    {
        var notes = new List<string>();
        var signedIn = SignedInOthersAsync(c);
        var started = c.AzureVm is { } vm ? StartedByOthersAsync(vm, since) : Task.FromResult(new List<string>());
        await Task.WhenAll(Bounded(signedIn), Bounded(started));
        if (signedIn.IsCompletedSuccessfully) notes.AddRange(signedIn.Result);
        if (started.IsCompletedSuccessfully) notes.AddRange(started.Result);
        return notes;

        static Task Bounded(Task t) => Task.WhenAny(t, Task.Delay(AskFor));
    }

    static async Task<List<string>> SignedInOthersAsync(Connection c)
    {
        try
        {
            if (await RemoteSessions.ChooseRouteAsync(c) != RemoteSessions.Route.Direct) return [];
            var me = App.Store.Resolve(c).Credential is { } credential ? RemoteSessions.SplitAccount(credential.User, credential.Domain).User : null;
            return [.. (await RemoteSessions.ListAsync(c, RemoteSessions.Route.Direct))
                .Where(s => !string.Equals(s.User, me, StringComparison.OrdinalIgnoreCase))
                .Select(s => Loc.Format("SharedSignedIn", s.Account, s.StateText))];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    static async Task<List<string>> StartedByOthersAsync(AzureVmLink vm, DateTime since)
    {
        try
        {
            var mine = App.Store.Data.Settings.AzureAccounts.Select(a => a.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. (await AzureVms.PowerEventsAsync(vm, since))
                // People (not Azure's own auto-shutdown) bringing it up: someone shutting it down does not use it.
                .Where(e => e.Operation != "stop" && e.Caller.Contains('@') && !mine.Contains(e.Caller))
                .GroupBy(e => e.Caller, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.MaxBy(e => e.At)!)
                .Select(e => Loc.Format(e.Operation == "restart" ? "SharedRestarted" : "SharedStarted", PersonName(e.Caller), e.At.ToLocalTime().ToString("t")))];
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or AzureSignInRequiredException or TaskCanceledException)
        {
            Log.Info($"Azure: activity of {vm.Name} unavailable: {ex.Message}");
            return [];
        }
    }

    /// <summary>"Jan.Kowalski@contoso.com" → "Jan Kowalski".</summary>
    internal static string PersonName(string upn) => upn.Split('@')[0].Replace('.', ' ').Replace('_', ' ');
}
