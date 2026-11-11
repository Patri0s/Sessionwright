using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace Sessionwright;

/// <summary>A virtual machine one of the signed-in accounts can see.</summary>
public sealed record VmInfo(string Account, string TenantId, string SubscriptionId, string SubscriptionName, string ResourceGroup,
                            string Name, string Location, VmPowerState State)
{
    public AzureVmLink Link => new() { Account = Account, TenantId = TenantId, SubscriptionId = SubscriptionId, ResourceGroup = ResourceGroup, Name = Name };
    public override string ToString() => Name;
}

/// <summary>A VM's auto-shutdown: a Microsoft.DevTestLab schedule in its resource group (time "HHmm" in a Windows time zone).</summary>
public sealed record ShutdownSchedule(string Id, string VmName, bool Enabled, string Time, string TimeZoneId, JsonElement Resource);

/// <summary>A directory (tenant) the account can reach only after signing in to it interactively (its own MFA, a guest).</summary>
public sealed record AzureTenantSignIn(AzureAccount Account, string TenantId, string Name)
{
    public string Text => Loc.Format("AzureTenantSignInNeeded", Name, Account.Username);
}

/// <summary>An account whose sign-in expired (or needs MFA again): it has to sign in interactively.</summary>
public sealed class AzureSignInRequiredException(string account, Exception inner)
    : Exception(Loc.Format("AzureSignInRequired", account), inner)
{
    public string Account { get; } = account;
}

/// <summary>
/// Azure virtual machines through Azure Resource Manager: the accounts (Microsoft Entra sign-in in the browser, any
/// number of them), their VMs, power states and starting them. Tokens live in the MSAL cache that Azure.Identity keeps
/// encrypted with DPAPI ("Sessionwright"); the settings only hold which accounts to use. Uses Azure.Identity's default public
/// client (the one of the Azure CLI), so no app registration is needed in any tenant.
/// </summary>
static class AzureVms
{
    const string Arm = "https://management.azure.com";
    const string ComputeApi = "2024-07-01", ResourcesApi = "2022-12-01", DevTestLabApi = "2018-09-15";
    static readonly string[] Scope = ["https://management.azure.com/.default"];
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly Dictionary<string, InteractiveBrowserCredential> Credentials = new(StringComparer.OrdinalIgnoreCase);

    static List<AzureAccount> Accounts => App.Store.Data.Settings.AzureAccounts;

    public static AzureAccount? Account(AzureVmLink link) =>
        Accounts.FirstOrDefault(a => string.Equals(a.Username, link.Account, StringComparison.OrdinalIgnoreCase));

    /// <summary>The window Windows' sign-in dialog docks to when no other is given (set once the main window has a handle).</summary>
    public static IntPtr MainWindowHandle { get; set; }

    static InteractiveBrowserCredentialOptions Options(AuthenticationRecord? record, string? tenant, IntPtr parent = default)
    {
        // Windows' own sign-in window (WAM, as the Azure CLI uses), docked to Sessionwright. The browser flow redirects to a
        // localhost port instead, which a security product scanning the URL can answer first: the sign-in then works,
        // but the browser shows "can't connect" (seen on a company laptop). The browser stays the fallback without a window.
        var window = parent != IntPtr.Zero ? parent : MainWindowHandle;
        var options = window != IntPtr.Zero ? new InteractiveBrowserCredentialBrokerOptions(window) : new InteractiveBrowserCredentialOptions();
        options.TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "Sessionwright" };
        options.AuthenticationRecord = record;
        options.TenantId = tenant;
        // Background refreshes must never open a sign-in window: they report AuthenticationRequiredException instead.
        options.DisableAutomaticAuthentication = true;
        options.BrowserCustomization = new BrowserCustomizationOptions
        {
            SuccessMessage = SignInPage(true, Loc.Get("AzurePageSuccessTitle"), Loc.Get("AzurePageSuccessText")),
            ErrorMessage = SignInPage(false, Loc.Get("AzurePageErrorTitle"), Loc.Get("AzurePageErrorText") + " {0}: {1}"),
        };
        options.AdditionallyAllowedTenants.Add("*"); // VMs in other tenants the account is a guest of
        return options;
    }

    /// <summary>
    /// The page the browser shows after signing in, instead of MSAL's plain one. The error page goes through string.Format
    /// ({0} error, {1} description), hence the doubled CSS braces; non-ASCII text as entities, as the charset is not sent.
    /// </summary>
    internal static string SignInPage(bool success, string title, string text)
    {
        var (color, mark) = success
            ? ("#2563eb", "<path d='M15 25.5l6.5 6.5L34 18.5' />")
            : ("#dc2626", "<path d='M17 17l14 14M31 17L17 31' />");
        var page = """
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>Sessionwright</title><style>
            :root{{color-scheme:light dark;--bg:#f3f4f6;--card:#fff;--text:#111827;--muted:#6b7280;--line:#e5e7eb}}
            @media (prefers-color-scheme:dark){{:root{{--bg:#111318;--card:#1c1f26;--text:#f3f4f6;--muted:#9ca3af;--line:#2b303b}}}}
            body{{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--text);
            font:15px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}}
            .card{{width:min(420px,calc(100vw - 32px));box-sizing:border-box;background:var(--card);border:1px solid var(--line);
            border-radius:12px;padding:40px 32px 28px;text-align:center;box-shadow:0 8px 32px rgba(0,0,0,.08)}}
            svg{{width:64px;height:64px}}h1{{font:600 22px/1.3 "Segoe UI Variable Display","Segoe UI",system-ui,sans-serif;margin:16px 0 8px}}
            p{{margin:0;color:var(--muted)}}.brand{{margin-top:28px;padding-top:16px;border-top:1px solid var(--line);font-size:13px;color:var(--muted)}}
            .brand b{{color:var(--text)}}
            </style></head><body><div class="card">
            <svg viewBox="0 0 48 48" fill="none" stroke="#fff" stroke-width="4" stroke-linecap="round" stroke-linejoin="round">
            <circle cx="24" cy="24" r="22" fill="@COLOR@" stroke="none" />@MARK@</svg>
            <h1>@TITLE@</h1><p>@TEXT@</p>
            <div class="brand"><b>Sessionwright</b> &middot; Microsoft Azure</div>
            </div></body></html>
            """;
        page = page.Replace("@COLOR@", color).Replace("@MARK@", mark)
            .Replace("@TITLE@", WebUtility.HtmlEncode(title)).Replace("@TEXT@", WebUtility.HtmlEncode(text));
        if (success) page = page.Replace("{{", "{").Replace("}}", "}");
        return string.Concat(page.Select(ch => ch > 127 ? $"&#{(int)ch};" : ch.ToString()));
    }

    /// <summary>Signs a (new) account in, in the browser. The caller adds the result to the settings.</summary>
    public static async Task<AzureAccount> SignInAsync(IntPtr parent = default, CancellationToken cancel = default)
    {
        var credential = new InteractiveBrowserCredential(Options(null, null, parent));
        var record = await credential.AuthenticateAsync(new TokenRequestContext(Scope), cancel);
        using var stream = new MemoryStream();
        await record.SerializeAsync(stream, cancel);
        return new AzureAccount { Username = record.Username, TenantId = record.TenantId, Record = Convert.ToBase64String(stream.ToArray()) };
    }

    /// <summary>Signs an existing account in again (expired session, new MFA), to its home directory or another one.</summary>
    public static async Task ReauthenticateAsync(AzureAccount account, string? tenant = null, IntPtr parent = default, CancellationToken cancel = default)
    {
        tenant ??= account.TenantId;
        // A credential of its own: the tokens land in the shared cache, the background credential keeps its record.
        var options = Options(null, tenant, parent);
        options.LoginHint = account.Username;
        await new InteractiveBrowserCredential(options).AuthenticateAsync(new TokenRequestContext(Scope, tenantId: tenant), cancel);
    }

    public static void Forget(AzureAccount account) => Credentials.Remove(account.Username);

    static InteractiveBrowserCredential Credential(AzureAccount account)
    {
        if (Credentials.TryGetValue(account.Username, out var cached)) return cached;
        using var stream = new MemoryStream(Convert.FromBase64String(account.Record));
        var record = AuthenticationRecord.Deserialize(stream);
        return Credentials[account.Username] = new InteractiveBrowserCredential(Options(record, account.TenantId));
    }

    static async Task<string> TokenAsync(AzureAccount account, string? tenant, CancellationToken cancel)
    {
        try
        {
            return (await Credential(account).GetTokenAsync(new TokenRequestContext(Scope, tenantId: tenant), cancel)).Token;
        }
        catch (AuthenticationRequiredException ex) { throw new AzureSignInRequiredException(account.Username, ex); }
        catch (AuthenticationFailedException ex) { throw new AzureSignInRequiredException(account.Username, ex); }
    }

    static async Task<HttpResponseMessage> RawAsync(HttpMethod method, AzureAccount account, string? tenant, string url, CancellationToken cancel,
                                                    HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url.StartsWith("http") ? url : Arm + url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(account, tenant, cancel));
        return await Http.SendAsync(request, cancel);
    }

    static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
        {
            // ARM errors: { "error": { "code": "...", "message": "..." } }
            var message = body;
            try { message = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? body; }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new HttpRequestException($"{(int)response.StatusCode}: {message}", null, response.StatusCode);
        }
        return JsonDocument.Parse(body.Length > 0 ? body : "{}");
    }

    static async Task<JsonDocument> SendAsync(HttpMethod method, AzureAccount account, string? tenant, string url, CancellationToken cancel,
                                              HttpContent? content = null)
    {
        using var response = await RawAsync(method, account, tenant, url, cancel, content);
        return await ReadAsync(response, cancel);
    }

    /// <summary>
    /// Runs a PowerShell script on the VM through Azure (Run Command: no port has to be open) and returns what it wrote.
    /// Azure answers "accepted" and the result comes from polling the Location it gives; that takes seconds to a minute.
    /// </summary>
    public static async Task<string> RunPowerShellAsync(AzureVmLink link, IReadOnlyList<string> script, CancellationToken cancel = default) =>
        RunCommandOutput(await RunCommandAsync(link, script, cancel));

    /// <summary>
    /// The same for a script of the user's: what it wrote and its errors, both (a script that half worked says both).
    /// Azure keeps only the last 4096 characters of each.
    /// </summary>
    public static async Task<(string Output, string Errors)> RunScriptAsync(AzureVmLink link, string script, CancellationToken cancel = default) =>
        RunCommandStreams(await RunCommandAsync(link, script.ReplaceLineEndings("\n").Split('\n'), cancel));

    static async Task<JsonElement> RunCommandAsync(AzureVmLink link, IReadOnlyList<string> script, CancellationToken cancel)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        var body = JsonSerializer.Serialize(new { commandId = "RunPowerShellScript", script });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await RawAsync(HttpMethod.Post, account, link.TenantId, $"{VmUrl(link)}/runCommand?api-version={ComputeApi}", cancel, content);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        try
        {
            while (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                var location = response.Headers.Location ?? throw new HttpRequestException(Loc.Get("RunCommandNoResult"));
                if (DateTime.UtcNow > deadline) throw new HttpRequestException(Loc.Get("RunCommandTimeout"));
                // Every 2 s, not the Retry-After Azure suggests: the result is often ready well before it.
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                response.Dispose();
                response = await RawAsync(HttpMethod.Get, account, link.TenantId, location.ToString(), cancel);
            }
            using var result = await ReadAsync(response, cancel);
            return result.RootElement.Clone();
        }
        finally { response.Dispose(); }
    }

    /// <summary>The script's standard output; its error output (nothing else written) as the failure.</summary>
    internal static string RunCommandOutput(JsonElement result)
    {
        var (output, errors) = RunCommandStreams(result);
        if (output.Trim().Length == 0 && errors.Length > 0) throw new HttpRequestException(errors);
        return output;
    }

    /// <summary>The script's standard output and its error output (trimmed).</summary>
    internal static (string Output, string Errors) RunCommandStreams(JsonElement result)
    {
        string Stream(string name) => result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? string.Concat(value.EnumerateArray()
                .Where(v => v.TryGetProperty("code", out var c) && c.GetString()?.Contains($"/{name}/", StringComparison.OrdinalIgnoreCase) == true)
                .Select(v => v.TryGetProperty("message", out var m) ? m.GetString() : ""))
            : "";
        return (Stream("StdOut"), Stream("StdErr").Trim());
    }

    /// <summary>All items of an ARM list, following nextLink.</summary>
    static async Task<List<JsonElement>> ListAsync(AzureAccount account, string? tenant, string url, CancellationToken cancel)
    {
        var items = new List<JsonElement>();
        for (string? next = url; next != null;)
        {
            using var page = await SendAsync(HttpMethod.Get, account, tenant, next, cancel);
            if (page.RootElement.TryGetProperty("value", out var value)) items.AddRange(value.EnumerateArray().Select(v => v.Clone()));
            next = page.RootElement.TryGetProperty("nextLink", out var link) ? link.GetString() : null;
        }
        return items;
    }

    /// <summary>
    /// Every VM of every signed-in account, over all tenants and enabled subscriptions it can reach. Accounts or tenants
    /// that fail are reported in <paramref name="problems"/>, those that need an interactive sign-in in
    /// <paramref name="signIns"/>, and skipped.
    /// </summary>
    public static async Task<List<VmInfo>> ListAllAsync(List<string> problems, List<AzureTenantSignIn> signIns, CancellationToken cancel = default)
    {
        var result = new List<VmInfo>();
        foreach (var account in Accounts.ToList())
        {
            List<JsonElement> tenants;
            try { tenants = await ListAsync(account, account.TenantId, $"/tenants?api-version={ResourcesApi}", cancel); }
            catch (AzureSignInRequiredException)
            {
                signIns.Add(new AzureTenantSignIn(account, account.TenantId, account.Username));
                continue;
            }
            // Also a request that timed out and an answer that is no JSON (a proxy's page): reported, not left loading.
            catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancel.IsCancellationRequested)
            {
                problems.Add($"{account.Username}: {ex.Message}");
                continue;
            }
            var names = tenants
                .Where(t => t.TryGetProperty("tenantId", out var id) && id.GetString() != null)
                .DistinctBy(t => t.GetProperty("tenantId").GetString())
                .ToDictionary(t => t.GetProperty("tenantId").GetString()!,
                              t => t.TryGetProperty("displayName", out var n) && n.GetString() is { Length: > 0 } name ? name : t.GetProperty("tenantId").GetString()!);
            foreach (var (tenant, tenantName) in names)
            {
                try
                {
                    var subscriptions = await ListAsync(account, tenant, $"/subscriptions?api-version={ResourcesApi}", cancel);
                    foreach (var sub in subscriptions.Where(s => s.GetProperty("state").GetString() == "Enabled"))
                    {
                        var subId = sub.GetProperty("subscriptionId").GetString()!;
                        var subName = sub.TryGetProperty("displayName", out var n) ? n.GetString() ?? subId : subId;
                        await LearnRegionNamesAsync(account, tenant, subId, cancel);
                        foreach (var vm in await ListAsync(account, tenant, StatusListUrl(subId), cancel))
                            if (ParseVm(vm) is { } v)
                                result.Add(new VmInfo(account.Username, tenant, subId, subName, v.ResourceGroup, v.Name, v.Location, v.State));
                    }
                }
                // Another tenant (a guest directory) usually asks for its own sign-in and MFA once.
                catch (AzureSignInRequiredException)
                {
                    Log.Info($"Azure: tenant {tenantName} ({tenant}) of {account.Username} needs signing in");
                    signIns.Add(new AzureTenantSignIn(account, tenant, tenantName));
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancel.IsCancellationRequested)
                {
                    if (tenant == account.TenantId) problems.Add($"{account.Username}: {ex.Message}");
                    else Log.Info($"Azure: tenant {tenantName} ({tenant}) of {account.Username} skipped: {ex.Message}");
                }
            }
        }
        return result;
    }

    // "westeurope" → "West Europe": Azure's own names, learned once per run from a subscription's locations.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> RegionNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The region as Azure shows it ("West Europe"); the code itself until the names are known.</summary>
    public static string RegionName(string location) => RegionNames.TryGetValue(location, out var name) ? name : location;

    static async Task LearnRegionNamesAsync(AzureAccount account, string tenant, string subscription, CancellationToken cancel)
    {
        if (!RegionNames.IsEmpty) return; // the same for every subscription
        try
        {
            foreach (var l in await ListAsync(account, tenant, $"/subscriptions/{E(subscription)}/locations?api-version={ResourcesApi}", cancel))
                if (ParseRegion(l) is var (code, name)) RegionNames[code] = name;
        }
        catch (Exception ex) when (ex is HttpRequestException or AzureSignInRequiredException or TaskCanceledException)
        {
            Log.Info($"Azure: region names unavailable: {ex.Message}"); // the codes show instead
        }
    }

    internal static (string Code, string Name)? ParseRegion(JsonElement l) =>
        l.TryGetProperty("name", out var code) && code.GetString() is { Length: > 0 } c
        && l.TryGetProperty("displayName", out var name) && name.GetString() is { Length: > 0 } n ? (c, n) : null;

    // A part of an ARM address from a link: it can come from an imported or hand-edited file, and a "/", "?" or "#" in it
    // would turn a start or stop into a request for something else, with the user's token.
    static string E(string part) => Uri.EscapeDataString(part);

    static string StatusListUrl(string subscription) =>
        $"/subscriptions/{E(subscription)}/providers/Microsoft.Compute/virtualMachines?api-version={ComputeApi}&statusOnly=true";

    static string VmUrl(AzureVmLink link) =>
        $"/subscriptions/{E(link.SubscriptionId)}/resourceGroups/{E(link.ResourceGroup)}/providers/Microsoft.Compute/virtualMachines/{E(link.Name)}";

    // The VM's resource id as Azure writes it (in a filter value, itself escaped as a whole): not an address to call.
    static string VmId(AzureVmLink link) =>
        $"/subscriptions/{link.SubscriptionId}/resourceGroups/{link.ResourceGroup}/providers/Microsoft.Compute/virtualMachines/{link.Name}";

    /// <summary>
    /// Power states of the linked VMs, one list call per subscription. Links whose account is not signed in (or needs
    /// signing in again) get null.
    /// </summary>
    public static async Task<Dictionary<AzureVmLink, VmPowerState?>> StatesAsync(IReadOnlyCollection<AzureVmLink> links, CancellationToken cancel = default)
    {
        var states = links.ToDictionary(l => l, _ => (VmPowerState?)null);
        foreach (var group in links.GroupBy(l => (Account: l.Account.ToLowerInvariant(), l.TenantId, l.SubscriptionId)))
        {
            if (Account(group.First()) is not { } account) continue;
            try
            {
                var vms = (await ListAsync(account, group.Key.TenantId, StatusListUrl(group.Key.SubscriptionId), cancel))
                    .Select(ParseVm).OfType<ParsedVm>()
                    .ToDictionary(v => (v.ResourceGroup.ToLowerInvariant(), v.Name.ToLowerInvariant()), v => v.State);
                foreach (var link in group)
                    states[link] = vms.TryGetValue((link.ResourceGroup.ToLowerInvariant(), link.Name.ToLowerInvariant()), out var s) ? s : null;
            }
            catch (Exception ex) when (ex is AzureSignInRequiredException or HttpRequestException or TaskCanceledException)
            {
                Log.Info($"Azure: states of subscription {group.Key.SubscriptionId} unavailable: {ex.Message}");
            }
        }
        return states;
    }

    /// <summary>The VM's power state now (its instance view).</summary>
    public static async Task<VmPowerState> StateAsync(AzureVmLink link, CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        using var view = await SendAsync(HttpMethod.Get, account, link.TenantId, $"{VmUrl(link)}/instanceView?api-version={ComputeApi}", cancel);
        return PowerState(view.RootElement);
    }

    /// <summary>Asks Azure to start the VM (it returns at once; the VM then takes a minute or two).</summary>
    public static async Task StartAsync(AzureVmLink link, CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        using var _ = await SendAsync(HttpMethod.Post, account, link.TenantId, $"{VmUrl(link)}/start?api-version={ComputeApi}", cancel);
        Log.Info($"Azure: start of {link.Name} requested");
    }

    /// <summary>A start, restart or shut down of a VM in the activity log: when, which, by whom (a user's sign-in name or an app id).</summary>
    public sealed record PowerEvent(DateTime At, string Operation, string Caller);

    /// <summary>The VM's starts, restarts and shut downs since <paramref name="since"/> (the activity log: read rights are enough).</summary>
    public static async Task<List<PowerEvent>> PowerEventsAsync(AzureVmLink link, DateTime since, CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        var filter = Uri.EscapeDataString($"eventTimestamp ge '{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}' and resourceUri eq '{VmId(link).Replace("'", "''")}'");
        var url = $"/subscriptions/{E(link.SubscriptionId)}/providers/Microsoft.Insights/eventtypes/management/values?api-version=2015-04-01&$filter={filter}";
        return [.. (await ListAsync(account, link.TenantId, url, cancel)).Select(ParsePowerEvent).OfType<PowerEvent>()];
    }

    internal static PowerEvent? ParsePowerEvent(JsonElement e)
    {
        static string? Text(JsonElement at, params string[] path)
        {
            foreach (var p in path) if (!at.TryGetProperty(p, out at)) return null;
            return at.ValueKind == JsonValueKind.String ? at.GetString() : null;
        }
        var operation = Text(e, "operationName", "value");
        var status = Text(e, "status", "value");
        var caller = Text(e, "caller");
        var at = Text(e, "eventTimestamp");
        if (operation == null || caller == null || at == null || status != "Succeeded") return null;
        var kind = operation.ToLowerInvariant() switch
        {
            "microsoft.compute/virtualmachines/start/action" => "start",
            "microsoft.compute/virtualmachines/restart/action" => "restart",
            "microsoft.compute/virtualmachines/deallocate/action" or "microsoft.compute/virtualmachines/poweroff/action" => "stop",
            _ => null,
        };
        return kind == null ? null : new PowerEvent(DateTime.Parse(at, null, System.Globalization.DateTimeStyles.RoundtripKind), kind, caller);
    }

    /// <summary>Restarts the VM (Azure's restart: the guest is shut down and started again).</summary>
    public static async Task RestartAsync(AzureVmLink link, CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        using var _ = await SendAsync(HttpMethod.Post, account, link.TenantId, $"{VmUrl(link)}/restart?api-version={ComputeApi}", cancel);
        Log.Info($"Azure: restart of {link.Name} requested");
    }

    /// <summary>Shuts the VM down and releases it (deallocate): Azure stops charging for its compute, the disks stay.</summary>
    public static async Task DeallocateAsync(AzureVmLink link, CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        using var _ = await SendAsync(HttpMethod.Post, account, link.TenantId, $"{VmUrl(link)}/deallocate?api-version={ComputeApi}", cancel);
        Log.Info($"Azure: deallocation of {link.Name} requested");
    }

    /// <summary>The auto-shutdown schedules in the VM's resource group, by VM name.</summary>
    public static async Task<Dictionary<string, ShutdownSchedule>> ShutdownSchedulesAsync(AzureVmLink link, CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        var url = $"/subscriptions/{E(link.SubscriptionId)}/resourceGroups/{E(link.ResourceGroup)}/providers/Microsoft.DevTestLab/schedules?api-version={DevTestLabApi}";
        return (await ListAsync(account, link.TenantId, url, cancel)).Select(ParseShutdown).OfType<ShutdownSchedule>()
            .DistinctBy(s => s.VmName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(s => s.VmName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sets the schedule's daily time ("HHmm") and/or turns it on or off; the rest (time zone, notifications) stays.</summary>
    public static async Task UpdateShutdownAsync(AzureVmLink link, ShutdownSchedule schedule, string? time = null, bool? enabled = null,
                                                 CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        var body = JsonNode.Parse(schedule.Resource.GetRawText())!.AsObject();
        foreach (var readOnly in new[] { "id", "name", "type" }) body.Remove(readOnly);
        var properties = body["properties"]!.AsObject();
        foreach (var readOnly in new[] { "createdDate", "provisioningState", "uniqueIdentifier" }) properties.Remove(readOnly);
        if (time != null) properties["dailyRecurrence"] = new JsonObject { ["time"] = time };
        if (enabled is { } on) properties["status"] = on ? "Enabled" : "Disabled";
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var _ = await SendAsync(HttpMethod.Put, account, link.TenantId, $"{schedule.Id}?api-version={DevTestLabApi}", cancel, content);
    }

    /// <summary>
    /// Whether the signed-in account may do <paramref name="action"/> in the VM's resource group, by its role
    /// assignments (deny assignments are not seen: the call itself still tells).
    /// </summary>
    public static async Task<bool> CanAsync(AzureVmLink link, string action, CancellationToken cancel = default)
    {
        var account = Account(link) ?? throw new InvalidOperationException(Loc.Format("AzureAccountMissing", link.Account));
        var url = $"/subscriptions/{E(link.SubscriptionId)}/resourcegroups/{E(link.ResourceGroup)}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01";
        return Allows(await ListAsync(account, link.TenantId, url, cancel), action);
    }

    // ---------- Response parsing (internal for tests) ----------

    /// <summary>One of the permission sets allows the action and does not exclude it ("*" matches any part).</summary>
    internal static bool Allows(IEnumerable<JsonElement> permissions, string action) =>
        permissions.Any(p => Lists(p, "actions", action) && !Lists(p, "notActions", action));

    static bool Lists(JsonElement permission, string list, string action) =>
        permission.TryGetProperty(list, out var patterns) && patterns.ValueKind == JsonValueKind.Array
        && patterns.EnumerateArray().Any(p => p.GetString() is { } pattern && System.Text.RegularExpressions.Regex.IsMatch(action,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    internal static ShutdownSchedule? ParseShutdown(JsonElement s)
    {
        if (!s.TryGetProperty("id", out var id) || !s.TryGetProperty("properties", out var p)) return null;
        if (!p.TryGetProperty("taskType", out var task) || task.GetString() != "ComputeVmShutdownTask") return null;
        if (!p.TryGetProperty("targetResourceId", out var target) || target.GetString() is not { } targetId) return null;
        if (!p.TryGetProperty("dailyRecurrence", out var daily) || !daily.TryGetProperty("time", out var time) || time.GetString() is not { } t) return null;
        var zone = p.TryGetProperty("timeZoneId", out var z) ? z.GetString() ?? "UTC" : "UTC";
        var enabled = p.TryGetProperty("status", out var status) && status.GetString() == "Enabled";
        return new ShutdownSchedule(id.GetString()!, targetId.Split('/')[^1], enabled, t, zone, s.Clone());
    }

    static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>When a daily "HHmm" in <paramref name="zoneId"/> next comes after <paramref name="utcNow"/> (UTC); null if unreadable.</summary>
    internal static DateTime? NextShutdown(string time, string zoneId, DateTime utcNow)
    {
        if (time.Length != 4 || !int.TryParse(time[..2], out var h) || !int.TryParse(time[2..], out var m) || h > 23 || m > 59) return null;
        var zone = Zone(zoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var fire = local.Date.AddHours(h).AddMinutes(m);
        if (fire <= local) fire = fire.AddDays(1);
        // A time in the spring-forward gap does not exist that day: an hour later is the same wall-clock moment.
        if (zone.IsInvalidTime(fire)) fire = fire.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fire, DateTimeKind.Unspecified), zone);
    }

    /// <summary>The daily time <paramref name="hours"/> after the next shutdown, and when that new shutdown comes (UTC).</summary>
    internal static (string Time, DateTime FireAt) ShiftShutdown(string time, string zoneId, DateTime utcNow, int hours)
    {
        var fire = (NextShutdown(time, zoneId, utcNow) ?? throw new InvalidOperationException($"Unreadable time {time}")).AddHours(hours);
        return (TimeZoneInfo.ConvertTimeFromUtc(fire, Zone(zoneId)).ToString("HHmm"), fire);
    }



    internal sealed record ParsedVm(string ResourceGroup, string Name, string Location, VmPowerState State);

    /// <summary>A VM from a list (statusOnly=true): name and group from its resource id, the power state from its instance view.</summary>
    internal static ParsedVm? ParseVm(JsonElement vm)
    {
        if (!vm.TryGetProperty("id", out var id) || id.GetString() is not { } resourceId) return null;
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var group = Array.FindIndex(parts, p => p.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase));
        if (group < 0 || group + 1 >= parts.Length) return null;
        var name = vm.TryGetProperty("name", out var n) ? n.GetString() ?? parts[^1] : parts[^1];
        var location = vm.TryGetProperty("location", out var l) ? l.GetString() ?? "" : "";
        var state = vm.TryGetProperty("properties", out var p) && p.TryGetProperty("instanceView", out var view) ? PowerState(view) : VmPowerState.Unknown;
        return new ParsedVm(parts[group + 1], name, location, state);
    }

    /// <summary>"PowerState/deallocated" etc. among an instance view's statuses.</summary>
    internal static VmPowerState PowerState(JsonElement instanceView)
    {
        if (!instanceView.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array) return VmPowerState.Unknown;
        foreach (var status in statuses.EnumerateArray())
            if (status.TryGetProperty("code", out var code) && code.GetString() is { } c && c.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))
                return Enum.TryParse<VmPowerState>(c["PowerState/".Length..], ignoreCase: true, out var state) ? state : VmPowerState.Unknown;
        return VmPowerState.Unknown;
    }

    public static bool Same(AzureVmLink a, AzureVmLink b) =>
        string.Equals(a.SubscriptionId, b.SubscriptionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ResourceGroup, b.ResourceGroup, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>"running", "deallocated" ... in the UI language.</summary>
    public static string Describe(VmPowerState? state) => Loc.Get($"VmState{state ?? VmPowerState.Unknown}");
}
