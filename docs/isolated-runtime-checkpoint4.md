# Isolated realm runtimes — Checkpoint 4

**Checkpoint 4 does not modify WoW executable behavior.** It establishes ownership
and launch plumbing for an independent, byte-identical copy of the user's
verified source executable. It implements no binary patching, FrameXML bypass,
integrity changes, signatures, or transformation recipes. No Blizzard executable
content is included or redistributed.

## Ownership and naming

`Settings.ClientPath` still identifies the SOURCE installation. An isolated
runtime retains its existing baseline `Wow.exe`, baseline inventory, realm
patches/addons, ledgers, realmlist, and WTF. The additional executable is a real
copy, never a hard link. Source and baseline executables are never opened for
writing by this service.

Names use a readable ASCII realm label (non-alphanumeric runs become `-`, trimmed
and limited to 40 characters, with `Realm` as the empty fallback), followed by
`-<full stable realm identity>.exe`. For example, a realm named `Example Realm`
gets `Example-Realm-<64-hex-realm-id>.exe`. The complete existing identity
separates realms with the same sanitized label. The suffix also avoids reserved
DOS device filenames. Names are deterministic, portable, and at most 109
characters for current identities. No realm names or IDs are hard-coded.

Existing entries are checked case-insensitively even on Linux. An unknown file,
directory, or baseline entry at the generated name is a collision, even if its
bytes already match. Portalkeeper never adopts or overwrites it. Managed path
checks reject traversal, symlinks, junctions, and paths outside their root.
Realm content operations cannot overwrite the owned executable or its ancestors
or descendants.

## Manifest extension

The existing `.portalkeeper/managed-runtime.json` remains schema version 1.
An optional `RealmExecutable` object records:

- `SourceRelativePath`: the verified source executable filename, under
  `SourceClientPath`.
- `SourceSha256`: its verified SHA-256, also matching the original runtime
  `SourceExecutableSha256`.
- `RuntimeRelativePath`: the deterministic realm executable filename.
- `Sha256`: the current realm executable SHA-256.
- `Generation`: `1` for this baseline-copy format.
- `State`: `BaselineCopy` (numeric `0`, following existing enum serialization).

The executable also has a `Files` entry with `Kind=RealmOwned` and its hash.
`LaunchExecutableRelativePath` now refers to that file. The existing copied
baseline executable entry remains unchanged. Manifest validation requires all
these fields to agree and rejects unsupported generation/transform states.
CP4 requires the source and generated hashes to be identical.

Absent/null `RealmExecutable` remains valid for reading and baseline inspection
of CP2/CP3 manifests. It is insufficient for isolated launch readiness. The
existing `Complete` baseline state and content fingerprint retain their meanings;
CP4 adds the executable readiness requirement.

## Lifecycle and migration

Normal ENTER REALM uses the existing preparation service:

1. Validate the source with `ClientService` and the realm's existing executable
   requirements, including its SHA-256 when configured.
2. Validate all existing baseline files and runtime/realm/source identity.
3. Provision required content using the CP3 runtime-local services.
4. If executable metadata is absent, create the independent realm executable.
   If it is present and valid, reuse it without rewriting it. A missing owned
   executable can be regenerated. No arbitrary runtime/custom executable is used
   as input: the source must match the recorded baseline hash, and a managed
   runtime cannot serve as the source.
5. Restore the content fingerprint only after full runtime and executable
   validation, then resolve the effective runtime as before.

For a new runtime this occurs inside the existing staging directory before
whole-runtime promotion. The builder reloads the manifest after provisioning to
preserve the executable metadata. Its explicit baseline-only construction API
remains available; normal preparation upgrades that baseline before launch.

Valid CP2/CP3 runtimes upgrade in place. Their runtime location, creation time,
baseline files, content, and personal runtime state are retained. Neither Forget
Runtime nor reconstruction is required merely because metadata is absent. This
uses the same generic path for all realm identities, including existing live
realms; the implementation does not touch live installations during testing.

## Atomic creation and recovery

Creation copies only the verified SOURCE to a uniquely named, create-new
`.portalkeeper/realm-executable-<guid>.tmp`. The copy is flushed, hashed, checked
against the source, and checked for a single native filesystem link. Identity
inspection must succeed on Linux/Windows; inability to prove independence fails
closed. Source validation is repeated before recording ownership.

After the temporary copy passes, the existing atomic manifest save reserves its
ownership. A non-overwriting rename promotes the copy to the final name, followed
by validation. The reservation is intentional: interruption between metadata save
and rename leaves a missing owned file that is not launch-ready and can be retried.
No second ledger or separate ownership system is introduced. Handled failures
remove only the temporary file created by that attempt; unknown files are never
cleaned up. A hard process crash may leave an unreferenced temporary file, which
is not automatically swept.

No existing executable is overwritten, including an owned executable whose bytes
or independence no longer validate. Corrupt files are preserved and launch is
refused with a repair message. With the game closed, explicitly preserve/move the
unexpected file outside the runtime, investigate it, then enter the realm again
to regenerate the now-missing owned file. An unmanaged collision likewise needs
explicit user resolution. An unchanged valid executable is reused, so a later
preparation/content/source-validation failure cannot replace it.

Changed source bytes, incompatible metadata, or baseline damage require explicit
repair; CP4 does not silently regenerate the entire baseline or trust changed
source bytes simply because the filename matches. The realm's existing source
verification rules are retained; CP4 does not invent an authoritative stock hash
when the realm has not supplied one.

## Launch behavior

Isolated readiness checks content, baseline files, executable ownership, current
hash, source provenance, and independent file identity before launch state writes.
`RealmLaunchService` selects the manifest-backed realm executable. It never falls
back to runtime `Wow.exe` or the source after failure.

The working directory remains the runtime root. On Linux the existing Wine
selection and prefix discovery still use the SOURCE executable, while Wine's
argument is the REALM executable. Environment prefix precedence is unchanged.
Windows uses the realm executable directly with the runtime working directory.
Legacy bypasses all new executable preparation and keeps its existing executable
selection, working directory, and launch behavior. No realm.conf change is needed;
`RuntimeMode=Isolated` remains sufficient.

## Automated verification

All fixtures use synthetic client bytes. The Linux process test runs a temporary
no-op Wine script and verifies the actual executable argument, cwd, and inherited
prefix. It does not launch WoW.

Commands from the repository root (cached dependencies were available):

```sh
DOTNET_CLI_HOME=/tmp/pk-cp4-dotnet dotnet build src/Portalkeeper/Portalkeeper.csproj --no-restore
DOTNET_CLI_HOME=/tmp/pk-cp4-dotnet dotnet build tests/Portalkeeper.RuntimeTests/Portalkeeper.RuntimeTests.csproj --no-restore
DOTNET_CLI_HOME=/tmp/pk-cp4-dotnet dotnet run --project tests/Portalkeeper.RuntimeTests --no-build
DOTNET_CLI_HOME=/tmp/pk-cp4-dotnet dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint3
DOTNET_CLI_HOME=/tmp/pk-cp4-dotnet dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint4
git diff --check
```

For sandboxed cross-volume tests, set `PORTALKEEPER_TEST_CROSS_VOLUME_ROOT` to an
existing writable directory on a different filesystem from the fixture temp
root. Otherwise the original home-directory default remains. This session used
`/tmp` (tmpfs) for fixtures and a writable workspace directory (ext4) for that
variable; both cross-volume refusal checks then ran rather than being skipped.
The initial default-home run could not access the sandbox-protected home root;
that was a fixture permission issue, not a cross-volume runtime regression.

CP4 tests cover Legacy, generation/hash equality/independence, retained baseline,
metadata persistence, reuse, CP2 and CP3 upgrades, safe naming, collisions,
corruption, missing-file regeneration, hard-link/symlink rejection, source hash
failure, path traversal, unsupported state, content write protection, no fallback,
and source preservation. A Linux directory permission fixture forces rename
failure after ownership persistence and verifies cleanup and in-place recovery.
Windows-specific start-info assertions are included for running the suite there.

Application and harness builds succeeded with zero warnings/errors. CP1/2,
CP3, and CP4 fixture batteries passed. The available CP1/2 checks are in the
harness's default battery; there is no separate CP1 command.

## Remaining acceptance and limits

Native Windows execution and a real Wine/WoW graphics launch were not performed
here. Run the tests on Windows, then use normal ENTER REALM for each live realm
and verify the new executable argument/name, runtime cwd, unchanged source hash,
and independent inode/file identity. Existing runtime `Wow.exe`, addon/patch
contents, realmlist, WTF, and source-derived Wine environment should remain intact.

The CP3 interprocess game/provisioning coordination limitations remain; this is
not a defense against concurrent external filesystem tampering. Do not modify
runtime/source files while preparation or the game is running. Executable
transformation, automatic corrupt-file replacement, baseline reconstruction,
and executable modification/FrameXML bypass remain out of scope.
