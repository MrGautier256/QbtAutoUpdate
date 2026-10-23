# Changelog

All notable changes to QbtAutoUpdate. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/). The release workflow publishes the section
of each version as its release notes.

## [1.0.0] - 2026-10-23

First public release.

### Added

- **Setup window**: double-click the exe, choose "update automatically" or "ask me first with a
  notification", and accept one administrator prompt. Once installed, the same exe opens a settings
  window: on/off, mode, check now, previous version, logs, uninstall.
- **Automatic updates** of the official qBittorrent for Windows:
  - QbtAutoUpdate checks at logon and every 6 hours through Task Scheduler, with no resident process.
  - It installs the official installer from SourceForge after verifying its OpenPGP signature against
    qBittorrent's release key (fingerprint pinned).
  - It keeps the libtorrent series in use (1.2 or 2.x), including across 5.3.0, where the default build
    changed to libtorrent 2.1.
- **Clean shutdown and restart**: qBittorrent is asked to close through the Restart Manager (or its WebUI
  if configured) and is never killed by default. Resume data is saved, then qBittorrent restarts in the
  user's session with its instance options.
- **No administrator prompt after setup**: installs run in a SYSTEM scheduled task. Standard accounts can
  trigger it, but they cannot hand it anything it has not verified.
- **Notify mode**: a Windows notification with "Update now", "Later" (24 h) and "Skip this version".
- **Rollback** to the previous release, same libtorrent series. Automatic updates then skip the version
  that was rolled back.
- Settings backup before each update, and recreation of the Public desktop shortcut that the installer
  removes.
- English and French user interface, following the Windows display language.
- Command line for scripting: `status`, `check`, `update`, `setup`, `config`, `rollback`, `restart`,
  `webui`, `uninstall`.
- Self-contained single-file executables for x64 and ARM64 (about 13 MB each).

[1.0.0]: https://github.com/MrGautier256/QbtAutoUpdate/releases/tag/v1.0.0
