#nullable enable

using System;
using System.IO;
using System.Text;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;wchar.h&gt;</c> wide I/O surface (WcharLib.cs): wide
/// character/line I/O (<c>fputwc</c>/<c>fgetwc</c>/<c>fputws</c>/<c>fgetws</c>/
/// <c>ungetwc</c>) and the wide formatted family (<c>swprintf</c>/<c>fwprintf</c>/
/// <c>swscanf</c>/<c>fwscanf</c>). The formatted functions are called in the fluent
/// form the emitter generates (<c>.Arg(…)</c>/<c>.Read(…)</c>/<c>.Done()</c>).
/// Stream cases use a unique temp file (avoiding any <c>Console</c> redirection,
/// which would race under xUnit parallelism); <c>wprintf</c>-to-stdout is covered by
/// the <c>wchar-io</c> functional fixture instead.
/// </summary>
[Collection("Console")]
public sealed unsafe class LibcWcharIoTests
{
    private static string Wstr(char* p) => p == null ? "<null>" : new string(p, 0, wcslen(p));

    private static FILE* Fopen(string path, string mode)
    {
        var pb = Encoding.UTF8.GetBytes(path + "\0");
        var mb = Encoding.UTF8.GetBytes(mode + "\0");
        fixed (byte* pp = pb)
        fixed (byte* mm = mb)
        {
            return fopen(pp, mm);   // fopen copies the path immediately
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"dotcc-wio-{Guid.NewGuid():N}.txt");

    // ---- swprintf (wide formatted output into a buffer) ------------------

    [Fact]
    public void swprintf_formats_into_wide_buffer()
    {
        char* buf = stackalloc char[32];
        int n = swprintf(buf, 32, L16("v=%d!\0")).Arg(42).Done();
        Wstr(buf).ShouldBe("v=42!");
        n.ShouldBe(5);
    }

    [Fact]
    public void swprintf_wide_string_and_float()
    {
        char* buf = stackalloc char[32];
        swprintf(buf, 32, L16("[%ls %.1f]\0")).Arg(L16("hi\0")).Arg(3.5).Done();
        Wstr(buf).ShouldBe("[hi 3.5]");
    }

    [Fact]
    public void swprintf_returns_negative_when_truncated()
    {
        char* buf = stackalloc char[4];
        // "abcdef" needs 6 + NUL but only 4 wide slots — C swprintf returns negative.
        int n = swprintf(buf, 4, L16("%ls\0")).Arg(L16("abcdef\0")).Done();
        n.ShouldBeLessThan(0);
        Wstr(buf).ShouldBe("abc");   // 3 chars + NUL fit
    }

    // ---- swscanf (wide formatted input from a string) --------------------

    [Fact]
    public void swscanf_parses_int_and_wide_token()
    {
        int a;
        char* word = stackalloc char[16];
        int got = swscanf(L16("42 yo\0"), L16("%d %ls\0")).Read(&a).Read(word).Done();
        got.ShouldBe(2);
        a.ShouldBe(42);
        Wstr(word).ShouldBe("yo");
    }

    [Fact]
    public void swscanf_wide_char()
    {
        char* c = stackalloc char[2];
        int got = swscanf(L16("Q\0"), L16("%lc\0")).Read(c).Done();
        got.ShouldBe(1);
        c[0].ShouldBe('Q');
    }

    // ---- file round-trips (UTF-8 on disk <-> UTF-16) ---------------------

    [Fact]
    public void file_roundtrip_fputws_fgetws()
    {
        var path = TempPath();
        var w = Fopen(path, "w");
        fputws(L16("hello\n\0"), w);
        fputws(L16("world\n\0"), w);
        fclose(w);
        try
        {
            var r = Fopen(path, "r");
            char* buf = stackalloc char[32];
            Wstr(fgetws(buf, 32, r)).ShouldBe("hello\n");
            Wstr(fgetws(buf, 32, r)).ShouldBe("world\n");
            (fgetws(buf, 32, r) == null).ShouldBeTrue();   // EOF before any char
            fclose(r);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void file_roundtrip_fputwc_fgetwc()
    {
        var path = TempPath();
        var w = Fopen(path, "w");
        fputwc('A', w);
        fputwc('B', w);
        fputwc('\n', w);
        fclose(w);
        try
        {
            var r = Fopen(path, "r");
            fgetwc(r).ShouldBe((int)'A');
            fgetwc(r).ShouldBe((int)'B');
            fgetwc(r).ShouldBe((int)'\n');
            fgetwc(r).ShouldBe(-1);   // WEOF
            fclose(r);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ungetwc_pushes_one_back()
    {
        var path = TempPath();
        var w = Fopen(path, "w");
        fputws(L16("xy\0"), w);
        fclose(w);
        try
        {
            var r = Fopen(path, "r");
            int c = fgetwc(r);            // 'x'
            c.ShouldBe((int)'x');
            ungetwc(c, r).ShouldBe((int)'x');
            fgetwc(r).ShouldBe((int)'x'); // pushed-back char returned again
            fgetwc(r).ShouldBe((int)'y');
            fclose(r);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void fwprintf_then_fwscanf_roundtrip()
    {
        var path = TempPath();
        var w = Fopen(path, "w");
        fwprintf(w, L16("%d %ls\0")).Arg(7).Arg(L16("zz\0")).Done();
        fclose(w);
        try
        {
            var r = Fopen(path, "r");
            int a;
            char* word = stackalloc char[8];
            int got = fwscanf(r, L16("%d %ls\0")).Read(&a).Read(word).Done();
            got.ShouldBe(2);
            a.ShouldBe(7);
            Wstr(word).ShouldBe("zz");
            fclose(r);
        }
        finally { File.Delete(path); }
    }
}
