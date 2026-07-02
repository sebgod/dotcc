#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C11 `_Generic` — type-generic selection resolved entirely at
/// LOWERING time on the controlling expression's synthesized CType
/// (IrBuilder.BuildGenericSelect): only the compatible association's expression
/// is lowered; the controlling expression is built for its type and discarded
/// (C says it is not evaluated). Constraint violations are gcc-worded collected
/// errors. End-to-end in the `c11-generic/` fixture (gcc- and MSVC-oracle-able).
/// </summary>
[Collection("GenericSelection")]
public sealed class GenericTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-generic-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Selects_the_arm_matching_the_controlling_type()
    {
        var src = WriteTemp("""
            int main(void) {
                int i = 0;
                double d = 0;
                int *p = &i;
                int a = _Generic(i, int: 10, double: 20, default: 30);
                int b = _Generic(d, int: 10, double: 20, default: 30);
                int c = _Generic(p, int *: 40, default: 30);
                int e = _Generic(1.5f, int: 10, double: 20, default: 30);
                return a + b + c + e;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int a = 10;");
            emitted.ShouldContain("int b = 20;");
            emitted.ShouldContain("int c = 40;");
            emitted.ShouldContain("int e = 30;");  // float hits default
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Controlling_expression_is_not_evaluated()
    {
        // C11 §6.5.1.1p3: the controlling expression of a generic selection is
        // not evaluated — the increment must leave no trace in the emitted C#.
        // (The variable name is deliberately unique: the emitted string includes
        // the spliced Libc runtime block, so a short name like `x` would collide
        // substring-wise with runtime code such as `idx++`.)
        var src = WriteTemp("""
            int main(void) {
                int uneval_probe = 5;
                int k = _Generic(uneval_probe++, int: 1, default: 0);
                return k + uneval_probe;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int k = 1;");
            emitted.ShouldNotContain("uneval_probe++");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lvalue_conversion_decays_arrays_and_drops_qualifiers()
    {
        // §6.5.1.1 (C17 semantics, as gcc/clang implement): the controlling
        // type undergoes lvalue conversion — `int[3]` matches `int *`, a
        // string literal (char[N]) matches `char *`, and a `const int` lvalue
        // matches plain `int`.
        var src = WriteTemp("""
            int main(void) {
                int arr[3] = { 1, 2, 3 };
                const int ci = 7;
                int a = _Generic(arr, int *: 1, default: 0);
                int s = _Generic("str", char *: 1, default: 0);
                int q = _Generic(ci, int: 1, default: 0);
                return a + s + q;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int a = 1;");
            emitted.ShouldContain("int s = 1;");
            emitted.ShouldContain("int q = 1;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Enum_controlling_expression_falls_back_to_its_int_backing()
    {
        // C leaves an enum's compatible integer type implementation-defined;
        // dotcc's enums are int-backed, so with no association naming the enum
        // itself the `int:` arm wins.
        var src = WriteTemp("""
            enum Color { RED, GREEN };
            int main(void) {
                enum Color c = RED;
                return _Generic(c, int: 0, default: 1);
            }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("return 0;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Selection_composes_with_static_assert()
    {
        // The selection happens at lowering, so an ICE arm feeds ConstEval —
        // dividend stacking with part 1 of the comptime milestone.
        var src = WriteTemp("""
            _Static_assert(_Generic(1L, long: 1, default: 0) == 1, "long selects");
            int main(void) { return 0; }
            """);
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { src }));
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void No_compatible_association_without_default_is_an_error()
    {
        var src = WriteTemp("int main(void) { return _Generic(1.5f, int: 1, double: 2); }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("'_Generic' selector of type 'float' is not compatible with any association");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Duplicate_compatible_types_are_an_error()
    {
        var src = WriteTemp("int main(void) { return _Generic(1, int: 1, int: 2); }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("'_Generic' specifies two compatible types");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Duplicate_default_is_an_error()
    {
        var src = WriteTemp("int main(void) { return _Generic(1, default: 1, default: 2); }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("duplicate 'default' case in '_Generic'");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Generic_gated_as_c11_under_pedantic()
    {
        var src = WriteTemp("int main(void) { return _Generic(1, int: 0, default: 1); }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
                .Message.ShouldContain("_Generic");
        }
        finally { File.Delete(src); }
    }
}
