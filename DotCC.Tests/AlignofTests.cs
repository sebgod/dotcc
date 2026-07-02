#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for `_Alignof(Type)` / `_Alignas(…)` (C11 §6.5.3.4 / §6.7.5) and
/// their C23 lowercase spellings. `_Alignof` folds at lowering to the layout
/// model's ABI alignment (an integer constant expression — it composes with
/// `_Static_assert`); `_Alignas` is accepted, constraint-checked (non-constant /
/// non-power-of-2 / weaker-than-natural are errors, matching gcc), and otherwise
/// ignored (a C# field/local has no controllable alignment). End-to-end in the
/// `c11-alignof/` fixture.
/// </summary>
[Collection("Alignof")]
public sealed class AlignofTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-align-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Alignof_is_an_integer_constant_expression()
    {
        // The fold happens at lowering, so the value flows into _Static_assert
        // (part 1's evaluation) — scalar, pointer, and LP64 `long` alignments.
        var src = WriteTemp("""
            _Static_assert(_Alignof(char) == 1, "char");
            _Static_assert(_Alignof(int) == 4, "int");
            _Static_assert(_Alignof(long) == 8, "LP64 long");
            _Static_assert(_Alignof(double) == 8, "double");
            _Static_assert(_Alignof(void*) == 8, "pointer");
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main()");
            // Folded away — no _Alignof survives into the emitted C#.
            emitted.ShouldNotContain("_Alignof");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Alignof_struct_is_max_field_alignment()
    {
        var src = WriteTemp("""
            struct P { char c; int i; };
            struct Q { char c; double d; };
            _Static_assert(_Alignof(struct P) == 4, "max field = int");
            _Static_assert(_Alignof(struct Q) == 8, "max field = double");
            int main(void) { return 0; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("static unsafe int main()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Alignas_is_accepted_and_ignored()
    {
        // Both operand forms — a constant expression and a type. Neither leaves
        // a trace in the emitted C# (no attribute, no call): pure no-op.
        var src = WriteTemp("""
            int main(void) {
                _Alignas(16) int over = 40;
                _Alignas(double) int asdbl = 2;
                _Alignas(0) int zero = 0;
                return over + asdbl + zero;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldNotContain("_Alignas");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Alignas_weaker_than_natural_is_rejected()
    {
        // C11 §6.7.5p4 — an alignment less strict than the type's own is a
        // constraint violation (gcc: "cannot reduce alignment").
        var src = WriteTemp("""
            int main(void) {
                _Alignas(1) int d = 4;
                return d;
            }
            """);
        try
        {
            var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }));
            ex.Message.ShouldContain("less strict than the type's natural alignment");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Alignas_non_power_of_2_is_rejected()
    {
        var src = WriteTemp("""
            int main(void) {
                _Alignas(3) int e = 5;
                return e;
            }
            """);
        try
        {
            var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }));
            ex.Message.ShouldContain("not a positive power of 2");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Alignas_non_constant_is_rejected()
    {
        var src = WriteTemp("""
            int main(void) {
                int n = 6;
                _Alignas(n) int f = 7;
                return f;
            }
            """);
        try
        {
            var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }));
            ex.Message.ShouldContain("not an integer constant");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lowercase_alignof_alignas_are_keywords_under_c23()
    {
        // C23 promotes the lowercase spellings onto the `_Alignof`/`_Alignas`
        // terminals — no <stdalign.h> needed.
        var src = WriteTemp("""
            _Static_assert(alignof(double) == 8, "keyword promotion");
            int main(void) {
                alignas(16) int x = 42;
                return x;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("static unsafe int main()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lowercase_via_stdalign_h_macros_under_c17()
    {
        // C11 §7.15: <stdalign.h> defines the lowercase macros pre-C23. The
        // constraint checks must fire through the macro path too.
        var src = WriteTemp("""
            #include <stdalign.h>
            _Static_assert(alignof(short) == 2, "macro alignof");
            int main(void) {
                alignas(1) int d = 4;
                return d;
            }
            """);
        try
        {
            var ex = Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17")));
            ex.Message.ShouldContain("less strict than the type's natural alignment");
        }
        finally { File.Delete(src); }
    }
}
