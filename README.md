# Portalkeeper

Portalkeeper is an independent realm launcher, addon manager and public realm-information viewer for **World of Warcraft 3.3.5a, build 12340**. It remains **under development**. It is not affiliated with AzerothCore or Blizzard Entertainment.

Linux functionality has been exercised in this session, including focused Armory checks; this is not exhaustive validation of every launcher, addon, client or realm configuration. **Windows runtime and packaging verification are pending.** Character previews are static; animation and particle effects are outside the current scope.

## Current functionality

- Validate a local client, discover one realm configuration, check authentication/world TCP reachability, prepare `realmlist.wtf` and launch WoW directly on Windows or through Wine on Linux. An unavailable health probe alone does not block launch.
- Manage realm-required, recommended and personal addons, discover GitHub addon metadata, install/update managed folders and back up replacements. Required-addon readiness gates entry; unrelated addons and SavedVariables are retained.
- Display optional realm news, a public monthly event calendar and the Armory, with downloaded-data fallback when available.
- Search/filter the Armory roster by players or playerbots, inspect the existing equipment paper doll, quality colors, icons and detailed tooltips, and load dressed static character previews in the background.
- Apply optional transmog appearances, including hidden equipment, while keeping original item information in slots/tooltips. Save the display preference per realm Armory URL.

Portalkeeper does not distribute the client or store game account credentials. Launching writes the selected client's realmlist; addon installation writes managed addon folders. Preview extraction itself only reads client archives and does not modify them.

## Build and run

Source builds require the **.NET 10 SDK** and package restore access. Git is useful for obtaining source, not required by the running addon downloader. From the repository root:

```sh
dotnet restore
dotnet build
dotnet run --project src/Portalkeeper/Portalkeeper.csproj
```

The root [Portalkeeper.slnx](Portalkeeper.slnx) and [application project](src/Portalkeeper/Portalkeeper.csproj) are the build targets. Source runs require the SDK; the Linux publishing script creates a self-contained .NET application:

```sh
./scripts/publish-linux.sh
```

It requires `dotnet` and `zip` and currently produces `dist/Portalkeeper-0.1.0-linux-x64.zip`. This review does not change release versions. Self-contained .NET does **not** bundle StormLib or guarantee all operating-system GUI/native libraries are installed. The script checks for private realm files and removes development symbols; Windows packaging is not verified.

## Client and realm setup

Select the local folder containing `Wow.exe` and `Data` in Settings, for example `/path/to/WoW-335a` or `C:\Games\WoW-335a`. Executable validation checks supported metadata/build markers; it does not validate every archive. Linux launching requires `wine` or `wine64` on PATH. The launcher honors `WINEPREFIX`, otherwise tries matching desktop-launcher prefix information and then Wine's default prefix. Settings shows the detected launch environment.

Copy [config/example.realm.conf](config/example.realm.conf) to `config/my-realm.realm.conf` beside the executable (or in the source working directory during development):

```ini
[Server]
Name=Example Realm
Address=realm.example.com
AuthPort=3724
WorldPort=8085

[Updates]
ManifestURL=
NewsURL=
StatusURL=
CalendarURL=
ArmoryURL=https://realm.example.com/armory/index.json
```

`Name` and `Address` are required; ports default to 3724 and 8085. Keep exactly one non-example `*.realm.conf`: discovery searches the working directory, executable directory and each one's `config/` child. The example is ignored; multiple files disable realm selection rather than providing a realm picker. Use **CHECK AGAIN** after correcting configuration. Private realm files are not copied by the project automatically; install your configuration separately.

All feed URLs are optional. `ManifestURL` overrides local `config/addons.json`. `NewsURL`, `CalendarURL` and `ArmoryURL` enable their corresponding views. **`StatusURL` is parsed but not consumed**; current health status comes from TCP probes, not an HTTP status feed. Keep private values out of public source/packages.

## Optional integrations and dependencies

| Feature | Required for that feature | Optional / fallback |
|---|---|---|
| Launcher | Local supported client; configured realm; Wine on Linux | TCP health is advisory. |
| Addon installation | Writable client addon directory and reachable source | Local or remote manifest; personal GitHub sources. |
| News/calendar | HTTP(S) schema-v1 feeds from mod-realm-news/mod-realm-calendar | Last cached feed on failure. No server database access. |
| Armory equipment | HTTP(S) schema-v1 mod-realm-armory feed | Cached roster/profiles; icons may be unavailable. |
| Static previews | Local compatible client MPQs and native StormLib matching process architecture | Missing assets/library keep equipment UI and an explanatory placeholder. |
| Transmog previews | Explicit capability in both roster and profile, resolved appearance metadata | Older/unsupported feeds show original equipment. No mandatory mod-transmog dependency. |

Linux preview loading tries `libstorm.so` then `libstorm.so.9`; Windows tries `StormLib.dll`. Install the native library and its transitive dependencies where the operating-system loader can find them. No StormLib binary is supplied by the project or Linux packaging script. A macOS library name exists in the resolver, but macOS runtime/launch support is not validated.

## Addon management

[config/addons.json](config/addons.json) uses `manifestVersion: 1` and an `addons` array. A generic GitHub entry is:

```json
{"id":"example-addon","name":"ExampleAddon","required":false,"recommended":true,"gitUrl":"https://github.com/owner/repository"}
```

GitHub discovery resolves the default branch, commit, addon directory, `.toc` and version where available. `addonPath` disambiguates repositories; `folder` and `version` are available overrides. Direct ZIP sources use `downloadUrl` and `sha256` with folder metadata. See [AddonManifest.cs](src/Portalkeeper/Models/AddonManifest.cs) for exact fields. Downloads are staged; replacement folders are backed up under `<client>/.portalkeeper/backups/`.

Required addons gate **ENTER REALM** until readiness checks pass. Recommended/personal addons do not. Source errors and known version/commit changes are displayed in addon management. Personal sources can be added or removed from management; realm policy wins if it adopts the same addon. Removing a personal management entry is not an instruction to erase its game data.

## News, calendar and Armory

News displays published articles with title, summary/body, category, author and local publication time, with pinned articles first. Calendar displays the published date range, month navigation and selected-day public events, including all-day and timed events; it is a viewer, not an event editor.

The server-side Armory module writes `index.json` plus `characters/<guid>.json`. An operator serves that directory over HTTP(S); Portalkeeper requests the roster and resolves profiles relative to `ArmoryURL`. The module neither hosts HTTP nor sends client models. Portalkeeper reads local MPQs to resolve the JSON's race, gender, customization and equipment display IDs.

Tooltips retain the actual equipped item's name/quality, armor, stats, resistances, damage, speed/DPS, sockets with equipped gems, enchants, socket bonus, supplied spell names/triggers, flavor text, durability and supported requirements. Null enchant fields do not render; invalid enchant data is not converted into invented effects. Spell names and trigger labels are not a complete WoW spell-formula engine.

In Settings, **Show transmogrified appearances** sits below **Hide while WoW is running**, defaults on and persists per Armory URL. Unsupported feeds disable it with “This realm doesn’t support transmogrification.” Turning it off renders original gear while “Transmogrified to: …” remains in tooltips. Explicitly hidden appearances omit the visual item, not its equipment slot.

See [current preview behavior](docs/armory-0.6-integration.md), [transmog details](docs/armory-transmog.md), [caching and troubleshooting](docs/operations.md) and [verification / Windows checklist](docs/verification.md). [Known implementation issues](docs/implementation-issues.md) are recorded separately.

## Privacy and historical material

Settings and caches live under the operating system's per-user application-data directory, in `Portalkeeper/`; client assets are not redistributed. Item icons can be fetched from the external `wow.zamimg.com` icon service. News/calendar/Armory are public-feed consumers, not authenticated account services.

The [MPQ proof report](docs/armory-0.6-proof.md) is historical research, not current setup instructions. The actual checkout does not contain the standalone probe or earlier Armory test harnesses mentioned by historical reports; those references describe prior evidence, not runnable commands in this checkout. No developer tools were removed in this documentation review.

See [LICENSE](LICENSE).
