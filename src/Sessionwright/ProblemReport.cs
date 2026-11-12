using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sessionwright;

/// <summary>
/// One zip to send to whoever helps with a problem: the log (both files), the data file without anything secret, and a
/// note on this PC (version, Windows, monitors). Instead of describing in words what the app shows in its log.
/// </summary>
static class ProblemReport
{
    public static void Save(string zipPath) => Save(zipPath, App.Store.Path, About());

    /// <summary>The logs next to <paramref name="dataPath"/>, that file sanitized, and <paramref name="about"/>.</summary>
    internal static void Save(string zipPath, string dataPath, string about)
    {
        var folder = Path.GetDirectoryName(dataPath)!;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var name in new[] { "sessionwright.1.log", "sessionwright.log" })
            if (File.Exists(Path.Combine(folder, name))) Add(zip, name, ReadShared(Path.Combine(folder, name)));
        if (File.Exists(dataPath)) Add(zip, "data.json", Sanitize(ReadShared(dataPath)));
        Add(zip, "about.txt", about);
    }

    // The log is still being written to: read alongside the writer.
    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
        writer.Write(text);
    }

    /// <summary>
    /// The data file without passwords (protected for this Windows account anyway), tokens, notes (people keep
    /// passwords in notes) or the secret parts of link addresses: names, hosts, groups and settings stay, as they
    /// explain most problems.
    /// </summary>
    internal static string Sanitize(string json)
    {
        var root = JsonNode.Parse(json);
        Strip(root);
        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";

        static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                        if (key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase)
                            || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Equals("Notes", StringComparison.OrdinalIgnoreCase))
                            o[key] = o[key] is null ? null : "(removed)";
                        // A script of the user's: may hold a password or a connection string.
                        else if (key.Equals("Body", StringComparison.OrdinalIgnoreCase)) o[key] = o[key] is null ? null : "(removed)";
                        // A command run before connecting: its arguments may hold a password too.
                        else if (key.Equals("Command", StringComparison.OrdinalIgnoreCase)) o[key] = o[key] is null ? null : "(removed)";
                        // An Azure account's record (ids of the account and its directory): not needed to explain a problem.
                        else if (key.Equals("Record", StringComparison.OrdinalIgnoreCase)) o[key] = o[key] is null ? null : "(removed)";
                        // A link: where it goes stays, what may carry a token or a password does not.
                        else if (key.Equals("Url", StringComparison.OrdinalIgnoreCase) && o[key] is JsonValue url && url.TryGetValue<string>(out var address))
                            o[key] = Links.WithoutSecrets(address);
                        else Strip(o[key]);
                    break;
                case JsonArray a:
                    foreach (var item in a) Strip(item);
                    break;
            }
        }
    }

    static string About() => string.Join(Environment.NewLine,
    [
        $"Sessionwright {Updater.CurrentVersion}{(Updater.IsInstalled ? " (installed)" : " (not installed)")}",
        $"Windows {Environment.OSVersion.VersionString}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
        $".NET {Environment.Version}, culture {System.Globalization.CultureInfo.CurrentCulture.Name}, UI {System.Globalization.CultureInfo.CurrentUICulture.Name}",
        $"Monitors: {string.Join("; ", System.Windows.Forms.Screen.AllScreens.Select(s => $"{s.Bounds} {App.Dpi(s)} dpi{(s.Primary ? " primary" : "")}"))}",
        $"Open sessions: {string.Join(", ", App.Sessions.Open.Select(s => $"{s.Title} ({s.Phase})"))}",
        $"Created {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
    ]);
}
