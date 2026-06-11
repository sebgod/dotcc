#nullable enable

using Xunit;

// The unit suite is serialized because it races on PROCESS-GLOBAL Console state
// under xUnit's default per-collection parallelism:
//   - Console.Out / Console.Error / Console.In are redirected by the libc-I/O
//     tests (printf / putchar / perror capture) and several assert EXACT
//     captured content. They cannot be made test-local: Libc writes to the
//     real Console, so the test must swap the global to capture it.
//   - The compiler's -Wconversion / -pedantic diagnostics also write to the
//     global Console.Error — so a diagnostic from one test can land in another
//     test's capture buffer, and two redirecting classes can clobber each
//     other's SetOut/SetError.
// Run in parallel, two such tests intermittently corrupt each other — a
// load-dependent flake (fires under CI/build load, where scheduling widens the
// window, far more than in an idle local run). Serializing the assembly removes
// the race class entirely; it costs only ~45s (the unit tests are individually
// fast). The functional suite is a separate assembly and keeps its own behavior.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
