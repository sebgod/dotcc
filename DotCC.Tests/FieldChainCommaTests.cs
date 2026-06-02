#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Type synthesis through struct/union FIELD chains, and the contexts that
/// consume it. dotcc lowers C's comma operator to a C# value-tuple, and a
/// pointer / function-pointer can't be a <c>ValueTuple</c> element (CS0306) — so
/// it's round-tripped through <c>nint</c>, which requires knowing the operand's
/// type. Three feeds into that detection are exercised here:
/// <list type="bullet">
/// <item>a field reached through a POINTER-TYPEDEF base (Lua's <c>StkId</c> →
/// <c>StackValue*</c>) — <c>StructKeyOf</c> resolves the typedef before peeling
/// the pointer, so the member's type is found;</item>
/// <item>a <c>(void)X</c> discard of a pointer in a comma — the discard carries
/// its operand's CType;</item>
/// <item>a pointer pre/post-increment — <c>++p</c> has the operand's pointer
/// type.</item>
/// </list>
/// Plus the parenthesised bare-name callee unwrap (<c>(f)(x)</c> → <c>f(x)</c>),
/// which avoids C#'s cast-ambiguity. End-to-end in <c>fnptr-field-comma/</c>.
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
            // The fn-ptr member is cast to nint for the tuple element, and the
            // .Item result is cast back to the CFunc fn-ptr typedef.
            emitted.ShouldContain("(nint)(b->v.fn)");
            emitted.ShouldContain("((CFunc)((_ = (0), (nint)(b->v.fn)).Item2))");
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
        // `(void)(++m)` discards a `char*` pre-increment as a comma operand; the
        // discard must carry the pointer type so the tuple element nint-casts it.
        var src = WriteTemp($$"""
            int discard_ptr(char *m) { return ((void)(++m), 1); }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(nint)(_ = (((++m))))");
        }
        finally { File.Delete(src); }
    }
}
