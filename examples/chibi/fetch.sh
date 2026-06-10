#!/usr/bin/env bash
# (Re)pin the vendored chibi-scheme sources in chibi-src/. Same model as
# examples/lua/fetch.sh: the tree is committed in-repo (builds and CI need no
# network); this script exists only to bump the pin. Clones the pinned commit
# and strips the nested .git, leaving a flat snapshot to review and commit.
#
# Why a commit, not a tag: upstream's numbered tags stop at 0.9.1 (2018) and
# the 'stable' tag is equally stale — releases like 0.10.x are cut straight
# from master, which is what distros package. Pin master by commit instead.
set -euo pipefail

CHIBI_COMMIT="${CHIBI_COMMIT:-6fd23611029fd380559987da887ffb53145c78e1}"  # master, 2026-06
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/chibi-src"

rm -rf "$DEST"
echo "Cloning ashinn/chibi-scheme @ $CHIBI_COMMIT into $DEST ..."
# -c core.autocrlf=false: vendor LF endings — the Makefile splices `cat RELEASE`
# into a C string in the generated install.h, and a CR there breaks the build.
git -c core.autocrlf=false clone https://github.com/ashinn/chibi-scheme.git "$DEST"
git -C "$DEST" checkout --detach "$CHIBI_COMMIT"
rm -rf "$DEST/.git"   # vendor a flat snapshot, not a nested repo
echo "Done. $(ls "$DEST"/*.c | wc -l) top-level .c files. Review + commit."
