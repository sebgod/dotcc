#nullable enable

using System;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in native shared-library round-trip oracle. Compiles a C library with
/// dotcc's <c>-shared</c> mode, NativeAOT-publishes it to a real <c>.so</c>, then
/// builds + runs a hand-written C consumer that links the native library and calls
/// the exported cdecl functions — the end-to-end proof that the
/// <c>[UnmanagedCallersOnly]</c> export ABI actually works from C, which
/// <see cref="LibraryModeTests"/> (managed-metadata only) can't give.
/// </summary>
/// <remarks>
/// Opt-in (<c>DOTCC_RUN_SHARED_LIB_ORACLE=1</c>) like the MSVC/gcc oracles: the
/// NativeAOT publish is slow and needs a native toolchain. Skips with a hint when
/// the flag is unset, and skips (not fails) when the toolchain
/// (<c>dotnet</c>/<c>gcc</c>/<c>clang</c>) isn't reachable. Runs in CI on the
/// dedicated <c>shared-lib-oracle</c> ubuntu job.
/// </remarks>
public sealed class SharedLibOracleTests
{
    private const string RunEnv = "DOTCC_RUN_SHARED_LIB_ORACLE";

    private static bool RunRequested => Environment.GetEnvironmentVariable(RunEnv) == "1";

    // The library under test: two plain exports, one double-typed export, an
    // internal (`static`) helper that must NOT be exported but IS callable from
    // an exported function. Mirrors examples/smoke-lib/math.c.
    private const string LibSource = """
        int add(int a, int b) { return a + b; }
        double scale(double v, double k) { return v * k; }
        static int helper(int x) { return x * 2; }
        int double_it(int x) { return helper(x); }
        """;

    // A real C program that links the published native library and calls the
    // exports through the plain C ABI (cdecl), printing deterministic results.
    private const string ConsumerSource = """
        #include <stdio.h>
        extern int add(int, int);
        extern double scale(double, double);
        extern int double_it(int);
        int main(void) {
            printf("add(2,3)=%d\n", add(2, 3));
            printf("double_it(21)=%d\n", double_it(21));
            printf("scale(2.5,4)=%g\n", scale(2.5, 4.0));
            return 0;
        }
        """;

    [Fact]
    public void native_shared_lib_is_callable_from_a_real_c_program()
    {
        if (!RunRequested)
        {
            Assert.Skip(
                $"shared-lib oracle is opt-in. Set {RunEnv}=1 to compile a dotcc -shared " +
                $"library, NativeAOT-publish it, and call its cdecl exports from a real C " +
                $"program. LibraryModeTests already checks the managed export metadata.");
        }
        if (!SharedLibOracle.IsAvailable)
        {
            Assert.Skip("shared-lib oracle unavailable: " + SharedLibOracle.Unavailable);
        }

        var stdout = SharedLibOracle.PublishAndConsume(LibSource, ConsumerSource)
            .Replace("\r\n", "\n").Trim();

        stdout.ShouldBe("add(2,3)=5\ndouble_it(21)=42\nscale(2.5,4)=10");
    }
}
