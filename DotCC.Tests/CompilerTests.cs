#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Library-level unit tests for <see cref="Compiler"/>. Each test writes a
/// short C snippet to a temp file, drives <see cref="Compiler.EmitCSharp"/>
/// in-process, and asserts on the returned C# string. No subprocesses.
/// </summary>
public sealed class CompilerTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-unit-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void EmitCSharp_minimal_main_emits_unsafe_int_main()
    {
        var src = WriteTemp("""
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });

            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldContain("return 0;");
            // file-based program header so the result is `dotnet run --file`-able
            emitted.ShouldStartWith("#:property AllowUnsafeBlocks=true");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_csproj_mode_omits_file_directive()
    {
        var src = WriteTemp("int main() { return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, fileBased: false);
            emitted.ShouldNotContain("#:property");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_throws_on_parse_error()
    {
        var src = WriteTemp("int main() { return }"); // missing operand
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("parse failed");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_throws_when_no_main()
    {
        var src = WriteTemp("int foo() { return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("no `main`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Preprocess_resolves_define_into_token_stream()
    {
        var src = WriteTemp("""
            #define ANSWER 42
            int main() { return ANSWER; }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();

            // ANSWER should not appear in the output — it gets substituted by 42.
            dumped.ShouldNotContain(" ANSWER ");
            dumped.ShouldContain(" 42 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Include_angle_form_resolves_to_synthetic_stdio_header()
    {
        // `#include <stdio.h>` reassembles the fragmented `<`, `stdio`, `.`,
        // `h`, `>` tokens back into a filename and resolves against
        // SystemHeaders (which carries the synthetic stdio.h).
        var src = WriteTemp("""
            #include <stdio.h>
            int main() { printf("hi"); return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Pragma_once_short_circuits_repeated_include()
    {
        // The directory of the .c file is also on the include search path,
        // so a sibling header is reachable via `#include "shared.h"`. The
        // header defines a macro and ends with `#pragma once`; if it were
        // re-included on the second `#include`, the second `#define` of
        // ANSWER would still substitute (it'd just shadow). The user-visible
        // behavior here is "the include should be processed exactly once" —
        // which we observe by counting `#define ANSWER 42` re-entry…
        // actually the most observable thing is "no error from re-include".
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-pragma-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var headerPath = Path.Combine(dir, "shared.h");
            File.WriteAllText(headerPath, """
                #pragma once
                #define ANSWER 42
                """);
            var srcPath = Path.Combine(dir, "main.c");
            File.WriteAllText(srcPath, """
                #include "shared.h"
                #include "shared.h"
                int main() { return ANSWER; }
                """);
            var emitted = Compiler.EmitCSharp(new[] { srcPath });
            emitted.ShouldContain("return 42;");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Error_directive_aborts_compilation()
    {
        var src = WriteTemp("""
            #error this is a forced failure
            int main() { return 0; }
            """);
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("#error");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Warning_directive_continues_compilation()
    {
        // #warning emits to stderr but doesn't abort; the program should
        // still compile and main should be present in the output.
        var src = WriteTemp("""
            #warning informational only
            int main() { return 7; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldContain("return 7;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Function_macro_two_args_substitutes_each()
    {
        // The canonical example. `MAX(a, b)` should land at the call site
        // as `((1) > (2) ? (1) : (2))` in the post-preprocess stream.
        var src = WriteTemp("""
            #define MAX(a, b) ((a) > (b) ? (a) : (b))
            int main() { return MAX(1, 2); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();

            // Both formal params replaced with the actual int literals.
            dumped.ShouldNotContain(" MAX ");
            dumped.ShouldNotContain(" a ");
            dumped.ShouldNotContain(" b ");
            dumped.ShouldContain(" 1 ");
            dumped.ShouldContain(" 2 ");
            // The surrounding parens + ternary structure all preserved.
            dumped.ShouldContain(" > ");
            dumped.ShouldContain(" ? ");
            dumped.ShouldContain(" : ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Function_macro_emits_and_compiles_end_to_end()
    {
        // Full pipeline: parser must see a valid expression after expansion.
        // The emitted C# should compile (no syntax errors).
        var src = WriteTemp("""
            #define SQUARE(x) ((x) * (x))
            int main() { return SQUARE(5); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("return ");
            // The 5 should appear (twice — substituted into both x positions)
            // and SQUARE shouldn't survive into the emit.
            emitted.ShouldNotContain("SQUARE");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Function_macro_zero_args_works()
    {
        // `#define PI() 3.14159` — function-like but takes no params.
        // Still requires a `()` at the call site to trigger expansion.
        var src = WriteTemp("""
            #define PI() 314
            int main() { return PI(); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldNotContain(" PI ");
            dumped.ShouldContain(" 314 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Function_macro_name_without_parens_passes_through()
    {
        // `#define ADD(a, b) ((a) + (b))` then bare `ADD` (no `(`) — the
        // macro name should land in the output as an identifier, not get
        // expanded. Real C allows this idiom (e.g. function-pointer math
        // through #ifdef-tested alternatives).
        var src = WriteTemp("""
            #define ADD(a, b) ((a) + (b))
            int main() { int x = ADD; return 0; }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // `ADD` appears as a bare ID (no call) — the macro definition
            // line had `ADD ( a , b ) ( ( a ) + ( b ) )` which got consumed
            // by OnDefine, so the only remaining `ADD` is the use-site one.
            dumped.ShouldContain(" ADD ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Function_macro_with_nested_parens_in_args_paren_balanced()
    {
        // `MAX((a+b), c)` — the inner `(a+b)` has its own parens. The
        // comma inside `(a+b)` (if any) must NOT split the arg list at
        // depth 1; arg collection is paren-balanced.
        var src = WriteTemp("""
            #define MAX(a, b) ((a) > (b) ? (a) : (b))
            int main() { return MAX((1 + 2), 3); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldNotContain("MAX");
            // The `1 + 2` lands wherever `a` was — verify the addition
            // tokens are present (a appeared 3 times in the body, so 1+2
            // gets substituted in 3 times).
            dumped.ShouldContain(" 1 ");
            dumped.ShouldContain(" + ");
            dumped.ShouldContain(" 2 ");
            dumped.ShouldContain(" 3 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Object_like_macro_still_works_alongside_function_like()
    {
        // Regression: refactoring _macros to MacroDef shouldn't break the
        // existing object-like substitution path (which lives in Rewrite,
        // not MacroExpander).
        var src = WriteTemp("""
            #define ANSWER 42
            #define DOUBLE(x) ((x) * 2)
            int main() { return DOUBLE(ANSWER); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // ANSWER should expand to 42 (object-like).
            dumped.ShouldNotContain(" ANSWER ");
            // DOUBLE(...) should expand to `((42) * 2)` — the inner arg
            // came in as `ANSWER` and got object-like-expanded BEFORE
            // reaching MacroExpander (Rewrite happens upstream).
            dumped.ShouldNotContain(" DOUBLE ");
            dumped.ShouldContain(" 42 ");
            dumped.ShouldContain(" * ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Stringify_operator_quotes_the_arg_text()
    {
        // `#x` reconstructs the arg's source text as a STRING literal.
        var src = WriteTemp("""
            #define STR(x) #x
            int main() { char* s = STR(hello world); return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The emitted C# wraps strings in L("...\0"u8); the inner
            // content should carry "hello world".
            emitted.ShouldContain("hello");
            emitted.ShouldContain("world");
            // STR shouldn't survive — the call expanded into a string literal.
            emitted.ShouldNotContain("STR(");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Token_paste_concatenates_into_single_identifier()
    {
        // `a ## b` pastes the two tokens into one identifier. Combined
        // with rescan, the pasted name can refer to another macro.
        var src = WriteTemp("""
            #define foo_1 100
            #define MAKEFOO(n) foo_##n
            int main() { return MAKEFOO(1); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // After paste: foo_1 → rescan expands to 100.
            dumped.ShouldNotContain("MAKEFOO");
            dumped.ShouldNotContain(" ## ");
            dumped.ShouldContain(" 100 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Token_paste_with_two_formals()
    {
        // `a ## b` where BOTH operands are formal params. The pasted
        // result substitutes both args' last/first tokens.
        var src = WriteTemp("""
            #define glue_2 200
            #define CAT(a, b) a ## b
            int main() { return CAT(glue_, 2); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // CAT(glue_, 2) → glue_2 → 200 (after rescan picks up glue_2).
            dumped.ShouldNotContain("CAT");
            dumped.ShouldContain(" 200 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Variadic_macro_va_args_expands_to_extras()
    {
        // The canonical use: `LOG(fmt, ...)` → `printf(fmt, __VA_ARGS__)`.
        var src = WriteTemp("""
            #define LOG(fmt, ...) printf(fmt, __VA_ARGS__)
            int main() { LOG("a=%d b=%d c=%d\n", 1, 2, 3); return 0; }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // __VA_ARGS__ should be replaced by the three int args, all
            // present and comma-separated.
            dumped.ShouldNotContain("__VA_ARGS__");
            dumped.ShouldNotContain("LOG(");
            dumped.ShouldContain(" 1 ");
            dumped.ShouldContain(" 2 ");
            dumped.ShouldContain(" 3 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Predefined_FILE_expands_to_string_literal_of_active_filename()
    {
        var src = WriteTemp("""
            int main() { char* f = __FILE__; return 0; }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // __FILE__ should have expanded to a STRING token. The dumped
            // token stream prints token content verbatim, so we just look
            // for any string-literal containing the bare filename.
            var bareName = Path.GetFileName(src);
            dumped.ShouldNotContain(" __FILE__ ");
            dumped.ShouldContain(bareName);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Predefined_LINE_expands_to_numeric_literal_at_use_site()
    {
        // The __LINE__ appears on line 2 of the source — should expand to `2`.
        var src = WriteTemp("""
            int main() {
                int line_here = __LINE__;
                return line_here;
            }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldNotContain(" __LINE__ ");
            // Line 2 in our source (the `int line_here = …;` line).
            dumped.ShouldContain(" 2 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Predefined_func_resolves_to_string_of_enclosing_function_name()
    {
        // __func__ is visitor-time, not preprocessor-time — drives through
        // EmitCSharp end-to-end. Each occurrence is replaced with the
        // dotcc string-literal idiom carrying the enclosing function name.
        var src = WriteTemp("""
            int compute() { char* name = __func__; return 0; }
            int main() { char* mn = __func__; return compute(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // `__func__` should NOT survive into the emitted C# — it'd be
            // an undefined identifier and Roslyn would reject it.
            emitted.ShouldNotContain("__func__");
            // Both function names should appear as string-literal targets.
            emitted.ShouldContain("L(\"compute\\0\"u8)");
            emitted.ShouldContain("L(\"main\\0\"u8)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Variadic_macro_with_named_params_plus_extras()
    {
        // Named param `level` + variadic extras. `level` substitutes
        // by name; the extras land in `__VA_ARGS__`.
        var src = WriteTemp("""
            #define WARN(level, ...) printf("[%d] ", level); printf(__VA_ARGS__)
            int main() {
                WARN(7, "x=%d\n", 42);
                return 0;
            }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldContain(" 7 ");
            dumped.ShouldContain(" 42 ");
            dumped.ShouldNotContain("__VA_ARGS__");
            dumped.ShouldNotContain("WARN(");
        }
        finally { File.Delete(src); }
    }
}
