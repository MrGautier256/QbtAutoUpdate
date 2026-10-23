using System.Security.AccessControl;
using System.Security.Principal;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Platform;

/// <summary>
/// Access rules of %ProgramData%\QbtAutoUpdate (spec §4.1). Each folder gets an explicit, protected
/// list (no inheritance from ProgramData, whose defaults let users create folders and files).
/// </summary>
public static class Acl
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    private const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Creates (or repairs) the machine data folders with their access rules.</summary>
    public static void SecureMachineData(AppPaths paths)
    {
        // Root, config.json, state.json, logs: administrators and SYSTEM write, users read.
        Apply(paths.MachineDataDir, ForMachineData());
        Directory.CreateDirectory(paths.MachineLogsDir);
        Apply(paths.WorkDir, ForWork());
        Apply(paths.InboxDir, ForInbox());
    }

    public static DirectorySecurity ForMachineData() => Protected(
        Rule(System, FileSystemRights.FullControl, All),
        Rule(Administrators, FileSystemRights.FullControl, All),
        Rule(Users, FileSystemRights.ReadAndExecute, All));

    /// <summary>SYSTEM and administrators only: the verified copy of the installer lives here.</summary>
    public static DirectorySecurity ForWork() => Protected(
        Rule(System, FileSystemRights.FullControl, All),
        Rule(Administrators, FileSystemRights.FullControl, All));

    /// <summary>
    /// Users may create files (not folders), and only their creator may touch a file afterwards, so
    /// users can neither read nor replace each other's requests.
    /// </summary>
    public static DirectorySecurity ForInbox() => Protected(
        Rule(System, FileSystemRights.FullControl, All),
        Rule(Administrators, FileSystemRights.FullControl, All),
        Rule(Users, FileSystemRights.CreateFiles | FileSystemRights.Traverse | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize, InheritanceFlags.None),
        new FileSystemAccessRule(CreatorOwner, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));

    /// <summary>
    /// True when a non-administrator group could write in <paramref name="directory"/>. The SYSTEM task
    /// refuses to run a binary from such a folder.
    /// </summary>
    public static bool IsWritableByNonAdmins(string directory)
    {
        const FileSystemRights Write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
                                       | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                                       | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        SecurityIdentifier[] untrusted =
        [
            Users, CreatorOwner,
            new(WellKnownSidType.AuthenticatedUserSid, null),
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.InteractiveSid, null),
        ];

        AuthorizationRuleCollection rules = new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        return rules.OfType<FileSystemAccessRule>().Any(r =>
            r.AccessControlType == AccessControlType.Allow
            && untrusted.Contains(r.IdentityReference)
            && (r.FileSystemRights & Write) != 0
            // CREATOR OWNER inherit-only entries only apply to things the owner creates, not to this folder's files.
            && !(r.IdentityReference.Equals(CreatorOwner) && r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
    }

    /// <summary>
    /// True for a real folder (not a junction or symbolic link) owned by SYSTEM or Administrators.
    /// A folder created beforehand by a user, or redirected elsewhere, must never receive SYSTEM's writes.
    /// </summary>
    public static bool IsTrustedDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        IdentityReference? owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        return owner is not null && (owner.Equals(System) || owner.Equals(Administrators));
    }

    private static void Apply(string directory, DirectorySecurity security)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            info.Create(security);
        else
            info.SetAccessControl(security);
    }

    private static DirectorySecurity Protected(params FileSystemAccessRule[] rules)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Administrators);
        foreach (FileSystemAccessRule rule in rules)
            security.AddAccessRule(rule);
        return security;
    }

    private static FileSystemAccessRule Rule(SecurityIdentifier who, FileSystemRights rights, InheritanceFlags inheritance) =>
        new(who, rights, inheritance, PropagationFlags.None, AccessControlType.Allow);
}
