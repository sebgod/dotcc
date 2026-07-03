#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the C99 minimum-width (<c>int_leastN_t</c>) and
/// fastest-minimum-width (<c>int_fastN_t</c>) <c>&lt;stdint.h&gt;</c> integer
/// families. Header-only typedefs: the least types are the exact-width types,
/// and dotcc follows glibc's LP64 (x86-64 / aarch64-Linux) choices EXACTLY —
/// <c>int_fast16/32/64_t</c> all map to <c>long</c> (64-bit), NOT the narrow
/// width — so <c>sizeof</c> and the limit macros stay byte-identical to the gcc
/// oracle. End-to-end in the <c>c99-stdint-fast/</c> fixture (MSVC opts out —
/// it is LLP64 and maps the fast types to 32-bit int).
/// </summary>
[Collection("StdintFamilies")]
public sealed class StdintFamiliesTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-stdint-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Emit(string decls)
    {
        var src = WriteTemp("#include <stdint.h>\nint main(void) {\n" + decls + "\n    return 0;\n}\n");
        try { return Compiler.EmitCSharp(new[] { src }); }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Least_types_map_to_their_exact_width_primitives()
    {
        var e = Emit("""
                int_least8_t   l8probe = 0;
                uint_least16_t l16probe = 0;
                int_least32_t  l32probe = 0;
                int_least64_t  l64probe = 0;
            """);
        e.ShouldContain("sbyte l8probe");
        e.ShouldContain("ushort l16probe");
        e.ShouldContain("int l32probe");
        e.ShouldContain("long l64probe");
    }

    [Fact]
    public void Fast_types_follow_glibc_lp64_fast16_32_64_are_long()
    {
        // The load-bearing choice: fast16/32/64 are the 64-bit machine word,
        // not the narrow width — this is what keeps sizeof byte-identical to gcc.
        var e = Emit("""
                int_fast8_t   f8probe = 0;
                int_fast16_t  f16probe = 0;
                uint_fast32_t f32probe = 0;
                int_fast64_t  f64probe = 0;
            """);
        e.ShouldContain("sbyte f8probe");
        e.ShouldContain("long f16probe");   // NOT short
        e.ShouldContain("ulong f32probe");  // NOT uint
        e.ShouldContain("long f64probe");
    }

    [Fact]
    public void Fast_limit_macros_reflect_the_underlying_long_width()
    {
        // INT_FAST16_MAX is the 64-bit limit (its underlying type is `long`), so
        // it folds into an integer constant expression — here an array bound —
        // proving it's a usable ICE, not a runtime value.
        var e = Emit("    int arr_probe[INT_FAST16_MAX > 2000000000 ? 3 : 1];");
        e.ShouldContain("stackalloc int[3]");
    }
}
