#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <c>setjmp</c> in an <c>if</c> condition WITHOUT an <c>else</c>
/// clause — Lua's <c>LUAI_TRY</c> shape (<c>if (setjmp((c)->b) == 0) ((f)(L,
/// ud))</c>). The missing branch is empty: the try/catch rewrite still applies,
/// the absent side becomes an empty block. dotcc used to reject the no-else form
/// with a "requires a matching else" diagnostic. End-to-end in the
/// <c>setjmp-no-else/</c> fixture.
/// </summary>
public sealed class SetjmpNoElseTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sj-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void If_setjmp_eq_zero_without_else_lowers_to_try_empty_catch()
    {
        // `if (setjmp(env) == 0) { normal }` (no else) → try { normal } catch { }.
        // The catch is empty (the absent else = no recovery), so no
        // `__longjmp_value` is bound (it would be unused).
        var src = WriteTemp("""
            #include <setjmp.h>
            jmp_buf env;
            void worker(void);
            int main(void) {
                if (setjmp(env) == 0) { worker(); }
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("try {");
            emitted.ShouldContain("catch (Libc.LongJmpException __jmp) when (__jmp.Token == env)");
            // empty recovery — no value binding
            emitted.ShouldNotContain("var __longjmp_value");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void If_setjmp_truthy_without_else_lowers_to_empty_try_with_recovery_catch()
    {
        // `if (setjmp(env)) { recovery }` (no else): setjmp is truthy only on the
        // longjmp re-entry, so the body is recovery and the normal path is empty
        // → try { } catch { recovery }.
        var src = WriteTemp("""
            #include <setjmp.h>
            #include <stdio.h>
            jmp_buf env;
            int main(void) {
                if (setjmp(env)) { printf("recovered\n"); }
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The IR emits the empty try body on its own line: `try\n        { }`
            emitted.ShouldContain("try");
            emitted.ShouldContain("{ }");
            emitted.ShouldContain("catch (Libc.LongJmpException __jmp) when (__jmp.Token == env)");
            // The IR does not emit __longjmp_value; recovery reads the value via
            // the exception object directly.
            emitted.ShouldNotContain("var __longjmp_value");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void No_else_setjmp_no_longer_throws()
    {
        // Regression guard: the old behaviour was a CompileException demanding an
        // `else`. The no-else form must now compile.
        var src = WriteTemp("""
            #include <setjmp.h>
            jmp_buf env;
            void f(void);
            int main(void) { if (setjmp(env) == 0) f(); return 0; }
            """);
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { src }));
        }
        finally { File.Delete(src); }
    }
}
