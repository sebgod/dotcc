#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;
// VaArg/VaList are nested in the Libc CLASS (DotCC.Libc.Libc). From this test's
// namespace (DotCC.Tests), the bare name `Libc` would resolve to the sibling
// NAMESPACE DotCC.Libc, so alias the nested types explicitly.
using VaArg = DotCC.Libc.Libc.VaArg;
using VaList = DotCC.Libc.Libc.VaList;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for variadic-function support (Phase 2 of the Lua port). Covers
/// the <see cref="Libc.VaArg"/>/<see cref="Libc.VaList"/> runtime round-trips
/// and the emit shapes for <c>...</c> params + <c>va_start</c>/<c>va_arg</c>/
/// <c>va_end</c>. The full end-to-end behavior (incl. pointer varargs and
/// forwarding a <c>va_list</c> to another function) is in the <c>varargs/</c>
/// functional fixture.
/// </summary>
[Collection("Vararg")]
public sealed class VarargTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-va-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    // ---- runtime: VaArg/VaList ------------------------------------------

    [Fact]
    public void va_arg_reads_back_each_scalar_type()
    {
        // Implicit conversions box each value at the "call site"; the explicit
        // conversions read them back (what `(T)ap.Next()` compiles to).
        var ap = new VaList(new VaArg[] { 42, 3.5, 9_000_000_000L, 7u });
        ((int)ap.Next()).ShouldBe(42);
        ((double)ap.Next()).ShouldBe(3.5);
        ((long)ap.Next()).ShouldBe(9_000_000_000L);
        ((uint)ap.Next()).ShouldBe(7u);
    }

    [Fact]
    public void va_copy_gives_an_independent_cursor()
    {
        // VaList is a value type, so `va_copy(b, a)` (lowered to `b = a`) copies
        // the cursor index; the two then advance independently.
        var a = new VaList(new VaArg[] { 1, 2, 3 });
        ((int)a.Next()).ShouldBe(1);
        var b = a;                       // va_copy at index 1
        ((int)a.Next()).ShouldBe(2);     // a advances to index 2
        ((int)b.Next()).ShouldBe(2);     // b independently re-reads index 1's successor
    }

    // ---- emit shapes -----------------------------------------------------

    [Fact]
    public void variadic_function_emits_params_VaArg_and_va_ops()
    {
        var src = WriteTemp("""
            #include <stdarg.h>
            static int sum(int count, ...) {
                va_list ap;
                va_start(ap, count);
                int total = 0;
                for (int i = 0; i < count; i++) { total += va_arg(ap, int); }
                va_end(ap);
                return total;
            }
            int main(void) { return sum(2, 3, 4); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("params VaArg[] _va");
            emitted.ShouldContain("new VaList(_va)");
            emitted.ShouldContain("(int)(ap.Next())");
            emitted.ShouldContain("ap.End()");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void va_arg_of_pointer_type_uses_NextPtr()
    {
        var src = WriteTemp("""
            #include <stdarg.h>
            static char *first(int n, ...) {
                va_list ap;
                va_start(ap, n);
                char *s = va_arg(ap, char *);
                va_end(ap);
                return s;
            }
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Pointer va_arg goes through NextPtr() + a (byte*) cast.
            emitted.ShouldContain("(byte*)(ap.NextPtr())");
        }
        finally { File.Delete(src); }
    }
}
