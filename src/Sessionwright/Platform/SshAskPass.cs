using System.IO;
using System.IO.Pipes;
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
    /// </summary>
    public static async Task ServeAsync(string pipeName, Func<string, string?> answer, CancellationToken cancel)
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
                var reply = answer(question) is { } text ? "+" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) : "-";
                await pipe.WriteAsync(Encoding.ASCII.GetBytes(reply + "\n"), cancel);
                await pipe.FlushAsync(cancel);
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                Log.Info($"SSH: a question from ssh was not answered: {ex.Message}");
            }
        }
    }
}
