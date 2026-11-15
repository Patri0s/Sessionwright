using System.Windows;
using System.Windows.Controls;

namespace Sessionwright;

public partial class RdpOptionsView : UserControl
{
    public RdpOptionsView()
    {
        InitializeComponent();
        ResolutionBox.ItemsSource = Enum.GetValues<ResolutionMode>().Select(m => new Choice(m, Loc.Get($"Resolution{m}"))).ToList();
        AudioBox.ItemsSource = Enum.GetValues<AudioMode>().Select(m => new Choice(m, Loc.Get($"Audio{m}"))).ToList();
        DepthBox.ItemsSource = new[] { 15, 16, 24, 32 }.Select(d => new Choice(d, Loc.Format("Bits", d))).ToList();
    }

    public void Load(RdpOptions o)
    {
        Choice.Select(ResolutionBox, o.Resolution);
        Choice.Select(AudioBox, o.Audio);
        Choice.Select(DepthBox, o.ColorDepth);
        WidthBox.Text = o.Width.ToString();
        HeightBox.Text = o.Height.ToString();
        ClipboardBox.IsChecked = o.RedirectClipboard;
        DrivesBox.IsChecked = o.RedirectDrives;
        PrintersBox.IsChecked = o.RedirectPrinters;
        SmartCardsBox.IsChecked = o.RedirectSmartCards;
        AdminBox.IsChecked = o.AdminSession;
        ShortcutsBox.IsChecked = o.RemoteShortcuts;
        GatewayBox.Text = o.Gateway ?? "";

        // One box per monitor plugged in now, numbered as Windows numbers them (Settings > Display).
        var screens = System.Windows.Forms.Screen.AllScreens;
        _absent = [.. (o.Monitors ?? []).Where(name => !screens.Any(s => Same(s.DeviceName, name)))];
        MonitorBoxes.Children.Clear();
        foreach (var s in screens)
            MonitorBoxes.Children.Add(new CheckBox
            {
                Content = Loc.Format(s.Primary ? "MonitorChoicePrimary" : "MonitorChoice", Number(s.DeviceName), s.Bounds.Width, s.Bounds.Height),
                Tag = s.DeviceName,
                IsChecked = o.Monitors == null || o.Monitors.Any(name => Same(name, s.DeviceName)),
                Margin = new Thickness(0, 0, 16, 0),
            });
    }

    List<string> _absent = []; // chosen earlier, not plugged in now (the monitors at the office): kept as they are

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"\\.\DISPLAY2" → "2".</summary>
    internal static string Number(string deviceName)
    {
        var digits = new string([.. deviceName.Reverse().TakeWhile(char.IsDigit).Reverse()]);
        return digits.Length > 0 ? digits : deviceName;
    }

    /// <summary>The monitors ticked; null when that is all of them (or none: a full screen on no monitor is all of them).</summary>
    List<string>? Monitors()
    {
        var boxes = MonitorBoxes.Children.OfType<CheckBox>().ToList();
        var ticked = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        return ticked.Count == 0 || ticked.Count == boxes.Count && _absent.Count == 0 ? null : [.. ticked, .. _absent];
    }

    /// <summary>Returns null and shows nothing when the size is invalid; caller reports the error.</summary>
    public RdpOptions? Read()
    {
        var resolution = (ResolutionMode)Choice.Get(ResolutionBox)!;
        if (Size(WidthBox.Text) is not { } w || Size(HeightBox.Text) is not { } h)
        {
            if (resolution == ResolutionMode.Fixed) return null;
            (w, h) = (1920, 1080);
        }
        return new RdpOptions
        {
            Resolution = resolution,
            Width = w,
            Height = h,
            ColorDepth = (int)Choice.Get(DepthBox)!,
            Audio = (AudioMode)Choice.Get(AudioBox)!,
            RedirectClipboard = ClipboardBox.IsChecked == true,
            RedirectDrives = DrivesBox.IsChecked == true,
            RedirectPrinters = PrintersBox.IsChecked == true,
            RedirectSmartCards = SmartCardsBox.IsChecked == true,
            AdminSession = AdminBox.IsChecked == true,
            RemoteShortcuts = ShortcutsBox.IsChecked == true,
            Gateway = GatewayBox.Text.Trim() is { Length: > 0 } g ? g : null,
            Monitors = resolution == ResolutionMode.AllMonitors ? Monitors() : null,
        };
    }

    /// <summary>After <see cref="Read"/> returned null: the size field to fix.</summary>
    public TextBox InvalidSizeBox => Size(WidthBox.Text) == null ? WidthBox : HeightBox;

    static int? Size(string text) => int.TryParse(text, out var v) && v is >= 200 and <= 8192 ? v : null;

    void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SizePanel.Visibility = Choice.Get(ResolutionBox) is ResolutionMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        MonitorsPanel.Visibility = Choice.Get(ResolutionBox) is ResolutionMode.AllMonitors ? Visibility.Visible : Visibility.Collapsed;
    }
}
