# Contributing to Sessionwright

Bug reports, ideas and pull requests are welcome. For anything larger than a fix, open an issue first so the direction can be agreed before the work is done.

## Build and test

Requires Windows and the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/Sessionwright
```

To try a build without touching your own connections, point it at another data file:

```powershell
$env:SESSIONWRIGHT_DATA = "$env:TEMP\sessionwright-test\data.json"
dotnet run --project src/Sessionwright
```

Each data file gets its own instance, so this runs next to the installed Sessionwright.

## How the code is organized

| Where | What |
|---|---|
| `RdpSession.cs` | One RDP connection: hosts the Windows Remote Desktop ActiveX control (`mstscax.dll`) and everything typed into it. |
| `SessionWindow.xaml.cs` | The window of one session: full screen, monitor moves, covers during transitions, sign-out. |
| `SessionManager.cs` | Opens, reconnects and closes sessions. |
| `Store.cs`, `Models.cs` | The data file: loading, saving, inheritance of credentials and settings. |
| `AzureVms.cs`, `AzureShutdown.cs`, `Machines.cs` | Azure Resource Manager: state, power, auto-shutdown, Run Command. |
| `MainWindow.xaml.cs` | The connection list, search and home page. |
| `Loc.cs` | Every text, English and Polish side by side. |
| `WhatsNew.cs` | What users are told after an update. |

## Conventions

- **Comments say why, not what.** Most of the non-obvious code exists because of something measured on a real server; the comment keeps that reason.
- **Texts** go into `Loc.cs` in both languages. Text in code, logs and test data is English.
- **A change users will notice** gets a line in `WhatsNew.cs` under a new day on top.
- **No picture jumps.** Anything that resizes a session happens under a cover that lifts once the server has repainted. Do not add instant resolution or window changes.
- **Sessions live in their own windows.** There is no tab mode.
- **Dependencies**: prefer the standard library and what is already referenced.
- **Tests** cover logic that needs no window (data, importers, parsing). Add one for new logic of that kind.

## Checking changes to sessions

Unit tests cannot see a remote desktop. A change to connecting, full screen, sign-out or typing into a session needs a run against a real server, and the pull request should say what was tried. Keys injected by automation do not behave like a physical keyboard inside a session, so keyboard shortcuts have to be pressed by hand.

## Pull requests

- One topic per pull request.
- Say what changes for the user and how it was checked, including what was not checked.
- `dotnet test` must pass; the build workflow runs it on every push.

## Security

Please do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).
