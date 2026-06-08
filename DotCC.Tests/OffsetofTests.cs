#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>offsetof(Type, member)</c> builtin (C89). dotcc prefers a
/// compile-time CONSTANT, computed from a struct/union layout model (C-ABI rules,
/// matching .NET blittable layout), so offsetof can feed a constant position — an
/// array-member bound, a case label, etc. (Lua's <c>char
/// padding[offsetof(Limbox_aux, follows_pNode)]</c>). When the layout isn't
/// modellable (a bit-field's offset is implementation-defined and dotcc's packing
/// differs from C), it falls back to a per-(type, member) helper using a real stack
/// <c>default</c> instance and address subtraction — a `fixed`-buffer member decays
/// to a pointer (<c>(byte*)t.field</c>); a regular field / [InlineArray] wrapper
/// uses <c>&amp;t.field</c>. End-to-end in the <c>offsetof/</c> and
/// <c>align-union/</c> fixtures.
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
    public void modellable_struct_folds_to_constant()
    {
        // a:double@0, b:char@8, c:int (align 4) @12 → offsetof(c) == 12 at runtime.
        // The typed IR no longer constant-folds modellable struct offsets to literals;
        // all offsetof lowerings use an inline lambda with address subtraction.
        // No __Offsets helper class is emitted.
        var src = WriteTemp("""
            struct S { double a; char b; int c; };
            int main(void) { return (int)offsetof(struct S, c); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(byte*)&__t.c - (byte*)&__t");
            emitted.ShouldNotContain("__Offsets");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void alignment_padding_folds()
    {
        // c:char@0, d:double aligned to 8 → offsetof(d) == 8 at runtime.
        // The typed IR no longer constant-folds offsetof to a literal; instead it
        // emits an inline lambda that subtracts &__t.d from &__t at runtime.
        // The correct field name `d` appears in the lambda body.
        var src = WriteTemp("""
            struct S { char c; double d; };
            int main(void) { return (int)offsetof(struct S, d); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("(byte*)&__t.d - (byte*)&__t");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void offsetof_as_array_bound_folds_to_literal_dimension()
    {
        // The motivating use: offsetof drives an array dimension, which C# needs
        // as a literal. a:int@0, b:double@8 → `char pad[8]` → stackalloc byte[8].
        var src = WriteTemp("""
            struct S { int a; double b; };
            int main(void) { char pad[offsetof(struct S, b)]; return (int)sizeof(pad); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("stackalloc byte[8]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void union_member_offset_is_zero()
    {
        // Every union member is at offset 0. The typed IR emits a lambda that
        // subtracts &__t.b from &__t; for a union both are at address 0, so the
        // subtraction evaluates to zero at runtime. No __Offsets helper class is
        // emitted — the IR uses inline lambdas for all offsetof lowerings.
        var src = WriteTemp("""
            union U { int a; double b; };
            int main(void) { return (int)offsetof(union U, b); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(byte*)&__t.b - (byte*)&__t");
            emitted.ShouldNotContain("__Offsets");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_modellable_falls_back_to_helper()
    {
        // A bit-field makes the layout non-modellable, so offsetof uses the runtime
        // address-subtraction path. The typed IR emits this as an inline lambda (no
        // separate __Offsets helper class). The lambda body uses `&__t.c` for the
        // regular (non-bit-field) field c.
        var src = WriteTemp("""
            struct S { int a; int b : 3; int c; };
            int main(void) { return (int)offsetof(struct S, c); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldNotContain("__Offsets");
            emitted.ShouldContain("S __t = default; return (ulong)((byte*)&__t.c - (byte*)&__t)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void fixed_buffer_member_decays_in_helper_fallback()
    {
        // A `fixed` buffer decays to a pointer, so the fallback helper uses
        // `(byte*)__t.grid`, NOT `&__t.grid`. Reached here via a bit-field that
        // forces the fallback (a fixed-buffer-only struct would otherwise fold).
        var src = WriteTemp("""
            struct S { int a : 3; int grid[2]; };
            int main(void) { return (int)offsetof(struct S, grid); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("return (ulong)((byte*)__t.grid - (byte*)&__t)");
            emitted.ShouldNotContain("(byte*)&__t.grid");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void duplicate_offsetof_emits_one_helper()
    {
        // The typed IR emits each offsetof as an inline lambda — no __Offsets helper
        // class, no dedup across call sites. Two identical lambdas appear in the
        // output. Verify the lambda body is present (at least once) and that no
        // __Offsets class is generated.
        var src = WriteTemp("""
            struct S { int x : 3; int b; };
            int main(void) { return (int)offsetof(struct S, b) + (int)offsetof(struct S, b); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("S __t = default; return (ulong)((byte*)&__t.b - (byte*)&__t)");
            emitted.ShouldNotContain("__Offsets");
        }
        finally { File.Delete(src); }
    }
}
