# Portalkeeper

Portalkeeper is an independent realm launcher, addon manager and public realm-information viewer for **World of Warcraft 3.3.5a, build 12340**. It remains **under development**. It is not affiliated with AzerothCore or Blizzard Entertainment.

Linux functionality has been exercised, including focused Armory checks. Windows portable and installed releases have also been validated on Windows, including client launch, native StormLib loading, Armory character previews, Start Menu integration and uninstall. This is not exhaustive validation of every launcher, addon, client or realm configuration. Character previews are static; animation and particle effects are outside the current scope.

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

It requires `dotnet` and `zip` and currently produces `dist/Portalkeeper-0.2.0-linux-x64.zip`. Self-contained .NET does **not** bundle StormLib or guarantee all operating-system GUI/native libraries are installed. The script checks for private realm files and removes development symbols.

## Windows releases

Two Windows release artifacts are offered, both built from the same project version (currently `0.2.0`). Both bundle the .NET runtime and `StormLib.dll`; no PowerShell launch or install script is required.

### Portable

```
Download  Portalkeeper-<version>-win-x64.zip
Extract   the ZIP into a folder
Run       Portalkeeper.exe
```

### Installed

```
Download  Portalkeeper-Setup-<version>.exe
Run       the installer (per-user, no administrator rights required)
Launch    Portalkeeper from the Start Menu
```

The installer retains the stable AppId `{64CF5E71-169A-422C-86D5-AC9A62E0AEEE}`, the existing installation directory, and per-user installation (`PrivilegesRequired=lowest`). Close Portalkeeper and run the newer installer: it replaces binaries in the existing installation without a manual uninstall or duplicate application entry. `%APPDATA%\Portalkeeper` and the selected WoW installation are not installer payload. User-created realm files beside the application remain available for first-launch import. See [upgrade instructions](docs/schema-v1-upgrade.md).

### Maintainers: creating the release artifacts

Both commands run the `win-x64` self-contained publish and validate that
`StormLib.dll` and `StormLib.LICENSE.txt` are present. They refuse to package
private `*.realm.conf` files and strip development-only files out of the ZIP.
Run them from the repository root:

```
pwsh scripts/publish-win.ps1 -Installer
```

This produces:

- `dist/Portalkeeper-<version>-win-x64.zip`
- `dist/Portalkeeper-Setup-<version>.exe`

The portable ZIP alone is `pwsh scripts/publish-win.ps1` (or `scripts/build-win-installer.ps1` for both). Compiling the installer requires [Inno Setup 6](https://jrsoftware.org/isdl.php) (6.2 or newer); `publish-win.ps1` reports a clear error if `ISCC.exe` is not found and still creates the portable ZIP in that case. No Visual Studio is needed.

## Client and realm setup

Select the local folder containing `Wow.exe` and `Data` in Settings, for example `/path/to/WoW-335a` or `C:\Games\WoW-335a`. Executable validation checks supported metadata/build markers; it does not validate every archive. Linux launching requires `wine` or `wine64` on PATH. The launcher honors `WINEPREFIX`, otherwise tries matching desktop-launcher prefix information and then Wine's default prefix. Settings shows the detected launch environment.

Portalkeeper 0.2.0 consumes **SchemaVersion=1**, generated and published by the server's **mod-realm-config** module. Portalkeeper has no database connection. Obtain the realm's public file from its administrator; [config/example.realm.conf](config/example.realm.conf) shows the complete contract.

Place one non-example `*.realm.conf` in the persistent `Portalkeeper/realms` directory (Windows `%APPDATA%\Portalkeeper\realms`; Linux `$XDG_CONFIG_HOME/Portalkeeper/realms`, normally `~/.config/Portalkeeper/realms`). On first use, the launcher also discovers files beside the executable or in its `config/` folder and the working directory. It copies a single file to persistent storage and retains the original. Multiple candidates require the user to select one by keeping just the desired file in the persistent directory.

`[Realm]` supplies name, description and website; `[Connection] Address` supplies `set realmlist <Address>`. `[Client]` supplies version, build, executable name and optional SHA-256, preserving existing executable metadata/build-marker validation. `[Portalkeeper] MinimumVersion` uses semantic version comparison and gives an upgrade message if incompatible.

`[Services]` supplies `NewsURL`, `CalendarURL`, `ArmoryURL`, `StatusURL`, `ManifestURL` and canonical `ConfigURL`. Empty optional URLs disable the corresponding feature. News, Calendar and Armory retain their existing viewers. `StatusURL` and `ManifestURL` are exposed by the model for discovery; the current application uses TCP health probes and the INI addon/patch catalog, not a second manifest. Neither optional endpoint is required to launch.

Startup and **CHECK AGAIN** refresh Schema v1 realms from `ConfigURL`. Downloads must pass all Schema v1 checks before atomic replacement, and failed refreshes retain the last known-good file. Legacy `[Server]`/`[Updates]` configs use their `UpdateURL` to attempt migration into a separate persistent Schema v1 copy while retaining the original untouched. Missing URLs, failed downloads or failed saves keep a valid legacy realm usable in clearly labeled compatibility mode, with the existing client validation and connection/launch behavior. **CHECK AGAIN** can retry migration. See [migration and upgrades](docs/schema-v1-upgrade.md).

## Optional integrations and dependencies

| Feature | Required for that feature | Optional / fallback |
|---|---|---|
| Launcher | Local supported client; configured realm; Wine on Linux | TCP health is advisory. |
| Addon installation | Writable client addon directory and reachable source | Schema v1 catalog; personal GitHub sources. |
| News/calendar | HTTP(S) schema-v1 feeds from mod-realm-news/mod-realm-calendar | Last cached feed on failure. No server database access. |
| Armory equipment | HTTP(S) schema-v1 mod-realm-armory feed | Cached roster/profiles; icons may be unavailable. |
| Static previews | Local compatible client MPQs and native StormLib matching process architecture | Missing assets/library keep equipment UI and an explanatory placeholder. |
| Transmog previews | Explicit capability in both roster and profile, resolved appearance metadata | Older/unsupported feeds show original equipment. No mandatory mod-transmog dependency. |

Linux preview loading tries `libstorm.so` then `libstorm.so.9`; Windows tries `StormLib.dll`. Install the native library and its transitive dependencies where the operating-system loader can find them. The Windows project and Linux publishing script bundle their respective StormLib binaries. A macOS library name exists in the resolver, but macOS runtime/launch support is not validated.

## Addon management

Realm addons are discovered dynamically from all `[Addon.<key>]` sections. There may be zero, one or many; the historical `config/addons.json` is no longer authoritative or shipped in releases. Required addons must exist with a `.toc` and have no known supported version/commit update before **ENTER REALM**. Recommended and Optional addons do not block launch; an absent Optional addon does not create a readiness warning.

GitHub sources reuse existing commit discovery and ZIP installation. `Ref` selects a branch or tag (including a release's tag); empty means the default branch. HTTP sources supply a ZIP containing the configured addon folder and a `.toc`. Schema v1 does not supply an addon hash; HTTP archives still undergo path, size, link and `.toc` validation. HTTPS is preferred; intentional HTTP remains supported. Downloaded scripts/executables are never executed by the installer.

`InstallDirectory` identifies a folder under `<client>/Interface/AddOns`. Existing installations are recognized through folder/TOC inspection and existing version/commit records. Upgrading Portalkeeper does not reinstall addons. Source failures retain local inspection and report unavailable update information. INSTALL/UPDATE and UPDATE ALL are explicit actions. UNINSTALL moves only the selected folder to a recoverable backup; REMOVE on personal entries only removes management. Components absent from the current realm catalog are never automatically deleted.

## Realm patches

All `[Patch.<key>]` entries appear in **MANAGE PATCHES**, showing name, requirement, destination and validation state. HTTP(S) sources install to `<client>/<InstallDirectory>/<FileName>`. Required patches block launch when missing or hash-invalid. Recommended and Optional patches do not. SHA-256 is checked when supplied; without it, existence is the supported check and the UI states that no hash was supplied.

INSTALL / REPAIR stages a download beside the destination, validates it, backs up any existing file and replaces it only after success. Valid existing hashed patches are not downloaded again. Explicit action can refresh hashless patches. REMOVE requires confirmation and backs up the selected file. No directory-wide pruning occurs. Paths reject roots, traversal, unsafe Windows filenames and symbolic links/junctions; management metadata cannot be targeted.

## Regression checks

```sh
dotnet run --project tests/Portalkeeper.Tests -c Release
bash tests/install-linux-upgrade.sh
```

The dependency-free test executable covers parsing, migration/refresh rollback, semantic versions, path safety, client validation, preserved settings/addons, and real loopback HTTP addon/patch downloads. The shell test runs the Linux installer twice against an isolated fake package and verifies preservation. See [verification](docs/schema-v1-upgrade.md#verification).

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
