using System.Diagnostics;
using Velopack;

namespace Sessionwright;

static class Program
{
    /// <summary>The version Velopack just installed and restarted into; null on any other start.</summary>
    internal static string? UpdatedTo { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        // Started by ssh.exe from a terminal window, to ask that window something (see SshAskPass): answered and gone,
        // before anything of the app itself starts.
        if (Environment.GetEnvironmentVariable(SshAskPass.Variable) is { Length: > 0 } pipe)
        {
            Environment.Exit(SshAskPass.Answer(pipe, args));
            return;
        }
        // Velopack first: install/update/uninstall hooks start the exe with --veloapp-* and exit inside Run().
        // A downloaded update is applied at start-up by App itself, in its own window (Velopack's has no silent option),
        // and only when no other Sessionwright runs: the updater force-stops every process in the install folder.
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
            .OnRestarted(version => UpdatedTo = version.ToString())
            .OnBeforeUninstallFastCallback(_ => Uninstall.Run())
            // Links work right after installing, even while another Sessionwright (a copy outside the install) runs.
            // A hook runs from the install folder, so its exe is the one to register; a portable copy's never is.
            .OnAfterInstallFastCallback(_ => SessionLink.RegisterInstalled(Environment.ProcessPath!))
            .OnAfterUpdateFastCallback(_ => SessionLink.RegisterInstalled(Environment.ProcessPath!))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    // Same exe path only: on a shared host other users run their own per-user install. A process that cannot be
    // opened (another user's, an elevated one) is not one the updater could stop either.
    internal static bool OtherInstanceRunning()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(self.ProcessName);
        try
        {
            return others.Any(p => p.Id != self.Id && SamePath(p, Environment.ProcessPath));
        }
        finally
        {
            foreach (var p in others) p.Dispose();
        }
    }

    static bool SamePath(Process p, string? path)
    {
        try
        {
            return string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
