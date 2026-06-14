#nullable enable

using System;
using System.Runtime.InteropServices;

namespace DotCC.Libc;

/// <summary>
/// C <c>&lt;wchar.h&gt;</c> wide-string and wide-memory surface. dotcc's
/// <c>wchar_t</c> is the MSVC shape — an unsigned 16-bit UTF-16 code unit lowered
/// to C# <c>char</c> — so every function here is the direct 16-bit sibling of its
/// <see cref="Libc">byte</see> counterpart in <see cref="Libc">Libc.cs</see> /
/// <c>StringLib.cs</c>: a bare <c>char*</c> loop over NUL-terminated UTF-16
/// buffers (no GC allocation), exactly mirroring how the <c>str*</c>/<c>mem*</c>
/// family walks <c>byte*</c>.
/// </summary>
/// <remarks>
/// The numeric parsers (<see cref="wcstol"/> et al.) don't reimplement C's
/// strtol/strtod grammar — they transcode the leading ASCII run to a scratch byte
/// buffer and delegate to the battle-tested byte cores (full C semantics: base
/// 0/2-36, <c>errno</c>+clamp on overflow, hex floats, <c>inf</c>/<c>nan</c>),
/// then map the byte endptr back onto the wide pointer.
///
/// NOT provided (see <c>wchar.h</c>): wide formatted I/O (<c>wprintf</c> /
/// <c>w*scanf</c>), wide input (<c>fgetws</c> / <c>getwc</c>), and the
/// multibyte&lt;-&gt;wide conversion model (<c>mbrtowc</c> / <c>wcrtomb</c>).
/// dotcc maps C <c>size_t</c> to <c>ulong</c>; length-returning functions narrow
/// to <c>int</c> to match <see cref="Libc.strlen"/>.
/// </remarks>
public static unsafe partial class Libc
{
    // ---------------------------------------------------------------------
    // Length / comparison / copy
    // ---------------------------------------------------------------------

    /// <summary><c>wcslen(s)</c> — code units before the first <c>0</c> in
    /// <paramref name="s"/> (the wide <see cref="strlen"/>).</summary>
    public static int wcslen(char* s)
    {
        int n = 0;
        while (s[n] != 0) { n++; }
        return n;
    }

    /// <summary><c>wcscmp(a, b)</c> — lexicographic compare by code unit; returns
    /// 0 / negative / positive (the wide <see cref="strcmp(byte*, byte*)"/>).</summary>
    public static int wcscmp(char* a, char* b)
    {
        while (*a != 0 && *a == *b) { a++; b++; }
        return *a - *b;
    }

    /// <summary><c>wcsncmp(a, b, n)</c> — compare at most <paramref name="n"/>
    /// code units; stops early at a mismatch or a shared NUL.</summary>
    public static int wcsncmp(char* a, char* b, ulong n)
    {
        for (ulong i = 0; i < n; i++)
        {
            if (a[i] != b[i]) { return a[i] - b[i]; }
            if (a[i] == 0) { return 0; }
        }
        return 0;
    }

    /// <summary><c>wcscoll(a, b)</c> — locale-aware compare. dotcc runs the
    /// <c>"C"</c> locale, where collation is plain code-unit order, so this is
    /// exactly <see cref="wcscmp"/>.</summary>
    public static int wcscoll(char* a, char* b) => wcscmp(a, b);

    /// <summary><c>wcscpy(dst, src)</c> — copy NUL-terminated <paramref name="src"/>
    /// (including the terminating <c>0</c>) into <paramref name="dst"/>. Returns
    /// <paramref name="dst"/>.</summary>
    public static char* wcscpy(char* dst, char* src)
    {
        char* p = dst;
        while ((*p++ = *src++) != 0) { }
        return dst;
    }

    /// <summary><c>wcsncpy(dst, src, n)</c> — copy up to <paramref name="n"/> code
    /// units; if <paramref name="src"/> is shorter, NUL-pad to <paramref name="n"/>;
    /// if it is <paramref name="n"/> or longer, <em>no</em> terminating NUL is
    /// written (the classic strncpy footgun, faithfully reproduced). Returns
    /// <paramref name="dst"/>.</summary>
    public static char* wcsncpy(char* dst, char* src, ulong n)
    {
        ulong i = 0;
        for (; i < n && src[i] != 0; i++) { dst[i] = src[i]; }
        for (; i < n; i++) { dst[i] = '\0'; }
        return dst;
    }

    // ---------------------------------------------------------------------
    // Concatenation
    // ---------------------------------------------------------------------

    /// <summary><c>wcscat(dst, src)</c> — append NUL-terminated <paramref name="src"/>
    /// to the end of <paramref name="dst"/>, re-terminating. Returns
    /// <paramref name="dst"/>.</summary>
    public static char* wcscat(char* dst, char* src)
    {
        char* p = dst;
        while (*p != 0) { p++; }
        while ((*p++ = *src++) != 0) { }
        return dst;
    }

    /// <summary><c>wcsncat(dst, src, n)</c> — append at most <paramref name="n"/>
    /// code units of <paramref name="src"/>, then always write a terminating NUL.
    /// Returns <paramref name="dst"/>.</summary>
    public static char* wcsncat(char* dst, char* src, ulong n)
    {
        char* p = dst;
        while (*p != 0) { p++; }
        ulong i = 0;
        for (; i < n && src[i] != 0; i++) { p[i] = src[i]; }
        p[i] = '\0';
        return dst;
    }

    // ---------------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------------

    /// <summary><c>wcschr(s, c)</c> — first occurrence of code unit
    /// <paramref name="c"/> in <paramref name="s"/>, or <c>null</c>. The
    /// terminating NUL is part of the string, so <c>wcschr(s, 0)</c> finds it.</summary>
    public static char* wcschr(char* s, char c)
    {
        while (true)
        {
            if (*s == c) { return s; }
            if (*s == 0) { return null; }
            s++;
        }
    }

    /// <summary><c>wcsrchr(s, c)</c> — last occurrence of <paramref name="c"/> in
    /// <paramref name="s"/>, or <c>null</c>. <c>wcsrchr(s, 0)</c> finds the NUL.</summary>
    public static char* wcsrchr(char* s, char c)
    {
        char* last = null;
        while (true)
        {
            if (*s == c) { last = s; }
            if (*s == 0) { return last; }
            s++;
        }
    }

    /// <summary><c>wcsstr(haystack, needle)</c> — first occurrence of
    /// <paramref name="needle"/> in <paramref name="haystack"/>, or <c>null</c>.
    /// An empty needle matches at the start.</summary>
    public static char* wcsstr(char* haystack, char* needle)
    {
        if (*needle == 0) { return haystack; }
        for (char* h = haystack; *h != 0; h++)
        {
            char* a = h;
            char* b = needle;
            while (*a != 0 && *b != 0 && *a == *b) { a++; b++; }
            if (*b == 0) { return h; }
        }
        return null;
    }

    /// <summary>Whether the NUL-terminated wide <paramref name="set"/> contains code
    /// unit <paramref name="c"/> — the inner scan of wcsspn/wcscspn/wcspbrk.</summary>
    private static bool WcsSetContains(char* set, char c)
    {
        for (char* p = set; *p != 0; p++) { if (*p == c) { return true; } }
        return false;
    }

    /// <summary><c>wcsspn(s, accept)</c> — length of the initial run of
    /// <paramref name="s"/> consisting only of code units from
    /// <paramref name="accept"/>.</summary>
    public static int wcsspn(char* s, char* accept)
    {
        int n = 0;
        while (s[n] != 0 && WcsSetContains(accept, s[n])) { n++; }
        return n;
    }

    /// <summary><c>wcscspn(s, reject)</c> — length of the initial run of
    /// <paramref name="s"/> containing no code unit from
    /// <paramref name="reject"/>.</summary>
    public static int wcscspn(char* s, char* reject)
    {
        int n = 0;
        while (s[n] != 0 && !WcsSetContains(reject, s[n])) { n++; }
        return n;
    }

    /// <summary><c>wcspbrk(s, accept)</c> — pointer to the first code unit of
    /// <paramref name="s"/> that is in <paramref name="accept"/>, or <c>null</c>.</summary>
    public static char* wcspbrk(char* s, char* accept)
    {
        for (char* p = s; *p != 0; p++) { if (WcsSetContains(accept, *p)) { return p; } }
        return null;
    }

    /// <summary><c>wcstok(str, delim, saveptr)</c> — C's wide tokenizer, which is
    /// reentrant by signature (an explicit <c>wchar_t**</c> save slot, unlike the
    /// stateful narrow <see cref="strtok"/>). First call passes the string; later
    /// calls pass <c>NULL</c> to resume from <paramref name="saveptr"/>. Writes a
    /// NUL over each consumed delimiter. Returns the next token, or <c>null</c>.</summary>
    public static char* wcstok(char* str, char* delim, char** saveptr)
    {
        char* s = str != null ? str : *saveptr;
        if (s == null) { return null; }
        // Skip leading delimiters.
        while (*s != 0 && WcsSetContains(delim, *s)) { s++; }
        if (*s == 0) { *saveptr = s; return null; }
        char* token = s;
        // Run to the next delimiter or the end.
        while (*s != 0 && !WcsSetContains(delim, *s)) { s++; }
        if (*s != 0) { *s = '\0'; s++; }
        *saveptr = s;
        return token;
    }

    // ---------------------------------------------------------------------
    // Wide-memory ops (counts are in wchar_t units, not bytes)
    // ---------------------------------------------------------------------

    /// <summary><c>wmemcpy(dst, src, n)</c> — copy <paramref name="n"/> code units.
    /// Returns <paramref name="dst"/>.</summary>
    public static char* wmemcpy(char* dst, char* src, ulong n)
    {
        ulong bytes = n * (ulong)sizeof(char);
        Buffer.MemoryCopy(src, dst, bytes, bytes);
        return dst;
    }

    /// <summary><c>wmemmove(dst, src, n)</c> — copy <paramref name="n"/> code units,
    /// overlap-safe. Returns <paramref name="dst"/>.</summary>
    public static char* wmemmove(char* dst, char* src, ulong n)
    {
        ulong bytes = n * (ulong)sizeof(char);
        Buffer.MemoryCopy(src, dst, bytes, bytes);   // overlap-safe
        return dst;
    }

    /// <summary><c>wmemset(dst, c, n)</c> — fill <paramref name="n"/> code units of
    /// <paramref name="dst"/> with <paramref name="c"/>. Returns
    /// <paramref name="dst"/>.</summary>
    public static char* wmemset(char* dst, char c, ulong n)
    {
        for (ulong i = 0; i < n; i++) { dst[i] = c; }
        return dst;
    }

    /// <summary><c>wmemcmp(a, b, n)</c> — compare <paramref name="n"/> code units;
    /// signed difference of the first differing pair, or 0.</summary>
    public static int wmemcmp(char* a, char* b, ulong n)
    {
        for (ulong i = 0; i < n; i++)
        {
            if (a[i] != b[i]) { return a[i] - b[i]; }
        }
        return 0;
    }

    /// <summary><c>wmemchr(s, c, n)</c> — pointer to the first occurrence of
    /// <paramref name="c"/> within the first <paramref name="n"/> code units of
    /// <paramref name="s"/>, or <c>null</c>.</summary>
    public static char* wmemchr(char* s, char c, ulong n)
    {
        for (ulong i = 0; i < n; i++)
        {
            if (s[i] == c) { return s + i; }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Wide → number conversions
    //
    // The wide numeric grammar (whitespace, sign, base prefix, digits, decimal
    // point, exponent, inf/nan) is entirely ASCII, so each parser copies the
    // leading <= 0x7F run into a scratch byte buffer and delegates to the byte
    // core — full C semantics already live there (base 0/2-36, errno+clamp on
    // overflow, C99 hex floats, inf/nan). One ASCII code unit maps to one byte,
    // so the byte endptr offset translates straight back onto the wide pointer.
    // ---------------------------------------------------------------------

    /// <summary>Allocate a NUL-terminated byte copy of the leading ASCII run of
    /// wide string <paramref name="nptr"/> (stopping at the first &gt; 0x7F unit or
    /// the NUL — neither can be part of a number, so it's a valid boundary). The
    /// caller must <see cref="NativeMemory.Free(void*)"/> the result.</summary>
    private static byte* WideNumericScratch(char* nptr)
    {
        int n = 0;
        while (nptr[n] != 0 && nptr[n] <= 0x7F) { n++; }
        byte* buf = (byte*)NativeMemory.Alloc((nuint)n + 1);
        for (int i = 0; i < n; i++) { buf[i] = (byte)nptr[i]; }
        buf[n] = 0;
        return buf;
    }

    /// <summary>Run <paramref name="nptr"/> through the byte signed-integer core and
    /// translate the endptr back to the wide pointer (shared by wcstol/wcstoll).</summary>
    private static long WcstollViaByte(char* nptr, char** endptr, int @base)
    {
        byte* buf = WideNumericScratch(nptr);
        byte* bEnd;
        long v = strtol(buf, &bEnd, @base);
        if (endptr != null) { *endptr = nptr + (bEnd - buf); }
        NativeMemory.Free(buf);
        return v;
    }

    /// <summary>Run <paramref name="nptr"/> through the byte unsigned-integer core and
    /// translate the endptr back to the wide pointer (shared by wcstoul/wcstoull).</summary>
    private static ulong WcstoullViaByte(char* nptr, char** endptr, int @base)
    {
        byte* buf = WideNumericScratch(nptr);
        byte* bEnd;
        ulong v = strtoul(buf, &bEnd, @base);
        if (endptr != null) { *endptr = nptr + (bEnd - buf); }
        NativeMemory.Free(buf);
        return v;
    }

    /// <summary>Run <paramref name="nptr"/> through the byte floating-point core and
    /// translate the endptr back to the wide pointer (shared by wcstod/wcstof/wcstold).</summary>
    private static double WcstodViaByte(char* nptr, char** endptr)
    {
        byte* buf = WideNumericScratch(nptr);
        byte* bEnd;
        double v = strtod(buf, &bEnd);
        if (endptr != null) { *endptr = nptr + (bEnd - buf); }
        NativeMemory.Free(buf);
        return v;
    }

    /// <summary><c>wcstol(nptr, endptr, base)</c> — parse a signed long.</summary>
    public static long wcstol(char* nptr, char** endptr, int @base) => WcstollViaByte(nptr, endptr, @base);

    /// <summary><c>wcstoll(nptr, endptr, base)</c> (C99) — signed long long
    /// (== <c>wcstol</c> on dotcc's LP64 model).</summary>
    public static long wcstoll(char* nptr, char** endptr, int @base) => WcstollViaByte(nptr, endptr, @base);

    /// <summary><c>wcstoul(nptr, endptr, base)</c> — parse an unsigned long.</summary>
    public static ulong wcstoul(char* nptr, char** endptr, int @base) => WcstoullViaByte(nptr, endptr, @base);

    /// <summary><c>wcstoull(nptr, endptr, base)</c> (C99) — unsigned long long.</summary>
    public static ulong wcstoull(char* nptr, char** endptr, int @base) => WcstoullViaByte(nptr, endptr, @base);

    /// <summary><c>wcstod(nptr, endptr)</c> — parse a double (sign, decimal point,
    /// <c>e</c>/<c>E</c> exponent, C99 hex floats, <c>inf</c>/<c>nan</c>).</summary>
    public static double wcstod(char* nptr, char** endptr) => WcstodViaByte(nptr, endptr);

    /// <summary><c>wcstof(nptr, endptr)</c> (C99) — parse a float (the double parse,
    /// narrowed to <see cref="float"/>).</summary>
    public static float wcstof(char* nptr, char** endptr) => (float)WcstodViaByte(nptr, endptr);

    /// <summary><c>wcstold(nptr, endptr)</c> (C99) — parse a long double
    /// (== <c>double</c> on dotcc's model).</summary>
    public static double wcstold(char* nptr, char** endptr) => WcstodViaByte(nptr, endptr);
}
