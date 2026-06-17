#nullable enable

using Xunit;

// ===== Test parallelization scheme (see AssemblyInfo.cs) =====
//
// Every test class names a [Collection]. xUnit serializes tests WITHIN a collection and
// runs DIFFERENT collections in PARALLEL. We use that to parallelize the pure bulk while
// keeping the few process-global / blocking classes safe — categorized by hazard:
//
//   * "Console" (DisableParallelization → runs in ISOLATION): classes that REDIRECT the
//     process-global Console.Out/Error to capture output. The compiler also writes
//     diagnostics to the global Console.Error, so a parallel test could leak a
//     `dotcc: warning:` line into a live capture buffer. Isolation removes that race.
//
//   * "Runtime" (DisableParallelization → runs in ISOLATION): libc-runtime tests that
//     BLOCK (sockets), spawn threads (atomics / entry-stack), or mutate other
//     process-global state (locale / signals / cwd / debug-heap flag). These MUST run
//     isolated: under parallel scheduling the blocking socket/thread tests starve the
//     thread pool (their helper thread never gets scheduled while the parallel pure
//     collections saturate it) and DEADLOCK. Running alone, they get the full pool — no
//     starvation. (This is why the first parallel attempt hung for 42 min: "Runtime" was a
//     plain collection overlapping the parallel pure phase.)
//
//   * pure classes — each gets its OWN uniquely-named [Collection] so they run fully in
//     parallel. They only call Compiler.EmitCSharp/EmitWat/Preprocess and assert on the
//     returned string: pure CPU, no blocking, no threads, no process-global state.
//
// CONVENTION FOR NEW TESTS: redirects Console → "Console"; calls the libc runtime / blocks /
// threads / signals / env / locale / cwd / debug-heap → "Runtime"; a pure emit/parse test →
// its own `[Collection("<ClassNameWithoutTests>")]`.

/// <summary>Serial, isolated collection for tests that capture the process-global
/// <see cref="System.Console"/> — nothing runs concurrently, so no parallel test's
/// diagnostic write corrupts a capture buffer.</summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection { }

/// <summary>Serial, isolated collection for blocking / threaded / process-global libc-runtime
/// tests. Isolation gives the blocking socket + thread tests the full thread pool, so their
/// helper threads can't be starved into a deadlock by parallel collections.</summary>
[CollectionDefinition("Runtime", DisableParallelization = true)]
public sealed class RuntimeCollection { }
