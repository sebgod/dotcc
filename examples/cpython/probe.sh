#!/usr/bin/env bash
# Compile every CPython translation unit in files.txt with dotcc (--emit=obj) and
# compare the set that emits with the committed emits.txt. The list is a
# ratchet: a unit on it that stops emitting is a regression, and a unit that
# starts emitting must be added, so the list always says what dotcc compiles.
#
#   probe.sh            run and compare; exit 1 on any difference
#   probe.sh --update   run and rewrite emits.txt from the result
#
# Environment: DOTCC_DLL (default: this checkout's Release build), CPYTHON_SRC
# (default: cpython-src/, from fetch.sh), PROBE_OUT (default: out/), PROBE_JOBS
# (default: the CPU count), PROBE_TIMEOUT (seconds per unit, default 600).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && (pwd -W 2>/dev/null || pwd))"
UPDATE=0
case "${1:-}" in
  --update) UPDATE=1 ;;
  "") ;;
  *) echo "usage: probe.sh [--update]" >&2; exit 2 ;;
esac

DLL="${DOTCC_DLL:-$HERE/../../DotCC/bin/Release/net10.0/dotcc.dll}"
SRC="${CPYTHON_SRC:-$HERE/cpython-src}"
OUT="${PROBE_OUT:-$HERE/out}"
JOBS="${PROBE_JOBS:-$(nproc)}"
[ -f "$DLL" ] || { echo "probe.sh: no dotcc build at $DLL (dotnet build DotCC -c Release)" >&2; exit 2; }
[ -d "$SRC/Include" ] || { echo "probe.sh: no CPython sources at $SRC (run fetch.sh)" >&2; exit 2; }

rm -rf "$OUT" && mkdir -p "$OUT/cs" "$OUT/status"
export DLL SRC OUT CFG="$HERE/include" PROBE_TIMEOUT="${PROBE_TIMEOUT:-600}"

# One unit: "<group>|<path under the tree>|<extra flags>". Include order is
# CPython's own: the config dir, Include, Include/internal, then mimalloc's.
unit() {
  local group rel extra name start status first
  IFS='|' read -r group rel extra <<< "$1"
  name="${rel//\//__}"; name="${name%.c}"
  extra="${extra//@TREE@/$SRC}"
  eval "set -- $extra"
  start=$SECONDS
  if timeout "$PROBE_TIMEOUT" dotnet "$DLL" --emit=obj -o "$OUT/cs/$name.cs" \
       -DNDEBUG -I"$CFG" -I"$SRC/Include" -I"$SRC/Include/internal" -I"$SRC/Include/internal/mimalloc" \
       "$@" "$SRC/$rel" > "$OUT/cs/$name.log" 2>&1 && [ -s "$OUT/cs/$name.cs" ]; then
    status=ok; first=""
  else
    status=fail
    first="$(grep -m1 -E 'error|dotcc:|Unhandled exception' "$OUT/cs/$name.log" \
      | sed -e "s#$SRC/##g" -e 's/; expected one of:.*//' | cut -c1-300 || true)"
    [ -n "$first" ] || first="(exit without a diagnostic; see out/cs/$name.log)"
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
grep -vE '^\s*(#|$)' "$HERE/emits.txt" 2>/dev/null > "$OUT/emits.was" || : > "$OUT/emits.was"

total=$(wc -l < "$OUT/results.tsv")
ok=$(wc -l < "$OUT/emits.now")
report() {
  echo "CPython probe: $ok of $total translation units emit"
  awk -F'\t' '{ n[$1]++; if ($3 == "ok") k[$1]++ } END { for (g in n) printf "  %-6s %d of %d\n", g, k[g], n[g] }' \
    "$OUT/results.tsv" | sort
  if [ "$ok" -lt "$total" ]; then
    echo
    echo "Units that do not emit (first diagnostic):"
    awk -F'\t' '$3 != "ok" { printf "  %s: %s\n", $2, $5 }' "$OUT/results.tsv"
  fi
}
report | tee "$OUT/summary.txt"

if [ "$UPDATE" = 1 ]; then
  {
    echo "# CPython translation units that dotcc compiles (--emit=obj), in files.txt order."
    echo "# examples/cpython/probe.sh fails when this list and the probe disagree;"
    echo "# probe.sh --update rewrites it."
    cat "$OUT/emits.now"
  } > "$HERE/emits.txt"
  echo
  echo "emits.txt rewritten: $ok units."
  exit 0
fi

lost=$(grep -vxFf "$OUT/emits.now" "$OUT/emits.was" || true)
gained=$(grep -vxFf "$OUT/emits.was" "$OUT/emits.now" || true)
status=0
if [ -n "$lost" ]; then
  echo
  echo "REGRESSION: listed in emits.txt but no longer emit:"
  while read -r rel; do
    awk -F'\t' -v r="$rel" '$2 == r { printf "  %s: %s\n", $2, $5 }' "$OUT/results.tsv"
  done <<< "$lost"
  status=1
fi
if [ -n "$gained" ]; then
  echo
  echo "Now emit but are not in emits.txt (run probe.sh --update and commit it):"
  sed 's/^/  /' <<< "$gained"
  status=1
fi
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  { echo '```'; cat "$OUT/summary.txt"; echo '```'; } >> "$GITHUB_STEP_SUMMARY"
fi
[ "$status" = 0 ] && echo && echo "emits.txt matches."
exit "$status"
