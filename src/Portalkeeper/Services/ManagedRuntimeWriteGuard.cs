using System;
using System.IO;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

// Preserve every manifest-backed baseline entry, including addon directories.
// Legacy roots have no runtime manifest and retain their existing behavior.
public static class ManagedRuntimeWriteGuard
{
    public static void Check(string root, string destination)
    {
        var manifestPath = ManagedPath.Resolve(root, ManagedRuntimeBuilder.ManifestRelativePath);
        if (!File.Exists(manifestPath)) return;
        var relative = Path.GetRelativePath(root, destination).Replace('\\', '/');
        if (relative.Equals(".portalkeeper", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".portalkeeper/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Realm content cannot replace runtime management metadata.");
        var manifest = new ManagedRuntimeManifestService().Load(manifestPath);
        foreach (var entry in manifest.Files)
        {
            if (entry.Kind is not (ManagedRuntimeFileKind.LinkedBaseline or ManagedRuntimeFileKind.CopiedBaseline)) continue;
            var baseline = entry.RuntimeRelativePath.Replace('\\', '/');
            if (baseline.Equals(relative, StringComparison.OrdinalIgnoreCase) ||
                baseline.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(baseline + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Realm content overlaps immutable runtime baseline: " + relative);
        }
    }
}
