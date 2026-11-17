using System.Runtime.Versioning;
using System.Security.AccessControl;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Restrictions;

/// <summary>
/// Decides whether the restrictions file was put there by an administrator. A file that a
/// standard user can create or rewrite says nothing about what the organisation allows.
/// </summary>
public static class RestrictionFileTrust
{
    public static Func<string, string?>? TestOverride { get; set; }

    /// <summary>Null when only administrators can change the file, else the reason it is not trusted.</summary>
    public static string? WhyUntrusted(string path)
    {
        if (TestOverride is { } test)
            return test(path);

        try
        {
            path = Path.GetFullPath(path);
            return OperatingSystem.IsWindows() ? WhyUntrustedOnWindows(path) : WhyUntrustedOnUnix(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return $"Its permissions could not be read: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? WhyUntrustedOnWindows(string path)
    {
        var file = new FileInfo(path);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return "The file is a link.";

        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
        var descriptor = new RawSecurityDescriptor(file.GetAccessControl(sections).GetSecurityDescriptorBinaryForm(), 0);
        if (!PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor, PolicyElevationAccessPolicy.FileControlMask, out string? failure))
            return failure;

        // Standard users may add files to a folder under ProgramData, which does not let them
        // replace this one. What matters going up is whether a folder can be deleted, renamed or
        // re-permissioned by someone who is not an administrator.
        for (DirectoryInfo? folder = file.Directory; folder is not null; folder = folder.Parent)
        {
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"The folder {folder.FullName} is a link.";

            descriptor = new RawSecurityDescriptor(folder.GetAccessControl(sections).GetSecurityDescriptorBinaryForm(), 0);
            if (!PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                    descriptor, PolicyElevationAccessPolicy.AncestorControlMask, out failure))
                return $"{folder.FullName}: {failure}";
        }

        return null;
    }

    [UnsupportedOSPlatform("windows")]
    private static string? WhyUntrustedOnUnix(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            return "The file is a link.";

        // Where no level is writable by its group or by others, only root or an owner root chose
        // can have put the file there.
        const UnixFileMode writableByOthers = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetUnixFileMode(current) & writableByOthers) != 0)
                return $"{current} can be modified by users who are not administrators.";
        }

        return null;
    }
}
