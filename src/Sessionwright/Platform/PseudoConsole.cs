using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sessionwright;

/// <summary>
/// A console program run without a console window (Windows' pseudoconsole, ConPTY): what it prints comes out of
/// <see cref="Output"/> as terminal text (UTF-8 with escape sequences), what is written to <see cref="Input"/> it reads
/// as typed. The way Windows Terminal runs ssh.exe.
/// </summary>
sealed class PseudoConsole : IDisposable
{
    IntPtr _console, _process;
    bool _disposed;

    public Stream Input { get; }
    public Stream Output { get; }
    readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes with the program's exit code once it has ended.</summary>
    public Task<int> Exit => _exit.Task;

    PseudoConsole(IntPtr console, IntPtr process, Stream input, Stream output) =>
        (_console, _process, Input, Output) = (console, process, input, output);

    /// <summary>Starts <paramref name="commandLine"/> with the process's environment plus <paramref name="environment"/>.</summary>
    public static PseudoConsole Start(string commandLine, IReadOnlyDictionary<string, string> environment, int columns, int rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        var console = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new COORD((short)columns, (short)rows), inRead, outWrite, 0, out console));
            // The console holds its own ends from here on.
            inRead.Dispose();
            outWrite.Dispose();

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016 /* PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE */, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var startup = new STARTUPINFOEX { lpAttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in environment) variables[name] = value;
            var block = string.Concat(variables.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => $"{v.Key}={v.Value}\0")) + "\0";

            const uint ExtendedStartupInfo = 0x00080000, UnicodeEnvironment = 0x00000400;
            if (!CreateProcess(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfo | UnicodeEnvironment, block, null, ref startup, out var info))
                throw new Win32Exception();
            CloseHandle(info.hThread);

            var pty = new PseudoConsole(console, info.hProcess,
                new FileStream(inWrite, FileAccess.Write, 1, false), new FileStream(outRead, FileAccess.Read, 4096, false));
            console = IntPtr.Zero; // the instance owns it now
            ThreadPool.QueueUserWorkItem(_ => pty.WaitForExit());
            return pty;
        }
        catch
        {
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
            inRead.Dispose();
            inWrite.Dispose();
            outRead.Dispose();
            outWrite.Dispose();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    void WaitForExit()
    {
        var process = _process;
        WaitForSingleObject(process, uint.MaxValue);
        GetExitCodeProcess(process, out var code);
        _exit.TrySetResult((int)code);
    }

    public void Resize(int columns, int rows)
    {
        if (!_disposed && columns > 0 && rows > 0) ResizePseudoConsole(_console, new COORD((short)columns, (short)rows));
    }

    /// <summary>Closes the console, which ends the program still attached to it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // In the background: closing waits until the output pipe is drained, and the reader may be the caller.
        var (console, process) = (_console, _process);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            ClosePseudoConsole(console);
            try { Input.Dispose(); } catch (IOException) { }
            Output.Dispose();
            CloseHandle(process);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct COORD(short x, short y)
    {
        public readonly short X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console, COORD size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string? application, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string environment, string? directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
}
