# CacheJanitor

Keeps one or more cache folders on a Windows machine under a fixed size by deleting the
least recently used files. Built for CI agents: tools download into the folder as normal
and know nothing about it; CacheJanitor tidies up behind them.

There is no built-in Windows "folder with a max size that evicts old stuff" — FSRM quotas
only *block* writes, which would break your builds — so this runs as a small service.

## How it works

- Watches each folder. Shortly after anything is written (debounce, default 15 s) and also
  every few minutes, it adds up the folder's size.
- If it's over `MaxSize`, it deletes the oldest-used entries until it's down to
  `TrimToPercent` of the limit (default 90%), so it isn't deleting on every new file.
- "Last used" = newest of a file's **last access**, last write and creation time.
- Never deletes anything used in the last `MinAgeMinutes` (default 10) — protects
  in-progress downloads.
- Files a tool has open can't be deleted on Windows, so they're simply skipped. In
  `TopLevelEntry` mode a folder is first renamed aside, which Windows refuses if any file in
  it is open — so a folder is either removed whole or left alone, never half-deleted.

## Important: enable last-access tracking

True LRU needs Windows to update a file's access time when a tool *reads* it. On many
machines that's turned off. Check and enable (admin, then reboot):

```
fsutil behavior query disablelastaccess
fsutil behavior set disablelastaccess 0
```

If it stays off, CacheJanitor still works but evicts by "least recently downloaded"
instead. It logs a warning at startup when the setting is off. NTFS only records access
time to about 1-hour precision, which is fine for this purpose.

## Configure — `appsettings.json` (next to the exe)

```json
{
  "Janitor": {
    "ScanIntervalMinutes": 5,
    "DebounceSeconds": 15,
    "Caches": [
      { "Path": "D:\\ci-cache\\downloads", "MaxSize": "100GB" },
      { "Path": "D:\\ci-cache\\toolchains", "MaxSize": "40GB", "Unit": "TopLevelEntry",
        "MinAgeMinutes": 60, "Exclude": [ "keep-forever" ] }
    ]
  }
}
```

| Setting | Default | Meaning |
|---|---|---|
| `Path` | — | Folder to manage (created if missing). |
| `MaxSize` | — | `750MB`, `100GB`, `1.5TB` or bytes. Binary units (1 GB = 1024³). |
| `TrimToPercent` | 90 | When over the limit, trim down to this % of it. |
| `Unit` | `File` | `File`: evict individual files anywhere in the tree. `TopLevelEntry`: evict each top-level file/folder as a whole (use when a tool unpacks one folder per item and a half-deleted folder would break it). |
| `MinAgeMinutes` | 10 | Never touch anything used more recently than this. |
| `Exclude` | `[]` | Globs relative to `Path` never evicted (`*`, `?`, `**`); matching a folder protects everything in it. |

Restart the service after editing.

## Build & install

Needs the .NET 10 SDK on the build machine (the published exe is self-contained, so CI
agents don't need .NET installed).

```powershell
# as admin, from this folder
.\install-service.ps1                     # publish to C:\Program Files\CacheJanitor + install + start
.\install-service.ps1 -EnableLastAccess   # same, and turn on last-access tracking (reboot after)
.\install-service.ps1 -Uninstall
```

Logs go to Event Viewer → Windows Logs → Application, source `CacheJanitor`.

## Without a service

Handy for trying it out, or as a pre/post step in a pipeline:

```
CacheJanitor.exe once --dry-run                       # what would it delete, per appsettings.json?
CacheJanitor.exe once                                 # trim everything once and exit
CacheJanitor.exe trim D:\ci-cache 50GB --unit TopLevelEntry --min-age 30 [--exclude "**/*.lock"] [--dry-run] [--verbose]
```

Running in the foreground (`CacheJanitor.exe` with no arguments) behaves exactly like the
service, logging to the console.

## Things to know

- The folder can briefly go over the limit — a 20 GB download lands before it's trimmed.
  Leave disk headroom of at least your largest single download.
- If everything is newer than `MinAgeMinutes`, in use, or excluded, it logs a warning and
  leaves the folder over the limit rather than break a running job.
- Size counted is file length, not size-on-disk (close enough for large downloads).
- Symlinks/junctions inside the cache are deleted as links; their targets aren't followed.
