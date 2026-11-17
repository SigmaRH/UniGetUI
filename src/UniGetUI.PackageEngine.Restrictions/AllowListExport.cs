using System.Text;
using System.Text.Json;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Restrictions;

/// <summary>
/// Writes a list of packages as a restrictions file, so that what is installed on a machine can
/// be reviewed and merged into the list an administrator deploys.
/// </summary>
public static class AllowListExport
{
    /// <param name="skipped">
    /// Packages no package manager can install again, such as software found on the machine
    /// without a known source. A rule for them would allow nothing.
    /// </param>
    public static RestrictionDocument Build(IEnumerable<IPackage> packages, out int skipped)
    {
        skipped = 0;
        var entries = new List<RestrictionEntry>();
        foreach (IPackage package in packages)
        {
            if (package.Source.IsVirtualManager)
            {
                skipped++;
                continue;
            }

            entries.Add(new RestrictionEntry
            {
                Manager = package.Manager.Id,
                // Such a manager does not say which source an installed package came from, so
                // the source it is listed under here is only a guess.
                Source = package.Manager.Capabilities.InstalledPackagesKeepTheirSource
                    ? package.Source.Name
                    : RestrictionRule.Any,
                Id = package.Id,
                Name = package.Name,
                Version = package.VersionString,
            });
        }

        return new RestrictionDocument { Enabled = true, Allow = Normalize(entries) };
    }

    /// <summary>Adds the entries of <paramref name="added"/> that <paramref name="existing"/> lacks.</summary>
    public static RestrictionDocument Merge(RestrictionDocument existing, RestrictionDocument added) =>
        new()
        {
            Version = existing.Version,
            Enabled = existing.Enabled,
            Allow = Normalize((existing.Allow ?? []).Concat(added.Allow ?? [])),
        };

    public static RestrictionDocument Read(string path)
    {
        ReadOnlySpan<byte> json = PackageRestrictions.WithoutByteOrderMark(File.ReadAllBytes(path));
        RestrictionDocument? document = JsonSerializer.Deserialize(json, RestrictionJsonContext.DocumentJsonTypeInfo);
        if (document is null || document.Version != RestrictionDocument.CurrentVersion)
            throw new JsonException($"{path} is not a version {RestrictionDocument.CurrentVersion} package restrictions file.");
        return document;
    }

    public static string ToJson(RestrictionDocument document) =>
        JsonSerializer.Serialize(document, RestrictionJsonContext.DocumentJsonTypeInfo) + Environment.NewLine;

    /// <summary>One <c>manager|source|id</c> line per rule, as the group policy list takes them.</summary>
    public static string ToRuleLines(RestrictionDocument document)
    {
        var lines = new StringBuilder();
        foreach (RestrictionEntry? entry in document.Allow ?? [])
        {
            if (RestrictionRule.Create(entry?.Manager, entry?.Source, entry?.Id) is { } rule)
                lines.AppendLine(rule.ToLine());
        }
        return lines.ToString();
    }

    /// <summary>Replaces the file in one step, so a reader never sees half of it.</summary>
    public static void WriteAtomic(string path, string content)
    {
        path = Path.GetFullPath(path);
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static List<RestrictionEntry?> Normalize(IEnumerable<RestrictionEntry?> entries)
    {
        var seen = new HashSet<RestrictionRule>(RuleComparer.Instance);
        var kept = new List<RestrictionEntry>();
        foreach (RestrictionEntry? entry in entries)
        {
            if (RestrictionRule.Create(entry?.Manager, entry?.Source, entry?.Id) is { } rule && seen.Add(rule))
                kept.Add(entry!);
        }

        return
        [
            .. kept
                .OrderBy(entry => entry.Manager, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Source ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id ?? "", StringComparer.OrdinalIgnoreCase),
        ];
    }

    private sealed class RuleComparer : IEqualityComparer<RestrictionRule>
    {
        public static readonly RuleComparer Instance = new();

        public bool Equals(RestrictionRule? x, RestrictionRule? y) =>
            ReferenceEquals(x, y)
            || (x is not null
                && y is not null
                && string.Equals(x.Manager, y.Manager, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(RestrictionRule rule) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(rule.Manager),
                StringComparer.OrdinalIgnoreCase.GetHashCode(rule.Source ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(rule.Id ?? ""));
    }
}
