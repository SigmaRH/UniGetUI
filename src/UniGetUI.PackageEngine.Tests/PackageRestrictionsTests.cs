using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Restrictions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>The administrator's package restrictions: rules, where they are read from, and the export.</summary>
public sealed class PackageRestrictionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        nameof(PackageRestrictionsTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly TestPackageManager _manager;

    public PackageRestrictionsTests()
    {
        Directory.CreateDirectory(_directory);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_directory, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_directory, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
        Settings.ResetSettings();
        _manager = new PackageManagerBuilder().WithName("Alpha").WithDisplayName("Alpha Manager").Build();

        // Neither the registry nor the permissions of this machine may decide a test.
        RestrictionRegistryReader.Source = () => null;
        RestrictionFileTrust.TestOverride = _ => null;
        PackageRestrictions.TestFilePathOverride = Path.Combine(_directory, PackageRestrictions.FileName);
    }

    public void Dispose()
    {
        RestrictionRegistryReader.Source = null;
        RestrictionFileTrust.TestOverride = null;
        PackageRestrictions.TestFilePathOverride = null;
        PackageRestrictions.TestOverride = null;
        PackageRestrictions.Invalidate();
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private Package PackageWithId(string id) =>
        new PackageBuilder().WithManager(_manager).WithId(id).Build();

    private static RestrictionSet Enforced(params string[] lines) =>
        new(RestrictionState.Enforced, RestrictionOrigin.Registry, lines.Select(line => RestrictionRule.ParseLine(line)!));

    private static void WriteFile(string json) => File.WriteAllText(PackageRestrictions.FilePath, json);

    [Fact]
    public void WithoutAConfigurationEveryPackageIsAllowed()
    {
        Assert.Equal(RestrictionState.NotConfigured, PackageRestrictions.Load().State);
        Assert.True(RestrictionSet.NotConfigured.Allows(PackageWithId("Contoso.App")));
    }

    [Fact]
    public void AnEmptyListAllowsNothing()
    {
        Assert.False(Enforced().Allows(PackageWithId("Contoso.App")));
    }

    [Fact]
    public void ARuleAllowsAManagerASourceOrAPackage()
    {
        Package package = PackageWithId("Contoso.App");
        string source = package.Source.Name;

        Assert.True(Enforced("Alpha").Allows(package));
        Assert.True(Enforced($"Alpha|{source}").Allows(package));
        Assert.True(Enforced($"Alpha|{source}|Contoso.App").Allows(package));
        Assert.True(Enforced("Alpha|*|Contoso.App").Allows(package));

        Assert.False(Enforced("Beta").Allows(package));
        Assert.False(Enforced("Alpha|another-source").Allows(package));
        Assert.False(Enforced($"Alpha|{source}|Contoso.Other").Allows(package));
    }

    [Fact]
    public void AManagerIsNamedByItsIdItsNameOrItsDisplayName()
    {
        Package package = PackageWithId("Contoso.App");

        Assert.True(Enforced(_manager.Id).Allows(package));
        Assert.True(Enforced("alpha").Allows(package));
        Assert.True(Enforced("ALPHA MANAGER").Allows(package));
    }

    [Fact]
    public void IdsIgnoreCaseAndMayUseWildcards()
    {
        Assert.True(Enforced("Alpha|*|contoso.app").Allows(PackageWithId("Contoso.App")));
        Assert.True(Enforced("Alpha|*|Contoso.*").Allows(PackageWithId("contoso.Tools.Cli")));
        Assert.True(Enforced("Alpha|*|*.Cli").Allows(PackageWithId("Contoso.Tools.Cli")));
        Assert.True(Enforced("Alpha|*|Contoso.*.Cli").Allows(PackageWithId("Contoso.Tools.Cli")));

        Assert.False(Enforced("Alpha|*|Contoso.*").Allows(PackageWithId("Fabrikam.Contoso.App")));
        Assert.False(Enforced("Alpha|*|Contoso.*.Cli").Allows(PackageWithId("Contoso.Cli")));
    }

    [Theory]
    [InlineData("npm|npm|@scope/name", "npm", "npm", "@scope/name")]
    [InlineData("vcpkg|*|zlib:x64-windows", "vcpkg", null, "zlib:x64-windows")]
    [InlineData("scoop|main", "scoop", "main", null)]
    [InlineData(" pip ", "pip", null, null)]
    [InlineData("winget|winget|*", "winget", "winget", null)]
    [InlineData("alpha|source|an|id|with|bars", "alpha", "source", "an|id|with|bars")]
    public void ARuleLineKeepsWhateverFollowsTheSecondSeparatorAsTheId(
        string line, string manager, string? source, string? id)
    {
        RestrictionRule? rule = RestrictionRule.ParseLine(line);

        Assert.NotNull(rule);
        Assert.Equal(manager, rule.Manager);
        Assert.Equal(source, rule.Source);
        Assert.Equal(id, rule.Id);
        Assert.Equal(rule, RestrictionRule.ParseLine(rule.ToLine()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("|winget|Contoso.App")]
    [InlineData("*|winget|Contoso.App")]
    public void ARuleLineWithoutAManagerIsNotARule(string line)
    {
        Assert.Null(RestrictionRule.ParseLine(line));
    }

    [Fact]
    public void TheFileIsAppliedWhenNoPolicyIsConfigured()
    {
        WriteFile("""
            {
              // Comments and trailing commas are accepted: administrators edit this by hand.
              "version": 1,
              "enabled": true,
              "allow": [
                { "manager": "Alpha", "id": "Contoso.App", "name": "Contoso App", "version": "1.0" },
                { "id": "No.Manager" },
              ],
            }
            """);

        RestrictionSet restrictions = PackageRestrictions.Load();

        Assert.Equal(RestrictionState.Enforced, restrictions.State);
        Assert.Equal(RestrictionOrigin.File, restrictions.Origin);
        Assert.Equal(new RestrictionRule("Alpha", null, "Contoso.App"), Assert.Single(restrictions.Rules));
        Assert.True(restrictions.Allows(PackageWithId("Contoso.App")));
        Assert.False(restrictions.Allows(PackageWithId("Contoso.Other")));
    }

    [Fact]
    public void AFileSavedWithAByteOrderMarkIsRead()
    {
        File.WriteAllText(
            PackageRestrictions.FilePath,
            """{ "version": 1, "allow": [ { "manager": "Alpha" } ] }""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(RestrictionState.Enforced, PackageRestrictions.Load().State);
        Assert.Single(AllowListExport.Read(PackageRestrictions.FilePath).Allow!);
    }

    [Fact]
    public void AFileThatTurnsRestrictionsOffAllowsEverything()
    {
        WriteFile("""{ "version": 1, "enabled": false, "allow": [] }""");

        RestrictionSet restrictions = PackageRestrictions.Load();

        Assert.Equal(RestrictionState.Disabled, restrictions.State);
        Assert.True(restrictions.Allows(PackageWithId("Contoso.App")));
    }

    [Theory]
    [InlineData("""{ "version": 2, "allow": [ { "manager": "Alpha" } ] }""")]
    [InlineData("""{ "version": 1, "allow": [ """)]
    [InlineData("null")]
    public void ATrustedFileThatCannotBeReadAllowsNothing(string json)
    {
        WriteFile(json);

        RestrictionSet restrictions = PackageRestrictions.Load();

        Assert.Equal(RestrictionState.Invalid, restrictions.State);
        Assert.True(restrictions.IsActive);
        Assert.False(restrictions.Allows(PackageWithId("Contoso.App")));
    }

    [Fact]
    public void AFileStandardUsersCanChangeIsIgnored()
    {
        WriteFile("""{ "version": 1, "allow": [] }""");
        RestrictionFileTrust.TestOverride = _ => "Users may write to it.";

        RestrictionSet restrictions = PackageRestrictions.Load();

        Assert.Equal(RestrictionState.IgnoredUntrusted, restrictions.State);
        Assert.Equal("Users may write to it.", restrictions.Detail);
        Assert.False(restrictions.IsActive);
        Assert.True(restrictions.Allows(PackageWithId("Contoso.App")));
    }

    [Fact]
    public void ThePolicyDecidesAloneWhenItIsConfigured()
    {
        WriteFile("""{ "version": 1, "allow": [ { "manager": "Alpha" } ] }""");
        RestrictionRegistryReader.Source = () =>
            new RegistryRestrictions(true, ["Alpha|*|Contoso.App", "", "|no|manager"]);

        RestrictionSet restrictions = PackageRestrictions.Load();

        Assert.Equal(RestrictionOrigin.Registry, restrictions.Origin);
        Assert.Single(restrictions.Rules);
        Assert.True(restrictions.Allows(PackageWithId("Contoso.App")));
        Assert.False(restrictions.Allows(PackageWithId("Contoso.Other")));
    }

    [Fact]
    public void APolicyThatIsTurnedOffOverridesAFileThatRestricts()
    {
        WriteFile("""{ "version": 1, "allow": [] }""");
        RestrictionRegistryReader.Source = () => new RegistryRestrictions(false, ["Alpha"]);

        RestrictionSet restrictions = PackageRestrictions.Load();

        Assert.Equal(RestrictionState.Disabled, restrictions.State);
        Assert.Equal(RestrictionOrigin.Registry, restrictions.Origin);
        Assert.True(restrictions.Allows(PackageWithId("Contoso.App")));
    }

    [Fact]
    public void TheSnapshotIsReadAgainOnceItIsStale()
    {
        TimeSpan interval = PackageRestrictions.RefreshInterval;
        try
        {
            PackageRestrictions.Invalidate();
            PackageRestrictions.RefreshInterval = TimeSpan.FromHours(1);
            Assert.Equal(RestrictionState.NotConfigured, PackageRestrictions.Current.State);

            WriteFile("""{ "version": 1, "allow": [] }""");
            Assert.Equal(RestrictionState.NotConfigured, PackageRestrictions.Current.State);

            int changes = 0;
            EventHandler onChanged = (_, _) => changes++;
            PackageRestrictions.Changed += onChanged;
            try
            {
                PackageRestrictions.RefreshInterval = TimeSpan.Zero;
                Assert.Equal(RestrictionState.Enforced, PackageRestrictions.Current.State);
                Assert.Equal(1, changes);
            }
            finally
            {
                PackageRestrictions.Changed -= onChanged;
            }
        }
        finally
        {
            PackageRestrictions.RefreshInterval = interval;
        }
    }

    [Fact]
    public void TheExportIsAFileThatAllowsExactlyWhatWasExported()
    {
        IManagerSource unknownOrigin = new SourceBuilder()
            .WithManager(_manager)
            .WithName("Local PC")
            .AsVirtualManager()
            .Build();
        Package listed = new PackageBuilder().WithManager(_manager).WithId("Contoso.App").WithName("Contoso App").WithVersion("2.1").Build();
        Package again = new PackageBuilder().WithManager(_manager).WithId("contoso.app").WithVersion("2.0").Build();
        Package untraceable = new PackageBuilder().WithManager(_manager).WithSource(unknownOrigin).WithId("Some.Program").Build();

        RestrictionDocument document = AllowListExport.Build([listed, again, untraceable], out int skipped);
        string json = AllowListExport.ToJson(document);

        Assert.Equal(1, skipped);
        RestrictionEntry entry = Assert.Single(document.Allow!)!;
        Assert.Equal(_manager.Id, entry.Manager);
        Assert.Equal(listed.Source.Name, entry.Source);
        Assert.Equal("Contoso.App", entry.Id);
        Assert.Equal("Contoso App", entry.Name);
        Assert.Equal("2.1", entry.Version);

        RestrictionSet restrictions = PackageRestrictions.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(RestrictionState.Enforced, restrictions.State);
        Assert.True(restrictions.Allows(listed));
        Assert.False(restrictions.Allows(PackageWithId("Contoso.Other")));

        Assert.Equal(
            $"{_manager.Id}|{listed.Source.Name}|Contoso.App{Environment.NewLine}",
            AllowListExport.ToRuleLines(document));
    }

    [Fact]
    public void AManagerThatForgetsWhereAPackageCameFromIsExportedForAnySource()
    {
        TestPackageManager manager = new PackageManagerBuilder()
            .WithName("Forgetful")
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.InstalledPackagesKeepTheirSource = false;
                return capabilities;
            })
            .Build();
        Package package = new PackageBuilder().WithManager(manager).WithId("Contoso.App").Build();

        RestrictionDocument document = AllowListExport.Build([package], out _);

        Assert.Equal(RestrictionRule.Any, Assert.Single(document.Allow!)!.Source);
        Assert.Equal($"{manager.Id}|*|Contoso.App{Environment.NewLine}", AllowListExport.ToRuleLines(document));
    }

    [Fact]
    public void MergingKeepsOneEntryForEachPackage()
    {
        string path = Path.Combine(_directory, "deployed.json");
        AllowListExport.WriteAtomic(path, """
            { "version": 1, "enabled": true, "allow": [
              { "manager": "Alpha", "source": "main", "id": "Contoso.App", "name": "Reviewed" },
              { "manager": "Alpha", "source": "main" }
            ] }
            """);
        var added = new RestrictionDocument
        {
            Allow =
            [
                new RestrictionEntry { Manager = "alpha", Source = "MAIN", Id = "contoso.app", Name = "Duplicate" },
                new RestrictionEntry { Manager = "Alpha", Source = "main", Id = "Contoso.New" },
            ],
        };

        RestrictionDocument merged = AllowListExport.Merge(AllowListExport.Read(path), added);

        string?[] expectedIds = [null, "Contoso.App", "Contoso.New"];
        Assert.Equal(expectedIds, merged.Allow!.Select(entry => entry!.Id).ToArray());
        Assert.Equal("Reviewed", merged.Allow!.Single(entry => entry!.Id == "Contoso.App")!.Name);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
