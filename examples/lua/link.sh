#!/usr/bin/env bash
# Phase 6 link harness: whole-program-compile the Lua core (+ lib) TUs together
# with a driver main, emit a .NET build, and (optionally) run it. Unlike probe.sh
# (which only --emit=obj's each TU in isolation), this exercises the FULL
# round-trip: merge all TUs -> Roslyn compile -> link -> run. That surfaces a new
# class of issues (C# compile errors in emitted code, cross-TU symbol merge,
# runtime semantics) the emit-only probe never hits.
#
#   link.sh            # core only (20 TUs) + driver, --emit=build
#   link.sh all        # core + lib (31 TUs) + driver
#   link.sh all file   # ...emit a single file instead (faster to eyeball)
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/lua-src"
ROOT="$(cd "$HERE/../.." && pwd)"
DLL="$ROOT/DotCC/bin/Release/net10.0/dotcc.dll"

CORE="lapi lcode lctype ldebug ldo ldump lfunc lgc llex lmem lobject lopcodes lparser lstate lstring ltable ltm lundump lvm lzio"
LIB="lauxlib lbaselib lcorolib ldblib liolib lmathlib loslib lstrlib ltablib lutf8lib linit"

UNITS="$CORE"
[ "${1:-}" = "all" ] && UNITS="$CORE $LIB"
MODE="${2:-build}"

# Collect the TU paths.
SRCS=""
for tu in $UNITS; do
    f="$SRC/$tu.c"
    [ -f "$f" ] && SRCS="$SRCS $f"
done

DRIVER="$HERE/driver.c"
OUT="$HERE/build"
rm -rf "$OUT"

echo "Linking $(echo $UNITS | wc -w) TUs + driver  (mode=$MODE)"
dotnet "$DLL" --emit="$MODE" -I "$SRC" $SRCS "$DRIVER" -o "$OUT"
