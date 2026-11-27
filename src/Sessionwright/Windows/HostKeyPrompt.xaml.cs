using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>
/// A server ssh has not seen before: its key's fingerprint, to compare before it is trusted. OpenSSH's own question,
/// laid out (see <see cref="Ssh.ParseHostKey"/>); the answer goes back to ssh, which keeps the key.
/// </summary>
public partial class HostKeyPrompt : FluentWindow
{
    HostKeyPrompt(Window owner, HostKeyQuestion question)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Owner = owner;
        TitleText.Text = Loc.Format("HostKeyTitle", question.Host);
        KindText.Text = Loc.Format("HostKeyKind", question.KeyType);
        FingerprintBox.Text = question.Fingerprint;
    }

    /// <summary>True when the user trusts the server.</summary>
    public static bool Ask(Window owner, HostKeyQuestion question) => new HostKeyPrompt(owner, question).ShowDialog() == true;

    void Trust_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(FingerprintBox.Text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // another program holds the clipboard
    }
}

/// <summary>What OpenSSH asks about a server it does not know: which, and its key.</summary>
public sealed record HostKeyQuestion(string Host, string KeyType, string Fingerprint);
