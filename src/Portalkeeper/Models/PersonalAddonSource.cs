using System.Text.Json.Serialization;

namespace Portalkeeper.Models;

public sealed class PersonalAddonSource
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("gitUrl")]
    public string GitUrl { get; set; } = string.Empty;

    [JsonPropertyName("addonPath")]
    public string AddonPath { get; set; } = string.Empty;
}
