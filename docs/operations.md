# Configuration, cache and troubleshooting

Start with the [README](../README.md) for exact realm keys, dependencies and launch instructions.

## Local state

The base is `<ApplicationData>/Portalkeeper`, using .NET's operating-system `ApplicationData` location (normally `%APPDATA%` on Windows and the user's config directory on Linux).

- `realms/`: persistent selected realm configuration. First-run imports preserve the original application-adjacent file. `realms/.portalkeeper/backups/` retains unique pre-refresh/pre-migration copies.
- `settings.json`: `ClientPath`, `HidePortalkeeperWhileGameRuns`, and `ShowTransmogrifiedAppearancesByRealm`. The latter is keyed by the literal Armory URL and defaults true for an absent key; capability still gates rendering.
- `armory-cache/realms/<URL-SHA256>/`: `index.json` and `character-<id>.json`. Old unscoped Armory caches are not reused. Changing URL spelling creates a new scope.
- `armory-cache/icons/`: shared cached icon JPEGs, fetched by icon name from the external icon service.
- `armory-cache/previews/`: completed 480×640 PNGs, capped at 200. Preview keys include renderer version (`armory-render-v3`), realm URL, visual character/equipment/transmog data, capability/preference and archive paths/sizes/modification times. Stat-only changes do not need a new portrait. Invalid PNGs regenerate; completed writes replace through a temporary file.
- `realm-news-cache.json` and `realm-calendar-cache.json`: last feed caches. Unlike Armory, these are not realm-URL scoped; see [known issues](implementation-issues.md).

GitHub discovery, personal-addon and addon installation tracking also maintain local state; these are not release-package content. Managed-addon backups are in `<client>/.portalkeeper/backups/`. The launcher also backs up realmlist changes. Do not clear client backups or SavedVariables to refresh an Armory image.

Preview decoding/rendering runs off the UI thread with serialized rendering and cancellation. A changed selection cannot install an obsolete image. MPQ bytes remain in bounded memory; raw extracted models/textures are not cached in the repository. To diagnose stale previews, close Armory and remove only the affected preview cache. A fresh render still needs the local client/library even if HTTP data is cached.

## Troubleshooting

| Symptom | Check |
|---|---|
| No realm / multiple realm configurations | Keep exactly one non-example realm file in persistent `Portalkeeper/realms`; verify SchemaVersion=1 and required fields, then CHECK AGAIN. Legacy files use UpdateURL only for bootstrap. |
| Client rejected | Choose the folder containing the supported Windows executable, not `Data`; validation must find build 12340 metadata/markers. |
| Cannot launch | Check client write permissions, locale/realmlist discovery, required-addon and required-patch readiness, and Wine/PATH/prefix on Linux. Health probes are advisory. |
| Addon source error | Check repository URL, source availability/rate limits, `.toc` and ambiguous addon directories; HTTP ZIPs require a safe configured folder and a .toc. |
| Feed unavailable | Check HTTP URL, schema version 1 and server publishing/hosting separately. Cached data may be older; read the displayed status/timestamp. |
| Roster works, profile fails | `characters/<id>.json` must resolve relative to the index URL; check publication completeness and web permissions. |
| Preview placeholder | Read its status; verify native StormLib architecture/dependencies, client `Data`/locale MPQs, required DBC/model/texture availability and supported archive layout. Slots/tooltips remain available. |
| Transmog checkbox disabled | Both feed capability discovery and support matter. Older feeds lack capability; enable compatible server publishing, then reopen Settings/Armory. |
| Transmog preview fails | Confirm resolved appearance display metadata and matching local client assets. Unresolved visible appearances use fallback, not guessed equipment; try original-gear mode to isolate the appearance. |
| Missing icon | Check icon service/cache; this does not establish that the client model is absent. |

Do not edit server customization values or replace client assets to mask an unsupported appearance. Custom patch ordering/delta files and exhaustive gear coverage remain unverified. The renderer's static material/pose limitations are documented in [preview behavior](armory-0.6-integration.md).
