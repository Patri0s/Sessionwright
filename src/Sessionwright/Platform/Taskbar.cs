using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace Sessionwright;

/// <summary>Windows taskbar integration: the jump list of the app icon and the buttons under a window's thumbnail.</summary>
static class Taskbar
{
    public const string ConnectArgument = "--connect";
    static string? _applied;

    /// <summary>Favorites and recent connections on the app icon's jump list; a click connects (see App startup).</summary>
    public static void UpdateJumpList(Store store)
    {
        if (Environment.ProcessPath is not { } exe || Application.Current is not { } app) return;
        var favorites = store.Favorites();
        var recent = store.Data.Connections.Where(c => c.LastUsed != null && !c.Favorite).OrderByDescending(c => c.LastUsed).Take(6).ToList();
        // Applying rewrites the shell's list: only when its content changed (Refresh runs often).
        var signature = string.Join("|", favorites.Concat(recent).Select(c => $"{c.Id}{c.Title}{c.Address}{c.Favorite}"));
        if (signature == _applied) return;

        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        void Add(IEnumerable<Connection> items, string category)
        {
            foreach (var c in items)
                list.JumpItems.Add(new JumpTask
                {
                    Title = c.Title, Description = c.Address, CustomCategory = category,
                    ApplicationPath = exe, IconResourcePath = exe, Arguments = $"{ConnectArgument} {c.Id}",
                });
        }
        Add(favorites, Loc.Get("JumpFavorites"));
        Add(recent, Loc.Get("JumpRecent"));
        JumpList.SetJumpList(app, list);
        try
        {
            list.Apply();
            _applied = signature; // only once applied: a failure (Explorer not ready at logon) is retried next time
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { Log.Error(ex, "Jump list"); }
    }

    /// <summary>Buttons under the window's taskbar thumbnail (glyphs from Segoe Fluent Icons / MDL2 Assets).</summary>
    public static void SetThumbButtons(Window window, params (char Glyph, string Tip, Action Click)[] buttons)
    {
        var info = new TaskbarItemInfo();
        foreach (var (glyph, tip, click) in buttons)
        {
            var button = new ThumbButtonInfo { ImageSource = Glyph(glyph), Description = tip };
            button.Click += (_, _) => click();
            info.ThumbButtonInfos.Add(button);
        }
        window.TaskbarItemInfo = info;
    }

    static ImageSource Glyph(char glyph)
    {
        // The thumbnail toolbar follows the taskbar's own theme, not the app's.
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
        var brush = light ? Brushes.Black : Brushes.White;
        var text = new FormattedText(glyph.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            16, brush, 1.0);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(brush, null, text.BuildGeometry(new Point((16 - text.Width) / 2, (16 - text.Height) / 2))));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
