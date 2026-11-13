using System.Security.AccessControl;
using System.Security.Principal;

namespace Wasla.PrintBridge.Hosting;

/// <summary>
/// The access rules of the service's data folder (WAS-61). The service trusts what is in the folder (the server URL and
/// device token, a pending move, the journal), so only SYSTEM, administrators and the service may write it, and only they
/// may read the settings and the token. The <c>logs</c> subfolder is also readable by local users, for Settings →
/// Diagnostics → Open log folder.
/// </summary>
internal static class PrintBridgeDataFolderSecurity
{
    private const string LogsFolderName = "logs";

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Run by an administrator (<c>Wasla.PrintBridge.Service.exe secure-data</c>) after the service is registered:
    /// creates the folders, makes Administrators the owner, replaces the folder's rules (no inheritance from ProgramData)
    /// and resets everything below to inherit them, which removes access a user had through CREATOR OWNER. Idempotent.
    /// </summary>
    public static void Apply(string root, SecurityIdentifier service)
    {
        var rootInfo = Directory.CreateDirectory(root);
        var logsInfo = Directory.CreateDirectory(Path.Combine(root, LogsFolderName));

        var rootSecurity = new DirectorySecurity();
        rootSecurity.SetOwner(Administrators);
        rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in RootRules(service))
            rootSecurity.AddAccessRule(rule);
        rootInfo.SetAccessControl(rootSecurity);

        // Everything below inherits the folder's rules and nothing else.
        foreach (var entry in rootInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry is DirectoryInfo directory)
            {
                var security = new DirectorySecurity();
                security.SetOwner(Administrators);
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                directory.SetAccessControl(security);
            }
            else if (entry is FileInfo file)
            {
                var security = new FileSecurity();
                security.SetOwner(Administrators);
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                file.SetAccessControl(security);
            }
        }

        var logsSecurity = new DirectorySecurity();
        logsSecurity.SetOwner(Administrators);
        logsSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        logsSecurity.AddAccessRule(new FileSystemAccessRule(
            Users,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        logsInfo.SetAccessControl(logsSecurity);
    }

    /// <summary>
    /// Checked by the service before it reads anything from the folder. Returns null when the folder is safe, otherwise a
    /// short reason for the log (no path content beyond the rule that failed).
    /// </summary>
    public static string? Verify(string root, SecurityIdentifier service)
    {
        try
        {
            return VerifyCore(root, service);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            // Fail closed: a folder whose rules the service cannot even read is not a folder it may trust.
            return "the service cannot read the data folder's access rules (" + ex.GetType().Name + ")";
        }
    }

    private static string? VerifyCore(string root, SecurityIdentifier service)
    {
        if (!Directory.Exists(root))
            return "the data folder does not exist (run secure-data)";

        var security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrusted(owner, service))
            return "the data folder is not owned by SYSTEM, Administrators or the service";

        if (!security.AreAccessRulesProtected)
            return "the data folder inherits access rules from its parent";

        if (!OnlyTrustedMayAccess(security, service))
            return "an account other than SYSTEM, Administrators or the service has access to the data folder";

        // The files the service trusts (settings and token, history, a pending move, the journal) must not have rules of
        // their own that let anyone else in.
        foreach (var file in new DirectoryInfo(root).EnumerateFiles())
        {
            var fileSecurity = file.GetAccessControl(AccessControlSections.Access);
            if (!OnlyTrustedMayAccess(fileSecurity, service))
                return "an account other than SYSTEM, Administrators or the service has access to a file in the data folder";
        }

        return null;
    }

    private static bool OnlyTrustedMayAccess(FileSystemSecurity security, SecurityIdentifier service)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            if (rule.IdentityReference is not SecurityIdentifier sid || !IsTrusted(sid, service))
                return false;
        }

        return true;
    }

    private static bool IsTrusted(SecurityIdentifier sid, SecurityIdentifier service) =>
        sid == LocalSystem || sid == Administrators || sid == service;

    private static IEnumerable<FileSystemAccessRule> RootRules(SecurityIdentifier service)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        yield return new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow);
        yield return new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow);
        if (service != LocalSystem && service != Administrators)
            yield return new FileSystemAccessRule(service, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow);
    }
}
