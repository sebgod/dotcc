#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #219: a byte no lexer rule matches is a STRAY preprocessing token (C11 6.4p1's
/// "each non-white-space character that cannot be one of the above"), so a skipped
/// <c>#if</c> group may hold any text (CPython's <c># error C 'size_t' …</c> in a false
/// branch, JavaScript under <c>#ifdef __EMSCRIPTEN__</c>). One that survives
/// preprocessing is gcc's error. Multi-character constants take gcc's value.
/// </summary>
[Collection("Console")]
public sealed class StrayTokenTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-stray-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string Rejected(string body) => Should.Throw<CompileException>(() => Emit(body)).Message;

    /// <summary>What compiling <paramref name="body"/> writes to stderr (its warnings).</summary>
    private static string Stderr(string body)
    {
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { Emit(body); return sw.ToString(); }
        finally { Console.SetError(prior); }
    }

    [Fact]
    public void A_skipped_group_may_hold_any_text()
    {
        var emitted = Emit("""
            #if 0
            # error C 'size_t' size should be either 4 or 8!
            don't "unterminated
            EM_JS(int, f, (), { return `${x}` @ 1; });
            #endif
            int main(void) { return 0; }
            """);
        emitted.ShouldContain("int main()");
    }

    [Fact]
    public void A_stray_byte_in_an_unused_macro_body_is_harmless()
        => Emit("#define QUOTE '\nint main(void) { return 0; }\n").ShouldContain("int main()");

    [Theory]
    [InlineData("int main(void) { int a = 1 @ 2; return a; }", "1:28: error: stray '@' in program")]
    [InlineData("int main(void) { int a = 1 ` 2; return a; }", "stray '`' in program")]
    [InlineData("int main(void) { char c = 'x; return c; }", "1:27: error: missing terminating ' character")]
    [InlineData("int main(void) { const char *s = \"abc; return 0; }", "missing terminating \" character")]
    [InlineData("#define QUOTE '\nint main(void) { return QUOTE; }", "missing terminating ' character")]
    public void A_stray_byte_that_survives_preprocessing_is_gccs_error(string source, string message)
        => Rejected(source).ShouldContain(message);

    [Fact]
    public void A_multi_character_constant_has_gccs_value_and_warning()
    {
        const string source = "int x = 'ab';\nint y = '\\x41\\102';\nint main(void) { return 0; }\n";
        var emitted = Emit(source);
        emitted.ShouldContain("int x = 24930;");   // 0x6162, as gcc packs it
        emitted.ShouldContain("int y = 16706;");   // 'A' 'B' through a hex and an octal escape
        Stderr(source).ShouldContain("multi-character character constant [-Wmultichar]");
    }

    [Fact]
    public void A_constant_of_more_than_four_characters_keeps_the_last_four()
    {
        const string source = "int x = 'abcde';\nint main(void) { return 0; }\n";
        Emit(source).ShouldContain("int x = 1650680933;");   // 'b' 'c' 'd' 'e'
        Stderr(source).ShouldContain("character constant too long for its type");
    }

    [Fact]
    public void Preprocess_only_prints_a_stray_byte_as_itself()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-stray-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, "a @ b\n");
        try
        {
            var sw = new StringWriter();
            Compiler.Preprocess(new[] { path }, sw);
            sw.ToString().ShouldContain("a @ b");
        }
        finally { File.Delete(path); }
    }
}
