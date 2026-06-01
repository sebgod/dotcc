#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the tag-vs-typedef-name namespace fix in
/// <see cref="TypeNameRewriter"/>: an identifier right after
/// <c>struct</c>/<c>union</c>/<c>enum</c> is a tag and must stay an <c>ID</c>,
/// even when it collides with a known typedef-name — the <c>typedef struct Foo
/// Foo;</c> idiom. End-to-end behavior is in the <c>typedef-tag-collision/</c>
/// fixture.
/// </summary>
public sealed class TypedefTagTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tag-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void typedef_struct_self_named_idiom_parses()
    {
        // `typedef struct Foo Foo;` then `struct Foo { … }` — the definition's
        // tag must not be seen as the just-registered typedef-name.
        var src = WriteTemp("""
            typedef struct State State;
            struct State { int top; };
            static int peek(struct State *s) { return s->top; }
            int main(void) { struct State st; st.top = 9; return peek(&st); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct State");
            emitted.ShouldContain("int peek(");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void forward_typedef_before_definition_parses()
    {
        // Forward typedef-name, used as a type BEFORE the struct body exists,
        // then the tag definition — both must resolve.
        var src = WriteTemp("""
            typedef struct Node Node;
            static int first(Node *n);
            struct Node { int v; };
            static int first(Node *n) { return n->v; }
            int main(void) { Node n; n.v = 5; return first(&n); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct Node");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void union_and_enum_tag_typedef_collisions_parse()
    {
        var src = WriteTemp("""
            typedef union U U;
            union U { int i; float f; };
            typedef enum E E;
            enum E { A, B };
            int main(void) { union U u; u.i = 1; enum E e = B; return u.i + (int)e; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("U");
            emitted.ShouldContain("enum E");
        }
        finally { File.Delete(src); }
    }
}
