#!/usr/bin/env bash
# Probe scoreboard: run `dotcc --emit=obj` over every Lua core (+ optionally lib)
# translation unit and print a pass/fail table with the first error per TU. This
# is the moving scoreboard for Phases 4–5 — re-run it after each fix to see the
# wall recede. Pure compile probe (no link, no run); --emit=obj compiles ONE TU
# to a .cs object fragment, which is exactly what the per-file gaps surface on.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/lua-src"
ROOT="$(cd "$HERE/../.." && pwd)"
DLL="$ROOT/DotCC/bin/Release/net10.0/dotcc.dll"
OUT="$(mktemp -d)"

# CORE_O from Lua's makefile (ltests is debug-only — skipped).
CORE="lapi lcode lctype ldebug ldo ldump lfunc lgc llex lmem lobject lopcodes lparser lstate lstring ltable ltm lundump lvm lzio"
# AUX + LIB_O minus loadlib (needs dlfcn/windows.h). Probed when $1 = all.
LIB="lauxlib lbaselib lcorolib ldblib liolib lmathlib loslib lstrlib ltablib lutf8lib linit"

UNITS="$CORE"
[ "${1:-}" = "all" ] && UNITS="$CORE $LIB"

pass=0; fail=0
printf "%-12s %-6s %s\n" "TU" "STATUS" "first error (if any)"
printf "%-12s %-6s %s\n" "----" "------" "--------------------"
for tu in $UNITS; do
    f="$SRC/$tu.c"
    [ -f "$f" ] || { printf "%-12s %-6s %s\n" "$tu" "MISS" "(no $tu.c)"; continue; }
    err=$(dotnet "$DLL" --emit=obj -I "$SRC" "$f" -o "$OUT/$tu.cs" 2>&1 >/dev/null)
    if [ $? -eq 0 ]; then
        printf "%-12s \033[32m%-6s\033[0m\n" "$tu" "OK"
        pass=$((pass+1))
    else
        first=$(echo "$err" | grep -m1 -iE "parse failed|lex failed|error|isn't supported|not supported|unsupported" | sed 's/^dotcc: //' | cut -c1-110)
        [ -z "$first" ] && first=$(echo "$err" | grep -m1 . | sed 's/^dotcc: //' | cut -c1-110)
        printf "%-12s \033[31m%-6s\033[0m %s\n" "$tu" "FAIL" "$first"
        fail=$((fail+1))
    fi
done
echo "-----"
echo "passed: $pass   failed: $fail   (of $((pass+fail)))"
rm -rf "$OUT"
