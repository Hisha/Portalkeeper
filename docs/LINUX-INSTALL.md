# Linux installation

The Linux x64 ZIP includes the .NET runtime, StormLib and its compression/crypto dependencies, a launcher, and a user-level installer. Extract the complete ZIP before running anything. No sudo is required.

## Run without installing

From the extracted folder:

```sh
./Portalkeeper
```

If your archive manager removed executable permissions, run `chmod +x Portalkeeper Portalkeeper.bin` first. Keep all package files together. Use `Portalkeeper`, not `Portalkeeper.bin`: the launcher sets the native-library search path.

## Install and add a menu entry

From the extracted folder:

```sh
bash install.sh
```

The installer checks native dependencies, copies the package into `$XDG_DATA_HOME/Portalkeeper-app` (normally `~/.local/share/Portalkeeper-app`), and creates `portalkeeper.desktop` in the adjacent `applications` directory. Open Portalkeeper from the application menu. The icon is included. If the menu does not refresh immediately, log out and back in.

To update, close Portalkeeper, extract the new ZIP, and run its installer. The installer replaces only its marked application directory. Existing application settings and caches remain separate and are not removed. The old application config directory, root realm files and local backup directory are preserved before the marked application folder is replaced. On first launch, a single legacy/application-adjacent realm is copied to `$XDG_CONFIG_HOME/Portalkeeper/realms` (normally `~/.config/Portalkeeper/realms`) and safely bootstrapped to Schema v1 when possible. Existing WoW directories, addons and patches are never part of the application replacement. See [Schema v1 upgrades](schema-v1-upgrade.md). Keep the extracted download until installation succeeds.

To uninstall, remove the `Portalkeeper-app` directory and `applications/portalkeeper.desktop` under your XDG data directory. This leaves personal configuration/cache files intact.

## Compatibility and troubleshooting

This is a Linux x86_64 build, not ARM. A compatible desktop Linux installation and system libraries are still required. Bundling .NET does not bundle glibc, the display server, or graphics drivers. Building on a newer distribution can require a newer glibc than older distributions provide. Fresh-machine compatibility has not yet been verified.

The package launcher checks linked libraries before startup. To run the check independently:

```sh
./Portalkeeper --check-dependencies
```

If it reports missing libraries, install the distribution packages providing those names. If launch still fails, run `./Portalkeeper` in a terminal and retain the error output. This dependency check does not prove that a graphical session or every dynamically loaded feature works.

## Building the ZIP

From the repository root, run `bash scripts/publish-linux.sh`. The publisher reads `Version` from the project file. It requires .NET SDK, zip, ldd and a Linux x64 build host. The native bundling step currently targets Debian/Ubuntu library and documentation locations; `STORMLIB_PATH` can override the StormLib input file. It includes the system distribution's copyright notices and relevant common license texts in `licenses/native`.

The publisher runs a native dependency check, excludes non-example realm configuration, and packages `install.sh`, the launcher, and the icon. Validate both direct launch and menu launch on a clean supported distribution before a public release. Native source/license distribution requirements must be reviewed for the chosen release dependencies; bundled notices alone are not a complete distribution audit.
