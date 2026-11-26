#!/usr/bin/env bash
# Goose's samples through dotcc: each one compiled by Goose to its standalone C
# (`goose -o x.c --standalone`), that C compiled by dotcc to .NET
# (--emit=build), and the program's output diffed against a reference built
# without dotcc: `goose run` (Goose compiling the same C in process with
# TinyCC), or gcc for the thread samples, which `goose run` cannot run. The
# graphics samples (27 to 31) need a window and are left out.
#
# Usage:  bash fetch.sh && bash run.sh            # every sample
#         bash run.sh 05_shapes 07_sieve          # some
#         JOBS=4 bash run.sh                      # four at a time
# Needs dotcc built first (dotnet build DotCC -c Release). Exit status 0 when
# every sample matched, 1 otherwise; a thread sample with no gcc on PATH is a
# SKIP, not a failure.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
BUILD="$HERE/build"
DOTCC="${DOTCC:-$ROOT/DotCC/bin/Release/net10.0/dotcc.dll}"
GOOSE_DIR="${GOOSE_DIR:-$(cat "$BUILD/goose-dir" 2>/dev/null)}"
if [ -z "$GOOSE_DIR" ] || [ ! -d "$GOOSE_DIR/samples" ]; then
  echo "run.sh: no Goose release; run fetch.sh first" >&2
  exit 1
fi
GOOSE="$GOOSE_DIR/goose"
[ -x "$GOOSE.exe" ] && GOOSE="$GOOSE.exe"
SAMPLES="$GOOSE_DIR/samples"
OUT="$BUILD/out"
mkdir -p "$OUT"
# Goose keeps its toolchain cache and config under GOOSE_HOME; keep them here.
export GOOSE_HOME="$BUILD/goose-home"

THREADS="23_mandelbrot_threads 32_pool_worker_snapshots"

# One sample, start to end; prints one result line.
one() {
  local n="$1" include=() stdin=/dev/null ref
  [ "$n" = 24_call_c ] && include=(--include call_c.h)
  [ "$n" = 17_calc ] && stdin="$SAMPLES/data/calc.stdin"
  if ! (cd "$SAMPLES" && "$GOOSE" -o "$OUT/$n.c" --standalone "${include[@]}" "$n.goose") > "$OUT/$n.goose.log" 2>&1; then
    echo "$n FAIL goose could not compile it to C"; return 1
  fi
  if [[ " $THREADS " == *" $n "* ]]; then
    if ! command -v gcc > /dev/null; then echo "$n SKIP (no gcc for the reference)"; return 0; fi
    gcc -O1 -std=c11 -w -o "$OUT/$n.gcc" "$OUT/$n.c" -lm -lpthread > "$OUT/$n.gcc.log" 2>&1 \
      || { echo "$n FAIL gcc could not build the reference"; return 1; }
    ref=("$OUT/$n.gcc")
  else
    ref=("$GOOSE" run "${include[@]}" "$n.goose")
  fi
  (cd "$SAMPLES" && timeout 300 "${ref[@]}" < "$stdin" > "$OUT/$n.expected.txt" 2> "$OUT/$n.expected.err") \
    || { echo "$n FAIL the reference run failed"; return 1; }
  rm -rf "${OUT:?}/build_$n"
  if ! dotnet "$DOTCC" --emit=build -I "$SAMPLES" "$OUT/$n.c" -o "$OUT/build_$n" > "$OUT/$n.dotcc.log" 2>&1; then
    echo "$n FAIL dotcc: $(grep -m1 -o 'error[^[]\{0,200\}' "$OUT/$n.dotcc.log")"; return 1
  fi
  (cd "$SAMPLES" && timeout 300 dotnet "$OUT/build_$n/bin/Release/net10.0/build_$n.dll" < "$stdin" \
    > "$OUT/$n.out.txt" 2> "$OUT/$n.err.txt")
  local rc=$?
  if diff <(tr -d '\r' < "$OUT/$n.expected.txt") <(tr -d '\r' < "$OUT/$n.out.txt") > "$OUT/$n.diff"; then
    echo "$n PASS"
  else
    echo "$n FAIL output differs (exit $rc, $(grep -c '^[<>]' "$OUT/$n.diff") lines; see $OUT/$n.diff)"; return 1
  fi
}

if [ "${1:-}" = --one ]; then one "$2"; exit $?; fi

if [ $# -gt 0 ]; then
  LIST=("$@")
else
  LIST=()
  for f in "$SAMPLES"/[01][0-9]_*.goose "$SAMPLES"/2[0-6]_*.goose "$SAMPLES"/32_*.goose; do
    n="$(basename "$f" .goose)"
    LIST+=("$n")
  done
fi

printf '%s\n' "${LIST[@]}" | xargs -P "${JOBS:-1}" -I{} bash "$0" --one {} | tee "$OUT/summary.txt"
total=$(wc -l < "$OUT/summary.txt")
pass=$(grep -c ' PASS$' "$OUT/summary.txt")
skip=$(grep -c ' SKIP ' "$OUT/summary.txt")
echo "goose samples: $pass passed, $skip skipped, of $total"
[ "$((pass + skip))" -eq "$total" ]
