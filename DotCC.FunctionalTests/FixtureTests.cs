#nullable enable

using System.Collections.Generic;
using System.Linq;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// End-to-end fixture tests. Driven by directories under <c>Fixtures/</c>:
/// each subdir with one or more <c>.c</c> files and an
/// <c>expected-stdout.txt</c> sidecar produces one test case. Adding a new
/// case = drop a folder. No code change required.
/// </summary>
public sealed class FixtureTests
{
    public static IEnumerable<object[]> Fixtures =>
        FixtureRunner.Discover().Select(f => new object[] { f.name, f.dir });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_emits_csharp_runnable_with_matching_stdout(string name, string dir)
    {
        var match = FixtureRunner.Discover().Single(f => f.name == name);

        var emitted = Compiler.EmitCSharp(
            inputPaths: match.sources,
            includeDirs: new[] { dir },
            defines: null,
            fileBased: false,  // csproj-shaped shell — Roslyn doesn't want the #:property header
            dialect: CDialect.Parse(match.std));

        var stdout = FixtureRunner.CompileAndRun(emitted, args: System.Array.Empty<string>());

        // Trim trailing newlines so platform-specific writer quirks (CRLF vs LF,
        // trailing newline insertion) don't fail an otherwise-correct match.
        stdout.ReplaceLineEndings("\n").TrimEnd('\n')
            .ShouldBe(match.expectedStdout.ReplaceLineEndings("\n").TrimEnd('\n'));
    }

    // ---- Typed-IR backend (--ir) regression gate ------------------------
    // The strangler migration: the new IR backend is grown against the SAME
    // behavioral fixtures, one allow-listed slice at a time. Each name here
    // must emit + compile + match stdout through `useIr: true`. The list grows
    // as the IR path covers more of the language; when it covers everything it
    // becomes the default and the legacy emitter is retired.
    public static readonly System.Collections.Generic.HashSet<string> IrSlice = new()
    {
        // Phase 0 vertical slice: functions + params, int/long-double scalars,
        // arithmetic / compound-assign / relational, if / else-if / for, return,
        // printf with int + double varargs, multi-function programs.
        "fibonacci",
        "factorial-for",
        "fizzbuzz",
        "factorial-longdouble",
    };

    public static IEnumerable<object[]> IrFixtures =>
        FixtureRunner.Discover().Where(f => IrSlice.Contains(f.name)).Select(f => new object[] { f.name });

    [Theory]
    [MemberData(nameof(IrFixtures))]
    public void Ir_fixture_emits_csharp_runnable_with_matching_stdout(string name)
    {
        var match = FixtureRunner.Discover().Single(f => f.name == name);

        var emitted = Compiler.EmitCSharp(
            inputPaths: match.sources,
            includeDirs: new[] { match.dir },
            defines: null,
            fileBased: false,
            dialect: CDialect.Parse(match.std),
            useIr: true);

        var stdout = FixtureRunner.CompileAndRun(emitted, args: System.Array.Empty<string>());

        stdout.ReplaceLineEndings("\n").TrimEnd('\n')
            .ShouldBe(match.expectedStdout.ReplaceLineEndings("\n").TrimEnd('\n'));
    }
}
