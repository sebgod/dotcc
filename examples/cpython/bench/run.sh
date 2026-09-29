#!/usr/bin/env bash
# Benchmark the dotcc-built CPython with BenchmarkDotNet: bench/run.sh [BDN args],
# for example `bench/run.sh --filter '*Float*'`. Jobs: the JIT build
# (build.sh's build/python) and, when it exists, a ReadyToRun build published
# into build/python-r2r (bench/run.sh --r2r publishes it first, about a minute).
# Results land in bench/BenchmarkDotNet.Artifacts/results.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && (pwd -W 2>/dev/null || pwd))"
OUT="${BUILD_OUT:-$HERE/../build}"
JIT="$OUT/python/bin/Release/net10.0/python.dll"
R2R="$OUT/python-r2r/python.dll"
[ -f "$JIT" ] || { echo "bench/run.sh: no interpreter at $JIT (run build.sh)" >&2; exit 2; }

if [ "${1:-}" = "--r2r" ]; then
  shift
  dotnet publish "$OUT/python" -c Release --use-current-runtime --self-contained false \
    -p:PublishReadyToRun=true -o "$OUT/python-r2r" --nologo -v quiet
fi

export PYBENCH_HOME="$OUT/home" PYBENCH_JIT_DLL="$JIT"
if [ -f "$R2R" ] && [ "$R2R" -nt "$JIT" ]; then export PYBENCH_R2R_DLL="$R2R"; fi
cd "$HERE"
[ $# -gt 0 ] || set -- --filter '*'
dotnet run -c Release -- "$@"
