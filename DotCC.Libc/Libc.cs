#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// libc-shaped runtime surface for dotcc-emitted programs. Every function
/// routes to a .NET BCL primitive. Names mirror C verbatim (lower-case) so
/// the emitter can pass identifiers through untouched once the integration
/// lands; capitalised aliases (<see cref="Malloc"/>, <see cref="Free"/>,
/// <see cref="Printf"/>) exist for callers that prefer .NET casing.
/// </summary>
/// <remarks>
/// Unsafe-by-design — every entry takes/returns raw pointers because that's
/// what real C code passes around. The library never allocates a GC object on
/// any hot path (Printf's <see cref="PrintfBuilder"/> is a ref struct,
/// <see cref="L"/> pins RVA data, strlen/strcmp/memset are bare loops).
/// </remarks>
public static unsafe partial class Libc
{
    // ---------------------------------------------------------------------
    // Memory: malloc / free
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>malloc(size)</c> — allocate <paramref name="size"/> bytes from the
    /// process heap. Backed by <see cref="NativeMemory.Alloc(nuint)"/>.
    /// Returns <c>null</c> only on OOM (the underlying syscall throws an
    /// <see cref="OutOfMemoryException"/> instead — match real C by catching
    /// in user code if needed).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* malloc(int size) => _dbgHeap ? DbgAlloc((nuint)size, false) : NativeMemory.Alloc((nuint)size);

    /// <inheritdoc cref="malloc(int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* Malloc(int size) => malloc(size);

    /// <summary>
    /// <c>free(p)</c> — return memory previously returned from
    /// <see cref="malloc"/> to the heap. <c>free(NULL)</c> is a no-op.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void free(void* p) { if (_dbgHeap) { DbgFree(p); } else { NativeMemory.Free(p); } }

    // ---------------------------------------------------------------------
    // Debug heap — a minimal AddressSanitizer for the native heap. Opt-in two
    // ways, inert otherwise: compile with `-fsanitize=address` (the emitted
    // shell calls EnableDebugHeap() before main), or set DOTCC_DEBUG_HEAP=1 at
    // runtime (the no-recompile override). A self-describing block:
    // [magic|size] header keeps the user pointer 32-aligned, and a trailing
    // canary catches a write past the allocation. `free` validates both —
    // flagging a bad/double free or an overflow with a managed stack trace at
    // the offending call. Useful because dotcc's allocations and a guest
    // runtime's own heap (e.g. chibi-scheme's GC arena) share this one process
    // heap, so a single out-of-bounds write or invalid free anywhere corrupts
    // everything downstream; the checked layout localizes it to the call site.
    // ---------------------------------------------------------------------

    /// <summary>When true, malloc/calloc/realloc/free route through the checked
    /// block layout below. Turned on by compiling with <c>-fsanitize=address</c>
    /// (the emitted shell calls <see cref="EnableDebugHeap"/> at startup) or by
    /// setting <c>DOTCC_DEBUG_HEAP=1</c> at runtime (seeded here — the override
    /// for an already-built program). Read on every malloc/free; the false path
    /// is a single branch over the normal <see cref="NativeMemory"/> route.</summary>
    internal static bool _dbgHeap =
        System.Environment.GetEnvironmentVariable("DOTCC_DEBUG_HEAP") == "1";

    /// <summary>Route subsequent malloc/calloc/realloc/free through the checked
    /// debug heap. The emitted program shell calls this once at startup when the
    /// program was compiled with <c>-fsanitize=address</c>. Must run before the
    /// first allocation: a block taken from the plain heap carries no header or
    /// canary, so the checked <c>free</c> would (correctly) reject it as an
    /// invalid free.</summary>
    public static void EnableDebugHeap() => _dbgHeap = true;
    /// <summary>When also set, every alloc/free scans EVERY live block's redzone,
    /// so an overflow is caught even on a block that is never freed (chibi's GC
    /// heap / context object) — at the next allocation, not the offending write.</summary>
    private static readonly bool _dbgScan =
        System.Environment.GetEnvironmentVariable("DOTCC_DEBUG_HEAP_SCAN") == "1";
    private const nuint _dbgHdr = 32;                       // 32-aligned user ptr
    private const nuint _dbgRed = 32;                       // trailing redzone
    private const ulong _dbgMagic = 0xD07CCA11ABCDEF01UL;
    private const byte _dbgCanary = 0xAB;
    // Live blocks are threaded through a singly-linked list in their headers
    // (no managed collection — embed/AOT clean): header = [magic|size|next|pad].
    private static byte* _dbgHead = null;
    private static readonly object _dbgLock = new();

    /// <summary>Allocate a checked block and return the user pointer.</summary>
    internal static void* DbgAlloc(nuint size, bool zero)
    {
        if (_dbgScan) { DbgScanAll("alloc"); }
        var basep = (byte*)NativeMemory.AlignedAlloc(_dbgHdr + size + _dbgRed, 32);
        *(ulong*)basep = _dbgMagic;
        *(nuint*)(basep + 8) = size;
        var user = basep + _dbgHdr;
        if (zero) { NativeMemory.Fill(user, size, 0); }
        NativeMemory.Fill(user + size, _dbgRed, _dbgCanary);   // arm the redzone
        if (_dbgScan)
        {
            lock (_dbgLock) { *(byte**)(basep + 16) = _dbgHead; _dbgHead = basep; }
        }
        return user;
    }

    /// <summary>Walk every live block and report the first whose trailing canary
    /// has been overwritten — catching an overflow into a block that is never
    /// freed. The trace points at the alloc/free where the scan ran (the size of
    /// the corrupted block narrows down the writer).</summary>
    private static void DbgScanAll(string where)
    {
        lock (_dbgLock)
        {
            for (var h = _dbgHead; h != null; h = *(byte**)(h + 16))
            {
                if (*(ulong*)h != _dbgMagic)
                {
                    System.Console.Error.WriteLine(
                        $"[dotcc debug-heap] {where}-scan: corrupt header at 0x{(nuint)(h + _dbgHdr):x} (magic=0x{*(ulong*)h:x})\n{System.Environment.StackTrace}");
                    return;
                }
                var size = *(nuint*)(h + 8);
                var red = h + _dbgHdr + size;
                for (nuint i = 0; i < _dbgRed; i++)
                {
                    if (red[i] != _dbgCanary)
                    {
                        System.Console.Error.WriteLine(
                            $"[dotcc debug-heap] {where}-scan: write past {size}-byte block 0x{(nuint)(h + _dbgHdr):x} (redzone[{i}]=0x{red[i]:x2})\n{System.Environment.StackTrace}");
                        return;
                    }
                }
            }
        }
    }

    /// <summary>Unlink a block from the live-list (scan mode).</summary>
    private static void DbgUnlink(byte* basep)
    {
        lock (_dbgLock)
        {
            if (_dbgHead == basep) { _dbgHead = *(byte**)(basep + 16); return; }
            for (var h = _dbgHead; h != null; h = *(byte**)(h + 16))
            {
                if (*(byte**)(h + 16) == basep) { *(byte**)(h + 16) = *(byte**)(basep + 16); return; }
            }
        }
    }

    /// <summary>Validate a user pointer's header magic and trailing canary,
    /// reporting (with a stack trace) a bad/double free or an overflow.</summary>
    private static nuint DbgValidate(byte* user, string where, out bool ok)
    {
        var basep = user - _dbgHdr;
        var magic = *(ulong*)basep;
        if (magic != _dbgMagic)
        {
            System.Console.Error.WriteLine(
                $"[dotcc debug-heap] {where}: invalid or double free of 0x{(nuint)user:x} (magic=0x{magic:x})\n{System.Environment.StackTrace}");
            ok = false;
            return 0;
        }
        var size = *(nuint*)(basep + 8);
        var red = user + size;
        for (nuint i = 0; i < _dbgRed; i++)
        {
            if (red[i] != _dbgCanary)
            {
                System.Console.Error.WriteLine(
                    $"[dotcc debug-heap] {where}: write past end of {size}-byte block 0x{(nuint)user:x} (redzone[{i}]=0x{red[i]:x2})\n{System.Environment.StackTrace}");
                break;
            }
        }
        ok = true;
        return size;
    }

    /// <summary>Checked counterpart of <see cref="free(void*)"/>.</summary>
    internal static void DbgFree(void* p)
    {
        if (p == null) { return; }
        if (_dbgScan) { DbgScanAll("free"); }
        DbgValidate((byte*)p, "free", out var ok);
        if (!ok) { return; }                       // leak rather than free a bad ptr
        var basep = (byte*)p - _dbgHdr;
        if (_dbgScan) { DbgUnlink(basep); }
        *(ulong*)basep = 0xDEADBEEFDEADBEEFUL;     // poison so a double free is caught
        NativeMemory.AlignedFree(basep);
    }

    /// <summary>Checked counterpart of <c>realloc</c>.</summary>
    internal static void* DbgRealloc(void* p, nuint newSize)
    {
        if (p == null) { return DbgAlloc(newSize, false); }
        var old = DbgValidate((byte*)p, "realloc", out var ok);
        if (!ok) { return DbgAlloc(newSize, false); }
        var np = DbgAlloc(newSize, false);
        Buffer.MemoryCopy(p, np, newSize, old < newSize ? old : newSize);
        DbgFree(p);
        return np;
    }

    /// <inheritdoc cref="free(void*)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Free(void* p) => free(p);

    /// <summary>
    /// <c>memset(dst, value, count)</c> — fill <paramref name="count"/>
    /// bytes at <paramref name="dst"/> with the low byte of
    /// <paramref name="value"/>. Routes to
    /// <see cref="NativeMemory.Fill(void*, nuint, byte)"/>. Returns
    /// <paramref name="dst"/> (matches C signature).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* memset(void* dst, int value, int count)
    {
        NativeMemory.Fill(dst, (nuint)count, (byte)value);
        return dst;
    }

    /// <summary>
    /// <c>memcpy(dst, src, count)</c> — copy <paramref name="count"/> bytes
    /// from <paramref name="src"/> to <paramref name="dst"/>. Routes to
    /// <see cref="Buffer.MemoryCopy(void*, void*, long, long)"/>. Returns
    /// <paramref name="dst"/> (matches C signature).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* memcpy(void* dst, void* src, int count)
    {
        Buffer.MemoryCopy(src, dst, count, count);
        return dst;
    }

    // ---------------------------------------------------------------------
    // String → number conversions (<stdlib.h>)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>strtod(nptr, endptr)</c> — parse a leading floating-point number from
    /// the C string <paramref name="nptr"/> (skipping leading whitespace), set
    /// <paramref name="endptr"/> (if non-null) to the first unconsumed byte, and
    /// return the value. Handles optional sign, decimal point, <c>e</c>/<c>E</c>
    /// exponent, and C99 <c>inf</c>/<c>infinity</c>/<c>nan</c> (case-insensitive).
    /// No conversion → returns 0 and leaves <c>*endptr == nptr</c>, per C.
    /// </summary>
    public static double strtod(byte* nptr, byte** endptr)
    {
        byte* p = nptr;
        // Skip leading whitespace (C "C"-locale isspace: ' ' \t \n \v \f \r).
        while (*p == (byte)' ' || (*p >= 9 && *p <= 13)) { p++; }
        byte* start = p;

        bool neg = false;
        if (*p == (byte)'+' || *p == (byte)'-') { neg = *p == (byte)'-'; p++; }

        // C99 hex float: 0x<hex> [. <hex>] p[+-]<dec> — delegated to
        // dotcc's own parser because .NET's double.Parse rejects 0x…p…
        if (*p == (byte)'0' && (p[1] == (byte)'x' || p[1] == (byte)'X'))
        {
            p += 2; // skip 0x
            // Integer part: required hex digits.
            byte* afterInt = p;
            while ((*afterInt >= (byte)'0' && *afterInt <= (byte)'9')
                || (*afterInt >= (byte)'a' && *afterInt <= (byte)'f')
                || (*afterInt >= (byte)'A' && *afterInt <= (byte)'F'))
            { afterInt++; }
            if (afterInt == p && *afterInt != (byte)'.')
            {
                // No hex digits at all — not a valid hex float, fall through
                // to the decimal path (which will fail on the 'x').
                goto decimal_path;
            }
            // Parse hex digits into a double significand. Cap the accumulator
            // when it reaches ~1e250 to avoid overflow for very long digit
            // runs (e.g. "0x3." + 1000 zeros; 3 × 16^1000 >> DBL_MAX).
            // Beyond the cap we count the skipped digits and fold them into
            // the binary exponent — they contribute nothing to the mantissa.
            double sig = 0.0; int fracHex = 0; int skipped = 0; bool sawHex = false;
            const double SigCap = 1e250;
            while (p < afterInt)
            {
                if (sig < SigCap)
                    sig = sig * 16.0 + HexVal(*p);
                else skipped++;
                p++; sawHex = true;
            }
            // Optional fractional part.
            if (*p == (byte)'.')
            {
                p++;
                while ((*p >= (byte)'0' && *p <= (byte)'9')
                    || (*p >= (byte)'a' && *p <= (byte)'f')
                    || (*p >= (byte)'A' && *p <= (byte)'F'))
                {
                    if (sig < SigCap)
                        sig = sig * 16.0 + HexVal(*p);
                    else skipped++;
                    p++; fracHex++; sawHex = true;
                }
            }
            if (!sawHex)
            {
                // No hex digits at all (e.g. "0x.") — not a valid number.
                if (endptr != null) { *endptr = nptr; }
                return 0.0;
            }
            // Binary exponent (C99 requires 'p'; Lua accepts hex floats without it).
            // If 'p' is present but not followed by a digit (or +/- digit), the
            // parse stops *before* the 'p' (C11 7.22.1.3: the exponent must have
            // at least one digit).
            int exp = 0;
            if (*p == (byte)'p' || *p == (byte)'P')
            {
                byte* expStart = p;
                p++;
                bool expNeg = false;
                if (*p == (byte)'+' || *p == (byte)'-') { expNeg = *p == (byte)'-'; p++; }
                if (*p < (byte)'0' || *p > (byte)'9')
                {
                    // No exponent digits — back up; the hex float ends before 'p'.
                    p = expStart;
                }
                else
                {
                    while (*p >= (byte)'0' && *p <= (byte)'9')
                    {
                        exp = exp * 10 + (*p - (byte)'0'); p++;
                    }
                    if (expNeg) exp = -exp;
                }
            }
            exp -= 4 * fracHex; // each fractional hex digit shifts 4 bits
            exp += 4 * skipped;  // cap-skipped digits: restore the exponent
            // Compute: sig * 2^exp. ScaleB handles the power-of-two multiply
            // and produces the correctly-rounded double result.
            double hexVal = double.IsNaN(sig) ? sig : System.Math.ScaleB(sig, exp);
            if (endptr != null) { *endptr = p; }
            return neg ? -hexVal : hexVal;
        }
        decimal_path:

        if (MatchCI(p, "inf"))
        {
            p += 3;
            if (MatchCI(p, "inity")) { p += 5; }
            if (endptr != null) { *endptr = p; }
            return neg ? double.NegativeInfinity : double.PositiveInfinity;
        }
        if (MatchCI(p, "nan"))
        {
            p += 3;
            if (*p == (byte)'(')   // optional (n-char-sequence)
            {
                while (*p != 0 && *p != (byte)')') { p++; }
                if (*p == (byte)')') { p++; }
            }
            if (endptr != null) { *endptr = p; }
            return double.NaN;
        }

        // Decimal mantissa: digits, optional '.', digits.
        bool sawDigit = false;
        while (*p >= (byte)'0' && *p <= (byte)'9') { p++; sawDigit = true; }
        if (*p == (byte)'.')
        {
            p++;
            while (*p >= (byte)'0' && *p <= (byte)'9') { p++; sawDigit = true; }
        }
        // Optional exponent — only consumed if followed by digits.
        if (sawDigit && (*p == (byte)'e' || *p == (byte)'E'))
        {
            byte* e = p + 1;
            if (*e == (byte)'+' || *e == (byte)'-') { e++; }
            if (*e >= (byte)'0' && *e <= (byte)'9')
            {
                p = e;
                while (*p >= (byte)'0' && *p <= (byte)'9') { p++; }
            }
        }

        if (!sawDigit)
        {
            if (endptr != null) { *endptr = nptr; }   // no conversion
            return 0.0;
        }

        var s = Encoding.ASCII.GetString(start, (int)(p - start));
        double val = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (endptr != null) { *endptr = p; }
        return val;
    }

    /// <summary>
    /// <c>atof(nptr)</c> — <c>strtod(nptr, NULL)</c>: parse a leading double,
    /// ignoring where it ends. No error reporting (matches C).
    /// </summary>
    public static double atof(byte* nptr) => strtod(nptr, null);

    /// <summary>Hex digit value (0-15).</summary>
    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => 0,
    };

    /// <summary>Case-insensitive ASCII prefix match of a lowercase literal.</summary>
    private static bool MatchCI(byte* p, string lower)
    {
        for (int i = 0; i < lower.Length; i++)
        {
            byte b = p[i];
            if (b == 0 || (b | 0x20) != lower[i]) { return false; }
        }
        return true;
    }

    // ---------------------------------------------------------------------
    // C-string ops over byte* (NUL-terminated UTF-8)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>strlen(s)</c> — bytes before the first <c>0</c> in
    /// <paramref name="s"/>. UB if <paramref name="s"/> is null (matches C).
    /// </summary>
    public static int strlen(byte* s)
    {
        int n = 0;
        while (s[n] != 0) { n++; }
        return n;
    }

    /// <summary>
    /// <c>strcmp(a, b)</c> — lexicographic byte-by-byte compare. Returns
    /// 0 / negative / positive. Real C returns <c>int</c>; we narrow the
    /// byte difference back to int.
    /// </summary>
    public static int strcmp(byte* a, byte* b)
    {
        while (*a != 0 && *a == *b) { a++; b++; }
        return *a - *b;
    }

    /// <summary>
    /// <c>strcoll(a, b)</c> — locale-aware string compare. dotcc runs the
    /// <c>"C"</c> locale (see <see cref="LocaleLib"/>), where collation order is
    /// plain byte order, so this is exactly <see cref="strcmp(byte*, byte*)"/>.
    /// </summary>
    public static int strcoll(byte* a, byte* b) => strcmp(a, b);

    /// <summary>
    /// POSIX <c>strcasecmp(a, b)</c> — <see cref="strcmp(byte*, byte*)"/> with
    /// ASCII case folded (the C-locale behavior; bytes ≥ 0x80 compare raw).
    /// </summary>
    public static int strcasecmp(byte* a, byte* b)
    {
        while (*a != 0 && Lower(*a) == Lower(*b)) { a++; b++; }
        return Lower(*a) - Lower(*b);
    }

    /// <summary>POSIX <c>strncasecmp(a, b, n)</c> — at most <paramref name="n"/>
    /// bytes of <see cref="strcasecmp"/>.</summary>
    public static int strncasecmp(byte* a, byte* b, ulong n)
    {
        for (; n > 0; n--, a++, b++)
        {
            var d = Lower(*a) - Lower(*b);
            if (d != 0 || *a == 0) { return d; }
        }
        return 0;
    }

    private static int Lower(byte c) => c >= 'A' && c <= 'Z' ? c + 32 : c;

    /// <summary>
    /// <c>strcpy(dst, src)</c> — copy NUL-terminated <paramref name="src"/>
    /// into <paramref name="dst"/>, including the terminating <c>0</c>.
    /// Returns <paramref name="dst"/>.
    /// </summary>
    public static byte* strcpy(byte* dst, byte* src)
    {
        byte* p = dst;
        while ((*p++ = *src++) != 0) { }
        return dst;
    }

    // ---------------------------------------------------------------------
    // I/O — streams, fprintf, printf, puts, fputs
    //
    // A C `FILE*` is a genuine pointer to an opaque `FILE` struct (see
    // FileLib.cs); `stdin`/`stdout`/`stderr` and `fopen` all hand back a
    // `FILE*`. `fprintf` is the underlying primitive; `printf` is
    // `fprintf(stdout, ...)`. `puts` is `fputs(s, stdout)` plus a newline.
    // The formatted-I/O builders bind a TextWriter/TextReader, which the FILE
    // supplies (WriterFor/ReaderFor) — so PrintfBuilder/ScanfReader are
    // FILE-agnostic. This mirrors how the actual C standard library factors
    // them. (stdin/stdout/stderr live in FileLib.cs alongside the FILE type.)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>fprintf(stream, fmt)</c> — the primitive. Start a fluent format
    /// chain that writes to <paramref name="stream"/>. Each
    /// <see cref="PrintfBuilder.Arg(int)"/> / <see cref="PrintfBuilder.Arg(double)"/>
    /// / <see cref="PrintfBuilder.Arg(byte*)"/> consumes the next <c>%</c>
    /// spec. Finish with <see cref="PrintfBuilder.Done()"/> which returns
    /// the printf-style <c>int</c>.
    /// </summary>
    /// <remarks>
    /// Fluent rather than <c>params object[]</c> because raw pointers can't
    /// be boxed. The builder is a <c>ref struct</c> so it stays stack-only
    /// and zero-alloc. The <c>FILE*</c> resolves to the right text sink via
    /// <see cref="WriterFor"/> (console → Console.Out/Error, resolved live so
    /// redirection holds; file → an unbuffered UTF-8 writer over the stream).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PrintfBuilder fprintf(FILE* stream, byte* fmt) => new(WriterFor(stream), fmt);

    /// <summary><c>printf(fmt)</c> ≡ <c>fprintf(stdout, fmt)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PrintfBuilder printf(byte* fmt) => fprintf(stdout, fmt);

    /// <inheritdoc cref="printf(byte*)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PrintfBuilder Printf(byte* fmt) => printf(fmt);

    /// <summary>
    /// <c>fputs(s, stream)</c> — write NUL-terminated UTF-8
    /// <paramref name="s"/> to <paramref name="stream"/>. No newline (per
    /// C standard — only <c>puts</c> appends one). Returns a non-negative
    /// byte count on success.
    /// </summary>
    public static int fputs(byte* s, FILE* stream)
    {
        var w = WriterFor(stream);
        if (s == null)
        {
            w.Write("(null)");
            return 6;
        }
        int len = strlen(s);
        // For a file-backed stream WriterFor returns an unbuffered UTF-8
        // writer, so decode→write round-trips back to the original bytes.
        w.Write(System.Text.Encoding.UTF8.GetString(s, len));
        return len;
    }

    /// <summary>
    /// <c>puts(s)</c> ≡ <c>fputs(s, stdout)</c> + a <c>'\n'</c>. Returns a
    /// non-negative byte count on success (real C's contract).
    /// </summary>
    public static int puts(byte* s)
    {
        int n = fputs(s, stdout);
        WriteByteTo(stdout, (byte)'\n');
        return n + 1;
    }

    /// <summary>
    /// <c>sprintf(dst, fmt, ...)</c> — format into <paramref name="dst"/>,
    /// NUL-terminated. Returns the number of bytes that <em>would</em> have
    /// been written (excl. NUL) — caller is responsible for sizing
    /// <paramref name="dst"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SprintfBuilder sprintf(byte* dst, byte* fmt) => new(dst, fmt, capacity: -1);

    /// <summary>
    /// <c>snprintf(dst, n, fmt, ...)</c> — like <see cref="sprintf"/> but
    /// caps the copy at <paramref name="n"/> bytes (inclusive of the
    /// terminating NUL — i.e. at most <c>n-1</c> formatted bytes plus NUL).
    /// Return value is the count that would have been written even when
    /// truncated, matching C99.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SprintfBuilder snprintf(byte* dst, int n, byte* fmt) => new(dst, fmt, capacity: Math.Max(0, n - 1));

    /// <summary>
    /// <c>fscanf(stream, fmt)</c> — start a fluent scan chain reading from
    /// <paramref name="stream"/>. Each <see cref="ScanfReader.Read(int*)"/>
    /// / <see cref="ScanfReader.Read(double*)"/> /
    /// <see cref="ScanfReader.Read(byte*)"/> consumes the next <c>%</c>
    /// spec and writes the parsed value. <see cref="ScanfReader.Done"/>
    /// returns the count of successful matches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ScanfReader fscanf(FILE* stream, byte* fmt) => new(ReaderFor(stream), fmt);

    /// <summary><c>scanf(fmt)</c> ≡ <c>fscanf(stdin, fmt)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ScanfReader scanf(byte* fmt) => fscanf(stdin, fmt);

    /// <summary>
    /// <c>sscanf(src, fmt)</c> — parse from the NUL-terminated UTF-8
    /// byte buffer <paramref name="src"/> instead of a stream. Equivalent
    /// to wrapping <paramref name="src"/> in a <see cref="StringReader"/>
    /// and calling <see cref="fscanf"/>.
    /// </summary>
    public static ScanfReader sscanf(byte* src, byte* fmt)
    {
        int len = strlen(src);
        var s = System.Text.Encoding.UTF8.GetString(src, len);
        return new ScanfReader(new StringReader(s), fmt);
    }

    // ---------------------------------------------------------------------
    // String literal lowering
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>L(u8)</c> — pin a UTF-8 RVA byte literal's address and return it
    /// as <c>byte*</c>. Used by the emitter to lower C string literals
    /// (<c>"foo"</c>) to a NUL-terminated pointer into the assembly's
    /// read-only data section. No GC pinning required — RVA data lives at a
    /// fixed address for program lifetime.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* L(ReadOnlySpan<byte> u8) =>
        (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(u8));

    /// <summary>
    /// <c>L&lt;T&gt;(data)</c> — the multi-byte sibling of <see cref="L(ReadOnlySpan{byte})"/>:
    /// return a constant RVA array literal's address as <c>T*</c>. Used by the
    /// emitter to lower a read-only (<c>const</c>) non-byte array
    /// (<c>const int tab[] = {…}</c>) to a pointer into the assembly's read-only
    /// data section. Roslyn folds a <c>ReadOnlySpan&lt;T&gt;</c> over an
    /// all-constant array to a fixed RVA blob (no allocation, no GC pin, program
    /// lifetime). LP64 little-endian only — dotcc's target.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T* L<T>(ReadOnlySpan<T> data) where T : unmanaged =>
        (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(data));

    // char16_t (UTF-16) string-literal pool. C# has no `u16` RVA literal (only the
    // byte `u8` form), so a u"…" literal can't ride the zero-copy L(...) path.
    // Instead pin its UTF-16 data on the Pinned Object Heap ONCE, cached per
    // distinct literal — a program-lifetime pool, mirroring how C places string
    // literals in .rodata. Pinning per use would leak a frame under a loop.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> _u16Literals =
        new(StringComparer.Ordinal);

    /// <summary><c>L16("…")</c> — return a stable <c>char*</c> (= char16_t*) to a
    /// pinned, NUL-terminated copy of a C11 <c>u"…"</c> literal's UTF-16 data. The
    /// argument is a C# string literal that already includes its trailing NUL char;
    /// each distinct literal is pinned once on the POH and rooted for the program's
    /// life (so the pointer never moves), exactly like <see cref="GlobalArrayFrom{T}"/>.</summary>
    public static char* L16(string s)
    {
        if (_u16Literals.TryGetValue(s, out var hit)) { return (char*)hit; }   // lock-free hot path
        // Miss: pin a copy and publish atomically. Two threads racing the same new
        // literal both pin; only the winner roots its array (so the pooled pointer
        // stays valid for the program's life). The loser's array is unreferenced once
        // we return — pinning prevents movement, not collection, so the GC reclaims
        // it; nothing leaks.
        var arr = GC.AllocateUninitializedArray<char>(s.Length, pinned: true);
        s.CopyTo(arr);
        var ptr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(arr));
        var actual = _u16Literals.GetOrAdd(s, ptr);
        if (actual == ptr) { lock (_globalArrayRoots) { _globalArrayRoots.Add(arr); } }
        return (char*)actual;
    }

    // char32_t (UTF-32) string-literal pool — the 32-bit sibling of the L16 pool.
    // C# has no `u32` RVA literal, so a U"…" literal can't ride the zero-copy L(...)
    // path either; pin its UTF-32 data on the POH ONCE, cached per distinct literal.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> _u32Literals =
        new(StringComparer.Ordinal);

    /// <summary><c>L32("…")</c> — return a stable <c>uint*</c> (= char32_t*) to a
    /// pinned, NUL-terminated copy of a C11 <c>U"…"</c> literal's UTF-32 data. The
    /// argument is a C# (UTF-16) string literal that already includes its trailing NUL
    /// char; <see cref="System.Text.Encoding.UTF32"/> folds it — surrogate pairs and
    /// all — into little-endian 4-byte code units (dotcc is LP64/LE, so the encoded
    /// byte stream IS the <c>uint</c> array). Each distinct literal is pinned once on
    /// the POH and rooted for the program's life, exactly like <see cref="L16"/>.</summary>
    public static uint* L32(string s)
    {
        if (_u32Literals.TryGetValue(s, out var hit)) { return (uint*)hit; }   // lock-free hot path
        // Reuse the BCL's UTF-16 → UTF-32 fold: GetBytes yields LE 4-byte scalars,
        // no BOM, and encodes the trailing '\0' as a zero code unit (the terminator).
        var bytes = System.Text.Encoding.UTF32.GetBytes(s);
        var arr = GC.AllocateUninitializedArray<uint>(bytes.Length / 4, pinned: true);
        System.Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        var ptr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(arr));
        var actual = _u32Literals.GetOrAdd(s, ptr);
        if (actual == ptr) { lock (_globalArrayRoots) { _globalArrayRoots.Add(arr); } }
        return (uint*)actual;
    }

    // ── <signal.h> ─────────────────────────────────────────────────────

    private static unsafe delegate*<int, void> _sigintHandler;

    /// <summary><c>signal(sig, func)</c> — register a signal handler.
    /// Only SIGINT (2) is supported; uses .NET
    /// <see cref="Console.CancelKeyPress"/>.</summary>
    public static unsafe delegate*<int, void> signal(
        int sig, delegate*<int, void> func)
    {
        if (sig != 2) return null;
        var prev = _sigintHandler;
        if (func == null) { _sigintHandler = null; Console.CancelKeyPress -= OnSigint; }
        else { _sigintHandler = func; Console.CancelKeyPress += OnSigint; }
        return prev;
    }
    private static unsafe void OnSigint(object? sender, ConsoleCancelEventArgs e)
    {
        if (_sigintHandler == null) return;
        _sigintHandler(2);
        e.Cancel = true;
    }
}
