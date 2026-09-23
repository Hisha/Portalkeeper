using System.IO;
using Portalkeeper.Models;
using Portalkeeper.Models.Runtime;

namespace Portalkeeper.Services;

// Determines the effective client root operated on for a realm. Checkpoint 1
// always resolves to the source client path so no behavior changes; this method
// is the seam through which an isolated per-realm runtime can be supplied later.
public sealed class RealmRuntimeResolver
{
    public string ResolveEffectiveClientPath(string sourceClientPath, RealmInfo? realm = null)
    {
        if (string.IsNullOrWhiteSpace(sourceClientPath))
            return sourceClientPath;

        return Path.GetFullPath(sourceClientPath);
    }

    public RealmClientRoots ResolveRoots(string sourceClientPath, RealmInfo? realm = null) =>
        new(sourceClientPath, ResolveEffectiveClientPath(sourceClientPath, realm));
}