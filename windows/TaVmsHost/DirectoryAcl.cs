using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TaVmsHost;

// The keys and tokens under <data>\distributed are the machine's credentials, and %ProgramData%
// lets every local user read what is created there. The installer does the same for config and
// dapr (Install-TaVms.ps1, Grant-Access -Private); these directories are made by TaVmsHost itself,
// by hand as well as from the installer, so it closes them here.
internal static class DirectoryAcl
{
  // Administrators and SYSTEM in full; LocalService, which the service and its children run as,
  // reads when it needs to. Nothing inherited from above.
  public static void Restrict(string path, bool serviceReads)
  {
    Directory.CreateDirectory(path);
    if (OperatingSystem.IsWindows()) RestrictOnWindows(path, serviceReads);
  }

  [SupportedOSPlatform("windows")]
  private static void RestrictOnWindows(string path, bool serviceReads)
  {
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    void Allow(WellKnownSidType who, FileSystemRights rights) => security.AddAccessRule(new FileSystemAccessRule(
      new SecurityIdentifier(who, null), rights,
      InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
    Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
    if (serviceReads) Allow(WellKnownSidType.LocalServiceSid, FileSystemRights.ReadAndExecute);
    new DirectoryInfo(path).SetAccessControl(security);
  }
}
