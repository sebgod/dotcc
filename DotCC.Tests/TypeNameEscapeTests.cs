#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for GH #244: a struct, union or enum tag (or a typedef-named anonymous aggregate) spelled like a C#
/// reserved keyword is <c>@</c>-escaped at every point the C# backend writes a type name. End to end in the
/// <c>cs-keyword-tags/</c> fixture.
/// </summary>
public sealed class TypeNameEscapeTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tn-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void issue_repro_escapes_struct_tags()
    {
        var e = Emit("""
            struct object { int x; };
            struct string { char c; };
            int main(void) { struct object o = { 1 }; struct string s = { 'x' }; return o.x + s.c; }
            """);
        e.ShouldContain("struct @object");
        e.ShouldContain("struct @string");
        e.ShouldContain("new @object {");
    }

    [Fact]
    public void union_tag_is_escaped()
    {
        Emit("union base { int i; char b[4]; };\nint main(void) { union base u; u.i = 1; return u.i; }")
            .ShouldContain("struct @base");
    }

    [Fact]
    public void enum_tag_and_enumerator_reference_are_escaped()
    {
        var e = Emit("enum event { A = 1, B = 4 };\nint main(void) { enum event v = B; return v + A; }");
        e.ShouldContain("enum @event : int");
        e.ShouldContain("@event.");
    }

    [Fact]
    public void typedef_named_anonymous_struct_is_escaped()
    {
        Emit("typedef struct { int v; } lock;\nint main(void) { lock l; l.v = 1; return l.v; }")
            .ShouldContain("struct @lock");
    }

    [Fact]
    public void opaque_tag_is_escaped()
    {
        Emit("struct fixed;\nstruct fixed *p;\nint main(void) { return p == 0; }")
            .ShouldContain("struct @fixed");
    }

    [Fact]
    public void struct_array_member_wrapper_name_has_no_at_sign()
    {
        var e = Emit("""
            struct pt { int x; };
            struct lock { struct pt a[2]; };
            int main(void) { struct lock l = { { { 1 }, { 2 } } }; return l.a[1].x; }
            """);
        e.ShouldContain("struct @lock");
        e.ShouldContain("__IA_lock_a");
        e.ShouldNotContain("__IA_@");
    }

    [Fact]
    public void global_named_like_its_tag_stays_qualified()
    {
        var e = Emit("struct object { int x; };\nint object = 3;\nint main(void) { struct object o = { 1 }; return object + o.x; }");
        e.ShouldContain("DotCcGlobals.@object");
    }

    [Fact]
    public void plain_tag_is_unchanged()
    {
        var e = Emit("struct rec { int x; };\nint main(void) { struct rec r = { 1 }; return r.x; }");
        e.ShouldContain("struct rec");
        e.ShouldNotContain("@rec");
    }
}
