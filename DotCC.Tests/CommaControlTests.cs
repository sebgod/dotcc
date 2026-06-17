#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the comma operator in a CONTROLLING expression
/// (`while`/`if`/`switch`) and the `(void)` discard cast. C# has no comma
/// operator, and a void side-effect operand can't be a tuple element, so dotcc
/// lifts the non-last operands into statements and tests the last with
/// <c>Cond.B</c>. Lua's llex.c `while (cast_void(save_and_next(ls)),
/// lisxdigit(ls-&gt;current))`. End-to-end in `comma-void-control/`.
/// </summary>
[Collection("CommaControl")]
public sealed class CommaControlTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-cc-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void while_with_comma_condition_lifts_side_effects_and_tests_last()
    {
        var src = WriteTemp("""
            int main(void) { int i = 0; while (i = i + 1, i < 3) { } return i; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The IR keeps the comma inside the while condition as a tuple;
            // the side-effect assignment is the first element and the boolean
            // test on the last element drives the loop.
            emitted.ShouldContain("while (Cond.B(((i = i + 1, ((CBool)(i < 3))).Item2)))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void void_cast_of_comma_lowers_to_discard_statements()
    {
        // `(void)(a(), b())` as a statement → the operands as statements, not a
        // tuple (a void operand can't be a tuple element).
        var src = WriteTemp("""
            void a(void); int b(void);
            int main(void) { (void)(a(), b()); return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("a();");
            emitted.ShouldContain("b();");
            emitted.ShouldNotContain(".Item2");   // NOT a tuple
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void void_cast_of_plain_value_is_a_discard_assignment()
    {
        // `(void)x;` where x has no side effects: the IR drops the cast as a
        // no-op and emits an empty statement (`;`). The variable is still
        // accessible and the return value is unaffected.
        var src = WriteTemp("""
            int main(void) { int x = 5; (void)x; return x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int x = 5;");
            emitted.ShouldContain("return x;");
            emitted.ShouldNotContain("(void)x");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void if_with_comma_condition_wraps_in_block()
    {
        var src = WriteTemp("""
            int main(void) { int x = 0; if (x = 7, x > 3) return 1; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("x = 7;");
            emitted.ShouldContain("if (Cond.B(((CBool)(x > 3))))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void value_position_comma_still_tuple_izes()
    {
        // The IR lifts the side-effect expression out as a separate statement
        // and assigns the last operand directly, rather than using a C# tuple.
        // `int c = (a, b)` → `_ = a; int c = b;`
        var src = WriteTemp("""
            int main(void) { int a = 1, b = 2; int c = (a, b); return c; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("_ = a;");
            emitted.ShouldContain("int c = b;");
        }
        finally { File.Delete(src); }
    }
}
