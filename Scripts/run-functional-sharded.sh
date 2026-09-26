#!/usr/bin/env bash
# Run the DotCC functional suite (DotCC.FunctionalTests) across several test HOSTS at once.
#
# xunit runs the rows of one theory serially inside one class, so the zig oracle's few hundred
# programs are a single-threaded tail: a plain `dotnet test` of this suite keeps about 2 cores busy.
# This script launches N copies of the xUnit v3 test executable directly (no MSBuild: `--no-build`
# semantics, build first), each with DOTCC_TEST_SHARD=i/N (see DotCC.FunctionalTests/TestShard.cs):
# every data-driven theory runs its i-th slice of rows in host i, and the other test methods are
# dealt round-robin across the hosts. Every test runs exactly once across the N hosts.
#
# Oracle env vars (DOTCC_RUN_ZIG_ORACLE, DOTCC_ZIG_LIB_DIR, PATH to zig, ...) pass through unchanged.
#
# Usage:
#   dotnet build -c Release                             # build first; this script never builds
#   Scripts/run-functional-sharded.sh                   # 8 hosts
#   SHARDS=12 Scripts/run-functional-sharded.sh
#   HOST_THREADS=4 Scripts/run-functional-sharded.sh    # xunit threads per host (default 2)
#   OUT_DIR=/tmp/func Scripts/run-functional-sharded.sh # keep the per-host logs + xunit XML there
#
# Exit status: 0 when every host reports no failures and no errors, 1 otherwise.
set -uo pipefail
cd "$(dirname "$0")/.."

BIN=DotCC.FunctionalTests/bin/Release/net10.0
EXE="$BIN/DotCC.FunctionalTests"
[ -x "$EXE.exe" ] && EXE="$EXE.exe"
if [ ! -x "$EXE" ]; then
  echo "run-functional-sharded: $EXE not found; build first (dotnet build -c Release)" >&2
  exit 1
fi

# 8 hosts x 2 xunit threads keeps the box (16 logical cores) busy without oversubscribing it: each
# host would otherwise start one collection thread per core, and a zig build is itself multithreaded.
N=${SHARDS:-8}
THREADS=${HOST_THREADS:-2}
OUT=${OUT_DIR:-$(mktemp -d)}
mkdir -p "$OUT"

# The data-driven theories: sharded by ROW inside every host (TestShard.Rows). Each needs at least N rows
# (TestShard refuses fewer, since a host would get none); the 4-row Dotcc_matches_zig_mixed is left whole.
SHARDED=(
  DotCC.FunctionalTests.FixtureTests.Fixture_emits_csharp_runnable_with_matching_stdout
  DotCC.FunctionalTests.ZigOracleTests.Dotcc_matches_zig
  DotCC.FunctionalTests.ZigOracleTests.Dotcc_matches_zig_multifile
  DotCC.FunctionalTests.GccWslOracleTests.Dotcc_matches_gcc_output
  DotCC.FunctionalTests.MsvcOracleTests.Dotcc_matches_msvc_output
)

# Every other test method, dealt round-robin across the hosts.
mapfile -t ALL < <("$EXE" -list methods | tr -d '\r' | grep '^DotCC\.')
OTHERS=()
for m in "${ALL[@]}"; do
  skip=0
  for s in "${SHARDED[@]}"; do [ "$m" = "$s" ] && skip=1 && break; done
  [ $skip -eq 0 ] && OTHERS+=("$m")
done

echo "run-functional-sharded: $N hosts x $THREADS threads, ${#SHARDED[@]} row-sharded theories, ${#OTHERS[@]} other methods; logs in $OUT"
start=$(date +%s)
pids=()
for (( i = 0; i < N; i++ )); do
  args=(-noLogo -noColor -maxThreads "$THREADS")
  for m in "${SHARDED[@]}"; do args+=(-method "$m"); done
  for (( j = i; j < ${#OTHERS[@]}; j += N )); do args+=(-method "${OTHERS[$j]}"); done
  DOTCC_TEST_SHARD="$i/$N" "$EXE" "${args[@]}" -xml "$OUT/shard$i.xml" > "$OUT/shard$i.log" 2>&1 &
  pids+=($!)
done

status=0
for (( i = 0; i < N; i++ )); do
  wait "${pids[$i]}" || status=1
done

total=0; failed=0; errors=0; skipped=0
for (( i = 0; i < N; i++ )); do
  line=$(grep -E 'Total: [0-9]+, Errors: [0-9]+, Failed: [0-9]+, Skipped: [0-9]+' "$OUT/shard$i.log" | tail -1)
  if [ -z "$line" ]; then
    echo "  shard $i: NO SUMMARY (host crashed?) see $OUT/shard$i.log"
    status=1
    continue
  fi
  t=$(sed -E 's/.*Total: ([0-9]+).*/\1/' <<<"$line")
  e=$(sed -E 's/.*Errors: ([0-9]+).*/\1/' <<<"$line")
  f=$(sed -E 's/.*Failed: ([0-9]+).*/\1/' <<<"$line")
  s=$(sed -E 's/.*Skipped: ([0-9]+).*/\1/' <<<"$line")
  total=$((total + t)); errors=$((errors + e)); failed=$((failed + f)); skipped=$((skipped + s))
  if [ "$f" -gt 0 ] || [ "$e" -gt 0 ]; then
    echo "  shard $i: $f failed, $e errors; see $OUT/shard$i.log"
    grep -E '^\s*\[FAIL\]' "$OUT/shard$i.log" | sed 's/^/    /'
  fi
done
[ $failed -gt 0 ] || [ $errors -gt 0 ] && status=1

echo "run-functional-sharded: Total: $total, Passed: $((total - failed - skipped)), Failed: $failed, Errors: $errors, Skipped: $skipped, Duration: $(( $(date +%s) - start ))s"
exit $status
