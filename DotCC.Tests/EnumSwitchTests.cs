#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A C <c>switch</c> is int-semantic — the controlling expression is
/// integer-promoted and case labels are converted to that type — so a switch on
/// a plain <c>int</c> may legally use enumerator case labels (Lua's lexer:
/// <c>switch (ls-&gt;t.token) { case TK_NAME: … }</c>). dotcc lowers enums to
/// real C# enums, which reject both <c>switch(int){ case Enum.X }</c> and
/// <c>switch(Enum){ case (int)… }</c>, so it decays the subject and the
/// enumerator case labels to <c>(int)</c> (uniform int = pure C semantics).
/// End-to-end in the <c>enum-switch-int/</c> fixture.
/// </summary>
public sealed class EnumSwitchTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-esw-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void enumerator_case_label_decays_to_int()
    {
        var src = WriteTemp("""
            enum Tok { TK_NAME = 257, TK_INT };
            int kind(int token) {
                switch (token) {
                    case TK_NAME: return 1;
                    case TK_INT:  return 2;
                    default:      return 0;
                }
            }
            int main(void) { return kind(257); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The enumerator case label is cast to its (int) constant value so it
            // matches the int-typed switch subject.
            emitted.ShouldContain("case (int)Tok.TK_NAME:");
            emitted.ShouldContain("case (int)Tok.TK_INT:");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void enum_typed_switch_subject_decays_to_int()
    {
        var src = WriteTemp("""
            enum Tok { TK_NAME = 257, TK_INT };
            int weight(enum Tok t) {
                switch (t) {
                    case TK_NAME: return 1;
                    case TK_INT:  return 2;
                    default:      return 0;
                }
            }
            int main(void) { return weight(TK_NAME); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Both the enum subject and the labels decay to int — uniform.
            emitted.ShouldContain("switch ((int)t)");
            emitted.ShouldContain("case (int)Tok.TK_NAME:");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void plain_int_switch_subject_is_untouched()
    {
        var src = WriteTemp("""
            int f(int x) {
                switch (x) {
                    case 1:  return 10;
                    default: return 0;
                }
            }
            int main(void) { return f(1); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // No enum involved → no (int) decay injected.
            emitted.ShouldContain("switch (x)");
            emitted.ShouldContain("case 1:");
        }
        finally { File.Delete(src); }
    }
}
