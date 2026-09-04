using System;
using Portalkeeper.Services;

namespace Portalkeeper.Models;

public sealed class AddonInfo
{
    public AddonDefinition Definition { get; init; } = new();

    public string DirectoryPath { get; init; } = string.Empty;

    public bool IsInstalled { get; init; }

    public string InstalledVersion { get; init; } = string.Empty;

    public string InstalledSourceCommit { get; init; } = string.Empty;

    public string DiscoveryError { get; init; } = string.Empty;

    public bool IsSourceError =>
        !string.IsNullOrWhiteSpace(DiscoveryError);

    public bool HasKnownInstalledVersion =>
        IsInstalled &&
        !string.IsNullOrWhiteSpace(InstalledVersion);

    public bool HasAvailableVersion =>
        !string.IsNullOrWhiteSpace(Definition.Version);

    public bool HasTrackedSourceCommit =>
        !string.IsNullOrWhiteSpace(InstalledSourceCommit) &&
        !string.IsNullOrWhiteSpace(Definition.SourceCommit);

    public bool VersionUpdateAvailable =>
        HasKnownInstalledVersion &&
        HasAvailableVersion &&
        AddonVersionComparer.Compare(
            InstalledVersion,
            Definition.Version) < 0;

    public bool SourceUpdateAvailable =>
        IsInstalled &&
        HasTrackedSourceCommit &&
        !InstalledSourceCommit.Equals(
            Definition.SourceCommit,
            StringComparison.OrdinalIgnoreCase);

    public bool IsUpdateAvailable =>
        VersionUpdateAvailable || SourceUpdateAvailable;

    public bool IsNewerThanManifest =>
        HasKnownInstalledVersion &&
        HasAvailableVersion &&
        AddonVersionComparer.Compare(
            InstalledVersion,
            Definition.Version) > 0;

    public string RequirementText =>
        Definition.IsPersonal
            ? "Personal"
            : Definition.Required
                ? "Required"
                : Definition.Recommended
                    ? "Recommended"
                    : "Optional";

    public bool CanRemoveFromManagement =>
        Definition.IsPersonal;

    public string StatusText =>
        IsSourceError
            ? "Source Error"
            : !IsInstalled
            ? "Missing"
            : IsUpdateAvailable
                ? "Update Available"
                : IsNewerThanManifest
                    ? "Newer"
                    : "Current";

    public string StatusSymbol =>
        IsSourceError
            ? "!"
            : !IsInstalled
            ? "○"
            : IsUpdateAvailable
                ? "↑"
                : "✓";

    public bool CanInstallOrUpdate =>
        !IsSourceError &&
        (!IsInstalled || IsUpdateAvailable) &&
        (
            (Definition.IsGitHubSource &&
             !string.IsNullOrWhiteSpace(Definition.SourceCommit))
            ||
            (!string.IsNullOrWhiteSpace(Definition.DownloadUrl) &&
             !string.IsNullOrWhiteSpace(Definition.Sha256))
        );

    public string ActionText =>
        !IsInstalled ? "INSTALL" : "UPDATE";

    public string VersionText
    {
        get
        {
            if (IsSourceError)
                return DiscoveryError;

            if (!IsInstalled)
                return HasAvailableVersion
                    ? $"Available: {Definition.Version}"
                    : "Not installed";

            if (!HasKnownInstalledVersion)
                return HasAvailableVersion
                    ? $"Installed: unknown   Available: {Definition.Version}"
                    : "Installed version: unknown";

            if (IsUpdateAvailable || IsNewerThanManifest)
                return $"Installed: {InstalledVersion}   Available: {Definition.Version}";

            return $"Installed: {InstalledVersion}";
        }
    }
}
