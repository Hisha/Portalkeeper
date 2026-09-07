# Verification status and Windows checklist

Portalkeeper remains under development. Linux functionality has been exercised in this session, not exhaustively validated. Earlier work built the application and exercised the real Avalonia Armory/Settings views through Headless/Skia with published/example JSON and read-only local StormLib assets. That is not equivalent to testing every native desktop interaction, launcher, addon source or realm configuration.

Focused evidence includes Hipally in transmog/original modes, hidden appearances, older-feed fallback, settings/cache behavior, and representative Abidraan, Ailania, Bullsha and Deyvia profiles. Earlier default-appearance checks covered ten races and both genders; they do not establish every customization/item combination. Historical [appearance](armory-appearance-fix.md) and [material](armory-material-rendering-fix.md) reports describe those checks. Referenced standalone test/probe projects are absent from this actual checkout, so their commands are not advertised as available tests.

This documentation update checks source/configuration names, local links, paths and build-command targets. It does not rerun expensive renders or claim a new build/runtime verification.

## Windows verification checklist — pending

- [ ] Build/publish for the intended Windows architecture; start from both Explorer and a terminal, including a path containing spaces, and confirm the main window opens.
- [ ] Verify Avalonia/Skia native runtime files and matching `StormLib.dll` plus its dependencies on a clean machine; test missing/wrong-architecture library handling.
- [ ] Select a valid 3.3.5a client, reject an invalid selection, discover the intended realm, back up/update realmlist and launch WoW. Check hide/restore on game exit.
- [ ] Open Armory, filter/select multiple characters, verify slots/tooltips/icons and static dressed previews; change selection during loading and close/reopen the window.
- [ ] Verify transmog on/off, hidden items, unchanged actual-item tooltips, and unsupported/older capability feeds with the disabled-setting hover text.
- [ ] Restart and confirm client path, hide preference and per-realm transmog preference persist; check isolation between different Armory URLs.
- [ ] Verify cached-preview reuse/invalidation, missing client/assets, unsupported patch layout, unavailable feeds with/without cache, and fallback messages without losing equipment UI.
- [ ] Inspect the distributable for native dependencies, documentation/configuration availability and absence of private realm data/client assets.

Windows runtime and packaging, macOS behavior, arbitrary custom patches, full gear/appearance coverage and native desktop regression coverage remain pending. Animation and particle effects are outside the static preview scope, not unfinished acceptance tests for this milestone. Server worldserver build/deployment verification belongs to mod-realm-armory's server environment.
