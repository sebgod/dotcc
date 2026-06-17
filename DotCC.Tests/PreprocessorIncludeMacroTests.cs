#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Regression tests for function-like macro expansion inside an
/// <c>#include</c>d file. The interesting case is a header that BOTH defines
/// and <c>#undef</c>s a function-like macro it uses (chibi-scheme's
/// <c>lib/srfi/39/param.c</c> does exactly <c>#define _I(x) … / use(s) / #undef
/// _I</c>): <see cref="CPreprocessor.OnInclude"/> drains the included file's
/// directives — including the trailing <c>#undef</c> — before the body tokens
/// reach the downstream macro expander, so without expanding within the include
/// the macro is already gone and its uses survive RAW into the emit. The fix
/// runs a <c>MacroExpander</c> over the include body while the definition is
/// still live (mirroring the top-level pipeline).
/// </summary>
[Collection("PreprocessorIncludeMacro")]
public sealed class PreprocessorIncludeMacroTests
{
    /// <summary>Write <paramref name="header"/> + <paramref name="main"/> into a
    /// fresh temp dir and return the <c>main.c</c> path; EmitCSharp auto-adds the
    /// source dir as an include path, so a quoted <c>#include</c> resolves.</summary>
    private static (string MainPath, string Dir) WritePair(string header, string main)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotcc-incmac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mac.h"), header);
        var mainPath = Path.Combine(dir, "main.c");
        File.WriteAllText(mainPath, main);
        return (mainPath, dir);
    }

    [Fact]
    public void Function_like_macro_defined_and_undefd_in_a_header_still_expands()
    {
        // mac.h defines TWICE, uses it, then #undefs it — all within the header.
        var (mainPath, dir) = WritePair(
            "#define TWICE(x) ((x) + (x))\nstatic int dbl(int n) { return TWICE(n); }\n#undef TWICE\n",
            "#include \"mac.h\"\nint main(void) { return dbl(21); }\n");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath });
            emitted.ShouldContain("return n + n;");     // TWICE(n) expanded (emitter drops redundant parens)
            // No raw macro call survives (case-sensitive: the runtime block's prose
            // contains "twice", which a case-insensitive match would trip on).
            emitted.ShouldNotContain("TWICE(", Case.Sensitive);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Object_like_macro_defined_and_undefd_in_a_header_still_expands()
    {
        // The object-like counterpart, for good measure.
        var (mainPath, dir) = WritePair(
            "#define ANSWER 42\nstatic int get(void) { return ANSWER; }\n#undef ANSWER\n",
            "#include \"mac.h\"\nint main(void) { return get(); }\n");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath });
            emitted.ShouldContain("return 42;");
            emitted.ShouldNotContain("ANSWER", Case.Sensitive);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
