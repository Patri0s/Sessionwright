using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace Sessionwright;

public partial class EditorWindow : FluentWindow
{
    readonly Node _node;
    string? _color;
    AzureVmLink? _vm;
    static Store Store => App.Store;

    // Environment colors: production red, test orange, and so on (the meaning is up to the user). Equally strong, so
    // each reads on the dark and the light theme; any other color through the picker at the end.
    static readonly (string? Hex, string Key)[] Palette =
    [
        (null, "ColorNone"), ("#E5534B", "ColorRed"), ("#E8833A", "ColorOrange"), ("#D29922", "ColorYellow"), ("#8CBF26", "ColorLime"),
        ("#2EA043", "ColorGreen"), ("#1B998B", "ColorTeal"), ("#39A9DB", "ColorCyan"), ("#3B82F6", "ColorBlue"), ("#6E6CF0", "ColorIndigo"),
        ("#A371F7", "ColorPurple"), ("#DB61A2", "ColorPink"), ("#A1745A", "ColorBrown"), ("#8B949E", "ColorGray"),
    ];

    RadioButton? _customSwatch;

    static bool SameColor(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string? PaletteName(string? hex) => Palette.FirstOrDefault(p => p.Hex != null && SameColor(p.Hex, hex)).Key is { } key ? Loc.Get(key) : null;

    public EditorWindow(Node node, bool isNew)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Dialog.FitToScreen(this);
        _node = node;
        var isConnection = node is Connection;
        Title = Bar.Title = Loc.Get((isNew, isConnection) switch
        {
            (true, true) => "NewConnection",
            (true, false) => "NewGroup",
            (false, true) => "EditConnection",
            _ => "EditGroup",
        });
        ConnectionFields.Visibility = TagsFields.Visibility = VmFields.Visibility = isConnection ? Visibility.Visible : Visibility.Collapsed;
        AzureTabHint.Visibility = isConnection ? Visibility.Collapsed : Visibility.Visible; // a group's apply to its connections with a VM

        NameBox.Text = node.Name;
        NotesBox.Text = node.Notes ?? "";
        foreach (var link in node.Links ?? []) AddLinkRow(link);
        // The template previews follow the connection's name and host as they are typed.
        NameBox.TextChanged += (_, _) => UpdateLinkPreviews();
        HostBox.TextChanged += (_, _) => UpdateLinkPreviews();
        if (node is Connection c)
        {
            HostBox.Text = c.Host;
            PortBox.Text = c.Port.ToString();
            TagsBox.Text = c.Tags ?? "";
            _vm = c.AzureVm?.Clone();
        }
        ShowVm();

        var parents = Store.Data.Groups
            .Where(g => Store.CanMove(node, g))
            .Select(g => new Choice(g, string.Join(" / ", Store.Ancestors(g).Reverse().Select(a => a.Name).Append(g.Name))))
            .OrderBy(x => x.Text, Store.NaturalOrder)
            .Prepend(new Choice(null, Loc.Get("RootGroup")))
            .ToList();
        ParentBox.ItemsSource = parents;
        ParentBox.SelectedItem = parents.FirstOrDefault(p => (p.Value as Group)?.Id == node.ParentId) ?? parents[0];

        LoadCredentials(Store.Data.Credentials.FirstOrDefault(x => x.Id == node.CredentialId));

        AutoStartBox.ItemsSource = new[]
        {
            new Choice(null, Loc.Get("AutoStartInherit")),
            new Choice(AutoStartDays.Off, Loc.Get("AutoStartOff")),
            new Choice(AutoStartDays.Weekdays, Loc.Get("AutoStartWeekdays")),
            new Choice(AutoStartDays.Daily, Loc.Get("AutoStartDaily")),
        };
        Choice.Select(AutoStartBox, node.AutoStart?.Days);
        AutoStartTimeBox.Text = node.AutoStart?.Time ?? "08:00";

        ShutDownAfterBox.ItemsSource = new[]
        {
            new Choice(null, Loc.Get("AutoStartInherit")),
            new Choice(true, Loc.Get("ShutDownAfterYes")),
            new Choice(false, Loc.Get("ShutDownAfterNo")),
        };
        Choice.Select(ShutDownAfterBox, node.ShutDownVmAfter);

        InheritBox.IsChecked = node.Rdp == null;
        var inherited = Store.Ancestors(node).Select(g => g.Rdp).FirstOrDefault(r => r != null) ?? Store.Data.Settings.Rdp;
        Options.Load(node.Rdp ?? inherited);
        Options.IsEnabled = node.Rdp != null;

        _color = node.Color;
        BuildPalette();
        UpdateInherited();

        Loaded += (_, _) =>
        {
            FitToTabs();
            (isConnection ? HostBox : NameBox).Focus();
            _loaded = Snapshot();
        };
        // Esc, Cancel or the close button with something changed: asked first, a filled-in form is not lost to one key.
        Closing += (_, e) =>
        {
            if (DialogResult == true || _loaded == null || Snapshot() == _loaded) return;
            e.Cancel = !Dialog.Confirm(this, Loc.Get("ConfirmDiscard"), Loc.Get("Discard"), DialogKind.Warning, cancelText: Loc.Get("KeepEditing"));
        };
    }

    string? _loaded;

    /// <summary>Everything the form holds, as text: differs from the one taken on load once anything was changed.</summary>
    string Snapshot() => string.Join("\u001f", NameBox.Text, HostBox.Text, PortBox.Text, TagsBox.Text, NotesBox.Text,
        string.Join("\u001e", _links.Select(r => $"{r.Name.Text}\u001d{r.Url.Text}")),
        (Choice.Get(ParentBox) as Group)?.Id, (Choice.Get(CredentialBox) as Credential)?.Id,
        Choice.Get(AutoStartBox), AutoStartTimeBox.Text, Choice.Get(ShutDownAfterBox), InheritBox.IsChecked, _color,
        InheritBox.IsChecked == true ? "" : System.Text.Json.JsonSerializer.Serialize(Options.Read()),
        _vm is { } vm ? Machines.Key(vm) : "");

    void LoadCredentials(Credential? selected)
    {
        var items = Store.Data.Credentials
            .OrderBy(x => x.Name, Store.NaturalOrder)
            .Select(x => new Choice(x, x.ToString()))
            .Prepend(new Choice(null, Loc.Get("InheritCredential")))
            .ToList();
        CredentialBox.ItemsSource = items;
        Choice.Select(CredentialBox, selected);
    }

    void ManageCredentials_Click(object sender, RoutedEventArgs e)
    {
        var current = Choice.Get(CredentialBox) as Credential;
        new CredentialsWindow(current) { Owner = this }.ShowDialog();
        LoadCredentials(Store.Data.Credentials.Contains(current!) ? current : null);
    }

    void InheritBox_Changed(object sender, RoutedEventArgs e)
    {
        Options.IsEnabled = InheritBox.IsChecked != true;
        UpdateInherited();
    }

    void ParentBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInherited();
    void CredentialBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInherited();

    void AutoStartBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInherited();

    // Built once: picking a swatch only checks it (the group unchecks the others). Only the checked swatch is a tab
    // stop, the arrows move between them. A color from outside the palette has a swatch of its own before the "+".
    void BuildPalette()
    {
        foreach (var (hex, key) in Palette) ColorPanel.Children.Add(Swatch(hex, Loc.Get(key)));
        var add = new System.Windows.Controls.Button { Style = (Style)FindResource("AddSwatch"), ToolTip = Loc.Get("ColorCustomPick") };
        AutomationProperties.SetName(add, Loc.Get("ColorCustomPick"));
        add.Click += (_, _) => PickCustomColor();
        ColorPanel.Children.Add(add);
        if (_color != null && PaletteName(_color) == null) ShowCustom(_color);
        var swatches = ColorPanel.Children.OfType<RadioButton>().ToList();
        var stop = swatches.FirstOrDefault(s => s.IsChecked == true) ?? swatches[0];
        foreach (var s in swatches) s.IsTabStop = s == stop;
    }

    RadioButton Swatch(string? hex, string name)
    {
        var swatch = new RadioButton
        {
            Style = (Style)FindResource("Swatch"), GroupName = "EnvironmentColor", Tag = hex, IsChecked = SameColor(hex, _color),
        };
        Paint(swatch, hex, name);
        // "No color": a struck-through empty circle.
        if (hex == null)
        {
            var line = new Line { X1 = 6, Y1 = 16, X2 = 16, Y2 = 6, StrokeThickness = 1.5 };
            line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
            swatch.Content = new Canvas { Width = 22, Height = 22, Children = { line } };
        }
        swatch.Checked += (_, _) =>
        {
            foreach (var other in ColorPanel.Children.OfType<RadioButton>()) other.IsTabStop = other == swatch;
            _color = swatch.Tag as string;
            UpdateInherited();
        };
        return swatch;
    }

    static void Paint(RadioButton swatch, string? hex, string name)
    {
        swatch.Tag = hex;
        swatch.Background = hex == null ? Brushes.Transparent
            : (Brush)new HexBrushConverter().Convert(hex, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture);
        swatch.ToolTip = name;
        AutomationProperties.SetName(swatch, name);
    }

    /// <summary>A color of the user's own: its swatch before the "+", checked.</summary>
    void ShowCustom(string hex)
    {
        var name = Loc.Format("ColorCustomNamed", hex);
        if (_customSwatch == null)
        {
            _customSwatch = Swatch(hex, name);
            ColorPanel.Children.Insert(ColorPanel.Children.Count - 1, _customSwatch);
        }
        else Paint(_customSwatch, hex, name);
        _customSwatch.IsChecked = false; // checked again below: the Checked handler takes the new color
        _customSwatch.IsChecked = true;
    }

    // The app's own color picker, from the color chosen now (the recent ones it keeps are saved with the connection).
    void PickCustomColor()
    {
        if (ColorPickerWindow.Pick(this, _color) is not { } hex) return;
        if (ColorPanel.Children.OfType<RadioButton>().FirstOrDefault(s => s != _customSwatch && SameColor(s.Tag as string, hex)) is { } same) same.IsChecked = true;
        else ShowCustom(hex);
    }

    /// <summary>Under each "inherit" choice: the value it gives and where it comes from (group or global settings).</summary>
    void UpdateInherited()
    {
        if (!IsInitialized || ParentBox.SelectedItem == null) return;
        var parent = Choice.Get(ParentBox) as Group;
        string From(Group? source, string what) => source == null ? what : Loc.Format("InheritedFrom", source.Name, what);

        var (colorSource, color) = Store.Inherited(parent, n => n.Color);
        var colorName = color == null ? null : PaletteName(color) ?? Loc.Format("ColorCustomNamed", color);
        ColorInherited.Text = _color != null ? "" : color == null ? Loc.Get("InheritedNoColor") : From(colorSource, colorName!);

        // The links of the groups above it, opened from it too.
        var groups = parent == null ? [] : new[] { parent }.Concat(Store.Ancestors(parent)).ToList();
        var inherited = groups.SelectMany(g => (g.Links ?? []).Select(l => $"{Links.NameOf(l)} ({g.Name})")).ToList();
        LinksInherited.Text = inherited.Count > 0 ? Loc.Format("LinksInheritedList", string.Join(", ", inherited)) : "";
        LinksInherited.Visibility = inherited.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var (credSource, credId) = Store.Inherited(parent, n => n.CredentialId?.ToString());
        var credential = Store.Data.Credentials.FirstOrDefault(c => c.Id.ToString() == credId);
        CredentialInherited.Text = Choice.Get(CredentialBox) != null ? ""
            : credential == null ? Loc.Get("InheritedNoCredential") : From(credSource, credential.ToString());

        var days = Choice.Get(AutoStartBox) as AutoStartDays?;
        AutoStartTimeBox.IsEnabled = days is AutoStartDays.Weekdays or AutoStartDays.Daily;
        var (startSource, start) = Store.Inherited(parent, n => n.AutoStart);
        AutoStartInherited.Text = string.Join(" ", new[]
        {
            days != null ? null : start == null ? Loc.Get("InheritedNoAutoStart") : From(startSource, start.ToString()),
            // It starts Azure VMs: a connection without one is not affected (a group's applies to the linked ones).
            _node is Connection && _vm == null && days is AutoStartDays.Weekdays or AutoStartDays.Daily ? Loc.Get("AutoStartNeedsVm") : null,
        }.OfType<string>());

        var (afterSource, after) = Store.Inherited(parent, n => n.ShutDownVmAfter?.ToString());
        ShutDownAfterInherited.Text = Choice.Get(ShutDownAfterBox) != null ? ""
            : after == null ? Loc.Get("InheritedNoShutDownAfter")
            : From(afterSource, Loc.Get(after == bool.TrueString ? "ShutDownAfterYes" : "ShutDownAfterNo"));

        var (rdpSource, rdp) = Store.Inherited(parent, n => n.Rdp);
        RdpInherited.Text = InheritBox.IsChecked != true ? "" : rdpSource == null ? Loc.Get("InheritedGlobal") : Loc.Format("InheritedFromGroup", rdpSource.Name);
        foreach (var caption in new[] { ColorInherited, CredentialInherited, RdpInherited, AutoStartInherited, ShutDownAfterInherited })
            caption.Visibility = caption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (InheritBox.IsChecked == true) Options.Load(rdp ?? Store.Data.Settings.Rdp); // what the connection will actually use
    }

    // Pasting "host:port" (multi-char insert) or leaving the field moves the port into the port box.
    // Not done per keystroke: typing "host:3" would otherwise split before the port is complete.
    void HostBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (e.Changes.Any(c => c.AddedLength > 1)) Dispatcher.BeginInvoke(SplitPort);
    }

    void HostBox_LostFocus(object sender, RoutedEventArgs e) => SplitPort();

    void SplitPort()
    {
        var text = HostBox.Text.Trim();
        if (!Store.TryParseAddress(text, out var host, out var port) || host == text.Trim('[', ']')) return;
        PortBox.Text = port.ToString();
        HostBox.Text = host;
        HostBox.CaretIndex = host.Length;
    }

    void Field_Changed(object sender, RoutedEventArgs e) => ErrorText.Text = "";

    /// <summary>
    /// As tall as its tallest tab, within the screen: no scroll bar where everything fits, for a new connection as for an
    /// edited one (a fixed height left one of them just over). Measured before the first frame (the window is cloaked).
    /// </summary>
    void FitToTabs()
    {
        var most = double.NaN;
        foreach (TabItem tab in Tabs.Items)
        {
            Tabs.SelectedItem = tab;
            UpdateLayout();
            if (tab.Content is ScrollViewer sv) most = double.IsNaN(most) ? sv.ExtentHeight - sv.ViewportHeight : Math.Max(most, sv.ExtentHeight - sv.ViewportHeight);
        }
        Tabs.SelectedIndex = 0;
        if (double.IsNaN(most)) return;
        var area = System.Windows.Forms.Screen.FromHandle(Native.Handle(this)).WorkingArea;
        var maxHeight = area.Height * 96.0 / Native.DpiOf(this) * 0.92;
        var height = Math.Clamp(ActualHeight + most, MinHeight, Math.Max(MinHeight, maxHeight));
        Top -= (height - ActualHeight) / 2; // stays centered over the main window
        Height = height;
    }

    // On the field's tab, once that tab's content is there. Focus first: leaving the host box splits "host:port", and
    // that edit would clear the message.
    void Fail(Control field, string message)
    {
        for (DependencyObject? d = field; d != null; d = LogicalTreeHelper.GetParent(d))
            if (d is TabItem tab)
            {
                Tabs.SelectedItem = tab;
                break;
            }
        Dispatcher.BeginInvoke(() =>
        {
            field.Focus();
            (field as TextBoxBase)?.SelectAll();
            ErrorText.Text = message;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void ShowVm()
    {
        if (IsInitialized) Dispatcher.BeginInvoke(UpdateInherited); // the auto-start hint depends on it
        AzureVmText.Text = _vm == null ? Loc.Get("AzureVmNoneLinked") : $"{_vm.Name} · {_vm.ResourceGroup} · {_vm.Account}";
        AzureVmText.Foreground = (Brush)FindResource(_vm == null ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        UnlinkVmButton.Visibility = _vm == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void ChooseVm_Click(object sender, RoutedEventArgs e)
    {
        if (VmPickerWindow.Pick(this, _vm, HostBox.Text.Trim()) is not { } vm) return;
        _vm = vm.Link;
        ShowVm();
    }

    void UnlinkVm_Click(object sender, RoutedEventArgs e)
    {
        _vm = null;
        ShowVm();
    }

    // ---------- Links ----------

    sealed record LinkRow(Wpf.Ui.Controls.TextBox Name, Wpf.Ui.Controls.TextBox Url, System.Windows.Controls.TextBlock Preview, Grid Row);
    readonly List<LinkRow> _links = [];

    void AddLink_Click(object sender, RoutedEventArgs e) => AddLinkRow(null).Url.Focus();

    // Name | address | remove; under the address, what a template becomes.
    LinkRow AddLinkRow(NodeLink? link)
    {
        var name = new Wpf.Ui.Controls.TextBox { PlaceholderText = Loc.Get("LinkName"), Text = link?.Name ?? "", Margin = new Thickness(0, 0, 8, 0) };
        var url = new Wpf.Ui.Controls.TextBox { PlaceholderText = Loc.Get("LinkUrlPlaceholder"), Text = link?.Url ?? "" };
        AutomationProperties.SetName(name, Loc.Get("LinkName"));
        AutomationProperties.SetName(url, Loc.Get("LinkUrl"));
        var remove = new Wpf.Ui.Controls.Button
        {
            Style = (Style)FindResource("IconButton"), Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 }, ToolTip = Loc.Get("LinkRemove"),
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        var preview = new System.Windows.Controls.TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(url, 1);
        Grid.SetColumn(remove, 2);
        Grid.SetRow(preview, 1);
        Grid.SetColumn(preview, 1);
        grid.Children.Add(name);
        grid.Children.Add(url);
        grid.Children.Add(remove);
        grid.Children.Add(preview);
        var row = new LinkRow(name, url, preview, grid);
        url.TextChanged += (_, _) => UpdateLinkPreview(row);
        remove.Click += (_, _) =>
        {
            LinksPanel.Children.Remove(grid);
            _links.Remove(row);
        };
        _links.Add(row);
        LinksPanel.Children.Add(grid);
        UpdateLinkPreview(row);
        return row;
    }

    void UpdateLinkPreviews()
    {
        foreach (var row in _links) UpdateLinkPreview(row);
    }

    // A template: in a connection what it becomes for it, in a group that it is filled in for each of its connections.
    void UpdateLinkPreview(LinkRow row)
    {
        var url = row.Url.Text.Trim();
        var template = Links.HasPlaceholders(url);
        row.Preview.Visibility = template ? Visibility.Visible : Visibility.Collapsed;
        if (!template) return;
        row.Preview.Text = _node is Connection
            ? "→ " + Links.Expand(url, new Connection { Name = NameBox.Text.Trim(), Host = HostBox.Text.Trim(), AzureVm = _vm })
            : Loc.Get("LinkTemplateForEach");
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        RdpOptions? options = null;
        if (InheritBox.IsChecked != true && (options = Options.Read()) == null)
        {
            Fail(Options.InvalidSizeBox, Loc.Get("InvalidSize"));
            return;
        }
        // Only web addresses: one is typed into the server's Run box, which would run anything else.
        foreach (var row in _links)
        {
            var url = row.Url.Text.Trim();
            if (url.Length == 0 && row.Name.Text.Trim().Length == 0) continue;
            if (Links.IsSafeTemplate(url)) continue;
            Fail(row.Url, Loc.Format("LinkInvalid", url));
            return;
        }
        var startDays = Choice.Get(AutoStartBox) as AutoStartDays?;
        string? startTime = null;
        if (startDays is AutoStartDays.Weekdays or AutoStartDays.Daily)
        {
            if (AzureAutoStart.ParseTime(AutoStartTimeBox.Text) is not { } t)
            {
                Fail(AutoStartTimeBox, Loc.Get("InvalidTime"));
                return;
            }
            startTime = t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (_node is Group && name.Length == 0)
        {
            Fail(NameBox, Loc.Get("NameRequired"));
            return;
        }

        // Everything is checked by now: nothing is written to the node before, so Cancel after an error changes nothing.
        if (_node is Connection c)
        {
            if (!Store.TryParseAddress(HostBox.Text, out var host, out var portFromHost))
            {
                Fail(HostBox, Loc.Format("InvalidAddress", HostBox.Text));
                return;
            }
            // "host:port" typed into the host field wins over the port field.
            var hostHasPort = host != HostBox.Text.Trim().Trim('[', ']');
            var port = portFromHost;
            if (!hostHasPort && (!int.TryParse(PortBox.Text, out port) || port is < 1 or > 65535))
            {
                Fail(PortBox, Loc.Get("InvalidPort"));
                return;
            }
            c.Host = host;
            c.Port = port;
            c.Tags = TagsBox.Text.Trim() is { Length: > 0 } tags ? tags : null;
            if (c.AzureVm == null || _vm == null || !AzureVms.Same(c.AzureVm, _vm)) c.VmState = null;
            c.AzureVm = _vm;
            // An empty name stays empty: the title follows the host, also after the host is edited later.
        }

        var autoStart = startDays is { } d ? new VmAutoStart { Days = d, Time = startTime ?? "08:00" } : null;
        // A new plan may start today, even if an older one already did.
        if (autoStart?.ToString() != _node.AutoStart?.ToString())
            foreach (var affected in _node is Group g ? Store.Data.Connections.Where(x => Store.Ancestors(x).Contains(g)) : _node is Connection self ? [self] : [])
                if (affected.AzureVm != null) affected.AzureVm.AutoStartedOn = null;
        _node.AutoStart = autoStart;
        _node.ShutDownVmAfter = Choice.Get(ShutDownAfterBox) as bool?;

        _node.Name = name;
        _node.Notes = NotesBox.Text.Trim() is { Length: > 0 } notes ? notes : null;
        _node.Links = _links.Where(r => r.Url.Text.Trim().Length > 0)
            .Select(r => new NodeLink { Name = r.Name.Text.Trim(), Url = r.Url.Text.Trim() }).ToList() is { Count: > 0 } links ? links : null;
        _node.ParentId = (Choice.Get(ParentBox) as Group)?.Id;
        _node.CredentialId = (Choice.Get(CredentialBox) as Credential)?.Id;
        _node.Rdp = options;
        _node.Color = _color;
        DialogResult = true;
    }
}
