#!/usr/bin/env bash
set -euo pipefail
APP_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
export LD_LIBRARY_PATH="$APP_DIR/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
if [[ "$(uname -m)" != x86_64 ]]; then
    echo 'Portalkeeper requires Linux x86_64.' >&2
    exit 1
fi
# The optional .NET LTTng tracing provider is not required for app startup.
missing="$( { ldd "$APP_DIR/Portalkeeper.bin"; find "$APP_DIR" -type f -name '*.so*' ! -name 'libcoreclrtraceptprovider.so' -exec ldd {} \; ; } 2>/dev/null | awk '/not found/{print $1}' | sort -u)"
if [[ -n "$missing" ]]; then
    printf 'Portalkeeper needs these system libraries:\n%s\n' "$missing" >&2
    exit 1
fi
if [[ "${1:-}" == --check-dependencies ]]; then
    echo 'Portalkeeper native dependency check passed.'
    exit 0
fi
exec "$APP_DIR/Portalkeeper.bin" "$@"
