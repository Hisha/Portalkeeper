# Static Armory rendering corrections

Diagnosed from the current published Hipally profile, the installed 3.3.5 client, and the supplied Armory (15:01:56) and WoW character-selection (15:02:23) screenshots on September 7, 2026.

## Causes and fixes

1. The old rasterizer discarded every texture pixel with alpha below 128, including opaque materials. Judgement's helmet and shoulder base passes specify blend mode 0 (opaque). Their texture alpha also serves layered material effects and must not remove the base surface. About 29.2% of the helmet texture and 59.4% of the shoulders' real separate alpha plane are below that threshold. This created holes in the faceplate and panels. The renderer now applies cutout rejection only to alpha-key material mode 1. The BLP decoder and geoset selection did not require changes.
2. The camera-right vector had the opposite sign to up cross camera-to-viewer. With M2's Z-up coordinates, a camera in front of the character must use that cross product. Correcting the camera basis restores left/right screen orientation. No output-image flip or attachment swap is applied. Hipally's attachment transforms have positive determinants near 1, and the right-hand anchor is at negative model Y, consistent with its correct screen-left placement. Helmet, shoulders and weapon triangle winding agrees with their stored normals: 110, 206, 206 and 254 aligned triangles respectively, none reversed. Culling remains governed by each material's two-sided flag.
3. The old renderer shaded all materials with a fixed grayscale light, ignoring the sword's unlit flag. Material unlit state is now retained. Static additive geometry also restores the helmet eyes; its no-depth-write flag is retained and it renders after opaque depth. Base surfaces take precedence over additive-only batches for the same submesh.
4. The installed client's Interface/GlueXML/GlueParent.lua supplies the Human character-selection directional lights visible in the reference: an ambient term, cool fill and stronger warm key. The static preview now reads those numeric entries as data, without executing Lua, and uses this consistent portrait rig across races. RGB lighting replaces the old grayscale approximation. If that optional client UI resource is absent, the previous neutral lighting remains available. Client archives remain read-only.

Preview cache version is now armory-render-v3. Existing previews regenerate automatically. No equipment, tooltip, transmog preference or published JSON values are changed.

## Verification

The application was built and the real Avalonia Armory window exercised using Headless/Skia against the current published profiles served over local HTTP. Hipally's transmog and original-gear modes, Abidraan, Ailania, Bullsha and Deyvia render successfully; repeat loads reuse the new preview cache. The added tests/ArmoryMaterialChecks exercise opaque, cutout and additive alpha rules against the actual material type.

## Remaining differences

This remains an orthographic static portrait with the initial embedded Stand pose. It does not reproduce the character-selection scene's perspective, exact animated frame, hand-grip pose, background point lights, environment-map/reflection layers, bloom or particles. The helmet/shoulder base geometry is now intact and the weapon appears on the correct side, but specular shine, glow softness and perspective proportions will still differ. All races intentionally share the reference Human portrait light rig; their race-specific selection backgrounds are not recreated.

The legacy model-viewer material reference used during diagnosis is wowmodelviewer/wowmodelviewer, 0.7.0.5-era src/model.cpp (ModelRenderPass::init), alongside the installed client's actual M2/BLP data and GlueXML light configuration. Raw client assets and Lua files are not bundled with the source.
