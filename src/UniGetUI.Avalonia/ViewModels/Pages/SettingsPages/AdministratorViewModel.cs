using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Restrictions;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class AdministratorViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    // ── Warning banner strings ────────────────────────────────────────────
    public string WarningTitle { get; } = CoreTools.Translate("Warning") + "!";

    public string WarningBody1 { get; } =
        CoreTools.Translate("The following settings may pose a security risk, hence they are disabled by default.")
        + " "
        + CoreTools.Translate("Enable the settings below if and only if you fully understand what they do, and the implications they may have.");

    public string WarningBody2 { get; } =
        CoreTools.Translate("The settings will list, in their descriptions, the potential security issues they may have.");

    public bool IsWindows { get; } = OperatingSystem.IsWindows();

    /// <summary>What the organization's package restrictions currently do on this device.</summary>
    public string PackageRestrictionsStatus { get; } = DescribePackageRestrictions(PackageRestrictions.Current);

    internal static string DescribePackageRestrictions(RestrictionSet restrictions) => restrictions.State switch
    {
        RestrictionState.Enforced => CoreTools.Translate(
            "Only the packages allowed by your organization can be found and installed. Installed packages can still be updated. Rules: {0}. Origin: {1}.",
            restrictions.Rules.Count,
            restrictions.Origin is RestrictionOrigin.Registry
                ? CoreTools.Translate("group policy")
                : PackageRestrictions.FilePath),
        RestrictionState.Disabled => CoreTools.Translate("Package restrictions are turned off by your organization."),
        RestrictionState.Invalid => CoreTools.Translate(
            "The package restrictions set by your organization could not be read, so no new package can be installed: {0}",
            restrictions.Detail ?? ""),
        RestrictionState.IgnoredUntrusted => CoreTools.Translate(
            "The package restrictions file {0} was ignored because users who are not administrators can modify it: {1}",
            PackageRestrictions.FilePath,
            restrictions.Detail ?? ""),
        _ => CoreTools.Translate("No package restrictions are configured on this device."),
    };

    // ── Observable state ─────────────────────────────────────────────────
    /// <summary>True when elevation is NOT prohibited — controls enabled-state of the cache-admin-rights cards.</summary>
    [ObservableProperty] private bool _isElevationEnabled;

    /// <summary>Mirrors AllowCLIArguments toggle — controls IsEnabled of AllowImportingCLIArguments.</summary>
    [ObservableProperty] private bool _isCLIArgumentsEnabled;

    /// <summary>Mirrors AllowPrePostOpCommand toggle — controls IsEnabled of AllowImportingPrePostInstallCommands.</summary>
    [ObservableProperty] private bool _isPrePostCommandsEnabled;

    public AdministratorViewModel()
    {
        _isElevationEnabled = !Settings.Get(Settings.K.ProhibitElevation);
        _isCLIArgumentsEnabled = SecureSettings.Get(SecureSettings.K.AllowCLIArguments);
        _isPrePostCommandsEnabled = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand);
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private static void RestartCache() => _ = CoreTools.ResetUACForCurrentProcess();

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RefreshElevationState()
    {
        IsElevationEnabled = !Settings.Get(Settings.K.ProhibitElevation);
        RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RefreshCLIState()
    {
        IsCLIArgumentsEnabled = SecureSettings.Get(SecureSettings.K.AllowCLIArguments);
    }

    [RelayCommand]
    private void RefreshPrePostState()
    {
        IsPrePostCommandsEnabled = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand);
    }

    [RelayCommand]
    private void NavigateToAgentPolicyInspector() =>
        NavigationRequested?.Invoke(this, typeof(AgentPolicyInspector));
}
