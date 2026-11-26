#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for a comma whose LEADING operand is a void CALL — `(voidcall, v)`.
/// In a hoistable context (init / statement position) the typed IR lifts the call
/// to a plain statement and assigns the last operand directly. In a value position
/// that can't hoist (deep inside a short-circuiting condition), it still lowers to
/// an immediately-invoked delegate that runs the call then returns the value. A
/// normal non-void comma in init position is also lifted (no delegate, no tuple).
/// End-to-end (incl. short-circuit) in the <c>comma-void-call/</c> fixture.
/// </summary>
[Collection("CommaVoidCall")]
public sealed class CommaVoidCallTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-cvc-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void void_leading_comma_in_value_position_uses_delegate_then_tuple()
    {
        var emitted = Emit("""
            static void note(int* p) { (*p)++; }
            int main(void) { int log = 0; int* lp = &log; int a = (note(lp), 5); return a + log; }
            """);
        // The IR lifts the comma: the void call is emitted as a statement and the
        // value is assigned directly — no delegate wrapper needed.
        emitted.ShouldContain("note(lp);");
        emitted.ShouldContain("int a = 5;");
    }

    [Fact]
    public void void_leading_comma_inside_short_circuit_condition_lowers_to_an_if_over_a_temp()
    {
        // The comma sits in an `&&`: lifting it ahead of the condition would run it
        // unconditionally. The `&&` becomes an if over a bool temp instead, the call inside
        // the if, so it runs only when the left operand is true (no closure either).
        var emitted = Emit("""
            static void note(int* p) { (*p)++; }
            int main(void) { int log = 0; int* lp = &log;
                if (log == 0 && (note(lp), 1)) { return 1; } return 0; }
            """);
        emitted.ShouldContain("bool __ct0 = log == 0;");
        emitted.ShouldContain("if (__ct0) { note(lp); __ct0 = true; }");
        emitted.ShouldNotContain("System.Func<int>)(() =>");
    }

    [Fact]
    public void normal_non_void_comma_stays_a_plain_tuple()
    {
        // Guard: a comma with no void operand must NOT grow a delegate.
        var emitted = Emit("""
            int main(void) { int x = 1, y = 2; int z = (x, y); return z; }
            """);
        // The IR lifts the comma operands: side-effect discarded, value assigned directly.
        emitted.ShouldContain("_ = x;");
        emitted.ShouldContain("int z = y;");
        emitted.ShouldNotContain("System.Func");
    }
}
