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
        ("2026-11-10",
        [
            ("The home page shows only what the list cannot: open sessions and sessions left running. Machines are in the list, in the machines window (the cloud button, Ctrl+M) and in the search (\"start …\", \"stop …\").",
             "Strona główna pokazuje tylko to, czego nie pokaże lista: otwarte sesje i sesje pozostawione na serwerach. Maszyny są na liście, w oknie maszyn (przycisk z chmurą, Ctrl+M) i w wyszukiwarce („uruchom …”, „wyłącz …”)."),
            ("A connection's menu is shorter: power, the auto-shutdown and who is signed in are under \"Machine\"; \"New…\" and sorting are in a group's menu and the empty list's.",
             "Menu połączenia jest krótsze: zasilanie, automatyczne wyłączenie i zalogowani użytkownicy są pod „Maszyna”; „Nowe…” i sortowanie są w menu grupy i pustej listy."),
            ("Settings have four sections and no Save button: every change applies at once.",
             "Ustawienia mają cztery sekcje i nie mają przycisku Zapisz: każda zmiana działa od razu."),
            ("A session's title bar shows sign out and full screen; the small view, Ctrl+Alt+Del, reconnect and links are under \"…\". The signal mark shows only while the link is poor.",
             "Pasek tytułu sesji pokazuje wylogowanie i pełny ekran; mały podgląd, Ctrl+Alt+Del, ponowne łączenie i linki są pod „…”. Wskaźnik sygnału widać tylko przy słabym łączu."),
            ("A failed connection says in one line what to check; a search command that shuts down, restarts or signs out is marked in the caution color.",
             "Nieudane połączenie mówi w jednej linii, co sprawdzić; polecenie wyszukiwarki, które wyłącza, restartuje lub wylogowuje, ma kolor ostrzegawczy."),
        ]),
        ("2026-11-09",
        [
            ("The cards on the home page open on a double-click, like the rows of the list. Enter still opens the one picked with the keyboard.",
             "Karty na stronie głównej otwierają się dwuklikiem, tak jak wiersze listy. Enter nadal otwiera kartę wybraną klawiaturą."),
            ("A small always-on-top view of a session: under \"…\" in the session's title bar, on the full-screen bar and on the session's card. Keep an eye on a long build while working elsewhere.",
             "Mały podgląd sesji zawsze na wierzchu: pod „…” na pasku tytułu sesji, na pasku pełnego ekranu i na karcie sesji. Zerkaj na długi build, pracując gdzie indziej."),
            ("Scripts run on the machine behind a connection linked to Azure (free disk space, service status, restart IIS or the batch service): \"Run on the machine\" in the connection's menu and in the session's machine menu; the library is under More > Scripts.",
             "Skrypty uruchamiane na maszynie połączenia powiązanego z Azure (wolne miejsce, stan usług, restart IIS lub usługi batch): „Uruchom na maszynie” w menu połączenia i w menu maszyny w oknie sesji; biblioteka jest w Więcej > Skrypty."),
            ("Full screen on several monitors can use only the monitors you tick, not just all of them.",
             "Pełny ekran na kilku monitorach może używać tylko zaznaczonych monitorów, nie tylko wszystkich."),
            ("Deleted something by mistake? For 20 seconds the notification's Undo (or Ctrl+Z) brings it back.",
             "Coś usunięte przez pomyłkę? Przez 20 sekund „Cofnij” w powiadomieniu (albo Ctrl+Z) to przywraca."),
            ("The editor asks before closing with unsaved changes.",
             "Edytor pyta przed zamknięciem, gdy są niezapisane zmiany."),
            ("Marking several connections: also Shift+↑/↓ and Ctrl+A; a click anywhere else ends it.",
             "Zaznaczanie kilku połączeń: także Shift+↑/↓ i Ctrl+A; kliknięcie gdziekolwiek obok je kończy."),
            ("The status mark has a shape of its own for each state: a dot = reachable, a thick ring = a session left running, a thin ring = machine off, a dash = unreachable.",
             "Znacznik stanu ma osobny kształt: kropka = dostępny, gruby pierścień = pozostawiona sesja, cienki pierścień = maszyna wyłączona, kreska = niedostępny."),
            ("Notes show as a small mark on the row (hover to read) and are searched.",
             "Notatki widać jako mały znaczek w wierszu (najedź, by przeczytać) i są przeszukiwane."),
            ("A session window closes by itself when you sign out inside the session; Reconnect on a working session asks first.",
             "Okno sesji zamyka się samo po wylogowaniu wewnątrz sesji; „Połącz ponownie” na działającej sesji najpierw pyta."),
            ("Updates are taken only from https or a folder, and a link with a password in its address is no longer opened.",
             "Aktualizacje są pobierane tylko z https albo z folderu, a link z hasłem w adresie nie jest już otwierany."),
        ]),
    ];

    public static string Latest => Entries[0].Day;

    /// <summary>The entries newer than the day already seen (all of them when none was).</summary>
    internal static List<(string Day, (string En, string Pl)[] Items)> Since(string? seen) =>
        [.. Entries.Where(e => string.CompareOrdinal(e.Day, seen ?? "") > 0)];
}
