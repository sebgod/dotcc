#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Pins for <see cref="MacroEngine"/>, the one macro-replacement engine behind
/// text, arguments, <c>#if</c> and <c>#line</c>: the C standard's own examples
/// (C11 6.10.3.5), per-token hide sets (GH #240, the <c>obmalloc.c</c> case of
/// GH #219), arguments collected unexpanded (<c>#</c> / <c>##</c> see them as
/// written, a comma from an object-like macro does not split them), chained
/// <c>##</c> (GH #218), a replacement taking its arguments from the source that
/// follows it, and the invocation errors gcc reports.
/// </summary>
[Collection("MacroEngine")]
public sealed class MacroEngineTests
{
    /// <summary>Preprocess <paramref name="source"/> (<c>-E</c>) and return the
    /// token stream after the file-name comment line.</summary>
    private static string Pp(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-mac-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, source);
        try
        {
            var sw = new StringWriter();
            Compiler.Preprocess(new[] { path }, sw);
            var text = sw.ToString();
            return text[(text.IndexOf('\n') + 1)..];
        }
        finally { File.Delete(path); }
    }

    /// <summary>The preprocessed text with all white space removed, for
    /// comparisons where token spacing does not matter.</summary>
    private static string Flat(string source) => Regex.Replace(Pp(source), @"\s+", "");

    /// <summary><paramref name="expected"/> with all white space removed.</summary>
    private static string Squeeze(string expected) => Regex.Replace(expected, @"\s+", "");

    [Fact]
    public void Standard_example_3_rescanning_and_hide_sets()
    {
        // C11 6.10.3.5 EXAMPLE 3.
        Flat("""
            #define x 3
            #define f(a) f(x * (a))
            #undef x
            #define x 2
            #define g f
            #define z z[0]
            #define h g(~
            #define m(a) a(w)
            #define w 0,1
            #define t(a) a
            #define p() int
            #define q(x) x
            #define r(x,y) x ## y
            #define str(x) # x
            f(y+1) + f(f(z)) % t(t(g)(0) + t)(1);
            g(x+(3,4)-w) | h 5) & m
            (f)^m(m);
            p() i[q()] = { q(1), r(2,3), r(4,), r(,5), r(,) };
            char c[2][6] = { str(hello), str() };
            """).ShouldBe(Squeeze("""
            f(2 * (y+1)) + f(2 * (f(2 * (z[0])))) % f(2 * (0)) + t(1);
            f(2 * (2+(3,4)-0,1)) | f(2 * (~ 5)) & f(2 * (0,1))^m(0,1);
            int i[] = { 1, 23, 4, 5, };
            char c[2][6] = { "hello", "" };
            """));
    }

    [Fact]
    public void Standard_example_5_placemarkers()
    {
        // C11 6.10.3.5 EXAMPLE 5: an empty argument pastes as nothing.
        Flat("""
            #define t(x,y,z) x ## y ## z
            int j[] = { t(1,2,3), t(,4,5), t(6,,7), t(8,9,),
                t(10,,), t(,11,), t(,,12), t(,,) };
            """).ShouldBe(Squeeze("int j[] = { 123, 45, 67, 89, 10, 11, 12, };"));
    }

    [Fact]
    public void Standard_example_7_variable_arguments()
    {
        // C11 6.10.3.5 EXAMPLE 7, including `#__VA_ARGS__` keeping the source's
        // spacing ("The first, second, and third items.").
        var pp = Pp("""
            #define debug(...) fprintf(stderr, __VA_ARGS__)
            #define showlist(...) puts(#__VA_ARGS__)
            #define report(test, ...) ((test)?puts(#test): printf(__VA_ARGS__))
            debug("Flag");
            debug("X = %d\n", x);
            showlist(The first, second, and third items.);
            report(x>y, "x is %d but y is %d", x, y);
            """);
        pp.ShouldContain("\"The first, second, and third items.\"");
        pp.ShouldContain("\"x>y\"");
        Regex.Replace(pp, @"\s+", "").ShouldBe(Squeeze("""
            fprintf(stderr, "Flag");
            fprintf(stderr, "X = %d\n", x);
            puts("The first, second, and third items.");
            ((x>y)?puts("x>y"): printf("x is %d but y is %d", x, y));
            """));
    }

    [Fact]
    public void A_painted_name_stays_unexpanded_through_another_macros_arguments()
    {
        // GH #240: CPython's `PyObject_GC_New(T, Py_TYPE(o))`.
        Flat("""
            #define CAST(t, e) ((t)(e))
            #define OCAST(o) CAST(int *, (o))
            #define TY(o) TY(OCAST(o))
            #define NEW(t, o) CAST(t *, mk(o))
            NEW(char, TY(p))
            """).ShouldBe(Squeeze("((char *)(mk(TY(((int *)((p)))))))"));
    }

    [Fact]
    public void A_self_referential_object_like_macro_in_an_argument_expands_once()
    {
        // GH #219 (obmalloc.c): `assert(usable_arenas == NULL)`.
        Flat("""
            #define ua (s->ua)
            #define as(e) ((e) ? 1 : 0)
            as(ua == 0);
            """).ShouldBe(Squeeze("(((s->ua) == 0) ? 1 : 0);"));
    }

    [Fact]
    public void Arguments_are_collected_before_expansion()
    {
        // `#` and `##` see the argument as written; a comma an object-like macro
        // expands to does not split the argument list.
        Flat("""
            #define N 5
            #define STR(x) #x
            #define XSTR(x) STR(x)
            #define CAT(a, b) a ## b
            #define W 0,1
            #define ONE(a) [a]
            STR(N) XSTR(N) CAT(x, N) ONE(W)
            """).ShouldBe(Squeeze("\"N\" \"5\" xN [0,1]"));
    }

    [Fact]
    public void A_replacement_takes_its_arguments_from_the_text_that_follows()
    {
        Flat("""
            #define ff(x) gg
            #define gg(y) [y]
            #define A hh(x)
            #define hh(x) A
            ff(1)(2) A
            """).ShouldBe(Squeeze("[2] A"));
    }

    [Fact]
    public void Chained_paste_and_pasted_tokens_relex()
    {
        // GH #218, and a paste that forms a number or an operator is that token.
        var pp = Pp("""
            #define C3(a, b, c) a ## b ## c
            #define U(x) x ## ULL
            #define OP(a, b) a ## b
            C3(x, y, z) U(1) OP(+, =)
            """);
        Regex.Split(pp.Trim(), @"\s+").ShouldBe(new[] { "xyz", "1ULL", "+=" });
    }

    [Fact]
    public void Gnu_comma_paste_before_va_args()
    {
        Flat("""
            #define E(fmt, ...) f(fmt, ##__VA_ARGS__)
            E(1); E(1, 2, 3);
            """).ShouldBe(Squeeze("f(1); f(1, 2, 3);"));
    }

    [Fact]
    public void Line_is_the_line_of_the_invocation()
    {
        // `__LINE__` from a replacement reports the outermost invocation's line;
        // `#` stringizes the name as written.
        Flat("""
            #define L __LINE__
            #define STR(x) #x
            #define XSTR(x) STR(x)
            L STR(__LINE__) XSTR(__LINE__)
            """).ShouldBe(Squeeze("4 \"__LINE__\" \"4\""));
    }

    [Fact]
    public void Conditions_are_macro_replaced_like_text()
    {
        // Function-like macros nested in a condition, `defined` in both forms,
        // and `defined` protecting its operand from replacement.
        Flat("""
            #define F(x) ((x) + 1)
            #define G(y) F(F(y))
            #define ON 1
            #if G(1) == 3 && defined(F) && defined ON && !defined(OFF)
            yes
            #else
            no
            #endif
            #if ON == 2
            wrong
            #elif F(ON) == 2
            elif
            #endif
            """).ShouldBe(Squeeze("yes elif"));
    }

    [Theory]
    [InlineData("#define F(a, b) a\nF(1)\n", "macro \"F\" requires 2 arguments, but only 1 given")]
    [InlineData("#define F(a) a\nF(1, 2)\n", "macro \"F\" passed 2 arguments, but takes just 1")]
    [InlineData("#define F(a, b, ...) a\nF(1)\n", "macro \"F\" requires at least 2 arguments, but only 1 given")]
    [InlineData("#define F(a) a\nF(1\n", "unterminated argument list invoking macro \"F\"")]
    [InlineData("#define P(a, b) a ## b\nP(x, +)\n", "pasting \"x\" and \"+\" does not give a valid preprocessing token")]
    public void Invocation_errors_are_reported_like_gcc(string source, string message)
    {
        Should.Throw<CompileException>(() => Pp(source)).Message.ShouldContain(message);
    }

    [Fact]
    public void An_empty_argument_list_passes_no_arguments_to_a_parameterless_macro()
    {
        Flat("#define P() int\n#define Q(x) [x]\nP() Q()\n").ShouldBe(Squeeze("int []"));
    }
}
