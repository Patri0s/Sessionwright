using Xunit;

namespace Sessionwright.Tests;

public class ScriptBatchTests
{
    // The table shows one line per machine: the first that says something, not a blank one or a table's rule.
    [Theory]
    [InlineData("\r\n\r\nDeviceID Size GB\r\n-------- -------\r\nC:       126.4\r\n", "DeviceID Size GB")]
    [InlineData("  ----- \n==== \n No restart is waiting. \n", "No restart is waiting.")]
    [InlineData("\n \n", "")]
    public void TheSummaryIsTheFirstLineThatSaysSomething(string output, string summary) =>
        Assert.Equal(summary, ScriptBatch.Summary(output));

    // Copied, the run reads as a table and then each machine's own output; a machine that wrote nothing has no section.
    [Fact]
    public void TheReportListsEveryMachineThenWhatEachWrote()
    {
        var rows = new List<ScriptRunRow>
        {
            new() { Connection = new Connection { Name = "SRV01", Host = "srv01" }, State = ScriptRunState.Done, Summary = "C: 126.4", Output = "C: 126.4\r\nD: 12.0\r\n" },
            new() { Connection = new Connection { Name = "SRV02-LONG", Host = "srv02" }, State = ScriptRunState.Failed, Summary = "not reached", Output = "" },
        };

        var report = ScriptBatch.Report("Free disk space", rows).ReplaceLineEndings("\n");

        Assert.Equal("Free disk space\n\nSRV01       " + rows[0].StateText + ": C: 126.4\nSRV02-LONG  " + rows[1].StateText + ": not reached\n\n--- SRV01 ---\nC: 126.4\nD: 12.0", report);
    }
}
