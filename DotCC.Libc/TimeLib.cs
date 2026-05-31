#nullable enable

using System;

namespace DotCC.Libc;

/// <summary>
/// C <c>&lt;time.h&gt;</c> scalar surface: <c>time</c> / <c>clock</c> /
/// <c>difftime</c>. <c>time_t</c> and <c>clock_t</c> lower (via plain typedefs
/// in the header) to C# <c>long</c>.
/// </summary>
/// <remarks>
/// The <c>struct tm</c> calendar family (<c>localtime</c> / <c>gmtime</c> /
/// <c>mktime</c> / <c>strftime</c> / <c>asctime</c> / <c>ctime</c>) lives in
/// <c>CalendarLib.cs</c>.
///
/// <para><c>clock()</c> returns a monotonic millisecond counter
/// (<see cref="Environment.TickCount64"/>) rather than true CPU time, with
/// <c>CLOCKS_PER_SEC</c> = 1000 — so the idiomatic
/// <c>(clock() - start) / (double)CLOCKS_PER_SEC</c> yields elapsed wall-clock
/// seconds. Avoiding <c>System.Diagnostics.Process</c> keeps the runtime block
/// lean.</para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary><c>time(t)</c> — seconds since the Unix epoch (UTC). Stores the
    /// value through <paramref name="t"/> when non-null and also returns it.</summary>
    public static long time(long* t)
    {
        long secs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (t != null) { *t = secs; }
        return secs;
    }

    /// <summary><c>clock()</c> — a monotonic millisecond counter (see remarks;
    /// pair with <c>CLOCKS_PER_SEC</c> = 1000). Use differences to measure
    /// elapsed time.</summary>
    public static long clock() => Environment.TickCount64;

    /// <summary><c>difftime(end, beginning)</c> — <c>end - beginning</c> as a
    /// <c>double</c> (seconds).</summary>
    public static double difftime(long end, long beginning) => (double)(end - beginning);
}
