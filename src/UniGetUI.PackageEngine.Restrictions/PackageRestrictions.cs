using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Restrictions;

/// <summary>
/// The package restrictions an administrator deployed to this machine: from group policy when it
/// is configured, else from <c>PackageRestrictions.json</c> in the machine-wide directory.
/// </summary>
public static class PackageRestrictions
{
    public const string FileName = "PackageRestrictions.json";

    private static readonly Lock _lock = new();
    private static RestrictionSet? _current;
    private static long _loadedAt;

    public static string? TestFilePathOverride { get; set; }

    /// <summary>Replaces the whole lookup in tests.</summary>
    public static Func<RestrictionSet>? TestOverride { get; set; }

    /// <summary>How long a snapshot is used before policy and file are read again.</summary>
    public static TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    public static string FilePath =>
        TestFilePathOverride ?? Path.Combine(CoreData.UniGetUIGlobalDirectory, FileName);

    /// <summary>Raised when a refresh finds a different configuration than the one in use.</summary>
    public static event EventHandler? Changed;

    public static RestrictionSet Current
    {
        get
        {
            if (TestOverride is { } test)
                return test();

            RestrictionSet? current = Volatile.Read(ref _current);
            if (current is not null && !IsStale())
                return current;

            RestrictionSet fresh;
            bool changed;
            lock (_lock)
            {
                current = _current;
                if (current is not null && !IsStale())
                    return current;

                fresh = Load();
                changed = current is null || !fresh.SameAs(current);
                if (!changed)
                    fresh = current!;
                Volatile.Write(ref _current, fresh);
                Volatile.Write(ref _loadedAt, Environment.TickCount64);
            }

            if (changed)
            {
                Report(fresh);
                if (current is not null)
                    Changed?.Invoke(null, EventArgs.Empty);
            }
            return fresh;
        }
    }

    /// <summary>Forgets the snapshot so the next read goes back to the policy and the file.</summary>
    public static void Invalidate()
    {
        lock (_lock)
            _current = null;
    }

    private static bool IsStale() =>
        Environment.TickCount64 - Volatile.Read(ref _loadedAt) >= RefreshInterval.TotalMilliseconds;

    public static RestrictionSet Load()
    {
        try
        {
            // A configured policy decides alone, even when it turns the restrictions off.
            if (RestrictionRegistryReader.Read() is { } registry)
                return FromRegistry(registry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new RestrictionSet(RestrictionState.Invalid, RestrictionOrigin.Registry,
                detail: $"The policy could not be read: {ex.Message}");
        }

        return FromFile(FilePath);
    }

    private static RestrictionSet FromRegistry(RegistryRestrictions registry)
    {
        if (!registry.Enabled)
            return new RestrictionSet(RestrictionState.Disabled, RestrictionOrigin.Registry);

        var rules = new List<RestrictionRule>();
        foreach (string line in registry.Lines)
        {
            if (RestrictionRule.ParseLine(line) is { } rule)
                rules.Add(rule);
            else if (!string.IsNullOrWhiteSpace(line))
                Logger.Warn($"Ignoring the package restriction rule \"{line}\": it names no package manager.");
        }
        return new RestrictionSet(RestrictionState.Enforced, RestrictionOrigin.Registry, rules);
    }

    private static RestrictionSet FromFile(string path)
    {
        if (!File.Exists(path))
            return RestrictionSet.NotConfigured;

        if (RestrictionFileTrust.WhyUntrusted(path) is { } reason)
            return new RestrictionSet(RestrictionState.IgnoredUntrusted, RestrictionOrigin.File, detail: reason);

        try
        {
            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Invalid(ex.Message);
        }
    }

    /// <summary>
    /// Drops the byte order mark that Windows editors and PowerShell put in front of UTF-8 files,
    /// which the JSON reader rejects.
    /// </summary>
    public static ReadOnlySpan<byte> WithoutByteOrderMark(ReadOnlySpan<byte> json) =>
        json.StartsWith("﻿"u8) ? json[3..] : json;

    public static RestrictionSet Parse(ReadOnlySpan<byte> json)
    {
        RestrictionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(WithoutByteOrderMark(json), RestrictionJsonContext.DocumentJsonTypeInfo);
        }
        catch (JsonException ex)
        {
            return Invalid(ex.Message);
        }

        if (document is null)
            return Invalid("The file is empty.");
        if (document.Version != RestrictionDocument.CurrentVersion)
            return Invalid($"Version {document.Version} is not supported.");
        if (document.Enabled is false)
            return new RestrictionSet(RestrictionState.Disabled, RestrictionOrigin.File);

        var rules = new List<RestrictionRule>();
        foreach (RestrictionEntry? entry in document.Allow ?? [])
        {
            if (RestrictionRule.Create(entry?.Manager, entry?.Source, entry?.Id) is { } rule)
                rules.Add(rule);
            else
                Logger.Warn("Ignoring a package restriction rule that names no package manager.");
        }
        return new RestrictionSet(RestrictionState.Enforced, RestrictionOrigin.File, rules);
    }

    private static RestrictionSet Invalid(string detail) =>
        new(RestrictionState.Invalid, RestrictionOrigin.File, detail: detail);

    private static void Report(RestrictionSet set)
    {
        switch (set.State)
        {
            case RestrictionState.Enforced:
                Logger.ImportantInfo($"Package restrictions are enforced ({set.Origin}, {set.Rules.Count} rules).");
                break;
            case RestrictionState.Disabled:
                Logger.Info($"Package restrictions are turned off ({set.Origin}).");
                break;
            case RestrictionState.Invalid:
                Logger.Error($"The package restrictions ({set.Origin}) could not be read, so no new package can be installed: {set.Detail}");
                break;
            case RestrictionState.IgnoredUntrusted:
                Logger.Warn($"Ignoring {FilePath}: {set.Detail}");
                break;
        }
    }
}
