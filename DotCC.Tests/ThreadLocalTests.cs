#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C11 `_Thread_local` (C23 `thread_local`) and Zig
/// `threadlocal var` — thread storage duration, lowered to `[ThreadStatic]` on
/// the emitted DotCcGlobals field (the marker rides `Symbol.IsThreadLocal`, set
/// by the spec resolution / the Zig container-var lowering). V1 constraints,
/// all loud: file-scope only (block scope rejected, even `static _Thread_local`
/// which C allows), scalars only on the Zig side. A non-zero initializer is set
/// on each thread's first access behind a ref-returning property (a .NET
/// [ThreadStatic] field initializer would run on the first thread only), and
/// `NULL` is a zero initializer (GH #247; also `thread-local-init/`, gcc-matched).
/// End-to-end in the `c11-thread-local/` fixture (gcc `-pthread` oracle) and
/// the `threadlocal_var` Zig oracle program.
/// </summary>
[Collection("ThreadLocal")]
public sealed class ThreadLocalTests
{
    private static string WriteTemp(string body, string ext = "c")
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tls-{System.Guid.NewGuid():N}.{ext}");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Thread_local_global_gets_thread_static_attribute()
    {
        var src = WriteTemp("""
            _Thread_local int tls_count;
            static _Thread_local long tls_static;
            int main(void) { tls_count = 1; return tls_count - 1 + (int)tls_static; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("[ThreadStatic]\n    public static int tls_count;");
            emitted.ShouldContain("[ThreadStatic]\n    public static long tls_static;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void A_zero_or_null_initializer_is_the_thread_static_default()
    {
        // Every thread's [ThreadStatic] slot starts zeroed, so a zero initializer,
        // NULL included (the null pointer constant `((void *)0)`), needs nothing more.
        var src = WriteTemp("""
            #include <stddef.h>
            _Thread_local int a = 0;
            _Thread_local int *p = NULL;
            int main(void) { return a + (p != 0); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("[ThreadStatic]\n    public static int a = 0;");
            emitted.ShouldContain("[ThreadStatic]\n    public static unsafe int* p = null;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void A_nonzero_initializer_is_set_on_each_threads_first_access()
    {
        // A [ThreadStatic] field initializer would run on the first thread only; C
        // gives every thread's instance the initial value (C11 6.2.4p4).
        var src = WriteTemp("_Thread_local int b = 7; int main(void) { int *q = &b; return b + *q; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("[ThreadStatic] private static unsafe int __tls_b;");
            emitted.ShouldContain("[ThreadStatic] private static bool __tls_b_set;");
            emitted.ShouldContain("public static unsafe ref int b");
            emitted.ShouldContain("if (!__tls_b_set) { __tls_b = 7; __tls_b_set = true; }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Block_scope_thread_local_is_rejected()
    {
        // C allows `static _Thread_local` at block scope; dotcc lowers
        // thread-locals as file-scope [ThreadStatic] fields only — loud V1 cut.
        var src = WriteTemp("int main(void) { static _Thread_local int x; return x; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("'_Thread_local' at block scope is not supported");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lowercase_thread_local_works_via_threads_h_and_c23_keyword()
    {
        // C11 <threads.h> supplies the macro (withdrawn under c23, where rule-2
        // promotion makes the lowercase spelling a first-class keyword) — the
        // identical source composes under every dialect from c11 on.
        var src = WriteTemp("""
            #include <threads.h>
            thread_local int tls_v;
            int main(void) { tls_v = 1; return tls_v - 1; }
            """);
        try
        {
            foreach (var std in new[] { "c11", "c17", "c23" })
            {
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse(std))
                    .ShouldContain("[ThreadStatic]\n    public static int tls_v;");
            }
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Thread_local_gated_as_c11_under_pedantic()
    {
        var src = WriteTemp("_Thread_local int x; int main(void) { return x; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), warnings: WarningFlags.Default | WarningFlags.PedanticErrors))
                .Message.ShouldContain("_Thread_local");
        }
        finally { File.Delete(src); }
    }

    // ---- the Zig twofer: `threadlocal var` ---------------------------------

    [Fact]
    public void Zig_threadlocal_var_gets_thread_static_attribute()
    {
        var src = WriteTemp("threadlocal var tl: i32 = 0;\npub fn main() u8 { tl = 42; return @intCast(tl); }\n", "zig");
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("[ThreadStatic]\n    public static int tl = 0;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Zig_function_local_threadlocal_is_rejected()
    {
        // The shared VarDecl nonterminal lets it parse; real zig rejects it at
        // parse time ("expected statement, found 'threadlocal'") — dotcc rejects
        // at lowering with a matching constraint.
        var src = WriteTemp("pub fn main() u8 {\n    threadlocal var x: i32 = 0;\n    x = 1;\n    return @intCast(x);\n}\n", "zig");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("'threadlocal' is only allowed on a container-level `var`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Zig_nonzero_threadlocal_initializer_is_set_per_thread()
    {
        var src = WriteTemp("threadlocal var tl: i32 = 7;\npub fn main() u8 { return @intCast(tl); }\n", "zig");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("public static unsafe ref int tl");
            emitted.ShouldContain("if (!__tls_tl_set) { __tls_tl = 7; __tls_tl_set = true; }");
        }
        finally { File.Delete(src); }
    }
}
