using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Restrictions;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>An install the administrator does not allow is refused whatever started it.</summary>
[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class PackageRestrictionOperationTests : IDisposable
{
    public PackageRestrictionOperationTests()
    {
        PackageRestrictions.TestOverride = () =>
            new RestrictionSet(RestrictionState.Enforced, RestrictionOrigin.Registry);
    }

    public void Dispose()
    {
        PackageRestrictions.TestOverride = null;
    }

    [Fact]
    public async Task AnInstallOutsideTheAllowedPackagesIsRefusedBeforeItStarts()
    {
        var manager = new PackageManagerBuilder().WithName("Alpha").WithDisplayName("Alpha").Build();
        _ = new InstalledPackagesLoader([manager]);
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.App").WithName("Contoso App").Build();

        using var operation = new InstallPackageOperation(package, new InstallOptions(), IgnoreParallelInstalls: true);
        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains(
            operation.GetOutput(),
            line => line.Item1.Contains("Contoso App") && line.Item1.Contains("allowed by your organization"));
    }
}
