#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;stdio.h&gt;</c> character I/O surface
/// (StdioLib.cs) plus <c>&lt;errno.h&gt;</c> / <c>strerror</c> / <c>perror</c>
/// (ErrnoLib.cs). The stream-reading entries are driven with a
/// <see cref="StringReader"/>; the stdin/stdout-bound ones redirect
/// <see cref="Console"/>.
/// </summary>
public sealed unsafe class LibcStdioCharTests
{
    private static string Cstr(byte* p) =>
        p == null ? "<null>" : System.Text.Encoding.ASCII.GetString(p, strlen(p));

    [Fact]
    public void putchar_putc_fputc_write_to_stdout()
    {
        var prev = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            putchar('A');
            int r = fputc('B', stdout);
            putc('C', stdout);
            r.ShouldBe((int)'B');
        }
        finally { Console.SetOut(prev); }
        sw.ToString().ShouldBe("ABC");
    }

    [Fact]
    public void getchar_reads_stdin_then_eof()
    {
        var prev = Console.In;
        Console.SetIn(new StringReader("Hi"));
        try
        {
            getchar().ShouldBe((int)'H');
            getchar().ShouldBe((int)'i');
            getchar().ShouldBe(-1);
        }
        finally { Console.SetIn(prev); }
    }

    [Fact]
    public void fgetc_and_getc_read_stream()
    {
        var r = new StringReader("xy");
        fgetc(r).ShouldBe((int)'x');
        getc(r).ShouldBe((int)'y');
        fgetc(r).ShouldBe(-1);
    }

    [Fact]
    public void fgets_keeps_newline_and_stops()
    {
        var r = new StringReader("hello\nworld");
        byte* buf = stackalloc byte[32];
        Cstr(fgets(buf, 32, r)).ShouldBe("hello\n");
        Cstr(fgets(buf, 32, r)).ShouldBe("world");
        ((nint)fgets(buf, 32, r)).ShouldBe((nint)0);   // EOF, nothing read
    }

    [Fact]
    public void fgets_respects_size_limit()
    {
        var r = new StringReader("abcdef");
        byte* buf = stackalloc byte[4];
        Cstr(fgets(buf, 4, r)).ShouldBe("abc");          // at most n-1 = 3 bytes
    }

    // ---- errno / strerror / perror ----

    [Fact]
    public void errno_is_settable_and_readable()
    {
        errno = ERANGE;
        errno.ShouldBe(34);
        errno = 0;
        errno.ShouldBe(0);
    }

    [Fact]
    public void strerror_known_value() => Cstr(strerror(EINVAL)).ShouldBe("Invalid argument");

    [Fact]
    public void strerror_unknown_value() => Cstr(strerror(9999)).ShouldBe("Unknown error");

    [Fact]
    public void perror_writes_prefix_and_message_to_stderr()
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            errno = ENOENT;
            perror(L("myfile\0"u8));
        }
        finally { Console.SetError(prev); }
        sw.ToString().ShouldBe("myfile: No such file or directory\n");
    }
}
