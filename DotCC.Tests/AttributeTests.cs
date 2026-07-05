#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C23 `[[attributes]]`. The glued `[[` opener token selects the
/// attribute path (valid C never has adjacent `[[` outside one); the closer is
/// two ordinary `]` tokens, so nested subscripts (`a[b[0]]`) are unaffected.
/// The recognized attrs are LOWERED: `[[noreturn]]` (and the `_Noreturn` /
/// C23-promoted `noreturn` specifier) → `[DoesNotReturn]` on the emitted method,
/// `[[deprecated[("msg")]]]` → `[System.Obsolete(…)]`, `[[nodiscard]]` → a
/// discard warning, and `[[maybe_unused]]` on a block-scope declaration →
/// `#pragma warning disable/restore CS0168, CS0219` bracketing the local so C#
/// doesn't warn if it stays unused. `[[fallthrough]];` parses as an
/// attribute-wrapped empty statement. The C23 purity hints `[[unsequenced]]` /
/// `[[reproducible]]` and every vendor-namespaced attr are recognized-but-inert
/// (no teeth-bearing .NET counterpart). End-to-end in the `c23-attributes/` and
/// `noreturn-specifier/` fixtures.
/// </summary>
[Collection("Attributes")]
public sealed class AttributeTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-attr-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Attributes_on_declarations_are_accepted_and_ignored()
    {
        // File-scope fn def + prototype, block-scope local, chained specs,
        // and every V1 attr shape: bare, call-with-string, call-with-const,
        // namespaced, empty-args.
        var src = WriteTemp("""
            [[nodiscard]] int answer(void);
            [[deprecated("use answer")]] [[maybe_unused]] int old_answer(void) { return 41; }
            [[gnu::aligned(16)]] int aligned_global = 5;
            int answer(void) { return 42; }
            int main(void) {
                [[maybe_unused]] int local = old_answer();
                return answer() - 42 + aligned_global - 5;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main()");
            // No attribute SYNTAX survives into the emitted C#…
            emitted.ShouldNotContain("[[");
            emitted.ShouldNotContain("nodiscard");
            emitted.ShouldNotContain("deprecated");
            // …but the recognized `deprecated` lowers to [Obsolete] with the
            // decoded message (the C warning surfaces at the .NET build's call sites).
            emitted.ShouldContain("[System.Obsolete(\"use answer\")]");
            // …and the block-scope [[maybe_unused]] local is bracketed so the
            // emitted C# doesn't warn that `local` is never read. (`maybe_unused`
            // on the FUNCTION old_answer stays a no-op — C# never warns on an
            // unused internal method.)
            emitted.ShouldContain("#pragma warning disable CS0168, CS0219");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Maybe_unused_local_is_bracketed_by_a_scoped_suppression()
    {
        // A block-scope [[maybe_unused]] local → the DeclStmt carries MaybeUnused,
        // and the backend wraps exactly that declaration with a disable/restore pair
        // (CS0168 declared-never-used + CS0219 assigned-never-used) — the faithful
        // lowering of C's "don't warn if this is unused".
        var src = WriteTemp("""
            int main(void) {
                [[maybe_unused]] int scratch = 7;
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("#pragma warning disable CS0168, CS0219");
            emitted.ShouldContain("#pragma warning restore CS0168, CS0219");
            // The suppression brackets the local, not the whole method.
            var disable = emitted.IndexOf("#pragma warning disable CS0168", System.StringComparison.Ordinal);
            var scratch = emitted.IndexOf("scratch", System.StringComparison.Ordinal);
            var restore = emitted.IndexOf("#pragma warning restore CS0168", System.StringComparison.Ordinal);
            disable.ShouldBeLessThan(scratch);
            scratch.ShouldBeLessThan(restore);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Ordinary_unused_local_gets_no_suppression()
    {
        // Regression: only a [[maybe_unused]]-marked local is bracketed. A plain
        // unused local keeps its ordinary emit (the C# unused-variable warning is
        // left intact — the whole point of the attribute is to opt OUT of it).
        var src = WriteTemp("""
            int main(void) {
                int scratch = 7;
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("scratch");
            emitted.ShouldNotContain("#pragma warning disable CS0168");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Unsequenced_and_reproducible_are_recognized_but_inert()
    {
        // The C23 purity hints have no teeth-bearing .NET counterpart (mapping to
        // the vestigial [Pure] would be cosmetic), so they are accepted, gated C23,
        // and emit nothing — no attribute survives, no suppression pragma fires. The
        // functions still emit (they compiled), proving the attrs were consumed.
        var src = WriteTemp("""
            [[unsequenced]] int uns_add(int a, int b);
            [[reproducible]] int rep_id(int a);
            int uns_add(int a, int b) { return a + b; }
            int rep_id(int a) { return a; }
            int main(void) { return uns_add(1, -1) + rep_id(0); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldContain("uns_add");
            emitted.ShouldContain("rep_id");
            // No attribute SYNTAX leaks, and specifically no cosmetic [Pure] lowering…
            emitted.ShouldNotContain("[[");
            emitted.ShouldNotContain("[System.Diagnostics.Contracts.Pure]");
            // …and neither hint trips the [[maybe_unused]] local suppression.
            emitted.ShouldNotContain("#pragma warning disable CS0168");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Deprecated_without_message_maps_to_bare_obsolete()
    {
        var src = WriteTemp("""
            [[deprecated]] int old_api(void) { return 1; }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("[System.Obsolete]");
            emitted.ShouldNotContain("[System.Obsolete(");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Noreturn_attribute_on_the_prototype_marks_the_definition()
    {
        // The marker rides the SHARED function symbol, so spelling the attribute
        // on the prototype alone still puts [DoesNotReturn] on the emitted method
        // — and pre-C23 `[[noreturn]]` arrives as a plain identifier (no keyword
        // promotion), exercising the bare-ID recognition path.
        var src = WriteTemp("""
            [[noreturn]] void die(void);
            void die(void) { for (;;) {} }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c17"));
            emitted.ShouldContain("[System.Diagnostics.CodeAnalysis.DoesNotReturn]");
            emitted.ShouldContain("static unsafe void die()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Fallthrough_attribute_declaration_is_an_empty_statement()
    {
        // `[[fallthrough]];` wraps the empty statement; the switch machinery's
        // explicit-jump insertion (goto case) still fires for the section.
        var src = WriteTemp("""
            int classify(int x) {
                int r = 0;
                switch (x) {
                case 1:
                    r += 1;
                    [[fallthrough]];
                case 2:
                    r += 2;
                    break;
                default:
                    break;
                }
                return r;
            }
            int main(void) { return classify(1); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("goto case 2");
            emitted.ShouldNotContain("fallthrough");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Noreturn_attribute_arrives_as_the_promoted_keyword_under_c23()
    {
        // Under -std=c23 the rewriter promotes bare `noreturn` onto the
        // `_Noreturn` terminal BEFORE parsing, so `[[noreturn]]` needs (and
        // exercises) the keyword-shaped Attr production.
        var src = WriteTemp("""
            #include <stdlib.h>
            [[noreturn]] void die(void) { exit(1); }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c23"));
            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldNotContain("[[");
            // The keyword-shaped Attr production feeds the same marker as the
            // bare-ID path: [DoesNotReturn] lands on the emitted method.
            emitted.ShouldContain("[System.Diagnostics.CodeAnalysis.DoesNotReturn]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Nested_subscripts_still_lex_as_two_brackets()
    {
        // The regression the glued-token design must not cause: `a[b[0]]` ends
        // in adjacent `]]`, and `[` never glues except for a real `[[`.
        var src = WriteTemp("""
            int main(void) {
                int inner[2] = { 1, 0 };
                int outer[2] = { 42, 7 };
                return outer[inner[1]] - 42;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("outer[inner[1]]");
        }
        finally { File.Delete(src); }
    }
}
