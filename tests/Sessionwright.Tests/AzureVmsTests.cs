using System.IO;
using System.Text.Json;
using Sessionwright;
using Xunit;

public class AzureVmsTests
{
    // A region's code and the name Azure shows for it, from the subscription's locations.
    [Fact]
    public void ReadsRegionNames()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"name":"westeurope","displayName":"West Europe","regionalDisplayName":"(Europe) West Europe"}""");
        Assert.Equal(("westeurope", "West Europe"), AzureVms.ParseRegion(doc.RootElement));
        using var bad = System.Text.Json.JsonDocument.Parse("""{"name":"westeurope"}""");
        Assert.Null(AzureVms.ParseRegion(bad.RootElement));
    }

    [Fact]
    public void ParsesVmFromStatusOnlyList()
    {
        using var doc = JsonDocument.Parse("""
            {
              "id": "/subscriptions/1111/resourceGroups/DEV-RG/providers/Microsoft.Compute/virtualMachines/DEV-12",
              "name": "DEV-12",
              "location": "westeurope",
              "properties": { "instanceView": { "statuses": [
                { "code": "ProvisioningState/succeeded" },
                { "code": "PowerState/deallocated" }
              ] } }
            }
            """);
        var vm = AzureVms.ParseVm(doc.RootElement);
        Assert.Equal(new AzureVms.ParsedVm("DEV-RG", "DEV-12", "westeurope", VmPowerState.Deallocated), vm);
    }

    [Theory]
    [InlineData("""{ "statuses": [ { "code": "PowerState/running" } ] }""", VmPowerState.Running)]
    [InlineData("""{ "statuses": [ { "code": "PowerState/Starting" } ] }""", VmPowerState.Starting)]
    [InlineData("""{ "statuses": [ { "code": "PowerState/hibernated" } ] }""", VmPowerState.Unknown)]
    [InlineData("""{ "statuses": [ { "code": "ProvisioningState/updating" } ] }""", VmPowerState.Unknown)]
    [InlineData("""{ }""", VmPowerState.Unknown)]
    public void ReadsPowerState(string instanceView, VmPowerState expected)
    {
        using var doc = JsonDocument.Parse(instanceView);
        Assert.Equal(expected, AzureVms.PowerState(doc.RootElement));
    }

    [Fact]
    public void IgnoresItemsWithoutResourceGroup()
    {
        using var doc = JsonDocument.Parse("""{ "id": "/subscriptions/1111/providers/x" }""");
        Assert.Null(AzureVms.ParseVm(doc.RootElement));
    }
}

public class AzureSignInPageTests
{
    [Fact]
    public void ErrorPageTakesMsalPlaceholders()
    {
        var page = string.Format(AzureVms.SignInPage(false, "Sign-in failed", "Try again. {0}: {1}"), "access_denied", "User cancelled");
        Assert.Contains("access_denied: User cancelled", page);
        Assert.Contains(":root{color-scheme", page);
    }

    [Fact]
    public void SuccessPageIsPlainHtmlWithEntities()
    {
        var page = AzureVms.SignInPage(true, "Zalogowano", "Możesz zamknąć");
        Assert.DoesNotContain("{{", page);
        Assert.Contains("Mo&#380;esz", page);
        if (Environment.GetEnvironmentVariable("SESSIONWRIGHT_PAGE_PREVIEW") is { } dir)
        {
            File.WriteAllText(Path.Combine(dir, "ok.html"), page);
            File.WriteAllText(Path.Combine(dir, "err.html"), string.Format(AzureVms.SignInPage(false, "Logowanie nie powiodło się", "Zamknij tę kartę i spróbuj ponownie w Sessionwright. {0}: {1}"), "access_denied", "The user cancelled."));
        }
    }
}
