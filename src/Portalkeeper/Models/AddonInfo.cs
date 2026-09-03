using Portalkeeper.Services;

namespace Portalkeeper.Models;

public sealed class AddonInfo
{
    public AddonDefinition Definition { get; init; } = new();

    public string DirectoryPath { get; init; } = string.Empty;

    public bool IsInstalled { get; init; }

    public string InstalledVersion { get; init; } = string.Empty;

    public bool HasKnownInstalledVersion =>
        IsInstalled &&
        !string.IsNullOrWhiteSpace(InstalledVersion);

    public bool HasAvailableVersion =>
        !string.IsNullOrWhiteSpace(Definition.Version);

    public bool IsUpdateAvailable =>
        HasKnownInstalledVersion &&
        HasAvailableVersion &&
        AddonVersionComparer.Compare(
            InstalledVersion,
            Definition.Version) < 0;

    public bool IsNewerThanManifest =>
        HasKnownInstalledVersion &&
        HasAvailableVersion &&
        AddonVersionComparer.Compare(
            InstalledVersion,
            Definition.Version) > 0;

    public string RequirementText =>
        Definition.Required
            ? "Required"
            : Definition.Recommended
                ? "Recommended"
                : "Optional";

    public string StatusText =>
        !IsInstalled
            ? "Missing"
            : IsUpdateAvailable
                ? "Update Available"
                : IsNewerThanManifest
                    ? "Newer"
                    : "Current";

    public string StatusSymbol =>
        !IsInstalled
            ? "○"
            : IsUpdateAvailable
                ? "↑"
                : "✓";

    public string VersionText
    {
        get
        {
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
