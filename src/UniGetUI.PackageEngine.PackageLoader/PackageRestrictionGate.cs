using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Restrictions;

namespace UniGetUI.PackageEngine.PackageLoader
{
    /// <summary>
    /// Applies the administrator's package restrictions to a package. What is already installed
    /// always passes, so it can still be updated, reinstalled and uninstalled.
    /// </summary>
    public static class PackageRestrictionGate
    {
        /// <summary>Whether packages are being filtered at all.</summary>
        public static bool IsActive => PackageRestrictions.Current.IsActive;

        /// <summary>Whether a package may be listed among the packages that can be installed.</summary>
        public static bool IsVisible(IPackage package)
        {
            RestrictionSet restrictions = PackageRestrictions.Current;
            return !restrictions.IsActive || restrictions.Allows(package) || IsListedAsInstalled(package);
        }

        /// <summary>
        /// Null when the package may be installed, else the reason to show the user.
        /// </summary>
        /// <param name="askManager">
        /// Ask the package manager itself when the installed list is not loaded, as in a headless
        /// session. It is slow, and only done for a package that would otherwise be refused.
        /// </param>
        public static string? WhyInstallRefused(IPackage package, bool askManager = false)
        {
            RestrictionSet restrictions = PackageRestrictions.Current;
            if (!restrictions.IsActive || restrictions.Allows(package) || IsListedAsInstalled(package))
                return null;

            if (askManager && !InstalledListIsReliable(package.Manager) && ManagerReportsInstalled(package))
                return null;

            return restrictions.State is RestrictionState.Invalid
                ? CoreTools.Translate(
                    "{0} cannot be installed: the package restrictions set by your organization could not be read.",
                    package.Name)
                : CoreTools.Translate(
                    "{0} cannot be installed: it is not among the packages allowed by your organization.",
                    package.Name);
        }

        private static bool IsListedAsInstalled(IPackage package) =>
            InstalledPackagesLoader.Instance is { } installed
            && installed.GetEquivalentPackages(package).Count > 0;

        private static bool InstalledListIsReliable(IPackageManager manager) =>
            InstalledPackagesLoader.Instance is { IsLoaded: true } && !manager.LastInstalledListingFailed;

        private static bool ManagerReportsInstalled(IPackage package)
        {
            foreach (IPackage installed in package.Manager.GetInstalledPackages())
            {
                if (installed.IsSamePackageAs(package))
                    return true;
            }
            return false;
        }
    }
}
