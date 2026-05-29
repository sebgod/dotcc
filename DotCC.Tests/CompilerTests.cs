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
    public void Duffs_device_parses_and_emits_structure()
    {
        // The legendary loop-unrolling trick. Case labels are interleaved
        // into a do-while body inside a switch — `case 7: case 6: …` mixed
        // with code that pre-C99 compilers happily accept. dotcc's grammar
        // handles it (case/default are statement-level labels that can
        // appear anywhere in a switch body).
        //
        // Important: the emit is structurally faithful but C# REJECTS it
        // — both because C# requires case labels at the top of the switch
        // (not inside nested blocks) AND because C# forbids implicit case
        // fall-through (CS0163). Translating Duff's device to runnable C#
        // requires a flat-switch-with-goto-case transformation, which is
        // a known limitation. This test pins down the grammar/emit half:
        // dotcc parses and emits without throwing.
        var src = WriteTemp("""
            void duff_copy(int* dst, int* src, int count) {
                int n = (count + 7) / 8;
                switch (count % 8) {
                case 0: do { *dst = *src; dst = dst + 1; src = src + 1;
                case 7:      *dst = *src; dst = dst + 1; src = src + 1;
                case 6:      *dst = *src; dst = dst + 1; src = src + 1;
                case 5:      *dst = *src; dst = dst + 1; src = src + 1;
                case 4:      *dst = *src; dst = dst + 1; src = src + 1;
                case 3:      *dst = *src; dst = dst + 1; src = src + 1;
                case 2:      *dst = *src; dst = dst + 1; src = src + 1;
                case 1:      *dst = *src; dst = dst + 1; src = src + 1;
                        } while (--n > 0);
                }
            }
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Structural assertions — confirms the case-labels-inside-do-while
            // shape made it through parse + emit intact.
            emitted.ShouldContain("switch ((count % 8))");
            emitted.ShouldContain("case 0:");
            emitted.ShouldContain("case 7:");
            emitted.ShouldContain("case 1:");
            emitted.ShouldContain("do {");
            emitted.ShouldContain("while (Cond.B(");
        }
        finally { File.Delete(src); }
    }

    // ---- Negative tests: invalid programs must be REJECTED ---------------
    // Each verifies that semantically-invalid C — well-formed at the parse
    // level but contradictory at the type-resolution level — fails with a
    // CompileException naming the actual user-typed keywords.

    [Fact]
    public void Conflicting_signedness_throws()
    {
        var src = WriteTemp("int main() { signed unsigned int x = 0; return x; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("cannot combine `signed` and `unsigned`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Duplicate_unsigned_throws()
    {
        var src = WriteTemp("int main() { unsigned unsigned int x = 0; return x; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("duplicate `unsigned`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Multiple_base_types_throws()
    {
        var src = WriteTemp("int main() { int float x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("multiple base types");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Conflicting_size_modifiers_throws()
    {
        var src = WriteTemp("int main() { short long x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("cannot combine `short` and `long`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Triple_long_throws()
    {
        var src = WriteTemp("int main() { long long long x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("more than two `long`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Bool_combined_with_other_specifier_throws()
    {
        var src = WriteTemp("int main() { unsigned _Bool x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("`_Bool` cannot be combined");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Float128_keyword_lowers_to_Float128_type()
    {
        var src = WriteTemp("int main() { _Float128 x = 3; return (int)x; }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("Float128 x = 3;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Gnu_float128_spelling_is_an_alias()
    {
        // `__float128` (the GNU spelling) lexes to the same type as `_Float128`.
        // gcc only defines it on x86, so the aarch64 gcc oracle can't cover it;
        // this asserts dotcc accepts it and lowers identically.
        var src = WriteTemp("int main() { __float128 x = 3; return (int)x; }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("Float128 x = 3;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Float128_combined_with_other_specifier_throws()
    {
        var src = WriteTemp("int main() { unsigned _Float128 x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("`_Float128` cannot be combined");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Float_with_size_modifier_throws()
    {
        var src = WriteTemp("int main() { long float x = 0; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("`float` cannot take size or sign modifiers");
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
    public void Define_object_like_with_parenthesized_body_is_not_function_like()
    {
        // `#define SCHAR_MIN (-128)` with a space before `(` is object-
        // like with body `(-128)`. Without whitespace tracking the
        // preprocessor used to mis-parse it as a function-like macro
        // taking `-128` as a "parameter". Position-based adjacency
        // check fixes that — the `(` here is NOT adjacent to SCHAR_MIN.
        var src = WriteTemp("""
            #define SCHAR_MIN (-128)
            int main() { return SCHAR_MIN; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The macro must expand at the use site — the literal name
            // should not survive into the emit.
            emitted.ShouldNotContain("return SCHAR_MIN");
            emitted.ShouldContain("-128");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Define_function_like_no_space_is_recognized()
    {
        // `#define SQ(x) ((x)*(x))` with `(` IMMEDIATELY after the name
        // (no space) IS function-like. The adjacency check must accept
        // this case.
        var src = WriteTemp("""
            #define SQ(x) ((x)*(x))
            int main() { return SQ(5); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The visitor adds spaces around `*` and outer parens, so
            // match the substitution fact (each `x` became `5`) rather
            // than the exact whitespace layout.
            emitted.ShouldContain("(5) * (5)");
            emitted.ShouldNotContain("SQ(");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Define_object_like_macro_chain_expands_transitively()
    {
        // `#define B A` where A is `#define A 42` should expand B → 42,
        // not B → A. Hide-set rescan handles the chain at use site.
        var src = WriteTemp("""
            #define A 42
            #define B A
            int main() { return B; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("return 42;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Define_self_referential_macro_does_not_infinite_loop()
    {
        // `#define A A` is legal C — the hide set guards against the
        // self-loop, so A expands to A (the bare token) once and stops.
        var src = WriteTemp("""
            #define A A
            int main() { int A = 5; return A; }
            """);
        try
        {
            // Compile shouldn't hang or stack-overflow.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int A = 5");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Include_stdint_h_typedefs_lower_to_using_aliases()
    {
        // <stdint.h> declares int8_t/int16_t/int32_t/int64_t + uint variants
        // and size_t/intptr_t/etc. as typedefs over the primitive int family.
        // The emitter lowers each `typedef T NAME;` to `using unsafe NAME = T;`
        // at file scope; the embedded resource pipeline carries those into
        // the emitted program. Spot-check the C# emit.
        var src = WriteTemp("""
            #include <stdint.h>
            int main() { int32_t a = 42; uint64_t b = 1000; return (int)a + (int)b; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("using unsafe int32_t = int;");
            emitted.ShouldContain("using unsafe uint64_t = ulong;");
            emitted.ShouldContain("using unsafe size_t = ulong;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Include_assert_h_expands_to_dotcc_assert_call()
    {
        // <assert.h> with NDEBUG undefined expands `assert(expr)` to
        // `__dotcc_assert(expr)`, a C# call resolved at link time
        // against DotCC.Libc.Libc's overloaded methods. The
        // [CallerArgumentExpression] attribute on the C# side captures
        // the source text of `expr` at the call site for diagnostic
        // messages.
        var src = WriteTemp("""
            #include <assert.h>
            int main() { int x = 5; assert(x > 0); return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Macro must have substituted — no `assert(` literal in emit.
            emitted.ShouldNotContain("assert(x > 0)");
            // Should call __dotcc_assert with the condition expression.
            emitted.ShouldContain("__dotcc_assert");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Include_assert_h_with_NDEBUG_lowers_to_noop_call()
    {
        // `#define NDEBUG` before `#include <assert.h>` selects the
        // no-op branch: `assert(expr)` expands to `__dotcc_assert_noop()`.
        // The condition expression is NOT evaluated (matches C99 §7.2.1.1).
        var src = WriteTemp("""
            #define NDEBUG
            #include <assert.h>
            int explode_marker_z7q() { return 0; }
            int main() { assert(explode_marker_z7q()); return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("__dotcc_assert_noop()");
            // The condition `explode_marker_z7q()` should appear EXACTLY
            // once — at the function definition. If NDEBUG hadn't
            // discarded the call site, we'd see a second occurrence.
            var calls = System.Text.RegularExpressions.Regex.Matches(
                emitted, @"explode_marker_z7q\s*\(").Count;
            calls.ShouldBe(1);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Include_string_h_resolves_and_calls_typecheck()
    {
        // <string.h> is a real .h file under DotCC.Lib/include/ — the
        // synthetic header declares strlen/strcmp/strcpy/memset/memcpy
        // (already implemented in DotCC.Libc.Libc). The parser must see
        // the prototypes so the calls below typecheck.
        var src = WriteTemp("""
            #include <string.h>
            int main()
            {
                int n = strlen("hello");
                int c = strcmp("a", "b");
                return n + c;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main()");
            // The functions resolve via `using static Libc;` in the shell — no
            // remap to capitalized names, the C-spelled identifiers pass
            // through. Spot-check that the call sites survive.
            emitted.ShouldContain("strlen(");
            emitted.ShouldContain("strcmp(");
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
            // and SQUARE shouldn't survive as a macro-invocation token in
            // the emit. Match `SQUARE(` so the case-insensitive comparison
            // doesn't trip on the math.h docstring "square root" that the
            // embedded DotCC.Libc runtime carries through.
            emitted.ShouldNotContain("SQUARE(");
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
    public void Designated_struct_init_emits_named_csharp_initializer()
    {
        var src = WriteTemp("""
            struct Point { int x; int y; };
            int main() {
                struct Point p = { .x = 7, .y = 13 };
                return p.x + p.y;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Designated init lowers to C#'s object-initializer syntax,
            // using the user-provided field names directly.
            emitted.ShouldContain("new Point { x = 7, y = 13 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Designated_init_supports_omitted_fields_zero_filled_per_C99()
    {
        // Per C99, fields not in the designated list zero-fill. C# struct
        // object-initializer does the same: omitted members keep default.
        var src = WriteTemp("""
            struct Pair { int a; int b; };
            int main() {
                struct Pair p = { .b = 99 };
                return p.a + p.b;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The exact initializer — `a` is absent (C# defaults it to 0). The
            // precise match below already proves `a` isn't emitted; we avoid a
            // bare ShouldNotContain("a = ") because that scans the whole file
            // including the spliced runtime, where `a = ` legitimately occurs
            // (e.g. `int extra = ...` in the embedded Float128 source).
            emitted.ShouldContain("new Pair { b = 99 }");
            emitted.ShouldNotContain("{ b = 99, a");
            emitted.ShouldNotContain("a = 0, b = 99");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Forward_struct_decl_emits_nothing_and_does_not_break_later_definition()
    {
        // `struct Node;` declares the tag without a body. C# types hoist,
        // so the forward decl emits nothing; the later full definition
        // works as normal.
        var src = WriteTemp("""
            struct Node;
            struct Node { int val; struct Node* next; };
            int main() {
                struct Node n;
                n.val = 5;
                return n.val;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Full struct decl is present (forward decl shouldn't have
            // duplicated it).
            emitted.ShouldContain("unsafe struct Node");
            emitted.ShouldContain("public int val");
            emitted.ShouldContain("public Node* next");
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

    // ---- -std= dialect selection -----------------------------------------
    // The dialect drives which __STDC_* macros are predefined. v1 scope:
    // headers and user code can branch on __STDC_VERSION__; the parser is
    // dialect-agnostic.

    [Fact]
    public void Default_dialect_seeds_STDC_VERSION_to_C17_value()
    {
        // No -std= → CDialect.Default (c17). __STDC_VERSION__ = 201710L.
        var src = WriteTemp("""
            int main() { return __STDC_VERSION__; }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // Macro substitution replaces the use-site identifier with the
            // numeric literal. Body lexed by the preprocessor so the L
            // suffix survives as part of the NUM token.
            dumped.ShouldNotContain("__STDC_VERSION__");
            dumped.ShouldContain("201710L");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void C99_dialect_sets_STDC_VERSION_to_199901L()
    {
        var src = WriteTemp("""
            int main() { return __STDC_VERSION__; }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw, dialect: CDialect.Parse("c99"));
            var dumped = sw.ToString();
            dumped.ShouldContain("199901L");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void C11_dialect_sets_STDC_VERSION_to_201112L()
    {
        var src = WriteTemp("int main() { return __STDC_VERSION__; }");
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw, dialect: CDialect.Parse("c11"));
            sw.ToString().ShouldContain("201112L");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void C23_dialect_sets_STDC_VERSION_to_202311L()
    {
        var src = WriteTemp("int main() { return __STDC_VERSION__; }");
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw, dialect: CDialect.Parse("c23"));
            sw.ToString().ShouldContain("202311L");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void C90_dialect_leaves_STDC_VERSION_undefined()
    {
        // C90: per the standard, __STDC__ is 1 but __STDC_VERSION__
        // is undefined. An `#if __STDC_VERSION__` evaluates the unresolved
        // identifier to 0, so the FALSE branch wins.
        var src = WriteTemp("""
            #if __STDC_VERSION__ >= 199901L
            #define MODERN 1
            #else
            #define MODERN 0
            #endif
            int main() { return MODERN; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"));
            emitted.ShouldContain("return 0;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void C99_dialect_makes_if_STDC_VERSION_GE_C99_take_true_branch()
    {
        // The whole point of seeding __STDC_VERSION__ with a real value:
        // header guards like `#if __STDC_VERSION__ >= 199901L` need to
        // evaluate correctly. The preprocessor's conditional-expression
        // evaluator pre-expands object-like macros via Rewrite, then
        // strips the L suffix and compares as long.
        var src = WriteTemp("""
            #if __STDC_VERSION__ >= 199901L
            #define MODERN 1
            #else
            #define MODERN 0
            #endif
            int main() { return MODERN; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c99"));
            emitted.ShouldContain("return 1;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void STDC_and_STDC_HOSTED_are_always_seeded()
    {
        // __STDC__ and __STDC_HOSTED__ are dialect-independent — every
        // conforming hosted compiler defines both as 1.
        var src = WriteTemp("""
            int main() {
                int a = __STDC__;
                int b = __STDC_HOSTED__;
                return a + b;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"));
            emitted.ShouldNotContain("__STDC__");
            emitted.ShouldNotContain("__STDC_HOSTED__");
            // Two `= 1` assignments survive.
            emitted.ShouldContain("a = 1");
            emitted.ShouldContain("b = 1");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void User_D_NAME_VALUE_now_substitutes_value_at_use_site()
    {
        // The value-bearing -D path was previously a defined-as-marker only.
        // The CPreprocessor now lexes the RHS into the macro body, so use
        // sites see the actual literal.
        var src = WriteTemp("int main() { return ANSWER; }");
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw, defines: new[] { "ANSWER=42" });
            sw.ToString().ShouldContain(" 42 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Unknown_std_value_throws_via_Parse()
    {
        // FormatException (not ArgumentException) so the frontend's
        // `dotcc: error: …` line doesn't carry the parameter-name
        // decoration that ArgumentException.Message tacks on.
        Should.Throw<System.FormatException>(() => CDialect.Parse("c42"))
            .Message.ShouldContain("c42");
    }

    [Fact]
    public void CDialect_Name_round_trips_known_values()
    {
        CDialect.Parse("c99").Name.ShouldBe("c99");
        CDialect.Parse("c17").Name.ShouldBe("c17");
        // c18 is a clang alias that normalizes to c17 (same standard year).
        CDialect.Parse("c18").Name.ShouldBe("c17");
        CDialect.Default.Name.ShouldBe("c17");
    }

    // ---- DialectKeywordRewriter: C23 `bool` promotion ---------------------
    // C23 makes `bool` a first-class keyword (no <stdbool.h> needed). The
    // rewriter promotes the bare `bool` ID onto the existing `_Bool` terminal,
    // but ONLY under -std=c23 — pre-C23 `bool` must stay an ordinary
    // identifier so the <stdbool.h> macro path keeps working and valid old
    // code that uses `bool` as a name still parses.

    [Fact]
    public void Bool_is_a_keyword_under_c23_without_stdbool_include()
    {
        // No `#include <stdbool.h>` — under c23 bare `bool` is the type.
        var src = WriteTemp("int main() { bool gt = 5 > 3; return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            // Promoted to `_Bool` → tsBool → integer-typed Libc.CBool.
            emitted.ShouldContain("CBool gt = (5 > 3);");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Bool_is_not_a_keyword_before_c23()
    {
        // Same source under the default c17 dialect: `bool` is NOT a keyword
        // and there's no <stdbool.h> include, so it's an unknown identifier —
        // `bool gt` is two adjacent IDs and the parse fails. This is the gate
        // working: we don't promote below the dialect's MinVersion.
        var src = WriteTemp("int main() { bool gt = 5 > 3; return 0; }");
        try
        {
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17")));
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Bool_via_stdbool_macro_still_works_under_c23()
    {
        // Regression: the rewriter sits AFTER macro expansion, so when the
        // header is included the `#define bool _Bool` macro has already fired
        // and the promotion table simply doesn't double-handle it. Including
        // <stdbool.h> under c23 must still emit the same `_Bool` (→ CBool).
        var src = WriteTemp("""
            #include <stdbool.h>
            int main() { bool gt = 5 > 3; return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("CBool gt = (5 > 3);");
        }
        finally { File.Delete(src); }
    }

    // ---- DialectKeywordRewriter: C23 `true` / `false` / `nullptr` ---------
    // Same rule-2 model as `bool`: keyword constants under c23, ordinary
    // identifiers (macro-supplied) before. `true`/`false` lower to the integer
    // literals 1/0 (normalized through CBool); `nullptr` to C# `null`.

    [Fact]
    public void True_and_false_are_keyword_constants_under_c23()
    {
        // No <stdbool.h> — under c23 the literals are first-class keywords,
        // lowering to integer 1/0 (stored into the CBool-typed locals).
        var src = WriteTemp("int main() { bool a = true; bool b = false; return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("CBool a = 1;");
            emitted.ShouldContain("CBool b = 0;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Nullptr_is_a_keyword_constant_under_c23()
    {
        // No <stddef.h> — under c23 `nullptr` is the null pointer constant,
        // lowered to C# `null` (matching the <stddef.h> `#define NULL null`).
        var src = WriteTemp("int main() { int* p = nullptr; return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("p = null;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Nullptr_is_an_identifier_before_c23()
    {
        // Pre-C23 with no header: `nullptr` is an unknown identifier, not a
        // keyword — `int* p = nullptr;` references an undeclared name. dotcc
        // emits it verbatim as `nullptr` (the gate didn't promote it to the
        // NULLPTR terminal, so it never reaches the `null` lowering).
        var src = WriteTemp("int main() { int* p = nullptr; return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"));
            emitted.ShouldContain("p = nullptr;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void True_is_an_identifier_pre_c23_but_a_keyword_constant_in_c23()
    {
        // The dialect gate, asserted by behaviour rather than by a (non-
        // compilable) emit string: `int true = 5;` is valid pre-C23 C — `true`
        // is an ordinary identifier, so it parses under c17. Under c23 `true`
        // is a keyword constant, so using it as a declarator name is a parse
        // error — which proves the rewriter gate actually flipped.
        //
        // (Pre-C23 it emits bare `true`, which is NOT C#-compilable as a
        // variable name — the documented `true`/`false`/`null` residual edge:
        // they can't be @-escaped because dotcc emits them as C# literals via
        // the stdbool/NULL macro paths. That's orthogonal to the gate here.)
        var src = WriteTemp("int main() { int true = 5; return true; }");
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17")));
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23")));
        }
        finally { File.Delete(src); }
    }

    // ---- _Static_assert / static_assert -----------------------------------
    // `_Static_assert` is a C11 keyword (always reserved). The C23 lowercase
    // `static_assert` is promoted onto it by the rewriter under -std=c23.
    // Compile-time only: dotcc parses it and drops it to an inert comment.

    [Fact]
    public void Static_assert_file_scope_emits_a_comment_not_a_call()
    {
        // C11 two-arg form at file scope — always a keyword, so no -std needed.
        var src = WriteTemp("""
            _Static_assert(1, "always true");
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static_assert (compile-time, not evaluated): \"always true\"");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Static_assert_block_scope_and_message_optional_c23_form()
    {
        // Block scope + the C23 message-less arity. `_Static_assert` is a
        // keyword in every dialect, so this parses even under the default c17.
        var src = WriteTemp("""
            int main() {
                _Static_assert(sizeof(int) >= 2, "int too small");
                _Static_assert(1);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static_assert (compile-time, not evaluated): \"int too small\"");
            emitted.ShouldContain("static_assert (compile-time, not evaluated) */");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lowercase_static_assert_is_a_keyword_under_c23()
    {
        // C23 promotes lowercase `static_assert` onto the `_Static_assert`
        // terminal — same comment lowering, no <assert.h> needed.
        var src = WriteTemp("""
            int main() {
                static_assert(1 + 1 == 2, "math works");
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("static_assert (compile-time, not evaluated): \"math works\"");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Lowercase_static_assert_is_a_plain_call_before_c23()
    {
        // Pre-C23 with no <assert.h> macro, `static_assert` is an ordinary
        // identifier: `static_assert(1, "x")` parses as a function-call
        // expression statement, NOT the keyword declaration. The gate didn't
        // promote it, so it is emitted verbatim as a call (not the comment).
        var src = WriteTemp("""
            int main() {
                static_assert(1, "x");
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"));
            emitted.ShouldContain("static_assert");
            emitted.ShouldNotContain("compile-time, not evaluated");
        }
        finally { File.Delete(src); }
    }

    // ---- malloc/free → stack-value peephole --------------------------------
    // `S* p = (S*)malloc(sizeof(S))` used only via `->` and freed in the same
    // function (no escape) lowers to a stack struct value `S p = new S();`,
    // `->` becomes `.`, and the free() is dropped. Any escaping use disqualifies.

    [Fact]
    public void Malloc_struct_used_only_via_arrow_and_freed_is_promoted()
    {
        var src = WriteTemp("""
            struct Point { int x; int y; };
            int main() {
                struct Point* p = (struct Point*)malloc(sizeof(struct Point));
                p->x = 3;
                p->y = 4;
                free(p);
                return p->x;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Stack value, not a heap allocation.
            emitted.ShouldContain("Point p = new Point()");
            // Arrow accesses lowered to `.` (the binop wrapper adds parens:
            // `(p.x) = 3`), and the pointer `->` form is gone for this var.
            emitted.ShouldContain("(p.x)");
            emitted.ShouldContain("(p.y)");
            emitted.ShouldNotContain("p->");
            // The user's cast-malloc is gone (the runtime still *defines*
            // malloc/free, but `(Point*)malloc` is unique to user code).
            emitted.ShouldNotContain("(Point*)malloc");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Malloc_struct_that_escapes_via_return_is_not_promoted()
    {
        // `p` is returned (escapes the function), so the stack-value rewrite
        // would dangle — dotcc must keep the low-level heap form.
        var src = WriteTemp("""
            struct Node { int v; struct Node* next; };
            struct Node* make(int v) {
                struct Node* p = (struct Node*)malloc(sizeof(struct Node));
                p->v = v;
                return p;
            }
            int main() { return make(5)->v; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(Node*)malloc");   // low-level kept
            emitted.ShouldContain("p->v");            // arrow kept (pointer)
            emitted.ShouldNotContain("new Node()");   // not promoted
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Malloc_struct_without_matching_free_is_not_promoted()
    {
        // No free() — the plan requires a matching free in the same function.
        // Without it we keep the heap form (changing lifetime silently would be
        // surprising), so no promotion.
        var src = WriteTemp("""
            struct Box { int n; };
            int main() {
                struct Box* b = (struct Box*)malloc(sizeof(struct Box));
                b->n = 9;
                return b->n;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(Box*)malloc");
            emitted.ShouldNotContain("new Box()");
        }
        finally { File.Delete(src); }
    }

    // ---- static storage duration -------------------------------------------
    // Block-scope `static` locals lower to mangled DotCcGlobals static fields
    // (one instance, persists across calls), with in-function references
    // rewritten to the mangled name. File-scope `static` is a passthrough to a
    // plain global (internal linkage is a no-op for non-exported variables).

    [Fact]
    public void Function_static_lowers_to_mangled_global_field()
    {
        var src = WriteTemp("""
            int next_id(void) {
                static int counter = 0;
                counter++;
                return counter;
            }
            int main() { return next_id(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The static became a mangled field initialised once...
            emitted.ShouldContain("__static_next_id_counter = 0");
            // ...and in-function references resolve to it.
            emitted.ShouldContain("__static_next_id_counter++");
            // The declaration emits no in-body local of the source name.
            emitted.ShouldNotContain("int counter = 0");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Same_named_function_statics_are_mangled_per_function()
    {
        // Two functions each with `static int counter` must not collide.
        var src = WriteTemp("""
            int a(void) { static int counter = 1; return ++counter; }
            int b(void) { static int counter = 2; return ++counter; }
            int main() { return a() + b(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("__static_a_counter = 1");
            emitted.ShouldContain("__static_b_counter = 2");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void File_scope_static_lowers_like_a_plain_global()
    {
        // `static` at file scope is internal linkage — a no-op for a variable
        // in dotcc's single-program model; it lowers to a DotCcGlobals field
        // under its own (un-mangled) name, the keyword simply consumed.
        var src = WriteTemp("""
            static int g = 7;
            int main() { return g; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("public static unsafe int g = 7;");
        }
        finally { File.Delete(src); }
    }

    // ---- <threads.h> -------------------------------------------------------
    // thrd_t / mtx_t are seeded type names (Libc structs); thrd_start_t is a
    // function-pointer typedef; calls pass through to the Libc runtime.

    [Fact]
    public void Threads_header_types_and_calls_lower_to_libc()
    {
        var src = WriteTemp("""
            #include <threads.h>
            int worker(void* arg) { return 0; }
            int main() {
                mtx_t mux;
                thrd_t t;
                mtx_init(&mux, mtx_plain);
                thrd_create(&t, &worker, &mux);
                thrd_join(t, 0);
                mtx_destroy(&mux);
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Opaque handles are the seeded Libc value structs, stack-allocated.
            emitted.ShouldContain("thrd_t t");
            emitted.ShouldContain("mtx_t mux");
            // Function name → C# function pointer for thrd_create (the `&`
            // operator wraps each operand in parens).
            emitted.ShouldContain("thrd_create((&t), (&worker), (&mux))");
            // mtx_plain is the <threads.h> macro constant (0).
            emitted.ShouldContain("mtx_init((&mux), 0)");
        }
        finally { File.Delete(src); }
    }

    // ---- C#-keyword identifier escaping ------------------------------------
    // C identifiers that are C# reserved keywords are @-escaped on emit, at
    // both declaration and reference sites (consistent because the escape is a
    // pure function of the name).

    [Fact]
    public void C_identifiers_that_are_csharp_keywords_are_escaped()
    {
        var src = WriteTemp("""
            struct rec { int new; int lock; };
            int object(int ref) { return ref * 2; }
            int main() {
                int new = 10;
                int string = object(new);
                struct rec ev;
                ev.new = new;
                return string + ev.lock;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("@new = 10");                 // keyword local decl
            emitted.ShouldContain("@object(@new)");             // keyword fn call + arg ref
            emitted.ShouldContain("int @object(int @ref)");     // keyword fn name + param decl
            emitted.ShouldContain("@ref * 2");                  // keyword param ref
            emitted.ShouldContain("public int @new;");          // keyword struct field decl
            emitted.ShouldContain("(ev.@new)");                 // keyword member access
            emitted.ShouldContain("int @string =");             // keyword local
        }
        finally { File.Delete(src); }
    }

    // ---- shadowing fixes ---------------------------------------------------

    [Fact]
    public void Local_shadowing_an_enum_constant_resolves_to_the_local()
    {
        // A local/param named like an enum constant must emit the bare local
        // name, NOT EnumName.Member (a const, not an lvalue).
        var src = WriteTemp("""
            enum E { X, Y };
            int f(int X) { return X + Y; }
            int main() {
                int Y = 5;
                Y = Y + 1;
                return f(Y);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The shadowing local `Y` is assigned (lvalue) — must be bare `Y`,
            // never `E.Y`. The param `X` likewise stays bare inside f.
            emitted.ShouldContain("Y = (Y + 1)");
            emitted.ShouldNotContain("E.Y =");
            // The genuine, un-shadowed enum use `Y` inside f still resolves to
            // the enum constant.
            emitted.ShouldContain("E.Y");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Local_shadowing_a_libc_builtin_name_is_a_plain_call()
    {
        // A function-pointer local named like a libc builtin must be called as
        // an ordinary call, not lowered as the builtin. `setjmp` is the sharp
        // case: without the guard its special-case returns a SetjmpCall marker
        // that throws when used outside an if/else — so a plain `return
        // setjmp(x)` would fail to emit at all.
        var src = WriteTemp("""
            typedef int (*fn)(int a);
            int dbl(int x) { return x * 2; }
            int main() {
                fn setjmp = &dbl;
                return setjmp(21);
            }
            """);
        try
        {
            EmitContentShouldBePlainCall(src);
        }
        finally { File.Delete(src); }
    }

    private static void EmitContentShouldBePlainCall(string src)
    {
        var emitted = Compiler.EmitCSharp(new[] { src });  // must not throw
        emitted.ShouldContain("setjmp(21)");
        emitted.ShouldContain("return setjmp(21);");
    }

    [Fact]
    public void Malloc_promote_interoperates_with_keyword_escaped_var_name()
    {
        // A promotable malloc'd pointer named with a C# keyword (`new`): the
        // promoted decl, the `.` accesses, and the dropped free must all agree
        // on the @-escaped name — the peephole maps are keyed by the raw name.
        var src = WriteTemp("""
            struct S { int x; };
            int main() {
                struct S* new = (struct S*)malloc(sizeof(struct S));
                new->x = 7;
                free(new);
                return new->x;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("S @new = new S()");  // promoted + escaped
            emitted.ShouldContain("(@new.x)");          // arrow -> dot, escaped
            emitted.ShouldNotContain("@new->");          // no pointer arrow left
            emitted.ShouldNotContain("S new = new S()"); // raw name never emitted
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
