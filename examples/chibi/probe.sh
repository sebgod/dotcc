#!/usr/bin/env bash
# Probe scoreboard for chibi-scheme: run `dotcc --emit=obj` over every core
# translation unit and print a pass/fail table with the first error per TU.
# Same moving-scoreboard model as examples/lua/probe.sh — re-run after each
# compiler fix to see the wall recede. Pure compile probe (no link, no run).
#
# Reference oracle: the gcc-in-WSL build (make all) + tests/r7rs-tests.scm,
# snapshot in baseline-r7rs.txt (1225/1225 under chibi 0.12.0 "magnesium").
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/chibi-src"
ROOT="$(cd "$HERE/../.." && pwd)"
DLL="$ROOT/DotCC/bin/Release/net10.0/dotcc.dll"
OUT="$(mktemp -d)"

# The 9 real TUs from the Makefile: SEXP_OBJS + EVAL_OBJS + main.
# (plan9.c is plan-9-only; clibs.c / opt/*.c are conditional .c-includes.)
UNITS="gc sexp bignum gc_heap opcodes vm eval simplify main"

# -I: gen-include carries the Makefile-generated chibi/install.h (captured from
# the WSL reference build); chibi-src/include is the real header tree.
# -D mirrors the reference build's command line (Makefile.detect on linux),
# plus dotcc's configuration choices: SEXP_USE_DL=0 (static modules — no
# dlopen on .NET; chibi's own static builds use the same) and
# SEXP_USE_POLL_PORT=0 (drops the POSIX select()/fd_set poll path).
FLAGS="-I $HERE/gen-include -I $SRC/include -D SEXP_USE_INTTYPES -D SEXP_USE_NTPGETTIME -D SEXP_USE_DL=0 -D SEXP_USE_POLL_PORT=0"

pass=0; fail=0
printf "%-10s %-6s %s\n" "TU" "STATUS" "first error (if any)"
printf "%-10s %-6s %s\n" "----" "------" "--------------------"
for tu in $UNITS; do
    f="$SRC/$tu.c"
    [ -f "$f" ] || { printf "%-10s %-6s %s\n" "$tu" "MISS" "(no $tu.c)"; continue; }
    err=$(dotnet "$DLL" --emit=obj $FLAGS "$f" -o "$OUT/$tu.cs" 2>&1 >/dev/null)
    if [ $? -eq 0 ]; then
        printf "%-10s \033[32m%-6s\033[0m\n" "$tu" "OK"
        pass=$((pass+1))
    else
        first=$(echo "$err" | grep -m1 -iE "parse failed|lex failed|error|isn't supported|not supported|unsupported" | sed 's/^dotcc: //' | cut -c1-110)
        [ -z "$first" ] && first=$(echo "$err" | grep -m1 . | sed 's/^dotcc: //' | cut -c1-110)
        printf "%-10s \033[31m%-6s\033[0m %s\n" "$tu" "FAIL" "$first"
        fail=$((fail+1))
    fi
done
echo "-----"
echo "passed: $pass   failed: $fail   (of $((pass+fail)))"
rm -rf "$OUT"
