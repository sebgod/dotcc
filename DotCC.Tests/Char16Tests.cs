#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C11 <c>char16_t</c> (<c>&lt;uchar.h&gt;</c>). dotcc lowers it to
/// C# <c>char</c> — a 16-bit UTF-16 code unit — so <c>char16_t*</c> arithmetic walks
/// 2 bytes and the type bridges to C# <c>string</c>/<c>Span&lt;char&gt;</c>. Phase 1
/// covers the type itself plus the coercion fix that teaches the backend C# <c>char</c>
/// is a 2-byte integer (without it, a narrowing store into a char16_t sink would be
/// silently emitted without its cast → CS0266). The <c>u"…"</c> / <c>u'…'</c> literals
/// are exercised in later phases.
/// </summary>
public sealed class Char16Tests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-c16-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void char16_t_local_lowers_to_csharp_char()
    {
        var src = WriteTemp("""
            int main(void) {
                char16_t myc16 = 0x41;
                return myc16;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("char myc16");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void char16_t_pointer_lowers_to_char_pointer()
    {
        var src = WriteTemp("""
            int first(char16_t *p) { return p[0]; }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("char* p");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void narrowing_int_store_into_char16_t_gets_an_explicit_cast()
    {
        // C# never implicitly converts a non-constant int to char; without the
        // coercion fix the cast would be dropped and Roslyn would reject it.
        var src = WriteTemp("""
            int main(void) {
                int i = 5;
                char16_t c;
                c = i;
                return c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("c = (char)(i)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void char16_t_arithmetic_promotes_to_int_then_narrows_on_store()
    {
        // c + 1 is int (integer promotion, like char); storing back to a char16_t
        // sink re-narrows with an explicit cast.
        var src = WriteTemp("""
            int main(void) {
                char16_t c = 1;
                c = c + 1;
                return c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("c = (char)(c + 1)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void char16_t_constant_init_gets_an_explicit_cast()
    {
        // Unlike byte/short/ushort, C# has NO implicit int-constant→char conversion
        // (§10.2.11 omits char), so even a fitting constant needs an explicit (char)
        // cast in the emitted C#.
        var src = WriteTemp("""
            int main(void) {
                char16_t c = 65;
                return c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("char c = unchecked((char)(65))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void include_uchar_header_resolves_and_type_works()
    {
        // <uchar.h> exists for the include path; char16_t is recognized either way.
        var src = WriteTemp("""
            #include <uchar.h>
            int main(void) {
                char16_t c = 0x42;
                return c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("char c =");
        }
        finally { File.Delete(src); }
    }

    // ---- u"…" / u'x' literals (Phase 2) -----------------------------------

    [Fact]
    public void u_string_literal_lowers_to_pooled_L16_pointer()
    {
        var src = WriteTemp("""
            int main(void) {
                const char16_t *p = u"hi";
                return p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Pooled, pinned UTF-16 pointer (NUL-terminated), assigned to a char*.
            emitted.ShouldContain("Libc.L16(\"hi\\u0000\")");
            emitted.ShouldContain("char* p =");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void u_char_constant_is_char16_typed_value()
    {
        var src = WriteTemp("""
            int main(void) {
                char16_t c = u'Z';
                return c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // u'Z' == 0x5A == 90, emitted as an explicit (char) cast of an int literal.
            emitted.ShouldContain("char c = unchecked((char)90)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void u_string_hex_escape_keeps_full_16_bits()
    {
        // \x1234 is a single UTF-16 code unit 0x1234 — the byte path would mask it
        // to 0x34; the char16_t path keeps all 16 bits.
        var src = WriteTemp("""
            int main(void) {
                const char16_t *p = u"\x1234";
                return p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L16(\"\\u1234\\u0000\")");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void adjacent_u_strings_concatenate()
    {
        var src = WriteTemp("""
            int main(void) {
                const char16_t *p = u"ab" u"cd";
                return p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L16(\"abcd\\u0000\")");
        }
        finally { File.Delete(src); }
    }

    // ---- char16_t s[] = u"…" array initializers (Phase 3) -----------------

    [Fact]
    public void local_char16_array_from_u_string_is_a_char_stackalloc()
    {
        // A mutable local array — a stack copy (UTF-16 code units + NUL), like
        // `char s[] = "…"` → stackalloc byte[]. h=104, i=105.
        var src = WriteTemp("""
            int main(void) {
                char16_t buf[] = u"hi";
                return buf[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // h=104, i=105, NUL. Each int code unit gets the explicit (char) cast C#
            // requires (no implicit int→char), wrapped unchecked like other narrowings.
            emitted.ShouldContain("char* buf = stackalloc char[]{ unchecked((char)(104)), unchecked((char)(105)), unchecked((char)(0)) }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sized_local_char16_array_zero_pads()
    {
        var src = WriteTemp("""
            int main(void) {
                char16_t buf[5] = u"hi";
                return buf[4];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("stackalloc char[]{ unchecked((char)(104)), unchecked((char)(105)), unchecked((char)(0)), unchecked((char)(0)), unchecked((char)(0)) }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void global_char16_array_uses_pinned_GlobalArrayFrom_char()
    {
        var src = WriteTemp("""
            char16_t g[] = u"hi";
            int main(void) { return g[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.GlobalArrayFrom<char>(new char[]{ unchecked((char)(104)), unchecked((char)(105)), unchecked((char)(0)) })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_global_char16_array_takes_the_rva_path()
    {
        // const → the zero-copy RVA path (Libc.L<char> over .rodata), same as a
        // const non-byte array; sound on dotcc's little-endian target.
        var src = WriteTemp("""
            const char16_t g[] = u"hi";
            int main(void) { return g[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L<char>(new char[]{ unchecked((char)(104)), unchecked((char)(105)), unchecked((char)(0)) })");
        }
        finally { File.Delete(src); }
    }
}
