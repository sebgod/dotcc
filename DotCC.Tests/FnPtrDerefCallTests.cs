#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for calling through a DEREFERENCED function pointer. In C,
/// <c>*fp</c> on a function pointer is a no-op — the function value decays
/// straight back to the pointer, so <c>(*fp)(args)</c> ≡ <c>fp(args)</c>. C#
/// function pointers are called directly and reject <c>*fp</c> (CS0193), so the
/// <c>Visit(C.Deref)</c> path drops the deref when the operand is a fn-ptr type
/// (bare <c>delegate*</c> or a fn-ptr typedef). A DATA pointer's <c>*p</c> stays
/// a real dereference. End-to-end in the <c>fnptr-deref-call/</c> fixture.
/// </summary>
public sealed class FnPtrDerefCallTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fpderef-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Emit(string body)
    {
        var src = WriteTemp(body);
        try { return Compiler.EmitCSharp(new[] { src }); }
        finally { File.Delete(src); }
    }

    [Fact]
    public void deref_call_through_local_fn_ptr_drops_the_star()
    {
        var emitted = Emit("""
            typedef int (*BinOp)(int, int);
            static int add(int a, int b) { return a + b; }
            int main(void) { BinOp f = add; return (*f)(2, 3); }
            """);
        emitted.ShouldContain("f(2, 3)");
        emitted.ShouldNotContain("(*f)(2, 3)");
    }

    [Fact]
    public void deref_call_through_struct_field_fn_ptr_drops_the_star()
    {
        var emitted = Emit("""
            typedef int (*BinOp)(int, int);
            typedef struct { BinOp op; } Holder;
            static int mul(int a, int b) { return a * b; }
            int main(void) { Holder h; h.op = mul; return (*(h.op))(4, 5); }
            """);
        // The deref is gone; the field access is still invoked directly
        // (paren nesting around the callee is irrelevant — the `*` is what matters).
        emitted.ShouldContain(")(4, 5)");
        emitted.ShouldNotContain("(*(h.op))");
    }

    [Fact]
    public void deref_of_a_data_pointer_stays_a_real_dereference()
    {
        // Guard: the fn-ptr no-op must NOT swallow a genuine data-pointer deref.
        var emitted = Emit("""
            int main(void) { int x = 7; int *p = &x; return *p; }
            """);
        emitted.ShouldContain("(*p)");
    }
}
