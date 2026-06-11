#nullable enable

using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// C <c>&lt;locale.h&gt;</c> (C90 7.4): <c>setlocale</c> + <c>localeconv</c>.
/// </summary>
/// <remarks>
/// <para>
/// dotcc supports only the <c>"C"</c> (== <c>"POSIX"</c>) locale — the one the
/// standard guarantees at program startup, and the only set of conventions that
/// is portable across hosts. <c>setlocale</c> accepts <c>NULL</c> (query) /
/// <c>""</c> / <c>"C"</c> / <c>"POSIX"</c> and returns <c>"C"</c>; any other
/// locale name is reported unsupported (<c>NULL</c>), exactly as a minimal C
/// library would. The <paramref name="category"/> is accepted but ignored —
/// there is only one locale to select.
/// </para>
/// <para>
/// <b><c>struct lconv</c> lowering.</b> C code spells the type <c>struct lconv</c>;
/// dotcc parses it via the usual <c>struct ID</c> rule and emits the bare tag
/// <c>lconv</c>, which binds to <see cref="lconv"/> here through
/// <c>using static Libc;</c> — so the body lives ONLY in the runtime (same
/// pattern as <c>&lt;time.h&gt;</c>'s <c>struct tm</c>); <c>&lt;locale.h&gt;</c>
/// must NOT redefine it. C <c>char *</c> members lower to <c>byte*</c> (dotcc's
/// <c>char</c> is <c>byte</c>), so <c>localeconv()-&gt;decimal_point[0]</c> reads
/// a byte — the locale-aware decimal point.
/// </para>
/// <para>
/// <c>localeconv</c> returns a single process-lifetime instance filled with the
/// "C" locale's conventions: <c>decimal_point = "."</c>, every other string
/// member <c>""</c>, and the numeric (monetary) members <c>CHAR_MAX</c> (255 in
/// dotcc's unsigned-char model) — the standard's "not available" sentinel.
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    // CS8981: the all-lowercase name is deliberate — it must match the C type
    // `struct lconv` so `using static Libc;` resolves the emitted `lconv`.
#pragma warning disable CS8981
    public struct lconv
    {
        public byte* decimal_point;
        public byte* thousands_sep;
        public byte* grouping;
        public byte* int_curr_symbol;
        public byte* currency_symbol;
        public byte* mon_decimal_point;
        public byte* mon_thousands_sep;
        public byte* mon_grouping;
        public byte* positive_sign;
        public byte* negative_sign;
        public byte int_frac_digits;
        public byte frac_digits;
        public byte p_cs_precedes;
        public byte p_sep_by_space;
        public byte n_cs_precedes;
        public byte n_sep_by_space;
        public byte p_sign_posn;
        public byte n_sign_posn;
        public byte int_p_cs_precedes;
        public byte int_n_cs_precedes;
        public byte int_p_sep_by_space;
        public byte int_n_sep_by_space;
        public byte int_p_sign_posn;
        public byte int_n_sign_posn;
    }
#pragma warning restore CS8981

    // The "C" locale's lconv, built once and kept for the process lifetime
    // (real localeconv returns a pointer to static storage the caller mustn't
    // modify). Native memory so the pointer never moves; the string members
    // point at pinned UTF-8 RVA literals via L(...).
    private static lconv* _lconv;
    private static readonly object _lconvLock = new();

    /// <summary><c>localeconv()</c> — the current locale's numeric/monetary
    /// formatting conventions. dotcc is always the "C" locale: decimal point
    /// ".", every other string empty, numeric members CHAR_MAX ("unavailable").
    /// </summary>
    public static lconv* localeconv()
    {
        // Double-checked lock: build into a LOCAL and publish to the field only
        // when fully initialized, so concurrent first-callers (e.g. parallel
        // test collections) never double-allocate or observe a half-filled
        // struct — the returned pointer is stable for the process lifetime.
        var existing = _lconv;
        if (existing != null) { return existing; }
        lock (_lconvLock)
        {
            if (_lconv != null) { return _lconv; }
            var p = (lconv*)NativeMemory.AllocZeroed((nuint)sizeof(lconv));
            byte* empty = L("\0"u8);
            p->decimal_point     = L(".\0"u8);
            p->thousands_sep     = empty;
            p->grouping          = empty;
            p->int_curr_symbol   = empty;
            p->currency_symbol   = empty;
            p->mon_decimal_point = empty;
            p->mon_thousands_sep = empty;
            p->mon_grouping      = empty;
            p->positive_sign     = empty;
            p->negative_sign     = empty;
            // CHAR_MAX (255, dotcc's unsigned char) = "not available" in "C".
            const byte na = 255;
            p->int_frac_digits = na; p->frac_digits = na;
            p->p_cs_precedes = na; p->p_sep_by_space = na;
            p->n_cs_precedes = na; p->n_sep_by_space = na;
            p->p_sign_posn = na; p->n_sign_posn = na;
            p->int_p_cs_precedes = na; p->int_n_cs_precedes = na;
            p->int_p_sep_by_space = na; p->int_n_sep_by_space = na;
            p->int_p_sign_posn = na; p->int_n_sign_posn = na;
            _lconv = p;       // publish only after fully built
            return p;
        }
    }

    /// <summary><c>setlocale(category, locale)</c> — select / query the locale.
    /// dotcc has only the "C" locale: NULL queries it; "" / "C" / "POSIX" select
    /// it (return "C"); any other name is unsupported (NULL). The category is
    /// accepted but ignored.</summary>
    public static byte* setlocale(int category, byte* locale)
    {
        if (locale == null) { return L("C\0"u8); }  // query — always "C"
        var name = Encoding.UTF8.GetString(locale, (int)strlen(locale));
        return name is "" or "C" or "POSIX" ? L("C\0"u8) : null;
    }
}
