#nullable enable

using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;time.h&gt;</c> calendar family (CalendarLib.cs):
/// gmtime/localtime/mktime/asctime/ctime/strftime. Assertions use UTC
/// (<c>gmtime</c>) + fixed epochs so they're machine-independent; the
/// local-zone functions are exercised via the tz-cancelling
/// <c>localtime(mktime(&amp;tm))</c> round-trip.
/// </summary>
public sealed unsafe class LibcCalendarTests
{
    private static string Cstr(byte* p) =>
        p == null ? "<null>" : System.Text.Encoding.ASCII.GetString(p, strlen(p));

    [Fact]
    public void gmtime_epoch_zero_is_thursday_jan_1_1970()
    {
        long t = 0;
        tm* g = gmtime(&t);
        g->tm_year.ShouldBe(70);   // 1970 - 1900
        g->tm_mon.ShouldBe(0);     // January
        g->tm_mday.ShouldBe(1);
        g->tm_hour.ShouldBe(0);
        g->tm_min.ShouldBe(0);
        g->tm_sec.ShouldBe(0);
        g->tm_wday.ShouldBe(4);    // Thursday
        g->tm_yday.ShouldBe(0);
    }

    [Fact]
    public void gmtime_known_timestamp_breaks_down_correctly()
    {
        long t = 1_700_000_000; // 2023-11-14 22:13:20 UTC (a Tuesday)
        tm* g = gmtime(&t);
        (g->tm_year + 1900).ShouldBe(2023);
        (g->tm_mon + 1).ShouldBe(11);
        g->tm_mday.ShouldBe(14);
        g->tm_hour.ShouldBe(22);
        g->tm_min.ShouldBe(13);
        g->tm_sec.ShouldBe(20);
        g->tm_wday.ShouldBe(2);    // Tuesday
        g->tm_yday.ShouldBe(317);  // 0-based day of year
    }

    [Fact]
    public void asctime_matches_the_canonical_form()
    {
        long t = 0;
        Cstr(asctime(gmtime(&t))).ShouldBe("Thu Jan  1 00:00:00 1970\n");
    }

    [Fact]
    public void strftime_formats_common_specifiers()
    {
        long t = 1_700_000_000;
        tm* g = gmtime(&t);
        byte* buf = stackalloc byte[64];
        int n = strftime(buf, 64, L("%Y-%m-%d %H:%M:%S\0"u8), g);
        Cstr(buf).ShouldBe("2023-11-14 22:13:20");
        n.ShouldBe(19);

        strftime(buf, 64, L("%A %B %e (%j)\0"u8), g);
        Cstr(buf).ShouldBe("Tuesday November 14 (318)"); // %e space-pads, %j is 1-based

        strftime(buf, 64, L("%I:%M %p\0"u8), g);
        Cstr(buf).ShouldBe("10:13 PM");
    }

    [Fact]
    public void strftime_returns_zero_when_result_does_not_fit()
    {
        long t = 0;
        tm* g = gmtime(&t);
        byte* buf = stackalloc byte[4];
        // "1970" is 4 chars + NUL = 5 > 4 → 0, per C.
        strftime(buf, 4, L("%Y\0"u8), g).ShouldBe(0);
    }

    [Fact]
    public void mktime_then_localtime_round_trips_and_normalizes()
    {
        // Local tz cancels across mktime (local→epoch) and localtime
        // (epoch→local). A mid-month midday avoids DST-transition edges.
        tm v = default;
        v.tm_year = 123;  // 2023
        v.tm_mon = 10;    // November
        v.tm_mday = 15;
        v.tm_hour = 9;
        v.tm_min = 30;
        v.tm_sec = 0;
        v.tm_isdst = -1;

        long e = mktime(&v);
        e.ShouldNotBe(-1);
        // mktime fills wday/yday in place; Nov 15 2023 is a Wednesday.
        v.tm_wday.ShouldBe(3);

        tm* back = localtime(&e);
        (back->tm_year + 1900).ShouldBe(2023);
        (back->tm_mon + 1).ShouldBe(11);
        back->tm_mday.ShouldBe(15);
        back->tm_hour.ShouldBe(9);
        back->tm_min.ShouldBe(30);
    }

    [Fact]
    public void mktime_normalizes_out_of_range_fields()
    {
        // tm_mon = 13 (Feb of the next year), tm_mday = 32 (→ Mar 4).
        tm v = default;
        v.tm_year = 123; // 2023
        v.tm_mon = 13;   // → 2024-02 (month index 1)
        v.tm_mday = 1;
        v.tm_isdst = -1;
        mktime(&v);
        (v.tm_year + 1900).ShouldBe(2024);
        v.tm_mon.ShouldBe(1); // February
        v.tm_mday.ShouldBe(1);
    }
}
