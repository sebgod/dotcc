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
    public void Snprintf_with_no_destination_still_measures()
    {
        var n = snprintf(null, 0, L("%d-%s\0"u8)).Arg(12).Arg(L("ab\0"u8)).Done();
        n.ShouldBe(5);
    }

    [Fact]
    public void Swprintf_formats_after_its_arguments()
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
