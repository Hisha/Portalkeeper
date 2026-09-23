# Isolated Realm Runtime Architecture

This document describes the architectural direction that will eventually let
Portalkeeper stage a per-realm **managed runtime** derived from a user's existing
World of Warcraft 3.3.5a installation, and the exact state of the implementation
after **Checkpoint 1**.

Runtime construction is **not active**. This build behaves exactly like v0.3.1
for an existing user. The work here is internal architecture and data-model
only.

## Status

- Checkpoint 1 status: **implemented**
- Managed runtime construction: **not implemented**
- Isolated (source != effective) behavior: **not activated**

---

## Definitions

### Source client

The user's existing World of Warcraft 3.3.5a installation selected in Settings.
This is what `settings.json` `ClientPath` stores today. Portalkeeper validates
it against build 12340 and uses it as the origin for an eventual runtime.
Portalkeeper does not consider the source tree itself to be a realm-owned
isolated runtime.

### Effective / launch client

The client root actually used by addon management, patch management, Armory
asset access, and launching. Today:

    SourceClientPath == EffectiveClientPath

Later, for an isolated realm:

    SourceClientPath != EffectiveClientPath

`RealmRuntimeResolver.ResolveEffectiveClientPath(sourceClientPath, realm)` is the
single seam through which callers obtain the effective root. Checkpoint 1 always
returns `sourceClientPath`, so every operation keeps its exact current
destination and launch target.

### Future managed runtime

An isolated, per-realm directory that Portalkeeper constructs and owns. It is a
generation of the realm's playable client derived from known baseline source
files plus Portalkeeper-managed realm addons and patches. The managed runtime
manifests (see below) are what will let Portalkeeper answer, after the fact:
"What did I create, where did it come from, may I repair/delete it, is it
complete, and does it still match its source and realm?"

---

## Backward compatibility (unchanged behavior)

Installing this build over Portalkeeper v0.3.1 requires no settings migration and
no re-selection of client or realm. Specifically, after Checkpoint 1:

| Operation | Destination |
|---|---|
| Launch target | existing `ClientPath` |
| Addon root | existing `ClientPath` |
| Patch root | existing `ClientPath` |
| WowPatch ownership ledger | existing `ClientPath` (`.portalkeeper/wow-patches.json`) |
| Armory asset root | existing `ClientPath` |

`ClientPath` continues to mean the user's selected WoW installation. The new
`EffectiveClientPath` view-model property resolves to that same value through
`RealmRuntimeResolver`. No new required persisted setting was introduced, so no
migration is needed.

---

## Checkpoint 1: what is implemented

### Runtime resolver

`Services/RealmRuntimeResolver.cs`

- `ResolveEffectiveClientPath(sourceClientPath, realm)` — always returns the
  source client path for now. The `realm` parameter is part of the seam so a
  later checkpoint can select a per-realm runtime.
- `ResolveRoots(sourceClientPath, realm)` — returns a `RealmClientRoots` value
  carrying `Source` and `Effective`, plus `IsIsolated`.

### Call-site classification

Every meaningful `ClientPath` use was classified as source or effective usage:

- **SOURCE** (left on `ClientPath`): loading/persisting the selected client,
  applying validated `ClientInfo`, and the settings save path. These describe
  the user's installation, not the launch client.
- **EFFECTIVE** (routed through `EffectiveClientPath`): realm launch readiness
  and launch, addon inspect/install/update/remove, patch inspect/
  install/update/remove, WowPatch allocation/ownership, launch-environment
  summary, and Armory preview asset access.

### Filesystem helpers

`Services/RuntimePaths.cs` — minimal, non-destructive helpers for later runtime
construction:

- `Canonical` / `SamePath` — canonical path comparison.
- `IsWithin(root, candidate)` / `Resolve` / `NormalizeRelative` — containment and
  relative-path safety, reusing `ManagedPath` for low-level validation. No
  overlays, junctions, symlink tricks, or runtime swapping are introduced.

`Models/Runtime/RealmClientRoots.cs` — distinguishes source vs effective roots.

### Baseline model

`Models/Runtime/BaselineManifest.cs`, `Models/Runtime/BaselineAsset.cs` and
`Services/BaselineManifestService.cs`

An explicit allowlist model that can describe:

- `Wow.exe`
- required root runtime DLL/files
- `Data/common.MPQ`, `Data/common-2.MPQ`, `Data/expansion.MPQ`,
  `Data/lichking.MPQ`, known Blizzard stock patch MPQs
- `Data/<locale>/<known baseline locale MPQs>`
- `Interface/AddOns/<known Blizzard baseline addon files>`

Design rules:

- A `BaselineAsset` is either a `File` or a `Directory`. Directory entries model
  known Blizzard addon trees and carry explicitly listed `Members` (client
  relative file paths). Runtime construction will operate only on those explicit
  members, never by scanning a folder or trusting a `Blizzard_` name prefix.
- The manifest is a fixed allowlist; it is never inferred from what happens to
  exist in a user's `Data` or `Interface/AddOns`.
- `Sha256` is optional. **No hashes are invented.** The repository currently
  contains no authoritative stock-client hash inventory (realm `ExecutableSHA256`
  and patch `SHA256` fields are empty), so the starter manifest carries no
  hashes. A future checkpoint must supply a complete 12340 inventory from an
  authoritative source.
- `Immutable = true` marks baseline files as shared/immutable baseline content,
  never treated as realm-owned, modifiable files.
- Validation rejects unsafe/traversing paths, `..`, duplicate entries, hashes on
  directories, and any entry under `.portalkeeper`.

`BaselineManifestService.WellKnownStockClient()` provides a **starter
category-level allowlist** (root `Wow.exe` and the stock `Data` archive names,
reusing the archive order already used by `ClientAssets`). It is documented as
incomplete: no hashes, no locale MPQs, no Blizzard addon file inventory.

### Managed runtime manifest model

`Models/Runtime/ManagedRuntimeManifest.cs`,
`Models/Runtime/ManagedRuntimeFileEntry.cs` and
`Services/ManagedRuntimeManifestService.cs`

The manifest can record:

- schema version
- stable/logical realm identity (`RealmId`) and realm name
- source client path and source executable SHA-256
- runtime generation (`Generation`)
- runtime path
- locale
- launch executable relative path
- creation/completion state plus timestamps
- managed file entries

Each `ManagedRuntimeFileEntry` distinguishes `LinkedBaseline`, `CopiedBaseline`,
and `RealmOwned`, and records runtime-relative path, source-relative path (for
baseline entries), and an optional expected SHA-256.

The service provides serialization/deserialization and validation. The
`Load`/`Save` file helpers are plumbing only; normal application flow does not
write a runtime manifest anywhere. No runtime manifests are created on disk.

### Realm identity

`Models/RealmIdentity.cs` exposes the existing logical realm identity algorithm
(`SHA-256` of upper-cased realm name, address, auth and world ports) used by
`WowPatchAllocationStore` as a single `FromRealm(RealmInfo)`. That store now
calls the shared helper instead of re-implementing it, so ownership ledgers
continue to use exactly the same identity.

`RealmIdentity.IsValid` accepts both the current 64-hex id and a GUID, so the
manifest model does not need rewriting when realm identity later evolves to an
administrator-provided stable `RealmID`/GUID. No `RealmID` is added to
`realm.conf` in this checkpoint.

---

## Ownership rules (design intent)

- **Immutable baseline**: verified baseline files are shared, immutable inputs
  to a runtime generation. They are never modified in place and never treated as
  realm-owned state.
- **Realm-owned**: realm addons/patches installed into the runtime are
  Portalkeeper-owned content recorded in the runtime manifest with explicit
  ownership.
- **Unknown/personal source assets**: not inherited.

### Known baseline vs unknown source assets

Later construction will work from the explicit baseline allowlist plus a
per-realm manifest of Portalkeeper-managed components:

```
verified Blizzard baseline addon files   -> hard-linked/copied into runtime
Portalkeeper realm addons                 -> independent Portalkeeper-owned files
unknown/personal source addons            -> NOT automatically inherited
```

### Why unknown source patches/addons are not inherited

A source client may contain personal addons, private MPQs, or modified files.
Blindly copying or linking everything would export unknown and unverified
content into every realm runtime, defeat per-realm isolation, and make it
impossible to answer "what did I create and may I delete it?" Only explicit
baseline allowlist entries and explicitly managed realm components enter the
runtime. This is why the baseline model requires explicit/known files rather
than trusting folders named `Blizzard_*`.

---

## Future transactional runtime construction (not implemented)

The intended later stream (Checkpoint 2+):

1. Build a runtime generation in a staging directory inside the managed root.
2. Acquire the baseline allowlist; for each explicit baseline file, verify the
   source against the recorded SHA-256 when one exists, then hard-link/copy into
   the staging runtime.
3. Install realm addons and patches into the staging runtime.
4. Atomically commit the staged runtime by moving/publishing it under the managed
   root and writing its `ManagedRuntimeManifest`.
5. Only a complete, manifest-backed runtime becomes the effective client used by
   addon/patch management and launch.

None of this is active. No runtimes are created, no hard links are made, and no
source files are touched by the new architecture.

---

## Intentionally not implemented

- Managed runtime directory creation
- Hard links to baseline files
- Copying baseline files into a runtime
- Moving/renaming/deleting files in the user's WoW installation
- Changing the launch target
- Changing addon or patch destinations
- Modifying `realm.conf` schema
- `Eitrigg.exe`
- `Wow.exe` patching
- FrameXML bypasses
- Migrating existing users or cleaning legacy realm files
- UI redesign
- A complete 12340 baseline hash inventory

## Verification

- `dotnet build src/Portalkeeper/Portalkeeper.csproj` succeeds with zero warnings
  and zero errors.
- The repository `Portalkeeper.slnx` references `tests/Portalkeeper.Tests` and
  `tests/Portalkeeper.UiTests`, which are absent from this checkout, so no
  automated test suite could be run. No test project was reconstructed in this
  checkpoint.
- Static checks confirm `EffectiveClientPath` resolves to `ClientPath`, realm
  behavior is unchanged by construction, and the new code creates no runtime
  directories, hard links, or source-modifying operations.