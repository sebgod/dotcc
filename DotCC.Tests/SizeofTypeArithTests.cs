#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <c>sizeof(Type)</c> followed by a binary operator that ALSO
/// has a unary-prefix form (<c>*</c> deref, <c>-</c> negate, <c>&amp;</c>
/// address-of). When the type is a struct/union (size not foldable at lex time),
/// the parser would otherwise read <c>sizeof(S) * x</c> as <c>sizeof((S)*x)</c>
/// — a cast of a deref — silently dropping the operator. <see cref="SizeofFolder"/>
/// wraps the unfoldable sizeof in parens so the operator binds as binary.
/// End-to-end in the <c>sizeof-struct/</c> fixture (gcc-oracle-validated).
/// </summary>
public sealed class SizeofTypeArithTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sza-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private const string Decls = "typedef struct S { double n; int b; } S;\n";

    private static string Probe(string expr) =>
        $"{Decls}int probe(void) {{ return (int)({expr}); }}\nint main(void) {{ return 0; }}\n";

    [Fact]
    public void sizeof_struct_times_constant_keeps_the_multiply()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(S) * 2")) });
        // The wrapped sizeof is a complete primary; the `* 2` survives.
        emitted.ShouldContain("((ulong)sizeof(S)) * (ulong)(2)");
    }

    [Fact]
    public void sizeof_struct_minus_constant_keeps_the_subtract()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(S) - 1")) });
        emitted.ShouldContain("((ulong)sizeof(S)) - (ulong)(1)");
    }

    [Fact]
    public void sizeof_struct_bitand_constant_keeps_the_and()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(S) & 240")) });
        emitted.ShouldContain("((ulong)sizeof(S)) & (ulong)(240)");
    }

    [Fact]
    public void sizeof_struct_plus_constant_is_unaffected()
    {
        // `+` has no unary form, so it was never absorbed and needs no wrap.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(S) + 2")) });
        emitted.ShouldContain("(ulong)sizeof(S) + (ulong)(2)");
    }

    [Fact]
    public void foldable_sizeof_times_constant_still_folds_to_a_number()
    {
        // `sizeof(int) * 8` — a foldable scalar type folds to a NUM in the token
        // stream (no `sizeof(...)` survives), so the wrap path never applies.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(int) * 8")) });
        emitted.ShouldNotContain("sizeof(int)");
        emitted.ShouldContain("4 * 8");
    }

    [Fact]
    public void malloc_sizeof_struct_peephole_is_not_wrapped()
    {
        // `malloc(sizeof(S))` is followed by `)`, not an operator, so the sizeof
        // stays bare and the malloc→stack-value peephole still recognises it.
        var src = $"{Decls}#include <stdlib.h>\n"
            + "int main(void) { S *p = (S*)malloc(sizeof(S)); p->b = 7; int r = p->b; free(p); return r; }\n";
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(src) });
        emitted.ShouldContain("new S()");          // peephole fired
    }
}
