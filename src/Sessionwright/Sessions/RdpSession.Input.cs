using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinForms = System.Windows.Forms;

namespace Sessionwright;

// Keyboard focus and keys sent to the session. Part of RdpSession (see RdpSession.cs).
public sealed partial class RdpSession
{
    // ---------- Focus and input ----------

    public void FocusRemote()
    {
        if (_disposed || _rdp.GetOcx() == null) return; // not created yet: the window enters full screen before it is shown
        // WPF focus first (else WPF takes focus back from the hosted HWND), then the ActiveX's input window itself:
        // focusing only the AxHost wrapper leaves the control without keyboard focus.
        Host.Focus();
        _rdp.Focus();
        if (RemoteInput.InputWindow(_rdp.Handle) is var input && input != IntPtr.Zero) RemoteInput.Focus(input);
    }

    /// <summary>
    /// Signs out by typing Win+R, "shutdown /l /f", Enter. Returns whether every key was delivered; the window then waits
    /// for the session to end, and calls <see cref="CancelSignOut"/> if it does not.
    /// </summary>
    public async Task<bool> TypeSignOutAsync()
    {
        try
        {
            return await TypeLogoffAsync();
        }
        catch
        {
            CancelSignOut();
            throw;
        }
    }

    async Task<bool> TypeLogoffAsync()
    {
        // Forced (/f): "logoff" let a program hold the sign-out (unsaved changes, Visual Studio, "these apps are preventing
        // sign-out") on a screen the cover hid, and nothing seemed to happen until every program was closed by hand. The
        // question before it says programs are closed and unsaved work is lost. /l signs out this session only: the
        // machine keeps running.
        if (!await OpenRunAsync()) return false;
        // Run's box is a few percent of the screen (2-4 % logged); Start with its search is far more. There Enter runs
        // the top result, which for "logoff" is the command itself, and a command line with arguments may not be:
        // "logoff" then, unforced, rather than a sign-out that does nothing.
        var command = _runOpened > 0.2 ? "logoff" : "shutdown /l /f";
        if (command == "logoff") Log.Info($"{Connection.Title}: Win+R opened Start ({_runOpened:P0} of the screen): typing logoff");
        if (!await TypeIntoRunAsync(command)) return false;
        // Run executes the command on Enter at once; Start search (see OpenRunAsync) only once its results are in, which took
        // over 1.5 s on a freshly logged-on DEV-12: Enter once the screen stopped changing.
        await Task.Delay(500);
        var atEnter = await WaitForStillAsync(700, 5000);
        if (atEnter == null) await Task.Delay(1000); // cannot see the session: a fixed wait
        if (_disposed || Phase != SessionPhase.Ready) return false;
        _intent = Intent.SignOut; // only now: a disconnect before the command was sent is no sign-out
        if (!SendKeysFocused(RemoteInput.Chord(RemoteInput.Enter)))
        {
            CancelSignOut();
            return false;
        }
        // Enter before Start search had its results does nothing: one more, but only if the screen did not change at all.
        for (var waited = 0; waited < 4000 && !_disposed; waited += 100) await Task.Delay(100);
        if (!_disposed && _intent == Intent.SignOut && atEnter != null && Sample() is { } now && Changed(atEnter, now) < 0.005)
        {
            Log.Info($"{Connection.Title}: Enter changed nothing on screen, pressing Enter once more");
            SendKeysFocused(RemoteInput.Chord(RemoteInput.Enter));
        }
        return true;
    }

    /// <summary>
    /// Waits until the remote screen did not change for <paramref name="stillMs"/>, at most <paramref name="maxMs"/>.
    /// Returns the last sample; null when the session cannot be seen.
    /// </summary>
    async Task<int[]?> WaitForStillAsync(int stillMs, int maxMs)
    {
        var previous = Sample();
        for (int waited = 0, still = 0; previous != null && waited < maxMs && still < stillMs; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now) return null;
            still = Changed(previous, now) < 0.002 ? still + 100 : 0;
            previous = now;
        }
        return previous;
    }

    /// <summary>
    /// Waits until a window came up over a good part of the remote screen (a browser opening) and settled, at most
    /// <paramref name="maxMs"/>: a new tab in a browser already open changes less, and only waits that long.
    /// </summary>
    public async Task WaitForWindowAsync(int maxMs)
    {
        if (Sample() is not { } before)
        {
            await Task.Delay(1500); // cannot see the session: a fixed wait
            return;
        }
        for (var waited = 0; waited < maxMs && !_disposed; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now || Changed(before, now) < 0.15) continue;
            await WaitForStillAsync(250, 1500);
            return;
        }
    }

    /// <summary>Waits while the shown desktop still changes (lock screen sliding away), at most <see cref="Timings.LogonSettleExtra"/>.</summary>
    public async Task WaitWhileChangingAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await WaitForStillAsync(200, Timings.LogonSettleExtra);
        if (clock.ElapsedMilliseconds > 400) Log.Info($"{Connection.Title}: screen still changing after logon, waited {clock.ElapsedMilliseconds} ms more");
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the session's browser: Win+R, the address, Enter. Only a safe web address
    /// (<see cref="Links.IsSafe"/>): Run would run anything else as a command. False when it could not be typed.
    /// </summary>
    public async Task<bool> OpenInSessionAsync(string url)
    {
        if (!Links.IsSafe(url) || !IsReady) return false;
        Log.Info($"{Connection.Title}: opening a link in the session");
        if (!await OpenRunAsync()) return false;
        var sent = await TypeIntoRunAsync(url) && SendKeysFocused(RemoteInput.Chord(RemoteInput.Enter));
        Log.Info($"{Connection.Title}: typed the link ({url.Length} characters), Enter {(sent ? "sent" : "refused")}");
        return sent;
    }

    /// <summary>
    /// Types into the open Run box one character at a time, as by hand: Run completes from its history while one types,
    /// and a burst got mixed up with its completion (DEV-13: half an address lost, a part repeated). A completion left
    /// selected after the text is dropped with Delete, else Enter would run that instead. False when a key was refused
    /// or the keyboard layout cannot type a character.
    /// </summary>
    async Task<bool> TypeIntoRunAsync(string text)
    {
        if (RemoteInput.Text(text) is null) return false;
        foreach (var ch in text)
        {
            if (!SendKeysFocused(RemoteInput.Text(ch.ToString())!)) return false;
            await Task.Delay(12);
        }
        await Task.Delay(300);
        return SendKeysFocused(RemoteInput.Chord(RemoteInput.Delete));
    }

    /// <summary>
    /// Files from this PC where they were dropped on the session: put on the clipboard, which the session shares, then a
    /// click at <paramref name="at"/> (the desktop, a folder there) and Ctrl+V, as when dragging within Windows; a
    /// folder pastes into itself wherever in it the click lands. Windows there copies them with its own progress. They
    /// stay on this PC's clipboard (the server reads them only while pasting). False when it could not be done.
    /// </summary>
    public async Task<bool> PasteFilesAsync(IReadOnlyList<string> files, System.Drawing.Point at)
    {
        if (!IsReady || !SharesClipboard) return false;
        var list = new System.Collections.Specialized.StringCollection();
        foreach (var file in files) list.Add(file);
        try { Clipboard.SetFileDropList(list); }
        catch (ExternalException ex) // another program holds the clipboard
        {
            Log.Warn($"{Connection.Title}: the dropped files could not be put on the clipboard: {ex.Message}");
            return false;
        }
        Log.Info($"{Connection.Title}: {files.Count} dropped item(s) on the clipboard, pasting them on the remote desktop");
        await Task.Delay(300); // the client offers the new clipboard to the server
        // While dragging, the remote pointer stayed where it was (the drag holds the mouse): moved to the drop and clicked
        // there, so that the paste goes to what is under it and not to the window used last.
        FocusRemote();
        if (!RemoteInput.Click(RemoteInput.InputWindow(_rdp.Handle), at)) return false;
        await Task.Delay(400); // the click reaching the server, the window under it coming forward
        return SendKeysFocused(RemoteInput.Chord(RemoteInput.Ctrl, RemoteInput.V));
    }

    /// <summary>The account the session signed in with (as given to the control), or null.</summary>
    public string? SignedInUser => Account is { Length: > 0 } account ? account : null;

    /// <summary>A sign-out is on its way by another route than typing: the disconnect it brings closes the window.</summary>
    public void ExpectSignOut() => _intent = Intent.SignOut;

    public void CancelSignOut()
    {
        if (_intent == Intent.SignOut) _intent = Intent.None;
    }

    /// <summary>
    /// Sends Ctrl+Alt+Del: the server shows its security screen (lock, change password, Task Manager, sign out), also over
    /// the lock screen. Succeeds once that page is painted: the server switches to its secure desktop through a black frame
    /// first, which the caller keeps covered until then.
    /// </summary>
    public async Task<bool> SendCtrlAltDelAsync()
    {
        if (_disposed || _rdp.GetOcx() == null) return false;
        FocusRemote();
        await Task.Delay(150); // focus settles
        var before = Sample();
        if (!SendKeys(RemoteInput.Chord(RemoteInput.Ctrl, RemoteInput.Alt, RemoteInput.Delete))) return false;
        if (before == null)
        {
            await Task.Delay(700); // cannot see the session: a fixed wait
            return true;
        }
        for (var waited = 0; waited < 3000; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now || Changed(before, now) <= 0.6) continue;
            // Past the black frame (unless the page really is black), then settled.
            for (var black = 0; black < 1500 && BlackFraction() > 0.9; black += 50) await Task.Delay(50);
            await WaitForStillAsync(150, 800);
            return true;
        }
        Log.Warn($"{Connection.Title}: the Ctrl+Alt+Del screen did not open");
        return false;
    }

    /// <summary>
    /// Win+R, then waits until something opened on the remote screen: a freshly logged-on server can take well over a
    /// second, and keys typed before land elsewhere (Start search, seen on DEV-12). False when nothing opened.
    /// </summary>
    double _runOpened; // how much of the screen Win+R changed last time; 0 when the session could not be seen

    async Task<bool> OpenRunAsync()
    {
        _runOpened = 0;
        if (_disposed || _rdp.GetOcx() == null) return false;
        var before = Sample();
        if (!SendKeysFocused(RemoteInput.Chord(RemoteInput.WinKey, RemoteInput.R))) return false;
        if (before == null)
        {
            await Task.Delay(1500); // cannot see the session: a generous fixed wait
            return true;
        }
        // The Run dialog first flashes up as an unpainted box at the top left, then moves into place and only then takes
        // the keyboard (DEV-12): wait for a change, then for the screen to settle.
        int[]? previous = null;
        var settled = 0;
        for (var waited = 0; waited < 4000; waited += 100)
        {
            await Task.Delay(100);
            if (Sample() is not { } now) continue;
            if (Changed(before, now) < 0.005)
            {
                previous = null;
                continue;
            }
            settled = previous != null && Changed(previous, now) < 0.002 ? settled + 1 : 0;
            previous = now;
            if (settled < 2) continue;
            _runOpened = Changed(before, now);
            Log.Info($"{Connection.Title}: Win+R opened something after {waited + 100} ms ({Changed(before, now):P1} of the screen changed)");
            if (Environment.GetEnvironmentVariable("SESSIONWRIGHT_PROBE") is { Length: > 0 } dump)
            {
                try { _probe?.Save(System.IO.Path.Combine(dump, $"probe-{DateTime.Now:HHmmss-fff}.png")); }
                catch (Exception ex) { Log.Warn($"{Connection.Title}: probe dump failed: {ex.Message}"); }
            }
            await Task.Delay(200);
            return true;
        }
        Log.Warn($"{Connection.Title}: nothing opened on Win+R within 4 s");
        return false;
    }
}
