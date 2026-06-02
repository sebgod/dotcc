#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for a comma whose LEADING operand is a void CALL — `(voidcall, v)`.
/// The void result can't be a C# tuple element (CS8210). In a value position that
/// can't hoist statements (deep inside a short-circuiting condition), it lowers to
/// an immediately-invoked delegate that runs the call then a tuple picks the value
/// (so C# infers its type). A discard / statement context still splits to plain
/// statements (via CommaOps), and a normal non-void comma stays the plain tuple.
/// End-to-end (incl. short-circuit) in the <c>comma-void-call/</c> fixture.
/// </summary>
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
        // The void call runs inside a delegate; a tuple picks the value (type inferred).
        emitted.ShouldContain("System.Func<int>)(() =>");
        emitted.ShouldContain("note(lp)");
        emitted.ShouldContain(").Item2");
    }

    [Fact]
    public void void_leading_comma_inside_short_circuit_condition_lowers_in_place()
    {
        // The comma sits in an `&&` — hoisting would break short-circuit, so it must
        // stay an in-place delegate (no statement lift out of the condition).
        var emitted = Emit("""
            static void note(int* p) { (*p)++; }
            int main(void) { int log = 0; int* lp = &log;
                if (log == 0 && (note(lp), 1)) { return 1; } return 0; }
            """);
        emitted.ShouldContain("System.Func<int>)(() =>");
        emitted.ShouldContain("note(lp)");
    }

    [Fact]
    public void normal_non_void_comma_stays_a_plain_tuple()
    {
        // Guard: a comma with no void operand must NOT grow a delegate.
        var emitted = Emit("""
            int main(void) { int x = 1, y = 2; int z = (x, y); return z; }
            """);
        emitted.ShouldContain("(x, y).Item2");
        emitted.ShouldNotContain("System.Func");
    }
}
