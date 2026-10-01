#!/usr/bin/env bash
# Run every C fixture through dotcc --target=wat, wat2wasm and node, and say where each stops:
#   EMIT <reason>   dotcc refused it (the reason the wat backend gives)
#   ASM             wat2wasm rejected the module
#   IMPORT / TRAP   it needs an import the shim lacks / it trapped (or timed out)
#   WRONG           it ran, and its stdout differs from expected-stdout.txt
#   PASS
# then count each kind and the refusals by reason, which orders the next wat bricks.
# DotCC.FunctionalTests/WatFixtureTests is the CI ratchet over the same fixtures
# (Fixtures/wat-fixtures.txt); this is the measuring tool.
#
# Usage:
#   dotnet build DotCC -c Release                   # build first; this script never builds
#   Scripts/wat-probe.sh [outdir]                   # default: out/wat-probe
# Environment: DOTCC_DLL (default DotCC/bin/Release/net10.0/dotcc.dll), WAT2WASM (wat2wasm),
#   NODE (node), JOBS (CPU count).
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
OUT="${1:-$REPO/out/wat-probe}"
export DLL="${DOTCC_DLL:-$REPO/DotCC/bin/Release/net10.0/dotcc.dll}"
export WAT2WASM="${WAT2WASM:-wat2wasm}" NODE="${NODE:-node}" RUNJS="$HERE/wat-probe.js" OUT
[ -f "$DLL" ] || { echo "wat-probe.sh: no dotcc build at $DLL" >&2; exit 2; }
mkdir -p "$OUT/w"

one() {
  local dir=$1 name; name=$(basename "$dir")
  local w="$OUT/w/$name" std=()
  [ -f "$dir/std.txt" ] && std=("-std=$(tr -d '\r\n' < "$dir/std.txt")")
  local srcs=("$dir"/*.c)
  [ -e "${srcs[0]}" ] && [ -f "$dir/expected-stdout.txt" ] || return 0
  if ! dotnet "$DLL" --target=wat "${std[@]}" "${srcs[@]}" -o "$w.wat" > "$w.emit.log" 2>&1; then
    local why; why=$(grep -m1 -oE "does not (yet )?support: .*" "$w.emit.log" | sed 's/^does not yet support: //' | cut -c1-140)
    echo -e "$name\tEMIT\t${why:-$(tail -1 "$w.emit.log" | cut -c1-140)}"; return 0
  fi
  if ! "$WAT2WASM" "$w.wat" -o "$w.wasm" > "$w.asm.log" 2>&1; then
    echo -e "$name\tASM\t$(grep -m1 -E "error" "$w.asm.log" | sed 's/.*error: //' | cut -c1-140)"; return 0
  fi
  timeout 20 "$NODE" "$RUNJS" "$w.wasm" > "$w.out" 2> "$w.err"
  local rc=$?
  if grep -q "^unresolved import" "$w.err"; then echo -e "$name\tIMPORT\t$(head -1 "$w.err")"; return 0; fi
  if grep -q "^trap:" "$w.err" || [ $rc -eq 124 ]; then
    echo -e "$name\tTRAP\t$(grep -m1 "^trap:" "$w.err")$([ $rc -eq 124 ] && echo timeout)"; return 0
  fi
  if "$NODE" "$RUNJS" --same "$dir/expected-stdout.txt" "$w.out"; then echo -e "$name\tPASS\t"
  else echo -e "$name\tWRONG\texit $rc"; fi
}
export -f one

ls -d "$REPO"/DotCC.FunctionalTests/Fixtures/*/ | sed 's:/$::' \
  | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'one "$1"' _ {} | sort > "$OUT/results.tsv"
cut -f2 "$OUT/results.tsv" | sort | uniq -c | sort -rn
echo "--- refusals by reason"
awk -F'\t' '$2=="EMIT"{print $3}' "$OUT/results.tsv" | sed -E "s/'[^']*'/'…'/g" | sort | uniq -c | sort -rn | head -40
