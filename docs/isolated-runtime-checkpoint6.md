# Checkpoint 6 — capability-driven realm executable selection

## Status and policy

Checkpoint 5 added one fixed generation-2 transformation with a temporary policy
that advanced **every Isolated** realm to executable Generation 2. Checkpoint 6
(v0.4.1) replaces that blanket policy with an explicit capability declared by the
realm itself: `[Client] Requirements=protected-framexml` in realm.conf.

* A realm that declares `Requirements=protected-framexml` requires the exact
  verified CP5 source and receives the fixed Generation 2 transformation. All CP5
  safety, ownership, and recovery rules apply unchanged for these realms.
* An Isolated realm that does **not** declare the requirement is a normal CP5-era
  isolated runtime: it uses its copied baseline executable. It does not enter the
  recipe path, the exact-source gate, or the generation transaction.
* Legacy realms are unaffected. Requirements participate in the configuration
  fingerprint, so changing them is a normal (re-provision) configuration change,
  never a runtime identity or schema change.

No realm.conf recipe identifiers, offsets, hashes, or generation numbers are ever
accepted from configuration. `protected-framexml` is the only known requirement
value; an unknown value fails closed.

## realm.conf capability model

`[Client] Requirements=` is an optional, comma/semicolon/whitespace separated list
of semantic capability names. The model is:

| Field | Meaning |
| --- | --- |
| `Requirements` | client capabilities the realm needs, normalized in `ClientRequirements` |

Normalization: each token is split on comma, semicolon, and whitespace, trimmed,
and compared case-insensitively; duplicates collapse and the result is sorted
using ordinal rules. The parsed value is therefore deterministic and always maps
to an identical configuration fingerprint. Known values:

| Requirement | Meaning |
| --- | --- |
| `protected-framexml` | the realm requires the fixed FrameXML digest acceptance transformation |

Sampling valid and invalid forms:

| Configuration | Result |
| --- | --- |
| key absent / `Requirements=` | empty list, baseline execution |
| `Requirements=protected-framexml` | generation 2 required |
| `Requirements=Protected-FrameXml;protECTED-FRAMEXML` | single normalized `protected-framexml` |
| `Requirements=anythingelse` | parse/loading fails closed |

Parsing rejects control characters. Unknown requirement values fail at load via
`ClientRequirements.ThrowIfUnsupportedRequirement`, and the same check runs as
defense-in-depth in `RealmRuntimePreparationService.PrepareAsync`,
`RealmRuntimeResolver.RequireReady`, `RealmExecutableService.Prepare`, and
`RealmExecutableService.SelectLaunchExecutable` for programmatic configurations.

## Capability to generation mapping

`RealmExecutableService.RequiresGeneration2(realm)` is true only for an Isolated
realm whose client requirements include `protected-framexml`.

### Preparation (`RealmRuntimePreparationService.PrepareAsync`)

* For every Isolated realm the exact CP5 source gate
  (`FrameXmlDigestOverrideRecipe.ValidateSource`) runs **only** when generation 2
  is required. Realms without the requirement may run from any valid supported
  3.3.5a client.
* `RealmExecutableService.Prepare` (the generation transaction) is invoked only
  when generation 2 is required, both when upgrading an existing runtime in place
  and when constructing a staging runtime. `Prepare` itself refuses generation 2
  for realms that do not require it.
* Realms without the requirement skip the recipe path entirely; content
  provisioning and fingerprint behavior are unchanged.

### Readiness (`RealmRuntimeResolver.RequireReady`)

The previous unconditional "Generation 2 is required" check becomes
capability-based: it fires only when `RequiresGeneration2(realm)` and the
recorded executable is not Generation 2. Baseline-only and Generation-1-owned
states remain acceptable when no capability demands generation 2.

### Launch selection (`RealmExecutableService.SelectLaunchExecutable`)

Selection makes the current realm.conf authoritative:

1. When generation 2 is required, the owned Generation 2 executable is validated
   and returned (`RequireValid`). No baseline/source fallback exists for a
   required generation.
2. When generation 2 is not required but a Generation 1 owned executable is
   recorded, that file is validated and returned.
3. Otherwise the copied baseline executable recorded for `realm.Client.Executable`
   is validated for existence, hash, and independence and returned.

A leftover Generation 2 artifact kept in the manifest of a realm that no longer
requires it is preserved untouched and is simply not the launch target.

## Fingerprint participation

`RealmRuntimePreparationService.ConfigurationKey` already serializes the full
`realm.Client`, so the new `Requirements` property automatically participates in
the one-time-v0.4.0 style fingerprint drift for every realm and in every
subsequent requirement change. Adding or removing `protected-framexml` is a
normal re-provisioning configuration change; runtime identity, paths, timestamps,
and owned files are preserved. The runtime's generation is reconciled only when
the capability is required.

## Lifecycle behavior

* **Upgrade**: an existing Isolated runtime that used the baseline gains the
  requirement via realm.conf; normal preparation generates Generation 2 in place
  with the CP5 recoverable transaction and re-provisions content for the new
  fingerprint.
* **Downgrade**: a realm that previously prepared Generation 2 drops the
  requirement. Preparation re-provisions content for the new fingerprint, leaves
  the valid Generation 2 artifact and its manifest record untouched, and launch
  selects the copied baseline.
* **Reuse**: a ready runtime for an unchanged configuration is returned without
  rewriting the executable or manifest.
* Unknown requirements block preparation, readiness, generation, and launch
  regardless of runtime mode.

## Automated validation

Run from the repository root, supplying a local verified executable (not a
committed fixture) for the generation-dependent lifecycle parts:

```sh
export PORTALKEEPER_CP5_EXE=/path/to/verified/Wow.exe
dotnet build tests/Portalkeeper.RuntimeTests/Portalkeeper.RuntimeTests.csproj --no-restore
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint3
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint4
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint5
dotnet run --project tests/Portalkeeper.RuntimeTests --no-build -- checkpoint6
```

Checkpoint 6 runs in two tiers:

* Without `PORTALKEEPER_CP5_EXE` it covers requirement parsing/normalization,
  fail-closed validation of unknown and control-character requirements,
  realm.conf parsing, model helpers, capability mapping, defense-in-depth
  rejection paths, refusal to generate when not required, refusal of the
  generation requirement against a non-verified source, and the full
  no-requirement Isolated lifecycle (baseline-only runtime, readiness,
  baseline launch selection, actual Linux Wine-launch invocation). In this mode
  the source is a synthetic fixture client, which newly-validates the baseline
  path for non-verified clients.
* With `PORTALKEEPER_CP5_EXE` it additionally covers generation 2 preparation for
  requirement realms, generation-2 launch selection, reuse, requirement-change
  fingerprint drift, downgrade to baseline with the leftover artifact preserved,
  and re-upgrade to generation 2.

Actual Linux results for this implementation: CP1/2 battery passed; CP3 passed 0
failures; CP4 passed 0 failures; CP5 passed 115 checks; CP6 passed 34 checks with
the verified executable and 26 checks without it. `dotnet build` produced zero
warnings and zero errors. Native Windows behavior was not executed by the agent
and still requires the platform-specific run and the live acceptance procedure.

## Files changed for implementation

Paths are relative to `/home/smithkt/git/Portalkeeper`.

| Change | File |
| --- | --- |
| Added `Requirements` and capability helpers | `src/Portalkeeper/Models/RealmInfo.cs` |
| Parse/normalize/validate `Requirements` | `src/Portalkeeper/Services/RealmConfigurationService.cs` |
| Capability mapping, generated-gate, launch selection | `src/Portalkeeper/Services/RealmExecutableService.cs` |
| Gate recipe source and generation per capability | `src/Portalkeeper/Services/RealmRuntimePreparationService.cs` |
| Capability-based readiness check | `src/Portalkeeper/Services/RealmRuntimeResolver.cs` |
| Select realm launch executable by capability | `src/Portalkeeper/Services/RealmLaunchService.cs` |
| Added focused implementation tests | `tests/Portalkeeper.RuntimeTests/Checkpoint6.cs` |
| Registered checkpoint 6 entry point | `tests/Portalkeeper.RuntimeTests/Program.cs` |
| Declared the requirement in CP4/CP5 generation fixtures | `tests/Portalkeeper.RuntimeTests/Checkpoint4.cs`, `Checkpoint5.cs` |
| Documented `Requirements=` | `config/example.realm.conf` |
| Added implementation/acceptance documentation | `docs/isolated-runtime-checkpoint6.md` |

CP3 is intentionally unchanged: its realms are Isolated without the capability and
now exercise the baseline path. No unrelated repository changes, executable
fixtures, commits, or pushes are included.

## Manual live acceptance — Kevin only

Automated development stops before this procedure. No live Eitrigg/PTR modification
or game launch is performed by the implementation tests.

1. Update each realm.conf `[Client]` section to declare
   `Requirements=protected-framexml` only for realms that actually require the
   FrameXML acceptance transformation. Keep SchemaVersion 1 and the unchanged
   ClientPath/SOURCE.
2. Launch with normal **ENTER REALM**. An existing CP5 generation-2 realm that
   continues to declare the requirement must be reused unchanged. A realm that
   drops the requirement must provision its new fingerprint and launch its
   baseline executable while its leftover artifact remains untouched.
3. Verify the reported `RealmExecutable.Generation` and `LaunchExecutableRelativePath`
   against the current realm.conf, and that no pending transaction remains.