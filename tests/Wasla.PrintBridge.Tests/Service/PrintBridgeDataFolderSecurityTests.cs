using System.Security.AccessControl;
using System.Security.Principal;
using Wasla.PrintBridge.Hosting;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-61: the service checks its data folder's access rules before it trusts anything in it. These tests run the check
/// against temporary folders of this user, who stands in for the service account. Applying the rules needs an
/// administrator (it makes Administrators the owner) and is exercised by <see cref="PrintBridgeWindowsServiceTests"/>.
/// </summary>
public sealed class PrintBridgeDataFolderSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wasla-pb-acl-" + Guid.NewGuid().ToString("N"));
    private readonly SecurityIdentifier _service;

    public PrintBridgeDataFolderSecurityTests()
    {
        using var self = WindowsIdentity.GetCurrent();
        _service = self.User!;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        // Give the folder back to this user so it can be deleted whatever a test set.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(_service, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_root).SetAccessControl(security);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AFolderOnlyTheServiceSystemAndAdministratorsMayUse_IsSafe()
    {
        CreateProtected();
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{}");

        Assert.Null(PrintBridgeDataFolderSecurity.Verify(_root, _service));
    }

    [Fact]
    public void AMissingFolder_IsNotSafe()
    {
        Assert.NotNull(PrintBridgeDataFolderSecurity.Verify(_root, _service));
    }

    [Fact]
    public void AFolderThatInheritsProgramDataRules_IsNotSafe()
    {
        // What an installation before WAS-61 has: every local user can read the token and create files.
        Directory.CreateDirectory(_root);

        Assert.Contains("inherits", PrintBridgeDataFolderSecurity.Verify(_root, _service));
    }

    [Fact]
    public void LocalUsersReadingTheFolder_IsNotSafe()
    {
        CreateProtected(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        Assert.Contains("an account other than", PrintBridgeDataFolderSecurity.Verify(_root, _service), StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsFileWithRulesOfItsOwnForOthers_IsNotSafe()
    {
        CreateProtected();
        var settings = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(settings, "{}");
        var security = new FileInfo(settings).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(settings).SetAccessControl(security);

        Assert.Contains("a file", PrintBridgeDataFolderSecurity.Verify(_root, _service), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherOwner_IsNotSafe()
    {
        CreateProtected();
        var otherService = new SecurityIdentifier("S-1-5-80-1234567890-1234567890-1234567890-1234567890-1234567890");

        // Owned by this user (or, elevated, by Administrators, whose rule is fine) with this user's rule: either way an
        // account that is not the service has access.
        Assert.NotNull(PrintBridgeDataFolderSecurity.Verify(_root, otherService));
    }

    private void CreateProtected(params FileSystemAccessRule[] extra)
    {
        var info = Directory.CreateDirectory(_root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(_service, FileSystemRights.Modify | FileSystemRights.ChangePermissions, inherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var rule in extra)
            security.AddAccessRule(rule);
        info.SetAccessControl(security);
    }
}
