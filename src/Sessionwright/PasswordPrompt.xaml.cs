using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>Asks for the password that protects the passwords in an export (set it, or enter it to import).</summary>
public partial class PasswordPrompt : FluentWindow
{
    public const int MinLength = 8;
    readonly bool _confirm;

    PasswordPrompt(Window owner, string message, bool confirm, string? error)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Owner = owner;
        _confirm = confirm;
        MessageText.Text = message;
        ConfirmPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = error ?? "";
        PrimaryButton.Content = Loc.Get(confirm ? "ExportButton" : "ImportButton");
        SecondaryButton.Content = Loc.Get("Cancel");
        PasswordBox.PasswordChanged += (_, _) => ErrorText.Text = "";
        ConfirmBox.PasswordChanged += (_, _) => ErrorText.Text = "";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    /// <summary>
    /// The password, or null when cancelled. With <paramref name="confirm"/> it is set: typed twice, at least
    /// <see cref="MinLength"/> characters.
    /// </summary>
    public static string? Ask(Window owner, string message, bool confirm, string? error = null)
    {
        var prompt = new PasswordPrompt(owner, message, confirm, error);
        return prompt.ShowDialog() == true ? prompt.PasswordBox.Password : null;
    }

    void Primary_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        ErrorText.Text = !_confirm ? (password.Length == 0 ? Loc.Get("ExportPasswordMissing") : "")
            : password.Length < MinLength ? Loc.Format("ExportPasswordShort", MinLength)
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
