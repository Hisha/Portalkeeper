using System;
using System.Collections.Generic;
using System.IO;
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

            if (entry.Sha256.Length != 0)
                ManagedPath.Hash(entry.Sha256);
        }
    }

    // These file helpers are plumbing only; normal application flow does not
    // write managed runtime manifests until runtime construction is implemented.
    public ManagedRuntimeManifest Load(string path) =>
        Deserialize(File.ReadAllText(path));

    public void Save(ManagedRuntimeManifest manifest, string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, Serialize(manifest));
    }
}