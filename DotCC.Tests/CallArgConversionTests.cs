#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// C converts each argument to its parameter type at the call (the prototype is
/// in scope) — int → unsigned/size_t, enum → int, a wider value → a narrower
/// parameter. C# rejects those implicit conversions, so dotcc records each
/// function's parameter types (<c>_fnParamTypes</c>, populated in
/// <c>StartFn</c>) and coerces each argument to its parameter (<c>CoerceArg</c> —
/// the call-site twin of the store conversions). End-to-end in the
/// <c>call-arg-conv/</c> fixture.
/// </summary>
[Collection("CallArgConversion")]
public sealed class CallArgConversionTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-cac-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void int_argument_to_unsigned_param_casts()
    {
        var src = WriteTemp("""
            #include <stddef.h>
            static unsigned long take(size_t n) { return n; }
            int main(void) { int x = 5; return (int)take(x); }
            """);
        try
        {
            // int → size_t (ulong) is not implicit in C#; the IR expands size_t → ulong and the arg takes the cast.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("take((ulong)(x))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void enum_argument_to_int_param_decays()
    {
        var src = WriteTemp("""
            enum Color { RED, GREEN, BLUE };
            static int shade(int c) { return c; }
            int main(void) { return shade(BLUE); }
            """);
        try
        {
            // A C enum is an int as an argument; C# needs the (int) decay.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("shade((int)(Color.BLUE))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void out_of_range_constant_argument_uses_unchecked()
    {
        var src = WriteTemp("""
            static int low8(unsigned char b) { return b; }
            int main(void) { return low8(0x141); }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("low8(unchecked((byte)(0x141)))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sizeof_argument_to_size_t_param_casts()
    {
        var src = WriteTemp("""
            #include <stddef.h>
            struct Big { double a, b, c; };
            static unsigned long take(size_t n) { return n; }
            int main(void) { return (int)take(sizeof(struct Big)); }
            """);
        try
        {
            // sizeof is size_t (ulong) per C, matching the size_t param — cast inserted by arg coercion.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("take(((ulong)(sizeof(Big))))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void matching_argument_is_untouched()
    {
        var src = WriteTemp("""
            static int id(int x) { return x; }
            int main(void) { int v = 7; return id(v); }
            """);
        try
        {
            // int arg → int param: no cast injected.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("id(v)");
            emitted.ShouldNotContain("id((int)(v))");
        }
        finally { File.Delete(src); }
    }
}
