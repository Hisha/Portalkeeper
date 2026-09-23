using System.Collections.Generic;

namespace Portalkeeper.Models.Runtime;

// Describes the known supported baseline client (WoW 3.3.5a, build 12340).
// Checkpoint 1 establishes the schema, validation and loaders; the complete
// production hash inventory is intentionally incomplete and must come from an
// authoritative source rather than being guessed.
public sealed class BaselineManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Build { get; init; } = string.Empty;

    public List<BaselineAsset> Assets { get; init; } = new();
}