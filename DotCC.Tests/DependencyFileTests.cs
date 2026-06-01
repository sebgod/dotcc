#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <see cref="Compiler.EmitDependencyRule"/> — the Make-format
/// dependency rule emitted by the frontend's <c>-MD</c>/<c>-MMD</c> flags. The
/// rule lists the translation unit plus every <c>#include</c>d header (so
/// CMake / Ninja / make can track header→TU dependencies for correct
/// incremental rebuilds). These drive the library API directly against a
/// throwaway temp directory of <c>.c</c>/<c>.h</c> files.
/// </summary>
public sealed class DependencyFileTests
{
    /// <summary>
    /// Run <paramref name="body"/> with a fresh temp directory, cleaned up
    /// afterwards. Mirrors the include-guard tests' temp-dir pattern.
    /// </summary>
    private static void WithTempDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotcc-dep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Normalize the emitted rule's separators so substring asserts are
    // platform-stable (the rule itself already uses forward slashes).
    private static string Slashes(string p) => p.Replace('\\', '/');

    [Fact]
    public void rule_lists_source_and_adjacent_quoted_header()
    {
        WithTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "foo.h"), "int foo(int);\n");
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src, "#include \"foo.h\"\nint main() { return 0; }\n");

            var rule = Compiler.EmitDependencyRule(src, new[] { "main.cs" }, includeSystemHeaders: true);

            rule.ShouldStartWith("main.cs:");
            rule.ShouldContain(Slashes(src));
            rule.ShouldContain(Slashes(Path.Combine(dir, "foo.h")));
            // gcc-style line continuations.
            rule.ShouldContain(" \\\n");
            rule.TrimEnd().ShouldNotEndWith("\\"); // last prereq line has no trailing backslash
        });
    }

    [Fact]
    public void rule_lists_header_found_via_minus_I_dir()
    {
        WithTempDir(dir =>
        {
            var incDir = Path.Combine(dir, "include");
            Directory.CreateDirectory(incDir);
            File.WriteAllText(Path.Combine(incDir, "cfg.h"), "#define TAG 1\n");
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src, "#include \"cfg.h\"\nint main() { return TAG; }\n");

            var rule = Compiler.EmitDependencyRule(
                src, new[] { "main.cs" }, includeSystemHeaders: true, includeDirs: new[] { incDir });

            rule.ShouldContain(Slashes(Path.Combine(incDir, "cfg.h")));
        });
    }

    [Fact]
    public void synthetic_system_header_is_never_listed()
    {
        // <stdio.h> is a synthetic embedded header — it resolves (so the
        // compile sees printf's signature) but has no on-disk path, so there
        // is nothing for make/ninja to stat. It must not appear in EITHER
        // -MD or -MMD output.
        WithTempDir(dir =>
        {
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src, "#include <stdio.h>\nint main() { return 0; }\n");

            var md = Compiler.EmitDependencyRule(src, new[] { "main.cs" }, includeSystemHeaders: true);
            var mmd = Compiler.EmitDependencyRule(src, new[] { "main.cs" }, includeSystemHeaders: false);

            md.ShouldNotContain("stdio.h");
            mmd.ShouldNotContain("stdio.h");
            // The source itself is always a prerequisite.
            md.ShouldContain(Slashes(src));
        });
    }

    [Fact]
    public void mmd_drops_angle_header_that_md_keeps()
    {
        // A *user* header pulled in via the angle form from a -I dir: -MD
        // (includeSystemHeaders) lists it; -MMD drops it. This is dotcc's
        // approximation of gcc's "omit system headers" — keyed on the <...>
        // spelling rather than a system include directory.
        WithTempDir(dir =>
        {
            var incDir = Path.Combine(dir, "sys");
            Directory.CreateDirectory(incDir);
            File.WriteAllText(Path.Combine(incDir, "vec.h"), "int vlen(void);\n");
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src, "#include <vec.h>\nint main() { return vlen(); }\n");

            var md = Compiler.EmitDependencyRule(
                src, new[] { "main.cs" }, includeSystemHeaders: true, includeDirs: new[] { incDir });
            var mmd = Compiler.EmitDependencyRule(
                src, new[] { "main.cs" }, includeSystemHeaders: false, includeDirs: new[] { incDir });

            md.ShouldContain(Slashes(Path.Combine(incDir, "vec.h")));
            mmd.ShouldNotContain("vec.h");
        });
    }

    [Fact]
    public void transitive_includes_are_listed()
    {
        WithTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "b.h"), "int b(void);\n");
            File.WriteAllText(Path.Combine(dir, "a.h"), "#include \"b.h\"\nint a(void);\n");
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src, "#include \"a.h\"\nint main() { return a() + b(); }\n");

            var rule = Compiler.EmitDependencyRule(src, new[] { "main.cs" }, includeSystemHeaders: true);

            rule.ShouldContain(Slashes(Path.Combine(dir, "a.h")));
            rule.ShouldContain(Slashes(Path.Combine(dir, "b.h")));
        });
    }

    [Fact]
    public void header_behind_false_if_is_not_listed()
    {
        // The dependency scan respects conditional compilation: a header
        // inside a `#if 0` block is never actually included, so it isn't a
        // build dependency.
        WithTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "live.h"), "int live(void);\n");
            File.WriteAllText(Path.Combine(dir, "dead.h"), "int dead(void);\n");
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src,
                "#include \"live.h\"\n#if 0\n#include \"dead.h\"\n#endif\nint main() { return live(); }\n");

            var rule = Compiler.EmitDependencyRule(src, new[] { "main.cs" }, includeSystemHeaders: true);

            rule.ShouldContain(Slashes(Path.Combine(dir, "live.h")));
            rule.ShouldNotContain("dead.h");
        });
    }

    [Fact]
    public void header_included_twice_is_listed_once()
    {
        WithTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "foo.h"),
                "#ifndef FOO_H\n#define FOO_H\nint foo(void);\n#endif\n");
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src,
                "#include \"foo.h\"\n#include \"foo.h\"\nint main() { return foo(); }\n");

            var rule = Compiler.EmitDependencyRule(src, new[] { "main.cs" }, includeSystemHeaders: true);

            // Count occurrences of the header path — must be exactly one.
            var needle = Slashes(Path.Combine(dir, "foo.h"));
            var count = 0;
            for (var i = rule.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = rule.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }
            count.ShouldBe(1);
        });
    }

    [Fact]
    public void multiple_targets_are_joined_with_space()
    {
        WithTempDir(dir =>
        {
            var src = Path.Combine(dir, "main.c");
            File.WriteAllText(src, "int main() { return 0; }\n");

            var rule = Compiler.EmitDependencyRule(
                src, new[] { "out/main.cs", "out/main.d" }, includeSystemHeaders: true);

            rule.ShouldStartWith("out/main.cs out/main.d:");
        });
    }

    [Fact]
    public void path_with_space_is_escaped()
    {
        WithTempDir(dir =>
        {
            var spaced = Path.Combine(dir, "has space");
            Directory.CreateDirectory(spaced);
            File.WriteAllText(Path.Combine(spaced, "foo.h"), "int foo(void);\n");
            var src = Path.Combine(spaced, "main.c");
            File.WriteAllText(src, "#include \"foo.h\"\nint main() { return foo(); }\n");

            var rule = Compiler.EmitDependencyRule(src, new[] { "main.cs" }, includeSystemHeaders: true);

            // Make escapes embedded spaces with a backslash.
            rule.ShouldContain("has\\ space");
        });
    }
}
