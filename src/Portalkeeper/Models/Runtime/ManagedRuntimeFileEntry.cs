namespace Portalkeeper.Models.Runtime;

public enum ManagedRuntimeFileKind { LinkedBaseline, CopiedBaseline, RealmOwned }

// A single file recorded in a per-realm managed runtime manifest. The kind
// records how the file entered the runtime so Portalkeeper can later decide
// whether a runtime file is safe to repair, re-link or delete.
public sealed class ManagedRuntimeFileEntry
{
    public string RuntimeRelativePath { get; init; } = string.Empty;

    // Source-relative path for baseline-derived entries; empty for RealmOwned.
    public string SourceRelativePath { get; init; } = string.Empty;

    // Expected SHA-256 when one is recorded.
    public string Sha256 { get; init; } = string.Empty;

    public ManagedRuntimeFileKind Kind { get; init; } = ManagedRuntimeFileKind.RealmOwned;
}