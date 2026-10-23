# Findings

Facts about qBittorrent's Windows distribution and runtime behaviour that QbtAutoUpdate depends on,
checked against primary sources before writing any code.

- **Checked on:** 2026-10-22
- **qBittorrent sources:** `master` (already at 5.4.0 dev) and tag `release-5.3.0`. The NSIS scripts
  (`installer.nsh`, `uninstaller.nsh`) and `src/gui/programupdater.cpp` are byte-identical between the
  two. `config.nsh` differs only in `QBT_VERSION`.
- **Qt sources:** `qtbase` branch `6.11` (5.3.0 ships Qt 6.11.2).
- **Binaries:** the four installers `qbittorrent_5.3.0_x64_setup.exe`, `qbittorrent_5.3.0_lt12_x64_setup.exe`,
  `qbittorrent_5.2.3_x64_setup.exe` and `qbittorrent_5.2.3_lt20_x64_setup.exe` were downloaded from
  SourceForge, verified with `gpg`, and unpacked with 7-Zip to inspect `qbittorrent.exe`. Nothing was
  executed.
- **A real installation** (qBittorrent 5.2.4 on Windows 11, x64) was inspected read-only: registry,
  process command line, profile folders.

Legend: ✅ confirmed as specified · ⚠️ differs from the spec, design must change · ➕ new fact not in the spec ·
🔬 still to be confirmed empirically (Windows Sandbox).

---

## 1. Summary

| # | Topic | Status | One-line finding |
|---|-------|--------|------------------|
| 1 | `versions.json` (both URLs, format) | ✅ | Both return `{"win":{"version":"5.3.0"},"macos":{"version":"5.2.3"}}`. |
| 2 | Built-in updater logic | ✅ | Queries both URLs, keeps the max, only opens the download page. |
| 3 | **Which build is the default** | ⚠️ | The default build switched to libtorrent 2.x in **5.3.0**, not 5.2.0. All 5.2.x defaults are libtorrent **1.2**. |
| 4 | File naming per version | ⚠️ | Four naming eras since 4.6 (`lt20_qt6`, `qt6_lt20`, `lt20`, then `lt12`). See §3.3. |
| 5 | ARM64 installers | ✅ | None published (0 matches in the whole `qbittorrent-win32` feed). x64 on ARM64 allowed since 5.2.2. |
| 6 | PGP key and signatures | ✅ | Fingerprint `D8F3DA77AAC6741053599C136E4A2D025B7CC9A2`, no expiry, signs with the primary key. All 4 sampled installers verify. |
| 7 | SourceForge access | ➕ | Cloudflare returns **403** to a bare `Mozilla/5.0` user agent. A descriptive UA works. |
| 8 | Registry view | ✅ | Keys exist **only** in the 32-bit view (`WOW6432Node`) on a real install. |
| 9 | NSIS silent mode skips `check_instance` | ✅ | Confirmed by NSIS docs §4.12. |
| 10 | Exit codes | ✅ | 0, 1654, 1223, 1314, 1603, 1062, 15618 confirmed in source. |
| 11 | Version resources | ➕ | Installer `FileVersion` is `5.3.0`, but `qbittorrent.exe`'s is **`v5.3.0`**. Use numeric fields. |
| 12 | **Fork detection** | ⚠️ | The Enhanced Edition uses the **same** install dir, registry keys, `CompanyName` and `ProductName`. The spec's check cannot detect it. |
| 13 | **Offline variant heuristic** | ➕ | `qbittorrent.exe` embeds `libtorrent/X.Y.Z.W` and the `-LTxxxx-` peer fingerprint. Reliable on all 4 builds and on the real install. |
| 14 | Clean shutdown via Restart Manager | 🔬 | Works in theory only through a Qt top-level window. qBittorrent saves its data *inside* `WM_QUERYENDSESSION`. Details in §5. |
| 15 | WebUI shutdown | ➕ | API key auth since 5.2.0 (`X-API-Key` or `Authorization: Bearer`), CSRF check skipped for it. |
| 16 | **Relaunch command line** | ⚠️ | The running process often carries a `.torrent` path or magnet as argument. Replaying it verbatim re-adds that torrent. |
| 17 | Uninstaller keeps unknown files | ➕ | `RMDir "$INSTDIR"` is non-recursive: user files in the install dir (e.g. themes) survive an update. |

---

## 2. Latest version (spec §2.1)

✅ `src/gui/programupdater.cpp` (identical on `master` and `release-5.3.0`):

- `QBT_MAIN_URL = https://www.qbittorrent.org/versions.json`
- `QBT_BACKUP_URL = https://qbittorrent.github.io/qBittorrent-website/versions.json`
- Both are fetched, a failed fetch yields an empty version, and `getLatestRemoteSource()` keeps the `std::max`.
- On Windows, `updateProgram()` only opens `https://www.qbittorrent.org/download`.
- JSON path: `json["win"]["version"]`, a string. No other fields.
- Both URLs answered identically on 2026-10-22 with `5.3.0`.

✅ FossHub removed in 5.3.0: Changelog line `OTHER: Remove FOSSHub site for program updates (vafada) #24611`.

➕ Observed on 5.2.4: at start-up it still queries `https://www.fosshub.com/feed/5b8793a7f9ee5a5c3e97a3b2.xml`
and logs a 404, but its other sources (`versions.json`) work. It does announce 5.3.0, with a **modal dialog
at every start**: "A new version is available. Do you want to download 5.3.0?". Answering Yes only opens the
download page. Two consequences:
- When QbtAutoUpdate closes qBittorrent, that modal may well be open. Tested: the Restart Manager path still
  closes cleanly with the modal open (TESTING.md).
- Every restart of an outdated qBittorrent shows the modal again, which is noisy during a postponed or failed
  update. The README recommends turning the built-in check off (*Options › Advanced › Check for program
  updates*, key `Advanced\updateCheck` in `[Preferences]`).

➕ Pre-release and maintenance builds exist side by side on SourceForge: `qbittorrent-5.3.0beta1`,
`qbittorrent-5.3.0rc1` and `qbittorrent-5.2.4` (a 5.2.x maintenance release published on 2026-09-28,
the same day as 5.3.0rc1). `versions.json` only ever announces stable releases. The tool must only act
on the version announced there, never on a folder discovered by listing.

---

## 3. Installers on SourceForge (spec §2.2)

### 3.1 URLs and redirects

✅ `https://downloads.sourceforge.net/project/qbittorrent/qbittorrent-win32/qbittorrent-{V}/{FILE}` answers
`302` to a mirror. Observed mirror hosts: `netix`, `altushost-swe`, `altushost-bul`, `altushost-net`,
`sf-eu-introserv-1`, `deac-riga`, `unlimited`, `master` (all `*.dl.sourceforge.net`).

➕ `https://sourceforge.net/projects/.../{FILE}/download` answers `302` to
`downloads.sourceforge.net/...?ts=...&use_mirror=<name>`. The `use_mirror=` parameter is a clean way to
switch mirror on retry.

➕ A file that does not exist returns **HTTP 404 with an HTML body** (not a redirect). So does the RSS feed
of a version folder that does not exist yet, and so does the folder page.

➕ The `use_mirror=<name>` parameter is ignored when sent to `downloads.sourceforge.net` (tested with
`master`, `autoselect`). Each request already gets a random mirror, so a retry naturally changes mirror.

⚠️➕ **User-Agent.** With `User-Agent: Mozilla/5.0`, Cloudflare in front of `sourceforge.net` returned
`403` with the body `no`. With no UA, `curl/8.x`, a full browser UA, or
`QbtAutoUpdate/0.1 (+https://github.com/...)`, it returned `200`. Decision: send a descriptive product UA,
never a fake bare browser string.

### 3.2 RSS feed

✅ `https://sourceforge.net/projects/qbittorrent/rss?path=/qbittorrent-win32/qbittorrent-{V}` lists every
file with:

- `<title>`: full path, e.g. `/qbittorrent-win32/qbittorrent-5.3.0/qbittorrent_5.3.0_x64_setup.exe`
- `<pubDate>`: in the form `Tue, 20 Oct 2026 12:52:43 UT`. Note the **`UT`** zone: .NET does not parse it
  out of the box, so it needs a custom parser.
- `<media:content filesize="50030660">` and `<media:hash algo="md5">`. These allow a cheap secondary
  integrity check before the PGP check.

➕ Fallback: the folder page `https://sourceforge.net/projects/qbittorrent/files/qbittorrent-win32/qbittorrent-{V}/`
embeds `net.sf.files = {…};`, a JSON object keyed by file name with `name`, `path`, `type`, `sha1`, `md5`,
**`sha256`**, `downloadable` and `staged` (plus `staging_days: 3`), but **no size and no date**. A file
still `staged` is treated as not yet available.

5.3.0 files were uploaded on 2026-10-20 between 12:51 and 12:52 UT. The release news is dated
2026-10-19 and the signatures were made on 2026-10-18 and 2026-10-19.

### 3.3 Variants and file names ⚠️

The spec says the default build moved to libtorrent 2.x in 5.2.0. **That is wrong: it happened in 5.3.0.**

Evidence:
1. The 5.3.0 news post on qbittorrent.org says *"The default version is now based on libtorrent 2.1.x series"*.
2. The 5.2.0, 5.2.1, 5.2.2, 5.2.3 and 5.2.4 folders contain `_x64_setup.exe` plus `_lt20_x64_setup.exe`,
   not `_lt12_`.
3. Inspecting the binaries: `qbittorrent_5.2.3_x64_setup.exe` ships libtorrent **1.2.20**, and
   `qbittorrent_5.2.3_lt20_x64_setup.exe` ships **2.0.13**.
4. The real 5.2.4 install inspected for this report is the default build and embeds `libtorrent/1.2.20.0`.

Actual file lists, taken from the RSS feeds of **all 25 releases from 4.6.0 to 5.3.0**. Each folder holds
exactly two installers, one `.asc` per installer and a `README`:

| Versions | libtorrent 1.2 build | libtorrent 2.x build |
|----------|----------------------|----------------------|
| 4.6.0 to 5.0.0 | `qbittorrent_{V}_x64_setup.exe` | `qbittorrent_{V}_lt20_qt6_x64_setup.exe` (2.0) |
| 5.0.1 to 5.1.4 | `qbittorrent_{V}_x64_setup.exe` | `qbittorrent_{V}_qt6_lt20_x64_setup.exe` (2.0) |
| 5.2.0 to 5.2.4 | `qbittorrent_{V}_x64_setup.exe` | `qbittorrent_{V}_lt20_x64_setup.exe` (2.0) |
| 5.3.0 (also 5.3.0beta1, rc1) | `qbittorrent_{V}_lt12_x64_setup.exe` | `qbittorrent_{V}_x64_setup.exe` (**2.1**) |

So the 2.x infix has been renamed three times (`lt20_qt6` → `qt6_lt20` → `lt20`) before the defaults
flipped. Eleven of these feeds are stored as test fixtures, covering every naming boundary.

Consequences:

- A user on the **default build of any 5.2.x** (libtorrent 1.2) must be updated to
  **`qbittorrent_5.3.0_lt12_x64_setup.exe`**. Picking the 5.3.0 default would silently move them to
  libtorrent 2.1. That is exactly the variant switch the spec forbids. qBittorrent's news post also warns
  heavy seeders that 2.1 reads more slowly than 1.2.
- A user on `lt20` (2.0.x) moves to the 5.3.0 default (2.1.x). The variant is the libtorrent *major series*
  (1 or 2), as the spec says.
- The resolver table is keyed by version range plus a file-name pattern, mapping to a series. Unknown names
  are ignored and logged. The RSS feeds above are saved as test fixtures in phase 1.

➕ The download page's SHA-256 table labels the 5.3.0 lt12 hash as **`5.3.0 (lt21)`**: a typo on the site.
This is one more reason not to parse that HTML. The two SHA-256 values themselves match the downloaded files:
`aff5b3bd…486436` (default) and `10876b71…a8e58` (lt12).

### 3.4 Architecture

✅ `config.nsh` still offers `QBT_CPU_ARCH` `x64` or `arm64`, but no arm64 file exists anywhere in the
`qbittorrent-win32` feed (100 most recent items, 0 matches).

➕ Changelog 5.2.2: `WINDOWS: NSIS: Allow to install x64 binary on ARM64 (Chocobo1) #24358`. Before 5.2.2 the
x64 installer refused to run on ARM64. That does not matter here because the tool only installs the latest
release (≥ 5.3.0).

✅ `.onInit` rejects an arm64 installer on native AMD64 (`IsNativeAMD64`) with exit code 1654.

---

## 4. Signatures (spec §2.3)

✅ `5B7CC9A2.asc` from `master`:

```
pub   rsa4096 2016-04-12 [SC]                 (no expiry)
      D8F3 DA77 AAC6 7410 5359  9C13 6E4A 2D02 5B7C C9A2
uid   sledgehammer_999 (Used for signing git commits/tags/etc) <hammered999@gmail.com>
uid   sledgehammer999 (Used for signing qBittorrent source tarballs and binaries v2.) <sledgehammer999@qbittorrent.org>
sub   rsa4096 2016-04-12 [E]
```

No revocation signature in the file. The only subkey is encryption-only, so binaries are signed by the
**primary** key.

✅ `gpg --verify` returned *Good signature* for all four sampled installers. Signature packet details, which
the BouncyCastle verifier must accept:

- ASCII-armored, detached, 887 bytes
- v4 signature, class `0x00` (binary document), RSA, **SHA-256** (digest algo 8)
- hashed subpackets: issuer fingerprint (v4), creation time, and a **non-critical notation**
  `manu=2,2.5+1.12,2,1`

➕ The download page still mentions the older 4096R/520EC6F6 key (fingerprint
`F4A5FD201B117B1C2AB590E2A1ACCAE4520EC6F6`), which signed only v3.3.4. It is not relevant for current builds
and is not to be trusted by the tool.

➕ The installers and `qbittorrent.exe` have **no Authenticode signature** (`NotSigned`). PGP is the only
cryptographic proof of origin.

---

## 5. NSIS installer behaviour (spec §2.4)

✅ Confirmed from `config.nsh`, `installer.nsh` and `uninstaller.nsh` (identical at `release-5.3.0`):

- `RequestExecutionLevel user` plus `!insertmacro UAC_RunElevated` in `.onInit` and `un.onInit`. When the
  process is already admin (`$3 <> 0`), it carries on without a prompt. `SetShellVarContext all` is in the
  same macro.
- `InstallDir $PROGRAMFILES64\qBittorrent` and `InstallDirRegKey HKLM Software\qbittorrent InstallLocation`.
- No `SetRegView 64` anywhere, and the plugins are `/x86-unicode`: the installer is 32-bit.
- Registry writes: `HKLM\Software\qBittorrent\InstallLocation`, the `Capabilities` and ProgIDs, and
  `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\qBittorrent` (`DisplayName`, `UninstallString`,
  `DisplayIcon`, `Publisher` = `The qBittorrent project`, `DisplayVersion`, `EstimatedSize`, `NoModify`,
  `NoRepair`).
- Sections selected by default: files (RO), Start menu, firewall (`nsisFirewallW::AddAuthorizedApplication`),
  `LongPathsEnabled = 1`. Sections off by default (`/o`): Desktop shortcut, startup with Windows.
- A hidden first section runs `"$uninstallerPath\uninst.exe" /S _?=$uninstallerPath` when a previous install
  is found in `$INSTDIR` (as set by `/D=` or the registry).
- The uninstaller always removes `$SMPROGRAMS\qBittorrent` and `$DESKTOP\qBittorrent.lnk`. With the `all`
  shell context these are the common Start menu and the **Public** desktop. Removing configuration
  (`%APPDATA%\qBittorrent`) and cache (`%LOCALAPPDATA%\qBittorrent`) is opt-in (`/o`). The HKCU `Run` value
  is never touched.
- `check_instance` is only a `MUI_PAGE_CUSTOMFUNCTION_LEAVE` of the directory page.
- `.onInstSuccess` sets error level 0. Exit codes found: 1654 (`.onInit`: Windows < 10 1809, not 64-bit,
  arm64 on x64), 1223, 1062, 1603 and 1314 (UAC macro), 15618 (`check_instance`).

✅ **Silent mode never calls page callbacks.** NSIS manual §4.12: *"any callback related to a specific page
or page type will not be called."* So `/S` provides no protection against a running `qbittorrent.exe`.

✅ `/D=`: NSIS manual §3.2: *"It must be the last parameter used in the command line and must not contain
any quotes, even if the path contains spaces. Only absolute paths are supported."* ➕ Implementation note:
build the raw `ProcessStartInfo.Arguments` string. `ArgumentList` would add quotes around a path with spaces.

➕ The UAC macro's error `MessageBox` calls (cases 1223, 1062 and the default case) have **no `/SD`**, so they
would show even in silent mode. This cannot happen when running as SYSTEM, which is already elevated.

✅ **Registry view, confirmed on a real install** (5.2.4, Windows 11 x64): `Software\qBittorrent` and the
`Uninstall\qBittorrent` key exist **only** under `RegistryView.Registry32`. `Registry64` has neither.
The tool reads both, 32-bit first.

➕ **The uninstaller only deletes the files it knows** (`qbittorrent.exe`, `qbittorrent.pdb`, `qt.conf`,
`uninst.exe`, `translations\`), then calls a non-recursive `RMDir "$INSTDIR"`. Extra files a user put in the
install folder (seen: a `qbt-theme-master` folder) survive an update. The Start menu folder and the firewall
rule are recreated: if the user had deleted the shortcut or tightened the rule, those changes are reverted.
The README documents this next to `LongPathsEnabled`.

✅ **Version resources:**

| File | `FileVersion` (string) | Numeric | `CompanyName` | `ProductName` |
|------|------------------------|---------|---------------|---------------|
| installer | `5.3.0` | 5.3.0.0 | `The qBittorrent project` | `qBittorrent` |
| `qbittorrent.exe` | **`v5.3.0`** | 5.3.0.0 | `The qBittorrent Project` (capital P) | `qBittorrent` |

Use `FileMajorPart`, `FileMinorPart`, `FileBuildPart` and `FilePrivatePart`, never the string. Compare names
case-insensitively. `qbittorrent.exe` is an AMD64 PE (`0x8664`).

---

## 6. Detecting the installation (spec §4.5)

### 6.1 Fork detection ⚠️

`c0re100/qBittorrent-Enhanced-Edition` (branch `v5_2_x`, latest release `release-5.2.4.10`):

- Same `InstallDir $PROGRAMFILES64\qBittorrent`, same `HKLM\Software\qBittorrent`, same
  `Uninstall\qBittorrent` key with `DisplayName` `qBittorrent` and `Publisher` `The qBittorrent project`.
- `src/qbittorrent.rc` is unchanged from upstream: `qbittorrent.exe` reports `CompanyName`
  `The qBittorrent Project` and `ProductName` `qBittorrent`.
- Only the *installer* metadata differs (`ProductName` `qBittorrent Enhanced`), and it is gone once installed.

So the spec's `CompanyName` and `ProductName` check **cannot** tell the fork apart. Markers that do:

1. Version: Enhanced Edition always sets the 4th component to ≥ 10 (`5.2.4.10`, `5.1.0.11`…). Upstream 4-part
   versions have only ever used small build numbers (for example `4.3.0.1`).
2. Strings only the fork contains: `AutoBanUnknownPeer` and `ShadowBan` (settings keys in its
   `sessionimpl.cpp`). They are absent from all four upstream binaries and from the 5.2.4 install.

Rule: if either marker matches, treat the install as a fork and do nothing. Positive detection is tested on a
real Enhanced Edition binary in an `Integration` test.

### 6.2 Variant detection: offline heuristic ➕

Two libtorrent strings are compiled into `qbittorrent.exe` (ASCII):

| Build | `libtorrent/…` (default user agent) | Peer fingerprint |
|-------|-------------------------------------|------------------|
| 5.3.0 default | `libtorrent/2.1.2.0` | `-LT2120-` |
| 5.3.0 lt12 | `libtorrent/1.2.20.0` | `-LT12K0-` |
| 5.2.3 default | `libtorrent/1.2.20.0` | `-LT12K0-` |
| 5.2.3 lt20 | `libtorrent/2.0.13.0` | `-LT20D0-` |
| real 5.2.4 install | `libtorrent/1.2.20.0` | `-LT12K0-` |

The major digit gives the series. Requiring both strings to agree makes false positives very unlikely. Do not
use a bare `\d+\.\d+\.\d+\.\d+` match: the binary is full of OIDs such as `2.5.4.10`.

Because this reads the binary that is actually installed, it is more reliable than `state.json`, which goes
stale if the user reinstalls another variant by hand. **Detection order** (DECISIONS.md D-11): binary scan →
WebUI `buildInfo` → `state.json` → ask during `setup`.

➕ `GET /api/v2/app/buildInfo` returns `{qt, libtorrent, boost, openssl, zlib, bitness, platform}`.
`libtorrent` is `Utils::Misc::libtorrentVersionString()`.

### 6.3 Residual risk for `apply`: renamed installer

The PGP signature covers the file content, not its name. A local user could drop a genuine, signed, newer
default installer renamed as `…_lt12_…` into the inbox. Version and signature checks would pass and the
machine would switch variant. Impact is limited: it is still an official, signed, newer build. A
standard-user-to-SYSTEM escalation is not possible this way. Proposed mitigation: after installing, `apply`
scans the new `qbittorrent.exe` (§6.2). On a mismatch it records the result in `state.json` and the user side
notifies. Detecting the variant from inside the solid-LZMA NSIS package before installing would need an NSIS
parser, so it is out of scope.

---

## 7. Closing qBittorrent cleanly (spec §2.5)

### 7.1 What qBittorrent does

✅ `src/app/application.cpp`:

- `connect(this, &QGuiApplication::commitDataRequest, this, &Application::shutdownCleanup, Qt::DirectConnection)`,
  Windows GUI only.
- `shutdownCleanup()` calls `cleanup()` **synchronously** (resume data, settings, session teardown, with a
  `ShutdownBlockReasonCreate("Saving torrent progress...")`), then queues `quit()`.
- `setQuitOnLastWindowClosed(false)`: closing the last window does not quit.

✅ `src/gui/mainwindow.cpp`, `closeEvent` (the `WM_CLOSE` path):

- *close to tray* enabled and window visible → `e->ignore()` and hide. qBittorrent does not quit.
- window hidden, or *close to tray* off, with `confirmOnExit` on and torrents active → modal dialog
  *"Some files are currently transferring… Are you sure you want to quit qBittorrent?"* (`e->spontaneous()`
  is true for an external `WM_CLOSE`).

This confirms that a plain `WM_CLOSE` is unreliable.

### 7.2 What Qt does with `WM_QUERYENDSESSION` 🔬

`qtbase/src/plugins/platforms/windows/qwindowscontext.cpp` (6.11):

- `WM_QUERYENDSESSION` → `QueryEndSessionApplicationEvent` → `qGuiAppPriv->commitData()` →
  `commitDataRequest`, which runs qBittorrent's cleanup.
- **But** `QWindowsContext::windowsProc` returns early (`No Qt Window found for event`) unless the target
  HWND belongs to a Qt platform window (`QWindowsWindow`).
- The tray icon's hidden top-level window (`QTrayIconMessageWindow`) has its own wndproc
  (`qWindowsTrayIconWndProc`). It forwards only tray messages, `WM_CLOSE` and `WM_COMMAND`. Everything else
  goes to `DefWindowProc`, which answers `TRUE` **without saving anything**.
- Qt's other internal windows are `HWND_MESSAGE` windows, which top-level enumeration never sees.

So the clean path depends on the **main window's HWND** receiving the message. `mainwindow.cpp` always
creates it when the tray is active: start minimized runs `showMinimized()` then `hide()`. A hidden but
existing top-level HWND should receive the Restart Manager broadcast.

✅ **Confirmed on a real qBittorrent 5.2.4** (TESTING.md, 2026-10-22). With the main window hidden in the
tray, the process has a top-level but invisible `Qt6103QWindowIcon` window. Restart Manager closed it in about
1 s, and qBittorrent's log shows its full clean exit: shutdown initiated, resume data saved, session
terminated, ready to exit.

✅ **Confirmed in Windows Sandbox on 5.2.3** (2026-10-23) in the hardest configuration:
- *start minimized* (`GUI/StartUpWindowState=Minimized`, window never shown);
- *minimize and close to tray*;
- ***confirm on exit*** with an **active torrent**.

Restart Manager closed it cleanly in 2 s without any confirmation dialog: the `commitDataRequest` path skips
`closeEvent`. The updated 5.3.0 then logged "torrent restored". 🔬 Still to see: a long shutdown with
many torrents.

➕ **qBittorrent survives the end of the scheduled task that restarted it.** The Check task's process starts
it and exits, and qBittorrent is still running 50 s later. Task Scheduler does not end the task's child
processes when the action completes normally.

### 7.3 Restart Manager

✅ Microsoft docs (*Guidelines for Applications*, `RmShutdown`):

- GUI apps receive `WM_QUERYENDSESSION` (`ENDSESSION_CLOSEAPP`), then `WM_ENDSESSION`, then `WM_CLOSE` if
  they are still running.
- Without `RmForceShutdown`, an application that does not exit is left alone. `RmShutdown` returns
  `ERROR_FAIL_SHUTDOWN` (351) and the per-process `AppStatus` is available from `RmGetList`.
- With `RmForceShutdown`, unresponsive apps are killed after 30 s. **Never used.**
- *"An installer that is running as a service with LocalSystem privileges cannot shut down or restart any
  applications in another user or terminal session."* This matches the design: the user-side process
  closes qBittorrent in its own session, and the SYSTEM side only checks that no instance runs anywhere.

➕ qBittorrent does its long cleanup **inside** `WM_QUERYENDSESSION`, against Microsoft's guideline. With many
torrents, Restart Manager may treat the reply as late and report a failure **while qBittorrent is in fact
shutting down**. Decision: the result of `RmShutdown` is informational only. Success means the registered
process (PID plus start time) has exited within `closeTimeoutSeconds`. A side risk: if `WM_ENDSESSION`
arrives before the cleanup finishes, Restart Manager's follow-up `WM_CLOSE` could hit `closeEvent` and show
the confirmation dialog. 🔬 To be observed in Sandbox.

### 7.4 WebUI shutdown ➕

`src/webui/webapplication.cpp` and `src/webui/api/appcontroller.cpp`:

- `POST /api/v2/app/shutdown` replies first, then calls `QCoreApplication::exit()` 100 ms later. That leads
  to `aboutToQuit` and the same `cleanup()`. GET, POST and HEAD are allowed by default.
- **API key authentication (new in 5.2.0**, Changelog `WEBAPI: Add support for authenticating via API key`):
  header `X-API-Key: <key>` or `Authorization: Bearer <key>`. API-key requests **skip the CSRF check**, are
  refused on `auth/*`, and need no cookie. Better than storing a username and password: the tool stores
  only the key, encrypted with DPAPI. Username and password with an `SID` cookie stay as a fallback for
  versions before 5.2.
- CSRF check for cookie sessions: `Sec-Fetch-Site` if present, else `Origin`, else `Referer` must match
  `Host`. **A request with none of these headers is accepted.** Sending `Origin` equal to the target is still
  the safe choice.
- The *Bypass authentication for clients on localhost* option may make authentication unnecessary.

### 7.5 Relaunch command line ⚠️

The 5.2.4 process inspected was started as
`"C:\Program Files\qBittorrent\qbittorrent.exe" "C:\Users\…\Downloads\<name>.torrent"`: double-clicking a
`.torrent` file starts qBittorrent with the file path. Relaunching with the same arguments would add that
torrent again (or open the *Add torrent* dialog).

Rule: rebuild the command line from a whitelist of the options in `src/app/cmdoptions.cpp` that describe the
*instance*:

- kept: `--profile=`, `--configuration=`, `--relative-fastresume`, `--webui-port=`, `--torrenting-port=`,
  `--no-splash`, `--confirm-legal-notice`
- dropped: positional arguments (files, magnets, URLs) and per-torrent options (`--save-path`,
  `--add-stopped`, `--skip-dialog`, `--category`, `--sequential`, `--first-and-last`, `--seed-mode`)

The HKCU `Run` value that qBittorrent writes itself has the form
`"…\qbittorrent.exe" "--profile=" "--configuration="`, with empty values. The parser must accept that form.

➕ Parsing rules confirmed in `parseCommandLine()` (`release-5.3.0`):
- An argument is an option only if it starts with `--` **and does not end with `.torrent`**, or is a two-character
  `-x`. Everything else, including `--something.torrent`, is a file path, magnet link or URL to add.
- Options with a value accept only `--name=value` (`--profile D:\p` is an error); values are unquoted from `'`
  or `"`. `--profile` is made absolute against qBittorrent's working directory.
- Every option can also come from a `QBT_<NAME>` environment variable.

➕ Profile layout (`src/base/profile_p.cpp`), with `[_name]` the optional `--configuration` suffix:
- default: config `%APPDATA%\qBittorrent[_name]` (QSettings INI files), data `%LOCALAPPDATA%\qBittorrent[_name]`;
- custom root (`--profile=R`, or portable `profile` folder next to the exe): `R\qBittorrent[_name]\config`,
  `\data`, `\cache`.

➕ The command line of a running process is readable without WMI through `NtQueryInformationProcess`
(`ProcessCommandLineInformation`, needs only `PROCESS_QUERY_LIMITED_INFORMATION`). It returned the 5.2.4
command line quoted above on the development machine.

### 7.6 Profile and backup ➕

On the inspected machine:

- `%APPDATA%\qBittorrent` contains `qBittorrent.ini`, `qBittorrent-data.ini`, several suffixed variants
  (`qBittorrent_<id>.ini`, used for `--configuration`), `categories*.json`, `watched_folders*.json`, an `rss\`
  folder and a `lockfile`.
- `%LOCALAPPDATA%\qBittorrent` contains `BT_backup`, `cache`, `GeoDB`, `logs`, `nova3`, `rss`, `SearchUI`.

Proposal: back up the top-level `*.ini` **and** `*.json` files of the config folder, skipping `lockfile` and
the temporary `*_new.ini.*` files. The `.json` files hold categories and watched folders, which the spec's
`*.ini` pattern would miss.

➕ The built-in update check is the `Advanced\updateCheck` key in the `[Preferences]` section of
`qBittorrent.ini` (it was `true` here). The README will advise turning it off in *Options › Advanced*. The
tool will not edit it.

---

## 8. Tooling and dependencies (spec §3)

| Item | Finding |
|------|---------|
| .NET SDK | 10.0.402 is installed on the development machine. |
| `BouncyCastle.Cryptography` | 2.7.0 (2026-07-30), targets `net6.0`: compatible. |
| `TaskScheduler` (dahall) | 2.12.2 (2025-07-08), latest asset `net9.0-windows7.0`: compatible with net10. Uses COM interop: check trimming warnings. |
| `Microsoft.Toolkit.Uwp.Notifications` | 7.1.3, **last release 2022-11-01**, not deprecated but unmaintained. Assets include `net5.0-windows10.0.17763`, so it is usable from `net10.0-windows10.0.19041.0`. **Not used** (D-38): QbtAutoUpdate calls the WinRT `ToastNotificationManager` from the SDK projection, registers an AUMID, and uses protocol activation instead of a COM activator. |
| Windows App SDK | 2.5.x. Heavier: unpackaged use needs the runtime or self-contained deployment. Kept as last resort. |

---

## 8b. Platform behaviours learned while building (phase 3)

- ➕ **Source-generated COM maps HRESULTs to .NET exception types**: `0x80070002` becomes `FileNotFoundException`,
  `E_ACCESSDENIED` becomes `UnauthorizedAccessException`; only the rest stay `COMException`. Catching
  `COMException` for "task folder not found" missed it, and setup crashed in the sandbox on a machine where
  `\QbtAutoUpdate` did not exist yet. Errors are now matched by `HResult`, with a regression test.
- ➕ **Windows Sandbox runs with UAC disabled** (`EnableLUA=0`) under the administrator `WDAGUtilityAccount`,
  and **copies the host's display language** (qBittorrent logged in French). The scenario reads logs as
  UTF-8 and matches English or French.
- ➕ `net user <name> <password> /add` asks an interactive question when the password is longer than 14
  characters.
- ➕ Only one Windows Sandbox can run at a time, and that includes one that is still shutting down. The
  launcher waits for the `WindowsSandbox*` processes to disappear.
- ➕ With UAC off, an "elevated" process is the normal state, so the restart goes through the shell-token path
  (`CreateProcessWithTokenW`). It worked: qBittorrent restarted in the user's session.

Phase 4 additions:

- ➕ **Console text and code pages.** A WinExe whose output is redirected has no console. .NET then encodes
  in a single-byte fallback: "à" came out as `E0` and "→" as `?`. The shell reading the pipe decodes with its
  console's code page: 850 on a French Windows, 65001 when set to UTF-8. Also, .NET encodes OEM code pages
  such as 850 only once `CodePagesEncodingProvider` is registered.
- ➕ **Fixing the redirected case.** QbtAutoUpdate now registers that provider and attaches to the parent
  console even when output is redirected. `AttachConsole` replaces the redirected standard handles with
  the console's: the output then went to the console and the file stayed empty. The handles are saved
  before attaching and restored afterwards.
- ➕ **Checked bytes.** Under `chcp 850`, "à" is written as `85`; under `chcp 65001`, as `C3 A0`; and a
  PowerShell pipe decodes it correctly.
- ➕ **Console strings stay ASCII-safe.** Console messages avoid characters missing from OEM code pages:
  `...` instead of `…`, `->` instead of `→`, straight quotes.
- ➕ **Toast history from PowerShell.** Windows PowerShell 5.1 reads an unpackaged app's notification history
  with `ToastNotificationManager.History.GetHistory('<AUMID>')`. The sandbox scenario uses it to check what
  was shown.

## 8c. Other updaters, checked 2026-10-23 (README FAQ, spec §9)

- ➕ **winget.** The package `qBittorrent.qBittorrent` 5.3.0 points at `qbittorrent_5.3.0_x64_setup.exe`,
  the new default libtorrent 2.1 build (`nullsoft`, `Scope: machine`, `UpgradeBehavior: install`, SHA-256
  in the manifest). Its 5.2.4 manifest pointed at `qbittorrent_5.2.4_x64_setup.exe`, which was libtorrent
  1.2. An upgrade therefore moves a 5.2.x default install to libtorrent 2.x. Source:
  `microsoft/winget-pkgs`, `manifests/q/qBittorrent/qBittorrent/`.
- ➕ **winget lt2 package.** `qBittorrent.qBittorrent.lt2` (the `lt20` builds) has versions up to 5.2.4.
  There is no libtorrent 1.2 package for 5.3.0.
- ➕ **Winget-AutoUpdate** (Romanitho/Winget-AutoUpdate, active):
  - It runs winget as SYSTEM, daily and at logon.
  - Closing an app is left to per-app mod scripts (`<AppID>-preinstall.ps1`, ...).
  - The provided helper `Stop-ModsProc` in `mods/_Mods-Functions.ps1` is
    `Stop-Process -Name $process -Force`, a kill.
- ➕ **UniGetUI** (Devolutions/UniGetUI, active). The per-package `KillBeforeOperation` list runs
  `KillProcessOperation`, which:
  - calls `CloseMainWindow()`, then waits 1 second;
  - then calls `Kill(entireProcessTree: true)`, but only when forced or when the "kill processes that refuse
    to die" setting is on.
  `CloseMainWindow` posts `WM_CLOSE` to the main window. qBittorrent with *close to tray* only hides itself
  on that message (§7.1), and a hidden window has no main window handle. Its "close running app and retry"
  path uses `forceKill: true`.

## 9. Open items to validate empirically

Done in phase 1: the offline variant heuristic was confirmed by the `Integration` tests. Both real 5.3.0
installers were downloaded, verified with the pinned key, and unpacked; the right series was read from each
`qbittorrent.exe`. On the development machine, `status` and `update --dry-run` reported 5.2.4,
libtorrent 1.2, target `qbittorrent_5.3.0_lt12_x64_setup.exe`, signature valid.

| Phase | Item | Status |
|-------|------|--------|
| 2 | Restart Manager closes qBittorrent with *minimize/close to tray*, *start minimized* and *confirm on exit* enabled, window visible, hidden or never shown (§7.2 and §7.3). | ✅ host 5.2.4 and Sandbox 5.2.3, active torrent included |
| 2 | Resume data actually written: no full recheck after restart. | ✅ "resume data saved" then "torrent restored" in qBittorrent's log; a long shutdown with many torrents is still open |
| 2 | Behaviour when qBittorrent runs elevated: `OpenProcess` access denied, Restart Manager failure. | 🔬 unit-tested (the update is blocked before anything is touched); needs a PC with UAC on |
| 3 | A task with a `BUILTIN\Users` group principal and interactive token runs for the logged-on user. | ✅ Sandbox: ran in the user's session (several users signed in at once: not tested) |
| 3 | `apply` task security descriptor that lets non-admins call `IRegisteredTask.Run`. | ✅ Sandbox: a standard account started it with `schtasks /run` |
| 3 | Installer under SYSTEM: no UI, Public desktop shortcut removed then recreated, HKCU `Run` intact. | ✅ Sandbox |
| 3 | Behaviour of an `/S` install when a file is locked (must never happen, but the post-check must catch it). | 🔬 not reproduced; the post-install version check would report it |
| 4 | Toast library under .NET 10, single-file and trimming; activation after the process has exited. | ✅ No library (D-38): WinRT projection in the single-file exe, protocol activation after the check has exited (Sandbox runs 8–10). Trimming still to decide in phase 5 |
| 5 | Current behaviour of winget, Winget-AutoUpdate and UniGetUI for qBittorrent (README FAQ). | ✅ §8c |
| 5 | No UAC prompt after setup, no window flashing, on a PC with UAC on (Sandbox has UAC off). | open |
