# Decisions

Choices made while building QbtAutoUpdate, with the reason for each. Facts they rely on are in
[FINDINGS.md](FINDINGS.md); the original brief is [SPEC.md](SPEC.md).

## Platform and dependencies

**D-01. One .NET 10 WinExe, single-file and self-contained, for win-x64 and win-arm64.**
As specified. `TargetFramework` is `net10.0-windows10.0.19041.0` (WinRT projections for the connection cost
and, later, notifications) with `SupportedOSPlatformVersion` 10.0.17763 (Windows 10 1809, the minimum of
qBittorrent's Qt6 builds). The manifest is `asInvoker`, Windows 10 `supportedOS`, PerMonitorV2, long paths,
and Common Controls v6 (needed by TaskDialog, see D-14).

**D-02. `BouncyCastle.Cryptography` for OpenPGP.**
.NET has no OpenPGP implementation, and shelling out to `gpg` is not an option (not installed on most PCs).
BouncyCastle is the reference managed implementation, MIT-licensed, maintained (2.7.0, July 2026), and has no
other dependency. It is used for one thing only: verifying a detached signature against one embedded key.

**D-03. No command-line parsing library.**
`System.CommandLine` is still a preview package. The grammar is small (seven verbs, a handful of options), and
`apply` must reject *any* argument. A short hand-written parser makes that rule explicit and testable.

**D-04. xUnit v3 on Microsoft Testing Platform.**
xUnit v3 4.x runs only on Microsoft Testing Platform, and the .NET 10 SDK requires opting in through
`global.json` (`"test": { "runner": "Microsoft.Testing.Platform" }`). VSTest packages are not needed.
Package versions are managed centrally in `Directory.Packages.props`.

**D-05. Trimming: viable, decision deferred to phase 5.**
Measured at the end of phase 1: untrimmed single-file publish is about 46 MB per architecture. A trimmed
publish is 13 MB, and `status` plus `update --dry-run` (PGP, WinRT connection cost, HTTP) worked. The only
warnings were two IL2104 warnings from `WinRT.Runtime` and `Microsoft.Windows.SDK.NET`. Trimming is not
switched on yet: the Task Scheduler COM interop (phase 3) and notifications (phase 4) must be tested trimmed
first. Meanwhile the code avoids reflection: JSON is read with `JsonDocument`, written with
`Utf8JsonWriter`, and P/Invoke uses `[LibraryImport]`.

## Release discovery and download

**D-06. A variant is a libtorrent series, and file names come from a table.**
`Variant.Lt12` means libtorrent 1.x and `Variant.Lt2` means libtorrent 2.x, matching config values `lt12` and
`lt2`. `ReleaseResolver.Rules` maps (version range, infix, architecture) to a variant. The table was built
from the real SourceForge listings of all 25 releases from 4.6.0 to 5.3.0; arm64 rows come from
`config.nsh`. Names that match no rule are ignored and logged. If a new release changes its naming, the
result is "variant missing", which means a notification and no install, never a guess.

**D-07. RSS feed first, folder page second; secondary integrity checks.**
The folder RSS gives names, sizes and upload dates, and `minReleaseAgeHours` needs the date. The folder HTML
page embeds the same list as JSON, with SHA-256 sums but no dates, and serves as fallback. Downloads are
checked against the listed size (RSS) or SHA-256 (folder page) when known. The MD5 in the RSS is ignored.
None of this replaces PGP.

**D-08. Redirects are followed by hand.**
`AllowAutoRedirect` is off. `SafeHttpClient` follows up to 8 redirects and checks every hop, not just the last,
against the allow-list: HTTPS only, default port, no user info, DNS names only, and mirrors limited to one
label before `.dl.sourceforge.net`. The client uses the user's system proxy. Its user agent is
`QbtAutoUpdate/<version> (+repo URL)`: Cloudflare blocks a bare `Mozilla/5.0`.

**D-09. Mirror retries are plain retries.**
`use_mirror=` is ignored by SourceForge (checked), and every request to `downloads.sourceforge.net` already
picks a random mirror. Three attempts with short delays (0 s, 3 s, 10 s) are enough. Each response is
rejected if it is HTML (content type or missing `MZ` header), has the wrong size, or exceeds 512 MB. A
moved `.partial` file is retried in case an antivirus scanner holds it for a moment.

**D-10. PGP policy: one pinned primary key, fail closed.**
The official key is embedded as a resource and its full fingerprint is pinned in code. Accepted: exactly
one v4 signature over a binary document (class `0x00`), SHA-256/384/512, issued by the pinned primary key,
with the key neither revoked nor expired, created after the key and before its expiry. Everything else is
refused with a specific verdict. `UnknownKey` produces the message "qBittorrent may have changed its signing
key: update QbtAutoUpdate".

## Installation detection

**D-11. Variant detection order: binary, then WebUI, then state.json, then ask.**
This deliberately differs from the spec, which puts `state.json` first. The libtorrent strings compiled into
`qbittorrent.exe` (FINDINGS §6.2) describe what is installed *now*; `state.json` goes stale if the user
reinstalls another variant by hand. When the two disagree, the binary wins and a warning is logged. An
explicit `variant` in `config.json` overrides both, because it is the user's explicit choice. The WebUI
`buildInfo` source exists since phase 2 (D-22); the setup question arrives in phase 3. Until then an
unknown variant means "do nothing".

**D-12. Fork detection without `CompanyName`.**
The Enhanced Edition reuses the official folder, registry keys and version resource strings, so it is
detected through a 4th version component ≥ 10, or through its own settings keys in the binary
(`AutoBanUnknownPeer`, `ShadowBan`, ASCII or UTF-16). `CompanyName`/`ProductName` are still checked, without
regard to case (the exe says "Project", the installer says "project"), to catch other products.

**D-13. Unusual layouts.**
- Registered install with a `profile` folder next to the exe: supported, and noted. The uninstaller removes
  only known files and a non-recursive `RMDir`, so the folder survives. Its backup is handled in phase 2.
- Registry keys but no exe: supported as an inconsistent install, so the next update repairs it (same version
  allowed).
- No registry keys but a running qbittorrent.exe: portable, not supported in v1.
- Program Files (x86), or an i386 binary: legacy 32-bit, not supported in v1.

## Behaviour and user experience

**D-14. The exe is its own installer and settings panel (maintainer request).**
Double-clicking an exe that is not installed opens a small setup dialog: mode and variant, then one UAC
prompt. Double-clicking the installed exe opens a settings dialog: automatic updates on/off, mode,
"Check now", "Uninstall". Both are native TaskDialogs (comctl32 v6 via P/Invoke) rather than WinForms or WPF,
to keep the exe small and trimmable. Setup also registers QbtAutoUpdate in *Installed apps*, so it can be
removed like any program. Changing a machine-wide setting needs administrator rights, so it costs one UAC
prompt; updates themselves never do. Lands in phase 3. Until then a double-click prints the usage.

**D-15. `enabled` setting in config.json.**
This setting is not in the spec's list but is needed for D-14's on/off switch. When off, scheduled checks stop
at once; explicit actions (the `update` command, "Update now", "Check now") still work.

**D-16. Defaults when config.json is missing: notify mode.**
An unconfigured machine should never install by surprise. Setup writes the user's choice; the defaults only
matter before setup or if the file is damaged. Values are validated one by one: a bad value falls back to its
own default with a logged warning, and the rest of the file is kept.

**D-17. Manual runs bypass the automatic-run gates.**
The off switch, metered connection, skipped version, snooze and minimum release age only apply to scheduled
runs. A user who types `update` or clicks "Update now" gets the update. Security checks (signature, version
never lower, variant) apply to every run.

**D-18. Exit codes.**
0 means OK, including normal waiting states (up to date, snoozed, not yet on SourceForge). Other codes: 1
error, 2 usage, 3 unsupported installation or unknown variant, 4 release sources unreachable, 5 signature or
final installer check refused, 6 qBittorrent busy (could not be closed, runs in another session or as
administrator), 7 install failed, 9 feature not available yet (temporary, during development).

**D-19. Logging.**
Size-based rotation at 1 MB, ten files (`QbtAutoUpdate.log`, `.1.log` … `.9.log`). Each write opens the file
under a named mutex, because two processes of the same user can run together (scheduled check plus a
notification click). URLs are logged as host and path only: SourceForge mirror URLs carry signed query
strings. Secrets are never passed to the logger.

**D-20. Integration tests are opt-in.**
They download about 90 MB from SourceForge and need 7-Zip to unpack an NSIS installer, which is not suitable
for CI. They run only when `QBTAU_INTEGRATION=1`, otherwise they are reported as skipped. They cover the
spec's requirement: both real 5.3.0 installers verify against the real key, and the offline variant
heuristic reads the right series from each unpacked `qbittorrent.exe`.

## Closing, installing and restarting (phase 2)

**D-21. Closing order: WebUI first when configured, Restart Manager otherwise.**
When the user configured the WebUI, its shutdown API is tried first: it runs qBittorrent's normal exit path
and shows no UI. If it fails (unreachable, refused), Restart Manager takes over (FINDINGS §7.3), never forced.
In both cases success means the snapshotted process has really exited within `closeTimeoutSeconds`; the
`RmShutdown` return value is only logged. Once the WebUI has accepted a shutdown, Restart Manager is not
sent as well: qBittorrent is already saving, and a late `WM_CLOSE` could raise its exit confirmation.

**D-22. WebUI access: loopback only, API key preferred, secrets under DPAPI.**
- The URL must point to this computer (`localhost`, `127.0.0.0/8`, `::1`): the tool only ever shuts down the
  local instance, and credentials must not cross a network. Self-signed HTTPS certificates are accepted for
  those addresses only, because that is how qBittorrent's HTTPS WebUI is usually set up.
- The API key (5.2.0+) is sent as `Authorization: Bearer` and needs no login; username and password log in
  through `/api/v2/auth/login`. All requests carry same-origin `Origin` and `Referer` headers.
- Secrets are encrypted with DPAPI, CurrentUser scope, plus an application entropy, before being written to
  `user.json`. `System.Security.Cryptography.ProtectedData` is Microsoft's own package for that API; it adds
  no third-party code.
- The spec lists no way to configure the WebUI, so there is a `webui` command (`--url`, `--api-key`,
  `--username`, `--password`, `--test`, `--clear`). A secret given as `-` is read from redirected standard
  input, to keep it out of shell history.
- The WebUI's `buildInfo` is also the third variant source (D-11).

**D-23. Development mode = `update` run elevated by hand.**
Until the SYSTEM task exists (phase 3), an elevated `update` runs the installer itself through
`InstallerRunner`. That class performs the same final checks `apply` will perform: name table, PGP, version
resource, no downgrade, variant, architecture, no qBittorrent running in any session, then `/S /D=<folder>`,
exit code and post-install check. A non-elevated `update` stops *before* closing anything (exit code 9).
In development mode the installer runs from the user's staging folder: acceptable, because the person who
elevated is trusted. The protected `work\` copy belongs to `apply`.

**D-24. Restart: same instance, never elevated, even after a failed install.**
- The command line is rebuilt from a whitelist of instance options (`QbtLaunch`, FINDINGS §7.5).
- From a normal process, qBittorrent is started through ShellExecute, like Explorer does. `UseShellExecute=false`
  would make it inherit QbtAutoUpdate's handles: a terminal reading a redirected output would then wait until
  qBittorrent exits.
- From an elevated process (development mode, rollback), it is started with the desktop shell's token
  (`CreateProcessWithTokenW`) so that it is not elevated. If that is impossible, it is not started and the
  user is told to start it.
- Working directory: the install folder. `QBT_*` environment variables of the original launch are not
  replayed, and a relative `--profile` (resolved by qBittorrent against an unknown directory) is replayed
  as is but cannot be backed up.
- Instances that were closed are started again whatever the outcome of the install.

**D-25. Settings backup.**
Copies the top-level `*.ini` and `*.json` files of each closed instance's config folder: the extension filter
naturally skips `lockfile` and the temporary `qBittorrent_new.ini.XXXXXX` files. When no instance was
running, the default profile is used. `backupTorrents` zips `BT_backup` with the fastest compression. The 3
newest backups are kept, ordered by creation time. A failed backup is logged but does not stop the update:
the installer never touches the profile.

**D-26. Several instances, other sessions, elevation.**
- Every instance in the user's session is closed, one by one, through Restart Manager; the WebUI is used
  only when exactly one instance runs, since its URL identifies a single instance. The first failure stops
  the sequence, and the instances already closed are started again.
- A qBittorrent in another Windows session, or one running as administrator while QbtAutoUpdate is not,
  blocks the update *before* anything is touched (exit code 6). The SYSTEM side would refuse to install
  anyway.

**D-27. `allowForceClose`.**
Off by default. When on, and only after the graceful budget is spent, the snapshotted process is terminated.
The kill goes through the handle opened at snapshot time, so a reused PID is never hit. It is logged as a
warning. The README will explain the risk (lost resume data, rechecks).

**D-28. `restart` command.**
`restart` closes qBittorrent exactly like an update would, then starts it again, without installing
anything. Users can check that the clean shutdown works with their own settings (tray, confirmation, many
torrents), and the Sandbox scenario uses it to validate FINDINGS §7.2 separately from installation.

## Scheduled tasks, SYSTEM side, setup (phase 3)

**D-29. No native self-extraction.**
`IncludeNativeLibrariesForSelfExtract` is off. It would extract native DLLs into `%TEMP%`, which for the
SYSTEM task is a folder where users can write: a DLL planting risk. Checked: the self-contained single-file
publish has no native library left outside the exe. If a future dependency brings one, it shows up as an
extra file next to the exe, and the release workflow must fail on that.

**D-30. Source-generated COM instead of the TaskScheduler package.**
Task Scheduler 2.0 and `IShellLinkW` are called through `[GeneratedComInterface]` (ComWrappers), which works
trimmed, unlike `[ComImport]` and the dahall `TaskScheduler` package that relies on it. Only the methods
actually called have real signatures; the other vtable slots are placeholders. The order comes from the
Windows SDK headers (`taskschd.h`, `shobjidl_core.h`), and live tests read a Task Scheduler folder and
round-trip a `.lnk`. Passing `VARIANT`s (`ComVariant`) by value requires
`[assembly: DisableRuntimeMarshalling]`, which is harmless since every P/Invoke is source-generated. Tasks are
registered from XML. Accounts are given as SIDs (`S-1-5-32-545`, `S-1-5-18`), never by localized names.

**D-31. Inbox protocol.**
The user side writes `<32-hex request id>__<installer name>` plus `.asc`. Users may only *create* files in
the inbox (no listing, no folders); `CREATOR OWNER` keeps control of their own files, so users cannot read or
replace each other's requests. "apply" picks the newest valid pair (regular files, known name, size limits),
deletes everything else unread, copies the pair into `work\` (SYSTEM and Administrators only), deletes the
inbox files, and verifies and runs only the copy. The request id goes back in `state.json`, so the requester
recognizes its own answer; a request that another, newer one superseded gets "no answer" and its files are
taken back.

**D-32. The SYSTEM side trusts nothing it did not create.**
- "apply" refuses to run if, as SYSTEM, it is not `%ProgramFiles%\QbtAutoUpdate\QbtAutoUpdate.exe`.
- It also refuses if a non-administrator group can write in that folder.
- It refuses when the data folders are junctions or not owned by SYSTEM or Administrators.
- Setup removes a pre-existing `%ProgramData%\QbtAutoUpdate` it does not trust, and empties `work\` and
  `inbox\`.
- A non-elevated process never creates that folder: logs of non-elevated runs go to the user's own folder.

**D-33. `state.json`: last install and last attempt.**
`lastInstall` (successful installs only) remembers the variant. `lastAttempt` records every apply: result
(`installed`, `refused`, `busy`, `failed`, `not-verified`), message, request id, version, variant, folder and
exit code. `installing` is written just before the installer runs. Found later with no qBittorrent left
(the old uninstaller had already run when the PC stopped), it lets the next check *and* apply rebuild the
installation in the recorded folder and variant (`InstallInfoRecovery`, spec §6).

**D-34. Which channel installs.**
When the Apply task exists, every update goes through it, elevated or not, for one code path. Without
it, an elevated run installs directly (development mode, D-23), and a non-elevated run stops before closing
anything.

**D-35. Setup details.**
- Re-running setup upgrades or repairs in place. A running older exe is renamed (`.old-<ticks>`, deleted by
  the next setup), because a running image can be renamed but not deleted.
- Without `--variant`, and when the installed variant cannot be read, `--quiet` stores libtorrent 2.x (the
  spec's default) explicitly; the window asks instead.
- Setup adds QbtAutoUpdate to *Installed apps* ("Modify" opens the settings window) and to the common Start
  menu, then starts the Check task once.
- The first scheduled run is one interval after setup, with a random delay of up to 10 minutes.
- `config` changes settings later and re-registers the Check task (interval, on/off).

**D-36. Uninstall removes the running exe too.**
It deletes the tasks, the *Installed apps* entry, the Start menu shortcut, the machine data, this user's data
(logs kept with `--keep-logs`), then the binaries. The exe that is running cannot be deleted: it is moved
to the temporary folder (same volume) and scheduled for deletion at the next restart
(`MOVEFILE_DELAY_UNTIL_REBOOT`), so Program Files is clean at once. Other users' `%LOCALAPPDATA%` data cannot
be reached and is documented.

**D-37. Windows Sandbox scenario in PowerShell.**
The spec bans PowerShell from the *product*. The test tooling in `sandbox\` is PowerShell (Windows
PowerShell 5.1 compatible, pure ASCII): `Start-Scenario.ps1` publishes the exe, fetches the official
installers, writes the `.wsb` with absolute paths (none in the repository) and starts the sandbox.
`Run-Scenario.ps1` runs inside, writes PASS/FAIL lines and evidence to a mapped folder, and shuts the sandbox
down.

## Notifications, localization, rollback (phase 4)

**D-38. Notifications through WinRT and a protocol, without the Community Toolkit.**
`Microsoft.Toolkit.Uwp.Notifications` has had no release since 2022, and its COM activator is not
trim-safe. QbtAutoUpdate calls `Windows.UI.Notifications` from the Windows SDK projection instead and writes
the toast XML itself (`ToastContent`, unit-tested).
- **Registration.** An unpackaged app needs an AppUserModelID with a display name and an icon. Setup
  registers it machine-wide and writes `QbtAutoUpdate.png` next to the exe. Each run that shows a toast also
  registers it under HKCU, in case setup ran for another account. Uninstall removes both.
- **Buttons.** The buttons use protocol activation, `qbtautoupdate:<action>?v=<version>&t=<token>`.
  Setup registers the protocol under HKLM to start the installed exe. A click therefore works after the
  check that showed the toast has exited, with no COM server and no resident process.
- **Token.** Any web page or document can open a `qbtautoupdate:` link, so each action carries a random
  per-user token. The token is 128 bits, kept in `user.json` and compared in constant time; links without it
  are ignored and logged.
- **Effect of a forged link.** Even a forged link with a valid token could only do what the user can
  already do: snooze, skip, open the logs, or start a signed, non-downgrading update.
- **When toasts are shown.** Only runs nobody watches show toasts: the scheduled check, and the run started by
  a click. A click always reports through toasts, even when the program that opened the link had a
  console. Terminal and window runs report in place. The `notifications` setting (`all`, `errors`, `none`)
  filters them, but in notify mode `errors` still shows the "available" toast, since that toast is the
  point of the mode.
- **Toast replacement.** Toasts carry a tag (`available`, `result`), so a newer one replaces the older one in
  the notification center.

**D-39. Localization: English and French resources, CLI diagnostics in English.**
- **Resources.** The strings live in `Resources/Strings.resx` (neutral English) and `Strings.fr.resx`. The
  French satellite assembly is bundled inside the single-file exe. Other languages fall back to English.
- **What is translated.** Windows, toasts and the messages they show are translated, which includes plan,
  close and NSIS exit-code messages, shared by the windows and the terminal.
- **What stays English.** The usage text, argument errors, `status` and log files stay English. They are
  meant for scripts, bug reports and searches. The same goes for technical details inside a translated
  message, such as a signature verdict or an installer refusal reason.
- **Tests.** Tests pin the invariant culture with a module initializer. A test checks that every key exists
  in French with the same placeholders, and that every key the code uses exists.
- **Console output.** Output is encoded in the console's code page, even when redirected (FINDINGS §8b).
  Console text therefore avoids characters missing from OEM code pages: `->`, not an arrow, and `...`, not
  an ellipsis.

**D-40. Rollback: the previous stable release, as administrator, then held back.**
- **Which version.** `rollback` reads the release folders from SourceForge's root RSS and takes the highest
  stable version below the installed one. It keeps the libtorrent series of the installation: from 5.3.0
  lt12 it installs the 5.2.4 *default* build, through the same name table (D-06).
- **How it installs.** It installs in-process, elevated, with the same checks as an update: signature,
  version resource, variant, architecture and clean close. Only the "no older version" check is lifted, and
  only on this path. The Apply task, which has no arguments, still cannot downgrade anything.
- **Held-back version.** Afterwards the rolled-back version goes into `config.json` as `skippedVersion`, for
  every user. Automatic runs skip it until a newer release appears, or until an administrator runs
  `config --skipped-version none`. A manual check can still install it. The settings window offers rollback
  behind a confirmation.

**D-41. Notify mode does not repeat itself.**
The scheduled check announces a version once, then stays silent about it for 24 hours (`notifiedVersion` and
`notifiedAt` in `user.json`). "Later" snoozes all checks for 24 hours. "Skip this version" is per user, while
the rollback hold (D-40) is machine-wide. A newer version is announced at once.

**D-42. Icon.**
`tools/New-Icon.ps1` draws the icon (a circular arrow around a download arrow on a blue tile) with
System.Drawing and writes `QbtAutoUpdate.ico` (16 to 256 px PNG frames) and the 256 px PNG used by toasts. The
generated files are committed; the script is there to change them. Toasts use the PNG.
