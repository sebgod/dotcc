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

    [Fact]
    public void A_macro_defined_later_in_a_header_leaves_the_earlier_prototype_alone()
    {
        // CPython's object.h: `PyAPI_FUNC(int) Py_Is(PyObject *x, PyObject *y);`
        // then `#define Py_Is(x, y) ((x) == (y))`. The prototype is expanded
        // while the header is read, before the #define exists, so it stays a
        // prototype; the includer's expander must not rescan it (GH #214).
        var (mainPath, dir) = WritePair(
            "int twice(int x);\n#define twice(x) ((x) * 2)\n",
            "#include \"mac.h\"\nint main(void) { return twice(3); }\n");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath });
            emitted.ShouldContain("return 3 * 2;");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_self_referential_macro_in_a_header_expands_once()
    {
        // `#define Py_REFCNT(ob) Py_REFCNT(_PyObject_CAST(ob))`: the inner name is
        // painted blue by the header's own rescan. Before GH #214 every include
        // level rescanned the result, so a header two levels down came out as
        // `get(v + 1 + 1 + 1)`.
        var dir = Path.Combine(Path.GetTempPath(), "dotcc-incmac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inner.h"),
            "static int get(int x) { return x; }\n#define get(x) get((x) + 1)\n"
            + "static int use(int v) { return get(v); }\n");
        File.WriteAllText(Path.Combine(dir, "mac.h"), "#include \"inner.h\"\n");
        var mainPath = Path.Combine(dir, "main.c");
        File.WriteAllText(mainPath, "#include \"mac.h\"\nint main(void) { return use(1) + get(2); }\n");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { mainPath });
            emitted.ShouldContain("return get(v + 1);");
            emitted.ShouldContain("use(1) + get(2 + 1)");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
