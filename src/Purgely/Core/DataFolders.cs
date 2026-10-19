using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Purgely.Core;

/// <summary>
/// Purgely's own data folders, and the protection they need.
/// Undo packages tell Purgely (running as administrator) what to put back – a registry file to import, a folder to
/// move. If a normal user could write there, they could plant a "package" that makes the administrator run their
/// changes. So C:\ProgramData\Purgely and every "X:\Purgely Undo" folder are writable only by administrators and SYSTEM.
/// </summary>
public static class DataFolders
{
    /// <summary>C:\ProgramData\Purgely – Undo packages and the Timeline.</summary>
    public static string Shared => Timeline.DataFolder;
    /// <summary>%LOCALAPPDATA%\Purgely – settings and per-user state.</summary>
    public static string PerUser => AppSettings.Folder;

    /// <summary>Every "X:\Purgely Undo" folder that exists on a fixed drive.</summary>
    public static List<string> DriveUndoFolders()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var folder = Path.Combine(d.RootDirectory.FullName, Cleaner.DriveUndoFolderName);
                if (Directory.Exists(folder)) list.Add(folder);
            }
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Applies the administrators-only rule to the shared data folders. Safe to call often; quiet on failure.</summary>
    public static void Protect()
    {
        if (!IsAdministrator()) return;
        try { Directory.CreateDirectory(Shared); } catch (Exception) { return; }
        ProtectFolder(Shared);
        foreach (var folder in DriveUndoFolders()) ProtectFolder(folder);
    }

    /// <summary>Administrators and SYSTEM: full control. Users: read only. Nothing inherited from the parent.</summary>
    public static void ProtectFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || FileMover.IsReparsePoint(folder)) return;
            // Already protected: don't rewrite it (that would walk the whole backup tree on every start).
            if (info.GetAccessControl().AreAccessRulesProtected && IsAdminOnly(folder)) return;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            info.SetAccessControl(security);
        }
        catch (Exception) { /* e.g. a drive without NTFS permissions (FAT/exFAT) – nothing to protect */ }
    }

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool IsTrusted(IdentityReference? who)
    {
        if (who is not SecurityIdentifier sid) return false;
        return sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
               sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller
    }

    /// <summary>
    /// True when only administrators/SYSTEM own and can change <paramref name="path"/> (a folder or a file).
    /// False when unsure – e.g. on drives without permissions (FAT/exFAT) or when the security can't be read.
    /// </summary>
    public static bool IsAdminOnly(string path, bool checkOwner = true)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            if (checkOwner && !IsTrusted(security.GetOwner(typeof(SecurityIdentifier)))) return false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & WriteRights) == 0) continue;
                if (!IsTrusted(rule.IdentityReference)) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// A "X:\Purgely Undo" folder may hold backups only if it is protected: Purgely created it, or could take it over and
    /// lock it. A folder someone else made first and that can't be locked is never used (backups then go to C:).
    /// </summary>
    public static bool PrepareDriveUndoFolder(string folder)
    {
        if (Directory.Exists(folder) && FileMover.IsReparsePoint(folder)) return false;
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        if (!IsAdminOnly(folder)) ProtectFolder(folder);
        return IsAdminOnly(folder);
    }

    /// <summary>Makes the Administrators group the owner of a file or folder Purgely wrote (some PCs make the creating user the owner).</summary>
    public static void OwnByAdministrators(string path)
    {
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                var security = dir.GetAccessControl(AccessControlSections.Owner);
                if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier o && o == admins) return;
                security.SetOwner(admins);
                dir.SetAccessControl(security);
            }
            else
            {
                var file = new FileInfo(path);
                var security = file.GetAccessControl(AccessControlSections.Owner);
                if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier o && o == admins) return;
                security.SetOwner(admins);
                file.SetAccessControl(security);
            }
        }
        catch (Exception) { }
    }

    public static bool IsAdministrator()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ who is signed in
    /// <summary>
    /// The account signed in to this Windows session (the one using the desktop). When an administrator's password
    /// was typed into the UAC prompt of a standard user, Purgely runs as that administrator – and "your" settings,
    /// AppData and Run entries are the administrator's, not the signed-in user's.
    /// </summary>
    public static (string Running, string SignedIn)? DifferentUser()
    {
        try
        {
            var running = WindowsIdentity.GetCurrent().Name; // DOMAIN\user
            var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            var user = QuerySession(session, WtsUserName);
            var domain = QuerySession(session, WtsDomainName);
            if (string.IsNullOrEmpty(user)) return null;
            var signedIn = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
            var runningUser = running.Contains('\\') ? running[(running.IndexOf('\\') + 1)..] : running;
            return string.Equals(runningUser, user, StringComparison.OrdinalIgnoreCase) ? null : (running, signedIn);
        }
        catch (Exception) { return null; }
    }

    private const int WtsUserName = 5, WtsDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static string? QuerySession(int session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _) || buffer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }
}
