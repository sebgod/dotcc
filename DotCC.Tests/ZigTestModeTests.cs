#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the Zig test-runner (`dotcc zig test`): in TEST mode each `test "…" {}` block is
/// lowered to a runnable `__zigtest_N` function and the entry point runs each and reports pass/fail;
/// in a NORMAL build the same blocks are dropped. Fast string assertions on the emitted C# — the
/// end-to-end compile-and-run behavior lives in DotCC.FunctionalTests.ZigTestRunnerTests.
/// </summary>
public class ZigTestModeTests
{
    private static string Emit(string body, bool testMode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigtm-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }, emit: EmitMode.File, testMode: testMode); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Test_mode_lowers_test_blocks_to_functions_and_a_runner()
    {
        var cs = Emit(
            "const std = @import(\"std\");\n" +
            "test \"alpha\" { try std.testing.expect(true); }\n" +
            "test \"beta\" {}\n",
            testMode: true);
        cs.ShouldContain("__zigtest_0");                    // first test lowered to a function
        cs.ShouldContain("__zigtest_1");                    // second
        cs.ShouldContain("ZigTesting.expect");              // std.testing.expect routed to the runtime helper
        cs.ShouldContain("test \\\"alpha\\\" ... ");         // the runner announces each test by name
        cs.ShouldContain("All \" + __passed + \" test(s) passed."); // the summary line
        cs.ShouldContain("__zigtest_0()");                  // the runner invokes each test
    }

    [Fact]
    public void Normal_build_drops_test_blocks()
    {
        // The SAME source in a non-test build: `test` blocks are analysis-only and dropped, so no
        // test function is emitted and there is no runner (main-based entry instead).
        var cs = Emit(
            "const std = @import(\"std\");\n" +
            "test \"alpha\" { try std.testing.expect(true); }\n" +
            "pub fn main() void {}\n",
            testMode: false);
        cs.ShouldNotContain("__zigtest_");
        cs.ShouldNotContain("test(s) passed");
    }
}
