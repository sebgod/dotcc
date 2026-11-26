#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C23 `unreachable()` (&lt;stddef.h&gt; §7.21.1). dotcc gives the
/// undefined-behavior marker DEFINED behavior — a loud throw via the
/// `[DoesNotReturn]` runtime helper `__dotcc_unreachable` — and the backend
/// lowers a statement-position call to a real C# `throw` (not a plain call),
/// because C#'s CS0161 "not all code paths return" analysis only treats a
/// literal throw as a control-flow terminator. That lets `unreachable()` sit as
/// the "can't happen" arm of a value-returning function with no bogus return.
/// End-to-end (not-reached path) in the `c23-unreachable/` fixture.
/// </summary>
[Collection("Unreachable")]
public sealed class UnreachableTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-unreachable-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Emit(string src, string std = "c23") =>
        Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse(std));

    private const string ValueFn = """
        #include <stddef.h>
        int pick(int x) {
            switch (x) {
                case 0: return 1;
                default: unreachable();
            }
        }
        int main(void) { return pick(0) - 1; }
        """;

    [Fact]
    public void Statement_position_unreachable_lowers_to_a_throw()
    {
        var src = WriteTemp(ValueFn);
        try
        {
            var emitted = Emit(src);
            // The call becomes a real C# throw (the CS0161-satisfying terminator).
            emitted.ShouldContain("throw new System.Diagnostics.UnreachableException(\"unreachable() reached\")");
            // ...and NO plain call to the helper survives at the site. The runtime
            // helper's own definition is `__dotcc_unreachable() =>` (arrow, no `;`),
            // so this uniquely catches an un-rewritten statement-position call.
            emitted.ShouldNotContain("__dotcc_unreachable();");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Runtime_helper_is_marked_DoesNotReturn()
    {
        // The spliced runtime helper carries [DoesNotReturn] — it satisfies
        // definite-assignment (CS0165) at any non-statement-position use, and is
        // the resolution target the <stddef.h> prototype binds to.
        var src = WriteTemp(ValueFn);
        try
        {
            var emitted = Emit(src);
            emitted.ShouldContain("[System.Diagnostics.CodeAnalysis.DoesNotReturn]");
            emitted.ShouldContain("__dotcc_unreachable()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Unreachable_is_c23_only_an_undeclared_call_before_c23()
    {
        // Pre-C23 the <stddef.h> macro is not defined, so `unreachable()` is an ordinary
        // identifier no declaration names: a call to an undeclared function, which C99
        // removed. gcc 14 rejects it, and so does dotcc (it used to emit the call, which
        // then failed at the C# stage).
        var src = WriteTemp(ValueFn);
        try
        {
            Should.Throw<CompileException>(() => Emit(src, "c17"))
                .Message.ShouldContain("implicit declaration of function 'unreachable'");
        }
        finally { File.Delete(src); }
    }
}
