# Testing

What has been tested, how, and what actually happened, including what does not work yet.
It covers the automated suite, the manual runs, and the Windows Sandbox end-to-end scenario
(spec §8, `sandbox\`).

## Automated tests

```
dotnet test                              # unit tests (integration tests are reported as skipped)
QBTAU_INTEGRATION=1 dotnet test          # also run the integration tests (network, ~90 MB, 7-Zip for one of them)
```

| Area | What is covered |
|------|-----------------|
| Versions | 3/4-component parsing and ordering, rejected pre-release strings |
| versions.json | both sources, one or both down, changed format, redirect to a foreign host |
| SourceForge | the real RSS feeds of 11 releases (4.6.0 → 5.3.0), the real folder page, `UT` dates, 404 folders, fallback |
| File choice | the naming table against every fixture, arm64 fallback, missing variant, missing signature, staged files, unknown names |
| Downloads | redirect chain, foreign or plain-HTTP mirror, HTML page instead of a binary, size and SHA-256 checks, retries |
| PGP | test keys generated at run time: valid, altered file, other key, SHA-1, text signature, expired key, revoked key, several signatures, garbage; real 5.3.0 signatures bound to the pinned key |
| Detection | registry views, file vs registry version, missing exe, fork by version and by strings, other products, 32-bit, portable |
| Variant scan | markers of all real builds, look-alikes found in real binaries, chunk boundaries, contradictions |
| Planning | every gate (off switch, metered, skip, snooze, minimum age, version held back by rollback), variant sources and their order, repair of an inconsistent install, no downgrade |
| Settings | defaults, per-field fallback, corrupt files, round trips, DPAPI-encrypted WebUI secrets |
| Command line (QbtAutoUpdate) | verbs, options, `apply` accepting no argument |
| Command line (qBittorrent) | the real "double-clicked .torrent" case, the Run key form, instance vs torrent options, unquoting, profile location (default, named, custom, portable, relative) |
| Quoting | round trip through `CommandLineToArgvW`; reading a live process's command line |
| Closing | Restart Manager path, WebUI path and fallback, never killing by default, `allowForceClose`, elevated / other user / unreadable processes |
| Backup | file selection on a copy of a real profile layout, BT_backup zip, several profiles, keeping 3, same-second collisions |
| Installing | final checks (name, signature, version resource, downgrade, same version, variant, architecture), busy, NSIS exit codes, timeout, post-install version and variant checks; rollback lifts only the downgrade check |
| Tasks, inbox, ACLs | task XML (principals, triggers, SDDL), inbox names and hostile content, data folder trust checks, `config` changes, recovery of an interrupted install; live Task Scheduler read and `.lnk` round trip |
| Notifications | toast XML (texts, escaping, protocol buttons with version and token), `qbtautoupdate:` link parsing, rejection of unknown actions and missing or wrong tokens |
| Localization | every English string translated to French with the same placeholders, every key used by the code present, French on a French Windows and English elsewhere |
| Rollback | previous stable release from the real SourceForge root feed, libtorrent series kept across the 5.3.0 naming change, `config --skipped-version` |

Integration tests (opt-in): latest release resolvable for both variants; both real 5.3.0 installers downloaded, verified with the
real key, unpacked with 7-Zip, and their libtorrent series read correctly; detection of the locally installed qBittorrent.

## Results

### 2026-10-22: phase 1, development machine (Windows 11 Pro 26200, x64)

| Run | Result |
|-----|--------|
| `dotnet test` | 230 passed, 4 integration tests skipped |
| `QBTAU_INTEGRATION=1 dotnet test` | 234 passed |
| `status` | qBittorrent 5.2.4, registry 32-bit view, libtorrent 1.2.20 → target `qbittorrent_5.3.0_lt12_x64_setup.exe` |
| `update --dry-run` | downloaded from two different mirrors, signature valid, nothing touched, staging emptied (≈3 s) |
| `dotnet publish -r win-x64` / `win-arm64` | single file, 46 MB untrimmed; 13 MB trimmed (2 IL2104 warnings from WinRT), trimmed build works |

### 2026-10-22: phase 2, development machine

| Run | Result |
|-----|--------|
| `dotnet test` | 317 passed, 4 integration tests skipped |
| `status` | running qBittorrent reported as "can be closed cleanly"; its command line `qbittorrent.exe "…\x.torrent"` is reduced to no arguments ("1 argument about torrents to add left out") |
| `restart --dry-run`, `update --dry-run` | describe Restart Manager close, settings backup from `%APPDATA%\qBittorrent`, install; nothing touched |
| `restart` (with the owner's permission), qBittorrent 5.2.4, *close to tray* on, *confirm on exit* off, no torrent loaded | Restart Manager reported success; process gone in < 1 s; qBittorrent's own log shows the full clean path: "shutdown initiated", "resume data saved", "BitTorrent session terminated", "ready to exit". Restarted unelevated with no argument (the `.torrent` it had been opened with was left out). |
| `restart` again, main window **hidden in the tray** (top-level `Qt6103QWindowIcon` window present but invisible) | Same clean path in 1 s, restarted. Confirms FINDINGS §7.2: the hidden main window still receives `WM_QUERYENDSESSION`. |

| `restart` while qBittorrent's own **"new version available" modal dialog** was open (shown at start-up by 5.2.4's built-in check) | Same clean path in < 1 s, no crash (only the Restart Manager's informational event 10002 in the Application log). The restarted 5.2.4 shows the modal again. |

Observed on the way: each start of 5.2.4 logs a 404 for the dead FossHub feed, yet still announces 5.3.0
through `versions.json` with a modal dialog (FINDINGS §2).

### 2026-10-23: phase 3, Windows Sandbox (Windows 11 26100, French display language)

Scenario: `sandbox\Start-Scenario.ps1` (see `sandbox\README.md`).

**Run 1: 17 failures.** `setup` crashed when registering the tasks: source-generated COM raised
`FileNotFoundException` for the not-yet-existing `\QbtAutoUpdate` folder, and only `COMException` was caught
(FINDINGS §8b). Everything after it failed as a consequence. Fixed, with a regression test.

**Run 2: 6 failures.** The whole update worked. The failures were scenario bugs:
- the qBittorrent log (UTF-8 without BOM) was read as ANSI by Windows PowerShell 5.1;
- `net user` prompted because of a password longer than 14 characters.

**Run 3: default build, 5.2.3 → 5.3.0: 34/34 PASS.** Configuration:
- qBittorrent 5.2.3 default build (libtorrent 1.2), started minimized to the tray (window never shown);
- close to tray, confirm on exit, start with Windows, Public desktop shortcut;
- an active torrent added with a `.torrent` argument.

Results:
- `setup --quiet --mode auto`: exe, tasks, ACLs, *Installed apps* entry and Start menu shortcut in place.
- The first check started by setup did the whole update in 14 s:
  - download from two mirrors (2 s) and signature OK;
  - Restart Manager close (2 s);
  - settings backup;
  - SYSTEM Apply task install (8 s);
  - restart.
- qBittorrent's own log: shutdown initiated, resume data saved, session terminated, ready to exit, then
  5.3.0 started and logged "torrent restored". No confirmation dialog appeared despite *confirm on exit*
  with an active torrent.
- **5.3.0 lt12** installed: the libtorrent 1.2 series was kept, not switched to the new 2.1 default.
- qBittorrent restarted without the `.torrent` argument and was still running 50 s after the task had ended.
- Public desktop shortcut and HKCU `Run` value intact; settings backed up.
- As a new standard account:
  - `work\` and `config.json` could not be written;
  - files could be dropped in `inbox\`, and the Apply task could be started (`schtasks /run`);
  - a tampered installer was refused ("BadSignature") and the inbox emptied.
- `uninstall --quiet`: no task, folder, registry entry or shortcut left; qBittorrent untouched.

**Run 4: lt20 build, 5.2.3 → 5.3.0: 34/34 PASS.** Same scenario starting from the 5.2.3 `lt20`
build (libtorrent 2.0). The update installed the 5.3.0 **default** build (libtorrent 2.1), which keeps the
2.x series. Every other check matches run 3.

(One attempt in between left an empty results folder: it was started while the previous sandbox was still
shutting down, and Windows Sandbox runs one instance at a time. The launcher now waits.)

**Runs 5–7: the windows ("ui" variant).** The same machine is driven through UI Automation instead of
`setup --quiet`. Buttons are pressed by their TaskDialog ids (`CommandButton_<id>`, `CommandLink_<id>`,
`RadioButton_<id>`), independent of the display language.
- Run 5 found the ids differ from Win32 control ids.
- Run 6 found the command link prefix.
- **Run 7: all checks PASS:**
  - Started like a double-click, the exe opened the setup window. It shows the detected qBittorrent 5.2.3
    (libtorrent 1.2) and the two modes.
  - "Ask me first" then "Install" gave the "QbtAutoUpdate is ready" window. `config.json` holds `notify`,
    and the first check installed nothing.
  - The installed exe opened the settings window. Its status was correct: notify mode, every 6 h, last check
    time.
  - "Check for updates now" offered "qBittorrent 5.3.0 is available". "Update now" showed the progress
    window, then the result listing each step, and qBittorrent 5.3.0 lt12 was installed.
  - All checks of run 3 (clean shutdown, restart, shortcut, Run key, standard account, tampered installer,
    uninstall) pass again.

ACLs as applied (from `icacls`):
- `%ProgramData%\QbtAutoUpdate`: protected; SYSTEM and Administrators full, Users read and execute.
- `inbox`: Users `(S,WD,X,RA)` (create files only), CREATOR OWNER `(OI)(IO)(F)`.
- `work`: SYSTEM and Administrators only.
- `Program Files\QbtAutoUpdate`: inherits Program Files.

### 2026-10-23: phase 4, Windows Sandbox (French display language)

The scenario gained these steps after the update (see `sandbox\README.md`):
- rollback;
- a scheduled check with the rolled-back version held back;
- notify mode, with its notification and throttling;
- the notification buttons opened as `qbtautoupdate:` links.

The dialog texts are matched in English or French, since the windows are now translated.
Automated suite: 397 passed, 4 integration tests skipped.

**Run 8: default build: 55/56 PASS.**
- **Rollback.** `rollback` took 5.3.0 lt12 back to the 5.2.4 *default* build. That build has libtorrent
  1.2, so the series was kept across the 5.3.0 naming change. qBittorrent was closed cleanly and restarted,
  and `config.json` held 5.3.0 back. The next scheduled check left 5.2.4 alone.
- **Notify mode.** After `config --mode notify --skipped-version none`, a scheduled check installed nothing.
  It showed one notification, in French, with three buttons whose links carry the version and the user's
  token. A second check did not repeat it.
- **Buttons.**
  - "Later" set a 24 h snooze and "Skip this version" was saved.
  - Two links with a wrong token were ignored and logged.
  - "Update now" installed 5.3.0 again and restarted qBittorrent.
- **The failure:** the success notification after "Update now" was missing. The scenario opened the links
  with `Start-Process` from its PowerShell console. The started exe attached to that console and took itself
  for a terminal run, which reports in place. Fixes:
  - a notification click (`activate`) now always reports through notifications;
  - the scenario opens links through Explorer, which has no console, like a real click.
- **Console output.** Accents in the captured output were garbled (`T�l�chargement`). This led to the
  console code page fix (FINDINGS §8b).

**Run 9: lt20 build: 56/56 PASS.**
- Same steps from 5.2.3 lt20. The update installed 5.3.0 default (libtorrent 2.1), and the rollback
  installed 5.2.4 **lt20**, so the 2.x series was kept both ways.
- The success notification after "Update now" was shown and has no button.
- Captured console output now has correct accents.

**Run 10: the windows ("ui" variant): 66/66 PASS, all in French.**
- The setup window ("Garder qBittorrent à jour") offered notify mode. The first check, started by setup,
  installed nothing and showed the "available" notification.
- The settings window ("Rechercher des mises à jour maintenant") offered 5.3.0 and installed it.
- "Réinstaller la version précédente" asked for confirmation, then reinstalled 5.2.4 (libtorrent 1.2). It
  listed each step and said that 5.3.0 is now held back.
- The notify-mode and button steps of run 9 passed again. The scenario aged `notifiedAt` by two days to
  get past the 24 h reminder delay.
- Uninstall removed the notification registration and the protocol as well.

### Not validated yet

- **A long shutdown** (many torrents, tracker announces) and resume data checked torrent by torrent.
- **The WebUI** shutdown path against a real qBittorrent.
- **UAC prompts and flashing windows**: Windows Sandbox runs with UAC off, so "no prompt after setup" and
  "no visible window" need a regular PC or VM with UAC on (phase 5). The same goes for the non-elevated
  path where a settings change relaunches the exe with "runas".
- **A standard account running the whole update** in its own session (signed in interactively). Its parts
  are covered: inbox write, Apply task start, ACLs.
- **ARM64**: the arm64 build is published but not run yet.
- **A real install in development mode** (`update` from an elevated terminal, without the Apply task). Its
  direct, in-process install path is the one `rollback` uses, which runs 8 to 10 validated.
- **A real mouse click on a notification button.** The scenario opens the same `qbtautoupdate:` links
  through Explorer, as Windows does, but does not click the toast itself.
- **Several users signed in at once**, each with their own notifications and token.
