#nullable enable

using System;
using System.IO;
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
/// Wide character/line I/O (<c>fputwc</c>/<c>fgetwc</c>/<c>fputws</c>/<c>fgetws</c>/…)
/// and wide formatted I/O (<c>wprintf</c>/<c>fwprintf</c>/<c>swprintf</c> and the
/// <c>w*scanf</c> family) are provided too — see further down. Output goes through
/// the stream's UTF-8 <c>TextWriter</c>; input assembles UTF-8 off the byte reader;
/// the formatted family transcodes the wide format to UTF-8 and reuses the byte
/// <see cref="PrintfBuilder"/>/<see cref="ScanfReader"/>. NOT provided: the explicit
/// multibyte&lt;-&gt;wide conversion model (<c>mbrtowc</c> / <c>wcrtomb</c> /
/// <c>mbstate_t</c>). dotcc maps C <c>size_t</c> to <c>ulong</c>; length-returning
/// functions narrow to <c>int</c> to match <see cref="Libc.strlen"/>.
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

    // ---------------------------------------------------------------------
    // Wide character / line I/O
    //
    // dotcc's narrow encoding is UTF-8, so wide I/O is UTF-8<->UTF-16. Output
    // goes through the stream's TextWriter (WriterFor) — the same unbuffered
    // UTF-8 writer fprintf uses, so wide and narrow writes on one handle stay
    // ordered. Input assembles UTF-8 off the byte reader (ReadWideFrom),
    // byte-stream-consistent with fgetc/fgets (shared bytes + ungetc pushback).
    // Wide FORMATTED I/O (wprintf / w*scanf) is handled by the emitter — see
    // the printf/scanf fluent lowering — not here.
    // ---------------------------------------------------------------------

    /// <summary><c>fputwc(c, stream)</c> — write the wide character
    /// <paramref name="c"/> (UTF-8-encoded) to <paramref name="stream"/>; returns
    /// it, or <c>WEOF</c> on a non-output stream.</summary>
    public static int fputwc(char c, FILE* stream)
    {
        WriterFor(stream).Write(c);
        return c;
    }

    /// <summary><c>putwc(c, stream)</c> — identical to <see cref="fputwc"/>.</summary>
    public static int putwc(char c, FILE* stream) => fputwc(c, stream);

    /// <summary><c>putwchar(c)</c> ≡ <c>fputwc(c, stdout)</c>.</summary>
    public static int putwchar(char c) => fputwc(c, stdout);

    /// <summary><c>fputws(s, stream)</c> — write NUL-terminated wide string
    /// <paramref name="s"/> (UTF-8-encoded) to <paramref name="stream"/>; no
    /// newline (per C — only the never-standardised wide <c>puts</c> would add one).
    /// Returns a non-negative value on success.</summary>
    public static int fputws(char* s, FILE* stream)
    {
        if (s == null) { return -1; }
        WriterFor(stream).Write(new string(s, 0, wcslen(s)));
        return 0;
    }

    /// <summary><c>fgetwc(stream)</c> — read one wide character from
    /// <paramref name="stream"/> (decoding a UTF-8 multibyte sequence; a code point
    /// above U+FFFF arrives as two calls, the surrogate pair). Returns the code unit
    /// (0..0xFFFF) or <c>WEOF</c> (-1) at end of input.</summary>
    public static int fgetwc(FILE* stream) => ReadWideFrom(stream);

    /// <summary><c>getwc(stream)</c> — identical to <see cref="fgetwc"/>.</summary>
    public static int getwc(FILE* stream) => fgetwc(stream);

    /// <summary><c>getwchar()</c> ≡ <c>fgetwc(stdin)</c>.</summary>
    public static int getwchar() => fgetwc(stdin);

    /// <summary><c>ungetwc(c, stream)</c> — push one wide character back so the next
    /// <see cref="fgetwc"/> returns it. One pushback only; <c>WEOF</c> can't be
    /// pushed. Returns <paramref name="c"/> or <c>WEOF</c> on failure.</summary>
    public static int ungetwc(int c, FILE* stream) => UngetWideTo(stream, c);

    /// <summary><c>fgetws(s, n, stream)</c> — read at most <c><paramref name="n"/>-1</c>
    /// wide characters into <paramref name="s"/>, stopping after a newline (which is
    /// kept) or at end of input, then NUL-terminate. Returns <paramref name="s"/>, or
    /// <c>null</c> if end of input is hit before any character (the wide
    /// <see cref="fgets"/>).</summary>
    public static char* fgetws(char* s, int n, FILE* stream)
    {
        if (n <= 0) { return null; }
        int i = 0;
        while (i < n - 1)
        {
            int ch = ReadWideFrom(stream);
            if (ch < 0)
            {
                if (i == 0) { return null; }   // EOF with nothing read
                break;
            }
            s[i++] = (char)ch;
            if (ch == '\n') { break; }
        }
        s[i] = '\0';
        return s;
    }

    // ---------------------------------------------------------------------
    // Wide formatted output (wprintf / fwprintf / swprintf)
    //
    // The emitter lowers these to the SAME fluent builder as printf/sprintf
    // (see CSharpBackend's printf-family lowering). The wide format is
    // transcoded to UTF-8 (so the byte PrintfBuilder parses the identical spec
    // grammar); only a wide %s arg differs, handled by PrintfBuilder.Arg(char*).
    // Wide formatted INPUT (w*scanf) lowers the same way to ScanfReader.
    // ---------------------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr> _wideFmtToUtf8 =
        new();

    /// <summary>Transcode a wide (UTF-16) printf/scanf format to a pinned,
    /// NUL-terminated UTF-8 <c>byte*</c> so the byte <see cref="PrintfBuilder"/> /
    /// <see cref="ScanfReader"/> parses it unchanged. Pooled by the wide pointer — a
    /// .rodata-style pool transcoded once per distinct literal (a format is ~always
    /// an <c>L"…"</c> literal; a mutable runtime format reusing an address is
    /// unsupported).</summary>
    private static byte* WideFmtToUtf8(char* wfmt)
    {
        var key = (IntPtr)wfmt;
        if (_wideFmtToUtf8.TryGetValue(key, out var hit)) { return (byte*)hit; }   // lock-free hot path
        // Miss: transcode + allocate, then publish atomically with the value-taking
        // GetOrAdd. Two threads racing the same new key both allocate; the loser
        // frees its buffer (otherwise the dropped NativeMemory block would leak — the
        // reason a value cache like this can't use the naive factory GetOrAdd).
        var bytes = System.Text.Encoding.UTF8.GetBytes(new string(wfmt, 0, wcslen(wfmt)));
        byte* buf = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        for (int i = 0; i < bytes.Length; i++) { buf[i] = bytes[i]; }
        buf[bytes.Length] = 0;
        var actual = _wideFmtToUtf8.GetOrAdd(key, (IntPtr)buf);
        if (actual != (IntPtr)buf) { NativeMemory.Free(buf); }   // lost the race → free ours
        return (byte*)actual;
    }

    /// <summary><c>wprintf(fmt, …)</c> — wide formatted output to stdout. The emitter
    /// expands the variadic tail into the fluent <c>.Arg(…).Done()</c> chain.</summary>
    public static PrintfBuilder wprintf(char* fmt) => new PrintfBuilder(WriterFor(stdout), WideFmtToUtf8(fmt));

    /// <summary><c>fwprintf(stream, fmt, …)</c> — wide formatted output to a stream.</summary>
    public static PrintfBuilder fwprintf(FILE* stream, char* fmt) => new PrintfBuilder(WriterFor(stream), WideFmtToUtf8(fmt));

    /// <summary><c>swprintf(s, n, fmt, …)</c> — wide formatted output into
    /// <paramref name="s"/>, at most <paramref name="n"/> wide chars including the
    /// terminating NUL (returns a negative value if it doesn't fit — C's swprintf
    /// contract, unlike snprintf).</summary>
    public static WSprintfBuilder swprintf(char* s, ulong n, char* fmt) => new WSprintfBuilder(s, WideFmtToUtf8(fmt), (int)n);

    // ---------------------------------------------------------------------
    // Wide formatted input (wscanf / fwscanf / swscanf)
    //
    // The emitter lowers these to the same fluent .Read(ptr).Done() chain as
    // scanf/sscanf. The wide format is transcoded to UTF-8 so the byte
    // ScanfReader parses it unchanged; a wide %s/%c target is a char* (UTF-16),
    // handled by ScanfReader.Read(char*). swscanf wraps its wide source string in
    // a StringReader, exactly as sscanf does for a byte source.
    // ---------------------------------------------------------------------

    /// <summary><c>wscanf(fmt, …)</c> ≡ <c>fwscanf(stdin, fmt, …)</c>.</summary>
    public static ScanfReader wscanf(char* fmt) => fwscanf(stdin, fmt);

    /// <summary><c>fwscanf(stream, fmt, …)</c> — wide formatted input from a stream.</summary>
    public static ScanfReader fwscanf(FILE* stream, char* fmt) => new ScanfReader(ReaderFor(stream), WideFmtToUtf8(fmt));

    /// <summary><c>swscanf(src, fmt, …)</c> — wide formatted input from the wide
    /// string <paramref name="src"/> (wrapped in a reader, like sscanf).</summary>
    public static ScanfReader swscanf(char* src, char* fmt) =>
        new ScanfReader(new StringReader(new string(src, 0, wcslen(src))), WideFmtToUtf8(fmt));
}
