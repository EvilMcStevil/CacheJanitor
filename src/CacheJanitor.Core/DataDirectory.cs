using System.Security.AccessControl;
using System.Security.Principal;

namespace CacheJanitor;

/// <summary>
/// Locks down %ProgramData%\CacheJanitor. Whoever can write config.json decides what LocalSystem deletes, and
/// ProgramData's default ACL lets any user create files in a new subfolder and own them, so the folder and its
/// files are reset to SYSTEM and Administrators full control, everyone else read-only.
/// </summary>
public static class DataDirectory
{
    /// <summary>Creates and secures the folder. Returns a warning if the permissions could not be set.</summary>
    public static string? EnsureSecured()
    {
        var dir = AppPaths.DataDirectory;
        Directory.CreateDirectory(dir);
        if (!IsElevated())
            return null;

        try
        {
            var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(security);

            // Files keep explicit entries a user may have added; give each the folder's permissions only.
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var fs = new FileSecurity();
                fs.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                fs.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new FileInfo(file).SetAccessControl(fs);
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return $"Could not secure {dir}: {ex.Message}";
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
