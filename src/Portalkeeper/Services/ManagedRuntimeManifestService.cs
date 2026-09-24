using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

public sealed class ManagedRuntimeManifestService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public string Serialize(ManagedRuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    public ManagedRuntimeManifest Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Managed runtime manifest is empty.");

        ManagedRuntimeManifest manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<ManagedRuntimeManifest>(json)
                       ?? throw new InvalidDataException("Managed runtime manifest is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid managed runtime manifest JSON: " + ex.Message, ex);
        }

        Validate(manifest);
        return manifest;
    }

    public void Validate(ManagedRuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != ManagedRuntimeManifest.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported managed runtime manifest schema version {manifest.SchemaVersion}.");

        if (!RealmIdentity.IsValid(manifest.RealmId))
            throw new InvalidDataException("Managed runtime manifest requires a valid realm identity.");

        if (manifest.RealmName.Length == 0)
            throw new InvalidDataException("Managed runtime manifest requires a realm name.");

        if (manifest.Generation.Length == 0)
            throw new InvalidDataException("Managed runtime manifest requires a runtime generation.");

        if (manifest.SourceClientPath.Length == 0)
            throw new InvalidDataException("Managed runtime manifest requires a source client path.");

        if (manifest.SourceExecutableSha256.Length != 0)
            ManagedPath.Hash(manifest.SourceExecutableSha256);

        if (manifest.LaunchExecutableRelativePath.Length != 0)
            RuntimePaths.NormalizeRelative(manifest.LaunchExecutableRelativePath, fileName: true);

        if (!Enum.IsDefined(manifest.State))
            throw new InvalidDataException("Invalid managed runtime state.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest.Files)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var runtimePath = RuntimePaths.NormalizeRelative(entry.RuntimeRelativePath);

            if (!seen.Add(runtimePath))
                throw new InvalidDataException(
                    $"Duplicate managed runtime file: {entry.RuntimeRelativePath}.");

            if (entry.Kind is ManagedRuntimeFileKind.LinkedBaseline
                or ManagedRuntimeFileKind.CopiedBaseline)
            {
                if (entry.SourceRelativePath.Length == 0)
                    throw new InvalidDataException(
                        $"Baseline entries require a source-relative path: {entry.RuntimeRelativePath}.");

                RuntimePaths.NormalizeRelative(entry.SourceRelativePath);
            }
            else if (entry.SourceRelativePath.Length != 0)
            {
                throw new InvalidDataException(
                    $"Realm-owned entries cannot carry a source-relative path: {entry.RuntimeRelativePath}.");
            }

            if (!Enum.IsDefined(entry.Kind))
                throw new InvalidDataException("Invalid managed runtime file kind.");

            if (entry.Sha256.Length != 0)
                ManagedPath.Hash(entry.Sha256);
        }

        if (manifest.RealmExecutable is { } executable)
        {
            RuntimePaths.NormalizeRelative(executable.SourceRelativePath, fileName: true);
            RuntimePaths.NormalizeRelative(executable.RuntimeRelativePath, fileName: true);
            ManagedPath.Hash(executable.SourceSha256);
            ManagedPath.Hash(executable.Sha256);
            if (executable.SourceSha256.Length == 0 || executable.Sha256.Length == 0 ||
                !ValidExecutableGeneration(executable) ||
                !executable.SourceSha256.Equals(manifest.SourceExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
                executable.RuntimeRelativePath != RealmExecutableService.FileName(manifest.RealmName, manifest.RealmId) ||
                manifest.LaunchExecutableRelativePath != executable.RuntimeRelativePath)
                throw new InvalidDataException("Invalid realm executable ownership, hash, or generation state.");
            var owned = manifest.Files.SingleOrDefault(e => e.RuntimeRelativePath.Equals(
                executable.RuntimeRelativePath, StringComparison.OrdinalIgnoreCase));
            var baseline = manifest.Files.SingleOrDefault(e => e.RuntimeRelativePath.Equals(
                executable.SourceRelativePath, StringComparison.OrdinalIgnoreCase));
            if (owned is null || owned.Kind != ManagedRuntimeFileKind.RealmOwned ||
                !owned.Sha256.Equals(executable.Sha256, StringComparison.OrdinalIgnoreCase) ||
                baseline is null || baseline.Kind != ManagedRuntimeFileKind.CopiedBaseline ||
                !baseline.Sha256.Equals(executable.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !baseline.SourceRelativePath.Equals(executable.SourceRelativePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Realm executable must have a separate owned file entry and a copied source baseline.");
        }
    }

    private static bool ValidExecutableGeneration(ManagedRealmExecutable executable) => executable.Generation switch
    {
        1 => executable.State == RealmExecutableState.BaselineCopy && executable.RecipeId is null &&
             executable.RecipeVersion is null && executable.Sha256.Equals(executable.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
             !executable.Sha256.Equals(FrameXmlDigestOverrideRecipe.OutputSha256, StringComparison.OrdinalIgnoreCase),
        2 => executable.State == RealmExecutableState.FrameXmlDigestOverride &&
             executable.RecipeId == FrameXmlDigestOverrideRecipe.Id && executable.RecipeVersion == FrameXmlDigestOverrideRecipe.Version &&
             executable.SourceSha256.Equals(FrameXmlDigestOverrideRecipe.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
             executable.Sha256.Equals(FrameXmlDigestOverrideRecipe.OutputSha256, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    // Atomic replacement keeps lifecycle updates readable after interruption.
    public ManagedRuntimeManifest Load(string path) =>
        Deserialize(File.ReadAllText(path));

    public void Save(ManagedRuntimeManifest manifest, string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(Serialize(manifest));
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}