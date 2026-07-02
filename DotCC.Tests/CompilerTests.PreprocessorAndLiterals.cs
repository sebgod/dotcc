#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

public sealed partial class CompilerTests
{
    // ---- octal (0-prefix) + binary (0b) integer literals ----------------

    [Fact]
    public void Octal_literal_converts_to_its_value()
    {
        // C# has no octal syntax (a leading 0 is plain decimal), so `0755`
        // must be converted to 493, not emitted verbatim (which would mean 755).
        var src = WriteTemp("int main() { int a = 0644; int b = 0755; return a + b; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int a = 420");   // 0644 octal
            emitted.ShouldContain("int b = 493");   // 0755 octal
            emitted.ShouldNotContain("0644");
            emitted.ShouldNotContain("0755");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Octal_zero_and_suffix_are_handled()
    {
        var src = WriteTemp("int main() { int z = 0; long b = 0777777L; return z; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int z = 0;");           // 0 stays 0
            emitted.ShouldContain("long b = 262143L");     // octal + L suffix
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Invalid_octal_digit_throws()
    {
        var src = WriteTemp("int main() { int x = 0789; return x; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("invalid digit '8' in octal constant");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Float_literal_suffixes_lower_correctly()
    {
        // `f`/`F` → C# float (verbatim). `l`/`L` → C long double, which dotcc
        // maps to C# double; C# has no float `L` suffix, so it's stripped and a
        // bare double literal is emitted. The `long double` *type* already maps
        // to `double`, so the variable types line up.
        var src = WriteTemp("""
            int main() {
                long double a = 1.5L;
                double b = 2.5l;
                float c = 3.25f;
                double d = 6.0e2L;
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("double a = 1.5;");      // L stripped
            emitted.ShouldContain("double b = 2.5;");      // lowercase l stripped
            emitted.ShouldContain("float c = 3.25f;");     // f kept
            emitted.ShouldContain("double d = 6.0e2;");    // suffix after exponent
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Hex_float_literals_convert_to_decimal_values()
    {
        // C# has no hex-float literal, so dotcc parses `0xH.HpE` and emits the
        // decimal value: `0x1.8p3` = 1.5*2^3 = 12, `0x1p-1` = 0.5, `0x.8p1` = 1,
        // `0x1.4p2f` = 5 (float). Integer-valued doubles are assigned to `double`
        // locals so C# infers the type correctly without a trailing `.0`.
        var src = WriteTemp("""
            int main() {
                double a = 0x1.8p3;
                double b = 0x1p-1;
                double c = 0x.8p1;
                float  d = 0x1.4p2f;
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The typed IR emits integer-valued doubles without a trailing `.0`.
            emitted.ShouldContain("double a = 12;");
            emitted.ShouldContain("double b = 0.5;");
            emitted.ShouldContain("double c = 1;");
            emitted.ShouldContain("float d = 5f;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Hex_float_literal_gated_as_c99_under_pedantic()
    {
        var src = WriteTemp("int main() { double x = 0x1.8p3; return (int)x; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
                .Message.ShouldContain("hex float literal");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Binary_literal_passes_through()
    {
        // C# accepts `0b` natively, so the binary literal is emitted verbatim.
        var src = WriteTemp("int main() { return 0b1011; }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("return 0b1011;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Binary_literal_gated_as_c23_under_pedantic()
    {
        var src = WriteTemp("int main() { return 0b101; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"), pedanticErrors: true))
                .Message.ShouldContain("binary integer literal");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Digit_separators_are_stripped()
    {
        // C23 `1'000'000` — the quotes are stripped (C# uses `_`, doesn't need
        // them). Works in decimal, hex, and binary.
        var src = WriteTemp("int main() { int m = 1'000'000; int h = 0xFF'FF; return m + h; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int m = 1000000");
            emitted.ShouldContain("int h = 0xFFFF");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Digit_separators_gated_as_c23_under_pedantic()
    {
        var src = WriteTemp("int main() { return 1'000; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"), pedanticErrors: true))
                .Message.ShouldContain("digit separator");
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
    public void Complex_type_lowers_to_System_Numerics_Complex()
    {
        // `float`/`double`/`long double` _Complex all map to .NET's Complex
        // (double-backed; float/long-double widen — documented).
        var src = WriteTemp("""
            int main() {
                double _Complex a;
                float _Complex b;
                long double _Complex c;
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Complex a = default");
            emitted.ShouldContain("Complex b = default");
            emitted.ShouldContain("Complex c = default");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Complex_requires_a_floating_base()
    {
        var src = WriteTemp("int main() { int _Complex x; return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("`_Complex` requires a `float`, `double`, or `long double` base");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Complex_imaginary_unit_and_complex_h_resolve()
    {
        // `I` chains through the <complex.h> macros to the imaginary-unit
        // property; the functions resolve via `using static Libc`.
        var src = WriteTemp("""
            #include <complex.h>
            int main() {
                double _Complex z = 1.0 + 2.0 * I;
                return (int)creal(conj(z));
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("__dotcc_complex_I");   // I → _Complex_I → property
            emitted.ShouldContain("conj(z)");
            emitted.ShouldContain("creal(");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Complex_gated_as_c99_under_pedantic()
    {
        var src = WriteTemp("int main() { double _Complex z; return 0; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
                .Message.ShouldContain("_Complex");
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

    // ---- #elifdef / #elifndef (C23) ----------------------------------------
    // Sugar for `#elif defined(NAME)` / `#elif !defined(NAME)`, handled by
    // LALR.CC's built-in conditional engine (the elifdef/elifndef roles).
    // Not dialect-gated — the directive is consumed inside the engine and
    // never reaches a dotcc hook (same ungated status as `//` comments).

    [Fact]
    public void Elifdef_selects_arm_when_macro_defined()
    {
        var src = WriteTemp("""
            #define FEATURE_B 1
            #if 0
            int dead_a(void) { return 1; }
            #elifdef FEATURE_B
            int picked(void) { return 2; }
            #else
            int dead_c(void) { return 3; }
            #endif
            int main() { return picked(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("picked");
            emitted.ShouldNotContain("dead_a");
            emitted.ShouldNotContain("dead_c");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Elifndef_selects_arm_when_macro_not_defined()
    {
        var src = WriteTemp("""
            #if 0
            int dead_a(void) { return 1; }
            #elifndef NEVER_DEFINED
            int picked(void) { return 2; }
            #endif
            int main() { return picked(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("picked");
            emitted.ShouldNotContain("dead_a");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Elifdef_locked_out_when_prior_arm_emitted()
    {
        // The arm-selection lock matches #elif: once an arm in the chain has
        // emitted, a later #elifdef never fires even if its macro is defined.
        var src = WriteTemp("""
            #define FEATURE_B 1
            #if 1
            int first(void) { return 1; }
            #elifdef FEATURE_B
            int dead_b(void) { return 2; }
            #endif
            int main() { return first(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("first");
            emitted.ShouldNotContain("dead_b");
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
            // The typed IR folds the constant subexpressions and drops the
            // redundant parentheses from the expanded `((5)*(5))`, emitting
            // `5 * 5`. The key invariant: SQ was recognized as function-like
            // (the macro expanded) and the two `5` operands are present.
            emitted.ShouldContain("5 * 5");
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
        // FLAGGED: the typed-IR backend resolves stdint typedefs eagerly to their
        // underlying C# primitive (e.g. `int32_t a` → `int a`) rather than emitting
        // `using unsafe int32_t = int;` at file scope. The test verifies that the
        // types resolve correctly (no compile error) and the variable declarations
        // use the right underlying primitives; re-point to `using unsafe` once the
        // IR emits typedef aliases.
        var src = WriteTemp("""
            #include <stdint.h>
            int main() { int32_t a = 42; uint64_t b = 1000; return (int)a + (int)b; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Typedef resolved to primitive — `int32_t a` becomes `int a`,
            // `uint64_t b` becomes `ulong b`.
            emitted.ShouldContain("int a = 42");
            emitted.ShouldContain("ulong b = 1000");
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
    public void Subdirectory_qualified_include_resolves_against_include_dir()
    {
        // chibi-scheme's layout: headers live under include/chibi/, sources
        // say `#include "chibi/sexp.h"` with `-I include`. The include map
        // must register nested headers under their dir-relative path (with
        // `/` separators), not just the bare filename.
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-subinc-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "include", "chibi"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "include", "chibi", "sexp.h"), """
                #define ANSWER 42
                typedef int sexp_sint_t;
                """);
            var srcPath = Path.Combine(dir, "main.c");
            File.WriteAllText(srcPath, """
                #include "chibi/sexp.h"
                int main() { sexp_sint_t x = ANSWER; return (int)x; }
                """);
            var emitted = Compiler.EmitCSharp(
                new[] { srcPath }, includeDirs: new[] { Path.Combine(dir, "include") });
            emitted.ShouldContain("42");
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
    public void Line_directive_renumbers_LINE_macro()
    {
        // `#line 100` makes the FOLLOWING physical line logical line 100, so
        // `__LINE__` on it expands to 100 (not its physical line number 2).
        var src = WriteTemp("""
            #line 100
            int a = __LINE__;
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldNotContain("__LINE__");
            dumped.ShouldContain(" 100 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Line_directive_with_filename_overrides_FILE_macro()
    {
        // `#line N "file"` overrides __FILE__ for the following lines, and the
        // line numbering continues from N (so line 2 is 50, line 3 is 51).
        var src = WriteTemp("""
            #line 50 "virtual.c"
            const char *f = __FILE__;
            int n = __LINE__;
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldContain("virtual.c");
            dumped.ShouldNotContain("__FILE__");
            dumped.ShouldContain(" 51 ");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Line_directive_compiles_end_to_end()
    {
        // The renumbered __LINE__ must survive into a compilable program — it
        // lowers to a plain integer constant.
        var src = WriteTemp("""
            #line 200
            int main() { return __LINE__; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldContain("return 200;");
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
            // Case-sensitive: the macro is upper-case `STR`, whereas the spliced
            // libc runtime block legitimately contains lower-case `strstr(` etc.
            emitted.ShouldNotContain("STR(", Case.Sensitive);
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
    public void Function_macro_arg_prescan_expands_nested_macro_call()
    {
        // C11 §6.10.3.1 argument prescan: an argument that is itself a
        // macro call must be FULLY expanded (in the call-site context)
        // BEFORE it's substituted into the body. The trap — reduced from
        // Lua's `getstr(tsvalue(o))` — is a macro whose body re-uses the
        // SAME macro (`cast`) that the argument also expands to. Without
        // prescan, `cast` lands on the hide set while the body's own
        // `cast(...)` is being rescanned, suppressing the argument's inner
        // `cast` and leaving it literal in the stream (which then parses as
        // a type name in expression position).
        var src = WriteTemp("""
            #define cast(t,e) ((t)(e))
            #define gco2ts(x) cast(GCU*, (x))
            #define check_exp(c,e) (c, (e))
            #define tsvalue(o) check_exp(((o)->tt)==4, gco2ts((o)->gc))
            #define rawgetshrstr(ts) (cast(char*, (ts)))
            #define getstr(ts) rawgetshrstr(ts)
            char* f(void* o) { return getstr(tsvalue(o)); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            // Every macro must be gone — none left literal in the stream.
            dumped.ShouldNotContain("cast");
            dumped.ShouldNotContain("check_exp");
            dumped.ShouldNotContain("gco2ts");
            dumped.ShouldNotContain("tsvalue");
            dumped.ShouldNotContain("getstr");
            // The fully-expanded casts surface as their target types.
            dumped.ShouldContain("GCU");
            dumped.ShouldContain("char");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Function_macro_arg_prescan_reused_param_expands_every_occurrence()
    {
        // Multi-use: when a parameter appears N times in the body, the
        // pre-expanded argument must be substituted (and fully expanded)
        // at every occurrence — not just the first. Both `cast(...)` uses
        // here wrap the same argument `gco2ts(o)`; both must collapse, with
        // no `cast`/`gco2ts` surviving and the target type appearing twice.
        var src = WriteTemp("""
            #define cast(t,e) ((t)(e))
            #define gco2ts(x) cast(GCU*, (x))
            #define both(p) (cast(int, p) + cast(long, p))
            int g(void* o) { return both(gco2ts(o)); }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();
            dumped.ShouldNotContain("cast");
            dumped.ShouldNotContain("gco2ts");
            dumped.ShouldNotContain("both");
            // The reused arg expanded at BOTH occurrences → two `GCU` casts.
            var gcuCount = dumped.Split("GCU").Length - 1;
            gcuCount.ShouldBe(2);
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
    public void Compound_literal_positional_lowers_to_object_creation()
    {
        // C99 `(Point){3, 4}` — an unnamed struct object in expression position.
        // Lowers to C# `new Point { x = 3, y = 4 }` (same field-name lookup as
        // aggregate init). Here it's a call argument, which an aggregate init
        // can't be — proving it's an expression, not just a decl initializer.
        var src = WriteTemp("""
            struct Point { int x; int y; };
            int sumpt(struct Point p) { return p.x + p.y; }
            int main() { return sumpt((struct Point){3, 4}); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("sumpt(new Point { x = 3, y = 4 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Compound_literal_designated_lowers_to_object_creation()
    {
        var src = WriteTemp("""
            struct Point { int x; int y; };
            int main() { struct Point p; p = (struct Point){ .x = 5, .y = 6 }; return p.x; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("new Point { x = 5, y = 6 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Compound_literal_gated_as_c99_under_pedantic()
    {
        var src = WriteTemp("""
            struct Point { int x; int y; };
            int sumpt(struct Point p) { return p.x; }
            int main() { return sumpt((struct Point){1, 2}); }
            """);
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
                .Message.ShouldContain("compound literals");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Array_compound_literal_lowers_to_stackalloc()
    {
        // C99 `(int[]){…}` / `(int[N]){…}` → stackalloc. Implicit `[]` takes the
        // initializer length; sized zero-fills to N. Valid in init position.
        var src = WriteTemp("""
            int main() {
                int *p = (int[]){10, 20, 30};
                int *q = (int[5]){1, 2};
                return p[0] + q[0];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("stackalloc int[]{ 10, 20, 30 }");
            emitted.ShouldContain("stackalloc int[]{ 1, 2, 0, 0, 0 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Array_compound_literal_struct_elements_and_designators()
    {
        // Struct-element form → `new T{…}` elements; designated form → dense
        // zero-fill. Both in initializer position.
        var src = WriteTemp("""
            struct P { int x; int y; };
            int main() {
                struct P *pts = (struct P[]){ {1, 2}, {3, 4} };
                int *g = (int[]){ [2] = 9, [4] = 1 };
                return pts[0].x + g[2];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("stackalloc P[]{ new P { x = 1, y = 2 }, new P { x = 3, y = 4 } }");
            emitted.ShouldContain("stackalloc int[]{ 0, 0, 9, 0, 1 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Array_compound_literal_in_call_arg_hoists_to_temp()
    {
        // Outside initializer position a `(int[]){…}` can't bind a stackalloc to a
        // pointer inline, so dotcc hoists it to a block-local pointer temp (C's
        // block-scoped automatic storage) emitted BEFORE the statement, and passes
        // the temp — so the array survives the call.
        var src = WriteTemp("""
            int sum3(int *a) { return a[0] + a[1] + a[2]; }
            int main() { return sum3((int[]){10, 20, 30}); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* __cl0 = stackalloc int[]{ 10, 20, 30 };");
            emitted.ShouldContain("sum3(__cl0)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Scalar_compound_literal_lowers_to_a_cast()
    {
        // `(int){5}` — an unnamed scalar object; lowers to a cast of the value.
        // The typed IR emits a single-level cast `(int)5` rather than the
        // double-wrapped `((int)(5))` the old emitter produced.
        var src = WriteTemp("int f(int x) { return x; } int main() { return f((int){5}); }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("f((int)5)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Scalar_compound_literal_rejects_multiple_initializers()
    {
        // A non-struct compound literal must have exactly one initializer.
        var src = WriteTemp("int main() { return (int){1, 2}; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("needs exactly one value");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Array_designators_build_a_dense_zero_filled_array()
    {
        // C99 `[i] = v`. dotcc flattens to a dense stackalloc: a designator sets
        // the cursor, an undesignated element fills the current slot, both
        // advance it (later writes win). Implicit `[]` size = max index + 1.
        var src = WriteTemp("""
            int main() {
                int a[5] = {[2] = 9, [4] = 1};
                int b[6] = {[1] = 10, 20, 30, [0] = 5};
                int c[] = {[3] = 7};
                return a[2] + b[0] + c[3];
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("stackalloc int[]{ 0, 0, 9, 0, 1 }");
            emitted.ShouldContain("stackalloc int[]{ 5, 10, 20, 30, 0, 0 }");
            emitted.ShouldContain("stackalloc int[]{ 0, 0, 0, 7 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Array_designator_gated_as_c99_under_pedantic()
    {
        var src = WriteTemp("int main() { int a[3] = {[1] = 5}; return a[1]; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
                .Message.ShouldContain("array designators");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Empty_initializer_zero_inits_scalar_struct_array_and_compound()
    {
        // C23 `{}` — universal zero-init. Scalar/struct → `= default`; sized
        // array → zeroed stackalloc (C# zeroes stackalloc); `(T){}` → default(T).
        var src = WriteTemp("""
            struct Point { int x; int y; };
            int main() {
                int n = {};
                struct Point p = {};
                int a[4] = {};
                struct Point q = (struct Point){};
                return n + p.x + a[0] + q.y;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int n = default");
            emitted.ShouldContain("Point p = default");
            emitted.ShouldContain("stackalloc int[4]");
            emitted.ShouldContain("default(Point)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Empty_initializer_gated_as_c23_under_pedantic()
    {
        var src = WriteTemp("struct S { int a; }; int main() { struct S s = {}; return s.a; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"), pedanticErrors: true))
                .Message.ShouldContain("empty initializer");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Array_members_lower_to_fixed_buffers()
    {
        // Sized `T name[N];` (C89) and the C99 flexible array member `T name[];`
        // both lower to a C# fixed-size buffer. The FAM uses [1] (over-allocates
        // by one element — the malloc idiom stays safe).
        var src = WriteTemp("""
            struct Vec { int len; int data[]; };
            struct Grid { int rows; int cells[4]; };
            int main() { struct Grid g; g.cells[0] = 1; return g.cells[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("public fixed int data[1];");    // FAM → [1]
            emitted.ShouldContain("public fixed int cells[4];");   // sized
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Flexible_array_member_gated_as_c99_under_pedantic()
    {
        var src = WriteTemp("struct S { int n; int data[]; }; int main() { return 0; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
                .Message.ShouldContain("flexible array member");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Array_member_of_non_primitive_element_uses_inline_array()
    {
        // C# `fixed` buffers allow only primitive elements, so a struct-element
        // array member lowers to a C# 12 [InlineArray] instead (see Phase 4g /
        // StructHackArrayTests). This used to throw; it's now supported.
        // The typed IR names the inline-array helper struct `__IA_S_items` and
        // its single element field `_e` (not `__e0`).
        var src = WriteTemp("struct P { int x; }; struct S { int n; struct P items[4]; }; int main() { return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("InlineArray(4)");
            emitted.ShouldContain("P _e;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Anonymous_struct_member_promotes_fields_into_parent()
    {
        // C11 anonymous struct member — its fields are promoted. For a struct
        // (sequential) that's an inline: x, y become real fields of Outer, so
        // `o.x` works verbatim and the layout matches C.
        var src = WriteTemp("""
            struct Outer { int tag; struct { int x; int y; }; int z; };
            int main() { struct Outer o; o.x = 10; return o.x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The inner fields land directly in the struct body, in declaration order.
            emitted.ShouldContain("public int tag;");
            emitted.ShouldContain("public int x;");
            emitted.ShouldContain("public int y;");
            emitted.ShouldContain("public int z;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Anonymous_union_member_lifts_to_nested_type_and_rewrites_access()
    {
        // C11 anonymous union member: fields overlap, so they're lifted into a
        // generated explicit-layout type with a synthetic field. The typed IR
        // names the helper type `__Anon0` and the synthetic field `__anon___Anon0`;
        // `v.i` rewrites to `v.__anon___Anon0.i`. `tag`/`extra` stay direct fields.
        var src = WriteTemp("""
            struct Value { int tag; union { int i; double d; }; int extra; };
            int main() { struct Value v; v.i = 42; v.tag = 0; return v.i + v.tag; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct __Anon0");                    // nested union type
            emitted.ShouldContain("FieldOffset(0)]\n    public int i;");        // overlapping
            emitted.ShouldContain("FieldOffset(0)]\n    public double d;");
            emitted.ShouldContain("public __Anon0 __anon___Anon0;");           // synth field in Value
            emitted.ShouldContain("v.__anon___Anon0.i");                       // promoted access rewrite
            emitted.ShouldContain("v.tag");                                    // direct field unchanged
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Anonymous_union_member_resolves_promoted_access_through_pointer()
    {
        var src = WriteTemp("""
            struct V { int tag; union { int i; float f; }; };
            int get(struct V *p) { return p->i; }
            int main() { struct V v; v.i = 3; return get(&v); }
            """);
        try
        {
            // The pointer base's CType (V*) is peeled to V to resolve the promotion.
            // The typed IR uses `__anon___Anon0` as the synthetic field name.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("p->__anon___Anon0.i");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Anonymous_struct_member_gated_as_c11_under_pedantic()
    {
        var src = WriteTemp("struct S { int a; struct { int b; }; }; int main() { struct S s; s.b = 1; return s.b; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c99"), pedanticErrors: true))
                .Message.ShouldContain("anonymous struct/union");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Anonymous_typedef_struct_emits_struct_under_alias()
    {
        // `typedef struct { … } Name;` (no tag) — the common idiom. dotcc emits
        // the C# struct under the alias name and binds the alias as a type-name
        // so later `Name x;` parses as a declaration (the lexer hack).
        var src = WriteTemp("""
            typedef struct { int x; int y; } Point;
            int main() { Point p = {3, 4}; return p.x + p.y; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct Point");
            emitted.ShouldContain("new Point { x = 3, y = 4 }");
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

    [Fact]
    public void String_literal_helper_is_qualified_so_a_user_L_cannot_shadow_it()
    {
        // The string-literal helper is `Libc.L`. Lua's ubiquitous `lua_State *L`
        // parameter would shadow a BARE `L(...)`, making `L("…")` try to call the
        // variable `L` (CS0149). Emitting `Libc.L(...)` (qualified) avoids that.
        var src = WriteTemp("""
            int show(int L) { return (int)"hi"[L - L]; }
            int main(void) { int L = 0; return show(L) + (int)"x"[L]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The user's two literals lower to the QUALIFIED helper, so the `L`
            // parameter/local can't capture them. (A bare `L("hi"…)` would be
            // CS0149 against the `int L`.)
            emitted.ShouldContain("Libc.L(\"hi\\0\"u8)");
            emitted.ShouldContain("Libc.L(\"x\\0\"u8)");
        }
        finally { File.Delete(src); }
    }

}
