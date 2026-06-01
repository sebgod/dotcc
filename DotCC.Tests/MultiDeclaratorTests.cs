#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for multi-declarator lists with per-declarator pointers —
/// <c>int *a, *b;</c> / <c>int *c, d;</c> — at block scope and as struct members
/// (the per-declarator <c>*</c> rides <c>DeclItemTail</c>; the first declarator's
/// <c>*</c>s are absorbed into Type). A uniform list stays one C# multi-declarator;
/// a mixed one splits. End-to-end in the <c>multi-declarator/</c> fixture.
/// </summary>
public sealed class MultiDeclaratorTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-md-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void uniform_pointer_list_stays_one_declaration()
    {
        var src = WriteTemp("""
            int main(void) { int x=1, y=2; int *a=&x, *b=&y; return *a + *b; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // both pointers → a single `int* a = …, b = …;`
            emitted.ShouldContain("int* a =");
            emitted.ShouldContain(", b =");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void mixed_pointer_nonpointer_list_splits()
    {
        var src = WriteTemp("""
            int main(void) { int x=1; int *c=&x, d=99; return *c + d; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // c is int*, d is int → separate declarations (C# binds * to the type)
            emitted.ShouldContain("int* c =");
            emitted.ShouldContain("int d = 99");
            // not joined into `int* c = …, d = …` (which would make d int* in C#)
            emitted.ShouldNotContain("int* c = (&x), d");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void multi_declarator_struct_members_with_per_declarator_pointers()
    {
        // Lua's `struct CallInfo *previous, *next;` shape.
        var src = WriteTemp("""
            struct Mixed { int a, b; int *p, q; };
            int main(void) { struct Mixed m; m.a=1; m.p=&m.a; m.q=2; return m.q; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("public int a;");
            emitted.ShouldContain("public int b;");
            emitted.ShouldContain("public int* p;");
            emitted.ShouldContain("public int q;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void plain_multi_declarator_unchanged()
    {
        // Regression guard for the existing no-pointer multi-declarator.
        var src = WriteTemp("""
            int main(void) { int a, b = 5, c; return a + b + c; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int a = default, b = 5, c = default");
        }
        finally { File.Delete(src); }
    }
}
