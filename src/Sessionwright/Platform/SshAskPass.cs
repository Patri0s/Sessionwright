using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Sessionwright;

/// <summary>
/// How ssh.exe asks for what it cannot ask in the terminal: OpenSSH runs the program named in SSH_ASKPASS with the
/// question as its argument and reads the answer from its output. That program is Sessionwright itself, started with
/// <see cref="Variable"/> set to a pipe of the window the ssh belongs to; the window answers (a saved password, the
/// user's OK for a new host key, a password typed into a prompt). So a saved password never is on a command line, and
/// the checking of host keys stays OpenSSH's.
/// </summary>
static class SshAskPass
{
    public const string Variable = "SESSIONWRIGHT_ASKPASS";

    /// <summary>The helper's whole run: asks the window through its pipe and prints the answer. The process's exit code.</summary>
    public static int Answer(string pipeName, string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            var question = Convert.ToBase64String(Encoding.UTF8.GetBytes(args.Length > 0 ? args[0] : "")) + "\n";
            pipe.Write(Encoding.ASCII.GetBytes(question));
            using var reader = new StreamReader(pipe, Encoding.ASCII);
            if (reader.ReadLine() is not ['+', .. var answer]) return 1; // declined: ssh takes it as cancelled
            using var output = Console.OpenStandardOutput();
            output.Write([.. Convert.FromBase64String(answer), (byte)'\n']);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or FormatException)
        {
            return 1;
        }
    }

    /// <summary>
    /// The window's side: answers every question asked through <paramref name="pipeName"/> with <paramref name="answer"/>
    /// (null declines), until cancelled. Runs on the caller's thread between awaits, so the answer may show a dialog.
    /// Only a helper started by one of <paramref name="children"/> (the window's ssh, or scp and the ssh it runs) is
    /// answered: the pipe's name can be found by any program of the user, which could otherwise ask for the password.
    /// </summary>
    public static async Task ServeAsync(string pipeName, Func<string, string?> answer, Func<IReadOnlyCollection<int>> children, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancel);
                using var reader = new StreamReader(pipe, Encoding.ASCII, leaveOpen: true);
                var question = Encoding.UTF8.GetString(Convert.FromBase64String(await reader.ReadLineAsync(cancel) ?? ""));
                var own = StartedBy(pipe, children());
                if (!own) Log.Warn("SSH: a question on the terminal's pipe did not come from its own ssh: declined");
                var reply = own && answer(question) is { } text ? "+" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) : "-";
                await pipe.WriteAsync(Encoding.ASCII.GetBytes(reply + "\n"), cancel);
                await pipe.FlushAsync(cancel);
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                Log.Info($"SSH: a question from ssh was not answered: {ex.Message}");
            }
            catch (Exception ex) // whatever answering did (a window that could not be shown): said, and the next question is still heard
            {
                Log.Error(ex, "SSH: answering a question from ssh failed");
            }
        }
    }

    /// <summary>
    /// The pipe's client is this program (the helper) and was started by one of <paramref name="children"/>, directly or
    /// through one process more (scp runs ssh, which runs the helper).
    /// </summary>
    static bool StartedBy(NamedPipeServerStream pipe, IReadOnlyCollection<int> children)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var client)) return false;
            using var helper = System.Diagnostics.Process.GetProcessById((int)client);
            if (!string.Equals(helper.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
            var process = (int)client;
            for (var level = 0; level < 3; level++)
            {
                if (Parent(process) is not { } parent) return false;
                if (children.Contains(parent)) return true;
                process = parent;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // gone meanwhile, or not ours to look at
        }
    }

    static int? Parent(int processId)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        var info = new PROCESS_BASIC_INFORMATION();
        return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int kind, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);
}
