#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the block-comment lexer rule. The canonical C-comment regex
/// (<c>/\*([^*]|\*+[^*/])*\*+/</c>) must treat a RUN of stars before the slash as
/// the comment terminator, not its body — so a comment closed with <c>**/</c>
/// (Lua's pervasive doc-comment style) ends there. The earlier
/// <c>(\*[^/][^*]*)*</c> form mis-scanned <c>**/</c> (it ate the first star as
/// <c>\*</c>, the second as <c>[^/]</c>, then the slash fell into <c>[^*]*</c>),
/// so the comment ran on to the NEXT <c>*/</c>, silently swallowing the code
/// between. End-to-end in the <c>block-comment-close/</c> fixture.
/// </summary>
[Collection("BlockComment")]
public sealed class BlockCommentTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-bc-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Doc_comment_closed_with_double_star_slash_does_not_swallow_following_code()
    {
        // The `**/` close must end the comment. If it didn't, the comment would
        // run to the trailing `/* end */` and eat `after` + `main` entirely.
        var src = WriteTemp("""
            int before(void) { return 1; }
            /*
            ** doc comment, closed with a star-run then slash
            **/
            int after(int x) { return x; }
            int main(void) { return after(2) - before(); }  /* end */
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int after(int x)");
            emitted.ShouldContain("static unsafe int before()");
            emitted.ShouldContain("static unsafe int main");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void One_line_double_star_doc_comment_closes()
    {
        // `/** … **/` on one line — leading and trailing double-star.
        var src = WriteTemp("""
            /** a one-liner doc **/
            int main(void) { return 0; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("static unsafe int main");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void All_stars_comment_closes()
    {
        // `/***/` is a valid (empty) comment — a run of stars then slash.
        var src = WriteTemp("""
            /***/
            int main(void) { return 7; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("return 7");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Star_inside_comment_body_is_not_a_terminator()
    {
        // A lone `*` not followed by `/` stays in the body — regression guard
        // that the new regex still scans interior stars.
        var src = WriteTemp("""
            int main(void) { /* a * b ** c */ return 3; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("return 3");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Plain_block_comment_still_works()
    {
        // Regression guard for the ordinary `/* … */` form.
        var src = WriteTemp("""
            int main(void) { /* discard */ int x = 5; return x; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("int x = 5");
        }
        finally { File.Delete(src); }
    }
}
