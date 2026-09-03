namespace Portalkeeper.Models;

public sealed class AddonInfo
{
    public AddonDefinition Definition { get; init; } = new();

    public string DirectoryPath { get; init; } = string.Empty;

    public bool IsInstalled { get; init; }

    public string InstalledVersion { get; init; } = string.Empty;

    public string RequirementText =>
        Definition.Required
            ? "Required"
            : Definition.Recommended
                ? "Recommended"
                : "Optional";

    public string StatusText =>
        IsInstalled
            ? "Installed"
            : "Missing";

    public string StatusSymbol =>
        IsInstalled
            ? "✓"
            : "○";

    public string VersionText =>
        string.IsNullOrWhiteSpace(InstalledVersion)
            ? Definition.Version
            : InstalledVersion;
}