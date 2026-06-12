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

    // The dotcc-side consumer: a C program dotcc itself compiles (to a managed
    // exe), which dlopens the published native .so (argv[1]) and resolves the same
    // three exports via dlsym, casting each dlsym() result DIRECTLY to its function
    // type — dotcc lowers those to `delegate* unmanaged[Cdecl]` so the calls meet
    // the .so's `[UnmanagedCallersOnly]` cdecl exports. Output is byte-identical to
    // the gcc consumer's, which is the round-trip proof.
    //
    // No dlclose here: this consumer is a *managed* (CoreCLR) program and the .so is
    // a NativeAOT library carrying its OWN runtime. Unloading a NativeAOT runtime out
    // from under a live managed host crashes at teardown — a CoreCLR+NativeAOT
    // coexistence limit, NOT a dlfcn bug. dlclose IS exercised elsewhere: the gcc
    // consumer dlcloses the same .so from pure-native code, and LibcDlfcnTests
    // dlcloses system libraries (libc.so.6 / msvcrt.dll) cleanly. A real plugin host
    // commonly keeps libraries loaded for the process lifetime anyway.
    private const string DotccConsumerSource = """
        #include <stdio.h>
        #include <dlfcn.h>
        int main(int argc, char **argv) {
            if (argc < 2) { printf("usage: consumer <lib>\n"); return 2; }
            void *h = dlopen(argv[1], RTLD_NOW);
            if (h == NULL) { printf("dlopen failed: %s\n", dlerror()); return 1; }

            int    (*add)(int, int)         = (int    (*)(int, int))      dlsym(h, "add");
            int    (*double_it)(int)        = (int    (*)(int))           dlsym(h, "double_it");
            double (*scale)(double, double) = (double (*)(double, double))dlsym(h, "scale");
            if (add == NULL || double_it == NULL || scale == NULL) {
                printf("dlsym failed: %s\n", dlerror());
                return 1;
            }

            printf("add(2,3)=%d\n", add(2, 3));
            printf("double_it(21)=%d\n", double_it(21));
            printf("scale(2.5,4)=%g\n", scale(2.5, 4.0));
            fflush(stdout);
            return 0;
        }
        """;

    private const string ExpectedOutput = "add(2,3)=5\ndouble_it(21)=42\nscale(2.5,4)=10";

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

        stdout.ShouldBe(ExpectedOutput);
    }

    [Fact]
    public void native_shared_lib_is_callable_from_a_dotcc_program_via_dlopen()
    {
        if (!RunRequested)
        {
            Assert.Skip(
                $"shared-lib oracle is opt-in. Set {RunEnv}=1 to compile a dotcc -shared " +
                $"library, NativeAOT-publish it, then have a dotcc-compiled program dlopen it " +
                $"and call its cdecl exports through dlsym — the dotcc-consumes-dotcc round-trip.");
        }
        if (!SharedLibOracle.IsAvailable)
        {
            Assert.Skip("shared-lib oracle unavailable: " + SharedLibOracle.Unavailable);
        }

        var stdout = SharedLibOracle.PublishAndConsumeViaDotcc(LibSource, DotccConsumerSource)
            .Replace("\r\n", "\n").Trim();

        // Identical to the gcc consumer's output: dotcc's dlsym'd unmanaged[Cdecl]
        // call sites meet the .so's UnmanagedCallersOnly cdecl exports.
        stdout.ShouldBe(ExpectedOutput);
    }
}
