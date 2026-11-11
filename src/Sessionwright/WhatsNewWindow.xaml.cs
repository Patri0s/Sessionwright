using System.Globalization;
using System.Windows;
using Wpf.Ui.Controls;

namespace Sessionwright;

/// <summary>The changes since the user last looked (after an update), or all of them (from Settings).</summary>
public partial class WhatsNewWindow : FluentWindow
{
    internal WhatsNewWindow(IReadOnlyList<(string Day, (string En, string Pl)[] Items)> entries)
    {
        InitializeComponent();
        Native.HideUntilRendered(this);
        Dialog.FitToScreen(this);
        Title = Bar.Title = Loc.Get("WhatsNewTitle");
        foreach (var (day, items) in entries)
        {
            var date = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Days.Children.Add(new System.Windows.Controls.TextBlock { Text = date.ToString("D", CultureInfo.CurrentUICulture), Style = (Style)FindResource("SectionHeader") });
            foreach (var (en, pl) in items)
            {
                var line = new System.Windows.Controls.TextBlock { Text = "•  " + (Loc.Polish ? pl : en), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 5, 4, 5) };
                Days.Children.Add(line);
            }
        }
    }

    /// <summary>Every entry, the newest first.</summary>
    public static void ShowAll(Window owner) => new WhatsNewWindow(WhatsNew.Entries) { Owner = owner }.ShowDialog();

    /// <summary>
    /// After an update: the main window says so in a bar (not a window in the way), with what is new since the user last
    /// looked one click away. A first start with nothing saved yet has no "before": nothing to tell.
    /// </summary>
    public static void ShowAfterUpdate(Window main)
    {
        var settings = App.Store.Data.Settings;
        var news = WhatsNew.Since(settings.NewsSeen);
        if (news.Count == 0) return;
        if (settings.NewsSeen == null && App.Store.Data.Connections.Count == 0) MarkSeen();
        else (main as MainWindow)?.OfferNews(news);
    }

    /// <summary>The user closed the bar or opened the list: not told again.</summary>
    public static void MarkSeen()
    {
        var settings = App.Store.Data.Settings;
        if (settings.NewsSeen == WhatsNew.Latest) return;
        settings.NewsSeen = WhatsNew.Latest;
        try { App.Store.Save(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Saving data failed"); // shown again next time: better than an error over a note
        }
    }
}
