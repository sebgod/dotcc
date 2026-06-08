#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for an ANONYMOUS struct type used directly in a declaration
/// (`struct { … } x;` / `static const struct { … } tab[] = {…}`) — distinct from
/// a typedef body or a struct member. Lua's lparser.c priority table. dotcc
/// synthesizes a name (`__Anon&lt;N&gt;`), emits the struct decl, records its
/// fields, and returns the synth name as the Type so the ordinary array /
/// aggregate-init productions compose. End-to-end in `anon-struct-decl/`.
/// </summary>
public sealed class AnonStructDeclTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-asd-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void file_scope_static_anon_struct_array_lowers_to_synth_type()
    {
        var src = WriteTemp("""
            typedef unsigned char lu_byte;
            static const struct { lu_byte left; lu_byte right; } priority[] = {
                {10, 10}, {14, 13}
            };
            int main(void) { return priority[1].left + priority[1].right; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct __Anon0");
            // array of the synth struct, elements as positional object-inits
            emitted.ShouldContain("new __Anon0 { left = 10, right = 10 }");
            emitted.ShouldContain("new __Anon0 { left = 14, right = 13 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void block_scope_anon_struct_variable_lowers_to_synth_type()
    {
        var src = WriteTemp("""
            int main(void) { struct { int a; int b; } pt = {5, 6}; return pt.a + pt.b; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct __Anon0");
            emitted.ShouldContain("__Anon0 pt = new __Anon0 { a = 5, b = 6 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void anon_struct_array_length_idiom_resolves()
    {
        // sizeof(t)/sizeof(t[0]) over an anon-struct array — the synth type's
        // element size cancels, giving the element count.
        var src = WriteTemp("""
            static const struct { int a; int b; } t[] = { {1,2},{3,4},{5,6} };
            int main(void) { return (int)(sizeof(t) / sizeof(t[0])); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("(ulong)((3 * sizeof(__Anon0)))) / ((ulong)(sizeof(__Anon0)))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void typedef_anon_struct_is_unaffected()
    {
        // Regression guard: the new anon-struct Type production must NOT capture
        // `typedef struct { … } Name;` — that still lowers to a named `struct Name`
        // (via typedefStructAnon), not a synth `__Anon` + alias.
        var src = WriteTemp("""
            typedef struct { int x; int y; } Point;
            int main(void) { Point p = {3, 4}; return p.x + p.y; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct Point");
            emitted.ShouldContain("Point p = new Point { x = 3, y = 4 }");
            emitted.ShouldNotContain("__NestS");
        }
        finally { File.Delete(src); }
    }
}
