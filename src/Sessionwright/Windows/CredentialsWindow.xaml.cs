using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Sessionwright;

public partial class CredentialsWindow : FluentWindow
{
    static Store Store => App.Store;
    public ObservableCollection<Credential> Credentials => Store.Data.Credentials;

    public CredentialsWindow(Credential? select = null)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        DataContext = this;
        Title = Bar.Title = Loc.Get("Credentials");
        List.SelectedItem = select ?? Credentials.FirstOrDefault();
        UpdateForm();
        Closing += (_, _) =>
        {
            if (Current is { } c) Commit(c);
            RemoveEmpty();
        };
        // Credentials are edited in place; persist on close (MainWindow refresh also saves).
        Closed += (_, _) => Save();
    }

    // A typed password counts as it is (an empty box keeps the stored one); a credential always has a name.
    void Commit(Credential c)
    {
        if (PasswordBox.Password.Length > 0) c.SetPassword(PasswordBox.Password);
        if (string.IsNullOrWhiteSpace(c.Name)) c.Name = string.IsNullOrWhiteSpace(c.User) ? Loc.Get("NewCredential") : c.User;
    }

    // Added but never filled in: dropped, unless something already points to it.
    void RemoveEmpty()
    {
        foreach (var c in Credentials.Where(c => (string.IsNullOrWhiteSpace(c.Name) || c.Name == Loc.Get("NewCredential"))
                                                 && string.IsNullOrWhiteSpace(c.User) && c.Secret == null && Usage(c) == 0).ToList())
            Credentials.Remove(c);
    }

    void Save()
    {
        try
        {
            Store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving credentials failed");
            Dialog.Show(IsLoaded ? this : Owner, Loc.Format("SaveFailed", ex.Message), DialogKind.Error);
        }
    }

    Credential? Current => List.SelectedItem as Credential;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var c in e.RemovedItems.OfType<Credential>()) Commit(c);
        UpdateForm();
    }

    void UpdateForm()
    {
        Form.IsEnabled = DeleteButton.IsEnabled = Current != null;
        EmptyState.Visibility = Credentials.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Password = "";
        // A stored password is never shown: the box says it is there, and a button takes it away.
        PasswordBox.PlaceholderText = Current?.Secret != null ? Loc.Get("PasswordKept") : "";
        RemovePasswordButton.Visibility = Current?.Secret != null ? Visibility.Visible : Visibility.Collapsed;
        PasswordState.Text = Current == null ? "" : Loc.Get(Current.Secret != null ? "PasswordStored" : "PasswordNotStored");
        UsageText.Text = Current == null ? "" : Loc.Format("UsedBy", Usage(Current));
        // What protects the passwords now: Windows for this account, or the user's master password.
        ProtectionNote.Text = Loc.Get(App.Store.Data.Settings.Master != null ? "MasterNote" : "DpapiNote");
    }

    // Also as the account a VPN signs in with before connecting: a credential used only there is not unused.
    static int Usage(Credential c) =>
        Store.Data.Groups.Cast<Node>().Concat(Store.Data.Connections).Count(n => n.CredentialId == c.Id || n.BeforeConnect?.VpnCredentialId == c.Id);

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var c = new Credential { Name = Loc.Get("NewCredential") };
        Credentials.Add(c);
        List.SelectedItem = c;
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c) return;
        if (!Dialog.Confirm(this, Loc.Format("ConfirmDeleteCredential", c.Name, Usage(c)), Loc.Get("Delete"), DialogKind.Warning, danger: true)) return;
        var users = Store.Data.Groups.Cast<Node>().Concat(Store.Data.Connections).Where(n => n.CredentialId == c.Id).ToList();
        var vpns = Store.Data.Groups.Cast<Node>().Concat(Store.Data.Connections).Select(n => n.BeforeConnect).OfType<BeforeConnect>()
            .Where(b => b.VpnCredentialId == c.Id).ToList();
        Store.DeleteCredential(c);
        List.SelectedItem = Credentials.FirstOrDefault();
        // For a while it can come back, with everything that used it (unless that got another credential meanwhile).
        Notification.Show(this, SymbolRegular.Delete24, Loc.Format("DeletedOne", c.Name), "", TimeSpan.FromSeconds(20), (Loc.Get("Undo"), () =>
        {
            if (!Credentials.Contains(c)) Credentials.Add(c);
            foreach (var n in users.Where(n => n.CredentialId == null)) n.CredentialId = c.Id;
            foreach (var b in vpns.Where(b => b.VpnCredentialId == null)) b.VpnCredentialId = c.Id;
            if (IsVisible) List.SelectedItem = c;
            Save();
        }));
    }

    // Saved on leaving the box, like the fields next to it: a button to press after typing was easy to miss.
    void PasswordBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c || PasswordBox.Password.Length == 0) return;
        c.SetPassword(PasswordBox.Password);
        Save();
        UpdateForm();
    }

    // Without a stored password Sessionwright asks for it on every connection.
    void RemovePassword_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c) return;
        c.SetPassword(null);
        Save();
        UpdateForm();
    }
}
