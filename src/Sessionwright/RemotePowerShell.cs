using System.Diagnostics;
using System.Text;

namespace Sessionwright;

/// <summary>
/// Runs a script on a machine that is not in Azure, over PowerShell remoting (WinRM, port 5985): Windows' own
/// powershell.exe does the remoting, so nothing is added to the app. The script runs as the connection's account.
/// </summary>
static class RemotePowerShell
{
    // Reads its four lines (host, user, password, script; base64 each) from standard input: a password on the command
    // line would show in the process list. Exit code 3 = the machine was not reached (as opposed to the script failing).
    const string Bootstrap = """
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        function D($s) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s)) }
        $i = [Console]::In
        $h = D $i.ReadLine(); $u = D $i.ReadLine(); $p = D $i.ReadLine(); $b = D $i.ReadLine()
        $a = @{ ComputerName = $h; ScriptBlock = [scriptblock]::Create($b) }
        if ($u) { $a.Credential = New-Object pscredential($u, (ConvertTo-SecureString $p -AsPlainText -Force)) }
        $r = @(Invoke-Command @a 2>&1)
        $bad = @($r | Where-Object { $_ -is [Management.Automation.ErrorRecord] })
        $down = @($bad | Where-Object { $_.Exception -is [Management.Automation.Remoting.PSRemotingTransportException] }).Count -gt 0
        [Console]::Out.Write((@($r | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) | Out-String -Width 300))
        [Console]::Error.Write((($bad | ForEach-Object { "$_" }) -join [Environment]::NewLine))
        if ($down) { exit 3 }
        """;

    /// <summary>What goes to the process's standard input; no user = the signed-in Windows account.</summary>
    internal static string Input(string host, string? user, string? password, string script)
    {
        static string E(string? s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        return $"{E(host)}\n{E(user)}\n{E(password)}\n{E(script)}\n";
    }

    /// <summary>What the script wrote and its errors. Throws <see cref="InvalidOperationException"/> when the machine was not reached.</summary>
    public static async Task<(string Output, string Errors)> RunAsync(string host, string? user, string? password, string script, CancellationToken cancel)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Encoding.ASCII, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap)) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("powershell.exe did not start.");
        // Closing the window stops the wait and the local process; what already runs on the machine goes on.
        using var kill = cancel.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already gone
        });
        await process.StandardInput.WriteAsync(Input(host, user, password, script));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        var errors = process.StandardError.ReadToEndAsync(cancel);
        await process.WaitForExitAsync(cancel);
        var problems = (await errors).Trim();
        if (process.ExitCode == 3)
            throw new InvalidOperationException(problems + Environment.NewLine + Environment.NewLine + Loc.Format("ScriptNoRemoting", host));
        return (await output, problems);
    }
}
