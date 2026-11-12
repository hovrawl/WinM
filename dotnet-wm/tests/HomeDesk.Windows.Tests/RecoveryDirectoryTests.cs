using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace HomeDesk.Windows.Tests;

[SupportedOSPlatform("windows10.0.15063")]
public sealed class RecoveryDirectoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JournalDirectoryIsUserOwnedAndPrivateWithoutRequiringOwnershipChanges(bool existingDirectory)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RecoveryAcl." + Guid.NewGuid().ToString("N"));
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var directory = new DirectoryInfo(path);
        try
        {
            if (existingDirectory)
            {
                var restricted = new DirectorySecurity();
                restricted.SetOwner(user);
                restricted.SetAccessRuleProtection(true, false);
                restricted.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.TakeOwnership,
                    AccessControlType.Deny));
                restricted.AddAccessRule(new FileSystemAccessRule(user,
                    FileSystemRights.FullControl & ~FileSystemRights.TakeOwnership, AccessControlType.Allow));
                directory.Create(restricted);
            }

            WindowsRecovery.PrepareJournalDirectory(path);

            var actual = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            Assert.Equal(user, actual.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(actual.AreAccessRulesProtected);
            var rule = Assert.Single(actual.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
            Assert.Equal(user, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
