# Isolated realm clients — Checkpoint 3

## Configuration and compatibility

SchemaVersion remains **1**. Add the following optional setting to the existing
`[Client]` section (do not create a duplicate section):

```ini
[Client]
RuntimeMode=Isolated
```

`Legacy` and `Isolated` are accepted case-insensitively. Omission means `Legacy`.
An explicitly empty, numeric, or unknown value is an error. The public example
contains a commented setting, so its behavior remains legacy. Old clients that
ignore unknown settings cannot enforce isolation; realm operators should set
`[Portalkeeper] MinimumVersion` to the actual release containing this change
when that release is assigned. This checkpoint does not change the app version.

`Settings.ClientPath` is always the source installation. Selecting an isolated
realm never saves the runtime as the user's client. Legacy realms resolve to the
source and create no runtime automatically. There is no realm-name-specific code.
Realm identities and runtime storage paths retain the Checkpoint 1/2 design.

## First entry and lifecycle

ENTER REALM validates the source, explains that preparation leaves the original
installation unchanged, and prepares the selected realm on a background task.
Progress reports source validation, construction, required content provisioning,
and final validation. Users do not choose a replacement ClientPath.

A new runtime follows these steps:

1. Validate the source using the existing client requirements and validator.
2. Build the explicit baseline allowlist in a unique sibling staging directory.
3. Write the baseline manifest (`State=Complete`, empty `ProvisionedConfiguration`).
4. Install required patches and required addons into staging using their current
   configured sources. GitHub addon definitions use the existing discovery service.
5. Inspect required content, record its configuration fingerprint, and validate
   the entire baseline runtime.
6. Atomically rename staging to the realm's final directory and revalidate.
7. The resolver checks structure, fingerprint, and required content before
   returning the effective root. Launch checks again before writing launch state.

`Complete` retains its Checkpoint 2 meaning: baseline construction is complete.
The additive manifest field `ProvisionedConfiguration` is a SHA-256 fingerprint
of the realm's client requirements, addon definitions, and patch definitions.
Missing/empty means baseline-only or provisioning in progress, never realm-ready.
Old manifests deserialize with an empty fingerprint. Manifest replacement is
atomic. Read-only component inspection may use the expected directory before it
exists; that candidate path is never treated as an effective launch root.

An existing structurally valid Checkpoint 2 runtime can be provisioned in place.
Its readiness fingerprint is cleared before provisioning and restored only after
content and structural checks succeed. A failed attempt leaves it unavailable;
a later attempt may reuse successfully installed content. A structurally invalid
runtime is retained and reported as requiring explicit repair/rebuild. There is
no automatic deletion or destructive rebuild. Missing required content can be
reinstalled by preparation. Changed configuration invalidates readiness.

For a new runtime, failure removes only its owned staging directory. No failed
isolated operation falls back to installing into or launching the source.
The source remains available to legacy realms. Recommended/optional components
remain explicit user choices in the existing management UI; they do not gate
readiness. No arbitrary source addons or custom MPQs are inherited, and no
historical source ownership ledger is copied or cleaned up.

## Content and source safety

Patch operations receive the runtime root. Each runtime has its own
`.portalkeeper/wow-patches.json`, so different realms may both allocate
`Data/patch-4.MPQ`. WowPatchAllocationStore's allocation algorithm is unchanged.
Addon install/update/remove operations receive the runtime root and use its
`Interface/AddOns` directory and its local installation state/backups.

A concrete safety gap in activating existing component services was their ability
to target baseline paths. `ManagedRuntimeWriteGuard` rejects patch destinations
and addon directory operations overlapping manifest-backed baseline entries,
including bundled addon archive destinations. It also rejects content targeting
runtime management metadata. Legacy roots keep their existing behavior.

Two small builder correctness corrections accompany activation: a runtime root
identical to the source is rejected (the old containment check covered only
ancestors/descendants), and copied baseline files receive content hashes when no
allowlist hash exists, so corruption can be detected without a hard-link identity.
Existing link identity checks remain authoritative for hashless linked baselines.
No baseline inode is opened for writing by provisioning.

Realmlist, WTF/Config.wtf, and their `.portalkeeper/backups` stay in the runtime.
Managed launch state paths reject symlinks/junctions and baseline overlaps before
writing. Source WTF and realmlist are not copied or modified by isolated launch.

## Wine environment and Armory

`RealmLaunchService.PrepareAndLaunch` accepts the source directory separately from
the effective directory. The shared `CreateLaunchStartInfo` resolves the Wine
prefix using the **source executable** through the existing environment/desktop
launcher discovery. It still chooses wine/wine64 from PATH as before, passes the
runtime executable as the argument, and sets the runtime working directory.
Environment WINEPREFIX precedence remains unchanged. There are no user-specific
paths. Normal entry and the explicit developer runtime launch use this mechanism;
for an isolated realm the developer launch also provisions its constructed runtime.
Windows remains a native executable launch. Legacy Linux resolves the same source
executable and Wine environment as before. No prefix is permanently persisted;
source environment changes are picked up on subsequent launches.

Armory deliberately uses the **source** root, both from MainWindow and in its
fallback constructor. Its preview loader needs Blizzard client assets, which
exist before runtime preparation. Armory availability should not depend on a
realm's downloaded components or runtime readiness.

## Verification

All tests use synthetic client files. No real WoW client was tested or modified.
The Checkpoint 3 HTTP handlers supply deterministic patch/archive bytes without
network access; the Linux launch fixture executes a temporary no-op Wine script.

Commands from the repository root:

```sh
dotnet build src/Portalkeeper/Portalkeeper.csproj
dotnet build tests/Portalkeeper.RuntimeTests/Portalkeeper.RuntimeTests.csproj
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint3
git diff --check
```

The sandboxed run used repository-local DOTNET_CLI_HOME, NUGET_PACKAGES, and
TMPDIR, with a local NuGet feed from already cached packages. The application and
harness built with zero warnings/errors. Both fixture batteries passed. The
Checkpoint 2 cross-volume checks were skipped because the available fixture and
home paths share a filesystem. Windows execution and real-game launch remain
manual validation items.

A solution build was attempted but the checked-in solution references absent
`tests/Portalkeeper.Tests/Portalkeeper.Tests.csproj` and
`tests/Portalkeeper.UiTests/Portalkeeper.UiTests.csproj`. Those unrelated references
were not changed. The application and existing RuntimeTests were built directly.

The new fixtures cover legacy defaults, strict mode parsing, two realms allocating
the same filename with different payloads, separate ledgers, runtime-only addons,
source custom patch exclusion, source content/file-set/inode identity preservation,
failed construction, failed patch/addon provisioning, existing baseline migration,
failed existing-runtime provisioning, invalid-runtime refusal, baseline mutation
rejection, source Wine inheritance, legacy Wine behavior, and runtime launch state.

## Manual Linux acceptance plan

1. Build the application and run both fixture commands above. Use disposable
   client copies for deliberate failure/corruption tests; never corrupt a linked
   runtime file backed by a real source inode.
2. With Portalkeeper closed, record the selected source directory and hash its
   Wow.exe, Data files, Interface/AddOns files, WTF, and `.portalkeeper` files.
   Record the file list too. Preserve the original realm configurations.
3. Use a SchemaVersion=1 realm without RuntimeMode. Confirm ENTER REALM uses the
   source executable/working directory and retains its existing Wine prefix.
   Capture the source snapshot **after** this legacy launch has exited, since
   legacy launches intentionally maintain source launch state.
4. Set RuntimeMode=Isolated in two test realm configs with different identities,
   distinct required WowPatch payloads, and required addon downloads. Keep the
   same source ClientPath. Use the normal ENTER REALM button for the first realm.
5. Observe preparation progress. Verify the runtime under
   `${XDG_CONFIG_HOME:-$HOME/.config}/Portalkeeper/runtimes/<realm-id>` has a complete
   manifest and a nonempty ProvisionedConfiguration; source ClientPath is unchanged.
   Check actual ApplicationData location if XDG configuration differs.
6. Verify the game process executable argument/working directory point at that
   runtime and its WINEPREFIX matches the working source launch. Confirm the game
   renders normally. Exit and repeat for the second realm. Neither runtime should
   contain the other's MPQ payload; each ledger contains only its own realm ID.
   Both may use patch-4.MPQ. Source historical patch-4/patch-5 remain untouched.
7. In each realm's management UI, install/update/remove an optional patch and
   addon. Confirm only that runtime changes. Verify realmlist, WTF, and backups
   remain runtime-local and source hashes/file list still match step 3.
8. Restart Portalkeeper and re-enter each realm; ready runtimes should be reused.
   Open Armory before preparing a fresh third realm and confirm source assets load.
9. For a new disposable realm, use an unreachable required download. ENTER REALM
   must fail, with no final runtime, no source changes, and no fallback launch.
   Restore the URL and retry successfully.
10. With the game closed, on a **disposable fixture/copy only**, delete the runtime's
    copied Wow.exe or unlink a required baseline entry. ENTER REALM must report
    repair/rebuild needed and retain the directory. Do not edit MPQ bytes through
    a runtime hard link. Retest a legacy realm to confirm source availability.
11. Exercise the developer construction/launch controls with a separate test realm.
    Confirm they inherit the same Wine environment and isolated realms provision
    before launch. Keep the real source unchanged throughout isolated tests.

## Manual Windows acceptance plan

1. In PowerShell at the repository root, run the same dotnet build and fixture
   commands. Use a disposable validated WoW source on NTFS for failure tests.
2. Configure the source ClientPath and test a legacy realm first. Verify native
   Wow.exe launch. Exit and capture source file lists and SHA-256 hashes using
   `Get-ChildItem -Recurse -File` and `Get-FileHash`.
3. Ensure source and `%APPDATA%\Portalkeeper\runtimes` are on the same volume.
   Set RuntimeMode=Isolated for two distinct test realms with distinct required
   WowPatch payloads and addons. ENTER REALM must prepare without changing ClientPath.
4. Inspect each runtime manifest, local ledger, patch payload, and AddOns directory.
   Verify independent patch-4.MPQ allocations and no source custom content copied.
5. Confirm native process executable and working directory point at the selected
   runtime. Verify realmlist, WTF, and backups are runtime-local. Exit and compare
   source hashes/file lists with step 2. Repeat for the second realm.
6. Test optional component install/update/remove, restart/reuse, and Armory before
   preparing another realm. Confirm actions affect only the selected runtime.
7. Repeat the failed download and invalid-runtime checks from Linux steps 9–10
   using disposable copies. Failure must preserve source and deny isolated launch.
8. If possible, use a disposable source on a different volume. Preparation must
   report the existing cross-volume hard-link limitation without silently copying
   gigabytes, changing the source, or selecting an incomplete runtime.

## Limits and deferred work

No Windows runtime execution or actual Wine/WoW graphics smoke test was performed
in this session. Cross-volume automatic copy fallback, repair UI, cancellation,
interprocess game/provisioning coordination, and personal-addon replication remain
future work. Existing optional-component UI and GitHub discovery/cache policies
are retained. Hashless remote patches retain existing presence-based validation;
realm operators should publish SHA-256 values. Ready-runtime inspection is local;
checking for upstream updates remains the existing configuration/addon refresh flow.

Source Blizzard assets must remain present and immutable while hard-linked runtimes
use them. No source cleanup, binary patching, executable generation, FrameXML
bypass, Native Social changes, server module changes, or Blizzard redistribution
is included. No commits or pushes were made.

## Exact file inventory and final git status

Added (untracked until the user chooses to stage):

- `docs/isolated-runtime-checkpoint3.md`
- `src/Portalkeeper/Services/ManagedRuntimeWriteGuard.cs`
- `src/Portalkeeper/Services/RealmRuntimePreparationService.cs`
- `tests/Portalkeeper.RuntimeTests/Checkpoint3.cs`

Modified tracked files:

- `config/example.realm.conf`
- `src/Portalkeeper/Models/RealmInfo.cs`
- `src/Portalkeeper/Models/Runtime/ManagedRuntimeManifest.cs`
- `src/Portalkeeper/Services/AddonInstallerService.cs`
- `src/Portalkeeper/Services/ManagedRuntimeBuilder.cs`
- `src/Portalkeeper/Services/ManagedRuntimeManifestService.cs`
- `src/Portalkeeper/Services/PatchService.cs`
- `src/Portalkeeper/Services/RealmConfigurationService.cs`
- `src/Portalkeeper/Services/RealmLaunchService.cs`
- `src/Portalkeeper/Services/RealmRuntimeResolver.cs`
- `src/Portalkeeper/ViewModels/ArmoryViewModel.cs`
- `src/Portalkeeper/ViewModels/MainViewModel.RuntimeTest.cs`
- `src/Portalkeeper/ViewModels/MainViewModel.cs`
- `src/Portalkeeper/Views/MainWindow.axaml.cs`
- `tests/Portalkeeper.RuntimeTests/Program.cs`

All listed changes are unstaged. No pre-existing changes were present. No commit
or push occurred. Generated build output is ignored. The archive is
`dist/Portalkeeper-IsolatedRuntime-Checkpoint3.zip` (also ignored) and contains
only these changed/new repository-relative source, test, configuration, and
documentation files. It contains no client files, binaries, runtime trees,
downloaded content, transcripts, or temporary test artifacts.
