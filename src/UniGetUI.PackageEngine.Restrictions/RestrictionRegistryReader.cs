using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UniGetUI.PackageEngine.Restrictions;

/// <summary>What the group policy key holds: the switch and the raw rule lines.</summary>
public sealed record RegistryRestrictions(bool Enabled, IReadOnlyList<string> Lines);

/// <summary>
/// Reads the restrictions an administrator deploys through group policy. The key lives under
/// <c>HKLM\SOFTWARE\Policies</c>, which only administrators can write, so its content is trusted
/// as it is.
/// </summary>
public static class RestrictionRegistryReader
{
    public const string KeyPath = @"SOFTWARE\Policies\Devolutions\UniGetUI\PackageRestrictions";
    public const string EnabledValue = "Enabled";
    public const string AllowedPackagesKey = "AllowedPackages";

    /// <summary>Replaces the registry in tests, which must never write to HKLM.</summary>
    public static Func<RegistryRestrictions?>? Source { get; set; }

    /// <summary>Null when the policy is not configured, which leaves the decision to the file.</summary>
    public static RegistryRestrictions? Read()
    {
        if (Source is { } source)
            return source();
        return OperatingSystem.IsWindows() ? ReadWindows() : null;
    }

    [SupportedOSPlatform("windows")]
    private static RegistryRestrictions? ReadWindows()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = machine.OpenSubKey(KeyPath);
        if (key?.GetValue(EnabledValue) is not { } enabled)
            return null;

        var lines = new List<string>();
        using RegistryKey? allowed = key.OpenSubKey(AllowedPackagesKey);
        if (allowed is not null)
        {
            // A policy list stores one rule per value. How the values are named depends on the
            // template, so only their data is read.
            foreach (string name in allowed.GetValueNames())
            {
                switch (allowed.GetValue(name))
                {
                    case string line:
                        lines.Add(line);
                        break;
                    case string[] several:
                        lines.AddRange(several);
                        break;
                }
            }
        }

        return new RegistryRestrictions(IsEnabled(enabled), lines);
    }

    // Anything that is not clearly "off" keeps the restrictions on.
    private static bool IsEnabled(object value) => value switch
    {
        int number => number != 0,
        long number => number != 0,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) => number != 0,
        string text when bool.TryParse(text, out bool flag) => flag,
        _ => true,
    };
}
