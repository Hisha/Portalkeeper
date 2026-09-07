#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="linux-x64"
DIST="$ROOT/dist"
PROJECT="$ROOT/src/Portalkeeper/Portalkeeper.csproj"
command -v dotnet >/dev/null 2>&1 || {
    echo "ERROR: dotnet was not found in PATH." >&2
    exit 1
}

command -v zip >/dev/null 2>&1 || {
    echo "ERROR: zip was not found. Install it before creating the release archive." >&2
    exit 1
}

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version)"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.+-][A-Za-z0-9.-]+)?$ ]] || { echo "Invalid project version: $VERSION" >&2; exit 1; }
PACKAGE_DIR="$DIST/Portalkeeper-$VERSION-$RID"
ARCHIVE="$DIST/Portalkeeper-$VERSION-$RID.zip"

echo "Publishing Portalkeeper $VERSION for $RID..."
rm -rf "$PACKAGE_DIR" "$ARCHIVE"
mkdir -p "$PACKAGE_DIR"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -o "$PACKAGE_DIR"

# Bundle StormLib and its non-glibc dependencies from this build host.
STORMLIB="${STORMLIB_PATH:-/usr/lib/x86_64-linux-gnu/libstorm.so.9}"
[[ -f "$STORMLIB" ]] || { echo "StormLib missing: $STORMLIB" >&2; exit 1; }
mkdir -p "$PACKAGE_DIR/native" "$PACKAGE_DIR/licenses/native"
cp -L "$STORMLIB" "$PACKAGE_DIR/native/libstorm.so"
while read -r soname arrow library rest; do
    case "$soname" in
        libz.so.*|libbz2.so.*|libtomcrypt.so.*|libtommath.so.*|libgmp.so.*)
            [[ "$arrow" == '=>' && -f "$library" ]] || { echo "Missing dependency: $soname" >&2; exit 1; }
            cp -L "$library" "$PACKAGE_DIR/native/$soname"
            ;;
    esac
done < <(ldd "$STORMLIB")
# Include the build distribution's copyright/license notices for bundled libraries.
for package in libstorm9 zlib1g libbz2-1.0 libtomcrypt1 libtommath1 libgmp10; do
    notice="/usr/share/doc/$package/copyright"
    [[ -f "$notice" ]] || { echo "Missing license notice: $notice" >&2; exit 1; }
    cp -L "$notice" "$PACKAGE_DIR/licenses/native/$package.txt"
done
for license in GPL-2 GPL-3 LGPL-2 LGPL-2.1 LGPL-3; do
    [[ ! -f "/usr/share/common-licenses/$license" ]] || cp -L "/usr/share/common-licenses/$license" "$PACKAGE_DIR/licenses/native/$license.txt"
done
mv "$PACKAGE_DIR/Portalkeeper" "$PACKAGE_DIR/Portalkeeper.bin"
cp "$ROOT/scripts/launch-linux.sh" "$PACKAGE_DIR/Portalkeeper"
cp "$ROOT/scripts/install-linux.sh" "$PACKAGE_DIR/install.sh"
cp "$ROOT/assets/branding/portalkeeper-icon.png" "$PACKAGE_DIR/portalkeeper-icon.png"
cp "$ROOT/docs/LINUX-INSTALL.md" "$PACKAGE_DIR/INSTALL-LINUX.md"
chmod +x "$PACKAGE_DIR/Portalkeeper" "$PACKAGE_DIR/Portalkeeper.bin" "$PACKAGE_DIR/install.sh"
"$PACKAGE_DIR/Portalkeeper" --check-dependencies

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
echo "Extract and run ./Portalkeeper, or bash install.sh for a user-level menu installation."
echo "The .NET runtime and StormLib libraries are bundled; compatible Linux system libraries are required."
