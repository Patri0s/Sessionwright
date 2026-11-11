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

    // A typed password counts without "Set password" (an empty box keeps the stored one); a credential always has a name.
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
        PasswordState.Text = Current == null ? "" : Loc.Get(Current.Secret != null ? "PasswordStored" : "PasswordNotStored");
        UsageText.Text = Current == null ? "" : Loc.Format("UsedBy", Usage(Current));
    }

    static int Usage(Credential c) =>
        Store.Data.Groups.Count(g => g.CredentialId == c.Id) + Store.Data.Connections.Count(x => x.CredentialId == c.Id);

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
        Store.DeleteCredential(c);
        List.SelectedItem = Credentials.FirstOrDefault();
        // For a while it can come back, with everything that used it (unless that got another credential meanwhile).
        Notification.Show(this, SymbolRegular.Delete24, Loc.Format("DeletedOne", c.Name), "", TimeSpan.FromSeconds(20), (Loc.Get("Undo"), () =>
        {
            if (!Credentials.Contains(c)) Credentials.Add(c);
            foreach (var n in users.Where(n => n.CredentialId == null)) n.CredentialId = c.Id;
            if (IsVisible) List.SelectedItem = c;
            Save();
        }));
    }

    void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c) return;
        // Empty password clears the stored secret, so Sessionwright will ask on connect.
        c.SetPassword(PasswordBox.Password);
        Save();
        UpdateForm();
    }
}
