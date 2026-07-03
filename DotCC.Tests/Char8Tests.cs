#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C23 <c>char8_t</c> (<c>&lt;uchar.h&gt;</c>) and its <c>u8"…"</c> /
/// <c>u8'x'</c> literals. dotcc lowers <c>char8_t</c> to C# <c>byte</c> (like its
/// <c>char</c>), and — since dotcc's plain narrow strings are ALREADY UTF-8 — a
/// <c>u8"…"</c> literal rides the exact same byte string path (<c>Libc.L(…u8)</c>)
/// with no new machinery. Pins cover the type, both literal forms, array
/// initializers, and the high-byte (raw UTF-8) path.
/// </summary>
[Collection("Char8")]
public sealed class Char8Tests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-c8-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void char8_t_local_lowers_to_csharp_byte()
    {
        var src = WriteTemp("""
            int main(void) {
                char8_t myc8 = 0x41;
                return (int)myc8;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte myc8");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void char8_t_pointer_lowers_to_byte_pointer()
    {
        var src = WriteTemp("""
            int first(char8_t *p) { return (int)p[0]; }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte* p");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void u8_char_constant_is_char8_typed_value()
    {
        // u8'A' == 0x41 == 65. Emitted as a (byte) cast of an INT literal — the inner
        // literal must stay int, because a byte-typed literal would render `65u`
        // (uint) and C# has no implicit uint-constant→byte conversion (CS0266).
        var src = WriteTemp("""
            int main(void) {
                char8_t c = u8'A';
                return (int)c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte c = (byte)65");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sizeof_char8_t_is_one_byte()
    {
        var src = WriteTemp("""
            int main(void) { return (int)sizeof(char8_t); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("sizeof(byte)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void include_uchar_header_resolves_and_char8_works()
    {
        var src = WriteTemp("""
            #include <uchar.h>
            int main(void) {
                char8_t c = 0x42;
                return (int)c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte c =");
        }
        finally { File.Delete(src); }
    }

    // ---- u8"…" string literals (reuse the UTF-8 byte path) ----------------

    [Fact]
    public void u8_string_literal_reuses_the_byte_L_path()
    {
        var src = WriteTemp("""
            int main(void) {
                const char8_t *p = u8"hi";
                return (int)p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Same zero-copy UTF-8 RVA pointer as a plain narrow string, to a byte*.
            emitted.ShouldContain("Libc.L(\"hi\\0\"u8)");
            emitted.ShouldContain("byte* p =");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void u8_string_keeps_raw_utf8_bytes()
    {
        // u8"\xC3\xA9" is the two raw UTF-8 bytes of U+00E9; high bytes (>0x7F) can't
        // ride a C# u8 literal, so the string lowering routes to the byte-array RVA.
        var src = WriteTemp("""
            int main(void) {
                const char8_t *p = u8"\xC3\xA9";
                return (int)p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L(new byte[]{ 0xC3, 0xA9, 0 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void adjacent_u8_strings_concatenate()
    {
        var src = WriteTemp("""
            int main(void) {
                const char8_t *p = u8"ab" u8"cd";
                return (int)p[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L(\"abcd\\0\"u8)");
        }
        finally { File.Delete(src); }
    }

    // ---- char8_t s[] = u8"…" array initializers ---------------------------

    [Fact]
    public void local_char8_array_from_u8_string_is_a_byte_stackalloc()
    {
        // A mutable local array — a stack copy of the UTF-8 bytes + NUL. h=104, i=105.
        var src = WriteTemp("""
            int main(void) {
                char8_t buf[] = u8"hi";
                return (int)buf[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte* buf = stackalloc byte[]{ 104, 105, 0 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void global_char8_array_uses_pinned_GlobalArrayFrom_byte()
    {
        var src = WriteTemp("""
            char8_t g[] = u8"hi";
            int main(void) { return (int)g[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.GlobalArrayFrom<byte>(new byte[]{ 104, 105, 0 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_global_char8_array_takes_the_rva_path()
    {
        // const → the zero-copy byte RVA path (Libc.L over .rodata), like any const
        // byte array.
        var src = WriteTemp("""
            const char8_t g[] = u8"hi";
            int main(void) { return (int)g[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Libc.L(new byte[]{ 104, 105, 0 })");
        }
        finally { File.Delete(src); }
    }
}
