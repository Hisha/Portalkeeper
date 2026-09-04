#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="0.1.0"
RID="linux-x64"
DIST="$ROOT/dist"
PACKAGE_DIR="$DIST/Portalkeeper-$VERSION-$RID"
ARCHIVE="$DIST/Portalkeeper-$VERSION-$RID.zip"
PROJECT="$ROOT/src/Portalkeeper/Portalkeeper.csproj"

command -v dotnet >/dev/null 2>&1 || {
    echo "ERROR: dotnet was not found in PATH." >&2
    exit 1
}

command -v zip >/dev/null 2>&1 || {
    echo "ERROR: zip was not found. Install it before creating the release archive." >&2
    exit 1
}

echo "Publishing Portalkeeper $VERSION for $RID..."
rm -rf "$PACKAGE_DIR" "$ARCHIVE"
mkdir -p "$PACKAGE_DIR"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -o "$PACKAGE_DIR"

cp "$ROOT/README.md" "$PACKAGE_DIR/README.md"
cp "$ROOT/LICENSE" "$PACKAGE_DIR/LICENSE"

# The public example is the only realm config that may ship in a package.
mapfile -t PRIVATE_REALM_FILES < <(
    find "$PACKAGE_DIR" -type f -name '*.realm.conf' \
        ! -name 'example.realm.conf' -print
)

if (( ${#PRIVATE_REALM_FILES[@]} > 0 )); then
    echo "ERROR: private realm configuration detected in release output:" >&2
    printf '  %s\n' "${PRIVATE_REALM_FILES[@]}" >&2
    exit 1
fi

# Development-only files should never be handed to users.
find "$PACKAGE_DIR" -type f \( -name '*.pdb' -o -name '*.Development.json' \) -delete

(
    cd "$DIST"
    zip -qr "$(basename "$ARCHIVE")" "$(basename "$PACKAGE_DIR")"
)

echo
echo "Release package created:"
echo "  $ARCHIVE"
echo
echo "Contents are self-contained; users do not need to install .NET."
