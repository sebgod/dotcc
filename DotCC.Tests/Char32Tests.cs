#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C11 <c>char32_t</c> (<c>&lt;uchar.h&gt;</c>). dotcc lowers it to
/// C# <c>uint</c> — a 32-bit UTF-32 code unit (NOT <see cref="System.Text.Rune"/>;
/// a char32_t is any value) — so <c>char32_t*</c> arithmetic walks 4 bytes. Unlike
/// <c>char16_t</c> (→ C# <c>char</c>, which needed the coercion table taught about
/// <c>char</c>), <c>uint</c> is already a fully-wired C# integer, so no coercion
/// change was needed. Pins cover the type, the <c>U"…"</c> / <c>U'x'</c> literals
/// (pooled <c>Libc.L32</c>), array initializers, and the char32_t payoff: an astral
/// scalar is ONE code unit (emitted as its surrogate pair, folded back at runtime).
/// </summary>
[Collection("Char32")]
public sealed class Char32Tests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-c32-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void char32_t_local_lowers_to_csharp_uint()
    {
        var src = WriteTemp("""
            int main(void) {
                char32_t myc32 = 0x41;
                return (int)myc32;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("uint myc32");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void char32_t_pointer_lowers_to_uint_pointer()
    {
        var src = WriteTemp("""
            int first(char32_t *p) { return (int)p[0]; }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("uint* p");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void narrowing_int_store_into_char32_t_gets_a_uint_cast()
    {
        // C# has no implicit int→uint conversion for a non-constant value, so the
        // store needs an explicit (uint) cast (same width, sign change).
        var src = WriteTemp("""
            int main(void) {
                int i = 5;
                char32_t c;
                c = i;
                return (int)c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("c = (uint)(i)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void U_char_constant_is_char32_typed_value()
    {
        var src = WriteTemp("""
            int main(void) {
                char32_t c = U'Z';
                return (int)c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // U'Z' == 0x5A == 90, emitted as a (uint) cast of an int literal.
            emitted.ShouldContain("uint c = (uint)90");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sizeof_char32_t_is_four_bytes()
    {
        // char32_t → C# uint, so sizeof(char32_t) lowers to sizeof(uint) (= 4).
        var src = WriteTemp("""
            int main(void) { return (int)sizeof(char32_t); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("sizeof(uint)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void include_uchar_header_resolves_and_char32_works()
    {
        var src = WriteTemp("""
            #include <uchar.h>
            int main(void) {
                char32_t c = 0x42;
                return (int)c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("uint c =");
        }
        finally { File.Delete(src); }
    }

    // ---- U"…" string literals (pooled Libc.L32) ---------------------------

    [Fact]
    public void U_string_literal_lowers_to_pooled_L32_pointer()
    {
        var src = WriteTemp("""
            int main(void) {
                const char32_t *p = U"hi";
                return (int)p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Pooled, pinned UTF-32 pointer (NUL-terminated), assigned to a uint*.
            emitted.ShouldContain("Libc.L32(\"hi\\u0000\")");
            emitted.ShouldContain("uint* p =");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void U_string_astral_scalar_is_one_code_unit()
    {
        // The char32_t payoff: a scalar above the BMP (U+1F600) is ONE UTF-32 code
        // unit. The emitted C# literal carries it as its UTF-16 surrogate pair, and
        // Libc.L32 folds the pair back to one 32-bit code unit at runtime.
        var src = WriteTemp("""
            int main(void) {
                const char32_t *p = U"\x1F600";
                return (int)p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L32(\"\\uD83D\\uDE00\\u0000\")");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void adjacent_U_strings_concatenate()
    {
        var src = WriteTemp("""
            int main(void) {
                const char32_t *p = U"ab" U"cd";
                return (int)p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L32(\"abcd\\u0000\")");
        }
        finally { File.Delete(src); }
    }

    // ---- char32_t s[] = U"…" array initializers ---------------------------

    [Fact]
    public void local_char32_array_from_U_string_is_a_uint_stackalloc()
    {
        // A mutable local array — a stack copy of the UTF-32 code units + NUL. uint
        // is a fully-wired C# integer, so the fitting constants need no cast (unlike
        // char16_t's char elements). h=104, i=105.
        var src = WriteTemp("""
            int main(void) {
                char32_t buf[] = U"hi";
                return (int)buf[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("uint* buf = stackalloc uint[]{ 104, 105, 0 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sized_local_char32_array_zero_pads()
    {
        var src = WriteTemp("""
            int main(void) {
                char32_t buf[5] = U"hi";
                return (int)buf[4];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("stackalloc uint[]{ 104, 105, 0, 0, 0 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void global_char32_array_uses_pinned_GlobalArrayFrom_uint()
    {
        var src = WriteTemp("""
            char32_t g[] = U"hi";
            int main(void) { return (int)g[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.GlobalArrayFrom<uint>(new uint[]{ 104, 105, 0 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_global_char32_array_takes_the_rva_path()
    {
        // const → the zero-copy RVA path (Libc.L<uint> over .rodata), sound on
        // dotcc's little-endian target.
        var src = WriteTemp("""
            const char32_t g[] = U"hi";
            int main(void) { return (int)g[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L<uint>(new uint[]{ 104, 105, 0 })");
        }
        finally { File.Delete(src); }
    }
}
