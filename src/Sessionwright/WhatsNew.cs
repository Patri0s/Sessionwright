namespace Sessionwright;

/// <summary>
/// What changed for the user, shown once after an update (and from Settings): things that work differently now would
/// otherwise only surprise, like the home page cards that open on a double-click. Kept by the day they were written,
/// not by version: a build's version is only set when it is released.
/// </summary>
static class WhatsNew
{
    /// <summary>The newest first; a day's entries are added to, a new day goes on top.</summary>
    internal static readonly (string Day, (string En, string Pl)[] Items)[] Entries =
    [
        ("2026-11-11",
        [
            ("Sessionwright 1.0, the first public release.",
             "Sessionwright 1.0, pierwsze publiczne wydanie."),
            ("Every session in its own window: full screen on the monitors you pick, a small always-on-top view, sign out and reconnect from the title bar.",
             "Każda sesja we własnym oknie: pełny ekran na wybranych monitorach, mały podgląd zawsze na wierzchu, wylogowanie i ponowne łączenie z paska tytułu."),
            ("Azure machines next to their connections: state, start and shut down, the auto-shutdown, who is signed in, and scripts run on the machine.",
             "Maszyny Azure przy swoich połączeniach: stan, uruchamianie i wyłączanie, automatyczne wyłączenie, zalogowani użytkownicy i skrypty uruchamiane na maszynie."),
            ("The search (Ctrl+F) finds a connection, connects to an address you type and runs commands such as \"start …\".",
             "Wyszukiwarka (Ctrl+F) znajduje połączenie, łączy z wpisanym adresem i wykonuje polecenia takie jak „uruchom …”."),
        ]),
    ];

    public static string Latest => Entries[0].Day;

    /// <summary>The entries newer than the day already seen (all of them when none was).</summary>
    internal static List<(string Day, (string En, string Pl)[] Items)> Since(string? seen) =>
        [.. Entries.Where(e => string.CompareOrdinal(e.Day, seen ?? "") > 0)];
}
