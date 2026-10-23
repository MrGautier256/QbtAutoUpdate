# Windows Sandbox scenario

End-to-end test of QbtAutoUpdate on a clean, disposable Windows, as required by the spec (§8). It is test
tooling, not part of the product.

## Requirements

- Windows 10/11 Pro or Enterprise with *Windows Sandbox* enabled (Windows Features › Windows Sandbox).
- The .NET 10 SDK, to publish the exe.
- Internet access (qBittorrent installers, the update itself, a small Creative Commons test torrent).

## Run

```powershell
.\sandbox\Start-Scenario.ps1 -Variant default   # qBittorrent 5.2.3 default build (libtorrent 1.2)
.\sandbox\Start-Scenario.ps1 -Variant lt20      # qBittorrent 5.2.3 "lt20" build (libtorrent 2.0)
.\sandbox\Start-Scenario.ps1 -Variant ui        # default build, driven through the windows (UI Automation)
```

Windows Sandbox runs **one instance at a time**: run the variants one after the other. The script waits
(up to 3 minutes) for a previous sandbox to finish shutting down; starting while one is still closing makes
the new one fail before the scenario starts, leaving an empty results folder.

The sandbox window opens, runs everything by itself and shuts down after about 15 to 25 minutes. The results
folder printed by the script then holds `summary.txt` (one PASS/FAIL line per check), the raw evidence
(`status-*.txt`, `state.json`, task XML, ACLs, command lines) and the logs (`logs\`).

## What the scenario does

1. Installs qBittorrent 5.2.3 silently and configures it to be awkward to close:
   - minimize and close to the tray, *start minimized* (the window is never shown), *confirm on exit*;
   - start with Windows, a Public desktop shortcut;
   - an active torrent started with a `.torrent` argument (download speed limited to 300 KiB/s).
2. `setup --quiet --mode auto`, then checks the files, tasks, ACLs, *Installed apps* entry, Start menu,
   notification registration and `qbtautoupdate:` protocol. The `ui` variant opens the exe like a
   double-click instead and chooses notify mode in the setup window; the first check must then install
   nothing and show a notification.
3. Waits for the first check (started by setup) to update qBittorrent through the SYSTEM task, then checks
   the result:
   - version and libtorrent series;
   - a clean shutdown in qBittorrent's own log, resume data kept;
   - restart without the `.torrent` argument, still running after the task ended;
   - desktop shortcut and Run key kept, settings backed up.

   The `ui` variant updates from the settings window ("Check for updates now", then "Update now").
4. Rollback (`rollback`, or the settings window in the `ui` variant), then:
   - the previous release is installed in the same libtorrent series and qBittorrent runs again;
   - `config.json` holds the rolled-back version back, and a scheduled check leaves it alone.
5. Notify mode (`config --mode notify --skipped-version none`), then:
   - a scheduled check installs nothing and shows one notification with three buttons;
   - a second check does not repeat it.

   The buttons are then opened as Windows opens them, through `qbtautoupdate:` links:
   - "Later" snoozes for a day and "Skip this version" is remembered;
   - links without the user's token are ignored;
   - "Update now" installs the version again and shows a success notification.
6. As a new standard user:
   - `work\` and `config.json` are not writable;
   - files can be dropped in `inbox\`, and the Apply task can be started;
   - a tampered installer dropped there is refused.
7. `uninstall --quiet`, then checks that nothing is left (notification registration and protocol included)
   and that qBittorrent is untouched.

Windows Sandbox runs with UAC disabled (`EnableLUA=0`) under an administrator account, so the scenario
cannot show the absence of UAC prompts. The standard-account checks run through a second account instead.
The sandbox copies the host's display language; dialog texts are matched in English or French.
