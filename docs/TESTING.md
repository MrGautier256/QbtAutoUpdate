# Testing

What has been tested, how, and what actually happened, including what does not work yet.
The Windows Sandbox end-to-end scenario (spec §8) is added in phase 5. Until then this file records the
automated suite and the manual runs.

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
| Planning | every gate (off switch, metered, skip, snooze, minimum age), variant sources and their order, repair of an inconsistent install, no downgrade |
| Settings | defaults, per-field fallback, corrupt files, round trips, DPAPI-encrypted WebUI secrets |
| Command line (QbtAutoUpdate) | verbs, options, `apply` accepting no argument |
| Command line (qBittorrent) | the real "double-clicked .torrent" case, the Run key form, instance vs torrent options, unquoting, profile location (default, named, custom, portable, relative) |
| Quoting | round trip through `CommandLineToArgvW`; reading a live process's command line |
| Closing | Restart Manager path, WebUI path and fallback, never killing by default, `allowForceClose`, elevated / other user / unreadable processes |
| Backup | file selection on a copy of a real profile layout, BT_backup zip, several profiles, keeping 3, same-second collisions |
| Installing | final checks (name, signature, version resource, downgrade, same version, variant, architecture), busy, NSIS exit codes, timeout, post-install version and variant checks |

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

### Not validated yet

- **Clean shutdown with torrents loaded**: resume data intact and no recheck after restart, and a long
  shutdown (many torrents, tracker announces). The machine above had no torrent.
- **Confirm on exit** with active torrents: per FINDINGS §7.2 the Restart Manager path bypasses the
  confirmation dialog; to be checked in Sandbox.
- **Start minimized** (window never shown since start-up), and the **WebUI** shutdown path against a real
  qBittorrent.
- **A real install in development mode** (`update` from an elevated terminal): installs qBittorrent, so only
  in Sandbox or a VM.
- Restart of qBittorrent from an elevated process through the shell token.
