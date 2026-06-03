#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// C's usual arithmetic conversions (§6.3.1.8). dotcc maps C's integer types onto
/// C# (<c>size_t</c>/<c>lua_Unsigned</c> → <c>ulong</c>, <c>lu_byte</c> →
/// <c>byte</c>, …). C# performs most of these implicitly but diverges twice: it
/// refuses to unify a 64-bit unsigned (<c>ulong</c>/<c>nuint</c>) with a signed
/// integer (CS0034), and it widens <c>uint op int</c> to <c>long</c> where C keeps
/// <c>unsigned int</c>. dotcc computes C's common type and, when it's unsigned,
/// casts the signed operand to it — then tags the result so the type propagates up
/// nested expressions / into comparisons / Cond.B / stores. End-to-end in the
/// <c>usual-arith-conv/</c> fixture (gcc-oracle-validated).
/// </summary>
public sealed class UsualArithConvTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-uac-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void ulong_times_int_casts_the_signed_operand()
    {
        var src = WriteTemp("""
            #include <stddef.h>
            size_t f(size_t n, int i) { return n * i; }
            int main(void) { return (int)f(2, 3); }
            """);
        try
        {
            // size_t -> ulong; the int operand takes the C conversion to ulong.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("(n * (ulong)(i))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void uint_times_int_yields_uint_not_long()
    {
        var src = WriteTemp("""
            unsigned f(unsigned u, int i) { return u * i; }
            int main(void) { return (int)f(2, 3); }
            """);
        try
        {
            // C: `unsigned * int` is `unsigned`; C# would widen to `long`. dotcc
            // casts the int to uint so the result is genuinely uint.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("(u * (uint)(i))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void ulong_divided_by_sizeof_reconciles()
    {
        // The Lua array-cap idiom `MAX_SIZET / sizeof(T)`. `sizeof` is size_t
        // (ulong) per C, so `lim / sizeof` is ulong/ulong — unsigned, no CS0034.
        var src = WriteTemp("""
            #include <stddef.h>
            struct Big { double a, b, c; };
            size_t cap(size_t lim) { return lim / sizeof(struct Big); }
            int main(void) { return (int)cap(100); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("/ (ulong)sizeof(Big)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void ulong_compared_with_int_reconciles()
    {
        var src = WriteTemp("""
            #include <stddef.h>
            int f(size_t n, int i) { return n > i; }
            int main(void) { return f(5, 3); }
            """);
        try
        {
            // The comparison is unsigned (C converts the int) — and typechecks.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("(n > (ulong)(i))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void same_signed_pair_is_untouched()
    {
        var src = WriteTemp("""
            long f(int a, long b) { return a + b; }
            int main(void) { return (int)f(1, 2); }
            """);
        try
        {
            // int + long is fine in C# (both signed) — no cast injected.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(a + b)");
            emitted.ShouldNotContain("(long)(a)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void shift_count_cast_to_int()
    {
        // C# requires the shift count to be int; a wide/unsigned count is cast down.
        var src = WriteTemp("""
            #include <stddef.h>
            long f(long v, size_t n) { return v << n; }
            int main(void) { return (int)f(1, 3); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("(v << (int)(n))");
        }
        finally { File.Delete(src); }
    }
}
