using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CacheJanitor;

/// <summary>
/// LRU only works if Windows updates a file's "last access" time when tools read it.
/// NTFS can have that switched off (NtfsDisableLastAccessUpdate), in which case we can only see
/// creation/write times and the policy degrades to "least recently downloaded".
/// </summary>
public static class LastAccessCheck
{
    public static void WarnIfDisabled(ILogger log)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            var raw = key?.GetValue("NtfsDisableLastAccessUpdate");
            if (raw is null) return;
            uint value = unchecked((uint)Convert.ToInt32(raw));
            // Low bit set = updates disabled (0x1 user-managed, 0x80000001 system-managed).
            if ((value & 1) == 1)
            {
                log.LogWarning(
                    "NTFS last-access updates are DISABLED (NtfsDisableLastAccessUpdate=0x{Value:X8}). " +
                    "Reads won't refresh a file's age, so eviction is by download/write time, not true LRU. " +
                    "To enable, run as admin: fsutil behavior set disablelastaccess 0 (then reboot).",
                    value);
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read last-access setting.");
        }
    }
}
