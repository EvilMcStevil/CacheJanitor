namespace CacheJanitor;

/// <summary>
/// Refuses folders that must never be managed. The service runs as LocalSystem and deletes whatever it finds
/// in a cache folder, so a mistyped path such as "C:\" or "C:\Users" must not be accepted.
/// </summary>
public static class PathGuard
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    /// <summary>Null if <paramref name="path"/> can be managed, otherwise the reason it cannot.</summary>
    public static string? Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "a folder path is required.";

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path.Trim()))
                return "the path must be absolute, e.g. D:\\ci-cache.";
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "the path is not valid.";
        }

        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            return "a drive or share root cannot be a cache.";

        foreach (var (protectedDir, exact) in ProtectedFolders())
        {
            if (string.Equals(full, protectedDir, StringComparison.OrdinalIgnoreCase))
                return $"{protectedDir} is a system folder.";
            if (!exact && IsInside(full, protectedDir))
                return $"folders inside {protectedDir} cannot be caches.";
        }
        if (IsInside(full, AppPaths.DataDirectory) || string.Equals(full, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase))
            return "CacheJanitor's own data folder cannot be a cache.";
        return null;
    }

    /// <summary>True if <paramref name="child"/> is strictly below <paramref name="parent"/>.</summary>
    public static bool IsInside(string child, string parent) =>
        child.Length > parent.Length + 1
        && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && (child[parent.Length] == Path.DirectorySeparatorChar || parent.EndsWith(Path.DirectorySeparatorChar));

    /// <summary>
    /// Folders that are refused outright. When <c>exact</c> is true only the folder itself is refused and its
    /// subfolders are allowed (a cache under a user's profile, say); otherwise everything below it is refused too.
    /// </summary>
    private static IEnumerable<(string Dir, bool Exact)> ProtectedFolders()
    {
        (Environment.SpecialFolder Folder, bool Exact)[] folders =
        [
            (Environment.SpecialFolder.Windows, false),
            (Environment.SpecialFolder.ProgramFiles, false),
            (Environment.SpecialFolder.ProgramFilesX86, false),
            (Environment.SpecialFolder.CommonApplicationData, true),
            (Environment.SpecialFolder.UserProfile, true),
        ];
        foreach (var (folder, exact) in folders)
        {
            var dir = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(dir))
                yield return (Path.TrimEndingDirectorySeparator(dir), exact);
        }

        // C:\Users. Found through %PUBLIC% because LocalSystem's own profile lives under C:\Windows.
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        var users = string.IsNullOrEmpty(publicDir) ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(publicDir));
        if (!string.IsNullOrEmpty(users))
            yield return (Path.TrimEndingDirectorySeparator(users), true);
    }
}
