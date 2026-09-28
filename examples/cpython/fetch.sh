#!/usr/bin/env bash
# Fetch the pinned CPython sources into cpython-src/ (git-ignored): a shallow,
# sparse clone of just the directories the probe compiles and includes. Unlike
# examples/lua/lua-src, CPython is too large to vendor, so CI runs this too (and
# caches the result by tag). Idempotent: an existing checkout at the pinned tag
# is left alone.
set -euo pipefail

# Pinned tag. Bump it together with include/pyconfig.h (re-run ./configure at the
# new tag and re-apply the dotcc adjustments) and emits.txt (probe.sh --update).
CPYTHON_TAG="${CPYTHON_TAG:-v3.13.15}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && (pwd -W 2>/dev/null || pwd))"
DEST="$HERE/cpython-src"

if [ -f "$DEST/.pinned-tag" ] && [ "$(cat "$DEST/.pinned-tag")" = "$CPYTHON_TAG" ]; then
  echo "cpython-src is already at $CPYTHON_TAG"
  exit 0
fi

# Empty the directory rather than remove it: git clones into an empty one, and a
# shell sitting in it (Windows holds the handle) cannot stop the refresh.
if [ -d "$DEST" ]; then find "$DEST" -mindepth 1 -maxdepth 1 -exec rm -rf {} +; fi
echo "Cloning python/cpython @ $CPYTHON_TAG (sparse) into $DEST ..."
# LF checkout on every host, like CI's; long paths for Windows working trees.
git -c core.autocrlf=false -c core.longpaths=true -c advice.detachedHead=false clone --quiet --depth 1 \
  --branch "$CPYTHON_TAG" --filter=blob:none --sparse \
  https://github.com/python/cpython.git "$DEST"
git -C "$DEST" -c core.autocrlf=false sparse-checkout set \
  Include Objects Parser Programs Python Modules Lib Tools/freeze
rm -rf "$DEST/.git"   # a flat snapshot, not a nested repo
echo "$CPYTHON_TAG" > "$DEST/.pinned-tag"
echo "Done. $(find "$DEST" -name '*.c' | wc -l) .c files, $(find "$DEST" -name '*.h' | wc -l) headers."
