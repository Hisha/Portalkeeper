using System;
using System.Collections.Generic;

namespace Portalkeeper.Models.Runtime;

public enum BaselineAssetKind { File, Directory }

public enum BaselineAssetCategory { Root, DataMpq, LocaleMpq, BaselineAddOn, Other }

// One entry in an explicit baseline allowlist. Baseline entries describe known
// supported stock client files; they are never inferred by scanning a user's
// Data or Interface/AddOns folders. Directory entries model known Blizzard
// addon trees and are constructed only from their explicitly listed Members,
// never by wholesale folder scanning.
public sealed class BaselineAsset
{
    public string RelativePath { get; init; } = string.Empty;

    // Optional SHA-256. Empty until authoritative values are sourced; the
    // repository does not yet contain a complete hash inventory.
    public string Sha256 { get; init; } = string.Empty;

    public BaselineAssetKind Kind { get; init; } = BaselineAssetKind.File;

    public BaselineAssetCategory Category { get; init; } = BaselineAssetCategory.Other;

    // Baseline files are expected to be immutable/shared across realms and are
    // never treated as Portalkeeper-owned, realm-modifiable content.
    public bool Immutable { get; init; } = true;

    // Whether construction must fail when this allowlisted asset is missing
    // from the source client. Non-required assets are inherited only when the
    // source actually contains them (for example optional voice/base locale
    // archives and the loose Blizzard stub files present in the reference
    // client). Required assets are the ones a bootable 3.3.5a client depends
    // on. Defaults to true for the authoritative core inventory.
    public bool Required { get; init; } = true;

    // Explicit client-relative member files for Directory assets. Empty until
    // the authoritative stock addon inventory is supplied in a later step.
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();
}