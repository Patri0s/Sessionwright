# Sessionwright

Nowoczesny menedżer połączeń pulpitu zdalnego (RDP) dla Windows — następca mRemoteNG w stylu Windows 11 (Fluent, Mica, jasny/ciemny motyw).

## Funkcje
- Drzewo połączeń z zagnieżdżonymi grupami, przeciąganie i upuszczanie, ulubione, ostatnie połączenia.
- Biblioteka poświadczeń (hasła szyfrowane DPAPI dla bieżącego użytkownika Windows) z dziedziczeniem z grup.
- Dziedziczenie ustawień RDP: połączenie → najbliższa grupa → ustawienia domyślne.
- Każda sesja we własnym oknie, z własną pozycją na pasku zadań (najedź na ikonę Sessionwright, by przełączać). Kontrolka `mstscax.dll`, dynamiczna rozdzielczość podążająca za oknem, pełny ekran na dowolnym monitorze lub na wszystkich monitorach.
- Płynne przejścia: zmiana okno ↔ pełny ekran, przeniesienie na inny monitor, pokazanie pulpitu po zalogowaniu i rozłączenie odbywają się pod krótkim przyciemnieniem, które znika dopiero, gdy serwer narysuje pulpit w nowej rozdzielczości – bez przeskoków obrazu.
- Podczas łączenia i logowania własny ekran statusu (ekrany logowania serwera są ukryte); gdy logowanie trwa długo, link „Pokaż ekran serwera” (np. dla komunikatu wymagającego kliknięcia).
- Wylogowanie i Ctrl+Alt+Del z paska tytułu, paska pełnego ekranu i miniatury na pasku zadań. Klawisze trafiają bezpośrednio do sesji przez kontrolkę RDP (nigdy do innego okna): Ctrl+Alt+Del otwiera prawdziwy ekran zabezpieczeń (także na ekranie blokady), a wylogowanie (Win+R, `logoff`) jest zasłonięte i sprawdzane (komunikat, jeśli się nie udało).
- Biblioteka skryptów PowerShell uruchamianych na maszynie połączenia powiązanego z Azure (Run Command: bez otwartych portów, jako SYSTEM, do minuty, jedno polecenie naraz na maszynę; wymaga `Microsoft.Compute/virtualMachines/runCommand/action`): „Uruchom na maszynie” w menu połączenia i w menu maszyny w oknie sesji, wynik i błędy w osobnym oknie. Wbudowane: wolne miejsce, stan usług, czas działania i procesy, restart IIS, restart usługi batch, oczekujący restart; własne w Więcej → Skrypty. Azure zwraca ostatnie 4096 znaków wyniku; treść skryptów nie trafia do raportu problemu.
- Mały podgląd sesji zawsze na wierzchu (w menu „…” na pasku tytułu sesji, przyciskiem na pasku pełnego ekranu i na karcie sesji): żywy obraz okna sesji w małym okienku, które można przesuwać i skalować; dwuklik przechodzi do sesji. Zminimalizowane okno sesji nie ma obrazu.
- „Co nowego” po aktualizacji: pasek na stronie głównej z przyciskiem otwierającym listę zmian od ostatniego uruchomienia (zamknięcie paska albo otwarcie listy oznacza ją jako przeczytaną); lista jest zawsze dostępna w Ustawieniach → Aktualizacje i w wyszukiwarce poleceń. Wpisy są w `WhatsNew.cs` (nowy dzień na górze).
- Kolory środowisk (np. czerwony dla produkcji) dla grup i połączeń: widoczne w drzewie, w oknie sesji i na pasku pełnego ekranu.
- Strona główna pokazuje tylko to, czego nie pokaże lista: otwarte sesje (miniatura, stan, jakość łącza) i sesje pozostawione na serwerach; w drzewie wyróżnione są otwarte połączenia. Maszyny Azure obsługuje się z menu połączenia („Maszyna”), z okna maszyn (Ctrl+M) i z wyszukiwarki poleceń.
- Nieudane połączenie ma jednozdaniowy komunikat w języku aplikacji (nie znaleziono serwera, brak odpowiedzi, przerwane, odrzucone logowanie), a tekst systemowy pod spodem jako szczegóły.
- Pasek zadań: ulubione i ostatnie połączenia na liście szybkiego dostępu ikony Sessionwright, przyciski pełnego ekranu / wylogowania / rozłączenia pod miniaturą sesji; miganie przycisku po rozłączeniu w tle.
- Każde połączenie otwiera się tam, gdzie było ostatnio (monitor, pozycja, rozmiar – również przy różnym skalowaniu monitorów); wskaźnik jakości łącza; komunikat przy automatycznym ponownym łączeniu; ponowne łączenie w tym samym oknie z aktualnymi poświadczeniami.
- Wyszukiwanie i szybkie łączenie z paska tytułu (Ctrl+F, `host[:port]` + Enter – zawsze z dokładnie wpisanym adresem); wyszukiwarka podpowiada też polecenia (np. Import, Rozłącz wszystkie).
- Sprawdzanie dostępności hostów (test TCP portu RDP; hosty za bramą RD Gateway są pomijane).
- Import plików `.rdp`, `.rdg` z Remote Desktop Connection Manager (grupy, ustawienia RDP, poświadczenia; hasła, jeśli zapisało je to samo konto Windows), `confCons.xml` z mRemoteNG (z ustawieniami RDP, bez haseł) i eksportu Sessionwright (opcjonalnie z hasłami zaszyfrowanymi hasłem eksportu); ostrzeżenia o przekierowaniu dysków/kart i bramach z importowanych plików.
- Interfejs PL/EN (domyślnie język systemu), motyw jasny/ciemny/systemowy (także kontrast), obsługa czytników ekranu.

## Skróty
| Skrót | Akcja |
|---|---|
| Ctrl+F | Szukaj / szybkie połączenie |
| Ctrl+N | Nowe połączenie |
| Ctrl+1…9 | Połącz z ulubionym o tym numerze |
| Ctrl+M | Maszyny Azure |
| Ctrl+, | Ustawienia |
| F1 | Lista skrótów |
| F11 (gdy fokus jest poza pulpitem zdalnym) | Pełny ekran |
| Ctrl+Alt+Enter (w sesji) | Pełny ekran / wyjście z pełnego ekranu |
| Ctrl+Alt+← / → (w sesji) | Poprzednia / następna otwarta sesja |
| Ctrl+Alt+↓ | Przełącznik sesji |
| F2 / Del | Edytuj / usuń zaznaczony element |
| F5 | Odśwież dostępność hostów |

W pełnym ekranie u góry jest pasek połączenia: przeciągnij go na inny monitor, aby przenieść tam sesję; dwuklik na pasku wychodzi z pełnego ekranu; pinezka chowa pasek przy krawędzi.

Rozdzielczość **Pełny ekran na kilku monitorach** działa jak `mstsc /multimon`: sesja zajmuje wszystkie monitory albo tylko zaznaczone w ustawieniach (jak `selectedmonitors` w pliku `.rdp`), a serwer widzi je jako osobne monitory. Wybór jest zapamiętywany po nazwie monitora; gdy żadnego z wybranych nie ma (np. w domu), używane są wszystkie. Układ monitorów jest ustalany przy łączeniu – poza pełnym ekranem okno pokazuje cały pulpit zdalny przeskalowany.

## Budowanie
Wymaga .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/Sessionwright
```

## Dane i diagnostyka
- Dane: `%APPDATA%\Sessionwright\data.json` (inna ścieżka: zmienna środowiskowa `SESSIONWRIGHT_DATA`), kopia `.bak` przy każdym zapisie. Uszkodzony plik jest zachowywany jako `data.json.corrupt-<data>` i odtwarzany z ostatniej poprawnej kopii (z komunikatem).
- Z jednego pliku danych korzysta naraz jeden Sessionwright: obok leży `data.json.lock`, więc drugie uruchomienie (to samo konto zalogowane w Windows drugi raz, inny komputer na tym samym pliku) kończy się komunikatem zamiast nadpisywać zmiany.
- Dziennik diagnostyczny: `%APPDATA%\Sessionwright\sessionwright.log` (rotacja 1 MB) – wersja, monitory, przebieg sesji (łączenie, logowanie, kody rozłączeń) i czasy przejść; otwierany z Ustawień („Otwórz dziennik diagnostyczny”).

## Instalacja i aktualizacje
- Instalacja dla bieżącego użytkownika, bez uprawnień administratora: uruchom `Sessionwright-win-Setup.exe` z najnowszego wydania na GitHubie (Releases) albo z folderu wydań (np. `\\fileserver\apps\Sessionwright`). Program trafia do `%LocalAppData%\Sessionwright`, skróty – na pulpit i do menu Start; odinstalowanie: Ustawienia Windows → Aplikacje. Alternatywa bez instalatora: `Sessionwright-win-Portable.zip`.
- Dane (`%APPDATA%\Sessionwright`) leżą poza folderem instalacji – aktualizacja ani odinstalowanie ich nie usuwają.
- Źródło aktualizacji to repozytorium GitHub (`https://github.com/właściciel/repo`, wydania z GitHub Releases), folder (UNC/lokalny) albo adres https z plikami wydania (zwykłe http jest odrzucane) (`releases.win.json`, `*.nupkg`). Repozytorium jest czytane bez tokenu: dopóki jest prywatne, zainstalowane kopie nie widzą aktualizacji (sprawdzenie kończy się zapisem w dzienniku); działają od chwili, gdy repozytorium stanie się publiczne. Kolejność: pole **Źródło aktualizacji** w Ustawieniach → domyślne źródło wbudowane przy budowaniu (`-UpdateFeed`). Bez źródła aktualizacje są wyłączone; wersje uruchamiane przez `dotnet run` lub zwykłe `dotnet publish` nigdy się nie aktualizują.
- Sessionwright sprawdza źródło w tle i pobiera nową wersję; instaluje ją po potwierdzeniu (ponowne uruchomienie) albo przy następnym starcie, o ile nie działa inna instancja Sessionwright.

## Wydanie nowej wersji
Wymaga .NET 10 SDK; narzędzie `vpk` (Velopack) jest przypięte w `.config/dotnet-tools.json` i instalowane przez skrypt (`dotnet tool restore`).

Najprościej: tag wersji – GitHub Actions (`.github/workflows/release.yml`) testuje, buduje i publikuje wydanie na GitHubie:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

Lokalnie (to samo; token z `GITHUB_TOKEN` albo z zalogowanego GitHub CLI – `gh auth login`):

```powershell
./build/publish.ps1 -Version 1.1.0 -UpdateFeed https://github.com/Patri0s/Sessionwright -Upload
./build/publish.ps1 -Version 1.1.0 -UpdateFeed \\fileserver\apps\Sessionwright -Upload   # albo do folderu
```

- Buduje wersję self-contained `win-x64` i pakuje ją do `releases/` (`Sessionwright-win-Setup.exe`, `Sessionwright-win-Portable.zip`, pakiety `full`/`delta`, `releases.win.json`); z `-Upload` publikuje wydanie na GitHubie z tagiem `v<wersja>` (`vpk upload github`) albo kopiuje je do folderu źródła i scala listę wydań (`vpk upload local`). Dla źródła http skopiuj zawartość `releases/` na serwer jako pliki statyczne.
- Numer wersji (SemVer, każde wydanie wyższe): jedynym źródłem jest `<Version>` w `src/Sessionwright/Sessionwright.csproj` (buildy deweloperskie); `-Version` nadpisuje go przy wydaniu.
- Skrypt pobiera poprzednie wydanie ze źródła, więc klienci ściągają tylko pakiet różnicowy (delta).
- `-UpdateFeed` wbudowuje domyślne źródło w program; użytkownik może je zmienić w Ustawieniach.
- Podpisywanie kodu: niepodpisany instalator wywołuje ostrzeżenie SmartScreen („System Windows ochronił ten komputer” → Więcej informacji → Uruchom mimo to). Produkcyjnie podpisuj: `-SignParams '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <odcisk certyfikatu>'` (certyfikat w magazynie Windows, bez haseł w skrypcie) albo `-AzureTrustedSignFile metadata.json` (Azure Trusted Signing, logowanie przez Azure CLI).
- CI (GitHub Actions): `build.yml` buduje i testuje push/PR do `master`; `release.yml` publikuje wydanie po wypchnięciu tagu `v*`.
