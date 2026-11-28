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
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), warnings: WarningFlags.Default | WarningFlags.PedanticErrors))
                .Message.ShouldContain("_Generic");
        }
        finally { File.Delete(src); }
    }

    private const string CalleePrelude = """
        float gcf(float v) { return v + 0.5f; }
        double gcd(double v) { return v + 0.25; }
        #define sel(X) _Generic((X), float: gcf, default: gcd)
        """ + "\n";

    [Fact]
    public void Generic_callee_is_a_direct_call_of_the_selected_function()
    {
        var src = WriteTemp(CalleePrelude + """
            int main(void) {
                float f = 1.0f;
                int i = 2;
                float a = _Generic((f), float: gcf, default: gcd)(f);
                double b = _Generic((i), float: gcf, default: gcd)(i);
                return (int)(a + b);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("gcf(f)");
            emitted.ShouldContain("gcd(i)");
            emitted.ShouldNotContain("delegate*<float, float>");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Generic_callee_call_has_the_selected_return_type()
    {
        var src = WriteTemp(CalleePrelude + """
            int main(void) {
                float f = 1.0f;
                int fs = _Generic(sel(f)(f), float: 1, default: 0);
                int ds = _Generic(sel(1)(1), double: 1, default: 0);
                int sz = (int)sizeof(sel(f)(f));
                return fs + ds + sz;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int fs = 1;");
            emitted.ShouldContain("int ds = 1;");
            emitted.ShouldContain("int sz = unchecked((int)((ulong)(sizeof(float))));");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Generic_callee_coerces_the_argument_to_the_selected_parameter()
    {
        var src = WriteTemp(CalleePrelude + """
            int main(void) {
                int i = 3;
                float f = 1.0f;
                return (int)_Generic((f), float: gcf, default: gcd)(i);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The int argument converts to the selected float parameter (C#'s implicit
            // int-to-float at the direct call); the double arm would take it as gcd(i).
            emitted.ShouldContain("(int)gcf(i)");
            emitted.ShouldNotContain("gcd(i)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Parenthesised_generic_callee_is_a_direct_call()
    {
        var src = WriteTemp(CalleePrelude + """
            int main(void) {
                float f = 1.0f;
                return (int)(_Generic((f), float: gcf, default: gcd))(f);
            }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("gcf(f)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Generic_callee_in_wat_is_a_direct_call()
    {
        var src = WriteTemp(CalleePrelude + """
            int main(void) {
                float f = 1.0f;
                return (int)_Generic((f), float: gcf, default: gcd)(f);
            }
            """);
        try
        {
            var wat = Compiler.EmitWat(new[] { src });
            wat.ShouldContain("call $gcf");
            wat.ShouldNotContain("call_indirect");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Undeclared_selected_callee_is_an_implicit_declaration_error()
    {
        var src = WriteTemp("""
            double gcd(double v) { return v; }
            int main(void) {
                float f = 1.0f;
                return (int)_Generic((f), float: nosuchfn, default: gcd)(f);
            }
            """);
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("implicit declaration of function 'nosuchfn'");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Undeclared_unselected_callee_stays_unbound()
    {
        var src = WriteTemp("""
            double gcd(double v) { return v; }
            int main(void) {
                float f = 1.0f;
                return (int)_Generic((f), float: gcd, default: nosuchfn)(f);
            }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("gcd(f)");
        }
        finally { File.Delete(src); }
    }
}
