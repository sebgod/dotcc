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
[Collection("Console")]
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
    public void utf8_bytes_to_stdout_print_as_their_characters()
    {
        // The console is a TextWriter: each byte path must assemble UTF-8, including a
        // sequence written a byte at a time or split across calls (CPython's write(1, …)).
        var prev = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            byte[] snake = [0xC3, 0xA9, 0xF0, 0x9F, 0x90, 0x8D];   // "é🐍"
            foreach (var b in snake) { putchar(b); }
            fixed (byte* p = snake)
            {
                fwrite(p, 1, 1, stdout);                 // the first byte of é alone,
                fwrite(p + 1, 1, (ulong)(snake.Length - 1), stdout); // then the rest
                write(1, p, 3);                          // é and the first byte of 🐍,
                write(1, p + 3, 3);                      // then its last three
            }
            byte[] euro = [0xE2, 0x82, 0, 0xAC, 0];      // "€" split over two fputs
            fixed (byte* e = euro) { fputs(e, stdout); fputs(e + 3, stdout); }
        }
        finally { Console.SetOut(prev); }
        sw.ToString().ShouldBe("é🐍é🐍é🐍€");
    }

    [Fact]
    public void invalid_utf8_to_stderr_prints_a_replacement_character()
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            fputc(0xFF, stderr);   // never valid in UTF-8
            fputc('A', stderr);
        }
        finally { Console.SetError(prev); }
        sw.ToString().ShouldBe("�A");
    }

    [Fact]
    public void utf8_stdin_hands_out_each_character_as_its_bytes()
    {
        var prev = Console.In;
        Console.SetIn(new StringReader("é🐍x"));
        try
        {
            int[] got = [getchar(), getchar(), getchar(), getchar(), getchar(), getchar(), getchar(), getchar()];
            got.ShouldBe([0xC3, 0xA9, 0xF0, 0x9F, 0x90, 0x8D, 'x', -1]);
        }
        finally { Console.SetIn(prev); }
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
    public void fgetc_and_getc_read_stdin()
    {
        var prev = Console.In;
        Console.SetIn(new StringReader("xy"));
        try
        {
            fgetc(stdin).ShouldBe((int)'x');
            getc(stdin).ShouldBe((int)'y');
            fgetc(stdin).ShouldBe(-1);
        }
        finally { Console.SetIn(prev); }
    }

    [Fact]
    public void fgets_keeps_newline_and_stops()
    {
        var prev = Console.In;
        Console.SetIn(new StringReader("hello\nworld"));
        try
        {
            byte* buf = stackalloc byte[32];
            Cstr(fgets(buf, 32, stdin)).ShouldBe("hello\n");
            Cstr(fgets(buf, 32, stdin)).ShouldBe("world");
            ((nint)fgets(buf, 32, stdin)).ShouldBe((nint)0);   // EOF, nothing read
        }
        finally { Console.SetIn(prev); }
    }

    [Fact]
    public void fgets_respects_size_limit()
    {
        var prev = Console.In;
        Console.SetIn(new StringReader("abcdef"));
        try
        {
            byte* buf = stackalloc byte[4];
            Cstr(fgets(buf, 4, stdin)).ShouldBe("abc");          // at most n-1 = 3 bytes
        }
        finally { Console.SetIn(prev); }
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
