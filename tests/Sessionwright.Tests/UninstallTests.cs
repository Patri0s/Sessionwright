using System.IO;
using Xunit;

namespace Sessionwright.Tests;

public class UninstallTests
{
    // How Update.exe was started decides whether the uninstall asks anything: winget and deployment tools pass --silent.
    [Theory]
    [InlineData(@"""C:\Users\u\AppData\Local\Sessionwright\Update.exe"" --uninstall --silent", true)]
    [InlineData("Update.exe --uninstall -s", true)]
    [InlineData(@"""C:\Users\u\AppData\Local\Sessionwright\Update.exe"" --uninstall", false)]
    [InlineData(@"C:\tools\silent-installer\Update.exe --uninstall", false)]
    public void TellsASilentUninstallFromItsCommandLine(string commandLine, bool silent) => Assert.Equal(silent, Uninstall.IsSilent(commandLine));

    // The test host was started by another process, so there is a parent command line to read.
    [Fact]
    public void ReadsTheParentsCommandLine() => Assert.False(string.IsNullOrWhiteSpace(Uninstall.ParentCommandLine()));

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

public class ShortcutTests
{
    // A shortcut made from Settings must look like the installer's: same target and the same taskbar identity, so the
    // running app groups with it and Velopack's uninstall finds it.
    [Fact]
    public void WritesTargetAndTaskbarIdentity()
    {
        var dir = Directory.CreateTempSubdirectory("sessionwright-shortcut").FullName;
        try
        {
            var path = Path.Combine(dir, "Sessionwright.lnk");
            Shortcut.Write(path, @"C:\Apps\Sessionwright\current", "velopack.Sessionwright");

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            var item = shell.Namespace(dir).ParseName("Sessionwright.lnk");
            Assert.Equal(@"C:\Apps\Sessionwright\current\Sessionwright.exe", (string)item.GetLink.Path);
            Assert.Equal("velopack.Sessionwright", (string)item.ExtendedProperty("System.AppUserModel.ID"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
