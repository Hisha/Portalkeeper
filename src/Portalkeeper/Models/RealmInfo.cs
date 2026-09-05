namespace Portalkeeper.Models;

public sealed class RealmInfo
{
    public string Name { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int AuthPort { get; init; } = 3724;
    public int WorldPort { get; init; } = 8085;
    public string ManifestUrl { get; init; } = string.Empty;
    public string NewsUrl { get; init; } = string.Empty;
    public string StatusUrl { get; init; } = string.Empty;
    public string CalendarUrl { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Address);
}