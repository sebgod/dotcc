#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// The C fixtures under <c>--target=wat</c>, ratcheted like the CPython probe's
/// <c>emits.txt</c>. <c>Fixtures/wat-fixtures.txt</c> lists the fixtures the wat backend
/// compiles. The always-on test requires that list to be exactly the fixtures that emit:
/// one that stops emitting is a regression, and one that starts must be added, so the
/// list stays true as the backend grows. The opt-in oracle (<c>DOTCC_RUN_WAT=1</c>, with
/// wabt's <c>wat2wasm</c> and <c>node</c> on PATH) runs each listed fixture and requires
/// the stdout in its <c>expected-stdout.txt</c>, the same the C# build prints.
/// </summary>
public sealed class WatFixtureTests
{
    private const string RunEnv = "DOTCC_RUN_WAT";

    private static string ListPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "wat-fixtures.txt");

    private static HashSet<string> Listed() =>
        File.ReadAllLines(ListPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<object[]> AllFixtures =>
        TestShard.Rows(FixtureRunner.Discover().Select(f => new object[] { f.name }));

    public static IEnumerable<object[]> ListedFixtures =>
        TestShard.Rows(Listed().Order(StringComparer.Ordinal).Select(n => new object[] { n }));

    /// <summary>Emit a fixture as wat, with the dialect its <c>std.txt</c> names.</summary>
    private static string EmitWat(string name)
    {
        var f = FixtureRunner.Discover().Single(x => x.name == name);
        return Compiler.EmitWat(f.sources, includeDirs: new[] { f.dir }, dialect: CDialect.Parse(f.std));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void A_fixture_is_listed_exactly_when_wat_compiles_it(string name)
    {
        bool emits;
        string why = "";
        try { EmitWat(name); emits = true; }
        catch (CompileException e) { emits = false; why = e.Message; }

        var listed = Listed().Contains(name);
        if (emits && !listed)
        {
            Assert.Fail($"'{name}' now compiles under wat: add it to Fixtures/wat-fixtures.txt (and check it runs: {RunEnv}=1).");
        }
        if (!emits && listed)
        {
            Assert.Fail($"'{name}' is listed in Fixtures/wat-fixtures.txt but no longer compiles under wat: {why}");
        }
    }

    [Theory]
    [MemberData(nameof(ListedFixtures))]
    public void A_listed_fixture_runs_under_wasm_like_the_csharp_build(string name)
    {
        if (Environment.GetEnvironmentVariable(RunEnv) != "1")
        {
            Assert.Skip($"set {RunEnv}=1 to run the wat fixtures (needs wabt's wat2wasm + node on PATH).");
        }
        var f = FixtureRunner.Discover().Single(x => x.name == name);
        var stdout = WatOracleTests.RunWatModuleStdout(EmitWat(name));
        stdout.ReplaceLineEndings("\n").TrimEnd('\n')
            .ShouldBe(f.expectedStdout.ReplaceLineEndings("\n").TrimEnd('\n'));
    }
}
