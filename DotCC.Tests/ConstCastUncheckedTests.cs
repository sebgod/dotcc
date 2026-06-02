#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// C truncates an out-of-range integer CONSTANT cast (mod 2^width); C# rejects a
/// constant cast that doesn't fit the target (CS0221) unless wrapped in
/// <c>unchecked(...)</c>. dotcc wraps an integer cast whose operand is a constant
/// expression that isn't provably in range — Lua's <c>cast_byte(~mask)</c>,
/// <c>(size_t)-1</c>, <c>cast_int(MAX_SIZET/sizeof(t))</c>. The decision rides a
/// <c>ConstExpr</c> flag (true even when the value is too wide to fold into a
/// 32-bit int — a <c>uint</c>-modular shift, a <c>ulong</c> divide), so the wrap
/// fires for those too. A constant that PROVABLY fits, and a runtime
/// (non-constant) cast, stay bare — clean output, and a runtime cast truncates
/// silently anyway. End-to-end in <c>const-cast-unchecked/</c>.
/// </summary>
public sealed class ConstCastUncheckedTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-ccu-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Emit(string body)
    {
        var src = WriteTemp(body);
        try { return Compiler.EmitCSharp(new[] { src }); }
        finally { File.Delete(src); }
    }

    [Fact]
    public void cast_byte_of_complement_mask_wraps_unchecked()
    {
        // `~(1 << 6)` folds to -65 (int), out of byte range → unchecked.
        var emitted = Emit("""
            typedef unsigned char lu_byte;
            lu_byte f(lu_byte v) { return v & (lu_byte)(~(1 << 6)); }
            int main(void) { return 0; }
            """);
        emitted.ShouldContain("unchecked(((lu_byte)((~((1 << 6))))))");
    }

    [Fact]
    public void cast_size_t_of_negative_one_wraps_unchecked()
    {
        // `(size_t)-1` → ulong; -1 doesn't fit ulong → unchecked.
        var emitted = Emit("""
            unsigned long f(void) { return (unsigned long)(-1); }
            int main(void) { return 0; }
            """);
        emitted.ShouldContain("unchecked(((ulong)((-1))))");
    }

    [Fact]
    public void uint_modular_constant_cast_wraps_via_constexpr_flag()
    {
        // `(~0u) << 3` is a uint-modular value dotcc can't fold to a 32-bit int, so
        // ConstInt is null — but it's still a constant expression, so the ConstExpr
        // flag drives the wrap (Roslyn computes the correct value under unchecked).
        var emitted = Emit("""
            typedef unsigned char lu_byte;
            lu_byte f(void) { return (lu_byte)(~((~0u) << 3)); }
            int main(void) { return 0; }
            """);
        emitted.ShouldContain("unchecked(((lu_byte)((~((((~0u)) << 3))))))");
    }

    [Fact]
    public void in_range_constant_cast_stays_bare()
    {
        // 219 fits uint → no cast wrapper (clean output preserved).
        var emitted = Emit("""
            unsigned f(void) { return (unsigned)(219); }
            int main(void) { return 0; }
            """);
        emitted.ShouldContain("((uint)(219))");
        emitted.ShouldNotContain("unchecked(((uint)(219)))");
    }

    [Fact]
    public void runtime_cast_stays_bare()
    {
        // A non-constant operand never needs unchecked (a runtime cast truncates).
        // (The spliced Libc runtime block legitimately contains `unchecked`, so the
        // assertion is on this cast's exact bare shape, not the whole emit.)
        var emitted = Emit("""
            typedef unsigned char lu_byte;
            int f(int x) { return (int)((lu_byte)x); }
            int main(void) { return 0; }
            """);
        emitted.ShouldContain("((int)(((lu_byte)x)))");
        emitted.ShouldNotContain("unchecked(((lu_byte)x))");
    }
}
