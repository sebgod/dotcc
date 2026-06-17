#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// The typed IR lifts a comma operator in init/value position into statements:
/// side-effect operands become standalone statements and the last operand is
/// assigned directly, avoiding the C# tuple / nint round-trip entirely. These
/// tests verify the lifted lowering for pointer typedef, fn-ptr typedef, and
/// plain int operands, and that fn-ptr typedefs decay bare function names to
/// their address. End-to-end in the <c>comma-ptr-typedef/</c> fixture.
/// </summary>
[Collection("CommaPointerTypedef")]
public sealed class CommaPointerTypedefTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-cpt-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private const string Decls = """
        typedef struct Node { int v; } Node;
        typedef Node *NodeRef;
        typedef int (*CFunc)(int);
        static int dbl(int x) { return x * 2; }
        """;

    [Fact]
    public void pointer_typedef_comma_value_round_trips_through_nint()
    {
        var src = WriteTemp($$"""
            {{Decls}}
            int main(void) {
                Node n = { 7 }; NodeRef np = &n; int g = 0;
                NodeRef p = (g = 1, np);
                return p->v;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The IR lifts the comma: the side-effect is emitted as a statement
            // and the pointer value is assigned directly (no tuple/nint round-trip).
            emitted.ShouldContain("g = 1;");
            emitted.ShouldContain("Node* p = np;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void fnptr_typedef_comma_value_round_trips_through_nint()
    {
        var src = WriteTemp($$"""
            {{Decls}}
            int main(void) {
                CFunc cf = dbl; int g = 0;
                CFunc f = (g = 2, cf);
                return f(20);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The IR lifts the comma: the side-effect is a statement; the
            // fn-ptr value is assigned directly (no tuple/nint round-trip).
            emitted.ShouldContain("g = 2;");
            emitted.ShouldContain("delegate*<int, int> f = cf;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void typedef_fnptr_decl_init_decays_bare_fn_name()
    {
        // `CFunc cf = dbl;` — a typedef'd fn-ptr variable initialized with a bare
        // function name decays to its address (the standalone `(*fp)()` declarator
        // already did; this is the typedef'd path).
        var src = WriteTemp($$"""
            {{Decls}}
            int main(void) { CFunc cf = dbl; return cf(5); }
            """);
        try
        {
            // The IR expands the fn-ptr typedef to its underlying delegate* type;
            // the function name still decays to its address (&dbl).
            Compiler.EmitCSharp(new[] { src }).ShouldContain("delegate*<int, int> cf = &dbl");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_pointer_comma_value_is_not_nint_cast()
    {
        var src = WriteTemp("""
            int main(void) { int g = 0, x = 9; int v = (g = 1, x); return v; }
            """);
        try
        {
            // An int comma value is lifted as a statement; no nint round-trip.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("g = 1;");
            emitted.ShouldContain("int v = x;");
            emitted.ShouldNotContain("(nint)(x)");
        }
        finally { File.Delete(src); }
    }
}
