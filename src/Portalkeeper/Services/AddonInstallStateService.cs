using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Portalkeeper.Services;

public sealed class AddonInstallStateService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public AddonInstallState Load(
        string clientDirectory,
        string addonId)
    {
        try
        {
            var path = GetStatePath(clientDirectory, addonId);
            if (!File.Exists(path))
                return new AddonInstallState();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AddonInstallState>(json)
                   ?? new AddonInstallState();
        }
        catch
        {
            return new AddonInstallState();
        }
    }

    public void Save(
        string clientDirectory,
        string addonId,
        string version,
        string sourceCommit)
    {
        var path = GetStatePath(clientDirectory, addonId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var state = new AddonInstallState
        {
            Version = version,
            SourceCommit = sourceCommit,
            UpdatedUtc = DateTime.UtcNow
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(state, JsonOptions));
    }

    private static string GetStatePath(
        string clientDirectory,
        string addonId)
    {
        var safeId = string.Concat(
            addonId.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character)
                    ? '_'
                    : character));

        return Path.Combine(
            clientDirectory,
            ".portalkeeper",
            "addons",
            safeId + ".json");
    }
}

public sealed class AddonInstallState
{
    public string Version { get; set; } = string.Empty;
    public string SourceCommit { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
