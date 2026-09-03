using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Portalkeeper.Models;

public sealed class AddonManifest
{
    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; init; } = 1;

    [JsonPropertyName("addons")]
    public List<AddonDefinition> Addons { get; init; } = new();
}

public sealed class AddonDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("folder")]
    public string Folder { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("recommended")]
    public bool Recommended { get; init; }

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}