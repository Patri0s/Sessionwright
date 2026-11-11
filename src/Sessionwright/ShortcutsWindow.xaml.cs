using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>Every keyboard shortcut in one place (F1, or the "?" in the title bar), grouped by where it works.</summary>
public partial class ShortcutsWindow : FluentWindow
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Title = Bar.Title = Loc.Get("ShortcutsTitle");
        var quick = SessionKeys.ParseKey(App.Store.Data.Settings.QuickConnectKey) is { } key ? SessionKeys.FormatKey(key) : null;

        Section("ShortcutsAnywhere",
            (quick ?? Loc.Get("ShortcutsOff"), "ShortcutQuickConnect"));
        Section("ShortcutsMain",
            ("Ctrl+F", "ShortcutSearch"),
            ("Enter", "ShortcutSearchEnter"),
            ("Ctrl+N", "ShortcutNew"),
            ("Enter", "ShortcutConnect"),
            ("F2", "ShortcutEdit"),
            ("Delete", "ShortcutDelete"),
            (Loc.Get("ShortcutClickKeys"), "ShortcutMark"),
            (Loc.Get("ShortcutMarkKeys"), "ShortcutMarkKeyboard"),
            ("Esc", "ShortcutUnmark"),
            ("Ctrl+Z", "ShortcutUndo"),
            ("F5", "ShortcutRefresh"),
            ("Ctrl+Alt+↓", "ShortcutSwitcher"),
            ("Ctrl+1…9", "ShortcutFavorite"),
            ("Ctrl+M", "ShortcutMachines"),
            ("Ctrl+,", "ShortcutSettings"),
            ("F1", "ShortcutHelp"));
        Section("ShortcutsSession",
            ("Ctrl+Alt+←", "ShortcutPrevious"),
            ("Ctrl+Alt+→", "ShortcutNext"),
            ("Ctrl+Alt+↓", "ShortcutSwitcher"),
            ("Ctrl+Alt+Enter", "ShortcutFullScreen"),
            ("Ctrl+Alt+End", "ShortcutCtrlAltDel"));
        Section("ShortcutsSwitcher",
            ("1 … 9", "ShortcutSwitcherDigit"),
            ("← → ↑ ↓", "ShortcutSwitcherArrows"),
            ("Enter", "ShortcutSwitcherEnter"),
            ("Esc", "ShortcutSwitcherEsc"));
    }

    void Section(string title, params (string Keys, string What)[] rows)
    {
        Sections.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = Loc.Get(title), Style = (Style)FindResource("SubtitleText"),
        });
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (keys, what) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;
            var caps = Caps(keys);
            Grid.SetRow(caps, row);
            var text = new System.Windows.Controls.TextBlock { Text = Loc.Get(what), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            AutomationProperties.SetName(text, $"{keys}: {Loc.Get(what)}");
            grid.Children.Add(caps);
            grid.Children.Add(text);
        }
        Sections.Children.Add(grid);
    }

    /// <summary>"Ctrl+Alt+↓" as key caps: Ctrl + Alt + ↓. Text that is not a key combination stays one cap.</summary>
    FrameworkElement Caps(string keys)
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
        var parts = keys.Length > 1 && keys.Contains('+') && !keys.Contains(' ') ? keys.Split('+') : [keys];
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                var plus = new System.Windows.Controls.TextBlock { Text = "+", Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                plus.SetResourceReference(ForegroundProperty, "TextFillColorTertiaryBrush");
                panel.Children.Add(plus);
            }
            var cap = new Border
            {
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(7, 1, 7, 2),
                Child = new System.Windows.Controls.TextBlock { Text = parts[i], FontSize = 12 },
            };
            cap.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
            cap.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            panel.Children.Add(cap);
        }
        return panel;
    }
}
