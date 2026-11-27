using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// Asks for a password: the one that protects the passwords in an export (set it, or enter it to import), or the
/// master password (set it, or enter it to unlock the saved passwords).
/// </summary>
public partial class PasswordPrompt : FluentWindow
{
    public const int MinLength = 8;
    readonly bool _confirm;
    readonly int _minLength;

    PasswordPrompt(Window? owner, string message, bool confirm, string? error, string button, int minLength, bool remember, string? label = null)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        // The master password may be asked for before any window shows (a link that starts the app).
        if (owner is { IsVisible: true }) Owner = owner;
        else (WindowStartupLocation, ShowInTaskbar, Topmost) = (WindowStartupLocation.CenterScreen, true, true);
        (_confirm, _minLength) = (confirm, minLength);
        MessageText.Text = message;
        if (label != null)
        {
            PasswordLabel.Text = label;
            System.Windows.Automation.AutomationProperties.SetName(PasswordBox, label);
        }
        ConfirmPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        RememberBox.Visibility = remember ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = error ?? "";
        PrimaryButton.Content = button;
        SecondaryButton.Content = Loc.Get("Cancel");
        PasswordBox.PasswordChanged += (_, _) => ErrorText.Text = "";
        ConfirmBox.PasswordChanged += (_, _) => ErrorText.Text = "";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    /// <summary>
    /// The password, or null when cancelled. With <paramref name="confirm"/> it is set: typed twice, at least
    /// <paramref name="minLength"/> characters.
    /// </summary>
    public static string? Ask(Window? owner, string message, bool confirm, string? error = null, string? button = null, int minLength = MinLength,
        string? label = null)
    {
        var prompt = new PasswordPrompt(owner, message, confirm, error, button ?? Loc.Get(confirm ? "ExportButton" : "ImportButton"), minLength, remember: false, label);
        return prompt.ShowDialog() == true ? prompt.PasswordBox.Password : null;
    }

    /// <summary>A password with a box to keep it, worded by the caller (<paramref name="keep"/>); null when cancelled.</summary>
    public static (string Password, bool Keep)? AskToKeep(Window? owner, string message, string button, string label, string keep)
    {
        var prompt = new PasswordPrompt(owner, message, confirm: false, null, button, 0, remember: true, label);
        prompt.RememberBox.Content = keep;
        return prompt.ShowDialog() == true ? (prompt.PasswordBox.Password, prompt.RememberBox.IsChecked == true) : null;
    }

    /// <summary>The master password to unlock with and whether to remember it on this PC; null when cancelled.</summary>
    public static (string Password, bool Remember)? AskMaster(Window? owner, string? error)
    {
        var prompt = new PasswordPrompt(owner, Loc.Get("MasterAsk"), confirm: false, error, Loc.Get("MasterUnlock"), 0, remember: true, Loc.Get("MasterPassword"));
        return prompt.ShowDialog() == true ? (prompt.PasswordBox.Password, prompt.RememberBox.IsChecked == true) : null;
    }

    void Primary_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        ErrorText.Text = !_confirm ? (password.Length == 0 ? Loc.Get("ExportPasswordMissing") : "")
            : password.Length < _minLength ? Loc.Format("ExportPasswordShort", _minLength)
            : password != ConfirmBox.Password ? Loc.Get("ExportPasswordMismatch")
            : "";
        if (ErrorText.Text.Length > 0)
        {
            PasswordBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
