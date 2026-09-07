# Armory 0.6 proof results — Hipally

Status: **minimal dressed-character rendering proved; UI integration not performed**.

The existing Armory UI, roster, filters, paper doll, icons, tooltips, appearance
fields and quality colors remain unchanged. The added utility is deliberately
separate from the solution's application project. It reads the supplied 0.5.1
profile and local client files only. There is no demonstrated JSON gap.

## Resolved appearance

| Input / lookup | Result |
|---|---|
| Character | Hipally, 1004 |
| Race / gender | Human / Male (1 / 0) |
| Appearance | skin 0, face 4, hairstyle 9, hair color 9, facial style 3 |
| ChrRaces male display | 49 |
| CreatureDisplayInfo model | 49 |
| CreatureModelData model | Character\Human\Male\HumanMale.m2 |
| Hair mesh group | 10, suppressed by equipped helmet |
| Helmet visibility row | 248 |
| Body meshes selected | 0 (two submeshes), 404, 505, 1202, 1301, 1502 |

Face uses `HumanMaleFaceLower04_00` and `HumanMaleFaceUpper04_00`.
Hair style 9 resolves through CharSections to `Hair02_09`, not a guessed
`Hair09_09`. Facial style 3 uses `FacialLowerHair03_09` and
`FacialUpperHair03_09`. The report retains all underlying choices even where the
helmet covers them.

## Equipment

All 17 equipped display IDs resolve. Armor components are selected by slot;
unrelated fields on a shared ItemDisplayInfo record are ignored. For example,
some glove/bracer/boot display records contain shoulder-model names that must not
be attached as shoulders. Empty character slots remain empty.

| Slot | Display | Body contribution |
|---|---:|---|
| Head | 53653 | Helm_Plate_Northrend_D_01_HuM.m2, Black skin, attachment 11 |
| Neck | 64190 | None |
| Shoulders | 53891 | L/RShoulder_Plate_Northrend_D_01.m2, Black skin, attachments 6/5 |
| Chest | 60528 | Upper/lower arm and torso component textures |
| Waist | 55379 | Belt / upper-leg overlay |
| Legs | 54278 | Paladin raid pants component textures |
| Feet | 62290 | Argent Alliance boots, mesh 505 |
| Wrists | 64833 | Lower-arm bracer overlay |
| Hands | 54420 | Paladin gloves, mesh 404 |
| Finger 1 | 64225 | None |
| Finger 2 | 63958 | None |
| Trinket 1 | 64711 | None |
| Trinket 2 | 68109 | None |
| Back | 64304 | Cape_Leather_C_01Black, character mesh 1502 |
| Main hand | 64397 | Sword_2H_IcecrownRaid_D_01.m2, Red skin, attachment 1 |
| Relic | 54524 | None |
| Tabard | 15817 | Fixed Scarlet Crusade upper/lower torso textures, mesh 1202 |

## Verification

- Eight DBC envelopes/layouts checked; all model paths and texture components
  opened successfully through read-only StormLib handles.
- 50 required files audited with SHA-256 for every found copy. No byte differences
  between the prototype and local AC map/camera ordering for this dependency set.
- The five omitted stock base/backup/speech archives have no copies of those files.
- M2, SKIN, material lookups and triangle indices checked by the renderer.
- 4,272 selected triangles render from one character plus four attached models.
- Six synthetic tests passed, including the separate BLP palette alpha plane.
- C# and exploratory resolver manifests matched exactly.
- Archives retained their original sizes and modification times after extraction.
- Front, back and three-quarter rendered images were visually inspected.
- Portalkeeper `dotnet build` passed with zero errors and two NU1900 warnings
  because this sandbox cannot write NuGet’s vulnerability-data cache.
- The standalone C# probe build passed with zero warnings and zero errors.

This does not establish the executable's total archive order or custom patch
semantics. The full audit and omitted-material-pass report are generated in the
cache, not stored with client bytes in the repository.

## Integration gate

This proves that existing JSON plus the client DBC/M2/SKIN/BLP data can produce a
dressed Hipally. It does not justify replacing the current UI with this research
renderer. Before application integration, choose a .NET rendering path, validate
additional race/gender and gear cases, implement material/animation handling,
and define cache/error/lifetime behavior. Windows native packaging and custom
patch handling remain unverified. See the probe README for commands and sources.
