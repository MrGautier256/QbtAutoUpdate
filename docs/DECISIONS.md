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
`buildInfo` source arrives with the WebUI client in phase 2, the setup question in phase 3. Until then an
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
error, 2 usage, 3 unsupported installation or unknown variant, 4 release sources unreachable, 5 signature
refused, 9 feature not available yet (temporary, during development).

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

## Decided ahead of later phases

These choices come from the findings and are recorded now so they are not lost:

- **Relaunch (phase 2).** Rebuild the command line from a whitelist of instance options; never replay
  `.torrent` paths or magnet links (FINDINGS §7.5).
- **Closing qBittorrent (phase 2).** The `RmShutdown` result is informational only; success means the
  registered process has really exited (FINDINGS §7.3).
- **WebUI (phase 2).** Prefer API key authentication (5.2.0+), stored with DPAPI; username and password are
  the fallback.
- **Config backup (phase 2).** Back up top-level `*.ini` and `*.json` from the profile, not only `*.ini`.
- **apply (phase 3).** After installing, scan the new `qbittorrent.exe` and record a variant mismatch: a
  renamed but genuine installer is the residual risk described in FINDINGS §6.3.
