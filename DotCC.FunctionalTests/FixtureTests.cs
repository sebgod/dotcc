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
            emit: EmitMode.Csproj,  // csproj-shaped shell — Roslyn doesn't want the #:property header
            dialect: CDialect.Parse(match.std));

        var stdout = FixtureRunner.CompileAndRun(emitted, args: System.Array.Empty<string>());

        // Trim trailing newlines so platform-specific writer quirks (CRLF vs LF,
        // trailing newline insertion) don't fail an otherwise-correct match.
        stdout.ReplaceLineEndings("\n").TrimEnd('\n')
            .ShouldBe(match.expectedStdout.ReplaceLineEndings("\n").TrimEnd('\n'));
    }
}
