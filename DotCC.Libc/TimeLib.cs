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

    /// <summary>C11 <c>struct timespec</c> (§7.27.1) — a whole-seconds + nanoseconds
    /// time value. A blittable value struct (like <see cref="tm"/>) so user code can
    /// stack-allocate it and take its address; <c>&lt;time.h&gt;</c> parses
    /// <c>struct timespec</c> via the usual <c>struct ID</c> rule (tag → this Libc
    /// type). Used by <c>timespec_get</c> and the <c>&lt;threads.h&gt;</c> timed calls
    /// (<c>thrd_sleep</c> / <c>mtx_timedlock</c> / <c>cnd_timedwait</c>).</summary>
    // CS8981: the all-lowercase name is deliberate — it must match the C type
    // `struct timespec` so `using static Libc;` resolves the emitted `timespec`.
#pragma warning disable CS8981
    public struct timespec { public long tv_sec; public long tv_nsec; }
#pragma warning restore CS8981

    /// <summary>C11 <c>TIME_UTC</c> time base for <see cref="timespec_get"/>.</summary>
    public const int TIME_UTC = 1;

    /// <summary><c>timespec_get(ts, base)</c> (C11 §7.27.2.5) — store the current
    /// time in <paramref name="ts"/> for the given <paramref name="base"/>. Only
    /// <see cref="TIME_UTC"/> is supported (UTC since the epoch); returns
    /// <paramref name="base"/> on success, 0 on an unsupported base or a null
    /// pointer. Nanoseconds come from the 100 ns tick resolution of the BCL clock.</summary>
    public static int timespec_get(timespec* ts, int @base)
    {
        if (ts == null || @base != TIME_UTC) { return 0; }
        var now = DateTimeOffset.UtcNow;
        ts->tv_sec = now.ToUnixTimeSeconds();
        ts->tv_nsec = (now.UtcTicks % TimeSpan.TicksPerSecond) * 100;   // 100 ns ticks → ns
        return @base;
    }
}
