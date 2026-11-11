using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Sessionwright;

public partial class CredentialPrompt : FluentWindow
{
    /// <summary>Library credential the user picked, or null for manual entry.</summary>
    public Credential? Credential { get; private set; }
    public string User { get; private set; } = "";
    public string? Domain { get; private set; }
    public string Password { get; private set; } = "";
    public bool SaveCredential { get; private set; }
    public bool SaveConnection { get; private set; }

    readonly Credential? _unreadable;
    bool _saveByDefault;

    /// <param name="secretUnreadable">The preselected credential's password could not be decrypted (DPAPI, other account):
    /// say so and offer to save the one typed now in its place.</param>
    public CredentialPrompt(Connection connection, Credential? preselected, bool connectionIsStored, bool secretUnreadable = false)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Title = Bar.Title = Loc.Format("ConnectToTitle", connection.Title);
        AddressText.Text = connection.Address;
        SaveConnectionBox.Visibility = connectionIsStored ? Visibility.Collapsed : Visibility.Visible;
        _unreadable = secretUnreadable ? preselected : null;
        PasswordBox.PasswordChanged += (_, _) =>
        {
            ErrorText.Text = "";
            UpdateSaveBox();
        };
        UserBox.TextChanged += (_, _) => ErrorText.Text = "";

        CredentialBox.ItemsSource = App.Store.Data.Credentials
            .OrderBy(x => x.Name, Store.NaturalOrder)
            .Select(x => new Choice(x, x.ToString()))
            .Prepend(new Choice(null, Loc.Get("ManualCredential")))
            .ToList();
        Choice.Select(CredentialBox, preselected);

        Loaded += (_, _) => (UserBox.IsEnabled && UserBox.Text.Length == 0 ? (Control)UserBox : PasswordBox).Focus();
    }

    void CredentialBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var c = Choice.Get(CredentialBox) as Credential;
        var unreadable = c != null && c == _unreadable;
        // A library credential without a user name still needs one to connect.
        UserBox.IsEnabled = DomainBox.IsEnabled = c == null || string.IsNullOrEmpty(c.User);
        UserBox.Text = c?.User ?? "";
        DomainBox.Text = c?.Domain ?? "";
        PasswordBox.Password = "";
        PasswordBox.PlaceholderText = c?.Secret != null && !unreadable ? Loc.Get("SavedPassword") : "";
        SaveCredentialBox.Content = Loc.Get(c == null ? "SaveToLibrary" : "SavePasswordInCredential");
        _saveByDefault = c != null && (c.Secret == null || unreadable);
        ErrorText.Text = unreadable ? Loc.Get("SecretUnreadableHint") : "";
        UpdateSaveBox();
    }

    // An empty password is never saved, so the box only counts once one is typed; then it starts at its default.
    void UpdateSaveBox()
    {
        var empty = PasswordBox.Password.Length == 0;
        if (empty || !SaveCredentialBox.IsEnabled) SaveCredentialBox.IsChecked = !empty && _saveByDefault;
        SaveCredentialBox.IsEnabled = !empty;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Credential = Choice.Get(CredentialBox) as Credential;
        User = UserBox.Text.Trim();
        Domain = DomainBox.Text.Trim() is { Length: > 0 } d ? d : null;
        Password = PasswordBox.Password;
        if (User.Length == 0)
        {
            UserBox.Focus();
            ErrorText.Text = Loc.Get("UserRequired");
            return;
        }
        if (Password.Length == 0 && Credential?.Secret is { } secret)
        {
            try { Password = Store.Unprotect(secret); }
            catch (CryptographicException)
            {
                PasswordBox.Focus();
                ErrorText.Text = Loc.Get("SecretUnreadable");
                return;
            }
        }
        SaveCredential = SaveCredentialBox.IsChecked == true;
        SaveConnection = SaveConnectionBox.IsChecked == true;
        DialogResult = true;
    }
}
