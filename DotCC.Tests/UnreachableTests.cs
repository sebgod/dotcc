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
    public void Unreachable_is_c23_only_bare_call_survives_before_c23()
    {
        // Pre-C23 the <stddef.h> macro is not defined, so `unreachable()` stays an
        // ordinary (undeclared) call at the site — the throw rewrite (which matches
        // the expanded `__dotcc_unreachable`) does NOT fire, so the bare `unreachable();`
        // call survives (and fails downstream at the C# stage, exactly as it would at
        // gcc's link step). A POSITIVE assertion, since a ShouldNotContain on the throw
        // would collide with the always-spliced runtime helper's own throw body.
        var src = WriteTemp(ValueFn);
        try
        {
            var emitted = Emit(src, "c17");
            // The bare call is present verbatim; the throw form contains no
            // `unreachable();`, and the helper definition is `__dotcc_unreachable() =>`
            // (no semicolon), so this uniquely identifies the un-rewritten call site.
            emitted.ShouldContain("unreachable();");
        }
        finally { File.Delete(src); }
    }
}
