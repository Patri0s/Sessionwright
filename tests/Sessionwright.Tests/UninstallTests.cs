using System.IO;
using Xunit;

namespace Sessionwright.Tests;

public class UninstallTests
{
    // "Remove my data" takes what Sessionwright wrote and nothing else: the folder may be one the user chose.
    [Fact]
    public void RemovesOnlyItsOwnFiles()
    {
        var root = Directory.CreateTempSubdirectory("sessionwright-uninstall").FullName;
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var tokens = Directory.CreateDirectory(Path.Combine(root, "tokens")).FullName;
            foreach (var own in new[] { "data.json", "data.json.bak", "data.json.corrupt-20261111-120000", "sessionwright.log", "sessionwright.1.log" })
                File.WriteAllText(Path.Combine(data, own), "x");
            File.WriteAllText(Path.Combine(data, "notes.txt"), "the user's own file");
            File.WriteAllText(Path.Combine(tokens, "Sessionwright.nocae"), "x");
            File.WriteAllText(Path.Combine(tokens, "OtherApp.nocae"), "x");

            Uninstall.RemoveData(Path.Combine(data, "data.json"), tokens);

            Assert.Equal(["notes.txt"], Directory.GetFiles(data).Select(Path.GetFileName));
            Assert.Equal(["OtherApp.nocae"], Directory.GetFiles(tokens).Select(Path.GetFileName));

            // With nothing else in it, the folder goes too.
            File.Delete(Path.Combine(data, "notes.txt"));
            File.WriteAllText(Path.Combine(data, "data.json"), "x");
            Uninstall.RemoveData(Path.Combine(data, "data.json"), tokens);
            Assert.False(Directory.Exists(data));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
