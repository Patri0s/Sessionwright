using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using Sessionwright;
using Xunit;

public class RemoteSessionsTests
{
    [Fact]
    public void ParsesTheScriptOutputAndSkipsEverythingElse()
    {
        var output = "WARNING: something\r\nSESSIONWRIGHT|2|CONTOSO|alice|0|RDP-Tcp#0\r\nSESSIONWRIGHT|3|CONTOSO|admin|4|\r\nSESSIONWRIGHT|x|bad|line|0|\r\n";
        var sessions = RemoteSessions.ParseList(output);
        Assert.Equal(
            [new ServerSession(2, "CONTOSO", "alice", ServerSessionState.Active, "RDP-Tcp#0"), new ServerSession(3, "CONTOSO", "admin", ServerSessionState.Disconnected, "")],
            sessions);
        Assert.Equal(@"CONTOSO\alice", sessions[0].Account);
    }

    [Theory]
    [InlineData(@"CONTOSO\jan", null, "jan", "CONTOSO")]
    [InlineData("jan@contoso.com", null, "jan@contoso.com", null)]
    [InlineData("jan", "CONTOSO", "jan", "CONTOSO")]
    [InlineData("jan", null, "jan", ".")]
    public void SplitsAccounts(string user, string? domain, string expectedUser, string? expectedDomain) =>
        Assert.Equal((expectedUser, expectedDomain), RemoteSessions.SplitAccount(user, domain));

    [Fact]
    public void ReadsRunCommandOutput()
    {
        using var doc = JsonDocument.Parse("""
            { "value": [ { "code": "ComponentStatus/StdOut/succeeded", "message": "SESSIONWRIGHT|2|D|u|0|x" },
                         { "code": "ComponentStatus/StdErr/succeeded", "message": "" } ] }
            """);
        Assert.Equal("SESSIONWRIGHT|2|D|u|0|x", AzureVms.RunCommandOutput(doc.RootElement));
    }

    [Fact]
    public void ErrorOutputAloneIsTheFailure()
    {
        using var doc = JsonDocument.Parse("""
            { "value": [ { "code": "ComponentStatus/StdOut/succeeded", "message": "" },
                         { "code": "ComponentStatus/StdErr/succeeded", "message": "Access denied" } ] }
            """);
        Assert.Equal("Access denied", Assert.Throws<HttpRequestException>(() => AzureVms.RunCommandOutput(doc.RootElement)).Message);
    }

    // The real API on this PC: the console session of the user running the tests is there.
    [Fact]
    public void ListsThisPcsSessions() =>
        Assert.Contains(RemoteSessions.ListHost("localhost"), s => string.Equals(s.User, Environment.UserName, StringComparison.OrdinalIgnoreCase));

    // The script sent to Azure VMs, run here: it must compile and report this PC's own session the same way.
    [Fact]
    public void TheAzureScriptReportsThisPcsSessions()
    {
        var script = Path.Combine(Path.GetTempPath(), $"sessionwright-sessions-{Guid.NewGuid():N}.ps1");
        File.WriteAllLines(script, RemoteSessions.ListScript);
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            var output = ps.StandardOutput.ReadToEnd();
            var errors = ps.StandardError.ReadToEnd();
            ps.WaitForExit();
            Assert.True(errors.Length == 0, errors);
            Assert.Contains(RemoteSessions.ParseList(output), s => string.Equals(s.User, Environment.UserName, StringComparison.OrdinalIgnoreCase));
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public async Task AsksDirectlyWhenPort445AnswersElseThroughAzure()
    {
        Assert.Equal(RemoteSessions.Route.Direct, await RemoteSessions.ChooseRouteAsync(new Connection { Host = "192.0.2.1" })); // no VM: only directly
        Assert.Equal(RemoteSessions.Route.Direct, await RemoteSessions.ChooseRouteAsync(new Connection { Host = "localhost", AzureVm = new AzureVmLink() }));
        // TEST-NET-1: never answers, so the check gives up after its short wait.
        Assert.Equal(RemoteSessions.Route.Azure, await RemoteSessions.ChooseRouteAsync(new Connection { Host = "192.0.2.1", AzureVm = new AzureVmLink() }));
    }

    // The message for Azure Run Command goes into PowerShell as a single-quoted string: nothing in it is run.
    [Theory]
    [InlineData("Save your work", "'Save your work'")]
    [InlineData(@"It's 5 min; $(Remove-Item C:\x) `n", @"'It''s 5 min; $(Remove-Item C:\x) `n'")]
    [InlineData("two\r\nlines", "'two  lines'")]
    public void QuotesMessagesForPowerShell(string message, string quoted) => Assert.Equal(quoted, RemoteSessions.PowerShellString(message));

    // Only a repository address reads GitHub releases; anything else stays a plain web or file feed.
    [Theory]
    [InlineData("https://github.com/example/Sessionwright", true)]
    [InlineData("https://github.com/example/Sessionwright/", true)]
    [InlineData("https://github.com/example/Sessionwright/releases", false)]
    [InlineData("https://example.com/example/Sessionwright", false)]
    [InlineData(@"\\server\apps\Sessionwright", false)]
    public void RecognizesAGitHubRepositoryAsTheFeed(string feed, bool gitHub) => Assert.Equal(gitHub, Updater.IsGitHub(feed));

    // The Azure sign-out script, run on this PC by Windows PowerShell 5.1 as Run Command would (an ASCII file), with
    // logoff replaced by nothing: it must parse, list the sessions and pick out this user's.
    [Fact]
    public void SignOutScriptRunsInWindowsPowerShell()
    {
        var script = RemoteSessions.SignOutScript(Environment.UserName);
        Assert.All(script, line => Assert.True(line.All(char.IsAscii), line));
        var file = Path.Combine(Path.GetTempPath(), $"sessionwright-signout-{Guid.NewGuid():N}.ps1");
        File.WriteAllLines(file, script.Select(l => l.Replace("logoff ", "$null = ")), System.Text.Encoding.ASCII);
        try
        {
            using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            var output = ps.StandardOutput.ReadToEnd();
            var errors = ps.StandardError.ReadToEnd();
            ps.WaitForExit();
            Assert.Equal("", errors.Trim());
            // Signed in at this PC: exactly one active session (a build agent without one reports how many it found).
            Assert.Matches("SESSIONWRIGHT-SIGNEDOUT|SESSIONWRIGHT-SESSIONS \\d+", output);
        }
        finally { File.Delete(file); }
    }

    // Run Command's script is read in the ANSI code page: Polish letters go in as Base64 of UTF-8, the script stays ASCII.
    [Fact]
    public void KeepsTheScriptAsciiForPolishText()
    {
        const string message = "Ten serwer zostanie uruchomiony ponownie za 5 min. Zapisz swoją pracę.";
        var quoted = RemoteSessions.PowerShellString(message);
        Assert.True(quoted.All(char.IsAscii));
        Assert.Equal(message, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(quoted.Split('\'')[1])));
        Assert.All(RemoteSessions.MessageScript("Sessionwright · Zoë Müller", message, 300), line => Assert.True(line.All(char.IsAscii), line));
    }
}
