#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #221: brace initializers follow C11 6.7.9p17-20's current-object walk. A
/// designation is a path (<c>.op.code</c>, <c>.v.Name.id</c>, <c>[3].x</c>,
/// <c>[1][2]</c>) to the subobject it initializes, a member of an anonymous union
/// is designated by its own name, and positional initialization continues after
/// the designated subobject with brace elision. End-to-end in
/// <c>designated-paths/</c> (gcc-matched).
/// </summary>
[Collection("DesignationPath")]
public sealed class DesignationPathTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-desig-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_member_of_an_anonymous_union_is_designated_by_name()
    {
        // CPython's `_PyObject_HEAD_INIT` sets `.ob_refcnt` inside an anonymous union.
        var emitted = Emit("""
            struct pyobj { union { long ob_refcnt; unsigned int split[2]; }; const char *ob_type; };
            int main(void) { struct pyobj o = { .ob_refcnt = 5, .ob_type = "t" }; return (int)o.ob_refcnt; }
            """);
        emitted.ShouldContain("ob_refcnt = 5");
        emitted.ShouldContain("ob_type = ");
    }

    [Fact]
    public void Nested_member_designators_merge_into_one_subobject()
    {
        // `.v.Name.id` and `.v.Name.ctx` both land in the same `v.Name`.
        var emitted = Emit("""
            struct name { int id; int ctx; };
            struct expr { int kind; union { struct name Name; long Constant; } v; };
            int main(void) { struct expr e = { .kind = 1, .v.Name.id = 42, .v.Name.ctx = 3 }; return e.v.Name.ctx; }
            """);
        emitted.ShouldContain("id = 42");
        emitted.ShouldContain("ctx = 3");
    }

    [Fact]
    public void Positional_initialization_continues_after_a_nested_designation()
    {
        var emitted = Emit("""
            struct inner { int b, c; };
            struct outer { struct inner a; int d; };
            int main(void) { struct outer o = { .a.b = 1, 2, 3 }; return o.a.c + o.d; }
            """);
        emitted.ShouldContain("b = 1");
        emitted.ShouldContain("c = 2");
        emitted.ShouldContain("d = 3");
    }

    [Fact]
    public void A_short_struct_array_is_zero_filled_to_its_declared_size()
    {
        // The storage is the declared 3 elements, not the 1 the list gives.
        var emitted = Emit("""
            struct point { int x, y; };
            int main(void) { struct point p[3] = { { 9, 9 } }; return p[2].x; }
            """);
        emitted.ShouldContain("default(point), default(point)");
    }

    [Fact]
    public void Array_designators_with_braced_and_member_paths()
    {
        var emitted = Emit("""
            struct point { int x, y; };
            int main(void) { struct point p[4] = { [1].x = 5, [1].y = 6, [3] = { 7, 8 } }; return p[3].y; }
            """);
        emitted.ShouldContain("x = 5, y = 6");
        emitted.ShouldContain("x = 7, y = 8");
    }

    [Fact]
    public void Multi_dimensional_designators_fill_the_flat_storage()
    {
        var emitted = Emit("""
            int main(void) { int g[2][3] = { [1][2] = 9, [0][1] = 4 }; return g[1][2]; }
            """);
        emitted.ShouldContain("stackalloc int[]{ 0, 4, 0, 0, 0, 9 }");
    }

    [Theory]
    [InlineData("struct s { int a; }; int main(void) { struct s v = { .b = 1 }; return 0; }", "designator '.b' names no member of 's'")]
    [InlineData("int main(void) { int a[2] = { [2] = 1 }; return 0; }", "array designator index 2 is out of bounds for [2]")]
    [InlineData("int main(void) { int a[2] = { 1, 2, 3 }; return 0; }", "excess elements in the initializer")]
    public void Invalid_designations_are_rejected(string source, string message)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-desig-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, source);
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { path })).Message.ShouldContain(message);
        }
        finally { File.Delete(path); }
    }
}
