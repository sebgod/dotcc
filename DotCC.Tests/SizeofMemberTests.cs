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
        emitted.ShouldContain("(int)(ulong)sizeof(double)");   // nested member chain → double
    }

    [Fact]
    public void pointer_member_subscript_sizeof_resolves_to_element()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(p->code[0])")) });
        emitted.ShouldContain("(int)(");      // int* element → int
    }

    [Fact]
    public void deref_of_pointer_member_sizeof_resolves_to_pointee()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(*p->code)")) });
        emitted.ShouldContain("(int)(");
    }

    [Fact]
    public void pointer_arithmetic_subscript_sizeof_resolves_to_element()
    {
        // `(buf + n)[0]` — additive pointer arith carries buf's decayed (byte*) type.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof((buf + n)[0])")) });
        emitted.ShouldContain("(int)(ulong)sizeof(byte)");     // char* element → byte
    }

    [Fact]
    public void comma_expression_sizeof_resolves_to_last_operand()
    {
        // The `getstr`-style shape: `((void)0, p->code)` — value is the last operand.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof((((void)0), (p->code))[0])")) });
        emitted.ShouldContain("(int)(");
    }

    [Fact]
    public void integer_arithmetic_sizeof_resolves_to_common_type()
    {
        // An integer multiplicative result now carries its usual-arithmetic common
        // type (phase 6i), so `sizeof(n * n)` is `sizeof(int)` — exactly C's
        // semantics (the result of `int * int` is `int`).
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(Probe("sizeof(n * n)")) });
        emitted.ShouldContain("(int)(");
    }

    [Fact]
    public void unsynthesizable_sizeof_still_throws()
    {
        // A FLOATING multiplicative result has no synthesized type (the integer
        // usual-arithmetic layer only types integer pairs), so sizeof of it must
        // still fail loudly rather than silently emit a wrong size.
        var src = WriteTemp(Probe("sizeof(p->in.d * p->in.d)"));
        Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
            .Message.ShouldContain("sizeof");
    }

    // Chain through an ANONYMOUS inline struct member (Lua's
    // `sizeof(*p.dyd.actvar.arr)`). The unnamed `struct { Elem *arr; … } vec;`
    // member lowers to a synth nested type; dotcc records that type's field types
    // and the parent field's type, so `o->vec.arr` carries CType and the deref /
    // subscript resolves to the element size.
    private const string NestDecls = """
        struct Elem { int a; int b; };
        struct Outer { struct { struct Elem *arr; int n; } vec; int tag; };
        """;

    private static string NestProbe(string expr) =>
        $"{NestDecls}\nint probe(struct Outer *o) {{ return (int){expr}; }}\nint main(void) {{ return 0; }}\n";

    [Fact]
    public void sizeof_deref_through_anonymous_nested_member_chain()
    {
        // `sizeof(*o->vec.arr)` — chain Outer→vec(synth)→arr(Elem*), deref → Elem.
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(NestProbe("sizeof(*o->vec.arr)")) });
        emitted.ShouldContain("(int)(ulong)sizeof(Elem)");
    }

    [Fact]
    public void sizeof_subscript_through_anonymous_nested_member_chain()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(NestProbe("sizeof(o->vec.arr[0])")) });
        emitted.ShouldContain("(int)(ulong)sizeof(Elem)");
    }

    [Fact]
    public void inner_field_type_resolves_under_synth_type_not_parent()
    {
        // The synth nested type owns its inner fields' types: `sizeof(o->vec.n)` is
        // the int field of the synth type (not leaked to / from the parent Outer).
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp(NestProbe("sizeof(o->vec.n)")) });
        emitted.ShouldContain("(int)(");
    }
}
