#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <see cref="DotCC.Libc"/>. Each test exercises a function
/// against either a stack-allocated or heap-allocated byte buffer, mirroring
/// the way emitted C code calls these.
/// </summary>
[Collection("Console")]
public sealed unsafe class LibcTests
{
    // -----------------------------------------------------------------
    // strlen / strcmp / strcpy
    // -----------------------------------------------------------------

    [Fact]
    public void strlen_counts_bytes_before_nul() =>
        strlen(L("hello\0"u8)).ShouldBe(5);

    [Fact]
    public void strlen_returns_zero_on_empty_string() =>
        strlen(L("\0"u8)).ShouldBe(0);

    [Fact]
    public void strcmp_returns_zero_on_equal_strings() =>
        strcmp(L("abc\0"u8), L("abc\0"u8)).ShouldBe(0);

    [Fact]
    public void strcmp_returns_negative_when_lhs_lex_smaller() =>
        strcmp(L("abc\0"u8), L("abd\0"u8)).ShouldBeLessThan(0);

    [Fact]
    public void strcmp_returns_positive_when_lhs_lex_larger() =>
        strcmp(L("abd\0"u8), L("abc\0"u8)).ShouldBeGreaterThan(0);

    // -----------------------------------------------------------------
    // malloc / free / memset / memcpy
    // -----------------------------------------------------------------

    [Fact]
    public void malloc_free_roundtrip()
    {
        var p = (byte*)malloc(16);
        try
        {
            for (int i = 0; i < 16; i++) { p[i] = (byte)(i * 2); }
            p[7].ShouldBe((byte)14);
            p[15].ShouldBe((byte)30);
        }
        finally { free(p); }
    }

    [Fact]
    public void memset_fills_buffer_with_byte_value()
    {
        var p = (byte*)malloc(8);
        try
        {
            // memset returns the dst pointer (C signature). Compare via IntPtr
            // since Shouldly's generic ShouldBe can't take raw void*.
            ((IntPtr)memset(p, 0xAB, 8)).ShouldBe((IntPtr)p);
            for (int i = 0; i < 8; i++) { p[i].ShouldBe((byte)0xAB); }
        }
        finally { free(p); }
    }

    [Fact]
    public void memcpy_copies_bytes()
    {
        var dst = (byte*)malloc(8);
        try
        {
            memcpy(dst, L("01234567\0"u8), 8);
            for (int i = 0; i < 8; i++) { dst[i].ShouldBe((byte)('0' + i)); }
        }
        finally { free(dst); }
    }

    [Fact]
    public void strcpy_copies_until_nul_inclusive()
    {
        var dst = (byte*)malloc(8);
        try
        {
            memset(dst, 0xFF, 8); // poison
            strcpy(dst, L("hi\0"u8));
            dst[0].ShouldBe((byte)'h');
            dst[1].ShouldBe((byte)'i');
            dst[2].ShouldBe((byte)0);
            // Bytes past the NUL are unchanged (still 0xFF poison).
            dst[3].ShouldBe((byte)0xFF);
        }
        finally { free(dst); }
    }

    // -----------------------------------------------------------------
    // printf / fprintf / puts / fputs
    // -----------------------------------------------------------------

    [Fact]
    public void printf_d_writes_int_to_stdout() =>
        CaptureStdout(() => printf(L("n=%d\0"u8)).Arg(42).Done())
            .ShouldBe("n=42");

    [Fact]
    public void printf_f_formats_double_with_six_places() =>
        CaptureStdout(() => printf(L("%f\0"u8)).Arg(1.5).Done())
            .ShouldBe("1.500000");

    [Fact]
    public void printf_s_writes_c_string() =>
        CaptureStdout(() => printf(L("hi %s!\0"u8)).Arg(L("world\0"u8)).Done())
            .ShouldBe("hi world!");

    [Fact]
    public void printf_percent_percent_literal() =>
        CaptureStdout(() => printf(L("100%%\0"u8)).Done())
            .ShouldBe("100%");

    [Fact]
    public void printf_o_formats_unsigned_octal() =>
        CaptureStdout(() => printf(L("%o %o %o\0"u8)).Arg(8).Arg(64).Arg(0).Done())
            .ShouldBe("10 100 0");

    [Fact]
    public void printf_hash_o_forces_leading_zero() =>
        CaptureStdout(() => printf(L("%#o %#o\0"u8)).Arg(8).Arg(0).Done())
            .ShouldBe("010 0"); // 0 already has a leading zero

    [Fact]
    public void printf_o_treats_int_as_unsigned() =>
        // -1 → 0xFFFFFFFF → 37777777777 (32-bit unsigned octal), matching C.
        CaptureStdout(() => printf(L("%o\0"u8)).Arg(-1).Done())
            .ShouldBe("37777777777");

    [Fact]
    public void printf_llo_formats_unsigned_64bit_octal() =>
        CaptureStdout(() => printf(L("%llo\0"u8)).Arg(64L).Done())
            .ShouldBe("100");

    [Fact]
    public void fprintf_to_stdout_routes_to_stdout() =>
        CaptureStdout(() => fprintf(stdout, L("hello %d\0"u8)).Arg(7).Done())
            .ShouldBe("hello 7");

    [Fact]
    public void fprintf_to_stderr_writes_to_stderr_not_stdout()
    {
        var stdoutCap = new StringWriter();
        var stderrCap = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdoutCap);
        Console.SetError(stderrCap);
        try
        {
            fprintf(stderr, L("err %d\0"u8)).Arg(2).Done();
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
        stdoutCap.ToString().ShouldBeEmpty();
        stderrCap.ToString().ShouldBe("err 2");
    }

    [Fact]
    public void puts_writes_with_newline() =>
        CaptureStdout(() => puts(L("line\0"u8)))
            .ReplaceLineEndings("\n")
            .ShouldBe("line\n");

    [Fact]
    public void fputs_writes_without_newline() =>
        CaptureStdout(() => fputs(L("noNl\0"u8), stdout))
            .ShouldBe("noNl");

    // -----------------------------------------------------------------
    // sprintf / snprintf
    // -----------------------------------------------------------------

    [Fact]
    public void sprintf_returns_byte_count_and_terminates()
    {
        var dst = (byte*)malloc(32);
        try
        {
            memset(dst, 0xFF, 32);
            var n = sprintf(dst, L("v=%d\0"u8)).Arg(42).Done();
            n.ShouldBe(4); // "v=42"
            dst[0].ShouldBe((byte)'v');
            dst[1].ShouldBe((byte)'=');
            dst[2].ShouldBe((byte)'4');
            dst[3].ShouldBe((byte)'2');
            dst[4].ShouldBe((byte)0);  // NUL terminator
            dst[5].ShouldBe((byte)0xFF); // untouched poison past the NUL
        }
        finally { free(dst); }
    }

    [Fact]
    public void sprintf_with_string_arg_copies_correctly()
    {
        var dst = (byte*)malloc(32);
        try
        {
            sprintf(dst, L("hi %s\0"u8)).Arg(L("there\0"u8)).Done();
            // Verify by reading the NUL-terminated result back.
            strlen(dst).ShouldBe(8); // "hi there"
            strcmp(dst, L("hi there\0"u8)).ShouldBe(0);
        }
        finally { free(dst); }
    }

    [Fact]
    public void snprintf_truncates_to_capacity_and_still_nul_terminates()
    {
        var dst = (byte*)malloc(16);
        try
        {
            memset(dst, 0xFF, 16);
            // Capacity 4 → at most 3 chars + NUL.
            var n = snprintf(dst, 4, L("hello %d\0"u8)).Arg(99).Done();
            n.ShouldBe(8); // "hello 99" — what would-have-been
            // First 3 bytes are 'h','e','l'; byte 3 is the NUL.
            dst[0].ShouldBe((byte)'h');
            dst[1].ShouldBe((byte)'e');
            dst[2].ShouldBe((byte)'l');
            dst[3].ShouldBe((byte)0);
            dst[4].ShouldBe((byte)0xFF); // untouched
        }
        finally { free(dst); }
    }

    [Fact]
    public void sprintf_long_arg_formats_as_integer()
    {
        // Regression for SprintfBuilder.Arg(long): before it existed, a long
        // bound to Arg(float) and misformatted. A 10-digit value (beyond the
        // int range) exercises the dedicated overload.
        var dst = (byte*)malloc(32);
        try
        {
            sprintf(dst, L("%ld\0"u8)).Arg(9999999999L).Done();
            strcmp(dst, L("9999999999\0"u8)).ShouldBe(0);
        }
        finally { free(dst); }
    }

    [Fact]
    public void sprintf_void_ptr_arg_prints_hex_address()
    {
        // %p with a void* — Arg(void*) routes to the byte* %p path. A typed T* reaches
        // it via the implicit T* -> void* conversion. glibc-shaped: "0x" + the address
        // in lowercase hex (matches clang/gcc and the wat backend).
        void* p = malloc(8);
        var dst = (byte*)malloc(64);
        try
        {
            sprintf(dst, L("%p\0"u8)).Arg(p).Done();
            var got = System.Text.Encoding.ASCII.GetString(dst, strlen(dst));
            got.ShouldBe("0x" + ((ulong)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { free(dst); free(p); }
    }

    [Fact]
    public void sprintf_null_ptr_with_percent_p_is_nil()
    {
        // A null pointer under %p is glibc's "(nil)" (distinct from %s's "(null)").
        var dst = (byte*)malloc(16);
        try
        {
            sprintf(dst, L("%p\0"u8)).Arg((void*)null).Done();
            System.Text.Encoding.ASCII.GetString(dst, strlen(dst)).ShouldBe("(nil)");
        }
        finally { free(dst); }
    }

    // -----------------------------------------------------------------
    // scanf / fscanf / sscanf
    // -----------------------------------------------------------------

    [Fact]
    public void sscanf_parses_single_int()
    {
        int x = -1;
        var matched = sscanf(L("42\0"u8), L("%d\0"u8)).Read(&x).Done();
        matched.ShouldBe(1);
        x.ShouldBe(42);
    }

    [Fact]
    public void sscanf_parses_signed_int()
    {
        int x = 0;
        sscanf(L("-17\0"u8), L("%d\0"u8)).Read(&x).Done();
        x.ShouldBe(-17);
    }

    [Fact]
    public void sscanf_parses_int_then_double()
    {
        int n = 0;
        double f = 0;
        var matched = sscanf(L("3 4.5\0"u8), L("%d %f\0"u8)).Read(&n).Read(&f).Done();
        matched.ShouldBe(2);
        n.ShouldBe(3);
        f.ShouldBe(4.5);
    }

    [Fact]
    public void sscanf_parses_string_token_to_first_whitespace()
    {
        var buf = (byte*)malloc(32);
        try
        {
            memset(buf, 0xFF, 32);
            sscanf(L("hello world\0"u8), L("%s\0"u8)).Read(buf).Done();
            strcmp(buf, L("hello\0"u8)).ShouldBe(0);
        }
        finally { free(buf); }
    }

    [Fact]
    public void sscanf_returns_count_of_successful_matches()
    {
        int n = 0;
        double f = 0;
        // Second conversion fails because input runs out; expect 1 match.
        var matched = sscanf(L("7\0"u8), L("%d %f\0"u8)).Read(&n).Read(&f).Done();
        matched.ShouldBe(1);
        n.ShouldBe(7);
    }

    [Fact]
    public void fscanf_reads_from_stdin()
    {
        var prev = Console.In;
        Console.SetIn(new StringReader("100 2.71"));
        try
        {
            int n = 0;
            double f = 0;
            var matched = fscanf(stdin, L("%d %f\0"u8)).Read(&n).Read(&f).Done();
            matched.ShouldBe(2);
            n.ShouldBe(100);
            f.ShouldBe(2.71);
        }
        finally { Console.SetIn(prev); }
    }

    // -----------------------------------------------------------------
    // strtod / atof
    // -----------------------------------------------------------------

    [Fact]
    public void strtod_parses_a_plain_double() =>
        strtod(L("3.14\0"u8), null).ShouldBe(3.14, 1e-12);

    [Fact]
    public void strtod_handles_whitespace_sign_exponent_and_trailing_junk() =>
        strtod(L("  -2.5e3xyz\0"u8), null).ShouldBe(-2500.0, 1e-9);

    [Fact]
    public void strtod_sets_endptr_to_first_unconsumed_byte()
    {
        byte* s = L("1.5 2.25\0"u8);
        byte* end;
        double a = strtod(s, &end);
        double b = strtod(end, &end);
        a.ShouldBe(1.5, 1e-12);
        b.ShouldBe(2.25, 1e-12);
        (*end).ShouldBe((byte)0);   // walked to the NUL terminator
    }

    [Fact]
    public void strtod_no_conversion_returns_zero_and_endptr_at_start()
    {
        byte* s = L("abc\0"u8);
        byte* end;
        strtod(s, &end).ShouldBe(0.0);
        ((nint)end).ShouldBe((nint)s);   // *endptr == nptr per C
    }

    [Fact]
    public void strtod_parses_inf_and_nan()
    {
        strtod(L("inf\0"u8), null).ShouldBe(double.PositiveInfinity);
        strtod(L("-INFINITY\0"u8), null).ShouldBe(double.NegativeInfinity);
        double.IsNaN(strtod(L("nan\0"u8), null)).ShouldBeTrue();
    }

    [Fact]
    public void atof_is_strtod_without_endptr() =>
        atof(L("42\0"u8)).ShouldBe(42.0);

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static string CaptureStdout(System.Action action)
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(prev); }
        return sw.ToString();
    }
}
