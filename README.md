# QbtAutoUpdate

[![Build](https://github.com/MrGautier256/QbtAutoUpdate/actions/workflows/build.yml/badge.svg)](https://github.com/MrGautier256/QbtAutoUpdate/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/MrGautier256/QbtAutoUpdate)](https://github.com/MrGautier256/QbtAutoUpdate/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Keeps the official [qBittorrent](https://www.qbittorrent.org/) for Windows up to date. When a new
release is out, QbtAutoUpdate:

1. closes qBittorrent cleanly, so it saves its resume data first;
2. downloads the official installer and checks its OpenPGP signature;
3. installs it silently, keeping your libtorrent series (1.2 or 2.x);
4. starts qBittorrent again.

It runs from Task Scheduler (no background process), and after a single administrator prompt at setup it
never asks again.

*[Version française](README.fr.md)*

## Installing

**Requirements**
- Windows 10 (version 1809 or later) or Windows 11, on x64 or ARM64.
- qBittorrent installed with the official installer from qbittorrent.org. The portable version, 32-bit
  builds and forks such as qBittorrent Enhanced Edition are not supported.

**Steps**
1. Download `QbtAutoUpdate-win-x64.exe` from the [latest release](https://github.com/MrGautier256/QbtAutoUpdate/releases/latest).
   On Windows on ARM, download `QbtAutoUpdate-win-arm64.exe` instead.
2. Double-click it. The executable is not code-signed yet (see [the FAQ](#why-does-windows-smartscreen-warn-about-it)),
   so SmartScreen may show "Windows protected your PC": click **More info**, then **Run anyway**.
3. Pick a mode:
   - **Update automatically**: close qBittorrent cleanly, install, start it again.
   - **Ask me first with a notification**: a Windows notification with "Update now", "Later" and
     "Skip this version".
4. Click **Install** and accept the administrator prompt. That is the only one.

QbtAutoUpdate copies itself to `C:\Program Files\QbtAutoUpdate` and adds itself to the Start menu and to
*Installed apps*. You can delete the downloaded file afterwards.

To upgrade QbtAutoUpdate itself, run the newer release the same way. The window offers to upgrade in place
and keeps your settings.

## Using it

Open **QbtAutoUpdate** from the Start menu to see what is installed and when the last check ran. From
there you can:
- check for updates now;
- turn automatic updates off or on;
- switch between the two modes;
- reinstall the previous version;
- open the logs;
- uninstall.

**When it checks.** It looks for a new release 3 minutes after you sign in, then every 6 hours. It skips
the check while you are on a metered connection, unless you started it yourself.

**What it installs.** It installs the same variant you already use:
- qBittorrent 5.3.0 changed its default build to libtorrent 2.1;
- if you run the libtorrent 1.2 build, QbtAutoUpdate installs the `lt12` build;
- it never switches series unless you ask (`config --variant`).

**When qBittorrent cannot close.** If qBittorrent cannot be closed (a dialog is open, or it is busy), the
update waits for the next check, and a notification offers to try again.

**Rolling back.** If a release causes problems, **Reinstall the previous version** puts back the release
before it, in the same libtorrent series. Automatic updates then skip the version you left until a newer
one comes out. A manual "check now" can still install it.

### Command line

The same exe has a command line for scripting. Run `QbtAutoUpdate.exe help` in a terminal for the details.

| Command | What it does |
|---------|--------------|
| `status` | Installed version and variant, latest release, settings, last result, log locations |
| `check` | Look for an update; install it (auto mode) or announce it (notify mode) |
| `update` | Download, verify and install the latest release now |
| `setup --quiet [--mode auto\|notify] [--interval <hours>] [--variant auto\|lt2\|lt12]` | Unattended setup (administrator) |
| `config [--enabled on\|off] [--mode ...] [--interval ...] [--variant ...] [--skipped-version <v>\|none]` | Change settings (administrator); without options, show them |
| `rollback` | Reinstall the previous release (administrator) |
| `restart` | Close qBittorrent the way an update does and start it again |
| `webui --url http://localhost:8080 --api-key <key>` | Optional: close qBittorrent through its WebUI instead of the Restart Manager |
| `uninstall [--keep-logs] [--quiet]` | Remove QbtAutoUpdate (administrator) |

Every command accepts `--dry-run`, which does everything except closing qBittorrent and installing.

### Settings file

Machine settings live in `C:\ProgramData\QbtAutoUpdate\config.json`, which only administrators can change.
The `config` command covers the common ones. Other keys can be edited by hand and apply at the next run.

| Key | Default | Meaning |
|-----|---------|---------|
| `enabled` | `true` | Automatic checks on or off |
| `mode` | `notify` | `auto` or `notify` (the setup window asks) |
| `checkIntervalHours` | `6` | Hours between checks (1–168) |
| `variant` | `auto` | `auto` keeps the installed libtorrent series; `lt2` or `lt12` forces one |
| `minReleaseAgeHours` | `0` | Wait until a release is this old before installing it automatically |
| `skipOnMeteredConnection` | `true` | No automatic download on a metered connection |
| `closeTimeoutSeconds` | `120` | How long qBittorrent gets to close |
| `restoreDesktopShortcut` | `true` | Recreate the Public desktop shortcut that the installer removes |
| `backupConfig` | `true` | Back up qBittorrent's settings before each update (last 3 kept) |
| `backupTorrents` | `false` | Also back up `BT_backup` (resume data), which can be large |
| `notifications` | `all` | `all`, `errors` or `none` |
| `allowForceClose` | `false` | Kill qBittorrent if it does not close in time. **Not recommended**: see below |
| `skippedVersion` | none | Set by rollback; automatic updates skip that version |

About `allowForceClose`: killing qBittorrent loses the resume data written since its last save, so
torrents can restart from older progress or be rechecked. It is off by default and should stay off unless
qBittorrent is known to hang on exit.

## How it works

- **Check task.** It runs as each signed-in user, without administrator rights, at logon and every few
  hours. It reads the latest version from qbittorrent.org and finds the installer and its signature on
  SourceForge.
  - In auto mode it downloads and verifies them, closes qBittorrent, backs up its settings, then hands the
    files to the Apply task.
  - In notify mode it shows a notification first.
- **Apply task.** It runs as SYSTEM, on demand, from `C:\Program Files\QbtAutoUpdate` (a folder only
  administrators can change), and takes no arguments.
  - It accepts files only through a drop folder where standard users can add files but cannot list or
    read anyone else's.
  - It copies them to a folder only SYSTEM and administrators can access, and verifies everything again
    there.
  - It runs the installer with `/S /D=<current folder>` and checks the installed version and variant.
- **Restart.** The Check task then starts qBittorrent again in your session, with the same instance
  options it had.

Nothing stays running between checks.

## Security

- **Signature.** Every installer is verified against the OpenPGP signature published next to it, using
  qBittorrent's release key. The key is embedded in the exe and its fingerprint pinned:
  `D8F3 DA77 AAC6 7410 5359  9C13 6E4A 2D02 5B7C C9A2`. An unknown, expired or revoked key, a missing
  signature or a modified file stops the update; nothing runs.
- **Other installer checks.** The installer name, its version resource, its architecture and its libtorrent
  series must all match what was expected. Older versions are refused, except through `rollback`, which
  requires an administrator.
- **Downloads.** HTTPS only, from an allow-list of hosts: www.qbittorrent.org, qbittorrent.github.io,
  sourceforge.net, downloads.sourceforge.net and its mirrors. The final host is checked again after every
  redirect.
- **Notification buttons.** They carry a secret token per user. A `qbtautoupdate:` link from a web page
  without it is ignored.
- **qBittorrent is never killed** unless you turn on `allowForceClose`.
- **Logs** contain no passwords or API keys. The optional WebUI secrets are encrypted with Windows DPAPI
  for your account.

Details: [docs/DECISIONS.md](docs/DECISIONS.md) and [docs/FINDINGS.md](docs/FINDINGS.md).

## Known side effects of the official installer

QbtAutoUpdate runs qBittorrent's own installer silently, with its default choices. Each update therefore:

- sets `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` to 1 (long path support for
  the whole PC);
- recreates qBittorrent's Windows Firewall rule, if you had removed or changed it;
- removes the Public desktop shortcut. QbtAutoUpdate puts it back if it existed (`restoreDesktopShortcut`).

Your settings, torrents and the "start with Windows" entry are not touched.

## Tip: turn off qBittorrent's own update check

qBittorrent has its own update check, which asks you to download the new version by hand. With
QbtAutoUpdate it is redundant. In qBittorrent, open **Tools › Options › Behavior** and untick **Check for
program updates**.

## Uninstalling

Use **Uninstall** in the QbtAutoUpdate window, *Installed apps* in Windows Settings, or
`QbtAutoUpdate.exe uninstall` as administrator. It removes:
- the scheduled tasks;
- `C:\Program Files\QbtAutoUpdate` and `C:\ProgramData\QbtAutoUpdate`;
- your own `%LOCALAPPDATA%\QbtAutoUpdate` folder (add `--keep-logs` to keep the logs);
- the Start menu shortcut and the notification registration.

qBittorrent itself is not touched. Other Windows accounts keep their small `%LOCALAPPDATA%\QbtAutoUpdate`
folder, which can be deleted by hand.

## Troubleshooting

- Run `QbtAutoUpdate.exe status` in a terminal: it shows the detected installation, the latest release and
  the result of the last attempt.
- Logs:
  - `%LOCALAPPDATA%\QbtAutoUpdate\logs`: checks, downloads, closing and restarting, for your account;
  - `C:\ProgramData\QbtAutoUpdate\logs`: setup, settings changes and the SYSTEM install.
- `C:\ProgramData\QbtAutoUpdate\state.json` records the last install and the last attempt.
- Settings backups are in `%LOCALAPPDATA%\QbtAutoUpdate\backups`.

## FAQ

### Why not winget, Winget-AutoUpdate or UniGetUI?

They are good general-purpose updaters. QbtAutoUpdate does one thing: qBittorrent, with its particular
needs. As checked in October 2026:

- **winget**:
  - The `qBittorrent.qBittorrent` package installs the default build. Since 5.3.0 that is libtorrent 2.1,
    so a PC on the 5.2.x default build (libtorrent 1.2) changes series on upgrade.
  - The separate `qBittorrent.qBittorrent.lt2` package stops at 5.2.4, and there is no libtorrent 1.2
    package for 5.3.0.
  - Integrity comes from the SHA-256 in the community manifest, not from qBittorrent's signature.
  - winget does not close qBittorrent before running the installer.
- **Winget-AutoUpdate (WAU)**:
  - It runs winget on a schedule, so it installs the same packages as winget.
  - Closing an app is left to your own "mod" scripts, and the provided helper uses `Stop-Process -Force`,
    which kills the process.
- **UniGetUI**:
  - It also uses winget for qBittorrent.
  - It can be told which processes to close before an update. It sends a close request to the main window,
    waits one second, then kills the process if "kill processes that refuse to die" is on.
  - With "close to tray" enabled, a window close request only hides qBittorrent, so it ends up killed, or
    the update fails.

QbtAutoUpdate instead:
- closes qBittorrent the way Windows does at shutdown, and waits until it has saved its resume data;
- keeps your libtorrent series;
- verifies qBittorrent's own OpenPGP signature;
- does not depend on winget.

### Why does Windows SmartScreen warn about it?

The executable is not signed with an Authenticode certificate yet, so SmartScreen does not know it. Each
release lists the SHA-256 of both files in `SHA256SUMS.txt`, and they are built by GitHub Actions from
this repository. Code signing through the [SignPath Foundation](https://signpath.org/) (free for open
source projects) and a winget manifest are planned.

### Does it update itself?

Not yet. Download a newer release and double-click it: it upgrades the installed copy and keeps your
settings.

### Does it work for standard (non-administrator) accounts?

Yes. An administrator runs the setup once. After that, updates install for every user without a prompt,
and notifications appear for whoever is signed in.

### Can it close qBittorrent through its WebUI?

Yes, optionally. If the WebUI is enabled, `QbtAutoUpdate.exe webui --url http://localhost:8080 --api-key <key>`
makes it ask qBittorrent to quit through the API. Give `-` instead of the key to pipe it in and keep it out
of your shell history; `--username` and `--password` work too. The Restart Manager is used otherwise. Only
addresses on this PC are accepted.

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet build
dotnet test                                       # unit tests
dotnet publish src/QbtAutoUpdate -c Release -r win-x64 -o publish   # single trimmed exe
```

- [docs/TESTING.md](docs/TESTING.md): what has been tested and how.
- [sandbox/](sandbox/README.md): the end-to-end scenario in Windows Sandbox.
- Releases: pushing a `v1.2.3` tag, or running the *Release* workflow, builds both executables and
  publishes the GitHub release with the matching [CHANGELOG](CHANGELOG.md) section.

## License

[MIT](LICENSE). QbtAutoUpdate is not affiliated with the qBittorrent project.
