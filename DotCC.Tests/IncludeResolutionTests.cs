#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <c>#include</c> resolution (GH #217), which follows gcc and
/// clang: a quoted name is looked up next to the file holding the directive,
/// then in each <c>-I</c> directory in command-line order (first match wins),
/// then among the embedded system headers; an angle name skips the first step.
/// Any file name is includable, and a name that resolves to nothing is a fatal
/// error. Each shape is from the CPython 3.13 tree.
/// </summary>
[Collection("IncludeResolution")]
public sealed class IncludeResolutionTests
{
    /// <summary>A scratch source tree: <see cref="Write"/> files into it, then
    /// compile one of them.</summary>
    private sealed class Tree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "dotcc-inc-" + Guid.NewGuid().ToString("N"));

        public Tree() => Directory.CreateDirectory(Root);

        /// <summary>Write <paramref name="text"/> at <paramref name="rel"/>
        /// (forward slashes) and return its full path.</summary>
        public string Write(string rel, string text)
        {
            var path = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Root);
            File.WriteAllText(path, text);
            return path;
        }

        public string Dir(string rel) => Path.Combine(Root, rel);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void A_quoted_include_looks_next_to_the_including_header_first()
    {
        // Include/cpython/pyatomic.h: `#include "pyatomic_std.h"`, a sibling in
        // cpython/, reached through `-I Include` and `<cpython/pyatomic.h>`.
        using var t = new Tree();
        t.Write("Include/cpython/pyatomic.h", "#include \"pyatomic_std.h\"\n");
        t.Write("Include/cpython/pyatomic_std.h", "static int atomic_answer(void) { return 42; }\n");
        var main = t.Write("main.c", "#include <cpython/pyatomic.h>\nint main(void) { return atomic_answer(); }\n");

        var emitted = Compiler.EmitCSharp(new[] { main }, includeDirs: new[] { t.Dir("Include") });

        emitted.ShouldContain("int atomic_answer()");
    }

    [Fact]
    public void A_quoted_include_can_climb_out_of_the_including_files_directory()
    {
        // Parser/lexer/lexer.c: `#include "../pegen.h"`.
        using var t = new Tree();
        t.Write("Parser/pegen.h", "static int pegen_answer(void) { return 7; }\n");
        var lexer = t.Write("Parser/lexer/lexer.c", "#include \"../pegen.h\"\nint main(void) { return pegen_answer(); }\n");

        var emitted = Compiler.EmitCSharp(new[] { lexer });

        emitted.ShouldContain("int pegen_answer()");
    }

    [Fact]
    public void Any_file_name_is_includable()
    {
        // Objects/typeobject.c: `#include "typeslots.inc"`; stringlib's
        // `clinic/transmogrify.h.h`.
        using var t = new Tree();
        t.Write("typeslots.inc", "static int slots[] = { 1, 2, 3 };\n");
        t.Write("clinic/transmogrify.h.h", "static int clinic_answer(void) { return slots[2]; }\n");
        var main = t.Write("main.c",
            "#include \"typeslots.inc\"\n#include \"clinic/transmogrify.h.h\"\nint main(void) { return clinic_answer(); }\n");

        var emitted = Compiler.EmitCSharp(new[] { main });

        emitted.ShouldContain("int clinic_answer()");
    }

    [Fact]
    public void The_first_include_directory_on_the_command_line_wins()
    {
        using var t = new Tree();
        t.Write("first/cfg.h", "#define WHICH 1\n");
        t.Write("second/cfg.h", "#define WHICH 2\n");
        var main = t.Write("main.c", "#include <cfg.h>\nint main(void) { return WHICH; }\n");

        var emitted = Compiler.EmitCSharp(new[] { main }, includeDirs: new[] { t.Dir("first"), t.Dir("second") });

        emitted.ShouldContain("return 1;");
    }

    [Fact]
    public void A_missing_include_is_a_fatal_error()
    {
        // Python-tokenize.c used to "emit OK" with four unresolved headers.
        using var t = new Tree();
        var main = t.Write("main.c", "int x;\n#include \"missing.h\"\nint main(void) { return 0; }\n");

        var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { main }));

        ex.Message.ShouldContain("main.c:2: fatal error: 'missing.h' file not found");
    }

    [Fact]
    public void An_angle_include_does_not_search_the_source_files_directory()
    {
        // gcc and clang: `<…>` searches the -I and system directories only.
        using var t = new Tree();
        t.Write("local.h", "#define LOCAL 1\n");
        var main = t.Write("main.c", "#include <local.h>\nint main(void) { return LOCAL; }\n");

        var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { main }));

        ex.Message.ShouldContain("'local.h' file not found");
    }

    [Fact]
    public void A_user_header_shadows_the_embedded_one_of_the_same_name()
    {
        using var t = new Tree();
        t.Write("inc/stdbool.h", "#define USER_STDBOOL 5\n");
        var main = t.Write("main.c", "#include <stdbool.h>\nint main(void) { return USER_STDBOOL; }\n");

        var emitted = Compiler.EmitCSharp(new[] { main }, includeDirs: new[] { t.Dir("inc") });

        emitted.ShouldContain("return 5;");
    }

    [Fact]
    public void Pragma_once_recognises_the_same_file_under_another_spelling()
    {
        using var t = new Tree();
        t.Write("inc/once.h", "#pragma once\nint counter;\n");
        var main = t.Write("main.c",
            "#include \"inc/once.h\"\n#include <once.h>\nint main(void) { return counter; }\n");

        var emitted = Compiler.EmitCSharp(new[] { main }, includeDirs: new[] { t.Dir("inc") });

        // One definition: a second copy of `int counter;` would be a C# duplicate.
        emitted.Split("int counter").Length.ShouldBe(2);
    }

    [Fact]
    public void Has_include_resolves_like_include()
    {
        using var t = new Tree();
        t.Write("sub/a.h", "#if __has_include(\"b.h\")\n#define HAVE_B 1\n#else\n#define HAVE_B 0\n#endif\n");
        t.Write("sub/b.h", "\n");
        var main = t.Write("main.c", "#include \"sub/a.h\"\nint main(void) { return HAVE_B; }\n");

        var emitted = Compiler.EmitCSharp(new[] { main });

        emitted.ShouldContain("return 1;");
    }
}
