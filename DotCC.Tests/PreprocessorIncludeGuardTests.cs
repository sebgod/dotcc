#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <see cref="CPreprocessor.DetectControllingMacro"/> — the
/// scanner that recognizes the standard <c>#ifndef X / #define X / ... / #endif</c>
/// header-guard wrapping pattern. When detected, subsequent
/// <c>#include</c>s of the same file short-circuit without re-opening +
/// re-lexing it. Same optimization gcc and clang document as the
/// "multiple-include optimization", on which every non-trivial C project
/// depends to keep transitive-include cost manageable.
/// </summary>
public sealed class PreprocessorIncludeGuardTests
{
    // -----------------------------------------------------------------
    // Positive cases — these files DO have a controlling guard.
    // -----------------------------------------------------------------

    [Fact]
    public void detects_minimal_guard() =>
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\n#define _FOO_H\n#endif\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_body() =>
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\n#define _FOO_H\nint foo(int x);\n#endif\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_leading_blank_lines() =>
        CPreprocessor.DetectControllingMacro(
            "\n\n#ifndef _FOO_H\n#define _FOO_H\n#endif\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_leading_block_comment() =>
        CPreprocessor.DetectControllingMacro(
            "/* (c) 2026 */\n#ifndef _FOO_H\n#define _FOO_H\n#endif\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_leading_line_comment() =>
        CPreprocessor.DetectControllingMacro(
            "// header for foo\n#ifndef _FOO_H\n#define _FOO_H\n#endif\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_trailing_blank_lines_and_comments() =>
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\n#define _FOO_H\n#endif\n\n/* end */\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_inner_nested_if() =>
        // Nested #if/#endif inside the outer guard should not confuse the
        // depth counter — only the outermost #endif closes the controlling
        // guard.
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\n#define _FOO_H\n#if 1\nint x;\n#endif\n#endif\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_inner_else() =>
        // #else doesn't change nesting depth.
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\n#define _FOO_H\n#if 1\n#else\n#endif\n#endif\n")
            .ShouldBe("_FOO_H");

    [Fact]
    public void detects_guard_with_extra_whitespace_in_directives() =>
        // Real-world headers often have `#   ifndef` or `# define` with
        // extra horizontal whitespace between the `#` and the keyword.
        CPreprocessor.DetectControllingMacro(
            "#  ifndef _FOO_H\n#   define _FOO_H\n#endif\n")
            .ShouldBe("_FOO_H");

    // -----------------------------------------------------------------
    // Negative cases — these files do NOT match the controlling-guard pattern.
    // -----------------------------------------------------------------

    [Fact]
    public void rejects_empty_file() =>
        CPreprocessor.DetectControllingMacro("").ShouldBeNull();

    [Fact]
    public void rejects_file_with_no_directives() =>
        CPreprocessor.DetectControllingMacro("int foo(int x);\n").ShouldBeNull();

    [Fact]
    public void rejects_mismatched_ifndef_define_names() =>
        // `#ifndef _FOO_H` paired with `#define _BAR_H` doesn't form a
        // proper guard — the macro that gets defined isn't the one being
        // tested, so re-including would re-process everything.
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\n#define _BAR_H\n#endif\n")
            .ShouldBeNull();

    [Fact]
    public void rejects_content_before_the_guard()
    {
        // Real C code before the `#ifndef` means the file isn't a
        // pure guard wrapping — re-including would re-emit that code.
        var source = "int x;\n#ifndef _FOO_H\n#define _FOO_H\n#endif\n";
        CPreprocessor.DetectControllingMacro(source).ShouldBeNull();
    }

    [Fact]
    public void rejects_content_after_closing_endif()
    {
        // Anything past the closing #endif (other than whitespace and
        // comments) means re-including would still emit it.
        var source = "#ifndef _FOO_H\n#define _FOO_H\n#endif\nint y;\n";
        CPreprocessor.DetectControllingMacro(source).ShouldBeNull();
    }

    [Fact]
    public void rejects_file_with_only_ifndef_no_define() =>
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\nint x;\n#endif\n")
            .ShouldBeNull();

    [Fact]
    public void rejects_file_without_endif() =>
        // Malformed file (missing endif) — better safe than wrong.
        CPreprocessor.DetectControllingMacro(
            "#ifndef _FOO_H\n#define _FOO_H\nint x;\n")
            .ShouldBeNull();

    [Fact]
    public void rejects_ifdef_instead_of_ifndef() =>
        // `#ifdef` is the opposite test — same shape but not a guard.
        CPreprocessor.DetectControllingMacro(
            "#ifdef _FOO_H\n#define _FOO_H\n#endif\n")
            .ShouldBeNull();

    [Fact]
    public void rejects_pragma_once_alone()
        // `#pragma once` is a different optimization (already handled
        // elsewhere in CPreprocessor); the controlling-macro detector
        // should not claim files that only use pragma-once as their
        // include-guard mechanism.
        => CPreprocessor.DetectControllingMacro(
            "#pragma once\nint foo(int);\n")
            .ShouldBeNull();

    // -----------------------------------------------------------------
    // End-to-end: prove the optimization actually fires through the
    // public Compiler.Preprocess surface, by `#include`ing the same
    // header twice from a tiny translation unit and checking the
    // CPreprocessor's hit counter.
    // -----------------------------------------------------------------

    [Fact]
    public void Preprocess_short_circuits_second_include_of_guarded_header()
    {
        // Write a .h with the standard guard pattern, and a .c that includes
        // it twice. After preprocessing, the optimization counter should be 1
        // (the second include skipped the file).
        var dir = Path.Combine(Path.GetTempPath(), "dotcc-include-opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "foo.h"),
                "#ifndef _FOO_H\n#define _FOO_H\nint foo(int x);\n#endif\n");
            var srcPath = Path.Combine(dir, "main.c");
            File.WriteAllText(srcPath,
                "#include \"foo.h\"\n#include \"foo.h\"\nint main() { return 0; }\n");

            // Drive through the same lex pipeline Compiler.Preprocess uses,
            // but reach in to construct the CPreprocessor ourselves so we
            // can observe the hit counter (internal field).
            var lexerTable = C.BuildLexer();
            var includeMap = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["foo.h"] = File.ReadAllText(Path.Combine(dir, "foo.h")),
            };
            var pre = new CPreprocessor(lexerTable, new Compiler.IncludeMap(includeMap), System.Array.Empty<string>());
            pre.SetActiveFilename("main.c");
            using var lex = LALR.CC.LexicalGrammar.BytesLexer.FromString(File.ReadAllText(srcPath), lexerTable);
            using var wrap = C.WrapPreprocessor(lex, pre);
            while (wrap.MoveNext()) { /* drain */ }

            pre.IncludeOptimizationHits.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Preprocess_does_not_short_circuit_unguarded_header()
    {
        // Unguarded header (no #ifndef/#define wrapper) — the optimization
        // must NOT fire, even though the file gets included twice.
        var dir = Path.Combine(Path.GetTempPath(), "dotcc-include-opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "raw.h"), "int raw(int);\n");
            var srcPath = Path.Combine(dir, "main.c");
            File.WriteAllText(srcPath,
                "#include \"raw.h\"\n#include \"raw.h\"\nint main() { return 0; }\n");

            var lexerTable = C.BuildLexer();
            var includeMap = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["raw.h"] = File.ReadAllText(Path.Combine(dir, "raw.h")),
            };
            var pre = new CPreprocessor(lexerTable, new Compiler.IncludeMap(includeMap), System.Array.Empty<string>());
            pre.SetActiveFilename("main.c");
            using var lex = LALR.CC.LexicalGrammar.BytesLexer.FromString(File.ReadAllText(srcPath), lexerTable);
            using var wrap = C.WrapPreprocessor(lex, pre);
            while (wrap.MoveNext()) { /* drain */ }

            pre.IncludeOptimizationHits.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
