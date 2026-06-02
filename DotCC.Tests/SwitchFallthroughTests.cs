#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// C switch fall-through: a case section that doesn't end in <c>break</c> /
/// <c>return</c> / … falls into the next case. C# forbids implicit fall-through
/// (CS0163) and forbids the final case falling out of the switch (CS8070). dotcc
/// analyses the switch body's statement pieces — grouping case sections and
/// checking whether each section's last piece terminates — and inserts the
/// explicit jump C performs: <c>goto case &lt;next&gt;;</c> / <c>goto default;</c>,
/// or a trailing <c>break;</c> on the final section. A section that already
/// terminates is left alone. End-to-end in the <c>switch-fallthrough/</c> fixture.
/// </summary>
public sealed class SwitchFallthroughTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-swf-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void non_terminating_case_gets_goto_next()
    {
        var src = WriteTemp("""
            int f(int x) {
                int r = 0;
                switch (x) {
                    case 1: r += 1;
                    case 2: r += 10; break;
                }
                return r;
            }
            int main(void) { return f(1); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("goto case 2;");
            // The terminating case keeps its break, gets no goto.
            emitted.ShouldContain("r += 10");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void fall_through_into_default_uses_goto_default()
    {
        var src = WriteTemp("""
            int f(int x) {
                int r = 0;
                switch (x) {
                    case 3: r += 100;
                    default: r += 1000;
                }
                return r;
            }
            int main(void) { return f(3); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("goto default;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void final_case_falling_out_gets_trailing_break()
    {
        var src = WriteTemp("""
            int f(int x) {
                int r = 0;
                switch (x) { default: r += 1; }
                return r;
            }
            int main(void) { return f(0); }
            """);
        try
        {
            // The default body has no break; the final section gets one appended.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("default:");
            emitted.ShouldContain("break;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void terminating_cases_get_no_goto()
    {
        var src = WriteTemp("""
            int f(int x) {
                switch (x) {
                    case 1: return 10;
                    case 2: return 20;
                    default: return 0;
                }
            }
            int main(void) { return f(1); }
            """);
        try
        {
            // Every section returns → no fall-through jumps inserted.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldNotContain("goto case");
            emitted.ShouldNotContain("goto default");
        }
        finally { File.Delete(src); }
    }
}
