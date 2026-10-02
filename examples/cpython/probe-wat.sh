#!/usr/bin/env bash
# The wat target's CPython probe: compile every CPython translation unit in files.txt with
# dotcc --target=wat --emit=obj (one unit on its own, what it does not define imported from
# env), assemble it with wat2wasm, and compare the set that gets through with the committed
# emits-wat.txt. Like probe.sh's emits.txt the list is a ratchet: a unit on it that stops
# compiling is a regression, and a unit that starts compiling must be added.
#
#   probe-wat.sh            run and compare; exit 1 on any difference
#   probe-wat.sh --update   run and rewrite emits-wat.txt from the result
#
# Each unit ends as ok (compiles and assembles), emit (dotcc refused it: the reason is its
# first diagnostic) or asm (wat2wasm rejected the module). The summary counts the refusals
# by reason, which orders the next wat bricks (GH #264).
#
# Environment: DOTCC_DLL (default: this checkout's Release build), CPYTHON_SRC (default:
# cpython-src/, from fetch.sh), WAT2WASM (default: wat2wasm on PATH; without one the
# assembly step is skipped and ok means it compiled), PROBE_OUT (default: out-wat/),
# PROBE_JOBS (default: the CPU count), PROBE_TIMEOUT (seconds per unit, default 600).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && (pwd -W 2>/dev/null || pwd))"
UPDATE=0
case "${1:-}" in
  --update) UPDATE=1 ;;
  "") ;;
  *) echo "usage: probe-wat.sh [--update]" >&2; exit 2 ;;
esac

DLL="${DOTCC_DLL:-$HERE/../../DotCC/bin/Release/net10.0/dotcc.dll}"
SRC="${CPYTHON_SRC:-$HERE/cpython-src}"
OUT="${PROBE_OUT:-$HERE/out-wat}"
JOBS="${PROBE_JOBS:-$(nproc)}"
W2W="${WAT2WASM:-$(command -v wat2wasm || true)}"
[ -f "$DLL" ] || { echo "probe-wat.sh: no dotcc build at $DLL (dotnet build DotCC -c Release)" >&2; exit 2; }
[ -d "$SRC/Include" ] || { echo "probe-wat.sh: no CPython sources at $SRC (run fetch.sh)" >&2; exit 2; }
[ -n "$W2W" ] || echo "probe-wat.sh: no wat2wasm (set WAT2WASM); modules are not assembled" >&2

rm -rf "$OUT" && mkdir -p "$OUT/wat" "$OUT/status"
export DLL SRC OUT W2W CFG="$HERE/include" PROBE_TIMEOUT="${PROBE_TIMEOUT:-600}"

# One unit: "<group>|<path under the tree>|<extra flags>", with probe.sh's include order.
unit() {
  local group rel extra name start status first
  IFS='|' read -r group rel extra <<< "$1"
  name="${rel//\//__}"; name="${name%.c}"
  extra="${extra//@TREE@/$SRC}"
  eval "set -- $extra"
  start=$SECONDS
  if timeout "$PROBE_TIMEOUT" dotnet "$DLL" --target=wat --emit=obj -o "$OUT/wat/$name.wat" \
       -DNDEBUG -I"$CFG" -I"$SRC/Include" -I"$SRC/Include/internal" -I"$SRC/Include/internal/mimalloc" \
       "$@" "$SRC/$rel" > "$OUT/wat/$name.log" 2>&1 && [ -s "$OUT/wat/$name.wat" ]; then
    if [ -z "$W2W" ] || "$W2W" --enable-threads --enable-exceptions "$OUT/wat/$name.wat" \
         -o "$OUT/wat/$name.wasm" > "$OUT/wat/$name.asm.log" 2>&1; then
      status=ok; first=""
    else
      status=asm
      first="$(grep -m1 'error' "$OUT/wat/$name.asm.log" | sed -e 's/^.*error: //' | cut -c1-300 || true)"
    fi
    rm -f "$OUT/wat/$name.wasm"
  else
    status=emit
    first="$(grep -E 'error|dotcc:|Unhandled exception' "$OUT/wat/$name.log" | grep -v 'warning:' | head -1 \
      | sed -e "s#$SRC/##g" -e 's/; expected one of:.*//' -e 's/^dotcc: //' -e 's/^dotcc does not yet support: //' \
      | cut -c1-300 || true)"
    [ -n "$first" ] || first="(exit without a diagnostic; see out-wat/wat/$name.log)"
  fi
  printf '%s\t%s\t%s\t%s\t%s\n' "$group" "$rel" "$status" "$((SECONDS - start))" "$first" \
    > "$OUT/status/$name.tsv"
}
export -f unit

grep -vE '^\s*(#|$)' "$HERE/files.txt" | xargs -d '\n' -P "$JOBS" -I{} bash -c 'unit "$1"' _ {}

# Collect in files.txt order.
while IFS='|' read -r group rel extra; do
  name="${rel//\//__}"; name="${name%.c}"
  cat "$OUT/status/$name.tsv"
done < <(grep -vE '^\s*(#|$)' "$HERE/files.txt") > "$OUT/results.tsv"

awk -F'\t' '$3 == "ok" { print $2 }' "$OUT/results.tsv" > "$OUT/emits.now"
grep -vE '^\s*(#|$)' "$HERE/emits-wat.txt" 2>/dev/null > "$OUT/emits.was" || : > "$OUT/emits.was"

total=$(wc -l < "$OUT/results.tsv")
ok=$(wc -l < "$OUT/emits.now")
report() {
  echo "CPython wat probe: $ok of $total translation units compile and assemble"
  awk -F'\t' '{ n[$3]++ } END { for (s in n) printf "  %-5s %d\n", s, n[s] }' "$OUT/results.tsv" | sort
  if [ "$ok" -lt "$total" ]; then
    echo
    echo "Refusals by reason (names elided):"
    awk -F'\t' '$3 != "ok" { print $3 ": " $5 }' "$OUT/results.tsv" \
      | sed -E "s/'[^']*'/'…'/g; s/[0-9]+:[0-9]+:/L:C:/g" | sort | uniq -c | sort -rn
    echo
    echo "Units that do not get through (first diagnostic):"
    awk -F'\t' '$3 != "ok" { printf "  %s [%s]: %s\n", $2, $3, $5 }' "$OUT/results.tsv"
  fi
}
report | tee "$OUT/summary.txt"

if [ "$UPDATE" = 1 ]; then
  {
    echo "# The CPython units dotcc compiles with --target=wat --emit=obj and wat2wasm assembles"
    echo "# (probe-wat.sh). A ratchet: probe-wat.sh fails when a listed unit stops getting"
    echo "# through or an unlisted one starts; rewrite it with probe-wat.sh --update."
    cat "$OUT/emits.now"
  } > "$HERE/emits-wat.txt"
  echo "probe-wat.sh: wrote emits-wat.txt ($ok units)"
  exit 0
fi

if ! diff -u "$OUT/emits.was" "$OUT/emits.now" > "$OUT/emits.diff"; then
  echo
  echo "probe-wat.sh: the units that get through differ from emits-wat.txt (- stopped, + started):"
  grep -E '^[-+][^-+]' "$OUT/emits.diff" || true
  echo "Run probe-wat.sh --update to accept the new set."
  exit 1
fi
echo "probe-wat.sh: matches emits-wat.txt"
