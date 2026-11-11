<div align="center">

<img src="docs/images/logo.png" alt="Sessionwright logo" width="96" height="96">

# Sessionwright

**The remote desktop manager for people who live on many servers.**

One window per session, a Fluent look, and everything around a session that usually takes a second tool:<br>
starting the machine, seeing who is signed in, running a script on it.

[![Build](https://github.com/Patri0s/Sessionwright/actions/workflows/build.yml/badge.svg)](https://github.com/Patri0s/Sessionwright/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Patri0s/Sessionwright?display_name=tag&sort=semver&color=2f81f7)](https://github.com/Patri0s/Sessionwright/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Patri0s/Sessionwright/total?color=2f81f7)](https://github.com/Patri0s/Sessionwright/releases)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

[**Download**](https://github.com/Patri0s/Sessionwright/releases/latest) ·
[Features](#features) ·
[Getting started](#getting-started) ·
[Azure](#azure) ·
[Your data](#your-data-and-privacy) ·
[Polski](docs/README.pl.md)

<br>

<img src="docs/images/main.png" alt="The main window: connections on the left, open sessions on the right" width="900">

</div>

## Why Sessionwright

- **Sessions are windows.** Each one has its own taskbar entry, remembers its monitor and size, and goes full screen on the monitors you pick.
- **No picture jumps.** Switching between window and full screen happens under a short cover that lifts once the server has repainted.
- **The machine is part of the connection.** Start an Azure VM that is off, postpone its auto-shutdown, see who is signed in, run a script — without leaving the app.
- **Nothing is left behind.** Sessions you disconnected without signing out are remembered and shown, so nothing keeps running on a server by accident.
- **Private by design.** Passwords are encrypted for your Windows account, there is no telemetry, and the app talks only to your servers, Azure and the update feed.

## Features

<table>
<tr>
<td width="33%" valign="top">

### Connections

- Nested groups with drag and drop
- Favorites on <kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd>
- Credential library, inherited from groups
- RDP settings inherited the same way
- Environment colors, notes and links
- Import from `.rdp`, RDCMan and mRemoteNG

</td>
<td width="33%" valign="top">

### Sessions

- One window per session
- Remote resolution follows the window
- Full screen on any, all or chosen monitors
- Sign out, <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Del</kbd>, reconnect
- Small always-on-top view
- Connection quality at a glance

</td>
<td width="33%" valign="top">

### Machines

- Azure VM state next to the connection
- Start, shut down, restart
- Postpone or skip the auto-shutdown
- Start machines ahead of the working day
- Signed-in users: sign out or disconnect
- Script library with output in a window

</td>
</tr>
</table>

### Search that is also a command line

Press <kbd>Ctrl</kbd>+<kbd>F</kbd> and type. The same box finds a connection, connects to an address you type, and runs commands: `start vm-12`, `restart prod`, `who is on srv01`. A global key (set in Settings) brings it forward from anywhere in Windows.

<div align="center">
<img src="docs/images/search.png" alt="Typing “restart” lists the restart command for every machine it applies to" width="900">
</div>

### Scripts on the machine

Run PowerShell on the machine behind a connection and read the output in a window. No port has to be open: scripts go through Azure Run Command. Built in: free disk space, service status, uptime and the busiest processes, restart IIS, restart the batch service, pending reboot. Add your own under *More → Scripts*.

<div align="center">
<img src="docs/images/scripts.png" alt="The script library" width="700">
</div>

### More details

<details>
<summary><b>Connections</b></summary>

- A tree of connections in nested groups, with drag and drop, favorites and multiple selection.
- A credential library. Passwords are encrypted for your Windows account (DPAPI) and can be inherited from a group.
- RDP settings inherited the same way: connection → nearest group → defaults.
- Environment colors (red for production, say) shown on the row, the session window and the full-screen bar.
- Notes and web links on a connection or a group; a group's link can be a template (`https://{name}.example.com`).
- Host availability at a glance (a TCP check of the RDP port).
- Import from `.rdp`, Remote Desktop Connection Manager (`.rdg`) and mRemoteNG (`confCons.xml`); export with passwords protected by a password of your choice.

</details>

<details>
<summary><b>Sessions</b></summary>

- Every session in its own window with its own taskbar entry. The remote resolution follows the window.
- Full screen on any monitor, on all of them, or on the ones you pick; drag the bar to another monitor and the session follows.
- A status screen of its own while connecting and signing in, with a one-sentence message when a connection fails.
- Sign out, Ctrl+Alt+Del and reconnect from the title bar, the full-screen bar and the taskbar thumbnail.
- A small always-on-top view of a session, to keep an eye on a long build while you work elsewhere.
- Every connection opens where it was last time: monitor, position and size.
- Favorites and recent connections on the taskbar jump list.

</details>

<details>
<summary><b>Machines</b></summary>

- Link a connection to an Azure VM: its state shows next to the connection, and connecting starts a machine that is off (after asking).
- Start, shut down (deallocate), restart; postpone or skip the auto-shutdown; start machines ahead of the working day.
- See who is signed in on a server and sign them out or disconnect them, without opening a session.
- A script library: run PowerShell on the machine and read the output and errors in a window.

</details>

<details>
<summary><b>The app itself</b></summary>

- English and Polish, light, dark and high-contrast themes, screen reader support.
- Updates itself in the background; "What's new" after an update.
- Lives in the notification area if you want it to, and can start with Windows.

</details>

## Getting started

### Install

Requires Windows 10 or 11 (x64). No administrator rights needed.

1. Download **`Sessionwright-win-Setup.exe`** from the [latest release](https://github.com/Patri0s/Sessionwright/releases/latest).
2. Run it. Sessionwright installs for the current user into `%LocalAppData%\Sessionwright`.

Prefer no installer? **`Sessionwright-win-Portable.zip`** from the same page runs from any folder.

> [!NOTE]
> The installer is not code-signed yet, so Windows SmartScreen may show "Windows protected your PC". Choose **More info → Run anyway**.

Sessionwright checks for updates in the background and installs a new version when you confirm, or at the next start.

### First steps

1. **Add a connection** — *New connection*, type the host, pick or create a credential.
2. **Connect** — double-click it, or press <kbd>Ctrl</kbd>+<kbd>F</kbd>, type part of its name and press <kbd>Enter</kbd>.
3. **Group and color** — drag connections into groups; give a group a color and a credential, and everything in it inherits both.

Coming from another tool? *More → Import* reads `.rdp`, `.rdg` and mRemoteNG files.

### Keyboard

| Keys | Action |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | Search, connect to an address, or run a command |
| <kbd>Ctrl</kbd>+<kbd>N</kbd> | New connection |
| <kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd> | Connect to that favorite |
| <kbd>Enter</kbd> / <kbd>F2</kbd> / <kbd>Del</kbd> | Connect / edit / delete the selected item |
| <kbd>Shift</kbd>+<kbd>↑</kbd>/<kbd>↓</kbd>, <kbd>Ctrl</kbd>+<kbd>A</kbd> | Select several connections |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> | Undo the delete just made |
| <kbd>Ctrl</kbd>+<kbd>M</kbd> | Azure machines |
| <kbd>Ctrl</kbd>+<kbd>,</kbd> | Settings |
| <kbd>F1</kbd> | All shortcuts |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Enter</kbd> (in a session) | Full screen on / off |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>←</kbd> / <kbd>→</kbd> (in a session) | Previous / next open session |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>↓</kbd> | Session switcher |

## Azure

Sign in under *Settings → Azure accounts*, then link a connection to a VM in its editor. Sessionwright talks to Azure Resource Manager as you; what works depends on your role on the machine:

| Feature | Needs |
|---|---|
| State, start, shut down, restart | `Microsoft.Compute/virtualMachines/*` actions (e.g. Virtual Machine Contributor) |
| Postpone or skip the auto-shutdown | `Microsoft.DevTestLab/schedules/write` on the resource group |
| Scripts, signed-in users through Azure | `Microsoft.Compute/virtualMachines/runCommand/action` |

Scripts run through Azure Run Command: the script runs as SYSTEM, takes up to a minute, and Azure allows one command at a time per machine.

## Your data and privacy

| | |
|---|---|
| **Where** | Everything is in `%APPDATA%\Sessionwright\data.json` (set `SESSIONWRIGHT_DATA` to use another path). It is written atomically with a backup copy, and a damaged file is kept and restored from the last good one. |
| **Passwords** | Encrypted with Windows DPAPI for your account: the file is useless on another account or PC. Azure tokens live in the encrypted MSAL cache, never in the data file. |
| **One writer** | One Sessionwright uses a data file at a time; a second one says so instead of overwriting changes. |
| **Telemetry** | None. Sessionwright connects only to your servers, to Azure (when you sign in) and to the update feed. |
| **Diagnostics** | A log is at `%APPDATA%\Sessionwright\sessionwright.log`. *Settings → Updates and data → Save a problem report* makes a zip with the log and the data file stripped of passwords, notes, script text and the secret parts of link addresses. |

More in [SECURITY.md](SECURITY.md).

## Known limits

- **Sign-out** uses the server's Remote Desktop Services API when port 445 answers. Otherwise it types `logoff` into the session's Run box under a cover, which depends on the keyboard and the session's state.
- **Opening a link in a session** is typed the same way; RDP has no channel for it.
- **Scripts** need an Azure-linked connection. The service names in the built-in Dynamics 365 scripts fit a standard development VM and may need editing for yours.
- **The small view** shows the session window's own picture: a minimized session window has none.
- **Picking monitors** for full screen was verified with two monitors.
- **Updates** are taken from https or a folder only, and the installer is unsigned for now.

## Build from source

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet run --project src/Sessionwright
```

| Path | What |
|---|---|
| `src/Sessionwright` | The application (WPF). `RdpSession` hosts the RDP ActiveX control, `SessionWindow` owns one session, `Store` reads and writes the data file, `AzureVms` talks to Azure. |
| `tests/Sessionwright.Tests` | Unit tests of the data layer, importers, Azure parsing and other logic without a window. |
| `build/publish.ps1` | Packs a release (installer, portable zip, update packages). |
| `.github/workflows` | Build and test on every push; release on a `v*` tag. |

Texts are in `src/Sessionwright/Loc.cs` (English and Polish side by side). User-visible changes go into `src/Sessionwright/WhatsNew.cs`, which the app shows after an update.

<details>
<summary><b>Releasing</b></summary>

<br>

Push a version tag and GitHub Actions tests, builds and publishes the release:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

Locally, `build/publish.ps1 -Version 1.1.0 -UpdateFeed https://github.com/Patri0s/Sessionwright -Upload` does the same; `-SignParams` or `-AzureTrustedSignFile` signs the installer. Details are in the script's header.

</details>

## Contributing

Bug reports, ideas and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Found a vulnerability? Please report it privately, as described in [SECURITY.md](SECURITY.md).

## Built with

Licenses of these components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

[WPF-UI](https://github.com/lepoco/wpfui) · [Velopack](https://github.com/velopack/velopack) · [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) · [Azure.Identity](https://github.com/Azure/azure-sdk-for-net) · the Remote Desktop ActiveX control that ships with Windows (`mstscax.dll`)
