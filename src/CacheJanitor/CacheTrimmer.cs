using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CacheJanitor;

/// <summary>
/// Brings one cache folder back under its size limit by deleting the least recently used entries.
/// Safe to call concurrently; passes on the same folder are serialized.
/// </summary>
public sealed class CacheTrimmer
{
    internal const string TrashPrefix = ".cachejanitor-trash-";

    private readonly CacheFolderOptions _options;
    private readonly ILogger _log;
    private readonly bool _dryRun;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Regex> _excludes;

    public CacheTrimmer(CacheFolderOptions options, ILogger log, bool dryRun = false)
    {
        _options = options;
        _log = log;
        _dryRun = dryRun;
        _excludes = options.Exclude.Select(GlobToRegex).ToList();
        _ = options.MaxBytes; // validate size string early
    }

    public string Root => _options.Path;
    public CacheFolderOptions Options => _options;

    public sealed record Result(
        long BytesBefore,
        long BytesAfter,
        int Evicted,
        int SkippedInUse,
        long Files,
        long Entries,
        DateTime? OldestUsedUtc,
        long[] AgeBytes,
        long[] AgeEntries,
        IReadOnlyList<EvictionRecord> Evictions,
        TimeSpan Duration);

    private sealed record Entry(string FullPath, string RelativePath, bool IsDirectory, long Bytes, long Files, DateTime LastUsedUtc);

    /// <summary>Scans and trims on a thread-pool thread at background I/O priority.</summary>
    public async Task<Result> TrimAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var _ = BackgroundIo.Enter();
                return Trim(ct);
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Result Trim(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var root = new DirectoryInfo(_options.Path);
        if (!root.Exists)
            throw new DirectoryNotFoundException($"Cache folder {_options.Path} does not exist.");

        EmptyTrash(root, ct);

        var entries = Scan(root, ct);
        long total = entries.Sum(e => e.Bytes);
        long max = _options.MaxBytes;
        long target = _options.TargetBytes;
        var now = DateTime.UtcNow;

        var evictions = new List<EvictionRecord>();
        long before = total;
        int inUse = 0;

        if (total <= max || !_options.Enabled)
        {
            _log.LogDebug("{Path}: {Used} of {Max} used, nothing to do.", root.FullName, SizeParser.Format(total), SizeParser.Format(max));
        }
        else
        {
            _log.LogInformation("{Path}: {Used} exceeds limit {Max}; trimming to {Target}.",
                root.FullName, SizeParser.Format(total), SizeParser.Format(max), SizeParser.Format(target));

            var cutoff = now - TimeSpan.FromMinutes(_options.MinAgeMinutes);
            var evicted = new HashSet<Entry>();

            foreach (var entry in entries.OrderBy(e => e.LastUsedUtc))
            {
                if (total <= target) break;
                ct.ThrowIfCancellationRequested();

                if (entry.LastUsedUtc > cutoff)
                    break; // everything after this is newer still

                if (IsExcluded(entry.RelativePath))
                    continue;

                if (!_dryRun && !TryEvict(root, entry, ct))
                {
                    inUse++;
                    continue;
                }

                total -= entry.Bytes;
                evicted.Add(entry);
                evictions.Add(new EvictionRecord { Utc = DateTime.UtcNow, Entry = entry.RelativePath, Bytes = entry.Bytes, LastUsedUtc = entry.LastUsedUtc });
                _log.Log(_dryRun ? LogLevel.Information : LogLevel.Debug, "{Action} {Entry} ({Size}, last used {When:u})",
                    _dryRun ? "[dry run] would evict" : "Evicted", entry.RelativePath, SizeParser.Format(entry.Bytes), entry.LastUsedUtc);
            }

            if (_options.Unit == EvictionUnit.File && !_dryRun)
                RemoveEmptyDirectories(root, cutoff, evicted.Select(e => Path.GetDirectoryName(e.FullPath)!).ToHashSet(StringComparer.OrdinalIgnoreCase), ct);

            entries.RemoveAll(evicted.Contains);

            if (total > target)
            {
                _log.LogWarning("{Path}: still at {Used} after trimming (target {Target}). {InUse} entries were in use; the rest are newer than {MinAge} min or excluded.",
                    root.FullName, SizeParser.Format(total), SizeParser.Format(target), inUse, _options.MinAgeMinutes);
            }
            else
            {
                _log.LogInformation("{Path}: freed {Freed} by evicting {Count} entries; now {Used}.",
                    root.FullName, SizeParser.Format(before - total), evictions.Count, SizeParser.Format(total));
            }
        }

        var ageBytes = new long[AgeBuckets.Count];
        var ageEntries = new long[AgeBuckets.Count];
        foreach (var e in entries)
        {
            int b = AgeBuckets.Of(now - e.LastUsedUtc);
            ageBytes[b] += e.Bytes;
            ageEntries[b]++;
        }

        return new Result(
            before, total, evictions.Count, inUse,
            Files: entries.Sum(e => e.Files),
            Entries: entries.Count,
            OldestUsedUtc: entries.Count == 0 ? null : entries.Min(e => e.LastUsedUtc),
            ageBytes, ageEntries, evictions, clock.Elapsed);
    }

    // ---------- scanning ----------

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,   // .NET does not recurse into junctions/symlinks
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private List<Entry> Scan(DirectoryInfo root, CancellationToken ct)
    {
        var list = new List<Entry>();

        if (_options.Unit == EvictionUnit.File)
        {
            foreach (var f in root.EnumerateFiles("*", Recursive))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Relative(root, f.FullName);
                if (rel.StartsWith(TrashPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryStat(f, out var bytes, out var used)) continue;
                list.Add(new Entry(f.FullName, rel, false, bytes, 1, used));
            }
        }
        else
        {
            foreach (var info in root.EnumerateFileSystemInfos("*", TopLevel))
            {
                ct.ThrowIfCancellationRequested();
                if (info.Name.StartsWith(TrashPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                if (info is FileInfo f)
                {
                    if (TryStat(f, out var bytes, out var used))
                        list.Add(new Entry(f.FullName, f.Name, false, bytes, 1, used));
                }
                else if (info is DirectoryInfo d)
                {
                    long bytes = 0, files = 0;
                    // Not LastUsed(d): a folder's access time changes whenever anyone (including this scan) lists it.
                    var used = d.LastWriteTimeUtc > d.CreationTimeUtc ? d.LastWriteTimeUtc : d.CreationTimeUtc;
                    if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        foreach (var child in d.EnumerateFiles("*", Recursive))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!TryStat(child, out var b, out var u)) continue;
                            bytes += b;
                            files++;
                            if (u > used) used = u;
                        }
                    }
                    list.Add(new Entry(d.FullName, d.Name, true, bytes, files, used));
                }
            }
        }

        return list;
    }

    private static bool TryStat(FileInfo f, out long bytes, out DateTime lastUsedUtc)
    {
        try
        {
            bytes = f.Length;
            lastUsedUtc = LastUsed(f);
            return true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        bytes = 0;
        lastUsedUtc = default;
        return false;
    }

    /// <summary>
    /// "Last used" is the newest of access, write and creation time. Access time only moves on reads if
    /// NTFS last-access updates are enabled (see README); write/creation time cover the rest.
    /// </summary>
    private static DateTime LastUsed(FileSystemInfo i)
    {
        var t = i.LastAccessTimeUtc;
        if (i.LastWriteTimeUtc > t) t = i.LastWriteTimeUtc;
        if (i.CreationTimeUtc > t) t = i.CreationTimeUtc;
        return t;
    }

    // ---------- eviction ----------

    private bool TryEvict(DirectoryInfo root, Entry entry, CancellationToken ct)
    {
        try
        {
            if (!entry.IsDirectory)
            {
                // File.Delete fails if a tool has the file open (without FILE_SHARE_DELETE),
                // which is exactly the protection we want.
                var fi = new FileInfo(entry.FullPath);
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                return true;
            }

            // Move the whole folder aside first. Windows refuses to rename a folder while files inside it
            // are open, so this is an all-or-nothing check that no tool is using it, and a tool never sees
            // a half-deleted folder under its real name.
            var trash = Path.Combine(root.FullName, TrashPrefix + Guid.NewGuid().ToString("N"));
            Directory.Move(entry.FullPath, trash);
            DeleteTree(new DirectoryInfo(trash), ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Skipped {Entry}, probably in use: {Message}", entry.RelativePath, ex.Message);
            return false;
        }
    }

    private void EmptyTrash(DirectoryInfo root, CancellationToken ct)
    {
        foreach (var d in root.EnumerateDirectories(TrashPrefix + "*", TopLevel))
        {
            try { DeleteTree(d, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug("Could not finish deleting {Dir}: {Message}", d.FullName, ex.Message);
            }
        }
    }

    /// <summary>
    /// Deletes a moved-aside folder. A stop request leaves the rest for <see cref="EmptyTrash"/> on the next pass,
    /// since the folder is already out of the tools' way under its trash name.
    /// </summary>
    private static void DeleteTree(DirectoryInfo dir, CancellationToken ct)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete(); // removes the link, never the target's contents
            return;
        }

        // Clear read-only flags (git objects, some package managers) so the recursive delete succeeds.
        foreach (var f in dir.EnumerateFiles("*", Recursive))
        {
            if (ct.IsCancellationRequested) return;
            try { if (f.IsReadOnly) f.IsReadOnly = false; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        dir.Delete(recursive: true);
    }

    /// <summary>
    /// Removes empty folders older than <paramref name="cutoff"/>. A folder this pass evicted files from counts by
    /// its creation time alone, since those deletions just updated its write time.
    /// </summary>
    private void RemoveEmptyDirectories(DirectoryInfo root, DateTime cutoff, HashSet<string> emptiedByUs, CancellationToken ct)
    {
        // Deepest first, so parents become empty after children go.
        var dirs = root.EnumerateDirectories("*", Recursive)
            .Where(d => !Relative(root, d.FullName).StartsWith(TrashPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.FullName.Length)
            .ToList();

        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                d.Refresh();
                if (!d.Exists || d.CreationTimeUtc > cutoff) continue;
                if (d.LastWriteTimeUtc > cutoff && !emptiedByUs.Contains(d.FullName)) continue;
                if (IsExcluded(Relative(root, d.FullName))) continue;
                if (d.EnumerateFileSystemInfos().Any()) continue;
                d.Delete();
                if (d.Parent is { } parent) emptiedByUs.Add(parent.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------- helpers ----------

    private static string Relative(DirectoryInfo root, string fullPath) =>
        Path.GetRelativePath(root.FullName, fullPath).Replace('\\', '/');

    private bool IsExcluded(string relativePath) => _excludes.Any(r => r.IsMatch(relativePath));

    internal static Regex GlobToRegex(string glob)
    {
        var g = glob.Replace('\\', '/').TrimStart('/');
        var sb = new StringBuilder("^");
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                i++;
                if (i + 1 < g.Length && g[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        // A pattern that matches a folder also excludes everything inside it.
        sb.Append("(?:/.*)?$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
