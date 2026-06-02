#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// <c>&lt;stdbool.h&gt;</c> is vestigial in C23: <c>bool</c>/<c>true</c>/<c>false</c>
/// are first-class keywords, so the header must NOT define them as macros (that
/// would shadow the keywords and make <c>#ifdef bool</c> wrongly true). dotcc's
/// synthetic header gates the macro bodies on <c>__STDC_VERSION__ &lt; 202311L</c>,
/// matching gcc; only <c>__bool_true_false_are_defined</c> survives in C23.
/// End-to-end in the <c>stdbool-c23/</c> fixture.
/// </summary>
public sealed class StdboolGatingTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sb-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void bool_macro_gated_off_under_c23_but_present_pre_c23()
    {
        // `#ifdef bool` is false under c23 (keyword, no macro), true pre-C23.
        var src = WriteTemp("""
            #include <stdbool.h>
            #ifdef bool
            int marker = 17;
            #else
            int marker = 23;
            #endif
            int main(void) { return marker; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23")).ShouldContain("marker = 23");
            Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17")).ShouldContain("marker = 17");
            Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90")).ShouldContain("marker = 17");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void bool_keyword_still_works_with_header_included_under_c23()
    {
        // The gated-off macros don't shadow the keywords; the rewriter promotes
        // bare `bool` -> `_Bool` (-> CBool) even with the header included.
        var src = WriteTemp("""
            #include <stdbool.h>
            int main(void) { bool b = true; return b == false; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23")).ShouldContain("CBool");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void bool_true_false_are_defined_macro_survives_in_c23()
    {
        // The one macro C23 keeps (deprecated) — defined in every dialect.
        var src = WriteTemp("""
            #include <stdbool.h>
            #ifdef __bool_true_false_are_defined
            int marker = 1;
            #else
            int marker = 0;
            #endif
            int main(void) { return marker; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23")).ShouldContain("marker = 1");
            Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17")).ShouldContain("marker = 1");
        }
        finally { File.Delete(src); }
    }
}
