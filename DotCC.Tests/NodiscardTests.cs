#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the C23 <c>[[nodiscard]]</c> discarded-result warning
/// (gcc <c>-Wunused-result</c>). Discarding a non-void result of a
/// <c>[[nodiscard]]</c>-declared function in statement position warns (on by
/// default — the attribute's whole purpose); a <c>(void)f()</c> cast suppresses
/// it, exactly as in C. The marker rides <c>Symbol.Nodiscard</c> (set by the
/// attribute walk, shared between a prototype and its definition); the warning
/// is emitted by <see cref="IrBuilder"/> and flushed to stderr by the frontend.
/// Wording is gcc-verbatim. (Warnings land on stderr — captured here the same
/// way <see cref="ConstCheckTests"/> captures the const-discard warning.)
/// </summary>
[Collection("Nodiscard")]
public sealed class NodiscardTests
{
    /// <summary>Emit under c23 and capture stderr (where the nodiscard warning lands).
    /// The unit assembly is serialized (AssemblyInfo), so the process-global
    /// <c>Console.Error</c> swap is race-free.</summary>
    private static string Stderr(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-nodiscard-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            Compiler.EmitCSharp(new[] { path }, dialect: CDialect.Parse("c23"));
            return sw.ToString();
        }
        finally { Console.SetError(prior); File.Delete(path); }
    }

    [Fact]
    public void Discarding_a_nodiscard_result_warns_with_gcc_wording()
    {
        var stderr = Stderr("""
            [[nodiscard]] int must_use(void) { return 1; }
            int main(void) { must_use(); return 0; }
            """);
        stderr.ShouldContain("ignoring return value of 'must_use', declared with attribute 'nodiscard'");
    }

    [Fact]
    public void The_reason_string_is_appended()
    {
        var stderr = Stderr("""
            [[nodiscard("check the code")]] int with_reason(void) { return 2; }
            int main(void) { with_reason(); return 0; }
            """);
        stderr.ShouldContain("declared with attribute 'nodiscard': \"check the code\"");
    }

    [Fact]
    public void A_void_cast_suppresses_the_warning()
    {
        // C's suppression idiom: (void)f() discards deliberately. BuildCast lowers
        // it to the call with a void result type, so the discard check skips.
        var stderr = Stderr("""
            [[nodiscard]] int must_use(void) { return 1; }
            int main(void) { (void)must_use(); return 0; }
            """);
        stderr.ShouldNotContain("ignoring return value");
    }

    [Fact]
    public void Using_the_result_does_not_warn()
    {
        var stderr = Stderr("""
            [[nodiscard]] int must_use(void) { return 1; }
            int main(void) { int x = must_use(); return x - 1; }
            """);
        stderr.ShouldNotContain("ignoring return value");
    }

    [Fact]
    public void A_plain_function_is_not_flagged()
    {
        var stderr = Stderr("""
            int plain(void) { return 3; }
            int main(void) { plain(); return 0; }
            """);
        stderr.ShouldNotContain("ignoring return value");
    }

    [Fact]
    public void The_marker_on_a_prototype_reaches_a_separate_definition()
    {
        // nodiscard spelled on the prototype; the call resolves to the shared
        // symbol, so the discard still warns even though the definition is bare.
        var stderr = Stderr("""
            [[nodiscard]] int compute(void);
            int compute(void) { return 7; }
            int main(void) { compute(); return 0; }
            """);
        stderr.ShouldContain("ignoring return value of 'compute', declared with attribute 'nodiscard'");
    }
}
