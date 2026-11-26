using System;
using System.IO;
using System.Text;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// A program that defines a function or object named like a runtime member gets its own, as a
/// C linker prefers the program's symbol over the C library's, also where the call crosses the
/// program's classes: past <c>Compiler.FunctionsPerClass</c> functions the program continues in
/// <c>DotCcProgram2</c>, and a call from <c>DotCcProgram</c> into it reached both the program's
/// function and <c>Libc</c>'s through <c>using static</c> (CS0121; CPython's pthread stubs hit
/// it). The program's classes now sit in the <c>DotCcUser</c> namespace with their own imports.
/// </summary>
public sealed class ProgramNamesWinTests
{
    [Fact]
    public void A_program_function_named_like_a_runtime_one_wins_across_classes()
    {
        var n = Compiler.FunctionsPerClass + 2;
        var src = new StringBuilder("#include <stdio.h>\n");
        // External, so each one is emitted; main calls the last two, which land in DotCcProgram2.
        for (var i = 0; i < n - 2; i++) { src.Append($"int f{i}(void) {{ return {i % 7}; }}\n"); }
        // Named like the runtime's <string.h> and <ctype.h> functions, defined by the program.
        src.Append("unsigned long strlen(const char *s) { (void)s; return 1000; }\n");
        src.Append("int toupper(int c) { return c + 1; }\n");
        // An object named like a runtime member too.
        src.Append("int M_PI = 3;\n");
        src.Append("int main(void) { printf(\"%lu %d %d %d\\n\", strlen(\"abc\"), toupper('a'), M_PI, f0() + f1()); return 0; }\n");
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-names-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, src.ToString());
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { path }, emit: EmitMode.Csproj);
            emitted.ShouldContain("static class DotCcProgram2");
            FixtureRunner.CompileAndRun(emitted, Array.Empty<string>()).TrimEnd().ShouldBe("1000 98 3 1");
        }
        finally { File.Delete(path); }
    }
}
