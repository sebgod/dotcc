#nullable enable

using Xunit;

// Tests run SERIAL (assembly-wide). Two independent reasons require this on the current
// arm64 setup — both were confirmed empirically; do NOT re-enable parallelization without
// addressing BOTH (and without different hardware, see reason 2):
//
//  1. PROCESS-GLOBAL Console. The libc-I/O tests redirect Console.Out/Error to capture
//     output, and the compiler writes -Wconversion/-pedantic diagnostics to the global
//     Console.Error — so in parallel a diagnostic from one test lands in another's capture
//     buffer. (The Console-capturing classes carry [Collection("Console")] and could be
//     isolated via DisableParallelization — see CollectionDefinitions.cs.)
//  2. DEADLOCK / THRASH on this box. Enabling within-assembly parallelism was tried twice:
//     (a) with the blocking socket/thread tests ([Collection("Runtime")]) overlapping the
//     parallel pure phase, the suite DEADLOCKED (42 min+) — their helper threads starve in
//     the saturated thread pool; (b) with "Runtime" also isolated (DisableParallelization),
//     no deadlock, but the parallel pure phase THRASHED (worse than the ~17 min serial) from
//     CPU oversubscription. Net loss either way on this hardware.
//
// Every test class still names a [Collection] — categorization + filtering
// (`--filter Collection=Console`), and ready if a future host can parallelize. Under this
// assembly-wide serial setting those names don't drive parallelism. See CollectionDefinitions.cs.
// FAIL-SAFE: run via Scripts/run-unit-tests.sh, which passes `--blame-hang-timeout` so a hang
// (in any mode) is bounded and the culprit named, instead of a silent stall.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
