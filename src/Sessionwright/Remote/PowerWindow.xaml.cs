using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>Restart or shut down the machines of the chosen connections: when, with which message, closing programs or not.</summary>
public partial class PowerWindow : FluentWindow
{
    static readonly int[] DelayMinutes = [0, 1, 5, 15, 30];
    bool _messageEdited, _settingMessage;

    /// <summary>What the user chose; null when cancelled.</summary>
    public PowerRequest? Request { get; private set; }

    public PowerWindow(IReadOnlyList<Connection> targets, bool restart = true)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Title = Bar.Title = Loc.Get("PowerTitle");
        TargetsText.Text = string.Join(", ", targets.Select(c => c.Title));
        var azure = targets.Count(c => c.AzureVm != null);
        RoutesText.Text = string.Join(" ", new[]
        {
            azure > 0 ? Loc.Get("PowerRouteAzure") : null,
            azure < targets.Count ? Loc.Get("PowerRouteDirect") : null,
        }.OfType<string>());
        DelayBox.ItemsSource = DelayMinutes.Select(m => new Choice(m, m == 0 ? Loc.Get("PowerNow") : Loc.Format("PowerInMinutes", m))).ToList();
        Choice.Select(DelayBox, 5);
        Changed(this, new RoutedEventArgs());
        if (!restart) ShutdownBox.IsChecked = true;
        Loaded += async (_, _) => await ShowOthersAsync(targets);
    }

    async Task ShowOthersAsync(IReadOnlyList<Connection> targets)
    {
        var since = DateTime.Now.AddHours(-12);
        var found = await Task.WhenAll(targets.Select(async c => (await SharedUse.OthersAsync(c, since)).Select(n => $"{c.Title}: {n}")));
        var lines = found.SelectMany(l => l).ToList();
        if (!IsLoaded) return;
        SharedRing.Visibility = Visibility.Collapsed;
        if (lines.Count == 0)
        {
            SharedPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SharedIcon.Visibility = Visibility.Visible;
        SharedText.Text = Loc.Get("SharedWarning") + "\n" + string.Join("\n", lines);
    }

    int Minutes => Choice.Get(DelayBox) as int? ?? 0;
    bool Restart => RestartBox.IsChecked == true;

    // The default message follows the choices until the user writes one of their own.
    void Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || DelayBox.SelectedItem == null) return;
        GoButton.Content = Loc.Get(Restart ? "PowerRestart" : "PowerShutdown");
        if (_messageEdited) return;
        _settingMessage = true;
        MessageText.Text = Minutes == 0
            ? Loc.Get(Restart ? "PowerDefaultRestartNow" : "PowerDefaultShutdownNow")
            : Loc.Format(Restart ? "PowerDefaultRestart" : "PowerDefaultShutdown", Minutes);
        _settingMessage = false;
    }

    void MessageBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_settingMessage) _messageEdited = true;
    }

    void Go_Click(object sender, RoutedEventArgs e)
    {
        Request = new PowerRequest(Restart, TimeSpan.FromMinutes(Minutes), MessageText.Text.Trim() is { Length: > 0 } m ? m : null, ForceBox.IsChecked == true);
        DialogResult = true;
    }
}
