using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Restrictions;

public enum RestrictionState
{
    /// <summary>No administrator configured anything: every package is allowed.</summary>
    NotConfigured,

    /// <summary>The administrator explicitly turned the restrictions off.</summary>
    Disabled,

    /// <summary>Only what a rule allows can be found or installed.</summary>
    Enforced,

    /// <summary>
    /// A trusted configuration could not be read. Nothing new can be installed, since allowing
    /// everything would let a damaged file lift the restrictions.
    /// </summary>
    Invalid,

    /// <summary>The file can be changed by standard users, so it was not applied.</summary>
    IgnoredUntrusted,
}

public enum RestrictionOrigin
{
    None,
    Registry,
    File,
}

/// <summary>An immutable snapshot of the administrator's package restrictions.</summary>
public sealed class RestrictionSet
{
    public static readonly RestrictionSet NotConfigured = new(RestrictionState.NotConfigured, RestrictionOrigin.None);

    private readonly Dictionary<string, ManagerRules> _managers = new(StringComparer.OrdinalIgnoreCase);

    public RestrictionSet(
        RestrictionState state,
        RestrictionOrigin origin,
        IEnumerable<RestrictionRule>? rules = null,
        string? detail = null)
    {
        State = state;
        Origin = origin;
        Detail = detail;
        Rules = [.. rules ?? []];
        foreach (RestrictionRule rule in Rules)
            Add(rule);
    }

    public RestrictionState State { get; }
    public RestrictionOrigin Origin { get; }
    public IReadOnlyList<RestrictionRule> Rules { get; }

    /// <summary>Why the configuration was ignored or rejected, for the log and the settings page.</summary>
    public string? Detail { get; }

    /// <summary>Whether packages are being filtered at all.</summary>
    public bool IsActive => State is RestrictionState.Enforced or RestrictionState.Invalid;

    public bool Allows(IPackage package) => Allows(package.Manager, package.Source.Name, package.Id);

    public bool Allows(IPackageManager manager, string source, string id)
    {
        if (!IsActive)
            return true;
        if (State is RestrictionState.Invalid)
            return false;

        return Allows(manager.Id, source, id)
            || Allows(manager.Name, source, id)
            || Allows(manager.DisplayName, source, id);
    }

    public bool SameAs(RestrictionSet other) =>
        State == other.State
        && Origin == other.Origin
        && Detail == other.Detail
        && Rules.SequenceEqual(other.Rules);

    private bool Allows(string manager, string source, string id) =>
        _managers.TryGetValue(manager, out ManagerRules? rules)
        && (rules.AnySource.Allows(id)
            || (rules.Sources.TryGetValue(source, out SourceRules? sourceRules) && sourceRules.Allows(id)));

    private void Add(RestrictionRule rule)
    {
        if (!_managers.TryGetValue(rule.Manager, out ManagerRules? manager))
            _managers[rule.Manager] = manager = new ManagerRules();

        SourceRules source = manager.AnySource;
        if (rule.Source is not null && !manager.Sources.TryGetValue(rule.Source, out source!))
            manager.Sources[rule.Source] = source = new SourceRules();

        if (rule.Id is null)
            source.Everything = true;
        else if (rule.Id.Contains('*'))
            source.Patterns.Add(rule.Id);
        else
            source.Ids.Add(rule.Id);
    }

    internal static bool GlobMatches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private sealed class ManagerRules
    {
        public SourceRules AnySource { get; } = new();
        public Dictionary<string, SourceRules> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SourceRules
    {
        public bool Everything { get; set; }
        public HashSet<string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Patterns { get; } = [];

        public bool Allows(string id)
        {
            if (Everything || Ids.Contains(id))
                return true;
            foreach (string pattern in Patterns)
            {
                if (GlobMatches(pattern, id))
                    return true;
            }
            return false;
        }
    }
}
