#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>offsetof(Type, member)</c> builtin (C89). dotcc generates a
/// per-(type, member) helper using a real stack <c>default</c> instance and address
/// subtraction — C#'s <c>&amp;((T*)0)->m</c> trick faults. A `fixed`-buffer member
/// (which decays to a pointer) uses <c>(byte*)t.field</c>; a regular field /
/// [InlineArray] wrapper uses <c>&amp;t.field</c>. End-to-end in the
/// <c>offsetof/</c> fixture.
/// </summary>
public sealed class OffsetofTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-of-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void regular_field_uses_address_of()
    {
        var src = WriteTemp("""
            struct S { int a; int b; };
            int main(void) { return (int)offsetof(struct S, b); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("__Offsets.S__b()");                 // the call
            emitted.ShouldContain("S __t = default; return (ulong)((byte*)&__t.b - (byte*)&__t)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void fixed_buffer_member_decays_without_address_of()
    {
        var src = WriteTemp("""
            struct S { int a; int grid[2]; };
            int main(void) { return (int)offsetof(struct S, grid); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // a fixed buffer decays to a pointer → `(byte*)__t.grid`, NOT `&__t.grid`.
            emitted.ShouldContain("return (ulong)((byte*)__t.grid - (byte*)&__t)");
            emitted.ShouldNotContain("(byte*)&__t.grid");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void inline_array_member_uses_address_of()
    {
        // A non-primitive (pointer) array member is an [InlineArray] wrapper struct,
        // which does NOT decay — so `&__t.field` is its address.
        var src = WriteTemp("""
            struct S { int a; char *names[2]; };
            int main(void) { return (int)offsetof(struct S, names); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(byte*)&__t.names - (byte*)&__t");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void duplicate_offsetof_emits_one_helper()
    {
        var src = WriteTemp("""
            struct S { int a; int b; };
            int main(void) { return (int)offsetof(struct S, b) + (int)offsetof(struct S, b); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // dedup: the helper method body appears once.
            var idx = emitted.IndexOf("ulong S__b()", System.StringComparison.Ordinal);
            idx.ShouldBeGreaterThan(-1);
            emitted.IndexOf("ulong S__b()", idx + 1, System.StringComparison.Ordinal).ShouldBe(-1);
        }
        finally { File.Delete(src); }
    }
}
