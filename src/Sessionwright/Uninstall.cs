using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using Wpf.Ui.Appearance;

namespace Sessionwright;

/// <summary>
/// What happens when Sessionwright is uninstalled. Velopack's Update.exe starts the exe for this just before it removes
/// the files and gives it 30 seconds (60 in its current source), so the one question asked here answers itself ("keep")
/// well before that. Shortcuts are not touched: Update.exe removes every shortcut pointing into the install folder.
/// </summary>
static class Uninstall
{
    static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(22);

    public static void Run()
    {
        // The exe is about to go: the "start with Windows" entry would point at nothing.
        Tray.SetStartWithWindows(false);
        SessionLink.Unregister();

        // Nobody to ask in a deployment tool's session or a silent uninstall (winget); nothing to ask about without data.
        if (!Environment.UserInteractive || UninstallerIsSilent() || !File.Exists(Store.DefaultPath)) return;
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

    /// <summary>
    /// Update.exe passes the hook nothing about how it was started; winget and deployment tools start it with --silent
    /// (the uninstall entry's QuietUninstallString), Windows Settings without. So the parent's own command line says.
    /// </summary>
    static bool UninstallerIsSilent() => ParentCommandLine() is { } line && IsSilent(line);

    internal static bool IsSilent(string commandLine) => Regex.IsMatch(commandLine, @"(^|\s)(--silent|-s)(\s|$)");

    /// <summary>The command line of the process that started this one; null when it is gone or cannot be read.</summary>
    internal static string? ParentCommandLine()
    {
        var basic = new IntPtr[6]; // PROCESS_BASIC_INFORMATION; the last field is the parent's id
        var handle = GCHandle.Alloc(basic, GCHandleType.Pinned);
        try
        {
            if (NtQueryInformationProcess(new IntPtr(-1), 0, handle.AddrOfPinnedObject(), IntPtr.Size * 6, out _) != 0) return null;
        }
        finally { handle.Free(); }

        var parent = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (int)basic[5]);
        if (parent == IntPtr.Zero) return null;
        try
        {
            // ProcessCommandLineInformation (Windows 8.1+): a UNICODE_STRING followed by the text it points to.
            NtQueryInformationProcess(parent, 60, IntPtr.Zero, 0, out var size);
            if (size <= 0) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(parent, 60, buffer, size, out _) != 0) return null;
                var length = (ushort)Marshal.ReadInt16(buffer);
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(parent); }
    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

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
