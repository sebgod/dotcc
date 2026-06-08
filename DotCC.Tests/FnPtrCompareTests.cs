#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for comparing a function pointer to a bare function name
/// (<c>fp == f</c> / <c>fp != f</c>). In C the name decays to a function pointer;
/// C#'s <c>&amp;f</c> is an untyped method-group address that can't be compared to
/// a <c>delegate*</c>, so dotcc casts it to the other operand's fn-ptr type. End-to-end
/// in the <c>fnptr-compare/</c> fixture.
/// </summary>
public sealed class FnPtrCompareTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fpc-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void comparing_fn_ptr_to_bare_fn_name_casts_the_decayed_address()
    {
        var emitted = Emit("""
            typedef int (*Op)(int);
            static int inc(int x) { return x + 1; }
            int main(void) { Op f = inc; return f == inc ? 1 : 0; }
            """);
        // The bare `inc` decays to `&inc`; the IR expands `Op` to the underlying
        // delegate* type for the cast (no using-alias emitted).
        emitted.ShouldContain("(delegate*<int, int>)(&inc)");
    }
}
