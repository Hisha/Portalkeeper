namespace Portalkeeper.Models.Runtime;

// CP4 supports only an independent, unmodified copy of the verified source.
public enum RealmExecutableState { BaselineCopy }

public sealed class ManagedRealmExecutable
{
    public string SourceRelativePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string RuntimeRelativePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public int Generation { get; init; } = 1;
    public RealmExecutableState State { get; init; } = RealmExecutableState.BaselineCopy;
}
