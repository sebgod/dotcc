#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// End-to-end coverage of <c>dotcc zig test</c> (the test-runner milestone): a <c>.zig</c> file's
/// <c>test "…" {}</c> blocks are lowered to runnable <c>anyerror!void</c> functions and the emitted
/// program's entry point runs each, reporting OK/FAIL and exiting 0 iff all pass. Drives the same
/// in-process pipeline the fixtures use (<see cref="FixtureRunner"/>: dotcc → Roslyn → invoke),
/// asking <see cref="Compiler.EmitCSharp"/> for test mode.
/// </summary>
public class ZigTestRunnerTests
{
    /// <summary>Emit <paramref name="source"/> in TEST mode, compile + run it in-process, and return
    /// the runner's stdout and process exit code.</summary>
    private static (string stdout, int exit) RunZigTest(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigtest-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, source);
        try
        {
            var cs = Compiler.EmitCSharp(new[] { path }, emit: EmitMode.File, testMode: true);
            return FixtureRunner.CompileAndRunCapturingExit(cs, Array.Empty<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void All_passing_tests_report_ok_and_exit_zero()
    {
        var (stdout, exit) = RunZigTest(
            "const std = @import(\"std\");\n" +
            "test \"one plus one\" { try std.testing.expect(1 + 1 == 2); }\n" +
            "test \"trivial\" {}\n");
        stdout.ShouldContain("test \"one plus one\" ... OK");
        stdout.ShouldContain("test \"trivial\" ... OK");
        stdout.ShouldContain("All 2 test(s) passed.");
        exit.ShouldBe(0);
    }

    [Fact]
    public void A_failing_expect_reports_fail_and_exits_nonzero()
    {
        var (stdout, exit) = RunZigTest(
            "const std = @import(\"std\");\n" +
            "test \"good\" { try std.testing.expect(true); }\n" +
            "test \"bad\" { try std.testing.expect(1 == 2); }\n");
        stdout.ShouldContain("test \"good\" ... OK");
        stdout.ShouldContain("test \"bad\" ... FAIL");
        stdout.ShouldContain("1 of 2 test(s) failed, 1 passed.");
        exit.ShouldBe(1);
    }

    [Fact]
    public void An_explicit_error_return_fails_the_test()
    {
        var (stdout, exit) = RunZigTest("test \"boom\" { return error.Boom; }\n");
        stdout.ShouldContain("test \"boom\" ... FAIL");
        exit.ShouldBe(1);
    }

    [Fact]
    public void expectEqual_passes_on_equal_and_fails_on_unequal()
    {
        var (okOut, okExit) = RunZigTest(
            "const std = @import(\"std\");\n" +
            "test \"eq\" { const a: i32 = 5; try std.testing.expectEqual(a, 5); }\n");
        okOut.ShouldContain("test \"eq\" ... OK");
        okExit.ShouldBe(0);

        var (failOut, failExit) = RunZigTest(
            "const std = @import(\"std\");\n" +
            "test \"neq\" { const a: i32 = 5; try std.testing.expectEqual(a, 6); }\n");
        failOut.ShouldContain("test \"neq\" ... FAIL");
        failExit.ShouldBe(1);
    }

    [Fact]
    public void A_file_with_no_tests_passes_with_zero_count()
    {
        // A `main` is IGNORED in test mode (real `zig test` runs only tests), and a file with no
        // test blocks legitimately runs zero tests.
        var (stdout, exit) = RunZigTest("pub fn main() void {}\n");
        stdout.ShouldContain("All 0 test(s) passed.");
        exit.ShouldBe(0);
    }
}
