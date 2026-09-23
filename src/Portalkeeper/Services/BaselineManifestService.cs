using System;
using System.Collections.Generic;
using System.IO;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

public sealed class BaselineManifestService
{
    public void Validate(BaselineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != BaselineManifest.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported baseline manifest schema version {manifest.SchemaVersion}.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in manifest.Assets)
        {
            ArgumentNullException.ThrowIfNull(asset);

            var relative = RuntimePaths.NormalizeRelative(asset.RelativePath);

            if (!seen.Add(relative))
                throw new InvalidDataException(
                    $"Duplicate baseline asset: {asset.RelativePath}.");

            if (FirstComponent(relative).Equals(
                    ".portalkeeper",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Baseline assets cannot reference Portalkeeper metadata: {asset.RelativePath}.");
            }

            ManagedPath.Hash(asset.Sha256);

            if (asset.Kind == BaselineAssetKind.Directory)
            {
                if (asset.Sha256.Length != 0)
                    throw new InvalidDataException(
                        $"A baseline directory cannot carry a SHA-256: {asset.RelativePath}.");

                foreach (var member in asset.Members)
                {
                    var memberPath = RuntimePaths.NormalizeRelative(member);

                    if (!RuntimePaths.IsWithin(relative, memberPath))
                        throw new InvalidDataException(
                            $"Baseline member escapes its directory asset: {member}.");
                }
            }
        }
    }

    public string ResolvePath(string clientRoot, BaselineAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrEmpty(clientRoot);
        return RuntimePaths.Resolve(clientRoot, asset.RelativePath);
    }

    // Starter category-level allowlist for the supported baseline client. It is
    // intentionally not a complete 12340 inventory: it carries no SHA-256, no
    // locale MPQs and no Blizzard addon file inventory. Those must come from an
    // authoritative source rather than being guessed. The names below reuse the
    // stock archive order already used by the repository's client previews.
    public BaselineManifest WellKnownStockClient()
    {
        var assets = new List<BaselineAsset>
        {
            new()
            {
                RelativePath = "Wow.exe",
                Kind = BaselineAssetKind.File,
                Category = BaselineAssetCategory.Root,
                Immutable = true
            }
        };

        foreach (var mpq in new[]
                 {
                     "common.MPQ",
                     "common-2.MPQ",
                     "expansion.MPQ",
                     "lichking.MPQ",
                     "patch.MPQ",
                     "patch-2.MPQ",
                     "patch-3.MPQ"
                 })
        {
            assets.Add(new BaselineAsset
            {
                RelativePath = "Data/" + mpq,
                Kind = BaselineAssetKind.File,
                Category = BaselineAssetCategory.DataMpq,
                Immutable = true
            });
        }

        return new BaselineManifest
        {
            Name = "wow-3.3.5a-12340",
            Version = "3.3.5a",
            Build = "12340",
            Assets = assets
        };
    }

    private static string FirstComponent(string relative)
    {
        var index = relative.IndexOf(Path.DirectorySeparatorChar);
        return index < 0 ? relative : relative[..index];
    }
}