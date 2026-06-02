#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for `static` struct/union AGGREGATE initializers (`static T x =
/// {...};`, file-scope and block-scope) and the recursive nested-brace handling
/// shared with the non-static `DeclStructInit`. A `static T x = {...}` lowers to
/// a once-initialised DotCcGlobals field; a nested brace for a struct/union field
/// recurses into `field = new &lt;FieldType&gt; { ... }` (the union's first member).
/// Lua's lcode.c `static const expdesc ef = {VKINT, {0}, NO_JUMP, NO_JUMP}`.
/// End-to-end in the `static-struct-init/` fixture (gcc-oracle-validated).
/// </summary>
public sealed class StaticStructInitTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-ssi-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void block_scope_static_struct_init_lowers_to_mangled_global_field()
    {
        var src = WriteTemp("""
            struct P { int x; int y; };
            int f(void) { static const struct P p = {10, 20}; return p.x + p.y; }
            int main(void) { return f(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Lowered to a once-init static field, mangled by function, and the
            // body use rewrites to the mangled name.
            emitted.ShouldContain("public static unsafe P __static_f_p = new P { x = 10, y = 20 };");
            emitted.ShouldContain("__static_f_p.x");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void file_scope_static_struct_init_lowers_to_global_field()
    {
        var src = WriteTemp("""
            struct P { int x; int y; };
            static const struct P origin = {3, 4};
            int main(void) { return origin.x + origin.y; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("public static unsafe P origin = new P { x = 3, y = 4 };");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void nested_brace_for_union_member_recurses_into_field_type()
    {
        // `{7, {42}, -1}` — the `{42}` initializes the union member `u`'s first
        // member; lowers to `u = new <synth union type> { i = 42 }`.
        var src = WriteTemp("""
            struct T { int kind; union { int i; float f; } u; int extra; };
            int f(void) { static const struct T t = {7, {42}, -1}; return t.kind + t.u.i + t.extra; }
            int main(void) { return f(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("kind = 7");
            emitted.ShouldContain("{ i = 42 }");   // the union's first member
            emitted.ShouldContain("extra = (-1)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_static_nested_struct_init_also_recurses()
    {
        // The DeclStructInit refactor: a non-static local nested aggregate init
        // now recurses too (previously: "nested brace initializer isn't valid").
        var src = WriteTemp("""
            struct Inner { int a; int b; };
            struct Outer { int tag; struct Inner nested; };
            int main(void) { struct Outer o = {1, {2, 3}}; return o.tag + o.nested.a + o.nested.b; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("new Outer { tag = 1, nested = new Inner { a = 2, b = 3 } }");
        }
        finally { File.Delete(src); }
    }
}
