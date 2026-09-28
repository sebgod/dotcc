#nullable enable

using System;
using System.IO;
using System.Linq;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #223: a <c>goto</c> may jump into a loop body (C11 6.8.6.1; CPython's dtoa.c
/// jumps from one digit loop into another at <c>bump_up</c>). C# scopes a label to
/// its block, so GotoScopeNormalizer lowers the loop that must be entered to labels
/// and gotos at its own level (its own <c>break</c> / <c>continue</c> become gotos),
/// then hoists the label out of the now-plain body block. End-to-end in
/// <c>goto-into-loop/</c> (gcc-matched).
/// </summary>
[Collection("GotoIntoLoop")]
public sealed class GotoIntoLoopTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-gotoloop-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    /// <summary>The emitted method whose signature contains <paramref name="signature"/>,
    /// up to its closing brace.</summary>
    private static string Method(string emitted, string signature)
    {
        var start = emitted.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"no method '{signature}' in the emit");
        var end = emitted.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return emitted.Substring(start, end - start);
    }

    [Fact]
    public void A_goto_into_a_while_body_lowers_the_loop_to_labels()
    {
        var method = Method(Emit("""
            static int into_while(int n)
            {
                int total = 0;
                goto middle;
                while (n > 0) {
                    total += n;
                  middle:
                    n--;
                }
                return total;
            }
            int main(void) { return into_while(4); }
            """), "int into_while(int n)");
        method.ShouldNotContain("while (");
        method.ShouldContain("__loop0_top:");
        method.ShouldContain("goto __loop0_end;");
        method.ShouldContain("goto __loop0_top;");
        method.ShouldContain("middle:");
    }

    [Fact]
    public void The_loops_own_break_and_continue_become_gotos()
    {
        var method = Method(Emit("""
            static int into_for(int start)
            {
                int i, steps = 0;
                if (start > 0) { i = start; goto inside; }
                for (i = 0; i < 5; i++) {
                    steps += 10;
                  inside:
                    steps++;
                    if (i == 3) { continue; }
                    if (i == 4) { break; }
                }
                return steps * 100 + i;
            }
            int main(void) { return into_for(2); }
            """), "int into_for(int start)");
        method.ShouldNotContain("for (");
        method.ShouldNotContain("continue;");
        method.ShouldNotContain("break;");
        method.ShouldContain("goto __loop0_cont;");
        method.ShouldContain("__loop0_cont:");
        method.ShouldContain("goto __loop0_end;");
    }

    [Fact]
    public void A_nested_switch_keeps_its_break_and_a_nested_loop_keeps_both()
    {
        var method = Method(Emit("""
            static int f(int start)
            {
                int i = 0, j, acc = 0;
                if (start) { goto again; }
                for (; i < 6; i++) {
                    switch (i % 3) {
                    case 0: acc += 1; break;
                    case 1: acc += 10; continue;
                    default: acc += 100; break;
                    }
                    for (j = 0; j < 2; j++) { if (j) { break; } acc++; }
                  again:
                    acc += 1000;
                }
                return acc;
            }
            int main(void) { return f(1); }
            """), "int f(int start)");
        method.ShouldContain("case 1:\n                    acc += 10;\n                    goto __loop0_cont;");
        method.ShouldContain("acc += 100;\n                    break;");
        method.ShouldContain("for (j = 0; Cond.B(((CBool)(j < 2))); j++)");
    }

    [Fact]
    public void A_label_lifted_through_several_blocks_gets_a_distinct_skip_label_per_level()
    {
        // dtoa's shape: the label sits two ifs deep in one loop's body and is jumped to
        // from another loop, so it is lifted through four blocks (CS0140 when every
        // level reused `__skip_bump_up`).
        var method = Method(Emit("""
            static int digits(char *buf, int ilim, int fast)
            {
                char *s = buf;
                int i;
                if (fast) {
                    for (i = 0;;) { *s++ = '7'; if (++i >= ilim) { goto bump_up; } }
                }
                else {
                    for (i = 1;; i++) {
                        *s++ = '9';
                        if (i == ilim) {
                            if (ilim > 1) {
                              bump_up:
                                while (*--s == '9') if (s == buf) { *s = '0'; break; }
                                ++*s++;
                            }
                            break;
                        }
                    }
                }
                *s = 0;
                return (int)(s - buf);
            }
            int main(void) { char b[8]; return digits(b, 2, 1); }
            """), "int digits(byte* buf, int ilim, int fast)");
        var labels = System.Text.RegularExpressions.Regex.Matches(method, @"^\s*(__\w+):", System.Text.RegularExpressions.RegexOptions.Multiline);
        labels.Count.ShouldBeGreaterThan(2);
        labels.Select(m => m.Groups[1].Value).ShouldBeUnique();
    }

    [Fact]
    public void A_for_whose_init_declares_a_variable_is_rejected_loudly()
        => Should.Throw<CompileException>(() => Emit("""
            int main(void)
            {
                int n = 0;
                goto in;
                for (int i = 0; i < 3; i++) {
                  in:
                    n++;
                }
                return n;
            }
            """)).Message.ShouldContain("a `for` with a declaration in its init");
}
