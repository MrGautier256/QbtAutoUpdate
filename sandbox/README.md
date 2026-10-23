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
```

Windows Sandbox runs **one instance at a time**: run the variants one after the other. The script waits
(up to 3 minutes) for a previous sandbox to finish shutting down; starting while one is still closing makes
the new one fail before the scenario starts, leaving an empty results folder.

The sandbox window opens, runs everything by itself and shuts down after about 5 to 10 minutes. The results
folder printed by the script then holds `summary.txt` (one PASS/FAIL line per check), the raw evidence
(`status-*.txt`, `state.json`, task XML, ACLs, command lines) and the logs (`logs\`).

## What the scenario does

1. Installs qBittorrent 5.2.3 silently and configures it to be awkward to close:
   - minimize and close to the tray, *start minimized* (the window is never shown), *confirm on exit*;
   - start with Windows, a Public desktop shortcut;
   - an active torrent started with a `.torrent` argument (download speed limited to 300 KiB/s).
2. `setup --quiet --mode auto`, then checks the files, tasks, ACLs, *Installed apps* entry and Start menu.
3. Waits for the first check (started by setup) to update qBittorrent through the SYSTEM task, then checks
   the result:
   - version and libtorrent series;
   - a clean shutdown in qBittorrent's own log, resume data kept;
   - restart without the `.torrent` argument, still running after the task ended;
   - desktop shortcut and Run key kept, settings backed up.
4. As a new standard user:
   - `work\` and `config.json` are not writable;
   - files can be dropped in `inbox\`, and the Apply task can be started;
   - a tampered installer dropped there is refused.
5. `uninstall --quiet`, then checks that nothing is left and that qBittorrent is untouched.

Windows Sandbox runs with UAC disabled (`EnableLUA=0`) under an administrator account, so the scenario
cannot show the absence of UAC prompts. The standard-account checks run through a second account instead.
