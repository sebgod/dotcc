#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// The printf family evaluates every argument before it writes anything (C11 6.5.2.2p10, dotcc #249): the fluent
/// builder only records its arguments, and <c>Done()</c> formats and writes the whole output in one write.
/// </summary>
[Collection("Console")]
public sealed unsafe class FormatArgumentOrderTests
{
    /// <summary>An argument expression that itself prints, as the issue's <c>noisy()</c> does.</summary>
    private static int Noisy()
    {
        printf(L("[side]\0"u8)).Done();
        return 7;
    }

    [Fact]
    public void A_later_argument_that_prints_lands_before_the_whole_output() =>
        CaptureStdout(() => printf(L("a=%d b=%d\n\0"u8)).Arg(1).Arg(Noisy()).Done())
            .ShouldBe("[side]a=1 b=7\n");

    [Fact]
    public void The_return_value_counts_only_the_calls_own_bytes()
    {
        var n = 0;
        var text = CaptureStdout(() => n = printf(L("a=%d b=%d\n\0"u8)).Arg(1).Arg(Noisy()).Done());
        text.ShouldBe("[side]a=1 b=7\n");
        n.ShouldBe("a=1 b=7\n".Length);
    }

    [Fact]
    public void A_builder_never_finished_writes_nothing() =>
        CaptureStdout(() => { _ = printf(L("a=%d\n\0"u8)).Arg(1); }).ShouldBe("");

    [Fact]
    public void A_later_argument_that_rewrites_the_buffer_of_an_earlier_percent_s_is_seen()
    {
        var buf = (byte*)malloc(32);
        var src = (byte*)malloc(8);
        try
        {
            strcpy(src, L("old\0"u8));
            int Rewrite() { strcpy(src, L("new\0"u8)); return 5; }
            var n = sprintf(buf, L("%s %d\0"u8)).Arg(src).Arg(Rewrite()).Done();
            n.ShouldBe(5);
            System.Text.Encoding.ASCII.GetString(buf, n).ShouldBe("new 5");
        }
        finally { free(buf); free(src); }
    }

    [Fact]
    public void A_later_argument_that_lengthens_the_format_is_seen()
    {
        var buf = (byte*)malloc(32);
        var fmt = (byte*)malloc(16);
        try
        {
            strcpy(fmt, L("%d\0"u8));
            int Lengthen() { strcpy(fmt, L("%d-%d\0"u8)); return 1; }
            var n = sprintf(buf, fmt).Arg(Lengthen()).Arg(2).Done();
            n.ShouldBe(3);
            System.Text.Encoding.ASCII.GetString(buf, n).ShouldBe("1-2");
        }
        finally { free(buf); free(fmt); }
    }

    [Fact]
    public void A_later_argument_that_lengthens_a_wide_format_is_seen()
    {
        var dst = (char*)malloc(16 * sizeof(char));
        var fmt = (char*)malloc(16 * sizeof(char));
        try
        {
            "%d\0".AsSpan().CopyTo(new Span<char>(fmt, 16));
            int Lengthen() { "%d-%d\0".AsSpan().CopyTo(new Span<char>(fmt, 16)); return 1; }
            var n = swprintf(dst, 16, fmt).Arg(Lengthen()).Arg(2).Done();
            n.ShouldBe(3);
            new string(dst, 0, n).ShouldBe("1-2");
        }
        finally { free(dst); free(fmt); }
    }

    [Fact]
    public void A_nested_printf_in_an_argument_keeps_both_calls_arguments_apart()
    {
        var outer = (byte*)malloc(32);
        var inner = (byte*)malloc(32);
        try
        {
            int Nested() => sprintf(inner, L("%d,%d\0"u8)).Arg(8).Arg(9).Done();
            var n = sprintf(outer, L("%d %d %d\0"u8)).Arg(1).Arg(Nested()).Arg(4).Done();
            System.Text.Encoding.ASCII.GetString(outer, n).ShouldBe("1 3 4");
            System.Text.Encoding.ASCII.GetString(inner, 3).ShouldBe("8,9");
            // The next call on the thread reuses a cached record array and must start empty.
            n = sprintf(outer, L("%d\0"u8)).Arg(5).Done();
            System.Text.Encoding.ASCII.GetString(outer, n).ShouldBe("5");
        }
        finally { free(outer); free(inner); }
    }

    [Fact]
    public void Snprintf_with_no_destination_still_measures()
    {
        var n = snprintf(null, 0, L("%d-%s\0"u8)).Arg(12).Arg(L("ab\0"u8)).Done();
        n.ShouldBe(5);
    }

    [Fact]
    public void Swprintf_formats_and_counts()
    {
        var dst = (char*)malloc(16 * sizeof(char));
        try
        {
            fixed (char* fmt = "x=%d\0")
            {
                var n = swprintf(dst, 16, fmt).Arg(42).Done();
                n.ShouldBe(4);
                new string(dst, 0, n).ShouldBe("x=42");
            }
        }
        finally { free(dst); }
    }

    [Fact]
    public void Sscanf_computes_a_later_target_address_before_any_store()
    {
        var arr = stackalloc int[4];
        var i = 0;
        var n = sscanf(L("3 9\0"u8), L("%d %d\0"u8)).Read(&i).Read(&arr[i]).Done();
        n.ShouldBe(2);
        i.ShouldBe(3);
        arr[0].ShouldBe(9);
        arr[3].ShouldBe(0);
    }

    [Fact]
    public void Sscanf_reads_its_source_after_every_argument_is_evaluated()
    {
        var src = (byte*)malloc(8);
        try
        {
            strcpy(src, L("1\0"u8));
            int* v = stackalloc int[1];
            int* Rewrite() { strcpy(src, L("42\0"u8)); return v; }
            var n = sscanf(src, L("%d\0"u8)).Read(Rewrite()).Done();
            n.ShouldBe(1);
            v[0].ShouldBe(42);
        }
        finally { free(src); }
    }

    [Fact]
    public void Swscanf_reads_its_source_after_every_argument_is_evaluated()
    {
        var src = (char*)malloc(8 * sizeof(char));
        try
        {
            src[0] = '1'; src[1] = '\0';
            int* v = stackalloc int[1];
            int* Rewrite() { src[0] = '4'; src[1] = '2'; src[2] = '\0'; return v; }
            fixed (char* fmt = "%d\0")
            {
                var n = swscanf(src, fmt).Read(Rewrite()).Done();
                n.ShouldBe(1);
                v[0].ShouldBe(42);
            }
        }
        finally { free(src); }
    }

    [Fact]
    public void A_scan_chain_never_finished_stores_nothing()
    {
        var v = 5;
        _ = sscanf(L("9\0"u8), L("%d\0"u8)).Read(&v);
        v.ShouldBe(5);
    }

    /// <summary>Run <paramref name="action"/> with stdout captured.</summary>
    private static string CaptureStdout(Action action)
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(prev); }
        return sw.ToString();
    }
}
