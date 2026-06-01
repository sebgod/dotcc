#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

public sealed partial class CompilerTests
{
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
            // Promoted to `_Bool` → tsBool → integer-typed Libc.CBool. The
            // relational `5 > 3` itself lowers to CBool (C's int-valued result),
            // and lands in the CBool slot via CBool's implicit conversions.
            emitted.ShouldContain("CBool gt = ((CBool)(5 > 3));");
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
            emitted.ShouldContain("CBool gt = ((CBool)(5 > 3));");
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

    // ---- CBool: pointer store coerces to truthiness (the `_Bool b = ptr;`
    // case) — verified directly against the runtime type, since the whole
    // point is that the coercion lives on CBool's implicit operators, not in
    // the emitter. C# permits a user-defined conversion from `void*`; a typed
    // `T*` reaches it via the standard `T* → void*` conversion.
    [Fact]
    public unsafe void CBool_converts_a_pointer_to_its_truthiness()
    {
        int n = 7;
        int* live = &n;
        int* dead = null;
        // int* -> void* (standard) -> CBool (user-defined): one of each, allowed.
        Libc.CBool a = live;
        Libc.CBool b = dead;
        Libc.CBool c = (void*)0;
        ((int)a).ShouldBe(1);   // non-null -> 1
        ((int)b).ShouldBe(0);   // NULL     -> 0
        ((int)c).ShouldBe(0);
    }

    [Fact]
    public void CBool_normalizes_scalars_to_zero_or_one()
    {
        ((int)(Libc.CBool)5).ShouldBe(1);      // any nonzero int -> 1
        ((int)(Libc.CBool)0).ShouldBe(0);
        ((int)(Libc.CBool)3.14).ShouldBe(1);   // double store also normalizes
        ((int)(Libc.CBool)true).ShouldBe(1);
        ((int)(Libc.CBool)false).ShouldBe(0);
    }

    // ---- Dialect gating (-pedantic / -pedantic-errors) --------------------
    // dotcc parses the union of all dialects; the gate REJECTS input features
    // that postdate the selected -std (gcc -pedantic-errors model). Opt-in:
    // plain -std= stays permissive. Output lowering is unaffected.

    [Fact]
    public void Dialect_gate_is_off_by_default()
    {
        // _Bool is a C99 feature; under c90 WITHOUT pedantic it must still emit.
        var src = WriteTemp("int main() { _Bool b = 1; return (int)b; }");
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90")));
        }
        finally { File.Delete(src); }
    }

    [Theory]
    [InlineData("int main() { _Bool b = 1; return 0; }", "c90", "_Bool", "C99")]
    [InlineData("int main() { long long x = 5; return (int)x; }", "c90", "long long", "C99")]
    [InlineData("int main() { for (int i = 0; i < 3; i++) {} return 0; }", "c90", "for", "C99")]
    [InlineData("_Static_assert(1, \"ok\");\nint main() { return 0; }", "c99", "_Static_assert", "C11")]
    [InlineData("enum Color : unsigned char { Red };\nint main() { return 0; }", "c17", "enum", "C23")]
    [InlineData("#define LOG(fmt, ...) fmt\nint main() { return 0; }", "c90", "variadic macro", "C99")]
    [InlineData("#warning hi\nint main() { return 0; }", "c17", "#warning", "C23")]
    [InlineData("int main() { int x = 1; x = x + 1; int y = 2; return x + y; }", "c90", "mixed declarations", "C99")]
    public void Pedantic_errors_rejects_too_new_feature(string body, string std, string featureNeedle, string stdNeedle)
    {
        var src = WriteTemp(body);
        try
        {
            var ex = Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse(std), pedanticErrors: true));
            ex.Message.ShouldContain(featureNeedle);
            ex.Message.ShouldContain(stdNeedle);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Pedantic_errors_allows_feature_at_its_own_standard()
    {
        // _Bool under c99 (its own standard) must NOT be gated.
        var src = WriteTemp("int main() { _Bool b = 1; return (int)b; }");
        try
        {
            Should.NotThrow(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c99"), pedanticErrors: true));
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Mixed_decl_gate_does_not_flag_a_nested_block_leading_decl()
    {
        // A declaration at the START of a nested block, even after a statement
        // in the OUTER block, is legal C90 (the nested block is its own scope).
        // The per-block accumulator must not raise a false positive here.
        var src = WriteTemp("int main() { int n = 0; n++; { int y = 2; n += y; } return n; }");
        try
        {
            Should.NotThrow(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true));
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Mixed_decl_gate_allows_all_declarations_first()
    {
        // Declarations before any statement is the canonical C90-legal shape.
        var src = WriteTemp("int main() { int x = 1; int y = 2; x++; return x + y; }");
        try
        {
            Should.NotThrow(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true));
        }
        finally { File.Delete(src); }
    }

    // ---- sizeof of an EXPRESSION (the type-synthesis layer) ---------------
    // The operand's synthesized CType drives the size: arrays compute
    // count*sizeof(element) (the C# pointer-lowering makes C# sizeof wrong);
    // everything else defers to C# sizeof(type).

    [Fact]
    public void Sizeof_array_length_idiom_lowers_to_count_times_elemsize()
    {
        var src = WriteTemp("int main() { int a[5]; int n = sizeof(a) / sizeof(a[0]); return n; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // sizeof(a) → 5*sizeof(int); sizeof(a[0]) → element type int.
            emitted.ShouldContain("((5 * sizeof(int)) / sizeof(int))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Sizeof_resolves_literals_casts_and_strings()
    {
        var src = WriteTemp(
            "int main() { int c = sizeof('a'); int d = sizeof((char)'a'); int s = sizeof(\"hello\"); return c + d + s; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int c = sizeof(int);");      // C char-literal is int
            emitted.ShouldContain("int d = sizeof(byte);");     // cast to char → byte
            emitted.ShouldContain("int s = (6 * sizeof(byte));");// "hello" = 5 chars + NUL
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Sizeof_type_form_is_unchanged()
    {
        var src = WriteTemp("int main() { int b = sizeof(int); return b; }");
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("int b = sizeof(int);");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Sizeof_of_unsupported_operand_fails_clearly()
    {
        // A non-additive arithmetic result has no pointer/array type, so its
        // sizeof can't be synthesized — dotcc must fail loudly. (Member access,
        // pointer arithmetic, and comma ARE now resolved — see SizeofMemberTests.)
        var src = WriteTemp("int main() { int a = 3, b = 4; int n = sizeof(a * b); return n; }");
        try
        {
            var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }));
            ex.Message.ShouldContain("sizeof");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Pedantic_errors_collects_all_violations_at_once()
    {
        // Two distinct C99 features under c90 — both must appear in one error.
        var src = WriteTemp("int main() { _Bool b = 1; long long x = 5; return (int)b + (int)x; }");
        try
        {
            var ex = Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true));
            ex.Message.ShouldContain("_Bool");
            ex.Message.ShouldContain("long long");
        }
        finally { File.Delete(src); }
    }

    // ---- Relational / logical operators yield C `int` 0/1 -----------------
    // They lower to CBool so the result is usable in any integer position
    // (assignment, arithmetic, argument, return) — not just conditionals.
    [Fact]
    public void Relational_and_logical_results_lower_to_CBool()
    {
        var src = WriteTemp(
            "int main() { int a=5,b=3; int x = a > b; int f = a && b; int e = (a == b); return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int x = ((CBool)(a > b));");          // relational
            emitted.ShouldContain("(CBool)(Cond.B(a) && Cond.B(b))");    // logical &&
            emitted.ShouldContain("(CBool)(a == b)");                    // equality
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

    // ---- DialectKeywordRewriter: C99 `inline` promotion -------------------
    // C99 makes `inline` a function specifier. The rewriter promotes the bare
    // `inline` ID onto the `inline` terminal, but ONLY from the C99 era on
    // (c99/c11/c17/c23 — the default c17 included). Pre-C99 `inline` stays an
    // ordinary identifier. The flagged Type makes the FnSig path emit a
    // [MethodImpl(MethodImplOptions.AggressiveInlining)] on the method.

    [Fact]
    public void Inline_function_emits_aggressive_inlining_attribute()
    {
        var src = WriteTemp("inline int sq(int x) { return x * x; } int main() { return sq(4); }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"));
            // The attribute lands immediately before the method, and the method
            // is otherwise an ordinary static unsafe local function.
            emitted.ShouldContain("[MethodImpl(MethodImplOptions.AggressiveInlining)]\nstatic unsafe int sq(int x)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Static_and_extern_inline_both_get_the_attribute()
    {
        var src = WriteTemp("""
            static inline int cube(int x) { return x * x * x; }
            extern inline long add(long a, long b) { return a + b; }
            int main() { return cube(2) + (int)add(1, 2); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"));
            emitted.ShouldContain("[MethodImpl(MethodImplOptions.AggressiveInlining)]\nstatic unsafe int cube(int x)");
            emitted.ShouldContain("[MethodImpl(MethodImplOptions.AggressiveInlining)]\nstatic unsafe long add(long a, long b)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Non_inline_function_has_no_attribute()
    {
        // Guard against the attribute leaking onto ordinary functions.
        var src = WriteTemp("int plain(int x) { return x; } int main() { return plain(0); }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"));
            emitted.ShouldContain("static unsafe int plain(int x)");
            emitted.ShouldNotContain("[MethodImpl(MethodImplOptions.AggressiveInlining)]\nstatic unsafe int plain");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Inline_is_an_identifier_pre_c99_but_a_keyword_from_c99()
    {
        // `int inline = 5;` is valid C89 — `inline` is an ordinary identifier,
        // so it parses under c90 (and emits a variable named `inline`, which is
        // not a C# keyword so it needs no escaping). From c99 on `inline` is a
        // keyword, so using it as a declarator name is a parse error — proving
        // the rewriter's version gate flipped. CDialect.Version is keyed by year
        // (1990/1999/2011/2017/2023), so c11/c17/c23 all correctly reject it via
        // a plain monotonic `Version >= 1999`.
        var src = WriteTemp("int main() { int inline = 5; return inline; }");
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90")));
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c99")));
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17")));
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23")));
        }
        finally { File.Delete(src); }
    }

    // ---- _Noreturn (C11) / noreturn (C23) ---------------------------------

    [Fact]
    public void Noreturn_function_emits_does_not_return_attribute()
    {
        // `_Noreturn` is always a keyword (reserved underscore-uppercase). The
        // body is an infinite loop so the emitted [DoesNotReturn] is honest.
        var src = WriteTemp("_Noreturn void die(void) { for (;;) {} } int main() { return 0; }");
        try
        {
            Compiler.EmitCSharp(new[] { src })
                .ShouldContain("[System.Diagnostics.CodeAnalysis.DoesNotReturn]\nstatic unsafe void die()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Noreturn_gated_as_c11_under_pedantic()
    {
        var src = WriteTemp("_Noreturn void die(void) { for (;;) {} } int main() { return 0; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90"), pedanticErrors: true))
                .Message.ShouldContain("_Noreturn");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Typeof_yields_expression_and_type_operand_types()
    {
        // C23 typeof: expr form reads the operand's CType; type form unwraps.
        var src = WriteTemp("""
            int main() {
                int x = 5;
                typeof(x) y = 1;
                typeof(int) z = 2;
                double d = 0.0;
                typeof(d) e = 0.0;
                int *p = &x;
                typeof(p) q = p;
                return y + z + (int)e + *q;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("int y = 1;");
            emitted.ShouldContain("int z = 2;");
            emitted.ShouldContain("double e = 0.0;");
            emitted.ShouldContain("int* q = p;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Typeof_is_an_identifier_pre_c23_but_a_keyword_in_c23()
    {
        // Pre-C23 `typeof` is an ordinary identifier; under c23 it's the operator,
        // so using it as a declarator name is a parse error (rule-2 gate).
        var asVar = WriteTemp("int main() { int typeof = 5; return typeof; }");
        var asKeyword = WriteTemp("int main() { int x = 1; typeof(x) y = 2; return x + y; }");
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { asVar }, dialect: CDialect.Parse("c17")));
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { asVar }, dialect: CDialect.Parse("c23")));
            // The operator form: works under c23, parse error pre-C23.
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { asKeyword }, dialect: CDialect.Parse("c23")));
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { asKeyword }, dialect: CDialect.Parse("c17")));
        }
        finally { File.Delete(asVar); File.Delete(asKeyword); }
    }

    [Fact]
    public void Lowercase_noreturn_is_an_identifier_pre_c23_but_a_keyword_in_c23()
    {
        // Lowercase `noreturn` is a C23 keyword (promoted onto _Noreturn). Pre-C23
        // it's an ordinary identifier, so it can name a variable; under c23 that
        // same use is a parse error — proving the rewriter's era gate.
        var asVar = WriteTemp("int main() { int noreturn = 5; return noreturn; }");
        var asKeyword = WriteTemp("noreturn void die(void) { for (;;) {} } int main() { return 0; }");
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { asVar }, dialect: CDialect.Parse("c17")));
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { asVar }, dialect: CDialect.Parse("c23")));
            // And the keyword form: works under c23, parse error pre-C23.
            Compiler.EmitCSharp(new[] { asKeyword }, dialect: CDialect.Parse("c23"))
                .ShouldContain("[System.Diagnostics.CodeAnalysis.DoesNotReturn]");
            Should.Throw<CompileException>(
                () => Compiler.EmitCSharp(new[] { asKeyword }, dialect: CDialect.Parse("c17")));
        }
        finally { File.Delete(asVar); File.Delete(asKeyword); }
    }

}
