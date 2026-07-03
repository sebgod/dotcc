#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C23 <c>[[fallthrough]]</c> + the opt-in
/// <c>-Wimplicit-fallthrough</c> warning (<c>WarningFlags.ImplicitFallthrough</c>).
/// dotcc's switch lowering already synthesizes C's fall-through jump, so the
/// attribute has no codegen — its only job is to suppress this warning, which
/// fires on a non-empty case that falls through to the next label without it.
/// gcc-verbatim wording ("this statement may fall through"), off by default like
/// gcc/clang. The check lives in <see cref="Compiler"/>'s IR builder
/// (<c>CheckImplicitFallthrough</c>); it fires exactly when the backend would
/// synthesize an implicit <c>goto case</c> and no marker excused it.
/// </summary>
[Collection("Console")]
public sealed class ImplicitFallthroughTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-ft-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>Emit <paramref name="body"/> capturing stderr (where the warning lands).
    /// The unit assembly serializes the Console collection, so the process-global
    /// <c>Console.Error</c> swap is race-free. <paramref name="enable"/> toggles
    /// <c>-Wimplicit-fallthrough</c>.</summary>
    private static string EmitCapturingStderr(string body, bool enable)
    {
        var path = WriteTemp(body);
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            var warnings = enable ? WarningFlags.Default | WarningFlags.ImplicitFallthrough : WarningFlags.Default;
            Compiler.EmitCSharp(new[] { path }, warnings: warnings);
        }
        finally { Console.SetError(prior); File.Delete(path); }
        return sw.ToString();
    }

    [Fact]
    public void Nonempty_case_falling_through_warns()
    {
        // `case 1:` does real work then falls into `case 2:` with no break/marker.
        var stderr = EmitCapturingStderr(
            "int main(void){int x=1,y=0;switch(x){case 1: y=y+1; case 2: y=y+2; break;} return y;}", enable: true);
        stderr.ShouldContain("this statement may fall through");   // gcc-verbatim
    }

    [Fact]
    public void Fallthrough_attribute_suppresses_the_warning()
    {
        // `[[fallthrough]];` before the next label is exactly what gives the attribute a job.
        var stderr = EmitCapturingStderr(
            "int main(void){int x=1,y=0;switch(x){case 1: y=y+1; [[fallthrough]]; case 2: y=y+2; break;} return y;}", enable: true);
        stderr.ShouldNotContain("fall through");
    }

    [Fact]
    public void A_case_that_breaks_does_not_warn()
    {
        var stderr = EmitCapturingStderr(
            "int main(void){int x=1,y=0;switch(x){case 1: y=y+1; break; case 2: y=y+2; break;} return y;}", enable: true);
        stderr.ShouldNotContain("fall through");
    }

    [Fact]
    public void A_case_that_returns_does_not_warn()
    {
        // A terminator other than break also ends the section (mirrors the backend's Terminates).
        var stderr = EmitCapturingStderr(
            "int main(void){int x=1;switch(x){case 1: return 1; case 2: return 2;} return 0;}", enable: true);
        stderr.ShouldNotContain("fall through");
    }

    [Fact]
    public void Stacked_empty_labels_do_not_warn()
    {
        // `case 1: case 2:` share one body — an empty case stacking into the next is
        // C's normal "these values do the same thing", not an accidental fall-through.
        var stderr = EmitCapturingStderr(
            "int main(void){int x=1,y=0;switch(x){case 1: case 2: y=y+2; break; default: y=9;} return y;}", enable: true);
        stderr.ShouldNotContain("fall through");
    }

    [Fact]
    public void The_last_section_never_warns()
    {
        // The final section can't fall INTO anything, so it needs no break/marker.
        var stderr = EmitCapturingStderr(
            "int main(void){int x=1,y=0;switch(x){case 1: y=1; break; default: y=9;} return y;}", enable: true);
        stderr.ShouldNotContain("fall through");
    }

    [Fact]
    public void Off_by_default_is_silent()
    {
        // Same falling-through program, but the flag is not set — no warning (like gcc/clang).
        var stderr = EmitCapturingStderr(
            "int main(void){int x=1,y=0;switch(x){case 1: y=y+1; case 2: y=y+2; break;} return y;}", enable: false);
        stderr.ShouldNotContain("fall through");
    }
}
