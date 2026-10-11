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
/// a grammar whose unary operand could be a cast would read <c>sizeof(S) * x</c>
/// as <c>sizeof((S)*x)</c> (a cast of a deref), silently dropping the operator.
/// The C grammar's cast-expression tier (C11 6.5.4: <c>sizeof</c> takes a
/// unary-expression, never a cast) makes the operator bind as binary with no
/// token rewriting. End-to-end in the <c>sizeof-struct/</c> fixture
/// (gcc-oracle-validated).
/// </summary>
[Collection("SizeofTypeArith")]
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
        emitted.ShouldContain("((ulong)(sizeof(S))) * (ulong)(2)");
    }

    [Fact]
    public void sizeof_struct_minus_constant_keeps_the_subtract()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(S) - 1")) });
        emitted.ShouldContain("((ulong)(sizeof(S))) - (ulong)(1)");
    }

    [Fact]
    public void sizeof_struct_bitand_constant_keeps_the_and()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(S) & 240")) });
        emitted.ShouldContain("((ulong)(sizeof(S))) & (ulong)(240)");
    }

    [Fact]
    public void sizeof_struct_plus_constant_is_unaffected()
    {
        // Unary plus doubles as a prefix too; the `+ 2` binds as binary.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(S) + 2")) });
        emitted.ShouldContain("((ulong)(sizeof(S))) + (ulong)(2)");
    }

    [Fact]
    public void foldable_sizeof_times_constant_still_folds_to_a_number()
    {
        // `sizeof(int) * 8` — a foldable scalar type folds to a NUM in the token
        // stream (no `sizeof(...)` survives).
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(int) * 8")) });
        emitted.ShouldNotContain("sizeof(int)");
        emitted.ShouldContain("4 * 8");
    }

    [Fact]
    public void sizeof_struct_times_a_variable_keeps_the_multiply()
    {
        // The case that used to need the token-level paren wrap: an unfoldable
        // sizeof followed by `*` and an identifier (which a cast could take as its
        // operand). With the cast tier the `*` is the binary multiply.
        var src = $"{Decls}int probe(int n) {{ return (int)(sizeof(S) * n); }}\nint main(void) {{ return probe(3) == 48 ? 0 : 1; }}\n";
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(src) });
        emitted.ShouldContain("((ulong)(sizeof(S))) * (ulong)(n)");
    }

    [Fact]
    public void sizeof_applies_to_a_unary_expression_not_a_cast()
    {
        // C11 6.5.3: `sizeof` takes a unary-expression, and a cast is not one, so
        // `sizeof (S) s` is a syntax error, as in gcc, rather than `sizeof((S)s)`.
        var src = $"{Decls}int main(void) {{ S s = {{ 0 }}; return (int)(sizeof (S) s); }}\n";
        Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { WriteTemp(src) }));
    }

    [Fact]
    public void unary_operators_take_a_cast_expression()
    {
        // `& * + - ~ !` take a cast-expression (C11 6.5.3), so a cast nests under
        // them, and a compound literal (a postfix expression) is still a sizeof
        // operand.
        var src = $"{Decls}int probe(long x, void *p) {{ return -(int)x + ~(int)x + !(int)x + *(int *)p + (int)sizeof (S){{ 1.0, 2 }}; }}\nint main(void) {{ return 0; }}\n";
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(src) });
        emitted.ShouldContain("return -(int)x + ~(int)x + ((int)x != 0 ? 0 : 1) + *(int*)p + unchecked((int)((ulong)(sizeof(S))));");
    }

    [Fact]
    public void sizeof_of_an_expression_containing_a_typename_is_not_folded()
    {
        // `sizeof((buf + K - n)[0])` is an EXPRESSION (subscript of pointer arith),
        // not a type — even though `K` here macro-expands to something containing
        // `sizeof(lua_Unsigned)`. The token-stream folder must NOT latch onto the
        // buried `lua_Unsigned` and fold the whole subscript to 8; the element is
        // `lu_byte` (1). This is the Lua bytecode-dump corruption (`dumpVarint`'s
        // `dumpVector(D, buff + DIBS - n, n)` → `n * sizeof((…)[0])`).
        var src = """
            typedef unsigned char lu_byte;
            typedef unsigned long long lua_Unsigned;
            #define CHAR_BIT 8
            #define DIBS ((int)(sizeof(lua_Unsigned) * CHAR_BIT) + 6) / 7
            int probe(void) {
                lu_byte buf[10]; unsigned n = 1;
                return (int)(n * sizeof((buf + DIBS - n)[0]));
            }
            int main(void) { return 0; }
            """;
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(src) });
        emitted.ShouldContain("sizeof(byte)");          // element sized correctly (1); IR expands lu_byte typedef to underlying byte
        emitted.ShouldNotContain("n * (uint)(8)");      // NOT mis-folded to 8
    }

    [Fact]
    public void malloc_sizeof_struct_peephole_still_fires()
    {
        // `malloc(sizeof(S))`: the sizeof is the call's sole argument, so the
        // malloc→stack-value peephole still recognises it.
        var src = $"{Decls}#include <stdlib.h>\n"
            + "int main(void) { S *p = (S*)malloc(sizeof(S)); p->b = 7; int r = p->b; free(p); return r; }\n";
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(src) });
        emitted.ShouldContain("new S()");          // peephole fired
    }
}
