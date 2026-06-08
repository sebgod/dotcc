#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// The operand of a postfix <c>.</c> / <c>-&gt;</c> must be a primary/postfix
/// expression. A COMPOUND base — pointer arithmetic (<c>p - 1</c>), a deref
/// (<c>*pp</c>), or a cast (<c>(S*)x</c>) — must keep parens so the member
/// operator binds to the whole base, not its trailing operand: <c>(p - 1)-&gt;v</c>
/// must not emit as <c>p - 1-&gt;v</c> (parsed <c>p - (1-&gt;v)</c>). A bare
/// identifier / member chain / call / index stays unwrapped. Lua's
/// <c>getlastfree</c> macro <c>((cast(Limbox*, (t)-&gt;node) - 1)-&gt;lastfree)</c>
/// is the motivating case. End-to-end in <c>ptr-arith-arrow/</c>.
/// </summary>
public sealed class PostfixBaseTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-pb-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Emit(string body)
    {
        var src = WriteTemp(body);
        try { return Compiler.EmitCSharp(new[] { src }); }
        finally { File.Delete(src); }
    }

    [Fact]
    public void pointer_arithmetic_arrow_base_keeps_parens()
    {
        var emitted = Emit("""
            struct S { int v; };
            int main(void) { struct S a[3]; struct S *p = &a[2]; return (p - 1)->v; }
            """);
        emitted.ShouldContain("(p - 1)->v");
        emitted.ShouldNotContain("p - 1->v");
    }

    [Fact]
    public void deref_arrow_base_keeps_parens()
    {
        var emitted = Emit("""
            struct S { int v; };
            int main(void) { struct S x; struct S *p = &x; struct S **pp = &p; return (*pp)->v; }
            """);
        emitted.ShouldContain("(*pp)->v");
    }

    [Fact]
    public void cast_arrow_base_keeps_parens()
    {
        // `(S*)raw->v` would parse as `(S*)(raw->v)`; the cast base needs wrapping.
        var emitted = Emit("""
            struct S { int v; };
            int main(void) { struct S x; void *raw = &x; return ((struct S*)raw)->v; }
            """);
        emitted.ShouldContain("((S*)raw)->v");
    }

    [Fact]
    public void simple_arrow_base_stays_unwrapped()
    {
        // A bare pointer variable needs no parens — and the malloc-promote keying
        // matches on the unwrapped name.
        var emitted = Emit("""
            struct S { int v; };
            int main(void) { struct S x; struct S *p = &x; p->v = 1; return p->v; }
            """);
        emitted.ShouldContain("p->v");
        emitted.ShouldNotContain("(p)->v");
    }

    [Fact]
    public void member_chain_dot_base_stays_unwrapped()
    {
        // A member chain is already postfix-safe — `a.b.c`, not `(a.b).c`.
        var emitted = Emit("""
            struct Inner { int c; };
            struct Outer { struct Inner b; };
            int main(void) { struct Outer a; a.b.c = 7; return a.b.c; }
            """);
        emitted.ShouldContain("a.b.c");
    }

    [Fact]
    public void postfix_incr_on_deref_keeps_parens()
    {
        // `(*p)++` increments the POINTEE. Dropping parens -> `*p++` = `*(p++)`,
        // which increments the POINTER (wrong) and isn't a C# statement-expression
        // (CS0201). Same compound-base rule as the `.`/`->` cases above.
        var emitted = Emit("""
            int main(void) { int n = 5; int *p = &n; (*p)++; (*p)--; return n; }
            """);
        // Statement context strips the redundant OUTER wrap, leaving the inner
        // base-protecting parens: `(*p)++`, never `*p++` (= `*(p++)`).
        emitted.ShouldContain("(*p)++");
        emitted.ShouldContain("(*p)--");
    }

    [Fact]
    public void genuine_pointer_post_increment_is_unwrapped()
    {
        // `*p++` (read `*p`, then advance the pointer — the common C idiom) is NOT
        // a postfix-on-deref: the `++` applies to the bare pointer `p`, which stays
        // unwrapped. C# shares the same precedence, so the IR emits `*p++` directly.
        // Regression guard that the fix above didn't over-wrap.
        var emitted = Emit("""
            int main(void) { int a[2] = {1,2}; int *p = a; int x = *p++; return x; }
            """);
        emitted.ShouldContain("*p++");
        emitted.ShouldNotContain("(*p)++");
    }
}
