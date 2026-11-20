namespace Sessionwright;

/// <summary>
/// What changed for the user, shown once after an update (and from Settings): things that work differently now would
/// otherwise only surprise, like the home page cards that open on a double-click. Kept by the day they were written,
/// not by version: a build's version is only set when it is released.
/// </summary>
static class WhatsNew
{
    /// <summary>
    /// The newest first; a new day goes on top. A day that a release has already shown is not added to: who has that
    /// release has seen the day and would miss the addition. What is written later the same day goes under the day
    /// with ".2" (".3"…) after it, which sorts after the day and shows as the same date. (Two entries here once
    /// carried the next day's date instead, and the window showed a date that had not come yet.)
    /// </summary>
    internal static readonly (string Day, (string En, string Pl)[] Items)[] Entries =
    [
        ("2026-11-20",
        [
            ("A full-screen session that loses its connection stays in full screen: the reason shows where the desktop was, with Reconnect, Close and Exit full screen. Full screen across several monitors is still left, so the other monitors come back.",
             "Sesja pełnoekranowa, która straci połączenie, zostaje na pełnym ekranie: powód widać w miejscu pulpitu, z przyciskami Połącz ponownie, Zamknij i Wyjdź z pełnego ekranu. Pełny ekran na kilku monitorach jest nadal opuszczany, żeby wróciły pozostałe monitory."),
            ("A team's shared list no longer brings an RD Gateway or the redirection of your drives and smart cards, and a list exported for the team leaves out the notes. An import says when it brings any of them, or Azure machines started on a schedule or shut down after a session.",
             "Lista zespołu nie wnosi już bramy RD Gateway ani przekierowania Twoich dysków i kart inteligentnych, a lista eksportowana dla zespołu pomija notatki. Import mówi, gdy coś z tego wnosi, a także maszyny Azure uruchamiane według harmonogramu albo wyłączane po sesji."),
            ("Exiting, updating or restarting Sessionwright asks first when it is still waiting to restart or shut down an Azure machine: closed, it would not happen.",
             "Zamknięcie, aktualizacja albo ponowne uruchomienie Sessionwright najpierw pyta, gdy wciąż czeka na ponowne uruchomienie albo wyłączenie maszyny Azure: po zamknięciu by się nie wykonało."),
            ("\"Start with Windows\" is set by the installed Sessionwright only: a portable copy no longer takes it over.",
             "„Uruchamiaj z Windows” ustawia tylko zainstalowany Sessionwright: kopia przenośna już go nie przejmuje."),
        ]),
        ("2026-11-18",
        [
            ("A machine that is not in Azure can be woken (Wake-on-LAN): give the connection its MAC address under Details in the editor (Detect reads it while the machine is on). Connecting to it while it is off wakes it and waits; Machine → Wake up and \"wake srv01\" in the search do it without connecting.",
             "Maszynę spoza Azure można obudzić (Wake-on-LAN): podaj jej adres MAC w edytorze połączenia, na karcie Szczegóły (Wykryj odczytuje go, gdy maszyna jest włączona). Łączenie z wyłączoną maszyną budzi ją i czeka; Maszyna → Obudź oraz „obudź srv01” w wyszukiwaniu robią to bez łączenia."),
            ("Full screen on several monitors: ticked monitors that are not next to each other (the left and the right of three) now take the ones between them along, instead of showing a stretched picture. Remote Desktop can only use monitors that are next to each other; the settings say which ones are added.",
             "Pełny ekran na kilku monitorach: zaznaczone monitory, które ze sobą nie sąsiadują (lewy i prawy z trzech), biorą teraz ze sobą te pomiędzy, zamiast pokazywać rozciągnięty obraz. Pulpit zdalny może używać tylko sąsiadujących monitorów; ustawienia mówią, które zostaną dodane."),
            ("Restarting or shutting down a machine, and the list of who is signed in, say that the saved password cannot be read (data copied from another Windows account) instead of showing an unexpected error.",
             "Restart i wyłączanie maszyny oraz lista zalogowanych osób mówią, że zapisanego hasła nie da się odczytać (dane skopiowane z innego konta Windows), zamiast pokazywać nieoczekiwany błąd."),
        ]),
        ("2026-11-16.2",
        [
            ("The search opens as a palette over the window you are in (Ctrl+K, or the quick-connect key inside a session). Empty, it lists the open sessions and the connections used last; Tab on a connection lists what can be done with it; > lists commands only.",
             "Wyszukiwanie otwiera się jako paleta nad oknem, w którym jesteś (Ctrl+K albo klawisz szybkiego łączenia w sesji). Pusta pokazuje otwarte sesje i ostatnio używane połączenia; Tab na połączeniu pokazuje, co można z nim zrobić; > pokazuje same polecenia."),
            ("A sign-out stays covered when you click a window of another program meanwhile: the typing into the session no longer shows.",
             "Wylogowywanie zostaje zasłonięte, gdy w jego trakcie klikniesz okno innego programu: wpisywanie do sesji już się nie pokazuje."),
            ("A first sign-in after the machine started says that Windows is preparing the desktop, instead of that signing in takes longer than usual.",
             "Pierwsze logowanie po uruchomieniu maszyny mówi, że Windows przygotowuje pulpit, zamiast że logowanie trwa dłużej niż zwykle."),
            ("The full-screen bar dropped on a monitor the session does not use says why it went back.",
             "Pasek sesji pełnoekranowej upuszczony na monitor, którego sesja nie używa, mówi, dlaczego wrócił."),
        ]),
        ("2026-11-15.2",
        [
            ("The bar at the top of a full-screen session is shorter: opening a link, the small view and Ctrl+Alt+Del are under More.",
             "Pasek u góry sesji pełnoekranowej jest krótszy: otwieranie linku, mały podgląd i Ctrl+Alt+Del są pod Więcej."),
            ("The connections used last on the home page connect on a double-click, like the list and the cards.",
             "Ostatnio używane połączenia na ekranie startowym łączą po dwukliku, tak jak lista i karty."),
            ("The editor has a Connecting tab for what happens before connecting (a VPN or a command); the Azure tab shows only with an Azure account in Settings.",
             "Edytor ma zakładkę Łączenie na to, co dzieje się przed połączeniem (VPN albo polecenie); zakładka Azure pokazuje się tylko przy koncie Azure w Ustawieniach."),
            ("In the credentials window a typed password is saved on leaving its box; Remove password takes a stored one away.",
             "W oknie poświadczeń wpisane hasło zapisuje się po wyjściu z pola; Usuń hasło usuwa zapisane."),
        ]),
        ("2026-11-15",
        [
            ("A script runs on several machines at once: mark the connections, or open a group's menu, and pick it under Scripts. One table shows each machine's result.",
             "Skrypt uruchamia się na wielu maszynach naraz: zaznacz połączenia albo otwórz menu grupy i wybierz go w Skryptach. Jedna tabela pokazuje wynik każdej maszyny."),
            ("The home page lists the connections used last while no session is open.",
             "Ekran startowy pokazuje ostatnio używane połączenia, gdy żadna sesja nie jest otwarta."),
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

    /// <summary>The date an entry's day stands for, without what tells it from another entry of the same date.</summary>
    public static DateTime DateOf(string day) => DateTime.ParseExact(day[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The entries newer than the day already seen (all of them when none was).</summary>
    internal static List<(string Day, (string En, string Pl)[] Items)> Since(string? seen) =>
        [.. Entries.Where(e => string.CompareOrdinal(e.Day, seen ?? "") > 0)];
}
