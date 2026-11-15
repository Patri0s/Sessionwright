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
    /// Signs out by typing Win+R, "logoff", Enter. Returns whether every key was delivered; the window then waits for the
    /// session to end, and calls <see cref="CancelSignOut"/> if it does not.
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
        if (!await OpenRunAsync() || RemoteInput.Text("logoff") is not { } logoff || !SendKeysFocused(logoff)) return false;
        // Run executes "logoff" on Enter at once; Start search (see OpenRunAsync) only once its results are in, which took
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
        // Enter before Start search had its results does nothing: one more, but only if the screen did not change at
        // all (an extra Enter could confirm a "save changes?" prompt of a closing app).
        for (var waited = 0; waited < 4000 && !_disposed; waited += 100) await Task.Delay(100);
        if (!_disposed && _intent == Intent.SignOut && atEnter != null && Sample() is { } now && Changed(atEnter, now) < 0.005)
        {
            Log.Info($"{Title}: Enter changed nothing on screen, pressing Enter once more");
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
        if (clock.ElapsedMilliseconds > 400) Log.Info($"{Title}: screen still changing after logon, waited {clock.ElapsedMilliseconds} ms more");
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the session's browser: Win+R, the address, Enter. Only a safe web address
    /// (<see cref="Links.IsSafe"/>): Run would run anything else as a command. False when it could not be typed.
    /// </summary>
    public async Task<bool> OpenInSessionAsync(string url)
    {
        if (!Links.IsSafe(url) || !IsReady) return false;
        Log.Info($"{Title}: opening a link in the session");
        if (!await OpenRunAsync()) return false;
        if (RemoteInput.Text(url) is null) return false; // a character the keyboard layout cannot type
        // One character at a time, as typed by hand: Run completes from its history while one types, and a burst of a
        // whole address got mixed up with its completion (DEV-13: half the address lost, a part repeated).
        foreach (var ch in url)
        {
            if (!SendKeysFocused(RemoteInput.Text(ch.ToString())!)) return false;
            await Task.Delay(12);
        }
        await Task.Delay(300);
        // A completion appended after the address stays selected: Delete drops it, else Enter would open that instead.
        var sent = SendKeysFocused(RemoteInput.Chord(RemoteInput.Delete)) && SendKeysFocused(RemoteInput.Chord(RemoteInput.Enter));
        Log.Info($"{Title}: typed the link ({url.Length} characters), Enter {(sent ? "sent" : "refused")}");
        return sent;
    }

    /// <summary>The account the session signed in with (as given to the control), or null.</summary>
    public string? SignedInUser => string.IsNullOrEmpty(_user) ? null : string.IsNullOrEmpty(_domain) ? _user : $@"{_domain}\{_user}";

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
        Log.Warn($"{Title}: the Ctrl+Alt+Del screen did not open");
        return false;
    }

    /// <summary>
    /// Win+R, then waits until something opened on the remote screen: a freshly logged-on server can take well over a
    /// second, and keys typed before land elsewhere (Start search, seen on DEV-12). False when nothing opened.
    /// </summary>
    async Task<bool> OpenRunAsync()
    {
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
            Log.Info($"{Title}: Win+R opened something after {waited + 100} ms ({Changed(before, now):P1} of the screen changed)");
            if (Environment.GetEnvironmentVariable("SESSIONWRIGHT_PROBE") is { Length: > 0 } dump)
            {
                try { _probe?.Save(System.IO.Path.Combine(dump, $"probe-{DateTime.Now:HHmmss-fff}.png")); }
                catch (Exception ex) { Log.Warn($"{Title}: probe dump failed: {ex.Message}"); }
            }
            await Task.Delay(200);
            return true;
        }
        Log.Warn($"{Title}: nothing opened on Win+R within 4 s");
        return false;
    }
}
