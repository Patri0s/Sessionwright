using System.Text;
using Xunit;

namespace Sessionwright.Tests;

public sealed class RemotePowerShellTests
{
    // The password travels on standard input, one base64 line each: no quoting to get wrong, nothing on a command line.
    [Fact]
    public void InputIsFourBase64LinesAndAnEmptyUserMeansTheWindowsAccount()
    {
        var lines = RemotePowerShell.Input("srv01", @"CORP\anna", "p\"a'ss word", "Get-Date\n'two lines'").Split('\n');
        Assert.Equal(5, lines.Length); // four and the end of the last
        Assert.Equal(["srv01", @"CORP\anna", "p\"a'ss word", "Get-Date\n'two lines'"],
            lines.Take(4).Select(l => Encoding.UTF8.GetString(Convert.FromBase64String(l))));
        Assert.Equal("", RemotePowerShell.Input("srv01", null, null, "x").Split('\n')[1]);
    }

    // The whole way through powershell.exe: a machine that is not there is told apart from a script that failed.
    [Fact]
    public async Task AMachineThatIsNotReachedIsAFailureWithTheWayOut()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemotePowerShell.RunAsync("no-such-machine.invalid", null, null, "'never runs'", TestContext.Current.CancellationToken));
        Assert.Contains("TrustedHosts", ex.Message);
    }
}
