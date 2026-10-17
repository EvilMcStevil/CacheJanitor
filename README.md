# CacheJanitor

Keeps one or more cache folders on a Windows machine under a fixed size by deleting the
least recently used files. Built for CI agents: tools download into the folder as normal
and know nothing about it; CacheJanitor tidies up behind them.

There is no built-in Windows "folder with a max size that evicts old stuff" — FSRM quotas
only *block* writes, which would break your builds — so this runs as a small service, with
a tray app to watch and configure it.

## How it works

- Watches each folder. Shortly after anything is written (debounce, default 15 s) and also
  every few minutes, it adds up the folder's size. A watched folder with no changes is
  rescanned at most hourly.
- If it's over `MaxSize`, it deletes the oldest-used entries until it's down to
  `TrimToPercent` of the limit (default 90%), so it isn't deleting on every new file.
- "Last used" = newest of a file's **last access**, last write and creation time.
- Never deletes anything used in the last `MinAgeMinutes` (default 10) — protects
  in-progress downloads.
- Files a tool has open can't be deleted on Windows, so they're simply skipped. In
  `TopLevelEntry` mode a folder is first renamed aside, which Windows refuses if any file in
  it is open — so a folder is either removed whole or left alone, never half-deleted.
- Scans run at Windows background priority (low CPU, I/O and memory priority).

## Install

```
msiexec /i CacheJanitor-1.0.0-x64.msi                       # interactive
msiexec /i CacheJanitor-1.0.0-x64.msi /qn                   # silent, service and tray
msiexec /i CacheJanitor-1.0.0-x64.msi /qn ADDLOCAL=Service  # silent, service only (headless agents)
```

The MSI installs to `C:\Program Files\CacheJanitor`:

- **CacheJanitor service** — runs as LocalSystem, automatic (delayed) start, restarts itself
  after a crash or a non-zero exit. Logs to Event Viewer → Windows Logs → Application,
  source `CacheJanitor`.
- **Tray app** (optional feature) — starts at every user's sign-in and is in the Start menu
  as *CacheJanitor*.

Upgrades are in place (major upgrade); configuration and statistics are kept. Uninstalling
leaves `%ProgramData%\CacheJanitor` behind.

Machines that used the old `install-service.ps1` need that service removed first:
`sc.exe stop CacheJanitor` then `sc.exe delete CacheJanitor` (as admin), and their
`"Janitor"` section moved from `appsettings.json` into the new `config.json` below.

## The tray app

The icon is a live gauge of the fullest folder: white while there's room, amber from 90% of
a limit, red when a folder is over its limit or failing, grey when the service isn't
running. Hover for every folder's fill level; click for the dashboard:

- **Overview** — fill level, file count, how far back the cache reaches, how long the last
  evicted entry had been idle (if that's short, the cache is too small), downloaded and
  evicted totals, the last 24 hours, download rate, size over 24 h / 7 days against the limit
  and trim target, and the age distribution of what's in the folder.
- **Activity** — bytes added vs. evicted per hour (or per 6 h), scan timing, watcher
  activity, entries skipped because they were in use, and the latest evictions.
- **Folder settings** / **Service settings** — everything in `config.json`, with Add folder,
  Remove and Browse. Saving asks for administrator approval (UAC) because the file decides
  what LocalSystem deletes; the service applies it within a couple of seconds.
- Start, stop and restart the service; trim everything now.

It warns when NTFS last-access tracking is off, when the service rejected part of the
config, and pops a notification when a folder goes over its limit or starts failing.

## Important: enable last-access tracking

True LRU needs Windows to update a file's access time when a tool *reads* it. On many
machines that's turned off. Check and enable (admin, then reboot):

```
fsutil behavior query disablelastaccess
fsutil behavior set disablelastaccess 0
```

If it stays off, CacheJanitor still works but evicts by "least recently downloaded"
instead. The service logs a warning at startup and the tray shows a banner when the setting
is off. NTFS only records access time to about 1-hour precision, which is fine for this
purpose.

## Configure — `%ProgramData%\CacheJanitor\config.json`

Edit it with the tray app, or by hand as administrator. The service reloads it on save; no
restart needed. For a fleet, drop the same file onto each agent.

```json
{
  "Janitor": {
    "ScanIntervalMinutes": 5,
    "DebounceSeconds": 15,
    "Caches": [
      { "Path": "D:\\ci-cache\\downloads", "MaxSize": "100GB" },
      { "Path": "D:\\ci-cache\\toolchains", "Name": "Toolchains", "MaxSize": "40GB",
        "Unit": "TopLevelEntry", "MinAgeMinutes": 60, "Exclude": [ "keep-forever" ] }
    ]
  }
}
```

| Setting | Default | Meaning |
|---|---|---|
| `Path` | — | Folder to manage (created if missing). Must be absolute. |
| `Name` | — | Optional display name for the tray app. |
| `MaxSize` | — | `750MB`, `100GB`, `1.5TB` or bytes. Binary units (1 GB = 1024³). |
| `TrimToPercent` | 90 | When over the limit, trim down to this % of it. |
| `Unit` | `File` | `File`: evict individual files anywhere in the tree. `TopLevelEntry`: evict each top-level file/folder as a whole (use when a tool unpacks one folder per item and a half-deleted folder would break it). |
| `MinAgeMinutes` | 10 | Never touch anything used more recently than this. |
| `Exclude` | `[]` | Globs relative to `Path` never evicted (`*`, `?`, `**`); matching a folder protects everything in it. |
| `Enabled` | `true` | `false` measures the folder (stats, tray) without evicting anything. |

Refused, with a message in the event log and the tray: drive and share roots, anything in
`C:\Windows` or `Program Files`, `C:\Users` and `C:\ProgramData` themselves, CacheJanitor's
own data folder, relative paths, and caches nested inside each other. A bad entry is
skipped; the rest keep running.

`%ProgramData%\CacheJanitor` is writable only by SYSTEM and Administrators (the installer
sets this, and the service re-applies it at every start), since whoever can write
`config.json` decides what the service deletes. `stats.json` beside it holds the lifetime
totals and 7 days of history the tray charts.

## Without a service

Handy for trying it out, or as a pre/post step in a pipeline:

```
CacheJanitor.exe once --dry-run                       # what would it delete, per config.json?
CacheJanitor.exe once                                 # trim everything once and exit
CacheJanitor.exe trim D:\ci-cache 50GB --unit TopLevelEntry --min-age 30 [--exclude "**/*.lock"] [--dry-run] [--verbose]
```

Running in the foreground (`CacheJanitor.exe` with no arguments) behaves exactly like the
service, logging to the console. Set `CACHEJANITOR_DATA` to a folder of your own to run a
test instance against a separate `config.json`; the tray app honours it too.

## Build

Needs the .NET 10 SDK. Both apps publish self-contained, so agents don't need .NET.

```
dotnet test tests/CacheJanitor.Tests
dotnet build installer/CacheJanitor.Installer.wixproj -c Release -p:Version=1.2.3
# -> installer/bin/x64/Release/CacheJanitor-1.2.3-x64.msi
```

`tools/make-icon.ps1` regenerates `assets/CacheJanitor.ico`.

## Things to know

- The folder can briefly go over the limit — a 20 GB download lands before it's trimmed.
  Leave disk headroom of at least your largest single download.
- If everything is newer than `MinAgeMinutes`, in use, or excluded, it logs a warning and
  leaves the folder over the limit rather than break a running job.
- Size counted is file length, not size-on-disk (close enough for large downloads).
- Symlinks/junctions inside the cache are deleted as links; their targets aren't followed.
- "Downloaded" in the tray is estimated from size growth between scans plus evictions, so
  files deleted by something else make it read low.
