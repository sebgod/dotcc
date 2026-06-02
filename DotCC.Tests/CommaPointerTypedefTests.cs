#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A comma operator in value position lowers to a C# tuple <c>(a, b).ItemN</c>.
/// A pointer / function-pointer operand can't be a <c>ValueTuple</c> type
/// argument (CS0306), so dotcc round-trips it through <c>nint</c>. The pointer
/// detection (<c>IsPointerCsType</c>) recognizes not just a literal <c>T*</c> but
/// also a pointer TYPEDEF (<c>NodeRef</c> → <c>Node*</c>, like Lua's
/// <c>StkId</c>) and a function-pointer type / typedef (<c>delegate*&lt;…&gt;</c> /
/// <c>CFunc</c>, like <c>lua_CFunction</c>) — neither ends in a literal <c>*</c>.
/// End-to-end in the <c>comma-ptr-typedef/</c> fixture.
/// </summary>
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
            // NodeRef (-> Node*) operand cast to nint for the tuple, result cast back.
            emitted.ShouldContain("(g = 1, (nint)(np)).Item2");
            emitted.ShouldContain("((NodeRef)((g = 1, (nint)(np)).Item2))");
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
            // CFunc (a fn-ptr typedef) operand round-trips through nint too.
            emitted.ShouldContain("(g = 2, (nint)(cf)).Item2");
            emitted.ShouldContain("((CFunc)((g = 2, (nint)(cf)).Item2))");
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
            Compiler.EmitCSharp(new[] { src }).ShouldContain("CFunc cf = &dbl");
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
            // An int comma value stays a plain tuple element — no nint round-trip.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(g = 1, x).Item2");
            emitted.ShouldNotContain("(nint)(x)");
        }
        finally { File.Delete(src); }
    }
}
