using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>Lists the virtual machines of all signed-in Azure accounts to link one to a connection.</summary>
public partial class VmPickerWindow : FluentWindow
{
    public static readonly IValueConverter StateText = new StateConverter();

    // The last list: shown at once next time while a fresh one loads (going through every directory and subscription takes seconds).
    static (List<VmInfo> Vms, List<string> Problems, List<AzureTenantSignIn> SignIns)? _cache;
    bool _focused;

    readonly AzureVmLink? _current;
    readonly string _host;
    List<VmInfo> _all = [];

    VmPickerWindow(Window owner, AzureVmLink? current, string host)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Owner = owner;
        Title = Bar.Title = Loc.Get("AzureVmPick");
        (_current, _host) = (current, host);
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>The chosen VM, or null when cancelled.</summary>
    public static VmInfo? Pick(Window owner, AzureVmLink? current, string host)
    {
        var window = new VmPickerWindow(owner, current, host);
        return window.ShowDialog() == true ? window.VmList.SelectedItem as VmInfo : null;
    }

    async Task LoadAsync()
    {
        AddAccountButton.Visibility = Visibility.Collapsed;
        SignInList.ItemsSource = null;
        if (App.Store.Data.Settings.AzureAccounts.Count == 0)
        {
            ShowStatus(Loc.Get("AzureNoAccounts"), busy: false, SymbolRegular.PersonAdd24);
            AddAccountButton.Visibility = Visibility.Visible;
            return;
        }
        var accounts = App.Store.Data.Settings.AzureAccounts.Select(a => a.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_cache is { } cached)
            Show([.. cached.Vms.Where(v => accounts.Contains(v.Account))], cached.Problems, [.. cached.SignIns.Where(s => accounts.Contains(s.Account.Username))]);
        else ShowStatus(Loc.Get("AzureVmLoading"), busy: true);
        RefreshRing.Visibility = _cache != null ? Visibility.Visible : Visibility.Collapsed;
        var problems = new List<string>();
        var signIns = new List<AzureTenantSignIn>();
        var vms = (await AzureVms.ListAllAsync(problems, signIns))
            .OrderBy(v => v.Name, Store.NaturalOrder).ThenBy(v => v.SubscriptionName, Store.NaturalOrder).ToList();
        _cache = (vms, problems, signIns);
        RefreshRing.Visibility = Visibility.Collapsed;
        if (IsLoaded) Show(vms, problems, signIns);
    }

    void Show(List<VmInfo> vms, List<string> problems, List<AzureTenantSignIn> signIns)
    {
        var picked = (VmList.SelectedItem as VmInfo)?.Link;
        _all = vms;
        ProblemsText.Text = string.Join("\n", problems);
        SignInList.ItemsSource = signIns;
        Filter();
        // What was picked, else the VM it is linked to, else the one named like the host (an Azure DNS label is usually the VM name).
        var label = _host.Split('.')[0];
        VmInfo? Find(AzureVmLink? link) => link == null ? null : _all.FirstOrDefault(v => AzureVms.Same(v.Link, link));
        VmList.SelectedItem = Find(picked) ?? Find(_current) ?? _all.FirstOrDefault(v => string.Equals(v.Name, label, StringComparison.OrdinalIgnoreCase));
        if (VmList.SelectedItem != null) VmList.ScrollIntoView(VmList.SelectedItem);
        if (_focused) return;
        _focused = true;
        SearchBox.Focus();
    }

    void Filter()
    {
        var tokens = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _all.Where(v => tokens.All(t => $"{v.Name} {v.ResourceGroup} {v.SubscriptionName} {v.Account} {v.Location}"
            .Contains(t, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var selected = VmList.SelectedItem;
        VmList.ItemsSource = shown;
        if (selected != null && shown.Contains(selected)) VmList.SelectedItem = selected;
        if (shown.Count > 0) StatusPanel.Visibility = Visibility.Collapsed;
        else ShowStatus(Loc.Get(_all.Count == 0 ? "AzureVmNone" : "AzureVmNoMatch"), busy: false, _all.Count == 0 ? SymbolRegular.Cloud24 : SymbolRegular.Search24);
    }

    void ShowStatus(string text, bool busy, SymbolRegular icon = SymbolRegular.Cloud24) => StatusPanel.Show(text, busy, icon);

    async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        AddAccountButton.IsEnabled = false;
        ShowStatus(Loc.Get("AzureSignInBrowser"), busy: true);
        try
        {
            var account = await AzureVms.SignInAsync(Native.Handle(this));
            App.Store.Data.Settings.AzureAccounts.RemoveAll(a => string.Equals(a.Username, account.Username, StringComparison.OrdinalIgnoreCase));
            App.Store.Data.Settings.AzureAccounts.Add(account);
            App.Store.Save();
        }
        catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException or OperationCanceledException or System.IO.IOException)
        {
            Log.Error(ex, "Azure sign-in");
            ProblemsText.Text = Loc.Format("AzureSignInFailed", ex.Message);
        }
        finally { AddAccountButton.IsEnabled = true; }
        await LoadAsync();
    }

    async void TenantSignIn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AzureTenantSignIn target) return;
        SignInList.IsEnabled = false;
        ShowStatus(Loc.Get("AzureSignInBrowser"), busy: true);
        try { await AzureVms.ReauthenticateAsync(target.Account, target.TenantId, Native.Handle(this)); }
        catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException or OperationCanceledException)
        {
            Log.Error(ex, $"Azure sign-in to tenant {target.TenantId}");
            ProblemsText.Text = Loc.Format("AzureSignInFailed", ex.Message);
        }
        finally { SignInList.IsEnabled = true; }
        await LoadAsync();
    }

    void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Filter();

    // As in the machines window: Ctrl+F to the search; Esc clears it first, then closes.
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
    }

    void VmList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ChooseButton.IsEnabled = VmList.SelectedItem != null;

    void VmList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // On a row: not on the scroll bar or a group header.
        if (VmList.SelectedItem != null && System.Windows.Controls.ItemsControl.ContainerFromElement(VmList, (DependencyObject)e.OriginalSource) is System.Windows.Controls.ListBoxItem)
            DialogResult = true;
    }

    void Choose_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    sealed class StateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => AzureVms.Describe(value as VmPowerState?);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
