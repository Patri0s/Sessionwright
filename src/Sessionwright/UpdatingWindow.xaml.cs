using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// What the user sees while the app closes to install an update, in its own look: Velopack's progress window is
/// silenced. It goes with this process; the new version starts a few seconds later and says it was updated.
/// </summary>
public partial class UpdatingWindow : FluentWindow
{
    readonly System.Diagnostics.Stopwatch _shown = new();

    UpdatingWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = Loc.Format("UpdatingText", version);
    }

    /// <summary>
    /// Shown and painted before the caller goes on: closing sessions and exiting keep the thread busy.
    /// <paramref name="applyTheme"/>: at start-up, before any other window has set the user's theme.
    /// </summary>
    public static UpdatingWindow ShowFor(string version, bool applyTheme = false)
    {
        var window = new UpdatingWindow(version);
        if (applyTheme) App.ApplyTheme(window);
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window._shown.Start();
        return window;
    }

    /// <summary>
    /// Keeps it on screen until it could be read: handing over to the updater takes a moment only, and a window that
    /// flashes and goes says nothing. The updater's own work (a few seconds) follows with nothing on screen.
    /// </summary>
    public void HoldUntilRead()
    {
        var left = TimeSpan.FromMilliseconds(1500) - _shown.Elapsed;
        if (left <= TimeSpan.Zero) return;
        // Waited with the window alive (its progress bar moving), not with the thread asleep.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = left };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
