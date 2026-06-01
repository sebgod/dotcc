#!/usr/bin/env bash
# Fetch the pinned Lua sources into a gitignored lua-src/ (we don't vendor —
# Lua is MIT but large, and this is an example, not a dependency). Re-runnable.
set -euo pipefail

# Pinned tag. v5.5.0 is the current 5.5 release (the manual calls the series
# "5.5"; there is no 5.5.1 yet). Bump here when a newer 5.5.x lands.
LUA_TAG="${LUA_TAG:-v5.5.0}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/lua-src"

if [ -d "$DEST/.git" ]; then
    echo "lua-src already present ($(git -C "$DEST" describe --tags 2>/dev/null || echo unknown)); skipping clone."
    exit 0
fi

rm -rf "$DEST"
echo "Cloning lua/lua @ $LUA_TAG into $DEST ..."
git clone --depth 1 --branch "$LUA_TAG" https://github.com/lua/lua.git "$DEST"
echo "Done. $(ls "$DEST"/*.c | wc -l) .c files, $(ls "$DEST"/*.h | wc -l) headers."
