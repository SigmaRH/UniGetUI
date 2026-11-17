using System.Globalization;
using System.Text.Json;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Restrictions;

namespace UniGetUI.PackageEngine;

/// <summary>
/// <c>unigetui export-installed &lt;file&gt;</c>: writes the packages installed on this machine as a
/// package restrictions file, without needing a running UniGetUI. An administrator runs it on a
/// few machines and merges the results into the list deployed to everyone.
/// </summary>
public static class ExportInstalledCommand
{
    public const string Command = "export-installed";
    public const string StandardOutput = "-";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The installed packages, and what could not be listed.</summary>
    public sealed record Inventory(IReadOnlyList<IPackage> Packages, IReadOnlyList<string> Problems);

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase);

    /// <param name="load">Replaces the package managers in tests.</param>
    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<IReadOnlyList<string>, TimeSpan, Inventory>? load = null)
    {
        string? destination = null;
        string? mergeWith = null;
        bool asRules = false;
        TimeSpan timeout = DefaultTimeout;
        var managers = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Value() => i + 1 < args.Length ? args[++i] : null;

            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help":
                    WriteUsage(output);
                    return 0;
                case "--manager":
                    if (Value() is not { Length: > 0 } manager)
                        return Usage(error, "--manager needs the name of a package manager.");
                    managers.Add(manager);
                    break;
                case "--format":
                    switch (Value()?.ToLowerInvariant())
                    {
                        case "json":
                            asRules = false;
                            break;
                        case "rules":
                            asRules = true;
                            break;
                        default:
                            return Usage(error, "--format is either json or rules.");
                    }
                    break;
                case "--merge":
                    if (Value() is not { Length: > 0 } file)
                        return Usage(error, "--merge needs the path of a package restrictions file.");
                    mergeWith = file;
                    break;
                case "--timeout":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds < 1)
                        return Usage(error, "--timeout needs a number of seconds.");
                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    if (destination is not null || (arg.StartsWith('-') && arg != StandardOutput))
                        return Usage(error, $"Unexpected argument {arg}.");
                    destination = arg;
                    break;
            }
        }

        if (destination is null)
            return Usage(error, null);

        RestrictionDocument? existing = null;
        if (mergeWith is not null)
        {
            try
            {
                existing = AllowListExport.Read(mergeWith);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                error.WriteLine($"Could not read {mergeWith}: {ex.Message}");
                return 1;
            }
        }

        load ??= LoadInstalled;
        Inventory inventory = load(managers, timeout);
        RestrictionDocument document = AllowListExport.Build(inventory.Packages, out int skipped);
        int found = document.Allow?.Count ?? 0;
        if (existing is not null)
            document = AllowListExport.Merge(existing, document);

        string content = asRules ? AllowListExport.ToRuleLines(document) : AllowListExport.ToJson(document);
        // With the list on standard output, everything said about it goes to the error stream.
        TextWriter report = destination == StandardOutput ? error : output;
        try
        {
            if (destination == StandardOutput)
                output.Write(content);
            else
                AllowListExport.WriteAtomic(destination, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Could not write {destination}: {ex.Message}");
            return 1;
        }

        foreach (string problem in inventory.Problems)
            error.WriteLine(problem);
        if (skipped > 0)
            report.WriteLine($"Left out {skipped} package(s) that no package manager can install again.");
        if (destination != StandardOutput)
            report.WriteLine($"Wrote {Path.GetFullPath(destination)} with {document.Allow?.Count ?? 0} package(s), {found} of them installed here.");
        return inventory.Problems.Count == 0 ? 0 : 3;
    }

    private static int Usage(TextWriter error, string? problem)
    {
        if (problem is not null)
            error.WriteLine(problem);
        WriteUsage(error);
        return 2;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine($"Usage: unigetui {Command} <file|-> [--manager <name>]... [--format json|rules] [--merge <file>] [--timeout <seconds>]");
        writer.WriteLine("Writes the packages installed on this machine as a package restrictions file,");
        writer.WriteLine("to review and merge into the list of packages an organization allows.");
        writer.WriteLine("  --manager  Only list the packages of this package manager. Can be repeated.");
        writer.WriteLine("  --format   json (default) is a package restrictions file; rules is one manager|source|id line per package.");
        writer.WriteLine("  --merge    Add the packages to the ones of an existing package restrictions file.");
        writer.WriteLine("  --timeout  How long to wait for the package managers, 120 seconds by default.");
    }

    private static Inventory LoadInstalled(IReadOnlyList<string> names, TimeSpan timeout)
    {
        // Managers reach the loaders while listing packages, so they must exist first.
        PEInterface.LoadLoaders();

        var problems = new List<string>();
        var selected = new List<IPackageManager>();
        if (names.Count == 0)
        {
            selected.AddRange(PEInterface.Managers);
        }
        else
        {
            foreach (string name in names)
            {
                IPackageManager? manager = PEInterface.Managers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (manager is null)
                    problems.Add($"No package manager is named {name}.");
                else if (!selected.Contains(manager))
                    selected.Add(manager);
            }
        }

        var tasks = selected
            .Select(manager => Task.Run<IReadOnlyList<IPackage>?>(() =>
            {
                manager.Initialize();
                return manager.IsReady() ? manager.GetInstalledPackages() : null;
            }))
            .ToArray();
        try
        {
            Task.WaitAll(tasks, timeout);
        }
        catch (AggregateException)
        {
            // Each failure is reported below, against its manager.
        }

        var packages = new List<IPackage>();
        for (int i = 0; i < selected.Count; i++)
        {
            IPackageManager manager = selected[i];
            var task = tasks[i];
            if (!task.IsCompleted)
            {
                problems.Add($"{manager.DisplayName} did not answer within {timeout.TotalSeconds:0} seconds.");
            }
            else if (task.IsFaulted)
            {
                Logger.Error(task.Exception!);
                problems.Add($"{manager.DisplayName} failed: {task.Exception!.GetBaseException().Message}");
            }
            else if (task.Result is not { } installed)
            {
                // A manager that is not on this machine is only a problem when it was asked for.
                if (names.Count > 0)
                    problems.Add($"{manager.DisplayName} is not available on this machine.");
            }
            else if (manager.LastInstalledListingFailed)
            {
                problems.Add($"{manager.DisplayName} could not list its installed packages.");
            }
            else
            {
                packages.AddRange(installed);
            }
        }

        return new Inventory(packages, problems);
    }
}
