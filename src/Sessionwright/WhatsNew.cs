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
        ("2026-11-13",
        [
            ("Before connecting, a connection or a group can connect a VPN set up in Windows (L2TP/IPsec and the like), signing in with the connection's own credential or another one: the password goes to Windows directly, never on a command line.",
             "Przed połączeniem połączenie lub grupa może połączyć VPN skonfigurowany w Windows (L2TP/IPsec i podobne), logując się poświadczeniem połączenia albo innym: hasło trafia bezpośrednio do Windows, nigdy do wiersza poleceń."),
            ("The warning about a server's certificate can be turned off: Settings > Behavior. The server's identity is then not checked, so use it on networks you trust.",
             "Ostrzeżenie o certyfikacie serwera można wyłączyć: Ustawienia > Działanie. Tożsamość serwera nie jest wtedy sprawdzana, więc używaj tego w zaufanych sieciach."),
            ("A team can share a list of connections: a file on OneDrive, a network share or behind an https address shows under a group of yours, read-only and without passwords, and everyone signs in as themselves. Group editor > Shared list; \"Export as a shared list\" in a group's menu makes the file.",
             "Zespół może współdzielić listę połączeń: plik na OneDrive, udziale sieciowym albo pod adresem https pokazuje się pod Twoją grupą, tylko do odczytu i bez haseł, a każdy loguje się własnym kontem. Edytor grupy > Wspólna lista; plik tworzy „Eksportuj jako wspólną listę” w menu grupy."),
            ("A group can be synchronized with Azure: the machines of a subscription (one resource group, one tag) become its connections, with their public or private address, and stay up to date. Group editor > Azure.",
             "Grupę można synchronizować z Azure: maszyny z subskrypcji (jednej grupy zasobów, z jednym tagiem) stają się jej połączeniami, z adresem publicznym albo prywatnym, i są aktualizowane. Edytor grupy > Azure."),
            ("\"Copy link to this connection\" now gives an https link, clickable in Teams, wikis and mail: it opens a small page that hands the connection to Sessionwright. The server address stays in the browser.",
             "„Kopiuj link do połączenia” daje teraz link https, klikalny w Teams, wiki i poczcie: otwiera małą stronę, która przekazuje połączenie do Sessionwright. Adres serwera nie opuszcza przeglądarki."),
        ]),
        ("2026-11-12",
        [
            ("A connection or a group can run a command before connecting, such as a VPN: it runs only when the server does not answer, and the session connects once it does. Editor > Remote desktop > Before connecting.",
             "Połączenie lub grupa może uruchomić polecenie przed połączeniem, np. VPN: uruchamia się tylko wtedy, gdy serwer nie odpowiada, a sesja łączy się, gdy odpowie. Edytor > Pulpit zdalny > Przed połączeniem."),
            ("\"Copy link to this connection\" in a connection's menu gives a sessionwright:// link: pasted into a wiki or a chat, a click opens the same server in Sessionwright, with the clicker's own credentials.",
             "„Kopiuj link do połączenia” w menu połączenia daje link sessionwright://: wklejony do wiki albo czatu, po kliknięciu otwiera ten sam serwer w Sessionwright, z poświadczeniami osoby, która kliknęła."),
        ]),
        ("2026-11-11",
        [
            ("Sessionwright 1.0, the first public release.",
             "Sessionwright 1.0, pierwsze publiczne wydanie."),
            ("Settings can add or remove the desktop and Start menu shortcuts, and uninstalling asks whether to keep your connections and settings.",
             "W Ustawieniach można dodać lub usunąć skrót na pulpicie i w menu Start, a odinstalowanie pyta, czy zachować połączenia i ustawienia."),
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
