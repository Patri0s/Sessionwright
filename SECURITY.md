# Security

## Reporting a vulnerability

Report it privately: **Security → Report a vulnerability** on this repository
([direct link](https://github.com/Patri0s/Sessionwright/security/advisories/new)). Please do not open a public issue.

Include what an attacker needs (access to the PC, to the data file, to the network), what they gain, and steps to reproduce. You can expect a first answer within a week.

## Supported versions

Fixes go into the latest release. Sessionwright updates itself, so there are no maintained older branches.

## What Sessionwright stores and how

- **Passwords** are encrypted with Windows DPAPI for the current Windows account and kept in `%APPDATA%\Sessionwright\data.json`. They cannot be read on another account or PC. Anything running as you on the same PC can decrypt them: that is the limit of DPAPI, and Sessionwright does not claim more.
- **Azure tokens** are in the encrypted MSAL token cache, not in the data file.
- **Exports** carry passwords only when you ask, encrypted with a password of your choice (PBKDF2-SHA256, AES-GCM).
- **Scripts and notes** are stored as plain text. Do not put passwords in them.
- **Problem reports** leave out passwords, notes, script text and the secret parts of link addresses, but keep host and account names.

## What Sessionwright connects to

Your servers (RDP, and port 445 for the signed-in users list and sign-out when it answers), Azure Resource Manager when you sign in to Azure, and the update feed. There is no telemetry.

## Known boundaries

- The installer is not code-signed yet. Verify that you downloaded it from this repository's Releases page.
- Updates are taken over https or from a folder; an update folder is as trustworthy as its write permissions.
- When the server's Remote Desktop Services API is not reachable, sign-out and opening a link type into the session's Run box. Only web addresses are ever typed.
