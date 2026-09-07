# Current Armory static previews

This is the current application guide, superseding the [historical MPQ proof](armory-0.6-proof.md). See the [README](../README.md) for build/configuration and native dependencies, and [operations](operations.md) for caches and fallback troubleshooting.

Selecting a character renders its race, gender, exact exported appearance and equipment using the configured local 3.3.5a client. Existing roster, equipment slots, icons, quality colors and tooltips remain. Optional [transmog](armory-transmog.md) projects appearance metadata for rendering without replacing the real equipment data.

Runtime is C#: `ArmoryPreviewService` plus `Services/CharacterRendering`. It reads MPQs with read-only StormLib, resolves eight DBC tables, decodes M2/SKIN/BLP assets, composes body textures and equipment attachments, and rasterizes a static 480×640 PNG. Python and the historical standalone probe are not runtime dependencies. Background loading, cancellation, cached previews and explanatory placeholder fallback are integrated into the actual Armory view.

The resolver handles ten playable races and both genders, facial/hair geosets, race-specific helmets, armor layers, robes, bare feet, capes, shoulders and equipped weapons. CharSections requires exact exported indices; class flags influence stable preference among matching records but do not reject an otherwise matching appearance solely because of class.

Stock archive priority is locale/global patch-3, locale/global patch-2, locale/global patch, locale lichking/expansion/base, then global lichking/expansion/common-2/common. The first available locale in the resolver's fixed locale list is used. The supplied Linux client's dependency set was exercised; this is not proof of all client/custom-patch layouts. Unsupported patch names and encountered delta/deletion entries trigger fallback.

Rendering uses the initial embedded Stand pose where available. Opaque material alpha does not cut holes; alpha-key materials use their cutoff. Material culling/unlit flags and independent static additive geometry such as helmet eyes are supported. A corrected camera basis determines handedness. The portrait uses Human selection lighting read as numeric data from optional client GlueXML, with neutral fallback; it does not execute Lua. See the [material correction report](armory-material-rendering-fix.md).

Previews remain static and orthographic. Animation and particle effects are outside scope. Exact grip pose, scene perspective, race-specific selection backgrounds, environment/reflection layers, bloom and animated enchant glows are not reproduced. Guild-tabard emblem/background/border customization is absent from the JSON and is not invented; equipment information remains visible.

Linux functionality was exercised in this session with focused Headless/Skia application-view checks, not exhaustive native desktop validation. Windows runtime and packaging remain pending. See the [verification checklist](verification.md).
