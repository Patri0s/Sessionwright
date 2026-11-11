using System.Windows;
using Wpf.Ui.Controls;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

public enum DialogKind { Info, Warning, Error, Question }

/// <summary>Fluent replacement for System.Windows.MessageBox, so every prompt matches the app's look.</summary>
public partial class Dialog : FluentWindow
{
    Dialog(Window? owner, string message, DialogKind kind, string primary, string? secondary, bool danger)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        // A window that was never shown (errors during startup) or is going away cannot own a dialog.
        if (owner is { IsLoaded: true, IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Over a session on all monitors, centering on the owner would put the dialog between monitors: it goes to the
        // monitor with the full-screen bar. Moved there before it is shown, centered once it has its size at that DPI:
        // on ContentRendered, while it is still cloaked (a move on Loaded was undone by WPF placing the window afterwards).
        if ((Owner as SessionWindow)?.MessageMonitor is { } monitor)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => Native.SetPosition(this, monitor.X + monitor.Width / 3, monitor.Y + monitor.Height / 3);
            ContentRendered += (_, _) =>
            {
                var size = Native.Bounds(this);
                Native.SetPosition(this, monitor.X + (monitor.Width - size.Width) / 2, monitor.Y + (monitor.Height - size.Height) / 2);
            };
        }

        MessageText.Text = message;
        (Glyph.Symbol, var brush) = kind switch
        {
            DialogKind.Warning => (SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
            DialogKind.Error => (SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
            DialogKind.Question => (SymbolRegular.QuestionCircle24, "AccentTextFillColorPrimaryBrush"),
            _ => (SymbolRegular.Info24, "AccentTextFillColorPrimaryBrush"),
        };
        Glyph.SetResourceReference(ForegroundProperty, brush); // follows a theme switch

        PrimaryButton.Content = primary;
        if (danger) PrimaryButton.Appearance = ControlAppearance.Danger;
        if (secondary == null) SecondaryButton.Visibility = Visibility.Collapsed;
        else SecondaryButton.Content = secondary;
    }

    bool _alternative;

    void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    void Alternative_Click(object sender, RoutedEventArgs e)
    {
        _alternative = true;
        DialogResult = true;
    }

    // An active tool window (the full-screen bar) is no owner to center on: its owner is.
    static Window? DefaultOwner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) is { } active
        ? active is FullScreenBar ? active.Owner : active
        : Application.Current?.MainWindow;

    public static void Show(Window? owner, string message, DialogKind kind = DialogKind.Info) =>
        new Dialog(owner ?? DefaultOwner, message, kind, "OK", null, false).ShowDialog();

    /// <summary>
    /// Returns true when the user picks the primary action. <paramref name="more"/>: a part of the message still being
    /// found out; <paramref name="waiting"/> shows meanwhile, then its text (nothing when it has none).
    /// </summary>
    public static bool Confirm(Window? owner, string message, string confirmText, DialogKind kind = DialogKind.Question, bool danger = false,
                               string? cancelText = null, Task<string?>? more = null, string? waiting = null)
    {
        var dialog = new Dialog(owner ?? DefaultOwner, message, kind, confirmText, cancelText ?? Loc.Get("Cancel"), danger);
        if (more != null) dialog.Loaded += async (_, _) => await dialog.ShowMoreAsync(more, waiting);
        return dialog.ShowDialog() == true;
    }

    async Task ShowMoreAsync(Task<string?> more, string? waiting)
    {
        MorePanel.Visibility = Visibility.Visible;
        MoreText.Text = waiting ?? "";
        string? text = null;
        try { text = await more; }
        catch (Exception ex) { Log.Info($"Dialog: the rest of the message is unavailable: {ex.Message}"); } // the question stands without it
        MoreRing.Visibility = Visibility.Collapsed;
        MorePanel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        MoreText.Text = text ?? "";
        MoreText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    /// <summary>Two actions and Cancel: 1 for the primary one, 2 for the alternative, 0 for Cancel (or the window closed).</summary>
    public static int Choose(Window? owner, string message, string primary, string alternative, DialogKind kind = DialogKind.Question)
    {
        var dialog = new Dialog(owner ?? DefaultOwner, message, kind, primary, Loc.Get("Cancel"), false);
        dialog.AlternativeButton.Content = alternative;
        dialog.AlternativeButton.Visibility = Visibility.Visible;
        return dialog.ShowDialog() != true ? 0 : dialog._alternative ? 2 : 1;
    }

    /// <summary>
    /// Keeps a tall window within the work area of the main window's monitor, so its footer is never off-screen. Called
    /// in the constructor: the window opens centered over the main window (Owner is assigned only after construction).
    /// </summary>
    public static void FitToScreen(Window w)
    {
        if (Application.Current?.MainWindow is not { IsLoaded: true } main) return;
        var area = Screen.FromHandle(Native.Handle(main)).WorkingArea;
        w.Height = Math.Min(w.Height, area.Height * 0.9 * 96 / Native.DpiOf(main));
    }
}
