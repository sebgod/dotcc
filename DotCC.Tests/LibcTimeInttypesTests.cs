#nullable enable

using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;time.h&gt;</c> scalar surface (TimeLib.cs) and the
/// <c>&lt;inttypes.h&gt;</c> functions (InttypesLib.cs).
/// </summary>
[Collection("Runtime")]
public sealed unsafe class LibcTimeInttypesTests
{
    // ---- time.h ----

    [Fact]
    public void time_returns_a_plausible_unix_timestamp()
    {
        // Any real run is well past 2023-11 (1.7e9 s since the epoch).
        time(null).ShouldBeGreaterThan(1_700_000_000L);
    }

    [Fact]
    public void time_stores_through_pointer()
    {
        long t = 0;
        long ret = time(&t);
        t.ShouldBe(ret);
        t.ShouldBeGreaterThan(1_700_000_000L);
    }

    [Fact]
    public void clock_is_nonnegative() => clock().ShouldBeGreaterThanOrEqualTo(0L);

    [Fact]
    public void difftime_subtracts() => difftime(1000, 400).ShouldBe(600.0);

    // ---- inttypes.h ----

    [Fact]
    public void imaxabs_absolute_value() => imaxabs(-42L).ShouldBe(42L);

    [Fact]
    public void imaxdiv_quotient_and_remainder()
    {
        var d = imaxdiv(17L, 5L);
        d.quot.ShouldBe(3L);
        d.rem.ShouldBe(2L);
    }

    [Fact]
    public void strtoimax_parses_signed() => strtoimax(L("-123\0"u8), null, 10).ShouldBe(-123L);

    [Fact]
    public void strtoumax_parses_unsigned() => strtoumax(L("4294967296\0"u8), null, 10).ShouldBe(4294967296UL);
}
