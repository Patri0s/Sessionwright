using System.Text.Json;
using Xunit;

namespace Sessionwright.Tests;

public class ScriptsTests
{
    // The built-in library: every script has a fixed id (a later version tells its own from the user's), a name and a body.
    [Fact]
    public void BuiltInScriptsAreComplete()
    {
        var scripts = Scripts.Defaults();
        Assert.True(scripts.Count >= 5);
        Assert.Equal(scripts.Count, scripts.Select(s => s.Id).Distinct().Count());
        Assert.Equal(scripts.Select(s => s.Id), Scripts.Defaults().Select(s => s.Id));
        Assert.All(scripts, s => Assert.True(s.Name.Length > 0 && s.Body.Trim().Length > 0 && !s.Name.StartsWith("Script")));
        Assert.Contains(scripts, s => s.Confirm); // what restarts a service asks first
    }

    [Fact]
    public void ShowsOutputThenErrors()
    {
        Assert.Equal("C: 10 GB", ScriptRunWindow.Text("C: 10 GB\r\n\r\n", ""));
        Assert.StartsWith("C: 10 GB", ScriptRunWindow.Text("C: 10 GB", "Access denied"));
        Assert.EndsWith("Access denied", ScriptRunWindow.Text("", "Access denied"));
        Assert.NotEqual("", ScriptRunWindow.Text("  ", ""));
    }

    [Fact]
    public void RunCommandGivesBothStreams()
    {
        using var doc = JsonDocument.Parse("""
            {"value":[{"code":"ComponentStatus/StdOut/succeeded","message":"done"},{"code":"ComponentStatus/StdErr/succeeded","message":"warning: x\n"}]}
            """);
        Assert.Equal(("done", "warning: x"), AzureVms.RunCommandStreams(doc.RootElement));
        Assert.Equal("done", AzureVms.RunCommandOutput(doc.RootElement));
    }

    // A failed connection: one line of Sessionwright's own for the usual reasons, Windows' text as the details; any other reason
    // gives Windows' first line, so the session's tile never grows by a paragraph.
    [Fact]
    public void FailedConnectionSaysOneLineAndKeepsTheDetails()
    {
        const string windows = "Remote Desktop can't find the computer.\nMake sure the name is right.";
        var (message, details) = RdpSession.Explain(260, windows);
        Assert.DoesNotContain('\n', message);
        Assert.NotEqual(windows.Split('\n')[0], message);
        Assert.Equal(windows, details);

        Assert.Equal(("Remote Desktop can't find the computer.", windows), RdpSession.Explain(99999, windows));
        Assert.Equal(("Disconnected (code 7).", ""), RdpSession.Explain(99999, "Disconnected (code 7)."));
    }

    // A script may hold a password or a connection string: its text does not leave in a problem report.
    [Fact]
    public void ProblemReportLeavesScriptBodiesOut()
    {
        var clean = ProblemReport.Sanitize("""{"Settings":{"Scripts":[{"Name":"Sync","Body":"sqlcmd -P S3cret"}]}}""");
        Assert.DoesNotContain("S3cret", clean);
        Assert.Contains("Sync", clean);
    }
}
