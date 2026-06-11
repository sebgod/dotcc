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

# Phase 3 static clibs: SEXP_USE_STATIC_LIBS makes eval.c `#include "clibs.c"`,
# which (in the --no-inline form genstatic emitted) `#include`s each module's
# stub .c with a renamed sexp_init_library, and registers them in
# sexp_static_libraries[]. So `(srfi 69)` / `(scheme time)` resolve through
# sexp_find_static_library (a compiled-in table keyed by the module path)
# instead of dlopen — no dynamism at all (see HANDOVER.md "Why static").
#   =1 not a bare -D: features.h does `#if SEXP_USE_STATIC_LIBS` (empty breaks it).
#   NO_INCLUDE=0: features.h defaults it to 1 (the extern-decl path, clibs.c as a
#     separate TU); 0 selects the `#include "clibs.c"` path — what emscripten's
#     static build uses, and the one-TU-chain dotcc wants.
#   -I gen-lib : eval.c finds clibs.c (the captured genstatic output)
#   -I $SRC    : clibs.c finds lib/srfi/69/hash.c, lib/scheme/time.c
FLAGS="$FLAGS -D SEXP_USE_STATIC_LIBS=1 -D SEXP_USE_STATIC_LIBS_NO_INCLUDE=0 -I $HERE/gen-lib -I $SRC"

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
