#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emitter fixes for expressions that aren't valid C# STATEMENTS and so must be
/// restructured at statement level — surfaced compiling the whole Lua program
/// (the emit-only probe never compiled it). Covers: a VOID-typed ternary used as
/// a statement (Lua's GC write-barriers) → <c>if</c>/<c>else</c>; a braceless
/// control-flow body that's a multi-statement comma → block-wrapped; and the
/// <c>setjmp</c>-&gt;try/catch rewrite bracing a single-statement try body.
/// End-to-end in <c>void-ternary-stmt/</c>, <c>braceless-comma-body/</c>,
/// <c>setjmp-try-body/</c>.
/// </summary>
public sealed class StatementLoweringTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sl-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void void_ternary_statement_lowers_to_if_else()
    {
        // `(cond ? voidcall() : (void)0);` — a void `?:` can't be a C# expression.
        var emitted = Emit("""
            static void act(int *p) { *p += 1; }
            int main(void) {
                int n = 0;
                ((n == 0) ? act(&n) : ((void)(0)));
                return n;
            }
            """);
        emitted.ShouldContain("if (Cond.B(");
        emitted.ShouldContain("act((&n));");
        // no literal void cast left as a (sub)expression
        emitted.ShouldNotContain("? act");   // not emitted as a C# ternary
    }

    [Fact]
    public void nested_void_ternary_statement_lowers_to_nested_if()
    {
        var emitted = Emit("""
            static void act(int *p) { *p += 1; }
            int main(void) {
                int n = 0;
                ((1) ? ((1) ? act(&n) : ((void)(0))) : ((void)(0)));
                return n;
            }
            """);
        // outer if with a nested if in its then-branch (>= 2 `if (Cond.B(` —
        // only user conditionals use Cond.B, so this counts the lowered ternaries)
        emitted.ShouldContain("act((&n));");
        emitted.Split("if (Cond.B(").Length.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void braceless_comma_body_is_block_wrapped()
    {
        // `if (c) (a=1, b=2); else …` — the comma expands to two statements; without
        // a block the `else` would detach ('else' cannot start a statement).
        var emitted = Emit("""
            int main(void) {
                int x = 0, y = 0, n = 0;
                if (n == 0) (x = 1, y = 2); else n = 9;
                return x + y + n;
            }
            """);
        // the comma body is wrapped so the if takes the whole block
        emitted.ShouldContain("if (Cond.B(((CBool)(n == 0)))) {");
        emitted.ShouldContain("else");
    }

    [Fact]
    public void setjmp_try_body_is_braced()
    {
        // `if (setjmp(env) == 0) stmt;` — the try BODY must be a block (`try stmt;`
        // is invalid C#). Lua's LUAI_TRY.
        var emitted = Emit("""
            #include <setjmp.h>
            static jmp_buf env;
            static void g(void) { }
            int main(void) {
                if (setjmp(env) == 0) g(); else return 1;
                return 0;
            }
            """);
        emitted.ShouldContain("try {");
        emitted.ShouldNotContain("try g(");
    }
}
