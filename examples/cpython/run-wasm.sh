#!/usr/bin/env bash
# Run build-wat.sh's python.wasm under node (Scripts/wat-run.js), as run.sh runs the C# build:
# the pinned tree's Lib/ is the standard library (a home like run.sh's, preopened as /py), the
# module itself is /py/bin/python.wasm (its sys.executable), and the current directory is the
# program's / and its current directory, so a relative path means what it means here.
#
#   run-wasm.sh -c "print('hello')"
#   RUN=run-wasm.sh programs.sh
#
# Environment: NODE (default: node), NODE_FLAGS (more flags for node, such as --liftoff-only
# to run on V8's baseline compiler alone), CPYTHON_SRC (default: cpython-src/), BUILD_OUT
# (default: build-wat/).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
SRC="${CPYTHON_SRC:-$HERE/cpython-src}"
OUT="${BUILD_OUT:-$HERE/build-wat}"
WASM="$OUT/python.wasm"
[ -f "$WASM" ] || { echo "run-wasm.sh: no interpreter at $WASM (run build-wat.sh)" >&2; exit 2; }

LIB="$OUT/home/lib/python3.13"
if [ ! -e "$LIB" ]; then
  mkdir -p "$OUT/home/lib"
  if command -v cygpath >/dev/null 2>&1; then
    cmd //c mklink //J "$(cygpath -w "$LIB")" "$(cygpath -w "$SRC/Lib")" >/dev/null
  else
    ln -s "$SRC/Lib" "$LIB"
  fi
fi

# A host path as node takes it: on Windows (Git Bash) C:/... rather than /c/...
host() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }

# MSYS_NO_PATHCONV: Git Bash would rewrite the guest paths (/py, /) into Windows ones.
# shellcheck disable=SC2086
exec env MSYS_NO_PATHCONV=1 "${NODE:-node}" ${NODE_FLAGS:-} --stack-size=8000 "$(host "$REPO/Scripts/wat-run.js")" \
  --dir "$(host "$PWD")::/" --dir "$(host "$OUT/home")::/py" --dir "$(host "$OUT")::/py/bin" \
  --env PYTHONHOME=/py --argv0 /py/bin/python.wasm "$(host "$WASM")" "$@"
