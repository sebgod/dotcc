#nullable enable

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// C <c>&lt;time.h&gt;</c> calendar family: the <c>struct tm</c> broken-down
/// time type plus <c>gmtime</c> / <c>localtime</c> / <c>mktime</c> /
/// <c>asctime</c> / <c>ctime</c> / <c>strftime</c>. The scalar surface
/// (<c>time</c> / <c>clock</c> / <c>difftime</c>) is in TimeLib.cs.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>struct tm</c> lowering.</b> C code spells the type <c>struct tm</c>;
/// dotcc parses <c>struct ID</c> as a type reference (<c>typeStruct</c>) and
/// emits just the tag (<c>tm</c>), which resolves to this <see cref="tm"/>
/// struct via <c>using static Libc;</c> — exactly like a user-defined struct,
/// but defined here in the runtime. <c>tm</c> is deliberately <i>not</i> seeded
/// in <c>PredefinedTypeNames</c>: that would make it a <c>TYPE_NAME</c> and
/// break the <c>['struct', ID]</c> grammar rule. All-<c>int</c> fields keep
/// <c>tm</c> unmanaged, so <c>struct tm *</c> stays a real pointer.
/// </para>
/// <para>
/// <b>Static-buffer functions.</b> <c>gmtime</c>/<c>localtime</c> return a
/// pointer into a reused thread-local native <c>tm</c>; <c>asctime</c>/
/// <c>ctime</c> into a reused thread-local char buffer — matching C's
/// "may be overwritten by a later call" contract.
/// </para>
/// <para>
/// <b>Determinism.</b> <c>gmtime</c> is UTC (machine-independent — use it for
/// reproducible output); <c>localtime</c>/<c>mktime</c> use the host time zone,
/// so <c>localtime(mktime(&amp;tm))</c> round-trips but absolute values vary.
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>Broken-down calendar time (C89 fields). <c>tm_mon</c> is 0..11,
    /// <c>tm_year</c> is years since 1900, <c>tm_wday</c> 0..6 (Sunday=0),
    /// <c>tm_yday</c> 0..365.</summary>
    // CS8981: the all-lowercase name is deliberate — it must match the C type
    // `struct tm` so `using static Libc;` resolves the emitted `tm`.
#pragma warning disable CS8981
    public struct tm
    {
        public int tm_sec;
        public int tm_min;
        public int tm_hour;
        public int tm_mday;
        public int tm_mon;
        public int tm_year;
        public int tm_wday;
        public int tm_yday;
        public int tm_isdst;
        // glibc/BSD extensions — chibi's (chibi time) reads them. dotcc reports
        // UTC (tm_gmtoff = 0, tm_zone = "UTC") so broken-down time stays
        // machine-independent; see FillTm.
        public long tm_gmtoff;
        public byte* tm_zone;
    }
#pragma warning restore CS8981

    private static readonly string[] _wdayAbbr = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] _wdayFull =
        { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
    private static readonly string[] _monAbbr =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    private static readonly string[] _monFull =
        { "January", "February", "March", "April", "May", "June",
          "July", "August", "September", "October", "November", "December" };

    [ThreadStatic] private static tm* _tmBuf;
    [ThreadStatic] private static byte* _ascBuf;

    private static tm* TmBuf() => _tmBuf != null ? _tmBuf : (_tmBuf = (tm*)NativeMemory.Alloc((nuint)sizeof(tm)));

    // Defensive modulo: real C is UB for out-of-range tm_wday/tm_mon, but a
    // bad index would throw — clamp into range instead.
    private static int Wd(tm* t) => ((t->tm_wday % 7) + 7) % 7;
    private static int Mo(tm* t) => ((t->tm_mon % 12) + 12) % 12;

    private static void FillTm(tm* t, DateTime dt, int isdst)
    {
        t->tm_sec = dt.Second;
        t->tm_min = dt.Minute;
        t->tm_hour = dt.Hour;
        t->tm_mday = dt.Day;
        t->tm_mon = dt.Month - 1;
        t->tm_year = dt.Year - 1900;
        t->tm_wday = (int)dt.DayOfWeek; // C# Sunday=0 matches C
        t->tm_yday = dt.DayOfYear - 1;  // C: 0-based
        t->tm_isdst = isdst;
        t->tm_gmtoff = 0;               // UTC (see the struct comment)
        t->tm_zone = _utcZone;
    }

    // A stable "UTC\0" buffer for tm_zone (allocated once; never freed — process
    // lifetime). A real timezone abbreviation would vary; dotcc reports UTC.
    private static byte* _utcZoneBuf;
    private static byte* _utcZone =>
        _utcZoneBuf != null ? _utcZoneBuf : (_utcZoneBuf = MakeUtcZone());
    private static byte* MakeUtcZone()
    {
        var p = (byte*)NativeMemory.Alloc(4);
        p[0] = (byte)'U'; p[1] = (byte)'T'; p[2] = (byte)'C'; p[3] = 0;
        return p;
    }

    // Reentrant primitives (POSIX *_r): the caller owns the output buffer, so
    // there's no shared state at all. The plain gmtime/localtime/asctime/ctime
    // are thin wrappers that supply a [ThreadStatic] buffer — the project's
    // "reentrant by default" rule (so e.g. two live asctime results need the _r
    // form, since the plain one reuses a single buffer the next call clobbers).

    /// <summary><c>gmtime_r(timer, result)</c> (POSIX) — break <c>*timer</c>
    /// (Unix seconds) down into the caller-provided <paramref name="result"/>
    /// (UTC). Returns <paramref name="result"/>, or <c>null</c> on bad/out-of-range
    /// input.</summary>
    public static tm* gmtime_r(long* timer, tm* result)
    {
        if (timer == null || result == null) { return null; }
        try
        {
            FillTm(result, DateTimeOffset.FromUnixTimeSeconds(*timer).UtcDateTime, 0);
            return result;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary><c>localtime_r(timer, result)</c> (POSIX) — like
    /// <see cref="gmtime_r"/> but in the host's local time zone (sets
    /// <c>tm_isdst</c>).</summary>
    public static tm* localtime_r(long* timer, tm* result)
    {
        if (timer == null || result == null) { return null; }
        try
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(*timer).ToLocalTime().DateTime;
            FillTm(result, local, TimeZoneInfo.Local.IsDaylightSavingTime(local) ? 1 : 0);
            return result;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary><c>gmtime(timer)</c> — UTC broken-down time into a reused
    /// thread-local buffer (overwritten by a later call). Machine-independent —
    /// prefer it for reproducible output. Wraps <see cref="gmtime_r"/>.</summary>
    public static tm* gmtime(long* timer) => gmtime_r(timer, TmBuf());

    /// <summary><c>localtime(timer)</c> — local broken-down time into a reused
    /// thread-local buffer. Wraps <see cref="localtime_r"/>.</summary>
    public static tm* localtime(long* timer) => localtime_r(timer, TmBuf());

    /// <summary>
    /// <c>mktime(t)</c> — interpret <paramref name="t"/> as local broken-down
    /// time, return the corresponding Unix seconds, and normalize the struct in
    /// place (filling <c>tm_wday</c>/<c>tm_yday</c> and folding out-of-range
    /// fields). Returns <c>-1</c> if not representable.
    /// </summary>
    public static long mktime(tm* t)
    {
        if (t == null) { return -1; }
        try
        {
            // Build from Jan 1 of the year, then add the (possibly out-of-range)
            // month/day/time components — DateTime arithmetic normalizes the
            // overflow (e.g. tm_mon=13 rolls into the next year).
            var dt = new DateTime(1900 + t->tm_year, 1, 1, 0, 0, 0, DateTimeKind.Local)
                .AddMonths(t->tm_mon)
                .AddDays(t->tm_mday - 1)
                .AddHours(t->tm_hour)
                .AddMinutes(t->tm_min)
                .AddSeconds(t->tm_sec);
            long secs = new DateTimeOffset(dt).ToUnixTimeSeconds();
            FillTm(t, dt, TimeZoneInfo.Local.IsDaylightSavingTime(dt) ? 1 : 0);
            return secs;
        }
        catch (ArgumentOutOfRangeException) { return -1; }
    }

    // Canonical 26-char form "Www Mmm dd hh:mm:ss yyyy\n" + NUL; the reused
    // buffer is sized generously so even a 10-digit year can't overflow it.
    private static byte* AscBuf() => _ascBuf != null ? _ascBuf : (_ascBuf = (byte*)NativeMemory.Alloc(64));

    /// <summary>
    /// <c>asctime_r(t, buf)</c> (POSIX) — write the fixed
    /// <c>"Www Mmm dd hh:mm:ss yyyy\n"</c> form (C locale) into the
    /// caller-provided <paramref name="buf"/> (must hold at least 26 bytes) and
    /// return it. Matches the standard <c>"%.3s %.3s%3d %.2d:%.2d:%.2d %d\n"</c>.
    /// </summary>
    public static byte* asctime_r(tm* t, byte* buf)
    {
        if (t == null || buf == null) { return null; }
        string s = string.Format(CultureInfo.InvariantCulture,
            "{0} {1}{2,3} {3:D2}:{4:D2}:{5:D2} {6}\n",
            _wdayAbbr[Wd(t)], _monAbbr[Mo(t)], t->tm_mday,
            t->tm_hour, t->tm_min, t->tm_sec, 1900 + t->tm_year);
        var bytes = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i < bytes.Length; i++) { buf[i] = bytes[i]; }
        buf[bytes.Length] = 0;
        return buf;
    }

    /// <summary><c>asctime(t)</c> — like <see cref="asctime_r"/> into a reused
    /// thread-local buffer (overwritten by a later call).</summary>
    public static byte* asctime(tm* t) => asctime_r(t, AscBuf());

    /// <summary><c>ctime_r(timer, buf)</c> (POSIX) ≡
    /// <c>asctime_r(localtime_r(timer, &amp;tmp), buf)</c> with a private
    /// <paramref name="tmp"/> — fully reentrant.</summary>
    public static byte* ctime_r(long* timer, byte* buf)
    {
        tm tmp;
        if (localtime_r(timer, &tmp) == null) { return null; }
        return asctime_r(&tmp, buf);
    }

    /// <summary><c>ctime(timer)</c> ≡ <c>asctime(localtime(timer))</c> — both
    /// using the thread-local buffers.</summary>
    public static byte* ctime(long* timer) => asctime(localtime(timer));

    /// <summary>
    /// <c>strftime(s, max, fmt, t)</c> — format broken-down time into
    /// <paramref name="s"/> per the conversion specifiers in
    /// <paramref name="fmt"/>. Returns the number of bytes written (excluding
    /// the terminating NUL), or 0 if the result (with NUL) would exceed
    /// <paramref name="max"/>. C-locale names. (<c>max</c> is the C
    /// <c>size_t</c>, which dotcc threads as <c>int</c>.)
    /// </summary>
    public static int strftime(byte* s, int max, byte* fmt, tm* t)
    {
        if (s == null || fmt == null || t == null || max <= 0) { return 0; }
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        for (byte* p = fmt; *p != 0; p++)
        {
            if (*p != (byte)'%') { sb.Append((char)*p); continue; }
            p++;
            switch (*p)
            {
                case (byte)'Y': sb.Append((1900 + t->tm_year).ToString(ci)); break;
                case (byte)'y': sb.Append(((1900 + t->tm_year) % 100).ToString("D2", ci)); break;
                case (byte)'C': sb.Append(((1900 + t->tm_year) / 100).ToString("D2", ci)); break;
                case (byte)'m': sb.Append((t->tm_mon + 1).ToString("D2", ci)); break;
                case (byte)'d': sb.Append(t->tm_mday.ToString("D2", ci)); break;
                case (byte)'e': sb.Append(t->tm_mday.ToString(ci).PadLeft(2)); break;
                case (byte)'H': sb.Append(t->tm_hour.ToString("D2", ci)); break;
                case (byte)'I':
                {
                    int h = t->tm_hour % 12; if (h == 0) { h = 12; }
                    sb.Append(h.ToString("D2", ci));
                    break;
                }
                case (byte)'M': sb.Append(t->tm_min.ToString("D2", ci)); break;
                case (byte)'S': sb.Append(t->tm_sec.ToString("D2", ci)); break;
                case (byte)'p': sb.Append(t->tm_hour < 12 ? "AM" : "PM"); break;
                case (byte)'A': sb.Append(_wdayFull[Wd(t)]); break;
                case (byte)'a': sb.Append(_wdayAbbr[Wd(t)]); break;
                case (byte)'B': sb.Append(_monFull[Mo(t)]); break;
                case (byte)'b': case (byte)'h': sb.Append(_monAbbr[Mo(t)]); break;
                case (byte)'j': sb.Append((t->tm_yday + 1).ToString("D3", ci)); break;
                case (byte)'w': sb.Append(Wd(t).ToString(ci)); break;
                case (byte)'u': { int w = Wd(t); sb.Append((w == 0 ? 7 : w).ToString(ci)); break; }
                case (byte)'F':
                    sb.Append($"{1900 + t->tm_year:D4}-{t->tm_mon + 1:D2}-{t->tm_mday:D2}"); break;
                case (byte)'T':
                    sb.Append($"{t->tm_hour:D2}:{t->tm_min:D2}:{t->tm_sec:D2}"); break;
                case (byte)'R': sb.Append($"{t->tm_hour:D2}:{t->tm_min:D2}"); break;
                case (byte)'D':
                    sb.Append($"{t->tm_mon + 1:D2}/{t->tm_mday:D2}/{(1900 + t->tm_year) % 100:D2}"); break;
                case (byte)'n': sb.Append('\n'); break;
                case (byte)'t': sb.Append('\t'); break;
                case (byte)'%': sb.Append('%'); break;
                case 0: goto done;                  // trailing '%'
                default: sb.Append('%'); sb.Append((char)*p); break; // unknown → literal
            }
        }
    done:
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (bytes.Length >= max) { return 0; }      // doesn't fit with the NUL
        for (int i = 0; i < bytes.Length; i++) { s[i] = bytes[i]; }
        s[bytes.Length] = 0;
        return bytes.Length;
    }
}
