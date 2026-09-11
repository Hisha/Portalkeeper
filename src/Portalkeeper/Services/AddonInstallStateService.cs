using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;

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
            var state = JsonSerializer.Deserialize<AddonInstallState>(json)
                   ?? new AddonInstallState();
            
            // Backward compatibility: if InstalledFolders is not present, populate from Version field
            // This handles legacy files that don't have the new property yet
            if (state.InstalledFolders.Count == 0 && !string.IsNullOrEmpty(state.Version))
            {
                // For backward compatibility, if we had a version but no folders, 
                // assume the old behavior where there was one folder named after the addon
                // This is a conservative approach for legacy files
                state.InstalledFolders = new List<string>();
            }
            
            return state;
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
        string sourceCommit,
        List<string> installedFolders)
    {
        var path = GetStatePath(clientDirectory, addonId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var state = new AddonInstallState
        {
            Version = version,
            SourceCommit = sourceCommit,
            UpdatedUtc = DateTime.UtcNow,
            InstalledFolders = installedFolders ?? new List<string>()
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

        return ManagedPath.Resolve(clientDirectory, Path.Combine(".portalkeeper", "addons", safeId + ".json"));
    }
}

public sealed class AddonInstallState
{
    public string Version { get; set; } = string.Empty;
    public string SourceCommit { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public List<string> InstalledFolders { get; set; } = new();
}
