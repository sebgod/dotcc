#!/usr/bin/env bash
# Phase 2 link harness: whole-program-compile all 9 chibi-scheme core TUs
# together and emit a .NET build. Unlike probe.sh (which only --emit=obj's each
# TU in isolation), this exercises the FULL round-trip: merge all TUs -> Roslyn
# compile -> link. That surfaces a new class of issues (C# compile errors in
# emitted code, cross-TU symbol merge, emit-shape problems) the emit-only probe
# never hits. Same playbook as examples/lua/link.sh, except chibi's main.c IS
# the driver (it owns main()), so no separate driver TU.
#
#   link.sh            # --emit=build (Roslyn compile via dotnet build)
#   link.sh file       # emit a single .cs file instead (faster to eyeball)
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/chibi-src"
ROOT="$(cd "$HERE/../.." && pwd)"
DLL="$ROOT/DotCC/bin/Release/net10.0/dotcc.dll"

# The 9 real TUs from the Makefile: SEXP_OBJS + EVAL_OBJS + main.
UNITS="gc sexp bignum gc_heap opcodes vm eval simplify main"
MODE="${1:-build}"

SRCS=""
for tu in $UNITS; do
    SRCS="$SRCS $SRC/$tu.c"
done

# Same configuration as probe.sh (see HANDOVER.md for the why of each -D).
FLAGS="-I $HERE/gen-include -I $SRC/include -D SEXP_USE_INTTYPES -D SEXP_USE_NTPGETTIME -D SEXP_USE_DL=0 -D SEXP_USE_POLL_PORT=0"

OUT="$HERE/build"
if [ "$MODE" = "file" ]; then
    echo "Linking $(echo $UNITS | wc -w) TUs  (mode=file -> $OUT/chibi.cs)"
    mkdir -p "$OUT"
    dotnet "$DLL" --emit=file $FLAGS $SRCS -o "$OUT/chibi.cs"
else
    rm -rf "$OUT"
    echo "Linking $(echo $UNITS | wc -w) TUs  (mode=$MODE -> $OUT)"
    dotnet "$DLL" --emit="$MODE" $FLAGS $SRCS -o "$OUT"
fi
