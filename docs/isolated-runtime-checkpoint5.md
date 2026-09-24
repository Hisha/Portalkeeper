# Checkpoint 5 — deterministic FrameXML executable generation

## Status and policy

The clean title-change proof is **PROVEN BY GAME STARTUP**, as reported by Kevin
and recorded in `isolated-runtime-checkpoint5-investigation.md`. The implementation
supports one fixed recipe for one exact source executable. It does not distribute
client binaries or add configuration for arbitrary executable modification.

> Superseded by Checkpoint 6 (v0.4.1): `docs/isolated-runtime-checkpoint6.md`.
> This document remains the authoritative specification of the fixed recipe and
> the recoverable promotion transaction. The Checkpoint 5 policy sentence
> "Normal preparation advances every Isolated runtime with the supported source
> to executable Generation 2" no longer holds. Since v0.4.1, generation 2 applies
> only to Isolated realms whose realm configuration declares `Requirements=
> protected-framexml`; unselected realms use the copied baseline executable. The
> recipe, the transaction, and the CP5 safety rules for realms that do require
> generation 2 are unchanged.

Normal preparation advances every **Isolated** runtime with the supported source
to executable Generation 2. Generation 1 remains readable/valid for migration but
is no longer sufficient for Isolated launch. Unsupported sources fail before
content downloads/runtime preparation; Legacy retains its existing behavior.
This is the milestone policy explicitly authorized for CP5. There is no new
realm.conf field, toggle, change to realm identity, or runtime reconstruction.

## Fixed recipe and identities

`FrameXmlDigestOverrideRecipe` owns the constants and code:

| Field | Value |
| --- | --- |
| RecipeId | `wow-12340-framexml-digest-acceptance` |
| RecipeVersion | `1` |
| Input | WoW 3.3.5a build 12340, PE32/i386, 7,704,216 bytes |
| Source SHA-256 | `aa63a5750d60ef16746c686b3d5e26876d98953eab08b1c026cd0faf78e88cb8` |
| Output SHA-256 | `15c47945d5c461fda32a34645a23a9f3488bbb2b10855fe328c0e866d9ca8001` |
| Section / target RVA | `.text` / `0x0012AEBC` |
| Derived, guarded file offset | `0x0012A2BC` |
| Expected / replacement | `FF AB 52 00` / `1C AC 52 00` |

The PE reader must identify PE32/i386, image base `0x400000`, six sections, and
exactly one `.text` with RVA `0x1000`, raw offset `0x400`, raw size `0x5DD400`,
virtual size `0x5DD3B3`. Each guarded RVA must map uniquely to file-backed `.text`
bytes and the known expected file offset. All fixed byte guards must match:

- Four-entry table at RVA `0x12AEB4`: `E5AB5200 F2AB5200 FFAB5200 1CAC5200`.
- Indexed dispatch at RVA `0x12ABDE`: `FF2485B4AE5200`.
- Verifier call at RVA `0x12ABD1`: `E80ABA2E00`.
- Original result-2 entry at RVA `0x12AEBC`: `FFAB5200`.

Input size and full SHA-256 are checked before PE parsing/transformation. The one
replacement changes only offsets `0x12A2BC` and `0x12A2BD`. Both size and the fixed
output SHA-256 must match after generation and after the temporary file is read
back. No fuzzy matching, external recipe, unknown input, configurable replacement,
or alternate output identity is supported.

This changes FrameXML's acceptance of a valid-signature/content-digest mismatch
for the entire referenced FrameXML/Bindings tree. Missing/corrupt signature cases,
GlueXML and addon callers, shared cryptographic routines, the later preflight/load
consistency comparison, authentication, server validation, and anti-cheat code
are unchanged. It is not an allowlist for a single Lua file.

## Manifest representation and validation

Manifest schema remains 1. The existing `RealmExecutable` object retains
`SourceRelativePath`, `SourceSha256`, `RuntimeRelativePath`, `Sha256`, `Generation`,
and `State`, adding nullable `RecipeId` and `RecipeVersion` only.

| Executable generation | State (serialized numeric value) | Recipe fields | Current hash |
| --- | --- | --- | --- |
| 1 | `BaselineCopy` (`0`) | null/absent | verified baseline/source hash |
| 2 | `FrameXmlDigestOverride` (`1`) | fixed ID above / `1` | fixed output hash above |

Generation 2 source provenance must equal the fixed source identity and the
manifest's `SourceExecutableSha256`. Both states require a separate `RealmOwned`
file record matching the current hash, a `CopiedBaseline` source executable
record matching the source hash, the deterministic CP4 realm filename, and a
matching `LaunchExecutableRelativePath`. Generation 1 cannot carry recipe
metadata or the known transformed output hash. Unknown/impossible state,
generation, recipe, version, source, hash, ownership, or launch combinations fail.

The expected transformed hash comes from the compiled recipe, not an arbitrary
manifest-supplied hash. `ManagedRuntimeValidator` continues to inspect both
legitimate generations through the common manifest/executable validators.
The resolver separately requires Generation 2 and refuses pending transactions.

## Generation and recoverable promotion

`RealmExecutableService` serializes executable preparation with an exclusive
`.portalkeeper/realm-executable.lock` handle. It checks source/baseline/realm
identity before mutation. The stable lock file remains after use.

1. Read only the verified SOURCE executable into memory. Never read an existing
   realm executable as transformation input.
2. Create a unique independent `.portalkeeper/realm-executable-<guid>.tmp`,
   flush the baseline copy, verify independence, validate source identity and PE
   guards again, then write only the fixed generated result to that temporary file.
3. Flush/read back the temporary result, verify exact size/hash and independence,
   validate the proposed Generation 2 metadata, and revalidate source and the old
   owned executable (when present).
4. Durably write then atomically rename a transaction record to
   `.portalkeeper/realm-executable-generation2.json`. It contains the complete
   prior and proposed manifest JSON plus one unique backup path under `.portalkeeper`.
   An existing transaction is recovered, never overwritten as a fresh reservation.
5. For a valid existing Generation 1, `File.Replace` atomically promotes the new
   file and preserves the previous executable at the journal's backup path. For a
   missing owned/new target, use non-overwriting `File.Move`. No live executable
   is opened for in-place writing.
6. Verify the promoted file, atomically replace `managed-runtime.json`, then
   validate once more. Remove only the verified journal-owned backup and journal.
   The runtime executable filename remains the CP4 filename.

These are recoverable atomic file operations, not a claim that two files can be
renamed in a single filesystem transaction. The journal gates launch throughout
the gap. Prior executable bytes and prior manifest JSON remain recoverable until
commit/validation succeeds. Recovery recomputes the one permitted next manifest,
requires the active manifest to match the prior or proposed state, validates the
backup path/content/identity, and either regenerates from SOURCE or finishes the
already-promoted exact output. Unexpected states require explicit repair.

A Unix interruption after creating the replacement backup link but before rename
can leave the old target and backup as exactly two links to the same inode.
Recovery recognizes only that exact journal-owned/source-identical pair, removes
its backup link, verifies independence, and retries. Extra links are not accepted.
Windows handle-sharing rules are respected when inspecting the lock file.

Handled exceptions clean only that attempt's temporary output. Abrupt exit can
leave an unreferenced temporary file; it is never adopted or swept automatically.
A pending journal blocks launch even when its output has already been promoted.
ENTER REALM retries recovery. Malformed metadata, journal inconsistency, unexpected
backup, or unexpected target is preserved with an explicit error.

File contents are flushed before promotion. Sudden storage loss/power-loss
semantics depend on the filesystem; this is not a database with directory-fsync
or hardware durability guarantees. External filesystem tampering and concurrent
content modification remain outside CP3/CP4 coordination guarantees. Close game
processes before migration. A locked Windows executable causes an explicit failure,
not a fallback or additional transformation.

## Migration, reuse, repair, and launch

A ready CP4 runtime first migrates/reconciles its executable before any content
fingerprint is cleared. If content remains current, preparation returns without
reprovisioning. Runtime path, realm identity, timestamps of baseline/content files,
patch/addon ledgers, installed patches/addons, WTF, realmlist, and the provisioned
configuration are preserved. CP2/CP3 baselines receive Generation 2 directly.
New runtimes generate inside staging before whole-runtime promotion.

A valid Generation 2 is reused without rewriting executable or manifest. A missing
owned file regenerates safely from verified SOURCE. A corrupt, incorrectly sized,
shared/hard-linked, symlinked, or otherwise unexpected existing target is **never
overwritten automatically**. Close the game, preserve/move the unexpected file
outside the runtime, investigate it, then retry preparation only after resolving
ownership. Unmanaged collisions are never adopted, even with the exact output
bytes. Invalid recipe metadata requires explicit repair; merely changing a hash
in a manifest is not a supported repair.

Isolated launch requires the validated Generation 2, runtime cwd, ready content,
and no pending transaction. There is no Generation 1/baseline/SOURCE fallback.
Wine executable/prefix discovery remains SOURCE-derived; its executable argument
is the realm executable. Windows uses that executable natively with runtime cwd.
Legacy does not enter the generation/recipe path.

## Certificate and Windows limitation

Recipe v1 leaves all WIN_CERTIFICATE bytes and the stored PE checksum unchanged,
exactly matching the proven artifact. The original image signature cannot attest
to the changed `.text`, and the stored checksum is stale. There is no certificate
removal/modification, re-signing, checksum repair, or OS trust-policy bypass.
**Native Windows live acceptance is still required.** If policy blocks execution,
report it and stop rather than changing recipe v1 or disabling that policy.

## Automated validation

Run from the repository root, supplying a local verified executable (not a
committed fixture):

```sh
export PORTALKEEPER_CP5_EXE=/path/to/verified/Wow.exe
export DOTNET_CLI_HOME=/tmp/pk-cp5-dotnet
dotnet build src/Portalkeeper/Portalkeeper.csproj --no-restore
dotnet build tests/Portalkeeper.RuntimeTests/Portalkeeper.RuntimeTests.csproj --no-restore
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint3
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint4
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint5
PYTHONDONTWRITEBYTECODE=1 python3 tests/test_framexml_12340_analysis.py -v
git diff --check
```

Use `PORTALKEEPER_TEST_CROSS_VOLUME_ROOT` for an existing writable alternate-volume
test directory when needed. Optional Python stock-archive checks additionally use
`PORTALKEEPER_CP5_STOCK_CLIENT` and installed StormLib.

The default CP1/2 battery remains synthetic. CP5 malformed-PE/guard and unsupported
source tests are synthetic. CP3/CP4 isolated preparation regressions and CP5 exact
hash/lifecycle checks now require the supplied executable, copied only into unique
`/tmp/pk-cp*` fixture trees. Without it they explicitly skip; that is not equivalent
to a full run. No production identity gate is weakened for tests. The Linux launch
test invokes a disposable Wine stand-in, not the real game. Windows-specific
start-info checks require running the suite there.

Coverage includes fixed output/two-byte differences, source/baseline preservation,
independence, malformed PE/maps/guards, manifest combinations, reuse, in-place
migration, retained content/personal state, missing-file repair, preserved
corruption/collisions, fault-injected transaction boundaries, abrupt child-process
exits, recovery journal tampering, the Unix backup-link window, no launch fallback,
actual Wine argument/cwd/SOURCE prefix, and CP1–4 regression behavior.

Actual Linux results: application and runtime harness builds passed with zero
warnings/errors; CP1/2 passed 46 checks, CP3 passed 49, CP4 passed 56, CP5 passed
115, and Python analysis passed all 10 tests with zero skips. The cross-filesystem
checks ran. Native Windows checks and a real game launch were not run by the agent.

A solution-wide `dotnet build --no-restore` was also attempted and failed because
`Portalkeeper.slnx` references absent `tests/Portalkeeper.Tests/Portalkeeper.Tests.csproj`
and `tests/Portalkeeper.UiTests/Portalkeeper.UiTests.csproj`. Those pre-existing
references were left unchanged; the available application and runtime test projects
were built directly as shown above.

## Files changed for implementation

Paths are relative to `/home/smithkt/git/Portalkeeper`.

| Change | File |
| --- | --- |
| Added fixed recipe | `src/Portalkeeper/Services/FrameXmlDigestOverrideRecipe.cs` |
| Added focused implementation tests | `tests/Portalkeeper.RuntimeTests/Checkpoint5.cs` |
| Added implementation/acceptance documentation | `docs/isolated-runtime-checkpoint5.md` |
| Updated clean game-startup evidence | `docs/isolated-runtime-checkpoint5-investigation.md` |
| Added state and recipe metadata | `src/Portalkeeper/Models/Runtime/ManagedRealmExecutable.cs` |
| Added test access to internal guard/fault boundaries | `src/Portalkeeper/Portalkeeper.csproj` |
| Added exact link-count inspection for interrupted replacement | `src/Portalkeeper/Services/HardLinkService.cs` |
| Added strict generation validation and flushed manifest writes | `src/Portalkeeper/Services/ManagedRuntimeManifestService.cs` |
| Reject pending generation during runtime validation | `src/Portalkeeper/Services/ManagedRuntimeValidator.cs` |
| Added generation transaction and recovery | `src/Portalkeeper/Services/RealmExecutableService.cs` |
| Added early source gate and in-place upgrade sequencing | `src/Portalkeeper/Services/RealmRuntimePreparationService.cs` |
| Require Generation 2 for launch readiness | `src/Portalkeeper/Services/RealmRuntimeResolver.cs` |
| Adapted existing regressions to exact-source policy | `tests/Portalkeeper.RuntimeTests/Checkpoint3.cs` |
| Adapted ownership/launch regressions to Generation 2 | `tests/Portalkeeper.RuntimeTests/Checkpoint4.cs` |
| Added CP5 and crash-child test entry points | `tests/Portalkeeper.RuntimeTests/Program.cs` |

The investigation document was already untracked when implementation began and
is updated in place. The pre-existing untracked `scripts/analyze-framexml-12340.py`
and `tests/test_framexml_12340_analysis.py` are unchanged; their tests were rerun.
No unrelated repository changes, executable fixtures, commits, or pushes are included.

## Manual live acceptance — Kevin only

Automated development stops before this procedure. No live Eitrigg/PTR upgrade or
game launch is performed by the implementation tests.

1. Close WoW and use the newly built Portalkeeper. Keep the configured ClientPath
   pointing to the unchanged SOURCE. Do not replace source/runtime baseline files
   or hand-edit executable metadata. Keep a private backup of each current runtime's
   manifest and realm executable before acceptance.
2. Record SOURCE SHA-256 above. For Eitrigg, then PTR, locate the existing runtime
   from its manifest (the existing `190B…` / `85EA…` directories). Record the
   runtime path, realm identity, baseline `Wow.exe` hash, patch/addon hashes,
   WTF/realmlist state, and content fingerprint. Do not reset/rebuild the runtime.
3. Select Eitrigg and click normal **ENTER REALM** once. Confirm it upgrades the
   existing runtime and launches its existing realm filename. Inspect
   `.portalkeeper/managed-runtime.json`: `RealmExecutable.Generation=2`, `State=1`,
   fixed recipe ID/version, exact source/output hashes, correct owned file record
   and launch path. The file must be 7,704,216 bytes and independently owned
   (Linux `stat` link count 1, distinct device/inode identity from source/baseline).
   No pending `realm-executable-generation2.json` should remain after success.
4. Verify process executable argument and cwd; on Linux verify the accepted
   SOURCE-derived Wine prefix/environment. Enter world; open the native Friends,
   Social, and Guild interfaces. Record Lua errors and observed behavior.
5. Close the client. Verify source/baseline hashes and content/realm identity/path
   are unchanged by migration. Normal launch may legitimately update runtime WTF,
   realmlist, and game caches; distinguish those from preparation resetting them.
   Enter again and confirm the generated executable hash and modification time
   are unchanged (reuse).
6. Repeat steps 3–5 for PTR. Verify its independent executable and same-slot MPQs
   remain separate from Eitrigg. Confirm no unnecessary patch/addon download or
   content reset occurs for a current configuration.
7. For native FrameXML acceptance, use the **same clean title-change proof MPQ**
   through the established realm content workflow, recording its hash. Confirm
   `CP5 FRAMEXML TEST` is visible in the native Friends frame after entering world,
   and ordinary Social/Guild behavior remains functional. The broken 000019 UI
   is not an acceptance fixture. Do not install the proof into SOURCE.
8. Repeat native acceptance on Windows when available, including trust-policy
   behavior. Report failures with platform, runtime, recipe/file hashes, pending
   transaction state, and logs. Do not fall back, adjust certificate/checksum,
   remove transaction records blindly, or broaden the recipe.

No Blizzard binaries are checked into source or included with documentation.
No commit or push is performed for this checkpoint without a separate request.
