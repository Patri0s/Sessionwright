using System.Text.Json;

namespace Sessionwright;

/// <summary>A group kept in step with Azure: the VMs of a subscription (one resource group, one tag) become its connections.</summary>
public class AzureSyncRule
{
    /// <summary>The signed-in account (user name) that lists the VMs.</summary>
    public string Account { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    /// <summary>For the editor and messages; the id is what counts.</summary>
    public string SubscriptionName { get; set; } = "";
    /// <summary>"" = every resource group.</summary>
    public string ResourceGroup { get; set; } = "";
    /// <summary>"key=value" or "key"; "" = every VM.</summary>
    public string Tag { get; set; } = "";
    /// <summary>Connect to the private IP address (reached through a VPN) instead of the public name or address.</summary>
    public bool PrivateAddress { get; set; }

    public AzureSyncRule Clone() => (AzureSyncRule)MemberwiseClone();
}

/// <summary>
/// A VM as the sync sees it: where it is, its tags, and how it is reached. The public address is its own, or its load
/// balancer's with the port a NAT rule forwards to RDP (how Dynamics 365 development VMs are published).
/// </summary>
public sealed record SyncVm(string ResourceGroup, string Name, IReadOnlyDictionary<string, string> Tags, string? PublicAddress, string? PrivateAddress,
                            int PublicPort = 3389);

/// <summary>
/// What a sync would do to a group. Connections are added for VMs nothing leads to yet; addresses are updated only in
/// connections the sync made (<see cref="Connection.Synced"/>) and the user did not readdress; nothing is ever removed
/// here: <see cref="Missing"/> is for the user to decide.
/// </summary>
/// <param name="NoAddresses">Azure gave no address for any of the VMs: the account cannot read their network resources.</param>
public sealed record AzureSyncPlan(IReadOnlyList<Connection> Add, IReadOnlyList<(Connection Connection, string Host, int Port)> Update,
                                   IReadOnlyList<Connection> Missing, bool NoAddresses = false);

static class AzureSync
{
    const int Rdp = 3389;

    static string? Text(JsonElement e, params string[] path)
    {
        foreach (var name in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return null;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    static IEnumerable<JsonElement> Array(JsonElement e, params string[] path)
    {
        foreach (var name in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return [];
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    }

    /// <summary>
    /// The VMs of a subscription with their addresses, from four ARM lists: virtual machines (tags, network cards),
    /// network interfaces (private address, own public address, NAT rules), public IP addresses (DNS name, address) and
    /// load balancers (the NAT rules: which public address and port lead to a VM's RDP port).
    /// </summary>
    internal static List<SyncVm> ParseVms(IEnumerable<JsonElement> vms, IEnumerable<JsonElement> interfaces, IEnumerable<JsonElement> publicAddresses,
                                          IEnumerable<JsonElement>? loadBalancers = null)
    {
        // Public address resources by id: the DNS name survives a deallocation, a dynamic IP address does not.
        var publicById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in publicAddresses)
            if (Text(p, "id") is { } id && (Text(p, "properties", "dnsSettings", "fqdn") ?? Text(p, "properties", "ipAddress")) is { Length: > 0 } address)
                publicById[id] = address;

        // NAT rules to the RDP port by id: the balancer's public address and the port it listens on.
        var natById = new Dictionary<string, (string Address, int Port)>(StringComparer.OrdinalIgnoreCase);
        foreach (var balancer in loadBalancers ?? [])
        {
            var frontends = Array(balancer, "properties", "frontendIPConfigurations")
                .Select(f => (Id: Text(f, "id"), Address: Text(f, "properties", "publicIPAddress", "id") is { } pid && publicById.TryGetValue(pid, out var a) ? a : null))
                .Where(f => f.Id != null && f.Address != null).ToDictionary(f => f.Id!, f => f.Address!, StringComparer.OrdinalIgnoreCase);
            foreach (var rule in Array(balancer, "properties", "inboundNatRules"))
                if (Text(rule, "id") is { } id && rule.TryGetProperty("properties", out var rp)
                    && rp.TryGetProperty("backendPort", out var back) && back.TryGetInt32(out var backendPort) && backendPort == Rdp
                    && rp.TryGetProperty("frontendPort", out var front) && front.TryGetInt32(out var frontendPort) && frontendPort is >= 1 and <= 65535
                    && Text(rp, "frontendIPConfiguration", "id") is { } frontend && frontends.TryGetValue(frontend, out var address))
                    natById[id] = (address, frontendPort);
        }

        // Network cards by id; the primary IP configuration first. An address of the card's own goes before a NAT rule.
        var cards = new Dictionary<string, (string? Private, string? Public, int Port)>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in interfaces)
        {
            if (Text(card, "id") is not { } id) continue;
            var ordered = Array(card, "properties", "ipConfigurations")
                .OrderByDescending(c => c.TryGetProperty("properties", out var cp) && cp.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True).ToList();
            if (ordered.Count == 0) continue;
            var privateAddress = ordered.Select(c => Text(c, "properties", "privateIPAddress")).FirstOrDefault(a => !string.IsNullOrEmpty(a));
            var own = ordered.Select(c => Text(c, "properties", "publicIPAddress", "id"))
                .Select(pid => pid != null && publicById.TryGetValue(pid, out var a) ? a : null).FirstOrDefault(a => a != null);
            var nat = ordered.SelectMany(c => Array(c, "properties", "loadBalancerInboundNatRules")).Select(r => Text(r, "id"))
                .Where(rid => rid != null && natById.ContainsKey(rid)).Select(rid => ((string, int)?)natById[rid!]).FirstOrDefault();
            cards[id] = own != null ? (privateAddress, own, Rdp) : nat is var (address, port) ? (privateAddress, address, port) : (privateAddress, null, Rdp);
        }

        var result = new List<SyncVm>();
        foreach (var vm in vms)
        {
            if (AzureVms.ParseVm(vm) is not { } parsed) continue;
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (vm.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var tag in t.EnumerateObject()) tags[tag.Name] = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() ?? "" : "";
            (string? Private, string? Public, int Port) reach = (null, null, Rdp);
            foreach (var reference in Array(vm, "properties", "networkProfile", "networkInterfaces"))
                if (Text(reference, "id") is { } cardId && cards.TryGetValue(cardId, out var card))
                    reach = (reach.Private ?? card.Private, reach.Public ?? card.Public, reach.Public == null ? card.Port : reach.Port);
            result.Add(new SyncVm(parsed.ResourceGroup, parsed.Name, tags, reach.Public, reach.Private, reach.Public == null ? Rdp : reach.Port));
        }
        return result;
    }

    /// <summary>"key=value" (the value compared without case) or "key" alone; an empty filter takes every VM.</summary>
    internal static bool HasTag(SyncVm vm, string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return true;
        var equals = filter.IndexOf('=');
        var key = (equals < 0 ? filter : filter[..equals]).Trim();
        return vm.Tags.TryGetValue(key, out var value) && (equals < 0 || string.Equals(value.Trim(), filter[(equals + 1)..].Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The address the rule asks for; the other kind when the VM has none of it; null when it has neither.</summary>
    internal static (string Host, int Port)? AddressOf(SyncVm vm, AzureSyncRule rule)
    {
        (string, int)? open = vm.PublicAddress is { } p ? (p, vm.PublicPort) : null, inside = vm.PrivateAddress is { } a ? (a, Rdp) : null;
        return rule.PrivateAddress ? inside ?? open : open ?? inside;
    }

    /// <summary>
    /// What to do so that <paramref name="group"/> matches <paramref name="vms"/> (the whole subscription, unfiltered).
    /// <paramref name="hidden"/>: VMs the user hid in the machines window.
    /// </summary>
    internal static AzureSyncPlan Plan(Group group, AzureSyncRule rule, IReadOnlyList<SyncVm> vms, IReadOnlyList<Connection> all, Func<AzureVmLink, bool> hidden)
    {
        AzureVmLink Link(SyncVm vm) => new() { Account = rule.Account, TenantId = rule.TenantId, SubscriptionId = rule.SubscriptionId, ResourceGroup = vm.ResourceGroup, Name = vm.Name };
        var wanted = vms
            .Where(vm => rule.ResourceGroup.Trim().Length == 0 || string.Equals(vm.ResourceGroup, rule.ResourceGroup.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(vm => HasTag(vm, rule.Tag))
            .Select(vm => (Vm: vm, Link: Link(vm))).Where(x => !hidden(x.Link)).ToList();

        var add = new List<Connection>();
        var update = new List<(Connection, string, int)>();
        foreach (var (vm, link) in wanted)
        {
            var existing = all.Where(c => c.AzureVm != null && AzureVms.Same(c.AzureVm, link)).ToList();
            var address = AddressOf(vm, rule);
            // A VM some connection already leads to (anywhere in the list) gets no second one.
            if (existing.Count == 0)
                add.Add(new Connection { Name = vm.Name, Host = address?.Host ?? vm.Name, Port = address?.Port ?? Rdp, ParentId = group.Id, AzureVm = link, Synced = true });
            else if (address is var (host, port))
                foreach (var c in existing.Where(c => c.Synced && c.ParentId == group.Id && (!string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase) || c.Port != port)))
                    update.Add((c, host, port));
        }
        // Made by the sync, in this group, in this subscription, and no longer among the VMs the rule takes.
        var missing = all.Where(c => c.Synced && c.ParentId == group.Id && c.AzureVm is { } vm
                                     && string.Equals(vm.SubscriptionId, rule.SubscriptionId, StringComparison.OrdinalIgnoreCase)
                                     && !wanted.Any(w => AzureVms.Same(w.Link, vm))).ToList();
        return new AzureSyncPlan(add, update, missing, NoAddresses: wanted.Count > 0 && wanted.All(w => AddressOf(w.Vm, rule) == null));
    }

    /// <summary>Adds and readdresses; the missing ones are left for the caller to ask about.</summary>
    internal static void Apply(Store store, AzureSyncPlan plan)
    {
        foreach (var c in plan.Add) store.Data.Connections.Add(c);
        foreach (var (c, host, port) in plan.Update) (c.Host, c.Port) = (host, port);
    }

    /// <summary>Lists the rule's subscription and plans the group's sync. Throws what the Azure requests throw.</summary>
    public static async Task<AzureSyncPlan> PlanAsync(Store store, Group group, CancellationToken cancel = default)
    {
        var rule = group.AzureSync ?? throw new InvalidOperationException("The group has no Azure sync rule.");
        var vms = await AzureVms.SyncListAsync(rule, cancel);
        return Plan(group, rule, vms, store.Data.Connections.ToList(), Machines.IsHidden);
    }
}
