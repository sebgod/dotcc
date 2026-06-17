#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// Mixed-language whole-program: a SINGLE dotcc invocation with both <c>.c</c> and
/// <c>.zig</c> translation units. The C group builds its <see cref="Ir.IrBuilder"/>
/// normally (preprocessor, struct/enum/global emission) and the Zig group lowers into
/// that same module, so the program emits once and a call across the language boundary
/// resolves at the C# level (every function is a <c>DotCcProgram</c> method called by
/// bare name). These tests prove the <c>IFrontend</c> seam composes two front-ends
/// into one running program — emit → Roslyn → run, the full round-trip.
/// </summary>
public sealed class MixedTranslationUnitTests
{
    /// <summary>Write each (extension, body) unit to a temp file, emit the mixed
    /// whole-program C#, and clean up. Order of units is irrelevant — dispatch
    /// partitions by extension.</summary>
    private static string EmitMixed(params (string ext, string body)[] units)
    {
        var paths = new List<string>();
        try
        {
            foreach (var (ext, body) in units)
            {
                var p = Path.Combine(Path.GetTempPath(), $"dotcc-mix-{Guid.NewGuid():N}{ext}");
                File.WriteAllText(p, body);
                paths.Add(p);
            }
            return Compiler.EmitCSharp(paths, fileBased: false);
        }
        finally
        {
            foreach (var p in paths) { try { File.Delete(p); } catch { /* best effort */ } }
        }
    }

    [Fact]
    public void C_main_calls_a_zig_function()
    {
        // C `main` calls `add`, defined in a .zig unit (declared in C via a prototype).
        var emitted = EmitMixed(
            (".c", "#include <stdio.h>\nint add(int a, int b);\nint main(void){ printf(\"sum=%d\\n\", add(40, 2)); return 0; }\n"),
            (".zig", "pub fn add(a: c_int, b: c_int) c_int { return a + b; }\n"));
        var (stdout, exit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
        stdout.Trim().ShouldBe("sum=42");
        exit.ShouldBe(0);
    }

    [Fact]
    public void Zig_main_calls_a_c_function_that_uses_a_struct()
    {
        // Zig `main` (void) calls `cmul`, a C function whose TU declares + uses a struct.
        // The shared-module build preserves the C struct (the obj-fragment link path
        // would drop it), and the void main is wired to call-then-return-0.
        var emitted = EmitMixed(
            (".zig",
                "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
                "extern fn cmul(a: c_int, b: c_int) c_int;\n" +
                "pub fn main() void { _ = printf(\"cmul=%d\\n\", cmul(6, 7)); }\n"),
            (".c", "struct Pair { int a; int b; };\nint cmul(int x, int y){ struct Pair p = { x, y }; return p.a * p.b; }\n"));
        emitted.ShouldContain("struct Pair");   // the C struct survives into the program
        var (stdout, exit) = FixtureRunner.CompileAndRunCapturingExit(emitted, Array.Empty<string>());
        stdout.Trim().ShouldBe("cmul=42");
        exit.ShouldBe(0);
    }
}
