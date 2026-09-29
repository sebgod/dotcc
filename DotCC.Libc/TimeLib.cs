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
    /// stack-allocate it and take its address. Runtime-owned: <c>&lt;time.h&gt;</c>
    /// declares the same body for the IR, the backend emits no C# type for it, and
    /// the tag resolves here (<c>RuntimeOwnedAggregateTests</c>). Used by <c>timespec_get</c> and the <c>&lt;threads.h&gt;</c> timed calls
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

    /// <summary><c>clock_gettime(clk, ts)</c> (POSIX): the time of clock <paramref name="clk"/>, by
    /// <c>&lt;time.h&gt;</c>'s Linux ids: 0 <c>CLOCK_REALTIME</c> (UTC since the epoch), 1
    /// <c>CLOCK_MONOTONIC</c> (the <see cref="System.Diagnostics.Stopwatch"/> timestamp, which never
    /// steps), 2 <c>CLOCK_PROCESS_CPUTIME_ID</c> (the process's user plus system CPU time). Returns 0,
    /// or -1 with <c>errno</c> EINVAL for another clock (a thread's CPU time is not available) and
    /// EFAULT for a null <paramref name="ts"/>.</summary>
    public static int clock_gettime(int clk, timespec* ts)
    {
        if (ts == null) { errno = EFAULT; return -1; }
        long ticks;   // 100 ns units
        switch (clk)
        {
            case 0:
                ticks = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
                break;
            case 1:
                var stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                var freq = System.Diagnostics.Stopwatch.Frequency;
                ts->tv_sec = stamp / freq;
                ts->tv_nsec = (long)((Int128)(stamp % freq) * 1_000_000_000 / freq);
                return 0;
            case 2:
                ticks = Environment.CpuUsage.TotalTime.Ticks;
                break;
            default:
                errno = EINVAL;
                return -1;
        }
        ts->tv_sec = ticks / TimeSpan.TicksPerSecond;
        ts->tv_nsec = ticks % TimeSpan.TicksPerSecond * 100;
        return 0;
    }

    /// <summary><c>clock_getres(clk, res)</c> (POSIX): the resolution of clock <paramref name="clk"/>
    /// (see <see cref="clock_gettime"/>): 100 ns for the real-time and CPU clocks, one
    /// <see cref="System.Diagnostics.Stopwatch"/> tick for the monotonic one. A null
    /// <paramref name="res"/> only checks the clock.</summary>
    public static int clock_getres(int clk, timespec* res)
    {
        long nsec;
        switch (clk)
        {
            case 0 or 2: nsec = 100; break;
            case 1: nsec = Math.Max(1, 1_000_000_000 / System.Diagnostics.Stopwatch.Frequency); break;
            default: errno = EINVAL; return -1;
        }
        if (res != null) { res->tv_sec = 0; res->tv_nsec = nsec; }
        return 0;
    }

    /// <summary>The clock ticks per second of <see cref="times"/> (<c>sysconf(_SC_CLK_TCK)</c>).</summary>
    private const long ClockTicksPerSecond = 100;

    /// <summary><c>times(buf)</c> (POSIX <c>&lt;sys/times.h&gt;</c>): the process's user and system CPU
    /// time in clock ticks into <c>struct tms</c> (four <c>clock_t</c>: utime, stime, cutime, cstime;
    /// the children's are 0, none having been waited for). Returns the ticks since an arbitrary fixed
    /// point, from the monotonic clock.</summary>
    public static long times(void* buf)
    {
        const long per = TimeSpan.TicksPerSecond / ClockTicksPerSecond;
        if (buf != null)
        {
            var cpu = Environment.CpuUsage;
            var t = (long*)buf;
            t[0] = cpu.UserTime.Ticks / per;
            t[1] = cpu.PrivilegedTime.Ticks / per;
            t[2] = 0;
            t[3] = 0;
        }
        return (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() * ClockTicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary><c>sysconf(name)</c> (POSIX): the configuration values <c>&lt;unistd.h&gt;</c> names,
    /// by Linux's numbers: 2 <c>_SC_CLK_TCK</c>, 30 <c>_SC_PAGESIZE</c>, 83/84
    /// <c>_SC_NPROCESSORS_CONF</c>/<c>_ONLN</c>. Any other name returns -1 with <c>errno</c>
    /// EINVAL.</summary>
    public static long sysconf(int name)
    {
        switch (name)
        {
            case 2: return ClockTicksPerSecond;
            case 30: return Environment.SystemPageSize;
            case 83 or 84: return Environment.ProcessorCount;
            default: errno = EINVAL; return -1;
        }
    }
}
