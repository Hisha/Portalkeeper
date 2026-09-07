#!/usr/bin/env bash
set -euo pipefail
SOURCE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
[[ "$DATA_DIR" == /* ]] || { echo 'XDG_DATA_HOME must be an absolute path.' >&2; exit 1; }
[[ "$DATA_DIR" != *$'\n'* && "$DATA_DIR" != *$'\r'* ]] || { echo 'Installation path cannot contain newlines.' >&2; exit 1; }
DEST="$DATA_DIR/Portalkeeper-app"
APPS="$DATA_DIR/applications"
[[ "$SOURCE" != "$DEST" ]] || { echo 'Run the installer from the extracted ZIP.' >&2; exit 1; }
chmod +x "$SOURCE/Portalkeeper" "$SOURCE/Portalkeeper.bin"
"$SOURCE/Portalkeeper" --check-dependencies
if [[ -e "$DEST" && ! -f "$DEST/.portalkeeper-install" ]]; then
    echo "Refusing to replace an unmanaged folder: $DEST" >&2
    exit 1
fi
mkdir -p "$DATA_DIR" "$APPS"
STAGE="$(mktemp -d "$DATA_DIR/.portalkeeper-install.XXXXXX")"
trap 'rm -rf -- "$STAGE"' EXIT
cp -a "$SOURCE/." "$STAGE/"
touch "$STAGE/.portalkeeper-install"
# Keep the previous application available until the replacement is staged.
BACKUP=""
if [[ -d "$DEST" ]]; then
    BACKUP="$(mktemp -d "$DATA_DIR/.portalkeeper-old.XXXXXX")"
    rmdir "$BACKUP"
    mv -- "$DEST" "$BACKUP"
fi
if ! mv -- "$STAGE" "$DEST"; then
    [[ -z "$BACKUP" ]] || mv -- "$BACKUP" "$DEST"
    exit 1
fi
# Desktop Entry string escaping, followed by Exec argument escaping.
escape_value() { local v="$1"; v="${v//\\/\\\\}"; printf '%s' "$v"; }
exec_path="$DEST/Portalkeeper"
exec_path="${exec_path//\\/\\\\}"
exec_path="${exec_path//\"/\\\"}"
exec_path="${exec_path//\$/\\\$}"
exec_path="${exec_path//\`/\\\`}"
exec_path="${exec_path//%/%%}"
{
    printf '[Desktop Entry]\nType=Application\nName=Portalkeeper\nComment=Realm launcher and Armory\n'
    printf 'Exec=%s\n' "$(escape_value "\"$exec_path\"")"
    printf 'Icon=%s\n' "$(escape_value "$DEST/portalkeeper-icon.png")"
    printf 'Terminal=false\nCategories=Game;\nStartupNotify=true\n'
} > "$APPS/portalkeeper.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" || true
[[ -z "$BACKUP" ]] || rm -rf -- "$BACKUP"
printf 'Installed Portalkeeper to %s\nOpen Portalkeeper from your application menu.\n' "$DEST"
