using System.Globalization;
using System.IO;
using System.Text.Json;
using Wpf.Ui.Appearance;

namespace Sessionwright;

/// <summary>
/// What happens when Sessionwright is uninstalled. Velopack starts the exe for this just before it removes the files and
/// kills it after 30 seconds, so the one question asked here answers itself ("keep") well before that.
/// </summary>
static class Uninstall
{
    static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(22);

    public static void Run()
    {
        // The exe is about to go: the "start with Windows" entry and a desktop shortcut made from Settings would point
        // at nothing.
        Tray.SetStartWithWindows(false);
        Updater.SetShortcut(Velopack.Windows.ShortcutLocation.Desktop, false);

        // Nobody to ask in a deployment tool's session; nothing to ask about without a data file.
        if (!Environment.UserInteractive || !File.Exists(Store.DefaultPath)) return;
        try
        {
            if (AskToRemoveData())
                RemoveData(Store.DefaultPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".IdentityService"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Uninstall"); // the data stays, which is the safe outcome
        }
    }

    static bool AskToRemoveData()
    {
        var (language, theme) = SavedLook();
        if (language.Length > 0) CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo(language);

        // The app's resources without its startup: the dialog needs the styles, not the data or the main window.
        App.ResourcesOnly = true;
        var app = new App();
        app.InitializeComponent();
        switch (theme)
        {
            case "Light": ApplicationThemeManager.Apply(ApplicationTheme.Light); break;
            case "Dark": ApplicationThemeManager.Apply(ApplicationTheme.Dark); break;
            default: ApplicationThemeManager.ApplySystemTheme(); break;
        }
        return Dialog.AskToRemoveData(Loc.Format("UninstallQuestion", Path.GetDirectoryName(Store.DefaultPath)),
            Loc.Get("UninstallKeepData"), Loc.Get("UninstallRemoveData"), AnswerWithin);
    }

    /// <summary>The language and theme from the data file, read without loading it (no migrations, no recovery here).</summary>
    static (string Language, string Theme) SavedLook()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Store.DefaultPath));
            var settings = json.RootElement.GetProperty("Settings");
            return (Text(settings, "Language"), Text(settings, "Theme"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ("", "");
        }

        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    }

    /// <summary>
    /// The data file with its copies, the log, and the Azure sign-ins. Only files Sessionwright wrote: the folder may be
    /// one the user chose (SESSIONWRIGHT_DATA) and hold other things.
    /// </summary>
    internal static void RemoveData(string dataPath, string tokens)
    {
        var folder = Path.GetDirectoryName(dataPath)!;
        var data = Path.GetFileName(dataPath);
        foreach (var file in Directory.EnumerateFiles(folder, data + "*").Concat(Directory.EnumerateFiles(folder, "sessionwright*.log")).ToList())
            TryDelete(file);
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);

        // Azure.Identity keeps the token cache named in AzureVms next to other applications' caches.
        if (Directory.Exists(tokens))
            foreach (var file in Directory.EnumerateFiles(tokens, "Sessionwright*").ToList())
                TryDelete(file);
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
