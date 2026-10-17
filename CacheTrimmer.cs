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

    public sealed record Result(long BytesBefore, long BytesAfter, int Evicted, int SkippedInUse);

    private sealed record Entry(string FullPath, string RelativePath, bool IsDirectory, long Bytes, DateTime LastUsedUtc);

    public async Task<Result> TrimAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return Trim(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Result Trim(CancellationToken ct)
    {
        var root = new DirectoryInfo(_options.Path);
        if (!root.Exists)
        {
            _log.LogWarning("Cache folder {Path} does not exist; skipping.", _options.Path);
            return new Result(0, 0, 0, 0);
        }

        EmptyTrash(root);

        var entries = Scan(root, ct);
        long total = entries.Sum(e => e.Bytes);
        long max = _options.MaxBytes;
        long target = _options.TargetBytes;

        if (total <= max)
        {
            _log.LogDebug("{Path}: {Used} of {Max} used, nothing to do.", root.FullName, SizeParser.Format(total), SizeParser.Format(max));
            return new Result(total, total, 0, 0);
        }

        _log.LogInformation("{Path}: {Used} exceeds limit {Max}; trimming to {Target}.",
            root.FullName, SizeParser.Format(total), SizeParser.Format(max), SizeParser.Format(target));

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(_options.MinAgeMinutes);
        long before = total;
        int evicted = 0, inUse = 0;

        foreach (var entry in entries.OrderBy(e => e.LastUsedUtc))
        {
            if (total <= target) break;
            ct.ThrowIfCancellationRequested();

            if (entry.LastUsedUtc > cutoff)
                break; // everything after this is newer still

            if (IsExcluded(entry.RelativePath))
                continue;

            if (_dryRun)
            {
                _log.LogInformation("[dry run] would evict {Entry} ({Size}, last used {When:u})",
                    entry.RelativePath, SizeParser.Format(entry.Bytes), entry.LastUsedUtc);
                total -= entry.Bytes;
                evicted++;
                continue;
            }

            if (TryEvict(root, entry))
            {
                total -= entry.Bytes;
                evicted++;
                _log.LogInformation("Evicted {Entry} ({Size}, last used {When:u})",
                    entry.RelativePath, SizeParser.Format(entry.Bytes), entry.LastUsedUtc);
            }
            else
            {
                inUse++;
            }
        }

        if (_options.Unit == EvictionUnit.File && !_dryRun)
            RemoveEmptyDirectories(root, cutoff);

        if (total > target)
        {
            _log.LogWarning("{Path}: still at {Used} after trimming (target {Target}). {InUse} entries were in use; the rest are newer than {MinAge} min or excluded.",
                root.FullName, SizeParser.Format(total), SizeParser.Format(target), inUse, _options.MinAgeMinutes);
        }
        else
        {
            _log.LogInformation("{Path}: freed {Freed} by evicting {Count} entries; now {Used}.",
                root.FullName, SizeParser.Format(before - total), evicted, SizeParser.Format(total));
        }

        return new Result(before, total, evicted, inUse);
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
                list.Add(new Entry(f.FullName, rel, false, bytes, used));
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
                        list.Add(new Entry(f.FullName, f.Name, false, bytes, used));
                }
                else if (info is DirectoryInfo d)
                {
                    long bytes = 0;
                    // Not LastUsed(d): a folder's access time changes whenever anyone (including this scan) lists it.
                    var used = d.LastWriteTimeUtc > d.CreationTimeUtc ? d.LastWriteTimeUtc : d.CreationTimeUtc;
                    if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        foreach (var child in d.EnumerateFiles("*", Recursive))
                        {
                            if (!TryStat(child, out var b, out var u)) continue;
                            bytes += b;
                            if (u > used) used = u;
                        }
                    }
                    list.Add(new Entry(d.FullName, d.Name, true, bytes, used));
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

    private bool TryEvict(DirectoryInfo root, Entry entry)
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
            DeleteTree(new DirectoryInfo(trash));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Skipped {Entry}, probably in use: {Message}", entry.RelativePath, ex.Message);
            return false;
        }
    }

    private void EmptyTrash(DirectoryInfo root)
    {
        foreach (var d in root.EnumerateDirectories(TrashPrefix + "*", TopLevel))
        {
            try { DeleteTree(d); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug("Could not finish deleting {Dir}: {Message}", d.FullName, ex.Message);
            }
        }
    }

    private static void DeleteTree(DirectoryInfo dir)
    {
        // Clear read-only flags (git objects, some package managers) so the recursive delete succeeds.
        foreach (var f in dir.EnumerateFiles("*", Recursive))
        {
            try { if (f.IsReadOnly) f.IsReadOnly = false; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        dir.Delete(recursive: true);
    }

    private void RemoveEmptyDirectories(DirectoryInfo root, DateTime cutoff)
    {
        // Deepest first, so parents become empty after children go.
        var dirs = root.EnumerateDirectories("*", Recursive)
            .Where(d => !Relative(root, d.FullName).StartsWith(TrashPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.FullName.Length)
            .ToList();

        foreach (var d in dirs)
        {
            try
            {
                d.Refresh();
                if (!d.Exists || d.LastWriteTimeUtc > cutoff || d.CreationTimeUtc > cutoff) continue;
                if (d.EnumerateFileSystemInfos().Any()) continue;
                d.Delete();
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
