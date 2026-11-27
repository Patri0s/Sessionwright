using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>Which file or folder on the server to download: a path, typed (a terminal's window asks, see SshWindow).</summary>
public partial class PathPrompt : FluentWindow
{
    PathPrompt(Window owner, string title)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Owner = owner;
        TitleText.Text = title;
        Loaded += (_, _) => PathBox.Focus();
    }

    /// <summary>The path as typed, without the spaces around it; null when cancelled.</summary>
    public static string? Ask(Window owner, string title)
    {
        var prompt = new PathPrompt(owner, title);
        return prompt.ShowDialog() == true ? prompt.PathBox.Text.Trim() : null;
    }

    void PathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => OkButton.IsEnabled = PathBox.Text.Trim().Length > 0;

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
