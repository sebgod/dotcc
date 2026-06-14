#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <c>wchar_t</c> (<c>&lt;wchar.h&gt;</c>). dotcc commits to the
/// MSVC-shaped wchar_t — an unsigned 16-bit UTF-16 code unit — so it lowers to C#
/// <c>char</c> exactly like <see cref="Char16Tests">char16_t</see>: <c>wchar_t*</c>
/// walks 2 bytes, and the <c>L"…"</c> / <c>L'x'</c> literals lower identically to
/// <c>u"…"</c> / <c>u'x'</c> (pooled <c>Libc.L16</c>, explicit <c>(char)</c> casts).
/// These mirror <see cref="Char16Tests"/> with the <c>L</c>-prefixed forms.
/// </summary>
public sealed class WcharTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-wc-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    // ---- the type --------------------------------------------------------

    [Fact]
    public void wchar_t_local_lowers_to_csharp_char()
    {
        var src = WriteTemp("""
            int main(void) {
                wchar_t mywc = 0x41;
                return mywc;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("char mywc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void wchar_t_pointer_lowers_to_char_pointer()
    {
        var src = WriteTemp("""
            int first(wchar_t *p) { return p[0]; }
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
    public void narrowing_int_store_into_wchar_t_gets_an_explicit_cast()
    {
        var src = WriteTemp("""
            int main(void) {
                int i = 5;
                wchar_t c;
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
    public void wchar_t_arithmetic_promotes_to_int_then_narrows_on_store()
    {
        var src = WriteTemp("""
            int main(void) {
                wchar_t c = 1;
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
    public void wchar_t_constant_init_gets_an_explicit_cast()
    {
        // Like char16_t: C# has NO implicit int-constant→char conversion, so even
        // a fitting constant needs an explicit (char) cast.
        var src = WriteTemp("""
            int main(void) {
                wchar_t c = 65;
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
    public void include_wchar_header_resolves_and_type_works()
    {
        var src = WriteTemp("""
            #include <wchar.h>
            int main(void) {
                wchar_t c = 0x42;
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

    // ---- L"…" / L'x' literals -------------------------------------------

    [Fact]
    public void L_string_literal_lowers_to_pooled_L16_pointer()
    {
        var src = WriteTemp("""
            int main(void) {
                const wchar_t *p = L"hi";
                return p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L16(\"hi\\u0000\")");
            emitted.ShouldContain("char* p =");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void L_char_constant_is_wchar_typed_value()
    {
        var src = WriteTemp("""
            int main(void) {
                wchar_t c = L'Z';
                return c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // L'Z' == 0x5A == 90, emitted as an explicit (char) cast of an int literal.
            emitted.ShouldContain("char c = unchecked((char)90)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void L_string_hex_escape_keeps_full_16_bits()
    {
        var src = WriteTemp("""
            int main(void) {
                const wchar_t *p = L"\x1234";
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
    public void adjacent_L_strings_concatenate()
    {
        var src = WriteTemp("""
            int main(void) {
                const wchar_t *p = L"ab" L"cd";
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

    // ---- wchar_t s[] = L"…" array initializers --------------------------

    [Fact]
    public void local_wchar_array_from_L_string_is_a_char_stackalloc()
    {
        var src = WriteTemp("""
            int main(void) {
                wchar_t buf[] = L"hi";
                return buf[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // h=104, i=105, NUL — each a (char)-cast int code unit, like char16_t.
            emitted.ShouldContain("char* buf = stackalloc char[]{ unchecked((char)(104)), unchecked((char)(105)), unchecked((char)(0)) }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sized_local_wchar_array_zero_pads()
    {
        var src = WriteTemp("""
            int main(void) {
                wchar_t buf[5] = L"hi";
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
    public void global_wchar_array_uses_pinned_GlobalArrayFrom_char()
    {
        var src = WriteTemp("""
            wchar_t g[] = L"hi";
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
    public void const_global_wchar_array_takes_the_rva_path()
    {
        var src = WriteTemp("""
            const wchar_t g[] = L"hi";
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
