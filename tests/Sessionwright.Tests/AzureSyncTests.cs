using System.Text.Json;
using Xunit;

namespace Sessionwright.Tests;

public class AzureSyncTests
{
    const string Sub = "11111111-1111-1111-1111-111111111111";

    static List<JsonElement> Items(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone())];

    static readonly List<JsonElement> Vms = Items($$"""
        [
          { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-DEV/providers/Microsoft.Compute/virtualMachines/vm-dev-1", "name": "vm-dev-1", "location": "westeurope",
            "tags": { "env": "Dev", "owner": "team-a" },
            "properties": { "networkProfile": { "networkInterfaces": [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-DEV/providers/Microsoft.Network/networkInterfaces/nic1" } ] } } },
          { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-DEV/providers/Microsoft.Compute/virtualMachines/vm-dev-2", "name": "vm-dev-2", "location": "westeurope",
            "properties": { "networkProfile": { "networkInterfaces": [ { "id": "/subscriptions/{{Sub}}/resourceGroups/rg-dev/providers/Microsoft.Network/networkInterfaces/NIC2" } ] } } },
          { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-PROD/providers/Microsoft.Compute/virtualMachines/vm-prod-1", "name": "vm-prod-1", "location": "westeurope",
            "tags": { "env": "prod" }, "properties": { } }
        ]
        """);

    static readonly List<JsonElement> Cards = Items($$"""
        [
          { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-DEV/providers/Microsoft.Network/networkInterfaces/nic1",
            "properties": { "ipConfigurations": [
              { "properties": { "privateIPAddress": "10.0.0.9", "primary": false } },
              { "properties": { "privateIPAddress": "10.0.0.4", "primary": true,
                                "publicIPAddress": { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-DEV/providers/Microsoft.Network/publicIPAddresses/pip1" } } } ] } },
          { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-DEV/providers/Microsoft.Network/networkInterfaces/nic2",
            "properties": { "ipConfigurations": [ { "properties": { "privateIPAddress": "10.0.0.5", "primary": true } } ] } }
        ]
        """);

    static readonly List<JsonElement> PublicAddresses = Items($$"""
        [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-DEV/providers/Microsoft.Network/publicIPAddresses/pip1",
            "properties": { "ipAddress": "20.1.2.3", "dnsSettings": { "fqdn": "vm-dev-1.westeurope.cloudapp.azure.com" } } } ]
        """);

    static AzureSyncRule Rule(string group = "", string tag = "", bool privateAddress = false) =>
        new() { Account = "jan@contoso.com", TenantId = "t", SubscriptionId = Sub, ResourceGroup = group, Tag = tag, PrivateAddress = privateAddress };

    // The DNS name before the IP address (it survives a deallocation), the primary IP configuration first, ids
    // matched without case, a VM without a network card with no address at all.
    [Fact]
    public void ReadsAddressesAndTagsFromTheThreeLists()
    {
        var vms = AzureSync.ParseVms(Vms, Cards, PublicAddresses);
        Assert.Equal(["vm-dev-1", "vm-dev-2", "vm-prod-1"], vms.Select(v => v.Name));
        Assert.Equal(("vm-dev-1.westeurope.cloudapp.azure.com", "10.0.0.4", "Dev"), (vms[0].PublicAddress, vms[0].PrivateAddress, vms[0].Tags["ENV"]));
        Assert.Equal((null, "10.0.0.5"), (vms[1].PublicAddress, vms[1].PrivateAddress));
        Assert.Equal((null, null), (vms[2].PublicAddress, vms[2].PrivateAddress));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("env", true)]
    [InlineData("env=dev", true)]
    [InlineData(" ENV = DEV ", true)]
    [InlineData("env=prod", false)]
    [InlineData("cost-center", false)]
    public void FiltersByTag(string filter, bool taken) => Assert.Equal(taken, AzureSync.HasTag(AzureSync.ParseVms(Vms, Cards, PublicAddresses)[0], filter));

    // New VMs become connections of the group; a VM some connection already leads to gets no second one; only
    // connections the sync made are readdressed; one whose VM left the rule is reported, never removed.
    [Fact]
    public void PlansAddsUpdatesAndMissingWithoutTouchingTheUsersOwn()
    {
        var vms = AzureSync.ParseVms(Vms, Cards, PublicAddresses);
        var group = new Group { Name = "Dev" };
        var rule = Rule(group: "rg-dev");
        AzureVmLink Link(string rg, string name) => new() { Account = rule.Account, TenantId = "t", SubscriptionId = Sub, ResourceGroup = rg, Name = name };

        var synced = new Connection { Name = "vm-dev-1", Host = "old.example.com", ParentId = group.Id, AzureVm = Link("RG-DEV", "vm-dev-1"), Synced = true };
        var own = new Connection { Name = "my own", Host = "kept.example.com", AzureVm = Link("rg-dev", "VM-DEV-2") };
        var gone = new Connection { Name = "vm-old", Host = "10.0.0.99", ParentId = group.Id, AzureVm = Link("RG-DEV", "vm-old"), Synced = true };
        var plan = AzureSync.Plan(group, rule, vms, [synced, own, gone], _ => false);

        Assert.Empty(plan.Add); // vm-dev-2 is the user's own connection's, vm-prod-1 is in another resource group
        Assert.Equal([(synced, "vm-dev-1.westeurope.cloudapp.azure.com", 3389)], plan.Update);
        Assert.Equal([gone], plan.Missing);

        // Without the user's own connection, vm-dev-2 is added: private address, as it has no public one.
        var added = Assert.Single(AzureSync.Plan(group, rule, vms, [synced], _ => false).Add);
        Assert.Equal(("vm-dev-2", "10.0.0.5", group.Id, true), (added.Name, added.Host, added.ParentId!.Value, added.Synced));
        Assert.True(AzureVms.Same(added.AzureVm!, Link("RG-DEV", "vm-dev-2")));
    }

    // A VM published through a load balancer (a Dynamics 365 development VM): the balancer's public name with the
    // port its NAT rule forwards to RDP. A connection the sync made with the VM's name as its address is put right.
    [Fact]
    public void ReadsTheAddressAndPortBehindALoadBalancer()
    {
        var vm = Items($$"""
            [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Compute/virtualMachines/DEV12-1", "name": "DEV12-1",
                "properties": { "networkProfile": { "networkInterfaces": [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/networkInterfaces/nic" } ] } } } ]
            """);
        var card = Items($$"""
            [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/networkInterfaces/nic",
                "properties": { "ipConfigurations": [ { "properties": { "privateIPAddress": "10.1.0.4", "primary": true, "loadBalancerInboundNatRules": [
                  { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb/inboundNatRules/winrm" },
                  { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb/inboundNatRules/RDP" } ] } } ] } } ]
            """);
        var address = Items($$"""
            [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/publicIPAddresses/lb-ip",
                "properties": { "ipAddress": "20.9.9.9", "dnsSettings": { "fqdn": "dev12.westeurope.cloudapp.azure.com" } } } ]
            """);
        var balancer = Items($$"""
            [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb", "properties": {
                "frontendIPConfigurations": [ { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb/frontendIPConfigurations/fe",
                  "properties": { "publicIPAddress": { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/publicIPAddresses/LB-IP" } } } ],
                "inboundNatRules": [
                  { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb/inboundNatRules/winrm",
                    "properties": { "frontendPort": 50986, "backendPort": 5986, "frontendIPConfiguration": { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb/frontendIPConfigurations/fe" } } },
                  { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb/inboundNatRules/rdp",
                    "properties": { "frontendPort": 51234, "backendPort": 3389, "frontendIPConfiguration": { "id": "/subscriptions/{{Sub}}/resourceGroups/RG-D365/providers/Microsoft.Network/loadBalancers/lb/frontendIPConfigurations/fe" } } } ] } } ]
            """);
        var vms = AzureSync.ParseVms(vm, card, address, balancer);
        Assert.Equal(("dev12.westeurope.cloudapp.azure.com", 51234, "10.1.0.4"), (vms[0].PublicAddress, vms[0].PublicPort, vms[0].PrivateAddress));

        var group = new Group { Name = "D365" };
        var rule = Rule();
        var named = new Connection { Name = "DEV12-1", Host = "DEV12-1", ParentId = group.Id, Synced = true,
            AzureVm = new AzureVmLink { Account = rule.Account, TenantId = "t", SubscriptionId = Sub, ResourceGroup = "rg-d365", Name = "dev12-1" } };
        var plan = AzureSync.Plan(group, rule, vms, [named], _ => false);
        Assert.Equal([(named, "dev12.westeurope.cloudapp.azure.com", 51234)], plan.Update);
        Assert.False(plan.NoAddresses);
        // Nothing readable about the network: said, and the names stay.
        Assert.True(AzureSync.Plan(group, rule, AzureSync.ParseVms(vm, [], [], []), [named], _ => false) is { NoAddresses: true, Update.Count: 0 });
    }

    [Fact]
    public void TakesThePrivateAddressWhenAskedAndSkipsHiddenVms()
    {
        var vms = AzureSync.ParseVms(Vms, Cards, PublicAddresses);
        var group = new Group { Name = "All" };
        var plan = AzureSync.Plan(group, Rule(privateAddress: true), vms, [], link => link.Name == "vm-dev-2");
        Assert.Equal([("vm-dev-1", "10.0.0.4", 3389), ("vm-prod-1", "vm-prod-1", 3389)], plan.Add.Select(c => (c.Name, c.Host, c.Port)));
    }
}
