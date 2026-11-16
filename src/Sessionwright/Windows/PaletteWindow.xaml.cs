using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Screen = System.Windows.Forms.Screen;

namespace Sessionwright;

/// <summary>A row of the palette: a connection or a command, as it is shown and announced.</summary>
public sealed record PaletteRow(object Item, SymbolRegular Icon, string Title, string Detail, bool Danger)
{
    public string Spoken => Detail.Length > 0 ? $"{Title}, {Detail}" : Title;
}

/// <summary>
/// Search and commands in one box, over whatever window the user is in (Ctrl+K in the main window, the quick-connect
/// key anywhere, a session included). Enter runs the row picked, Tab on a connection lists what can be done with it,
/// ">" lists commands only; Esc or a click elsewhere closes. What the rows are is the main window's (PaletteResults).
/// </summary>
public partial class PaletteWindow : Window
{
    static PaletteWindow? _open;

    readonly MainWindow _main;
    readonly Window? _over;
    Connection? _scope;

    PaletteWindow(MainWindow main, Window? over)
    {
        InitializeComponent();
        _main = main;
        _over = over;
        SourceInitialized += (_, _) =>
        {
            var round = 2; // DWMWCP_ROUND: Windows 11 rounds it and draws its border and shadow
            DwmSetWindowAttribute(Native.Handle(this), 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        };
        Closed += (_, _) =>
        {
            if (_open == this) _open = null;
        };
        Update();
    }

    /// <summary>Opens the palette on <paramref name="over"/>'s monitor (else the one with the pointer), or closes it.</summary>
    public static void Toggle(MainWindow main, Window? over)
    {
        if (_open != null)
        {
            _open.Close();
            return;
        }
        var shown = over is { IsVisible: true } && over.WindowState != WindowState.Minimized;
        var area = (shown ? Screen.FromHandle(Native.Handle(over!)) : Screen.FromPoint(System.Windows.Forms.Cursor.Position)).WorkingArea;
        var palette = _open = new PaletteWindow(main, shown ? over : null);
        // On its monitor before it is shown (its scale), centered once its width in pixels is known. The top stays where
        // it is while the list under the box grows and shrinks.
        new System.Windows.Interop.WindowInteropHelper(palette).EnsureHandle();
        Native.SetPosition(palette, area.Left + area.Width / 3, area.Top + area.Height / 5);
        palette.Show();
        palette.UpdateLayout();
        Native.SetPosition(palette, area.Left + (area.Width - Native.Bounds(palette).Width) / 2, area.Top + area.Height / 5);
        palette.Activate();
        palette.PaletteBox.Focus();
    }

    /// <summary>The data changed under an open palette: the same rows, as they are now.</summary>
    public static void Refresh() => _open?.Update();

    void Update()
    {
        var keep = (Rows.SelectedItem as PaletteRow)?.Item;
        var rows = _main.PaletteResults(PaletteBox.Text, _scope).Select(Row).ToList();
        Rows.ItemsSource = rows;
        var index = keep == null ? -1 : rows.FindIndex(r => ReferenceEquals(r.Item, keep));
        Rows.SelectedIndex = index >= 0 ? index : rows.Count > 0 ? 0 : -1;
        Rows.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Nothing.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static PaletteRow Row(object item) => item switch
    {
        Connection c => new(c, c.AzureVm != null ? SymbolRegular.Cloud24 : SymbolRegular.Desktop24, c.Title,
            string.Join(" · ", new[] { c.IsOpen ? Loc.Get("SessionOpen") : "", c.Address == c.Title ? "" : c.Address }.Where(s => s.Length > 0)), false),
        SearchAction a => new(a, a.Icon, a.Name, a.Kind, a.Danger),
        _ => throw new InvalidOperationException($"Not a palette row: {item}"),
    };

    void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        Update();
        // Enter runs the first row: the exact match, quick connect to a full address, the best name match, a command.
        if (Rows.Items.Count > 0) Rows.SelectedIndex = 0;
    }

    void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItem is not PaletteRow row) return;
        Rows.ScrollIntoView(row);
        Announce.Text = row.Spoken;
        UIElementAutomationPeer.CreatePeerForElement(Announce)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void SetScope(Connection? scope)
    {
        _scope = scope;
        ScopeChip.Visibility = scope != null ? Visibility.Visible : Visibility.Collapsed;
        ScopeText.Text = scope?.Title ?? "";
        if (PaletteBox.Text.Length > 0) PaletteBox.Text = ""; // lists anew
        else Update();
        if (Rows.Items.Count > 0) Rows.SelectedIndex = 0;
    }

    void Run(PaletteRow row)
    {
        Close(); // first: what runs may open a window or ask a question of its own
        _main.RunPaletteResult(row.Item);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Rows.Items.Count > 0:
                Rows.SelectedIndex = (Rows.SelectedIndex + 1) % Rows.Items.Count;
                break;
            case Key.Up when Rows.Items.Count > 0:
                Rows.SelectedIndex = (Rows.SelectedIndex + Rows.Items.Count - 1) % Rows.Items.Count;
                break;
            case Key.Enter when Rows.SelectedItem is PaletteRow row:
                Run(row);
                break;
            // Nothing found, but an address: connects to it. Other words stay, to be corrected.
            case Key.Enter when _scope == null && PaletteBox.Text.Trim() is { Length: > 0 } text && Store.TryParseAddress(text, out _, out _):
                Close();
                _main.QuickConnectTyped(text);
                break;
            case Key.Enter:
                break;
            case Key.Tab: // never travels: there is one box
                if (_scope == null && Rows.SelectedItem is PaletteRow { Item: Connection c }) SetScope(c);
                break;
            case Key.Back when _scope != null && PaletteBox.Text.Length == 0:
            case Key.Escape when _scope != null:
                SetScope(null);
                break;
            case Key.Escape:
                Close();
                _over?.Activate(); // typing goes on where it was
                break;
            default: return;
        }
        e.Handled = true;
    }

    void Rows_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Rows, source) is ListBoxItem { DataContext: PaletteRow row }) Run(row);
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsVisible) Close(); // a click elsewhere, Alt+Tab
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
