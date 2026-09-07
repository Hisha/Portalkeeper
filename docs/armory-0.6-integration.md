# Armory 0.6: integrated static previews

Selecting a character loads a dressed preview into the existing Armory paper doll, using the profile's race, gender, appearance and equipment display IDs and the local client folder configured in Settings. No server or JSON changes are required. Existing slots, tooltips, roster filtering and icons are retained.

Build with `dotnet build`, then run `dotnet run --project src/Portalkeeper` using the .NET 10 SDK. Linux requires native StormLib (`libstorm.so` or `libstorm.so.9`). Choose the folder containing the client's Data directory in Settings. Windows loading attempts StormLib.dll, but Windows packaging remains unverified. macOS is also unverified.

The renderer is C#: it reads MPQs, resolves eight DBC tables, composes armor textures, skins M2 models and equipment attachments, and creates a static PNG. Python and the standalone probe are not required at runtime.

Loading runs in the background. Changing selection cancels obsolete rendering and prevents stale images from replacing the selected character. Closing Armory cancels pending rendering. Missing assets or native libraries retain the placeholder and equipment UI with an explanation.

Only completed PNGs are cached under the per-user Portalkeeper/armory-cache/previews folder. Keys include realm URL, visual JSON, and archive paths, sizes and modification times. Cache writes are atomic, corrupt images regenerate, and the cache is limited to 200 images. Raw client assets stay in memory; client archives are never modified.

Resolution supports all ten playable 3.3.5 races and both genders without a character-ID special case. It handles race-specific helmets, facial/hair geosets, armor layers, robes, bare feet, cape, shoulders and equipped weapons. Rendering uses the initial embedded Stand pose when available. Animated particles, reflective/additive materials, weapon enchant glows, precise grip animations and rotation are outside this static milestone.

Customized guild-tabard artwork is omitted because the JSON lacks emblem/background/border customization. No emblem is invented; its equipment tooltip remains available. Stock archive precedence was checked against the extraction proof on the supplied Linux client. Unknown patch names and encountered delta-patch records fall back. Arbitrary custom patches and other client builds remain unverified.

The earlier tools/ArmoryClientProbe and proof documentation are preserved as historical research. Runtime code is in Services/CharacterRendering and ArmoryPreviewService.

## Verification

Release build passed with zero warnings and zero errors. An Avalonia Headless/Skia check opened the actual ArmoryWindow, served the supplied published JSON over local HTTP, selected Hipally, and captured the dressed character inside the application with 17 equipped items. Identical-preview cache reuse and missing-client fallback passed. The check uses the real view, bindings, view model and rendering service, not a mock webpage. Native desktop interaction and Windows packaging were not tested.

All 20 race/gender combinations produced previews using default appearance selections and the example equipment set. This checks general resolution, not every customization or item combination.
