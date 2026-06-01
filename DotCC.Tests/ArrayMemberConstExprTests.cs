#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for phase 4k — a struct array-member bound that's an integer
/// CONSTANT EXPRESSION (a <c>sizeof</c>, an enum constant, or arithmetic over them)
/// is folded to the literal a C# <c>fixed[N]</c> / <c>[InlineArray(N)]</c> needs, and
/// a typedef element resolves to its underlying primitive for the fixed-buffer check.
/// End-to-end in the <c>array-member-constexpr/</c> fixture.
/// </summary>
public sealed class ArrayMemberConstExprTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-amc-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void sizeof_bound_folds_to_literal()
    {
        var src = WriteTemp("""
            struct S { char buf[sizeof(void *)]; };
            int main(void) { struct S s; s.buf[0] = 1; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("fixed byte buf[8]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void enum_constant_bound_folds()
    {
        var src = WriteTemp("""
            enum E { A, B, C, N };   /* A=0 … N=3 */
            struct S { int v[N]; };
            int main(void) { struct S s; s.v[2] = 1; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("fixed int v[3]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void arithmetic_over_constants_folds()
    {
        var src = WriteTemp("""
            enum E { BASE = 3 };
            struct S { long w[2 * BASE]; };
            int main(void) { struct S s; s.w[5] = 1; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("fixed long w[6]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void typedef_element_resolves_to_primitive_fixed_buffer()
    {
        // `byte_t` (→ unsigned char → byte) must take the `fixed byte` path, not the
        // [InlineArray] one (C# `fixed` needs a primitive keyword, not the alias).
        var src = WriteTemp("""
            typedef unsigned char byte_t;
            struct S { byte_t data[4]; };
            int main(void) { struct S s; s.data[0] = 1; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("fixed byte data[4]");
            emitted.ShouldNotContain("InlineArray");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_primitive_element_with_enum_bound_uses_inline_array()
    {
        // A pointer element still takes the [InlineArray] path, but the enum-constant
        // bound folds to the literal the attribute needs.
        var src = WriteTemp("""
            enum E { K = 3 };
            struct Node { int x; };
            struct S { struct Node *kids[K]; };
            int main(void) { struct S s; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("InlineArray(3)");
        }
        finally { File.Delete(src); }
    }
}
