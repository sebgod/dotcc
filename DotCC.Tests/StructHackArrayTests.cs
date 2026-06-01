#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for struct array members of NON-primitive element type — lowered
/// to a C# 12 <c>[InlineArray(N)]</c> field (where a <c>fixed</c> buffer is
/// restricted to primitives), with access decayed to the element pointer
/// (<c>(T*)&amp;field</c>) so the struct-hack over-indexing + array→pointer decay
/// stay faithful. End-to-end behavior is in the <c>struct-hack-array/</c> fixture.
/// </summary>
public sealed class StructHackArrayTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sha-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void struct_element_array_member_uses_inline_array()
    {
        var src = WriteTemp("""
            typedef struct Pt { int x; int y; } Pt;
            typedef struct Poly { int n; Pt pts[1]; } Poly;
            int main(void) { Poly p; p.pts[0].x = 1; return p.pts[0].x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("InlineArray(1)");
            emitted.ShouldContain("Pt __e0;");
            // primitive members are still plain fields, not inline arrays
            emitted.ShouldContain("public int");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void inline_array_member_access_decays_to_element_pointer()
    {
        // Over-index access must go through `(Pt*)&...`, not the bounds-checked
        // InlineArray indexer.
        var src = WriteTemp("""
            typedef struct Pt { int x; } Pt;
            typedef struct Poly { int n; Pt pts[1]; } Poly;
            int get(Poly *p, int i) { return p->pts[i].x; }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(Pt*)&");   // decayed element pointer
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void pointer_element_array_member_stores_as_nint()
    {
        // A pointer element can't be an InlineArray element (CS9184), so the
        // storage is `nint` while access uses the real element pointer.
        var src = WriteTemp("""
            typedef struct U { int v; } U;
            typedef struct Bag { int n; U *items[1]; } Bag;
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("nint __e0;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void inline_array_member_sizeof_is_count_times_element()
    {
        var src = WriteTemp("""
            typedef struct Pt { int x; int y; } Pt;
            typedef struct Poly { int n; Pt pts[3]; } Poly;
            int main(void) { Poly p; return (int)sizeof(p.pts); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("3 * sizeof(Pt)");   // not sizeof(wrapper)
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void tagged_named_nested_member_emits_under_tag_name()
    {
        // `union Node { struct NodeKey { … } u; }` — Lua's shape. The tag becomes
        // a real C# type and the member is a field of it.
        var src = WriteTemp("""
            typedef union Node {
                long whole;
                struct NodeKey { int a; int b; } u;
            } Node;
            int main(void) { Node n; n.u.a = 5; return n.u.a; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("struct NodeKey");
            emitted.ShouldContain("NodeKey u;");
            emitted.ShouldContain("n.u.a");
        }
        finally { File.Delete(src); }
    }
}
