using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Sessionwright;

/// <summary>A session on a server (WTS_CONNECTSTATE_CLASS values).</summary>
public enum ServerSessionState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }

public sealed record ServerSession(int Id, string Domain, string User, ServerSessionState State, string Station)
{
    public string Account => Domain.Length > 0 ? $@"{Domain}\{User}" : User;
    public string StateText => Loc.Get(State switch
    {
        ServerSessionState.Active => "SessionStateActive",
        ServerSessionState.Disconnected => "SessionStateDisconnected",
        _ => "SessionStateOther",
    });
    public string Details => Station.Length > 0 ? Loc.Format("SessionDetails", StateText, Id, Station) : Loc.Format("SessionDetailsNoStation", StateText, Id);
}

/// <summary>
/// Who is signed in on a server, and signing them out or disconnecting them, without opening a session. Two ways:
/// - an Azure VM: Azure Run Command (a script run through Azure; no port has to be open, takes up to a minute, needs
///   Microsoft.Compute/virtualMachines/runCommand/action);
/// - any other server: the Remote Desktop Services API over the network (RPC, port 445), as the connection's saved
///   account (network-only credentials, like mstsc's), else as this Windows user.
/// Both report the numeric session state, so a server in another language reads the same.
/// </summary>
static class RemoteSessions
{
    public enum Route { Direct, Azure }

    /// <summary>
    /// Directly when the server answers on port 445 (a VPN, the company network: an answer within a second), else an
    /// Azure VM through Azure (a minute or so, but no port needed).
    /// </summary>
    public static async Task<Route> ChooseRouteAsync(Connection c)
    {
        if (c.AzureVm == null) return Route.Direct;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(c.Host, 445, timeout.Token);
            return Route.Direct;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
        {
            return Route.Azure;
        }
    }

    public static Task<List<ServerSession>> ListAsync(Connection c, Route route, CancellationToken cancel = default) => route == Route.Azure
        ? ListThroughAzureAsync(c.AzureVm!, cancel)
        : Task.Run(() => Direct(c, Enumerate), cancel);

    static async Task<List<ServerSession>> ListThroughAzureAsync(AzureVmLink vm, CancellationToken cancel) =>
        ParseList(await AzureVms.RunPowerShellAsync(vm, ListScript, cancel));

    /// <summary>Signs the session out (its programs end) or only disconnects it (they keep running).</summary>
    public static Task EndAsync(Connection c, Route route, ServerSession s, bool signOut, CancellationToken cancel = default)
    {
        if (route == Route.Azure)
            return AzureVms.RunPowerShellAsync(c.AzureVm!, [signOut ? $"logoff {s.Id}" : $"tsdiscon {s.Id}"], cancel);
        return Task.Run(() => Direct(c, server =>
        {
            if (!(signOut ? WTSLogoffSession(server, s.Id, true) : WTSDisconnectSession(server, s.Id, true))) throw new Win32Exception();
            return 0;
        }), cancel);
    }

    static readonly HashSet<string> NoDirectSignOut = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Signs <paramref name="user"/>'s session out through the Remote Desktop Services API (port 445): nothing is typed,
    /// so neither the keyboard nor the window is needed meanwhile. False when the server cannot be asked that way quickly
    /// or has no single active session of that user; the caller types the sign-out then. A server that refused once is not
    /// asked again in this run: each try costs the wait for an answer before the typing.
    /// </summary>
    public static async Task<bool> TrySignOutAsync(Connection c, string user)
    {
        if (NoDirectSignOut.Contains(c.Host)) return false;
        var name = AccountName(user);
        try
        {
            using (var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(800)))
            using (var tcp = new System.Net.Sockets.TcpClient())
                await tcp.ConnectAsync(c.Host, 445, probe.Token);
            var work = Task.Run(() => Direct(c, server =>
            {
                var mine = Enumerate(server).Where(s => s.State == ServerSessionState.Active && string.Equals(s.User, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mine.Count != 1) return false;
                if (!WTSLogoffSession(server, mine[0].Id, false)) throw new Win32Exception();
                return true;
            }));
            // An RPC that gets no answer can take half a minute: typing is quicker then.
            if (await Task.WhenAny(work, Task.Delay(4000)) != work) throw new TimeoutException("no answer within 4 s");
            if (await work) return true;
            Log.Info($"{c.Title}: no single active session of {name} on the server");
        }
        catch (Exception ex) // best effort: whatever went wrong, typing still signs out
        {
            Log.Info($"{c.Title}: sign-out through the Remote Desktop Services API unavailable ({ex.Message}): typing it");
        }
        NoDirectSignOut.Add(c.Host);
        return false;
    }

    /// <summary>
    /// The last resort when typing the sign-out failed (Run did not open, the screen was locked): Azure Run Command signs
    /// <paramref name="user"/>'s active session out on the VM itself. Up to a minute, no port and no keyboard needed.
    /// </summary>
    public static async Task<bool> TrySignOutThroughAzureAsync(AzureVmLink vm, string user)
    {
        try
        {
            var output = await AzureVms.RunPowerShellAsync(vm, SignOutScript(AccountName(user)));
            if (output.Contains("SESSIONWRIGHT-SIGNEDOUT")) return true;
            Log.Info($"{vm.Name}: nothing signed out through Azure ({output.Trim()})");
        }
        catch (Exception ex) // reported as a failed sign-out by the caller
        {
            Log.Info($"{vm.Name}: sign-out through Azure failed: {ex.Message}");
        }
        return false;
    }

    // The session list of ListScript, then a sign-out of the one active session of that user (state 0 = active).
    internal static string[] SignOutScript(string user) =>
    [
        "$found = & {",
        .. ListScript,
        "}",
        $"$user = {PowerShellString(user)}",
        "$mine = @($found | Where-Object { $p = $_.Split('|'); $p[3] -eq $user -and $p[4] -eq '0' })",
        "if ($mine.Count -eq 1) { logoff $mine[0].Split('|')[1]; 'SESSIONWRIGHT-SIGNEDOUT' } else { \"SESSIONWRIGHT-SESSIONS $($mine.Count)\" }",
    ];

    /// <summary>The bare user name the server lists: "DOMAIN\user" and "user@domain" both give "user".</summary>
    static string AccountName(string user)
    {
        var name = user[(user.IndexOf('\\') + 1)..];
        return name.IndexOf('@') is > 0 and var at ? name[..at] : name;
    }

    /// <summary>The account the direct way uses: the connection's saved one, else this Windows user.</summary>
    public static string DirectAccount(Connection c) =>
        App.Store.Resolve(c).Credential is { Secret: not null } credential ? credential.FullUser : WindowsIdentity.GetCurrent().Name;

    /// <summary>The sessions on <paramref name="host"/>, asked as the current (possibly impersonated) Windows identity.</summary>
    internal static List<ServerSession> ListHost(string host) => OnServer(host, Enumerate);

    static T OnServer<T>(string host, Func<IntPtr, T> work)
    {
        var server = WTSOpenServer(host);
        if (server == IntPtr.Zero) throw new Win32Exception();
        try { return work(server); }
        finally { WTSCloseServer(server); }
    }

    static T Direct<T>(Connection c, Func<IntPtr, T> work) => AsSavedAccount(c, () => OnServer(c.Host, work));

    /// <summary>Runs <paramref name="work"/> towards the network as the connection's saved account (else as this Windows user).</summary>
    internal static T AsSavedAccount<T>(Connection c, Func<T> work)
    {
        if (App.Store.Resolve(c).Credential is not { Secret: { } secret } credential) return work();
        // Network-only credentials: the server sees the saved account, this PC keeps its own identity.
        var (user, domain) = SplitAccount(credential.User, credential.Domain);
        if (!LogonUser(user, domain, Store.Unprotect(secret), 9 /* LOGON32_LOGON_NEW_CREDENTIALS */, 3 /* LOGON32_PROVIDER_WINNT50 */, out var token))
            throw new Win32Exception();
        using (token) return WindowsIdentity.RunImpersonated(token, work);
    }

    /// <summary>A message box in every signed-in session (directly, port 445), closing itself after <paramref name="seconds"/>.</summary>
    public static Task SendMessageAsync(Connection c, string title, string message, int seconds) => Task.Run(() => Direct(c, server =>
    {
        foreach (var s in Enumerate(server))
            WTSSendMessage(server, s.Id, title, title.Length * 2, message, message.Length * 2, 0x30 /* MB_ICONWARNING */, Math.Max(1, seconds), out _, false);
        return 0;
    }));

    /// <summary>The same through Azure Run Command: no port needed.</summary>
    public static Task SendMessageThroughAzureAsync(AzureVmLink vm, string title, string message, int seconds) =>
        AzureVms.RunPowerShellAsync(vm, MessageScript(title, message, seconds));

    // The same API on the VM itself, with the same title: msg.exe headed it "Message from" and its sender, which is nobody
    // for a script run by Azure (SYSTEM), so only the date stood there.
    internal static string[] MessageScript(string title, string message, int seconds) =>
    [
        "Add-Type -TypeDefinition @'",
        "using System; using System.Runtime.InteropServices;",
        "public static class SessionwrightMessage {",
        "  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct S { public int Id; public string Station; public int State; }",
        "  [DllImport(\"wtsapi32.dll\", CharSet = CharSet.Unicode)] public static extern bool WTSEnumerateSessions(IntPtr h, int r, int v, out IntPtr list, out int count);",
        "  [DllImport(\"wtsapi32.dll\", CharSet = CharSet.Unicode)] public static extern bool WTSSendMessage(IntPtr h, int id, string t, int tl, string m, int ml, int style, int timeout, out int response, bool wait);",
        "}",
        "'@",
        $"$t = {PowerShellString(title)}; $m = {PowerShellString(message)}",
        "$list = [IntPtr]::Zero; $count = 0; $r = 0",
        "[void][SessionwrightMessage]::WTSEnumerateSessions([IntPtr]::Zero, 0, 1, [ref]$list, [ref]$count)",
        "$size = [Runtime.InteropServices.Marshal]::SizeOf([type][SessionwrightMessage+S])",
        "for ($i = 0; $i -lt $count; $i++) {",
        "  $s = [Runtime.InteropServices.Marshal]::PtrToStructure([IntPtr]($list.ToInt64() + $i * $size), [type][SessionwrightMessage+S])",
        // Session 0 has no desktop; a listener or an empty session just refuses the message.
        $"  if ($s.Id -gt 0) {{ [void][SessionwrightMessage]::WTSSendMessage([IntPtr]::Zero, $s.Id, $t, $t.Length * 2, $m, $m.Length * 2, 0x30, {Math.Max(1, seconds)}, [ref]$r, $false) }}",
        "}",
    ];

    /// <summary>A PowerShell string expression: nothing in it is run or expanded.</summary>
    // Run Command saves the script without a BOM, and Windows PowerShell reads such a file in the ANSI code page: "pracę"
    // arrived as "pracÄ™". Text beyond ASCII goes in as UTF-8 in Base64, decoded on the VM; the script itself stays ASCII.
    internal static string PowerShellString(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.All(char.IsAscii)
            ? $"'{text.Replace("'", "''")}'"
            : $"([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))}')))";
    }

    /// <summary>"DOMAIN\user", "user@domain" (no domain then) or a user with its own domain field.</summary>
    internal static (string User, string? Domain) SplitAccount(string user, string? domain)
    {
        var slash = user.IndexOf('\\');
        if (slash > 0) return (user[(slash + 1)..], user[..slash]);
        return (user, user.Contains('@') ? null : string.IsNullOrEmpty(domain) ? "." : domain);
    }

    static List<ServerSession> Enumerate(IntPtr server)
    {
        if (!WTSEnumerateSessions(server, 0, 1, out var list, out var count)) throw new Win32Exception();
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            var sessions = new List<ServerSession>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(list + i * size);
                var user = Query(server, info.SessionId, 5 /* WTSUserName */);
                if (user.Length == 0) continue; // services, listeners
                sessions.Add(new ServerSession(info.SessionId, Query(server, info.SessionId, 7 /* WTSDomainName */), user,
                    (ServerSessionState)info.State, info.WinStationName ?? ""));
            }
            return sessions;
        }
        finally { WTSFreeMemory(list); }
    }

    static string Query(IntPtr server, int session, int infoClass)
    {
        if (!WTSQuerySessionInformation(server, session, infoClass, out var buffer, out _)) return "";
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    // The same API on the VM itself, one "SESSIONWRIGHT|id|domain|user|state|station" line per signed-in session.
    internal static readonly string[] ListScript =
    [
        "Add-Type -TypeDefinition @'",
        "using System; using System.Runtime.InteropServices;",
        "public static class SessionwrightWts {",
        "  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct S { public int Id; public string Station; public int State; }",
        "  [DllImport(\"wtsapi32.dll\", CharSet = CharSet.Unicode)] public static extern bool WTSEnumerateSessions(IntPtr h, int r, int v, out IntPtr list, out int count);",
        "  [DllImport(\"wtsapi32.dll\", CharSet = CharSet.Unicode)] public static extern bool WTSQuerySessionInformation(IntPtr h, int id, int c, out IntPtr b, out int n);",
        "}",
        "'@",
        "$list = [IntPtr]::Zero; $count = 0",
        "[void][SessionwrightWts]::WTSEnumerateSessions([IntPtr]::Zero, 0, 1, [ref]$list, [ref]$count)",
        "$size = [Runtime.InteropServices.Marshal]::SizeOf([type][SessionwrightWts+S])",
        "for ($i = 0; $i -lt $count; $i++) {",
        "  $s = [Runtime.InteropServices.Marshal]::PtrToStructure([IntPtr]($list.ToInt64() + $i * $size), [type][SessionwrightWts+S])",
        "  $b = [IntPtr]::Zero; $n = 0",
        "  [void][SessionwrightWts]::WTSQuerySessionInformation([IntPtr]::Zero, $s.Id, 5, [ref]$b, [ref]$n); $u = [Runtime.InteropServices.Marshal]::PtrToStringUni($b)",
        "  [void][SessionwrightWts]::WTSQuerySessionInformation([IntPtr]::Zero, $s.Id, 7, [ref]$b, [ref]$n); $d = [Runtime.InteropServices.Marshal]::PtrToStringUni($b)",
        "  if ($u) { \"SESSIONWRIGHT|$($s.Id)|$d|$u|$($s.State)|$($s.Station)\" }",
        "}",
    ];

    internal static List<ServerSession> ParseList(string output) =>
    [
        .. output.Split('\n').Select(l => l.Trim().Split('|')).Where(p => p is ["SESSIONWRIGHT", _, _, _, _, _] && int.TryParse(p[1], out _) && int.TryParse(p[4], out _))
            .Select(p => new ServerSession(int.Parse(p[1]), p[2], p[3], (ServerSessionState)int.Parse(p[4]), p[5])),
    ];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTS_SESSION_INFO
    {
        public int SessionId;
        public string? WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr WTSOpenServer(string name);
    [DllImport("wtsapi32.dll")] static extern void WTSCloseServer(IntPtr server);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr list, out int count);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSLogoffSession(IntPtr server, int session, bool wait);
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSDisconnectSession(IntPtr server, int session, bool wait);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSSendMessage(IntPtr server, int session, string title, int titleBytes, string message, int messageBytes, int style,
                                      int timeoutSeconds, out int response, bool wait);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);
}
