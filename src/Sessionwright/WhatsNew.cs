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
        ("2026-11-15",
        [
            ("A script runs on several machines at once: mark the connections, or open a group's menu, and pick it under Scripts. One table shows each machine's result.",
             "Skrypt uruchamia się na wielu maszynach naraz: zaznacz połączenia albo otwórz menu grupy i wybierz go w Skryptach. Jedna tabela pokazuje wynik każdej maszyny."),
            ("The home page lists the connections used last while no session is open: a double-click connects.",
             "Ekran startowy pokazuje ostatnio używane połączenia, gdy żadna sesja nie jest otwarta: dwuklik łączy."),
            ("Settings have a Security section: the master password and the server certificate warning are there now.",
             "Ustawienia mają sekcję Bezpieczeństwo: tam jest teraz hasło główne i ostrzeżenie o certyfikacie serwera."),
            ("With a master password a lock in the title bar shows whether the saved passwords are unlocked; a click locks or unlocks them.",
             "Przy haśle głównym kłódka na pasku tytułu pokazuje, czy zapisane hasła są odblokowane; kliknięcie je blokuje albo odblokowuje."),
            ("The editor has a Details tab for tags, notes and links, and a group says in one place where its connections come from: added by you, an Azure subscription, or a team's shared list.",
             "Edytor ma zakładkę Szczegóły na tagi, notatki i linki, a grupa w jednym miejscu mówi, skąd biorą się jej połączenia: dodawane przez Ciebie, z subskrypcji Azure albo ze wspólnej listy zespołu."),
            ("The list marks what comes from a team's shared list, groups synchronized with Azure (with the last result on hover) and notes locked by the master password.",
             "Lista oznacza pozycje ze wspólnej listy zespołu, grupy synchronizowane z Azure (z ostatnim wynikiem po najechaniu) i notatki zablokowane hasłem głównym."),
            ("The sign-in prompt has one box for the user: type DOMAIN\\user there.",
             "Okno logowania ma jedno pole na użytkownika: wpisz w nim DOMENA\\użytkownik."),
        ]),
        ("2026-11-13",
        [
            ("Before connecting, a connection or a group can connect a VPN set up in Windows (L2TP/IPsec and the like), signing in with the connection's own credential or another one: the password goes to Windows directly, never on a command line.",
             "Przed połączeniem połączenie lub grupa może połączyć VPN skonfigurowany w Windows (L2TP/IPsec i podobne), logując się poświadczeniem połączenia albo innym: hasło trafia bezpośrednio do Windows, nigdy do wiersza poleceń."),
            ("A master password can protect the saved passwords instead of Windows, so the data can go to another PC: Settings > Security. The portable copy can then keep its data next to the program, for a USB stick. Two options there: encrypt the notes too, and lock the passwords when Windows is locked.",
             "Zapisane hasła może chronić hasło główne zamiast Windows, więc dane da się przenieść na inny komputer: Ustawienia > Bezpieczeństwo. Wersja przenośna może wtedy trzymać dane obok programu, na pendrivie. Są tam dwie opcje: szyfrowanie także notatek i blokowanie haseł po zablokowaniu Windows."),
            ("Scripts also run on machines that are not in Azure, over PowerShell remoting (WinRM) with the connection's credential.",
             "Skrypty działają też na maszynach spoza Azure, przez zdalny PowerShell (WinRM) z poświadczeniem połączenia."),
            ("The warning about a server's certificate can be turned off: Settings > Security. The server's identity is then not checked, so use it on networks you trust.",
             "Ostrzeżenie o certyfikacie serwera można wyłączyć: Ustawienia > Bezpieczeństwo. Tożsamość serwera nie jest wtedy sprawdzana, więc używaj tego w zaufanych sieciach."),
            ("A team can share a list of connections: a file on OneDrive, a network share or behind an https address shows under a group of yours, read-only and without passwords, and everyone signs in as themselves. Group editor > Shared list; \"Export as a shared list\" in a group's menu makes the file. When the list changed, Sessionwright says how many connections came, changed and went.",
             "Zespół może współdzielić listę połączeń: plik na OneDrive, udziale sieciowym albo pod adresem https pokazuje się pod Twoją grupą, tylko do odczytu i bez haseł, a każdy loguje się własnym kontem. Edytor grupy > Wspólna lista; plik tworzy „Eksportuj jako wspólną listę” w menu grupy. Gdy lista się zmieni, Sessionwright mówi, ile połączeń doszło, zmieniło się i zniknęło."),
            ("A group can be synchronized with Azure: the machines of a subscription (one resource group, one tag) become its connections, with their public or private address, and stay up to date. Group editor > Azure. An account that may not read network resources is enough: running machines are asked for their addresses.",
             "Grupę można synchronizować z Azure: maszyny z subskrypcji (jednej grupy zasobów, z jednym tagiem) stają się jej połączeniami, z adresem publicznym albo prywatnym, i są aktualizowane. Edytor grupy > Azure. Wystarczy konto bez prawa odczytu zasobów sieciowych: uruchomione maszyny są pytane o swoje adresy."),
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
