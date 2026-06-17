#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C translation phase 2 — backslash-newline line continuation.
/// <see cref="Compiler.SpliceLineContinuations"/> removes a <c>\</c> that is
/// immediately followed by a newline before the source reaches the byte lexer,
/// so multi-line macros (ubiquitous in real headers — e.g. Lua's
/// <c>luaconf.h</c>) lex correctly. A <c>\</c> that is NOT a continuation stays
/// put and surfaces as a clean <see cref="CompileException"/> rather than an
/// unhandled lexer exception.
/// </summary>
[Collection("LineContinuation")]
public sealed class LineContinuationTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-cont-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    // ---- the splice transform itself -------------------------------------

    [Fact]
    public void splices_backslash_lf() =>
        Compiler.SpliceLineContinuations("a\\\nb").ShouldBe("ab");

    [Fact]
    public void splices_backslash_crlf() =>
        Compiler.SpliceLineContinuations("a\\\r\nb").ShouldBe("ab");

    [Fact]
    public void escaped_backslash_at_eol_leaves_one_backslash() =>
        // `\\` then newline: sequential, non-overlapping replace removes the
        // (backslash,newline) pair starting at the SECOND backslash, leaving one.
        Compiler.SpliceLineContinuations("a\\\\\nb").ShouldBe("a\\b");

    [Fact]
    public void leaves_a_non_continuation_backslash_untouched() =>
        // A `\` not before a newline (e.g. a stray, or `\` followed by a letter)
        // is preserved — it is the lexer's problem, not phase 2's.
        Compiler.SpliceLineContinuations("a \\ b").ShouldBe("a \\ b");

    [Fact]
    public void no_backslash_returns_input_unchanged()
    {
        const string s = "int main(void) { return 0; }\n";
        Compiler.SpliceLineContinuations(s).ShouldBeSameAs(s); // fast path: same reference
    }

    // ---- end to end ------------------------------------------------------

    [Fact]
    public void multiline_macro_compiles()
    {
        // The `\` at the end of the #define's first line is a real
        // backslash-newline (raw string literals keep it verbatim).
        var src = WriteTemp(""""
            #define ADD(a, b) \
                ((a) + (b))
            int main(void) { return ADD(40, 2); }
            """");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main");
            // The macro expanded across the continuation.
            emitted.ShouldContain("40");
            emitted.ShouldContain("2");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void stray_backslash_is_a_clean_compile_error()
    {
        var src = WriteTemp("int main(void) { int a = 1 \\ 2; return a; }");
        try
        {
            // A bare `\` (not a continuation, not in a literal) is an invalid
            // token — dotcc reports it as a CompileException ("lex failed"),
            // NOT an unhandled LALR.CC LexerException.
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("lex failed");
        }
        finally { File.Delete(src); }
    }
}
