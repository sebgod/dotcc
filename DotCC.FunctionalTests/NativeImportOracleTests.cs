#nullable enable

using System;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in native IMPORT-mode round-trip oracle (the implicit-linking twin of
/// <see cref="SharedLibOracleTests"/>). A dotcc program declares plain prototypes and
/// calls them — no <c>dlopen</c> — and is compiled with <c>-l</c>/<c>-L</c>; dotcc's
/// <c>DotCcImports</c> GOT table binds those prototypes to a real native library's
/// exports at startup. Proves the binding calls native code through the C ABI, against
/// both a gcc-built <c>.so</c> and a dotcc-built (NativeAOT) <c>.so</c>.
/// </summary>
/// <remarks>
/// Opt-in via <c>DOTCC_RUN_SHARED_LIB_ORACLE=1</c> (shares the toolchain gate + runner
/// with the shared-lib oracle). Skips with a hint when unset, and skips (not fails) when
/// the toolchain isn't reachable. Runs in CI on the <c>shared-lib-oracle</c> ubuntu job.
/// </remarks>
public sealed class NativeImportOracleTests
{
    private const string RunEnv = "DOTCC_RUN_SHARED_LIB_ORACLE";

    private static bool RunRequested => Environment.GetEnvironmentVariable(RunEnv) == "1";

    // Same library body as the shared-lib oracle: two plain exports, a double-typed
    // export, and a static helper that must NOT be exported but IS called internally.
    private const string LibSource = """
        int add(int a, int b) { return a + b; }
        double scale(double v, double k) { return v * k; }
        static int helper(int x) { return x * 2; }
        int double_it(int x) { return helper(x); }
        """;

    // The import-mode consumer: plain prototypes (NOT from a system header → import
    // candidates) + a main that calls them. No dlopen — dotcc binds add/double_it/scale
    // to the linked library via the DotCcImports table. printf stays runtime-provided
    // (declared in the synthetic <stdio.h>, so it lexes in the line band).
    private const string ImportConsumerSource = """
        #include <stdio.h>
        int add(int, int);
        double scale(double, double);
        int double_it(int);
        int main(void) {
            printf("add(2,3)=%d\n", add(2, 3));
            printf("double_it(21)=%d\n", double_it(21));
            printf("scale(2.5,4)=%g\n", scale(2.5, 4.0));
            return 0;
        }
        """;

    private const string ExpectedOutput = "add(2,3)=5\ndouble_it(21)=42\nscale(2.5,4)=10";

    [Fact]
    public void gcc_built_shared_lib_is_callable_via_import_mode()
    {
        if (!RunRequested)
        {
            Assert.Skip(
                $"native-import oracle is opt-in. Set {RunEnv}=1 to gcc-build a shared library and " +
                $"call its exports from a dotcc program compiled with -l/-L (import mode, no dlopen).");
        }
        if (!SharedLibOracle.IsAvailable)
        {
            Assert.Skip("shared-lib/import oracle unavailable: " + SharedLibOracle.Unavailable);
        }

        var stdout = SharedLibOracle.GccLibImportRoundTrip(LibSource, "mylib", ImportConsumerSource)
            .Replace("\r\n", "\n").Trim();

        stdout.ShouldBe(ExpectedOutput);
    }

    [Fact]
    public void dotcc_built_shared_lib_is_callable_via_import_mode()
    {
        if (!RunRequested)
        {
            Assert.Skip(
                $"native-import oracle is opt-in. Set {RunEnv}=1 to NativeAOT-publish a dotcc -shared " +
                $"library and bind its exports from another dotcc program via -l (import mode, no dlopen).");
        }
        if (!SharedLibOracle.IsAvailable)
        {
            Assert.Skip("shared-lib/import oracle unavailable: " + SharedLibOracle.Unavailable);
        }

        var stdout = SharedLibOracle.DotccLibImportRoundTrip(LibSource, ImportConsumerSource)
            .Replace("\r\n", "\n").Trim();

        // Byte-identical to the gcc-lib leg and the shared-lib oracle's outputs: the GOT
        // binding meets the .so's [UnmanagedCallersOnly] cdecl exports either way.
        stdout.ShouldBe(ExpectedOutput);
    }
}
