# Isolated Realm Runtime Architecture

This document describes how Portalkeeper stages a per-realm **managed runtime**
derived from a user's existing World of Warcraft 3.3.5a installation, and the
exact state of the implementation after **Checkpoint 2**.

Isolated runtime behavior is **not the normal launch path**. ENTER REALM,
addon/patch management, Armory and the Armory continue to operate against
`ClientPath` exactly as before. Checkpoint 2 adds an explicit, transactional
runtime *constructor* and validator plus a small developer/test entry point
that builds an isolated runtime on demand. Nothing switches normal realms to a
runtime automatically, and `Settings.ClientPath` is never changed.

## Status

- Checkpoint 1 status: **implemented** (resolver seam, classification,
  baseline + runtime manifest models, realm identity)
- Checkpoint 2 status: **implemented** (transactional construction, validator,
  hard-link service, explicit baseline inventory, locale discovery, test
  harness, developer/test entry point)
- Isolated (source != effective) behavior: **not activated** for normal flow;
  available only through the explicit developer/test path in Settings.

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

The service provides serialization/deserialization and validation. In
Checkpoint 1 `Load`/`Save` were plumbing only. In Checkpoint 2 the builder
writes the manifest and the validator reads it; no normal application flow
creates a runtime manifest.

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

## Checkpoint 2: what is implemented

This checkpoint implements transactional runtime **construction** and
**validation** using an explicit baseline allowlist and hard links. It remains
an explicitly invoked developer/test capability; normal realms are unaffected.

### Baseline inventory

`Services/BaselineInventoryProvider.cs`

An explicit, locale-parameterized 3.3.5a baseline allowlist built from the
pristine reference client (`/home/smithkt/Downloads/WoWClient3.3.5a/` on the
author's machine):

- 8 root runtime files (`Wow.exe`, `WowError.exe`, `Battle.net.dll`,
  `DivxDecoder.dll`, `dbghelp.dll`, `ijl15.dll`, `msvcr80.dll`, `unicows.dll`).
  These are **copied** (never linked): they may be patched or replaced per
  realm and must remain independent. `Wow.exe` alone can carry the realm's
  authoritative `ExecutableSha256`.
- 7 stock global `Data` archives (`common-2.MPQ`, `common.MPQ`,
  `expansion.MPQ`, `lichking.MPQ`, `patch-2.MPQ`, `patch-3.MPQ`, `patch.MPQ`).
- required locale archives for `<locale>` (e.g. `locale-enUS.MPQ`,
  `expansion-locale-enUS.MPQ`, `lichking-locale-enUS.MPQ`, `patch-enUS.MPQ`,
  `patch-enUS-2.MPQ`, `patch-enUS-3.MPQ`) and optional locale archives
  (`speech-`, `expansion-speech-`, `lichking-speech-`, `base-`, `backup-`).
- the 23 stock loose Blizzard addons as explicit `Blizzard_*` files
  (`<AddOn>/<AddOn>.pub`), matching the pristine client. **No `Blizzard_`
  folder is trusted by name**; only these explicit files are baseline.

Every entry is relative only (rejected if it escapes), and `BaselineAsset` now
carries `Required` (default `true`; optional locale archives and baseline
addons are `Required = false` so an absent stock file is skipped, not fatal).

### Locale discovery

`Services/LocaleDiscovery.cs`

Determines the runtime locale from the source client by finding the single
`Data/<locale>/locale-<locale>.MPQ`. The fixture and real-client tests confirm
`enUS` detection. The caller may also supply an explicit locale.

### Hard-link service

`Services/HardLinkService.cs`

.NET managed code has no cross-platform hard-link create API, so this service
wraps native calls:

- Windows: `CreateHardLinkW` (Kernel32) plus `GetFileInformationByHandle`
  (`BY_HANDLE_FILE_INFORMATION` volume + file index) for identity checks.
- Linux: `link` (libc) plus `stat` (`st_dev` + `st_ino`, x86_64 struct layout)
  for identity checks.
- `TryCreateHardLink` never throws for filesystem failure classes; it returns
  `HardLinkFailure` (CrossDevice, PermissionDenied, DestinationExists,
  SourceNotFound, InvalidPath, Unexpected) and `DescribeFailure` maps it to a
  user-readable message.
- `SameFilesystem(left, right)` detects cross-volume construction up front.
- `CanVerifyFileIdentity` / `AreSameFile` let the builder and validator verify
  that a reported link is real (same inode) and that a copy is independent.

### Managed runtime builder

`Services/ManagedRuntimeBuilder.cs`

Transactional construction of one realm runtime under a runtime root:

```
<runtime-root>/
    <realm-id>/
        .portalkeeper/managed-runtime.json
        Wow.exe Data/... Interface/AddOns/...
    <realm-id>.staging-<unique>/     (temporary during construction)
```

Flow:

1. Validate the source client with the existing `ClientService.ValidateClient`
   and the realm's `ClientRequirements` — validation is never weakened.
2. Compute the source `Wow.exe` SHA-256; the realm's authoritative
   `ExecutableSha256` is recorded as the expected hash of the copied runtime
   `Wow.exe`.
3. Discover the locale (or accept an explicit one).
4. Load/validate the explicit baseline allowlist (`Required` semantics).
5. Derive the stable realm identity and the final runtime path.
6. Require the runtime root to be separate from the source client, and refuse
   to overwrite an existing final runtime.
7. Preflight: construction only works within a single volume. On an existing
   different-volume layout Portalkeeper **refuses and explains** rather than
   silently copying gigabytes. (Staging is Portalkeeper-owned; `Directory.Move`
   promotes it within the same volume.)
8. Build the staged runtime: root files are **copied**; `Data`, locale and
   baseline-addon files are **hard-linked** from the source (immutable shared
   inodes, zero extra data blocks) and every link is identity-verified.
9. A hard link that cannot be created, a missing required asset, or a file
   that fails to appear is an error; optional assets are skipped and recorded.
10. Write the `ManagedRuntimeManifest` (`State = Complete`, `RuntimePath` =
    final path) into `.portalkeeper/`, then validate the staged runtime.
11. Promote the staging directory to the final path atomically and revalidate
    the promoted runtime.

On failure the staging directory is deleted; if deletion fails the orphan path
is reported. The source client is never written; unknown or personal source
files (`patch-4.MPQ`, personal addons, etc.) are never inherited.

### Managed runtime validator

`Services/ManagedRuntimeValidator.cs`

Rejects anything that is not a complete, manifest-backed runtime:

- missing/invalid/unparseable manifest, non-`Complete` state
- realm identity / realm name mismatch, source client mismatch
- manifest `RuntimePath` that does not match the runtime's actual location
- missing runtime directories (`Data`, `Interface`, `Interface/AddOns`,
  `Data/<locale>`) or missing launch executable
- for each recorded file: exact containment, existence, SHA-256 verification
  when a hash is recorded, hard-link identity against its source for
  `LinkedBaseline`, and independence (not the same file) for `CopiedBaseline`

### Developer/test entry point

`src/Portalkeeper/ViewModels/MainViewModel.RuntimeTest.cs` and the
"ISOLATED RUNTIME (DEVELOPER TEST)" section in `SettingsWindow.axaml`:

- Shows the proposed, Portalkeeper-owned runtime location for the selected
  realm.
- CONSTRUCT TEST RUNTIME builds an isolated runtime from the selected source
  client (same-volume default application root).
- LAUNCH TEST RUNTIME launches the constructed runtime explicitly through the
  existing `RealmLaunchService` (still bypassed by normal ENTER REALM).
- FORGET RUNTIME clears the in-memory constructed path.

This path deliberately never changes `Settings.ClientPath`, never alters
`RealmRuntimeResolver`, and never launches through the normal ENTER REALM flow.

### Test harness

`tests/Portalkeeper.RuntimeTests/` — console harness, no external test
framework:

- default run: fixture battery (synthesized 3.3.5a client) covering
  construction, exclusion of unknown/personal content, source untouched,
  copied-vs-linked baseline semantics, manifest accuracy, validator
  acceptance/rejection, staging cleanup, overwrite refusal, traversal
  rejection, resolver unchanged, hard-link primitives, and cross-filesystem
  refusal.
- `real --source <path> --root <runtime-root> [--realm-conf <file>]`: builds
  and validates a runtime from a real client + realm configuration.

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

Construction (Checkpoint 2) works from the explicit baseline allowlist; a
per-realm manifest of Portalkeeper-managed components ("realm addons/patches in
the runtime") is the remaining future work:

```
verified Blizzard baseline addon files   -> hard-linked/copied into runtime (implemented)
Portalkeeper realm addons                 -> independent Portalkeeper-owned files (future)
unknown/personal source addons            -> NOT automatically inherited (implemented)
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

The intended later stream (Checkpoint 3+):

1. Build a runtime generation in a staging directory inside the managed root.
2. Acquire the baseline allowlist; for each explicit baseline file, verify the
   source against the recorded SHA-256 when one exists, then hard-link/copy into
   the staging runtime.
3. Install realm addons and patches into the staging runtime.
4. Atomically commit the staged runtime by moving/publishing it under the managed
   root and writing its `ManagedRuntimeManifest`.
5. Only a complete, manifest-backed runtime becomes the effective client used by
   addon/patch management and launch.

Checkpoint 2 implements steps 1–2 for the explicit baseline anyway (plus
validation and the explicit test path). Steps 3–5 remain **not active** for the
normal flow: no realm addons/patches are installed into a runtime, no runtime is
swapped in as the effective client automatically, and `ClientPath` is unchanged.

---

## Intentionally not implemented

- Making an isolated runtime the default/automatic launch or management client
- Installing realm addons or patches into a runtime
- Auto-repair / auto-rebuild of a runtime from the UI (an existing runtime is
  never overwritten; construction is refused with guidance instead)
- Copy fallback across volumes (refused with an explanation)
- Changing `Settings.ClientPath`
- Changing the normal addon or patch destinations
- Modifying `realm.conf` schema
- `Eitrigg.exe`
- `Wow.exe` patching
- FrameXML bypasses
- Migrating existing users or cleaning legacy realm files
- UI redesign
- A complete 12340 baseline hash inventory (only the realm-supplied Wow.exe
  hash is authoritative today; other baseline files are path-allowlisted only)

## Verification

- `dotnet build src/Portalkeeper/Portalkeeper.csproj` succeeds with zero warnings
  and zero errors.
- `tests/Portalkeeper.RuntimeTests` fixture battery: 46/46 checks passed on
  Linux (construction, exclusion of unknown content, source untouched, copied vs
  linked baseline semantics, staging cleanup, manifest accuracy, validator
  accept/reject, traversal rejection, overwrite refusal, resolver unchanged,
  hard-link primitives, cross-filesystem refusal).
- Real-client scenario on Linux: built an Eitrigg runtime from the actual
  3.3.5a source (`locale enUS`, 49 file entries, 0 skipped) and validated it
  successfully; inode/device identity confirmed genuine hard links for
  `Data/common.MPQ` and `Data/enUS/locale-enUS.MPQ`, and the runtime `Wow.exe`
  was an independent copy matching the realm's authoritative SHA-256 —
  `aa63a5...e88cb8`.
- Windows native paths (`CreateHardLinkW`, `GetFileInformationByHandle`) were
  compiled on Linux only; they require a Windows build plus runtime construction
  and identity verification before they are considered runtime-verified.
- Launching a constructed runtime remains **manual**: it requires a Windows or
  Linux desktop session with a real WoW session, so a game launch
  (construct → launch → log into the realm) from the Settings developer/test
  section is the follow-up manual test.