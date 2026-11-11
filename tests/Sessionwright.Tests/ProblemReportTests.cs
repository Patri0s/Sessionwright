using System.IO;
using Sessionwright;
using Xunit;

public class ProblemReportTests
{
    // Nothing secret leaves the PC with a report: passwords, tokens and notes (where people keep passwords) are removed,
    // what explains a problem (names, hosts, settings) stays.
    [Fact]
    public void LeavesNothingSecretInTheDataFile()
    {
        const string data = """
            {"Credentials":[{"Name":"Test","User":"tester","Secret":"AQAAANCMnd8B"}],
             "Connections":[{"Name":"DEV-12","Host":"dev.example.com","Notes":"pw: hunter2","Rdp":{"GatewayPassword":"x","Resolution":"Fullscreen"},
                             "Links":[{"Name":"Storage","Url":"https://files.example.com/share?sig=S1GNATURE"}]}],
             "Settings":{"AzureAccounts":[{"Username":"a@b.c","RefreshToken":"t0k3n","Record":"{\"homeAccountId\":\"H0ME\"}"}],"Theme":"Dark"}}
            """;
        var clean = ProblemReport.Sanitize(data);
        foreach (var secret in new[] { "AQAAANCMnd8B", "hunter2", "t0k3n", "\"x\"", "S1GNATURE", "H0ME" }) Assert.DoesNotContain(secret, clean);
        foreach (var kept in new[] { "DEV-12", "dev.example.com", "tester", "Fullscreen", "Dark", "a@b.c", "files.example.com/share" }) Assert.Contains(kept, clean);
    }

    // The zip holds both logs (also while the app still writes to one), the sanitized data file and the note on the PC.
    [Fact]
    public void ZipsTheLogsTheSanitizedDataAndTheNote()
    {
        var dir = Directory.CreateTempSubdirectory("sessionwright-report-").FullName;
        try
        {
            var data = Path.Combine(dir, "data.json");
            File.WriteAllText(data, """{"Credentials":[{"User":"tester","Secret":"AQAAANCMnd8B"}]}""");
            File.WriteAllText(Path.Combine(dir, "sessionwright.1.log"), "older");
            using var writer = new FileStream(Path.Combine(dir, "sessionwright.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            writer.Write("newer"u8);
            writer.Flush();
            var zipPath = Path.Combine(dir, "report.zip");

            ProblemReport.Save(zipPath, data, "Sessionwright 1.0.0");

            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            string Read(string name)
            {
                using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                return reader.ReadToEnd();
            }
            Assert.Equal("older", Read("sessionwright.1.log"));
            Assert.Equal("newer", Read("sessionwright.log"));
            Assert.DoesNotContain("AQAAANCMnd8B", Read("data.json"));
            Assert.Contains("tester", Read("data.json"));
            Assert.Equal("Sessionwright 1.0.0", Read("about.txt"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
