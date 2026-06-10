#!/usr/bin/env bash
# (Re)pin the vendored Lua sources in lua-src/. The tree is now committed in-repo
# (so builds and CI need no network); we just don't carry upstream's git history.
# This clones the pinned tag and strips the nested .git, leaving a flat snapshot
# to review and commit. Run it only to bump the pinned version.
set -euo pipefail

# Pinned tag. v5.5.0 is the current 5.5 release (the manual calls the series
# "5.5"; there is no 5.5.1 yet). Bump here when a newer 5.5.x lands.
LUA_TAG="${LUA_TAG:-v5.5.0}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/lua-src"

rm -rf "$DEST"
echo "Cloning lua/lua @ $LUA_TAG into $DEST ..."
git clone --depth 1 --branch "$LUA_TAG" https://github.com/lua/lua.git "$DEST"
rm -rf "$DEST/.git"   # vendor a flat snapshot, not a nested repo
echo "Done. $(ls "$DEST"/*.c | wc -l) .c files, $(ls "$DEST"/*.h | wc -l) headers. Review + commit."
