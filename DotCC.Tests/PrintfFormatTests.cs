#nullable enable

using System.Collections.Generic;
using System.Linq;
using DotCC.Ir;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the backend-neutral <see cref="PrintfFormat"/> parser — the
/// shared printf format-spec grammar the wat backend uses for compile-time
/// expansion. Kept in lockstep with the runtime <c>PrintfBuilder.ParseSpec</c>
/// (which can't be referenced across the assembly boundary), so these pin the
/// grammar independently.
/// </summary>
[Collection("PrintfFormat")]
public sealed class PrintfFormatTests
{
    private static List<int> Bytes(string s) => s.Select(ch => (int)ch).ToList();

    private static string Lit(PrintfFormat.Segment seg) =>
        new string(seg.Literal!.Select(b => (char)b).ToArray());

    [Fact]
    public void a_plain_string_is_one_literal_segment()
    {
        var segs = PrintfFormat.Parse(Bytes("hello"));
        segs.Count.ShouldBe(1);
        segs[0].IsLiteral.ShouldBeTrue();
        Lit(segs[0]).ShouldBe("hello");
    }

    [Fact]
    public void a_bare_conversion_parses_with_no_formatting()
    {
        var segs = PrintfFormat.Parse(Bytes("%d"));
        segs.Count.ShouldBe(1);
        segs[0].IsLiteral.ShouldBeFalse();
        var spec = segs[0].Conversion!.Value;
        spec.Conv.ShouldBe('d');
        spec.HasFormatting.ShouldBeFalse();
        spec.Width.ShouldBe(-1);
        spec.Precision.ShouldBe(-1);
    }

    [Fact]
    public void literals_and_conversions_interleave_in_order()
    {
        var segs = PrintfFormat.Parse(Bytes("x=%d\n"));
        segs.Count.ShouldBe(3);
        Lit(segs[0]).ShouldBe("x=");
        segs[1].Conversion!.Value.Conv.ShouldBe('d');
        Lit(segs[2]).ShouldBe("\n");
    }

    [Fact]
    public void double_percent_folds_to_a_literal_percent()
    {
        var segs = PrintfFormat.Parse(Bytes("100%%"));
        segs.Count.ShouldBe(1);
        Lit(segs[0]).ShouldBe("100%");
    }

    [Fact]
    public void flags_width_and_precision_are_parsed()
    {
        // '-' and '0' flags, width 5, precision 3.
        var spec = PrintfFormat.Parse(Bytes("%-05.3d"))[0].Conversion!.Value;
        spec.Left.ShouldBeTrue();
        spec.Zero.ShouldBeTrue();
        spec.Width.ShouldBe(5);
        spec.Precision.ShouldBe(3);
        spec.Conv.ShouldBe('d');
        spec.HasFormatting.ShouldBeTrue();
    }

    [Fact]
    public void a_length_modifier_is_recorded_and_skipped()
    {
        var spec = PrintfFormat.Parse(Bytes("%ld"))[0].Conversion!.Value;
        spec.Length.ShouldBe('l');
        spec.Conv.ShouldBe('d');
        spec.HasFormatting.ShouldBeFalse();
    }

    [Fact]
    public void width_zero_flag_and_plain_width_are_distinguished()
    {
        PrintfFormat.Parse(Bytes("%05d"))[0].Conversion!.Value.Zero.ShouldBeTrue();
        var plain = PrintfFormat.Parse(Bytes("%5d"))[0].Conversion!.Value;
        plain.Zero.ShouldBeFalse();
        plain.Width.ShouldBe(5);
    }
}
