# Portalkeeper

Portalkeeper is an independent, cross-platform realm launcher and addon manager for **World of Warcraft 3.3.5a (build 12340)**. It is designed to make a private realm easier to use without distributing or modifying the game client itself.

Portalkeeper can validate a 3.3.5a client, apply the selected realm's `realmlist.wtf`, check realm availability, manage required/recommended/personal addons, install and update GitHub-hosted addons, and launch World of Warcraft on Windows or Linux.

> Portalkeeper is an independent, community-developed project. It is not affiliated with or endorsed by AzerothCore, the AzerothCore development team, Blizzard Entertainment, or World of Warcraft.

## Current status

Version **0.1.0** is under active development. Linux functionality has been validated with a published build. Windows validation and packaging are still in progress.

## What Portalkeeper manages

Portalkeeper intentionally keeps its scope narrow:

- validates a WoW 3.3.5a build 12340 installation;
- discovers one local `*.realm.conf` file and writes the appropriate `realmlist.wtf` when launching;
- checks authentication and world-server reachability without blocking launch solely because a health probe fails;
- reads realm addon policy from `config/addons.json` or a configured remote manifest;
- supports required, recommended, and personal addons;
- discovers GitHub-hosted addons from their repository and `.toc` metadata;
- installs/updates only addons Portalkeeper manages;
- backs up an existing managed addon before replacing it;
- preserves unrelated addons and WoW `WTF`/SavedVariables data;
- launches WoW directly on Windows and through Wine on Linux;
- can hide while WoW is running and restore when the game exits.

Portalkeeper does **not** download or distribute World of Warcraft, store account credentials, or modify AzerothCore server code.

## Realm configuration

Copy `config/example.realm.conf` to a new file ending in `.realm.conf`, for example:

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
```

`AuthPort` and `WorldPort` are optional and default to `3724` and `8085`.

Place the private realm file either beside the Portalkeeper executable or, preferably, in Portalkeeper's `config/` folder. Portalkeeper deliberately ignores `example.realm.conf`. If no usable realm file is present, the Realm card explains what to do and provides **CHECK AGAIN**, so a restart is not required after adding the file.

Private `*.realm.conf` files are ignored by Git and should not be committed to a public repository.

## Realm addon policy

`config/addons.json` describes addons managed by the realm. GitHub-hosted addons normally need only their repository URL:

```json
{
  "manifestVersion": 1,
  "addons": [
    {
      "id": "example-addon",
      "name": "ExampleAddon",
      "required": false,
      "recommended": true,
      "gitUrl": "https://github.com/owner/repository"
    }
  ]
}
```

Portalkeeper discovers the repository's default branch, current commit, addon folder, `.toc`, and `## Version` automatically when possible. `addonPath` can be supplied for genuinely ambiguous multi-addon repositories.

Required addons gate **ENTER REALM** until installed/current. Recommended and personal addons do not. If a personal addon later becomes realm-managed, the realm entry wins and the redundant personal-management record is removed without deleting or reinstalling the addon.

## Building from source

Requirements:

- .NET 10 SDK
- Git

From the repository root:

```bash
dotnet restore src/Portalkeeper/Portalkeeper.csproj
dotnet build src/Portalkeeper/Portalkeeper.csproj
```

Run during development with:

```bash
dotnet run --project src/Portalkeeper/Portalkeeper.csproj
```

## Creating the Linux 0.1.0 package

The repository includes a packaging script that creates a clean, self-contained `linux-x64` ZIP. A recipient does **not** need to install .NET.

```bash
./scripts/publish-linux.sh
```

The resulting archive is written to:

```text
dist/Portalkeeper-0.1.0-linux-x64.zip
```

The script starts from a clean release directory, copies only public documentation/configuration, removes development symbols, and fails if it detects any private `*.realm.conf` file in the package.

## Linux / Wine

Portalkeeper uses Wine to launch `Wow.exe`. It first honors an existing `WINEPREFIX`, then looks for a matching desktop launcher and its configured prefix, including the logged-in user's normal desktop-entry location. If no dedicated prefix can be identified, Wine's default prefix is used and Settings reports that fact.

## Addon backups and local state

Managed addon backups are stored under the WoW client directory:

```text
.portalkeeper/backups/
```

Portalkeeper's user settings, GitHub source cache, and personal-addon list are stored in the operating system's normal per-user application-data location. These are not part of a release package.

## Privacy and distribution

Do not commit private realm addresses, credentials, or private URLs to the public repository. Portalkeeper does not need a user's WoW account name or password and should never be used to distribute Blizzard client binaries.

## License

See [LICENSE](LICENSE).
