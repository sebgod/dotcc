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
/// which C allows), zero/default initializer only (a .NET [ThreadStatic]
/// initializer runs on the first thread only), scalars only on the Zig side.
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
            emitted.ShouldContain("[ThreadStatic]\n    public static unsafe int tls_count;");
            emitted.ShouldContain("[ThreadStatic]\n    public static unsafe long tls_static;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Zero_initializer_is_allowed_nonzero_is_rejected()
    {
        // Zero-init matches the zero/default value .NET gives every thread's
        // slot anyway; a non-zero initializer would only reach the FIRST thread
        // ([ThreadStatic] semantics), so it is a loud compile error.
        var ok = WriteTemp("_Thread_local int a = 0; int main(void) { return a; }");
        var bad = WriteTemp("_Thread_local int b = 7; int main(void) { return b; }");
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { ok }));
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { bad }))
                .Message.ShouldContain("non-zero-initialized _Thread_local is not supported");
        }
        finally { File.Delete(ok); File.Delete(bad); }
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
                    .ShouldContain("[ThreadStatic]\n    public static unsafe int tls_v;");
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
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
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
                .ShouldContain("[ThreadStatic]\n    public static unsafe int tl = 0;");
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
    public void Zig_nonzero_threadlocal_initializer_is_rejected()
    {
        var src = WriteTemp("threadlocal var tl: i32 = 7;\npub fn main() u8 { return @intCast(tl); }\n", "zig");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("non-zero initializer is not supported");
        }
        finally { File.Delete(src); }
    }
}
