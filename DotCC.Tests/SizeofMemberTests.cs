#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <c>sizeof</c> of an EXPRESSION that flows through member access,
/// pointer arithmetic, or a comma operator. dotcc synthesizes a partial CType up
/// each expression: a member access (<c>s.f</c> / <c>p-&gt;f</c>) now carries its
/// field's recorded type (<c>_structFieldTypes</c>), additive pointer arithmetic
/// (<c>p ± n</c>) carries the decayed pointer type, and a value-context comma
/// carries its last operand's type. <c>sizeof</c> reads that CType; an
/// unsynthesizable operand still fails loudly rather than emit a wrong size.
/// End-to-end in the <c>sizeof-member/</c> fixture (gcc-oracle-validated).
/// </summary>
public sealed class SizeofMemberTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-szm-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private const string Decls = """
        struct Inner { int g; double d; };
        struct Node { int *code; struct Inner in; char tag; };
        """;

    private static string Probe(string expr) =>
        $"{Decls}\nint probe(struct Node *p, char *buf, int n) {{ return (int){expr}; }}\nint main(void) {{ return 0; }}\n";

    [Fact]
    public void member_field_sizeof_resolves_to_field_type()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(p->in.d)")) });
        emitted.ShouldContain("(int)sizeof(double)");   // nested member chain → double
    }

    [Fact]
    public void pointer_member_subscript_sizeof_resolves_to_element()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(p->code[0])")) });
        emitted.ShouldContain("(int)sizeof(int)");      // int* element → int
    }

    [Fact]
    public void deref_of_pointer_member_sizeof_resolves_to_pointee()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(*p->code)")) });
        emitted.ShouldContain("(int)sizeof(int)");
    }

    [Fact]
    public void pointer_arithmetic_subscript_sizeof_resolves_to_element()
    {
        // `(buf + n)[0]` — additive pointer arith carries buf's decayed (byte*) type.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof((buf + n)[0])")) });
        emitted.ShouldContain("(int)sizeof(byte)");     // char* element → byte
    }

    [Fact]
    public void comma_expression_sizeof_resolves_to_last_operand()
    {
        // The `getstr`-style shape: `((void)0, p->code)` — value is the last operand.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof((((void)0), (p->code))[0])")) });
        emitted.ShouldContain("(int)sizeof(int)");
    }

    [Fact]
    public void unsynthesizable_sizeof_still_throws()
    {
        // A multiplicative result has no pointer/array type — sizeof of it can't be
        // resolved, so dotcc must still fail loudly (not silently emit a wrong size).
        var src = WriteTemp(Probe("sizeof(n * n)"));
        Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
            .Message.ShouldContain("sizeof");
    }
}
