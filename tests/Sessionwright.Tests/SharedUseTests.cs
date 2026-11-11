using System.Text.Json;
using Sessionwright;
using Xunit;

public class SharedUseTests
{
    static AzureVms.PowerEvent? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AzureVms.ParsePowerEvent(doc.RootElement);
    }

    [Fact]
    public void ReadsAStartFromTheActivityLog()
    {
        var e = Parse("""
            { "operationName": { "value": "Microsoft.Compute/virtualMachines/start/action" }, "status": { "value": "Succeeded" },
              "caller": "Jan.Kowalski@contoso.onmicrosoft.com", "eventTimestamp": "2026-11-04T18:39:40.1234567Z" }
            """)!;
        Assert.Equal(("start", "Jan.Kowalski@contoso.onmicrosoft.com"), (e.Operation, e.Caller));
        Assert.Equal(new DateTime(2026, 11, 4, 18, 39, 40, DateTimeKind.Utc), e.At.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("Microsoft.Compute/virtualMachines/deallocate/action", "Succeeded", "stop")]
    [InlineData("Microsoft.Compute/virtualMachines/restart/action", "Succeeded", "restart")]
    [InlineData("Microsoft.Compute/virtualMachines/start/action", "Started", null)]   // only what happened
    [InlineData("Microsoft.Compute/virtualMachines/write", "Succeeded", null)]        // not a power operation
    public void KeepsOnlyFinishedPowerOperations(string operation, string status, string? expected)
    {
        var e = Parse($$"""{ "operationName": { "value": "{{operation}}" }, "status": { "value": "{{status}}" }, "caller": "x@y", "eventTimestamp": "2026-11-04T18:00:00Z" }""");
        Assert.Equal(expected, e?.Operation);
    }

    [Theory]
    [InlineData("Jan.Kowalski@contoso.onmicrosoft.com", "Jan Kowalski")]
    [InlineData("jan_kowalski@contoso.com", "jan kowalski")]
    public void NamesPeopleFromTheirSignInName(string upn, string name) => Assert.Equal(name, SharedUse.PersonName(upn));
}
