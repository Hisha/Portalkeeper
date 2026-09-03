namespace Portalkeeper.Models;

public sealed class RealmInfo
{
    public string Name { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string ManifestUrl { get; init; } = string.Empty;
    public string NewsUrl { get; init; } = string.Empty;
    public string StatusUrl { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Address);
}