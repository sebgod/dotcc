#nullable enable

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;inttypes.h&gt;</c> functions: greatest-width integer arithmetic
/// and string conversion. The <c>PRI*</c> / <c>SCN*</c> format-string macros
/// live in the synthetic header. <c>intmax_t</c> / <c>uintmax_t</c> are
/// <c>long</c> / <c>ulong</c> (dotcc's LP64 model), so these mirror the
/// <c>strtoll</c> / <c>strtoull</c> / <c>llabs</c> / <c>lldiv</c> cores.
/// </summary>
public static unsafe partial class Libc
{
    /// <summary><c>imaxdiv_t</c> — quotient/remainder pair for <see cref="imaxdiv"/>.</summary>
    public struct imaxdiv_t { public long quot; public long rem; }

    /// <summary><c>imaxabs(n)</c> — absolute value of an <c>intmax_t</c>.</summary>
    public static long imaxabs(long n) => n < 0 ? unchecked(-n) : n;

    /// <summary><c>imaxdiv(num, den)</c> — quotient + remainder, truncating toward zero.</summary>
    public static imaxdiv_t imaxdiv(long num, long den) => new imaxdiv_t { quot = num / den, rem = num % den };

    /// <summary><c>strtoimax(nptr, endptr, base)</c> — parse a signed greatest-width int.</summary>
    public static long strtoimax(byte* nptr, byte** endptr, int @base) => strtoll(nptr, endptr, @base);

    /// <summary><c>strtoumax(nptr, endptr, base)</c> — parse an unsigned greatest-width int.</summary>
    public static ulong strtoumax(byte* nptr, byte** endptr, int @base) => strtoull(nptr, endptr, @base);
}
