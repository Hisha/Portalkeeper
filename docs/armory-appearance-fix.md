# Exact exported appearance selection

Historical fix/verification report. Use the [current guide](armory-0.6-integration.md) for application instructions; coverage limits are in [verification](verification.md).

Changed application file: `src/Portalkeeper/Services/CharacterRendering/CharacterScene.cs`.

CharSections selection now accepts matching race, gender, section type, variation and color records regardless of the death-knight appearance flag. The existing player-record flag check remains. Stable preference ordering still favors flagged records for death knights and unflagged records for other classes, so an existing normal match retains its previous selection. Only when that preferred variant is absent is the other exact-match variant selected. No exported appearance values or server data are changed.

Verified against the installed Linux client and the supplied published profiles:

- Abidraan (1284): skin 6, face 12. Skin record 5825 (flags 17), face record 12328 (flags 5). Full dressed application renderer produced a PNG successfully.
- Ailania (1666): skin 11, face 6. Skin record 10339 and face record 10346 (both flags 5). Full dressed application renderer produced a PNG successfully.
- Hipally (1004): full dressed render succeeded.
- Exhaustive comparison over the installed CharSections groups preserved all 12,973 previously valid group/class selections. No formerly valid choice changed; 2,595 formerly rejected exact group/class selections became available.

The fix is part of the application's existing rendering path. No additional renderer errors occurred for the two reported profiles. Client archives and published JSON were only read. The fix is present in this checkout. The earlier delivery used a source ZIP; that delivery detail is not an installation requirement.

Release build: zero warnings, zero errors. Hipally and the previously successful default-appearance Tauren male, Night Elf female and Draenei female regression renders were byte-for-byte identical to their prior PNGs.
