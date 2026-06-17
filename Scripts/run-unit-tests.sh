#!/usr/bin/env bash
# Run the DotCC unit suite (DotCC.Tests) with a HANG FAIL-SAFE.
#
# `--blame-hang-timeout` bounds every single test: if one runs longer than the timeout — a
# deadlock, a runaway loop, a blocking call that never returns — the VSTest Blame collector
# writes a sequence file naming the culprit (the last test that started) and ABORTS the run,
# instead of a silent multi-minute stall. The suite runs serial (see DotCC.Tests/AssemblyInfo.cs),
# so no legitimate single test is slow; 300s catches a genuine hang with no false positives.
#
# Usage:
#   Scripts/run-unit-tests.sh                                  # whole suite
#   Scripts/run-unit-tests.sh --filter "FullyQualifiedName~ZigFrontendTests"
#   HANG_TIMEOUT=120s Scripts/run-unit-tests.sh                # tighter bound
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet test DotCC.Tests/DotCC.Tests.csproj -c Release \
  --blame-hang-timeout "${HANG_TIMEOUT:-300s}" \
  "$@"
