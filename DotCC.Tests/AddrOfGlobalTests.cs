#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for taking the address of a static-field-backed variable. A C
/// global / static-local of value type lowers to a C# <c>static</c> field, which
/// is a MOVEABLE variable — a bare <c>&amp;field</c> is CS0212. dotcc hands the
/// (stable) address back via <c>Unsafe.AsPointer(ref field)</c>. A genuine LOCAL
/// is a fixed variable, so its <c>&amp;</c> stays the plain form. End-to-end in
/// the <c>addr-of-global/</c> fixture.
/// </summary>
public sealed class AddrOfGlobalTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-addr-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void address_of_global_scalar_uses_unsafe_aspointer()
    {
        var emitted = Emit("""
            int g = 0;
            int* take(void) { return &g; }
            int main(void) { return *take(); }
            """);
        emitted.ShouldContain("(int*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref g)");
        emitted.ShouldNotContain("(&g)");
    }

    [Fact]
    public void address_of_global_struct_uses_unsafe_aspointer()
    {
        var emitted = Emit("""
            typedef struct { int a; } S;
            static S s;
            S* take(void) { return &s; }
            int main(void) { return take()->a; }
            """);
        emitted.ShouldContain("(S*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s)");
    }

    [Fact]
    public void address_of_static_local_uses_unsafe_aspointer()
    {
        var emitted = Emit("""
            int next(void) { static int seed = 1; int* p = &seed; (*p)++; return seed; }
            int main(void) { return next(); }
            """);
        emitted.ShouldContain("Unsafe.AsPointer(ref __static_next_seed)");
    }

    [Fact]
    public void address_of_a_plain_local_stays_the_bare_form()
    {
        // Guard: a real local is a fixed variable — its `&` must NOT be rewritten.
        var emitted = Emit("""
            int main(void) { int x = 5; int* p = &x; return *p; }
            """);
        emitted.ShouldContain("(&x)");
        emitted.ShouldNotContain("Unsafe.AsPointer(ref x)");
    }
}
