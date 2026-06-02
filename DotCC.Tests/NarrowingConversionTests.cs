#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// C allows an implicit narrowing integer conversion at a store (init /
/// assignment / return) — a wider value silently truncates. C# requires an
/// explicit cast there, so dotcc inserts <c>(target)(value)</c> (an out-of-range
/// CONSTANT needs <c>unchecked(...)</c>; a constant that FITS needs nothing, like
/// C). With <c>-Wconversion</c> (opt-in, off by default — like gcc/clang) dotcc
/// also warns at each width-narrowing store. End-to-end in the
/// <c>narrowing-store/</c> fixture.
/// </summary>
public sealed class NarrowingConversionTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-narrow-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    // Capture stderr around one EmitCSharp call. Methods within a test class run
    // sequentially, so the Console.Error swap is contained to this class.
    private static string EmitCapturingStderr(string src, bool warnConversion)
    {
        var prior = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try { Compiler.EmitCSharp(new[] { src }, warnConversion: warnConversion); }
        finally { Console.SetError(prior); }
        return sw.ToString();
    }

    [Fact]
    public void variable_narrowing_init_inserts_cast()
    {
        var src = WriteTemp("""
            typedef unsigned char u8;
            int main(void) { int big = 300; u8 b = big; return (int)b; }
            """);
        try
        {
            // int → u8 (byte) narrowing: the C# cast C requires is inserted.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("u8 b = (u8)(big)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void fitting_constant_needs_no_cast()
    {
        var src = WriteTemp("""
            typedef unsigned char u8;
            int main(void) { u8 b = 5; return (int)b; }
            """);
        try
        {
            // 5 fits a byte → C#'s implicit constant conversion accepts it; no cast.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("u8 b = 5;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void out_of_range_constant_uses_unchecked()
    {
        var src = WriteTemp("""
            int main(void) { short s = 0x12345; return s; }
            """);
        try
        {
            // A constant cast out of range is a C# compile error unless unchecked.
            Compiler.EmitCSharp(new[] { src }).ShouldContain("unchecked((short)(0x12345))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void widening_store_inserts_no_cast()
    {
        var src = WriteTemp("""
            int main(void) { int i = 5; long x = i; return (int)x; }
            """);
        try
        {
            // int → long widens; C# converts implicitly, so no cast is added.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("long x = i;");
            emitted.ShouldNotContain("(long)(i)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void assignment_narrowing_inserts_cast()
    {
        var src = WriteTemp("""
            typedef unsigned char u8;
            int main(void) { u8 b = 0; int big = 300; b = big; return (int)b; }
            """);
        try
        {
            Compiler.EmitCSharp(new[] { src }).ShouldContain("b = (u8)(big)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Wconversion_warns_on_narrowing()
    {
        var src = WriteTemp("""
            typedef unsigned char lu_byte;
            int main(void) { int big = 300; lu_byte b = big; return (int)b; }
            """);
        try
        {
            var stderr = EmitCapturingStderr(src, warnConversion: true);
            stderr.ShouldContain("[-Wconversion]");
            stderr.ShouldContain("from `int` to `lu_byte`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void no_Wconversion_is_silent_by_default()
    {
        var src = WriteTemp("""
            typedef unsigned char lu_byte;
            int main(void) { int big = 300; lu_byte b = big; return (int)b; }
            """);
        try
        {
            EmitCapturingStderr(src, warnConversion: false).ShouldNotContain("Wconversion");
        }
        finally { File.Delete(src); }
    }
}
