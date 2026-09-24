# Checkpoint 5 — protected FrameXML investigation

## Current outcome — PROVEN BY GAME STARTUP

The narrowly scoped transformation changes only the FrameXML caller's handling
of the shared verifier's **content-digest mismatch** result. Missing/corrupt
signatures, GlueXML/addon verification, and the later FrameXML load-consistency
check remain unchanged. One jump-table entry changes; only two executable bytes differ.

Kevin subsequently reported a clean controlled game experiment:

- Start with stock `Interface/FrameXML/FriendsFrame.lua` extracted from
  `Data/enUS/patch-enUS-3.MPQ`.
- The only intentional functional change was
  `FriendsFrameTitleText:SetText(FRIENDS_LIST);` to
  `FriendsFrameTitleText:SetText("CP5 FRAMEXML TEST");`.
- The source-identical control executable rejected this protected-resource change.
- The transformed candidate, using the **identical proof MPQ**, entered the world.
- The native Friends frame visibly displayed **CP5 FRAMEXML TEST**; ordinary
  Social UI remained functional and the Guild tab was enabled/functioning normally.

This is **PROVEN BY GAME STARTUP**, based on Kevin's reported A/B observation,
not a game launch performed by the coding agent. The earlier
`mod-native-social PTR-Content-000019` payload is NOT the clean proof: it entered
world with the transformed executable, but its FriendsFrame implementation was
functionally broken. This report does not claim that old UI payload works.

The sections below retain the original static/offline evidence. Their original
stop point was resolved by the clean experiment above. The production integration
is described in [Checkpoint 5 implementation](isolated-runtime-checkpoint5.md).
Windows live acceptance and the additional negative controls remain distinct
validation items; they were not established by the reported title-change proof.

## Identity, tools, and methodology

The read-only source was `/home/smithkt/WoW-335a/Wow.exe`, found through the
configured Portalkeeper source ClientPath. Observed identity:

- PE32 / Intel i386; nominal image base `0x00400000`.
- Size: **7,704,216 bytes**.
- SHA-256: `aa63a5750d60ef16746c686b3d5e26876d98953eab08b1c026cd0faf78e88cb8`.
- This is the exact build 12340 identity supplied for the investigation.

Available: `file`, `strings`, GNU `objdump`/`readelf` 2.46, Python 3 with `pefile`,
installed `libstorm.so`, Wine/wine64. No radare2/rizin, Ghidra/analyzeHeadless,
llvm-objdump, Python capstone/lief/unicorn were found on the inspected PATH/import
paths. Tools actually used: file, objdump, Python/pefile, hashlib/struct/ctypes,
and StormLib in explicit read-only mode. Wine was not invoked.

Analysis began with literal strings and little-endian VA references, then followed
verified x86 call sites, branches, arguments, output buffers, and jump-table data.
Raw pointer/E8 searches locate candidates; they are not by themselves disassembly
or proof of causality. Instruction interpretation was checked against surrounding
control flow and function prologues. The jump table below is DATA inside `.text`,
not instructions, despite what linear disassembly might print.

A disposable workspace under the Codex task's `work/checkpoint5` held only analysis
scripts and derived JSON. Source bytes/assets were read into memory. No full
executable dump, extracted client asset, executable copy, or derivative binary was
added to the repository or deliverables.

## Evidence labels

- **OBSERVED:** exact source bytes, strings, PE locations, disassembled control
  flow, and read-only archive contents.
- **PROVEN BY EXPERIMENT — OFFLINE ONLY:** independent signature verification,
  reconstruction of stock resource digests, and an in-memory resource mutation.
  These do not prove WoW will start with modified native UI.
- **INFERRED:** semantic function labels and the prediction that the candidate
  will admit a stable, modified FrameXML tree while retaining the other checks.
- **PROVEN BY EXPERIMENT — GAME STARTUP:** the clean title-change A/B test
  reported by Kevin above. No claim is made that the coding agent ran the game.

No private debug symbols were available. Function names used below describe the
observed role; they are not asserted Blizzard symbol names.

## Known failure, traced to the exact message

The executable contains `FrameXML missing signature`, `FrameXML has corrupt
signature`, and `FrameXML is modified or corrupt`. Their string VAs are
`0x00A02FB0`, `0x00A02F90`, and `0x00A02F70`, respectively. The FrameXML TOC path is
at `0x00A02FCC`; `Bindings.xml` is at `0x00A02FEC`. Separate GlueXML diagnostics and
TOC arguments occur in a separate caller.

The exact reported user-facing sentence is not an ASCII/UTF-16LE literal in this
executable. It is row **10**, key `MSG_FRAMEXML_UI_CORRUPT`, of the local stock
`DBFilesClient\Startup_Strings.dbc`:

> Your game interface files are corrupt.  Please remove your Interface\FrameXML folder.

Row 9 is the distinct GlueXML/login-interface corruption error. The enUS locale
archive's DBC SHA-256 is
`dbd35336bc773fe2947119d2f6ddd62b18869b29279ffd78d41e83845d36ad62`.

Observed failure chain:

1. The FrameXML caller beginning at `0x0052A980` calls the verifier at
   `0x0052ABD1`, with `Interface\FrameXML\FrameXML.toc`, `Bindings.xml`, an embedded
   public key, and an output digest buffer.
2. Its failure cases converge on `push 10; call 0x004033C0` at `0x0052AC12`.
3. `0x004033C0` stores that ID at global VA `0x00B2F9A4` and transfers to
   `0x0047D760` (shutdown-request path).
4. `0x00406BE1` reads the recorded ID; `0x00406C2E` indexes the loaded localized
   string table and passes the message to `0x0086C6E0`.
5. `0x0086C784` calls through `0x00B2EDD4`; the resolver stub at `0x00896016`
   associates this pointer with `MessageBoxW`.

This is a code/data chain to the exact error, not a proximity-based string guess.
The same error ID is also used by the later FrameXML digest consistency failure,
so the dialog alone does not identify which of the two gates fired.

## Mechanism

### Shared signature and resource verifier

`0x008165E0` forms `<toc>.sig`, reads it, and requires **0x114 / 276 bytes**.
Its observed return contract is:

| EAX | Meaning | FrameXML target |
| --- | --- | --- |
| 0 | Signature file cannot be loaded | `0x0052ABE5` |
| 1 | Invalid size or invalid signature | `0x0052ABF2` |
| 2 | Signature valid; computed resource digest differs | `0x0052ABFF` |
| 3 | Signature valid; computed resource digest matches | `0x0052AC1C` |

These values follow the return arithmetic and the callers' diagnostics, not
merely an assumed enum. Out-of-range values also reach the failure path.

The stock `.sig` layout observed in MPQs is a 16-byte resource digest, four bytes
`NGIS`, and a 256-byte signature. The helper at `0x00816550` verifies a signature
over the first 16 bytes plus the uppercased signature BASENAME, e.g.
`FRAMEXML.TOC.SIG`. The embedded 256-byte modulus starts at VA `0x009E2C28`;
the exponent supplied at `0x00A4421C` is **65537**.

**Offline proof:** interpreting the modulus/signature as little-endian integers,
RSA public exponentiation produces exactly:

```text
SHA1(resource_digest || uppercase_signature_basename) || 235 bytes of 0xBB || 0x0B
```

Both stock FrameXML and GlueXML signatures verify. Flipping a signature bit or a
signed digest bit makes that verification fail. The full modulus/signature bytes
are neither printed nor stored by the delivered utility.

### Resource digest is MD5 over the referenced content tree

`0x008164D0` initializes a digest, uses `0x008162B0` to hash the TOC and referenced
resources recursively, includes the optional Bindings file, and finalizes into
the caller's output buffer. TOC entries and XML `Script file`/`Include file`
references participate. The MD5 initialization constants, round operations, and
16-byte output are visible in the hash routines; independent reconstruction
confirms the algorithm for the stock fixtures.

The utility uses the explicit stock MPQ precedence already represented in
Portalkeeper's `ClientAssets.cs`; custom `patch-4.MPQ` and later realm patches are
not consulted. For local enUS stock archives, both winning signatures came from
`Data/enUS/patch-enUS-3.MPQ`:

| Offline fixture | Signed and reconstructed MD5 | Resource visits |
| --- | --- | --- |
| Stock FrameXML, plus Bindings.xml | `51b264400410d3409c4e79f6f4257711` | 266, before the extra Bindings read |
| Stock GlueXML | `b44247235136edcfbbc314797eb2c705` | 60 |

FrameXML signature SHA-256:
`a530a9fb6e01d76fcaf80ec9c31b496d8e70bc505da6e268b7351874a39f1a1f`.
GlueXML signature SHA-256:
`0ce72cf65f67d65476fee9d58f97ac033798853f9bf4b2a0de8377bee5483678`.

Appending only `\n-- Portalkeeper CP5 digest-only experiment\n` to the in-memory
FriendsFrame.lua data changes the reconstructed FrameXML MD5 to
`63d5ecdf57fcff752f957243344bb041`. FriendsFrame.lua is visited exactly once.
The stock signature still verifies over its original digest, but the recomputed
resource digest now differs: the disassembled verifier would return **2**.
No asset was written. This is an offline causal digest experiment, **not** the
required native-social UI/game-startup experiment.

The utility's resource walker is scoped to the stock fixtures and checked against
both signed digests. It is not claimed to reproduce every malformed XML/parser
edge case or the client's loose-file/custom-patch precedence.

### A second, later consistency check remains

After the initial successful dispatch, `0x0052AC77` loads the FrameXML TOC through
`0x00814340`, accumulating another digest while loading the resources. Bindings
is also processed. `0x0052ACB5` finalizes this digest; the caller compares it with
the verifier's output buffer. At `0x0052AD43`, bytes `74 0A` conditionally skip
another error-ID-10 call only when the digests match.

Crucially, on result **2** the verifier has already written the **computed** digest
to the output buffer; it does not replace that buffer with the signed stock digest.
Thus redirecting case 2 to the ordinary success continuation is inferred to
retain a pre-load versus loaded-content consistency check. This still needs
confirmation during the real modified-resource experiment.

## Exact locations

VAs use image base `0x00400000`; debugger addresses should use actual module base
plus RVA if relocated. All entries below are `.text` unless specified.

| Role | VA | RVA | File offset |
| --- | --- | --- | --- |
| FrameXML-containing function | `0x0052A980` | `0x0012A980` | `0x00129D80` |
| FrameXML call to shared verifier | `0x0052ABD1` | `0x0012ABD1` | `0x00129FD1` |
| FrameXML indexed dispatch | `0x0052ABDE` | `0x0012ABDE` | `0x00129FDE` |
| FrameXML four-entry table | `0x0052AEB4` | `0x0012AEB4` | `0x0012A2B4` |
| **Case-2 table entry** | **`0x0052AEBC`** | **`0x0012AEBC`** | **`0x0012A2BC`** |
| Post-load consistency branch | `0x0052AD43` | `0x0012AD43` | `0x0012A143` |
| Shared verifier | `0x008165E0` | `0x004165E0` | `0x004159E0` |
| Signature helper | `0x00816550` | `0x00416550` | `0x00415950` |
| Resource digest driver | `0x008164D0` | `0x004164D0` | `0x004158D0` |
| Recursive resource digest walker | `0x008162B0` | `0x004162B0` | `0x004156B0` |
| MD5 init / update / final | `0x00779340` / `0x00779A30` / `0x00779AE0` | `0x00379340` / `0x00379A30` / `0x00379AE0` | `0x00378740` / `0x00378E30` / `0x00378EE0` |
| Public signature verification helper | `0x00770DB0` | `0x00370DB0` | `0x003701B0` |
| RSA modulus, `.rdata` | `0x009E2C28` | `0x005E2C28` | `0x005E1428` |

The shared verifier has three identified direct call sites:
`0x004DA7DD` (GlueXML), `0x0052ABD1` (FrameXML), and `0x005F7B41` (addon TOC/Bindings
paths). Do **not** alter its return logic or the shared hash/signature routines.

## Proven transformation — fixed recipe v1

Recipe ID: `wow-12340-framexml-digest-acceptance`, recipe version 1.
Input size/hash must be exactly the verified source above.

At `.text` RVA `0x0012AEBC`, file offset `0x0012A2BC`:

```text
Expected entry:     FF AB 52 00   -> 0x0052ABFF (FrameXML digest-mismatch failure)
Candidate entry:    1C AC 52 00   -> 0x0052AC1C (ordinary FrameXML load continuation)
```

Expected surrounding table, file offset `0x0012A2B4`:

```text
E5 AB 52 00  F2 AB 52 00  FF AB 52 00  1C AC 52 00
```

Expected dispatch at RVA `0x0012ABDE`:
`FF 24 85 B4 AE 52 00` (`jmp dword ptr [eax*4 + 0x0052AEB4]`).
The expected original verifier call is `E8 0A BA 2E 00` at RVA `0x0012ABD1`.

The entry is four bytes wide, but only file offsets `0x0012A2BC` and `0x0012A2BD`
change. An in-memory-only exact transformation calculates this hypothetical
output SHA-256:

`15c47945d5c461fda32a34645a23a9f3488bbb2b10855fe328c0e866d9ca8001`

The analyzer never writes this image. The later clean game experiment establishes
startup behavior for that exact transformation; the implementation reproduces
this output hash from SOURCE in an independent temporary file. SOURCE and runtime
baseline executables remain immutable.

**Scope:** this accepts digest changes anywhere in the FrameXML-referenced resource
tree, including Bindings, rather than only FriendsFrame.lua. It is not a per-file
administrator allowlist. It intentionally relinquishes stock-content authenticity
for that tree while still requiring a valid stock signature file and consistent
loaded content. GlueXML/addon call sites, signature cryptography, missing/bad
signature cases, archive validation, authentication, server validation, and
anti-cheat code are not changed. Static byte/control-flow isolation supports this
scope; complete absence of unintended runtime effects is not yet proven.

**PE signing/checksum limitation:** the source contains a 4,760-byte
`WIN_CERTIFICATE` (revision `0x0200`, type 2 / PKCS signed data) at file offset
`0x00757C00`. Its stored and independently computed PE checksum are `0x00762696`.
The candidate leaves that certificate and checksum field byte-for-byte unchanged,
but changes the signed image; its newly computed PE checksum would be
`0x007626B3`. Therefore the original executable image signature cannot attest to
the changed image, and the stored PE checksum would be stale. Certificate-chain
trust and Windows policy acceptance were not tested. The candidate does NOT
remove certificates, re-sign, alter signature verification code, or bypass OS
trust policy. This is a real side effect of the proposed executable edit, distinct
from the retained FrameXML `.sig` checks, and must be evaluated during Windows
acceptance. The hypothetical output hash above deliberately includes the
unchanged certificate/checksum. If OS policy blocks the experiment, report that
and stop rather than disabling the policy or silently adding more edits.

A second possible approach—changing `74 0A` at the post-load comparison—was NOT
selected: by itself it does not remove the earlier case-2 failure and would weaken
the later consistency check. A shared-verifier "always succeeds" change was also
rejected as unnecessarily broad. No claim is made that every possible smaller
transformation was exhaustively searched.

## Original proposed experiment and remaining controls

The original investigation proposed the following disposable-copy experiment.
The **clean title-change fixture above now supplies the positive startup proof**;
do not use the broken native-social 000019 payload as the acceptance fixture.
An automated in-world UI oracle remains unavailable. The full stock/negative
control matrix and Windows behavior were not reported as completed. Record the
proof MPQ hash and modified payload hash for subsequent acceptance runs; those
fixture hashes were not included in Kevin's report.

For current normal-launch live acceptance, use the implementation document rather
than manually editing executables or manifests. The original experimental
procedure below remains a reference for debugger and negative-control work only.

1. With test game processes stopped, create a disposable full client/runtime
   clone OUTSIDE source and live managed runtimes. Copies of writable files and
   executable candidates must be independent, not hard links. Do not modify the
   source or live Eitrigg/PTR files, their manifests, or ledgers. Keep a disposable
   stock-content control and an otherwise identical modified-content fixture.
2. Run the analyzer below against the verified SOURCE. Make two independent
   executable copies FROM THAT SOURCE, one control and one candidate, inside the
   disposable fixture. Both initially must have the verified size/hash. Do not
   derive either from a custom/transformed/runtime executable.
3. Prefer first running the failing control under a debugger. Break at actual
   image base + `0x0012ABD9` after verification, and at +`0x0012AD43` if reached.
   Record EAX at the first breakpoint. Expected: **2** for the known modified
   resource, **3** for stock. If the failure instead reports 0/1, or another path
   is implicated, STOP: do not broaden the candidate to suppress that error.
4. For the binary A/B test, in a hex editor edit ONLY the disposable candidate's
   four-byte entry after checking the full original SHA-256/size, PE section,
   table, dispatch, and exact original bytes listed above. Never patch by offset
   alone. Require the computed output SHA-256 above and exactly those two changed
   byte positions; retain the control hash. If anything differs, STOP. No automatic
   patch utility is supplied at this unproven stage.
5. Launch each test directly with cwd set to its disposable runtime. On Linux use
   the same known-good Wine executable and SOURCE-discovered prefix/environment
   as the accepted CP4 launch. On Windows launch the selected disposable `.exe`
   natively with that working directory. Do not use normal Portalkeeper launch:
   CP4 correctly rejects a transformed executable whose hash differs from its
   BaselineCopy manifest. Do not bypass that by editing a live manifest.
6. Execute this matrix, keeping the modified MPQ/payload hashes identical between
   the two modified-content runs:

   | Executable | Content | Required observation |
   | --- | --- | --- |
   | Control/source-identical | Stock FrameXML | Normal entry and Friends UI work |
   | Control/source-identical | Clean title-change proof patch | Reproduces the exact corruption failure |
   | Candidate | Stock FrameXML | Normal entry and Friends UI still work |
   | Candidate | Same clean title-change proof patch | Enters world and actually loads the modified FriendsFrame UI |

   A login screen, hidden dialog, or addon-only replacement is not success. Open
   the intended Friends UI and demonstrate the actual native modification (e.g.
   the intended Players tab), capturing screenshot/log evidence and Lua errors.
7. In additional DISPOSABLE fixtures, overlay a single-bit-corrupted FrameXML
   `.toc.sig` and require signature failure; separately change a protected
   GlueXML resource with its original signature and require the GlueXML failure.
   These are negative controls for the intentionally retained boundaries. Do not
   damage the original stock MPQs to create them. Stop rather than layering more
   executable changes if these controls unexpectedly pass.
8. Record executable/payload hashes, cwd, Wine environment or Windows details,
   first verifier result, final comparison result if debugged, FrameXML/GlueXML
   logs, and visible native UI outcome. Re-hash SOURCE and live executables to
   confirm they remain unchanged. Keep all experiment artifacts private/local.

The later title-change experiment reproduced the control failure and demonstrated
that same modified native UI under the transformed executable. The remaining
negative controls and Windows acceptance should still be recorded separately.

## Manifest/recipe design and implementation

Generation 1 remains `BaselineCopy`. Generation 2 is `FrameXmlDigestOverride`,
with fixed `RecipeId` and `RecipeVersion`. The expected output identity comes from
the compiled recipe, never an arbitrary manifest claim. There is no generic patch
engine or realm.conf change. See the [implementation document](isolated-runtime-checkpoint5.md)
for the transaction, ownership, validation, migration, launch, and test results.

## Original investigation deliverables and verification

Added only:

- `docs/isolated-runtime-checkpoint5-investigation.md`
- `scripts/analyze-framexml-12340.py`
- `tests/test_framexml_12340_analysis.py`

No existing files modified. The analyzer contains only derived offsets, short
instruction/table guards, algorithms, and hashes. It requires a user-supplied
exact source executable, validates its full identity BEFORE analysis, fails
closed, prints derived JSON, and has no executable-writing option. Optional MPQ
verification reads the explicit stock archive list in read-only mode and performs
its resource changes in memory only.

Reproduction from repository root:

```sh
python3 scripts/analyze-framexml-12340.py /path/to/verified/Wow.exe
python3 scripts/analyze-framexml-12340.py /path/to/verified/Wow.exe \
  --stock-client /path/to/stock-client --locale enUS
PYTHONDONTWRITEBYTECODE=1 \
PORTALKEEPER_CP5_EXE=/path/to/verified/Wow.exe \
PORTALKEEPER_CP5_STOCK_CLIENT=/path/to/stock-client \
python3 tests/test_framexml_12340_analysis.py -v
git diff --check
```

Executed against the exact local source and stock enUS archives: **10 tests passed,
zero skipped**. Coverage includes incorrect size/hash, already-modified input,
ambiguous/unbacked PE mapping, read-only CLI failure, exact call sites/guards,
only-case-2 candidate changes, unchanged other gates, source identity/content,
RSA/SHA-1 positive/negative cases, stock MD5 reconstruction, the in-memory
FriendsFrame digest change, and localized failure mapping. No binaries are stored
as test fixtures. With no user-supplied executable, five local-input tests skip;
that is not equivalent to a verified-source run.

During the original investigation, no Portalkeeper application source changes were necessary, so application builds
and CP1–4 runtime tests were not rerun for this documentation/analysis-only change.
No game launch, source/live executable modification, automatic transformation,
commit, push, or binary redistribution occurred.

## Remaining unknowns and acceptance scope

- Windows native launch/trust-policy behavior with the unchanged certificate and
  stale PE checksum remains untested here. Recipe v1 must not add edits to resolve it.
- The clean proof does not establish that every native-social implementation works.
  The old 000019 payload's UI defects are separate from executable acceptance.
- Missing/corrupt FrameXML signature and modified GlueXML negative controls remain
  supported by static analysis and unchanged bytes, but were not reported as live-tested.
- The clean proof MPQ/payload hashes, full platform details, and screenshots were
  not supplied with Kevin's result; retain them during further acceptance.
- Normal Portalkeeper ENTER REALM migration on live Eitrigg/PTR is the next manual
  acceptance step. Automated implementation tests use disposable fixtures only.
