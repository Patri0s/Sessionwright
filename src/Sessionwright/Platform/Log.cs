using System.Diagnostics;
using System.IO;

namespace Sessionwright;

/// <summary>
/// Diagnostic log next to the data file (sessionwright.log, rotated at 1 MB to sessionwright.1.log). Records what support needs to
/// explain "it disconnected" or "the screen jumped": session phases with disconnect reasons and transition timings.
/// </summary>
static class Log
{
    const long MaxSize = 1024 * 1024;
    static readonly object Gate = new();

    public static string Folder => Path.GetDirectoryName(Store.DefaultPath)!;
    public static string File => Path.Combine(Folder, "sessionwright.log");

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(Exception ex, string? context = null) => Write("ERR", context == null ? ex.ToString() : $"{context}: {ex}");

    /// <summary>Logs how long <paramref name="what"/> took once the returned scope is disposed.</summary>
    public static IDisposable Time(string what) => new Timing(what);

    sealed class Timing(string what) : IDisposable
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public void Dispose() => Info($"{what}: {_clock.ElapsedMilliseconds} ms");
    }

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                if (new FileInfo(File) is { Exists: true, Length: > MaxSize })
                    System.IO.File.Move(File, Path.Combine(Folder, "sessionwright.1.log"), overwrite: true);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // logging must never throw
    }
}
