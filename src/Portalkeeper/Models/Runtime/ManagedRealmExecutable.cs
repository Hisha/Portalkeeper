namespace Portalkeeper.Models.Runtime;

// Generation 1 remains an unmodified copy; generation 2 is the single fixed CP5 recipe.
public enum RealmExecutableState { BaselineCopy, FrameXmlDigestOverride }

public sealed class ManagedRealmExecutable
{
    public string SourceRelativePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string RuntimeRelativePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string? RecipeId { get; init; }
    public int? RecipeVersion { get; init; }
    public int Generation { get; init; } = 1;
    public RealmExecutableState State { get; init; } = RealmExecutableState.BaselineCopy;
}
