#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Opt-in differential test for the Zig front-end: compile + run a Zig program
/// through dotcc (emit C# → Roslyn → run) AND through the real <c>zig</c>
/// compiler, then assert they agree. The Zig analogue of
/// <see cref="GccWslOracleTests"/> / <see cref="MsvcOracleTests"/>, but a PURE
/// differential — there is no committed snapshot to validate. The always-on
/// <see cref="ZigFrontendTests"/> already pins dotcc's emit; here real zig IS
/// the oracle, so the two pipelines are compared head-to-head with no baseline
/// file in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exit code is the observable</b> — the current Zig surface has no I/O, so
/// each case compares the <b>process exit code</b> (the program's <c>main</c>
/// return; Zig's <c>fn main() u8</c> returns the exit code), which is why
/// <see cref="FixtureRunner.CompileAndRunCapturingExit"/> exists. Once
/// <c>@cImport</c> brings <c>c.printf</c> → stdout, these grow a stdout
/// differential and a <c>ZigFixtures/&lt;name&gt;/</c> walk mirroring
/// <see cref="FixtureRunner.Discover"/>. For now the cases are inline.
/// </para>
/// <para>
/// <b>Modes</b> (env vars): opt-in via <c>DOTCC_RUN_ZIG_ORACLE=1</c> (skips with
/// a hint otherwise); skips with a clear message when no <c>zig</c> is on PATH.
/// Same posture as the gcc/MSVC oracles — toolchain absence is a skip.
/// </para>
/// </remarks>
public sealed class ZigOracleTests
{
    private const string RunZigEnv = "DOTCC_RUN_ZIG_ORACLE";

    private static bool ZigRunRequested =>
        Environment.GetEnvironmentVariable(RunZigEnv) == "1";

    /// <summary>Each case: a self-contained Zig program + its expected process exit
    /// code + expected stdout (newline-normalized, trailing-newline-trimmed). They span
    /// the lowered surface — arithmetic, comparison, the if-expression, if/while
    /// statements + assignment, a prefix op, parameters, a function call (incl. forward
    /// reference), and an `extern fn` libc call that produces real OUTPUT — so a
    /// divergence pins which feature drifted from real zig.</summary>
    public static IEnumerable<object[]> Programs => new[]
    {
        new object[] { "arith",
            "pub fn main() u8 { const x: u8 = 40; return x + 2; }\n", 42, "" },
        new object[] { "if_expr",
            "pub fn main() u8 { const x: u8 = 40; const y: u8 = if (x > 10) x else 0; return y + 2; }\n", 42, "" },
        new object[] { "if_stmt",
            "pub fn main() u8 { var x: u8 = 0; if (3 > 2) { x = 42; } else { x = 1; } return x; }\n", 42, "" },
        new object[] { "while_sum",
            "pub fn main() u8 { var i: u8 = 0; var sum: u8 = 0; while (i < 5) { sum = sum + i; i = i + 1; } return sum; }\n", 10, "" },
        new object[] { "bitnot",
            "pub fn main() u8 { const a: u8 = 0; const b: u8 = ~a; return b; }\n", 255, "" },
        // i64 parameters: `wide` is type-checked by dotcc's emit + Roslyn with the
        // wider signedness the UsualArithmetic fix preserves; main is the observable.
        new object[] { "i64_params",
            "fn wide(a: i64, b: i64) i64 { return a * b; }\npub fn main() u8 { return 42; }\n", 42, "" },
        // A function CALL — main invokes a named function with arguments.
        new object[] { "call",
            "fn add(a: u8, b: u8) u8 { return a + b; }\npub fn main() u8 { return add(40, 2); }\n", 42, "" },
        // A FORWARD-referenced call — `add` is defined AFTER `main` (Zig has no
        // prototypes); the two-pass lowering must resolve it.
        new object[] { "call_forward",
            "pub fn main() u8 { return add(40, 2); }\nfn add(a: u8, b: u8) u8 { return a + b; }\n", 42, "" },
        // extern fn libc FFI: `putchar` from libc (linked -lc) produces real STDOUT.
        // dotcc routes it by bare name to its Libc runtime; zig links the real libc.
        new object[] { "extern_putchar",
            "extern fn putchar(c: c_int) c_int;\npub fn main() u8 { _ = putchar(72); _ = putchar(105); _ = putchar(10); return 0; }\n", 0, "Hi" },
        // VARIADIC extern fn + a string literal: `printf` with `[*c]const u8` format
        // and a `...` pack. dotcc routes it through the printf-family fluent builder;
        // zig links real libc printf. The `%d` exercises the variadic-tail formatting.
        // The `@as(c_int, …)` cast is REQUIRED: a bare literal has no fixed-size ABI
        // type, so both zig AND dotcc reject `printf("%d", 42)` (variadic strictness).
        new object[] { "printf_fmt",
            "extern fn printf(format: [*c]const u8, ...) c_int;\npub fn main() u8 { _ = printf(\"Hi %d\\n\", @as(c_int, 42)); return 0; }\n", 0, "Hi 42" },
        // VOID-returning main (`pub fn main() void`) — idiomatic Zig with no exit code.
        // dotcc's shell calls it for effect and returns 0; real zig's start code does
        // the same. No explicit `return;` needed (a void body falls off the end).
        new object[] { "void_main",
            "extern fn printf(format: [*c]const u8, ...) c_int;\npub fn main() void { _ = printf(\"void %d\\n\", @as(c_int, 7)); }\n", 0, "void 7" },
        // OPTIONALS (Milestone B1). A `?*T` lowers to a bare nullable pointer (Zig's
        // niche); `null` is none, `orelse` defaults, `.?`/deref unwraps.
        new object[] { "optional_ptr",
            "pub fn main() u8 { var x: u8 = 5; const p: ?*u8 = &x; const q: ?*u8 = null; return (p orelse &x).* + (q orelse &x).*; }\n", 10, "" },
        // A `?T` over a value type → C# Nullable<T>: `orelse` is `??`, `.?` is `.Value`.
        new object[] { "optional_value",
            "pub fn main() u8 { const a: ?u8 = 40; const b: ?u8 = null; return (a orelse 0) + (b orelse 2); }\n", 42, "" },
        // ERROR UNIONS (Milestone B2). A `!u8` returns an error union; `try` unwraps-or-
        // propagates, `catch` supplies a fallback, `return error.X` is the error path.
        // try success: parse(40)=41, outer unwraps + adds → Ok(42), `catch 0` passes it through.
        new object[] { "errunion_try_ok",
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x + 1; }\n" +
            "fn outer(x: u8) !u8 { const v = try parse(x); return v + 1; }\n" +
            "pub fn main() u8 { return outer(40) catch 0; }\n", 42, "" },
        // catch on the error path: parse(0) → error.Zero, so `catch 7` yields the fallback.
        new object[] { "errunion_catch_err",
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x + 1; }\n" +
            "pub fn main() u8 { return parse(0) catch 7; }\n", 7, "" },
        // try PROPAGATION: parse(0) errors, `try` aborts `outer` with it (the exception-based
        // early return), and main's `catch 5` handles the propagated error → 5.
        new object[] { "errunion_propagate",
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x + 1; }\n" +
            "fn outer(x: u8) !u8 { const v = try parse(x); return v + 1; }\n" +
            "pub fn main() u8 { return outer(0) catch 5; }\n", 5, "" },
        // `!void`: check() returns no payload; `try check(x);` propagates any error and
        // discards the void success. check(5) is fine → run returns Ok(9) → `catch 0` → 9.
        new object[] { "errunion_void",
            "fn check(x: u8) !void { if (x == 0) return error.Zero; }\n" +
            "fn run(x: u8) !u8 { try check(x); return 9; }\n" +
            "pub fn main() u8 { return run(5) catch 0; }\n", 9, "" },
    };

    private static string Norm(string s) => s.ReplaceLineEndings("\n").TrimEnd('\n');

    [Theory]
    [MemberData(nameof(Programs))]
    public void Dotcc_matches_zig(string name, string program, int expectedExit, string expectedStdout)
    {
        if (!ZigRunRequested)
        {
            Assert.Skip(
                $"Zig oracle is opt-in. Set {RunZigEnv}=1 to compile + run each program " +
                $"with the real zig compiler and assert dotcc's Zig path agrees. The " +
                $"always-on ZigFrontendTests already pins dotcc's emit.");
        }
        if (!ZigOracle.IsAvailable)
        {
            Assert.Skip($"{RunZigEnv} requested but no `zig` is on PATH on this host.");
        }

        // dotcc path: write a temp .zig, emit C# (csproj-shaped — Roslyn rejects
        // the #:property header), compile in-process, run, capture the exit code.
        var zigPath = Path.Combine(Path.GetTempPath(), $"dotcc-zig-oracle-{name}-{Guid.NewGuid():N}.zig");
        File.WriteAllText(zigPath, program);
        int dotccExit;
        string dotccStdout;
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { zigPath }, fileBased: false);
            (dotccStdout, dotccExit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
        }
        finally { File.Delete(zigPath); }

        // zig path: build + run the SAME source with the real compiler.
        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-zig-oracle-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var zigSrc = Path.Combine(workDir, "main.zig");
        File.WriteAllText(zigSrc, program);
        try
        {
            var (zigStdout, zigExit) = ZigOracle.CompileAndRun(zigSrc, workDir);

            // Both observables — exit code AND stdout — must agree with real zig.
            dotccExit.ShouldBe(zigExit, $"dotcc's Zig path diverges from real zig on '{name}' (exit code)");
            dotccExit.ShouldBe(expectedExit, $"'{name}' did not produce the expected exit code");
            Norm(dotccStdout).ShouldBe(Norm(zigStdout), $"dotcc's Zig path diverges from real zig on '{name}' (stdout)");
            Norm(dotccStdout).ShouldBe(expectedStdout, $"'{name}' did not produce the expected stdout");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
