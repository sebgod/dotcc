#!/usr/bin/env bash
# Run each programs/<name>.py on the interpreter build.sh built and compare its
# output with programs/<name>.expected, which host CPython 3.13 prints. One line
# per program with its time; exit 1 when any program fails or differs.
#
#   programs.sh              every program
#   programs.sh nbody lisp   just these
#
# Environment: PROGRAM_TIMEOUT (seconds per program, default 600),
# HOST_PYTHON (a host CPython 3.13 to time each program on as well) and RUN (the
# script that runs the interpreter, default run.sh; run-wasm.sh runs python.wasm).
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE/programs"
if [ $# -gt 0 ]; then names=("$@"); else names=(); for f in *.py; do names+=("${f%.py}"); done; fi

now() { date +%s.%N; }
elapsed() { awk -v a="$1" -v b="$2" 'BEGIN { printf "%.1f", b - a }'; }

out="$(mktemp)"
trap 'rm -f "$out"' EXIT
failed=0
for name in "${names[@]}"; do
  start=$(now)
  timeout "${PROGRAM_TIMEOUT:-600}" bash "$HERE/${RUN:-run.sh}" "$name.py" > "$out" 2>&1
  rc=$?
  took=$(elapsed "$start" "$(now)")
  if [ "$rc" -eq 0 ] && tr -d '\r' < "$out" | cmp -s - "$name.expected"; then status=ok; else status=FAIL; failed=1; fi
  host=""
  if [ -n "${HOST_PYTHON:-}" ]; then
    start=$(now)
    "$HOST_PYTHON" "$name.py" > /dev/null 2>&1
    host="   host $(elapsed "$start" "$(now)")s"
  fi
  printf '%-16s %-4s %6ss%s\n' "$name" "$status" "$took" "$host"
  if [ "$status" = FAIL ]; then
    echo "  exit $rc; expected (<) vs got (>):"
    tr -d '\r' < "$out" | diff "$name.expected" - | head -20 | sed 's/^/  /'
  fi
done
exit "$failed"
