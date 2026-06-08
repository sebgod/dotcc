#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Type synthesis through struct/union FIELD chains, and the contexts that
/// consume it. The typed IR lifts comma operators in statement / return position
/// into separate statements, so side-effect operands (including void casts and
/// pointer increments) become plain statements and the last operand is returned or
/// assigned directly — no tuple or <c>nint</c> round-trip. These tests verify
/// that lift, plus the parenthesised bare-name callee unwrap
/// (<c>(f)(x)</c> → <c>f(x)</c>), which avoids C#'s cast-ambiguity. End-to-end
/// in <c>fnptr-field-comma/</c>.
/// </summary>
public sealed class FieldChainCommaTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fcc-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private const string Decls = """
        typedef int (*CFunc)(int);
        typedef union Val { int i; CFunc fn; } Val;
        typedef struct Box { Val v; } Box;
        typedef Box *BoxPtr;
        """;

    [Fact]
    public void fnptr_field_through_pointer_typedef_base_round_trips_through_nint()
    {
        // `b` is a BoxPtr (a pointer typedef); `((b)->v).fn` reaches the union's
        // function-pointer member. The field's type must resolve through the
        // typedef'd-pointer chain for the comma-tuple to nint-cast it.
        var src = WriteTemp($$"""
            {{Decls}}
            CFunc pick(BoxPtr b) { return ((void)0, ((b)->v).fn); }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The IR lifts the comma: `(void)0` becomes `_ = 0` (discarded int);
            // the fn-ptr member is returned directly — no tuple or nint round-trip.
            emitted.ShouldContain("_ = 0;");
            emitted.ShouldContain("return b->v.fn;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void parenthesized_identifier_callee_is_unwrapped()
    {
        // Lua's `LUAI_TRY` spells a direct fn-ptr call as `((f)(args))`. C# reads
        // `(f)(x)` as a cast of the tuple `(x)` — so the redundant parens around
        // the bare-name callee are stripped to `f(x)`.
        var src = WriteTemp($$"""
            {{Decls}}
            int call_through(CFunc f, int x) { return (f)(x); }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("return f(x);");
            emitted.ShouldNotContain("(f)(x)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void void_discard_of_pointer_increment_in_comma_is_nint_cast()
    {
        // `(void)(++m)` discards a `char*` pre-increment as a comma operand; the IR
        // lifts the side-effect to a plain statement and returns the last operand
        // directly — no tuple or nint cast needed.
        var src = WriteTemp($$"""
            int discard_ptr(char *m) { return ((void)(++m), 1); }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("++m;");
            emitted.ShouldContain("return 1;");
        }
        finally { File.Delete(src); }
    }
}
