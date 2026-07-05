#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for VALUE-CAPTURING <c>setjmp</c> — the shapes that observe the value
/// <c>longjmp</c> passes (not just zero-vs-nonzero): <c>T r = setjmp(env);</c> /
/// <c>r = setjmp(env);</c> followed by a <c>switch</c>/<c>if</c> on <c>r</c>, and the bare
/// <c>switch (setjmp(env)) {…}</c>. Real C's "setjmp returns twice" is lowered to a faithful
/// goto-restart: the enclosing region re-runs from a synthetic label with <c>r</c> holding the
/// jump value each time a matching <c>longjmp</c> is caught. Plus the honesty pins: any setjmp
/// in a shape dotcc can't model (a loop/ternary condition, a nested sub-expression, a discarded
/// call) is a loud <see cref="CompileException"/>, never a silent lower-to-always-0. End-to-end
/// in the <c>setjmp-switch-value/</c> and <c>setjmp-capture-if/</c> fixtures.
/// </summary>
[Collection("SetjmpValueCapture")]
public sealed class SetjmpValueCaptureTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sjv-{System.Guid.NewGuid():N}.c");
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
    public void Decl_capture_then_switch_lowers_to_goto_restart()
    {
        // `int r = setjmp(env); switch (r) {…}` → reset r to 0, arm a fresh token, label the
        // try, and on a matching longjmp write the value into r and goto back — the body's
        // switch on r then reaches the recovery case.
        var emitted = Emit("""
            #include <setjmp.h>
            #include <stdio.h>
            static jmp_buf env;
            static void boom(void){ longjmp(env, 2); }
            int main(void){
                int r = setjmp(env);
                switch (r) {
                    case 0: boom(); break;
                    case 2: printf("got %d\n", r); break;
                    default: break;
                }
                return 0;
            }
            """);
        emitted.ShouldContain("int r = 0;");
        emitted.ShouldContain("env = new Libc.LongJmpToken();");
        emitted.ShouldContain("__setjmp_0:");
        emitted.ShouldContain("catch (Libc.LongJmpException __jmp) when (__jmp.Token == env)");
        emitted.ShouldContain("r = (int)__jmp.Value;");
        emitted.ShouldContain("goto __setjmp_0;");
    }

    [Fact]
    public void Bare_switch_on_setjmp_synthesizes_capture_var()
    {
        // `switch (setjmp(env)) {…}` has no user var, so a synthetic `__sjval0` is declared,
        // reset to 0, and the switch subject rewritten to it.
        var emitted = Emit("""
            #include <setjmp.h>
            #include <stdio.h>
            static jmp_buf env;
            static void boom(void){ longjmp(env, 7); }
            int main(void){
                switch (setjmp(env)) {
                    case 0: boom(); break;
                    case 7: printf("caught\n"); break;
                    default: break;
                }
                return 0;
            }
            """);
        emitted.ShouldContain("int __sjval0 = 0;");
        emitted.ShouldContain("__setjmp_0:");
        emitted.ShouldContain("switch (__sjval0)");
        emitted.ShouldContain("__sjval0 = (int)__jmp.Value;");
        emitted.ShouldContain("goto __setjmp_0;");
    }

    [Fact]
    public void Assign_capture_into_predeclared_var()
    {
        // `r = setjmp(env);` into a pre-declared simple var → reset `r = 0;` + goto-restart.
        var emitted = Emit("""
            #include <setjmp.h>
            #include <stdio.h>
            static jmp_buf env;
            static void boom(void){ longjmp(env, 3); }
            int main(void){
                int r;
                r = setjmp(env);
                if (r == 0) boom();
                printf("r=%d\n", r);
                return 0;
            }
            """);
        emitted.ShouldContain("r = 0;");
        emitted.ShouldContain("__setjmp_0:");
        emitted.ShouldContain("r = (int)__jmp.Value;");
        emitted.ShouldContain("goto __setjmp_0;");
    }

    [Fact]
    public void Classic_if_guard_still_lowers_to_setjmp_guard_not_goto_restart()
    {
        // Regression: the zero-vs-nonzero `if` guard keeps its simpler try/catch lowering —
        // no goto-restart label, no value binding.
        var emitted = Emit("""
            #include <setjmp.h>
            #include <stdio.h>
            static jmp_buf env;
            static void boom(void){ longjmp(env, 1); }
            int main(void){
                if (setjmp(env) == 0) { boom(); } else { printf("recovered\n"); }
                return 0;
            }
            """);
        emitted.ShouldContain("catch (Libc.LongJmpException __jmp) when (__jmp.Token == env)");
        emitted.ShouldNotContain("__setjmp_0:");
        emitted.ShouldNotContain("goto __setjmp_0;");
    }

    [Fact]
    public void Setjmp_in_while_condition_is_rejected()
    {
        var ex = Should.Throw<CompileException>(() => Emit("""
            #include <setjmp.h>
            static jmp_buf env;
            int main(void){ while (setjmp(env)) { } return 0; }
            """));
        ex.Message.ShouldContain("setjmp");
        ex.Message.ShouldContain("not supported");
    }

    [Fact]
    public void Bare_discarded_setjmp_is_rejected()
    {
        Should.Throw<CompileException>(() => Emit("""
            #include <setjmp.h>
            static jmp_buf env;
            int main(void){ setjmp(env); return 0; }
            """));
    }

    [Fact]
    public void Setjmp_in_nested_subexpression_is_rejected()
    {
        // `x = setjmp(env) + 1;` — the setjmp is buried in an arithmetic expression, not a
        // recognized capture, so it must fail loudly rather than lower to always-0 + 1.
        Should.Throw<CompileException>(() => Emit("""
            #include <setjmp.h>
            static jmp_buf env;
            int main(void){ int x = setjmp(env) + 1; return x; }
            """));
    }
}
