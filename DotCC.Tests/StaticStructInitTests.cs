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
[Collection("StaticStructInit")]
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
            // Lowered to a static field, mangled by function, built in place by a
            // helper that runs in initialization order; the body use rewrites to the
            // mangled name.
            emitted.ShouldContain("public static P p__s0;");
            emitted.ShouldContain("var __o = (P*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref p__s0);\n        __o->x = 10;\n        __o->y = 20;\n");
            emitted.ShouldContain("internal static readonly bool p__s0__filled = p__s0__init();");
            emitted.ShouldContain("p__s0.x");
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
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("public static P origin;");
            emitted.ShouldContain("__o->x = 3;\n        __o->y = 4;\n");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void nested_brace_for_union_member_recurses_into_field_type()
    {
        // `{7, {42}, -1}`: the `{42}` initializes the union member `u`'s first
        // member, stored in place through the static object's storage.
        var src = WriteTemp("""
            struct T { int kind; union { int i; float f; } u; int extra; };
            int f(void) { static const struct T t = {7, {42}, -1}; return t.kind + t.u.i + t.extra; }
            int main(void) { return f(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("__o->kind = 7;");
            emitted.ShouldContain("__o->u.i = 42;");   // the union's first member
            emitted.ShouldContain("__o->extra = -1;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void a_constant_char_array_member_is_one_block_copy()
    {
        // A string's characters are one copy from read-only data, not a store per byte
        // (CPython's static string objects were 6,700 of them in one method). The copy
        // stops at the last non-zero element; a single element keeps its store.
        var src = WriteTemp("""
            struct S { int n; char name[24]; char tag[4]; char one[4]; };
            static struct S s = { 3, "hello, block copy", "ab", "z" };
            int main(void) { return s.name[0] + s.tag[1] + s.one[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(__o->name, L(\"hello, block copy\"u8), 17 * (uint)sizeof(byte));");
            emitted.ShouldContain("CopyBlockUnaligned(__o->tag, L(\"ab\"u8), 2 * (uint)sizeof(byte));");
            emitted.ShouldContain("__o->one[0] = 122;");
            emitted.ShouldNotContain("__o->name[0] =");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void a_block_copy_past_ascii_spells_its_bytes()
    {
        // A u8 literal would UTF-8-encode a byte past 0x7F as two, so such a run is a
        // constant array (still read-only data, still one copy).
        var src = WriteTemp("""
            struct S { unsigned char b[12]; };
            static struct S s = { { 1, 2, 3, 4, 5, 6, 7, 200, 9 } };
            int main(void) { return s.b[7]; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("CopyBlockUnaligned(__o->b, L(new byte[] { 1, 2, 3, 4, 5, 6, 7, 200, 9 }), 9 * (uint)sizeof(byte));");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void a_large_initializer_is_split_into_parts()
    {
        // 600 pointer stores (no block copy for addresses): past 512 the helper calls parts,
        // so no one method holds them all (clang took 25 GB on CPython's 13,000 in one).
        var ptrs = string.Join(", ", System.Linq.Enumerable.Repeat("&g", 600));
        var src = WriteTemp($$"""
            static int g;
            struct S { int *p[600]; };
            static struct S s = { { {{ptrs}} } };
            int main(void) { return s.p[599] == &g ? 0 : 1; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("        s__init1(__o);\n        s__init2(__o);\n        return true;\n");
            emitted.ShouldContain("private static unsafe void s__init1(S* __o)");
            emitted.ShouldContain("private static unsafe void s__init2(S* __o)");
            emitted.ShouldNotContain("s__init3");
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
