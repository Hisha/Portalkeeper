using Portalkeeper.Services;

namespace Portalkeeper.Models.Runtime;

// Distinguishes the source client (the user's selected WoW installation) from
// the effective/launch client root. Today they are always equal; the resolver
// seam is what will let them differ for an isolated realm runtime later.
public sealed record RealmClientRoots(string Source, string Effective)
{
    public bool IsIsolated => !RuntimePaths.SamePath(Source, Effective);
}