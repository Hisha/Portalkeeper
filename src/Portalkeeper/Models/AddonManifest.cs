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

    // Optional override. For GitHub sources Portalkeeper normally discovers this.
    [JsonPropertyName("folder")]
    public string Folder { get; init; } = string.Empty;

    // Optional for direct-download sources. GitHub sources discover this from the .toc.
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("recommended")]
    public bool Recommended { get; init; }

    // Normal source for Portalkeeper-managed GitHub addons.
    [JsonPropertyName("gitUrl")]
    public string GitUrl { get; init; } = string.Empty;

    // Optional repository-relative addon directory override for unusual repos.
    [JsonPropertyName("addonPath")]
    public string AddonPath { get; init; } = string.Empty;

    // Fallback for addons not hosted in a supported GitHub repository.
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    // Runtime-discovered GitHub metadata. These do not need to be present in JSON.
    [JsonIgnore]
    public string SourceCommit { get; init; } = string.Empty;

    [JsonIgnore]
    public string SourceBranch { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsGitHubSource =>
        !string.IsNullOrWhiteSpace(GitUrl);
}
