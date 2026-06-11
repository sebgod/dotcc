#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;stdlib.h&gt;</c> surface beyond the four primitives already in
/// <see cref="Libc"/> proper (<c>malloc</c> / <c>free</c> + <c>strtod</c> /
/// <c>atof</c>): integer string-conversion, integer arithmetic helpers,
/// allocation extras, pseudo-random numbers, environment access, program
/// control, and generic <c>qsort</c> / <c>bsearch</c>.
/// </summary>
/// <remarks>
/// The <c>div_t</c> / <c>ldiv_t</c> / <c>lldiv_t</c> result structs are
/// pre-registered as known type names (<c>Compiler.PredefinedTypeNames</c>) so
/// emitted user code can spell them without a typedef. <c>qsort</c> /
/// <c>bsearch</c> take a C# function pointer (<c>delegate*</c>) for the
/// comparator — AOT-clean, no delegate allocation — which dotcc produces by
/// decaying a bare comparator name to <c>&amp;cmp</c> at the call site.
/// </remarks>
public static unsafe partial class Libc
{
    // div/ldiv/lldiv result structs. dotcc maps C `long` and `long long` both
    // to C# `long` (LP64), so ldiv_t and lldiv_t share a shape but stay
    // distinct named types so user code's spelling resolves.

    /// <summary><c>div_t</c> — quotient/remainder pair for <see cref="div"/>.</summary>
    public struct div_t { public int quot; public int rem; }

    /// <summary><c>ldiv_t</c> — quotient/remainder pair for <see cref="ldiv"/>.</summary>
    public struct ldiv_t { public long quot; public long rem; }

    /// <summary><c>lldiv_t</c> — quotient/remainder pair for <see cref="lldiv"/>.</summary>
    public struct lldiv_t { public long quot; public long rem; }

    // ---------------------------------------------------------------------
    // String → integer conversions
    // ---------------------------------------------------------------------

    private static int DigitVal(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9') { return b - '0'; }
        if (b >= (byte)'a' && b <= (byte)'z') { return b - 'a' + 10; }
        if (b >= (byte)'A' && b <= (byte)'Z') { return b - 'A' + 10; }
        return -1;
    }

    /// <summary>Skip leading C-locale whitespace, then resolve the effective
    /// base: <paramref name="base"/> 0 auto-detects (<c>0x</c>→16, leading
    /// <c>0</c>→8, else 10), and base 16 tolerates an optional <c>0x</c> prefix.
    /// Advances <paramref name="p"/> past any consumed prefix.</summary>
    private static int NormalizeBase(ref byte* p, int @base)
    {
        while (*p == (byte)' ' || (*p >= 9 && *p <= 13)) { p++; }
        // The sign is parsed by the caller before this; here p sits at the
        // first magnitude byte.
        if (@base == 0)
        {
            if (*p == (byte)'0')
            {
                if (p[1] == (byte)'x' || p[1] == (byte)'X') { p += 2; return 16; }
                return 8;   // leading '0' is consumed later as octal digit 0
            }
            return 10;
        }
        if (@base == 16 && *p == (byte)'0' && (p[1] == (byte)'x' || p[1] == (byte)'X')) { p += 2; }
        return @base;
    }

    /// <summary>Signed 64-bit core shared by strtol/strtoll (dotcc's
    /// <c>long</c> == <c>long long</c> == 64-bit). Clamps to LONG_MIN/MAX and
    /// sets <see cref="errno"/> to ERANGE on overflow.</summary>
    private static long StrtollCore(byte* nptr, byte** endptr, int @base)
    {
        byte* p = nptr;
        while (*p == (byte)' ' || (*p >= 9 && *p <= 13)) { p++; }
        bool neg = false;
        if (*p == (byte)'+' || *p == (byte)'-') { neg = *p == (byte)'-'; p++; }

        @base = NormalizeBase(ref p, @base);

        ulong cutoff = neg ? (ulong)long.MaxValue + 1UL : (ulong)long.MaxValue;
        ulong cutlim = cutoff % (ulong)@base;
        cutoff /= (ulong)@base;

        bool any = false, overflow = false;
        ulong acc = 0;
        for (; ; p++)
        {
            int d = DigitVal(*p);
            if (d < 0 || d >= @base) { break; }
            any = true;
            if (overflow) { continue; }
            if (acc > cutoff || (acc == cutoff && (ulong)d > cutlim)) { overflow = true; continue; }
            acc = acc * (ulong)@base + (ulong)d;
        }

        if (endptr != null) { *endptr = any ? p : nptr; }
        if (overflow) { errno = ERANGE; return neg ? long.MinValue : long.MaxValue; }
        return unchecked((long)(neg ? 0UL - acc : acc));
    }

    /// <summary>Unsigned 64-bit core shared by strtoul/strtoull. Clamps to
    /// ULONG_MAX and sets <see cref="errno"/> to ERANGE on overflow. A leading
    /// minus negates the result modulo 2^64 (per C).</summary>
    private static ulong StrtoullCore(byte* nptr, byte** endptr, int @base)
    {
        byte* p = nptr;
        while (*p == (byte)' ' || (*p >= 9 && *p <= 13)) { p++; }
        bool neg = false;
        if (*p == (byte)'+' || *p == (byte)'-') { neg = *p == (byte)'-'; p++; }

        @base = NormalizeBase(ref p, @base);

        ulong cutoff = ulong.MaxValue / (ulong)@base;
        ulong cutlim = ulong.MaxValue % (ulong)@base;

        bool any = false, overflow = false;
        ulong acc = 0;
        for (; ; p++)
        {
            int d = DigitVal(*p);
            if (d < 0 || d >= @base) { break; }
            any = true;
            if (overflow) { continue; }
            if (acc > cutoff || (acc == cutoff && (ulong)d > cutlim)) { overflow = true; continue; }
            acc = acc * (ulong)@base + (ulong)d;
        }

        if (endptr != null) { *endptr = any ? p : nptr; }
        if (overflow) { errno = ERANGE; return ulong.MaxValue; }
        return neg ? 0UL - acc : acc;
    }

    /// <summary><c>strtol(nptr, endptr, base)</c> — parse a signed long.</summary>
    public static long strtol(byte* nptr, byte** endptr, int @base) => StrtollCore(nptr, endptr, @base);

    /// <summary><c>strtoll(nptr, endptr, base)</c> (C99) — parse a signed long long
    /// (== <c>strtol</c> on dotcc's LP64 model).</summary>
    public static long strtoll(byte* nptr, byte** endptr, int @base) => StrtollCore(nptr, endptr, @base);

    /// <summary><c>strtoul(nptr, endptr, base)</c> — parse an unsigned long.</summary>
    public static ulong strtoul(byte* nptr, byte** endptr, int @base) => StrtoullCore(nptr, endptr, @base);

    /// <summary><c>strtoull(nptr, endptr, base)</c> (C99) — parse an unsigned long long.</summary>
    public static ulong strtoull(byte* nptr, byte** endptr, int @base) => StrtoullCore(nptr, endptr, @base);

    /// <summary><c>atoi(nptr)</c> ≡ <c>(int)strtol(nptr, NULL, 10)</c>.</summary>
    public static int atoi(byte* nptr) => unchecked((int)StrtollCore(nptr, null, 10));

    /// <summary><c>atol(nptr)</c> ≡ <c>strtol(nptr, NULL, 10)</c>.</summary>
    public static long atol(byte* nptr) => StrtollCore(nptr, null, 10);

    /// <summary><c>atoll(nptr)</c> (C99) ≡ <c>strtoll(nptr, NULL, 10)</c>.</summary>
    public static long atoll(byte* nptr) => StrtollCore(nptr, null, 10);

    // ---------------------------------------------------------------------
    // Integer arithmetic
    // ---------------------------------------------------------------------

    /// <summary><c>abs(n)</c> — absolute value. UB at INT_MIN (wraps, per C —
    /// no exception, unlike <see cref="Math.Abs(int)"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int abs(int n) => n < 0 ? unchecked(-n) : n;

    /// <summary><c>labs(n)</c> — absolute value of a long.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long labs(long n) => n < 0 ? unchecked(-n) : n;

    /// <summary><c>llabs(n)</c> (C99) — absolute value of a long long.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long llabs(long n) => n < 0 ? unchecked(-n) : n;

    /// <summary><c>div(num, den)</c> — quotient + remainder. Both truncate
    /// toward zero (C99), matching C#'s <c>/</c> and <c>%</c>.</summary>
    public static div_t div(int num, int den) => new div_t { quot = num / den, rem = num % den };

    /// <summary><c>ldiv(num, den)</c> — long quotient + remainder.</summary>
    public static ldiv_t ldiv(long num, long den) => new ldiv_t { quot = num / den, rem = num % den };

    /// <summary><c>lldiv(num, den)</c> (C99) — long long quotient + remainder.</summary>
    public static lldiv_t lldiv(long num, long den) => new lldiv_t { quot = num / den, rem = num % den };

    // ---------------------------------------------------------------------
    // Allocation extras (malloc / free live in Libc.cs)
    // ---------------------------------------------------------------------

    /// <summary><c>calloc(n, size)</c> — allocate <c>n * size</c> zero-filled
    /// bytes. Routes to <see cref="NativeMemory.AllocZeroed(nuint, nuint)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* calloc(int n, int size) =>
        _dbgHeap ? DbgAlloc((nuint)n * (nuint)size, true) : NativeMemory.AllocZeroed((nuint)n, (nuint)size);

    /// <summary><c>realloc(p, size)</c> — resize a prior allocation, preserving
    /// contents up to the smaller of old/new size. Routes to
    /// <see cref="NativeMemory.Realloc(void*, nuint)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* realloc(void* p, int size) =>
        _dbgHeap ? DbgRealloc(p, (nuint)size) : NativeMemory.Realloc(p, (nuint)size);

    // ---------------------------------------------------------------------
    // Pseudo-random numbers
    // ---------------------------------------------------------------------

    /// <summary><c>RAND_MAX</c> — the largest value <see cref="rand"/> returns.
    /// 32767, the C-mandated minimum (and MSVC's value) for portability.</summary>
    public const int RAND_MAX = 32767;

    [ThreadStatic]
    private static Random? _rand;

    /// <summary><c>rand()</c> — pseudo-random int in <c>[0, RAND_MAX]</c>. Uses a
    /// thread-local generator (reentrant; real C's <c>rand</c> shares one global
    /// state). Seeded as if <c>srand(1)</c> until <see cref="srand"/> is called,
    /// per C. Note: the .NET PRNG differs from any C library's, so sequences are
    /// not byte-compatible with gcc/MSVC — only the contract (range, determinism
    /// per seed) holds.</summary>
    public static int rand()
    {
        _rand ??= new Random(1);
        return _rand.Next(0, RAND_MAX + 1);
    }

    /// <summary><c>srand(seed)</c> — reseed <see cref="rand"/>. Takes a wide
    /// integer so any C integer argument (incl. <c>(unsigned)time(NULL)</c>)
    /// widens in without a cast; truncated to unsigned-int per C.</summary>
    public static void srand(long seed) => _rand = new Random(unchecked((int)(uint)seed));

    // ---------------------------------------------------------------------
    // Environment + program control
    // ---------------------------------------------------------------------

    [ThreadStatic] private static byte* _envBuf;
    [ThreadStatic] private static int _envCap;

    /// <summary>Copy a managed string into a thread-local native buffer as
    /// NUL-terminated UTF-8 and return a pointer to it. The buffer is reused
    /// across calls (matching C's "may be overwritten by a later call"
    /// contract for <c>getenv</c>).</summary>
    private static byte* StashCString(string? s)
    {
        if (s is null) { return null; }
        int need = Encoding.UTF8.GetByteCount(s) + 1;
        if (_envBuf == null || _envCap < need)
        {
            if (_envBuf != null) { NativeMemory.Free(_envBuf); }
            _envCap = need;
            _envBuf = (byte*)NativeMemory.Alloc((nuint)need);
        }
        int n = Encoding.UTF8.GetBytes(s, new Span<byte>(_envBuf, _envCap));
        _envBuf[n] = 0;
        return _envBuf;
    }

    /// <summary><c>getenv(name)</c> — look up an environment variable; returns a
    /// pointer to its value (in a reused thread-local buffer) or <c>null</c> if
    /// unset. Routes to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public static byte* getenv(byte* name)
    {
        var key = Encoding.UTF8.GetString(name, strlen(name));
        return StashCString(Environment.GetEnvironmentVariable(key));
    }

    /// <summary>POSIX <c>setenv(name, value, overwrite)</c> — add or change an
    /// environment variable; returns 0 on success, -1 (EINVAL) if <paramref
    /// name="name"/> is null/empty or contains <c>'='</c>. When <paramref
    /// name="overwrite"/> is 0 and the name already exists the value is left
    /// untouched. Routes to <see cref="Environment.SetEnvironmentVariable(string,string)"/>
    /// (chibi's <c>(chibi ast)</c> env-cell support uses it).</summary>
    public static int setenv(byte* name, byte* value, int overwrite)
    {
        if (name == null || *name == 0) { errno = EINVAL; return -1; }
        var key = Encoding.UTF8.GetString(name, strlen(name));
        if (key.IndexOf('=') >= 0) { errno = EINVAL; return -1; }
        if (overwrite == 0 && Environment.GetEnvironmentVariable(key) != null) { return 0; }
        Environment.SetEnvironmentVariable(key, value == null ? string.Empty : Encoding.UTF8.GetString(value, strlen(value)));
        return 0;
    }

    /// <summary>POSIX <c>unsetenv(name)</c> — remove an environment variable;
    /// returns 0 on success, -1 (EINVAL) if <paramref name="name"/> is null/empty
    /// or contains <c>'='</c>. Removing an unset name is not an error. Routes to
    /// <see cref="Environment.SetEnvironmentVariable(string,string)"/> with a null
    /// value.</summary>
    public static int unsetenv(byte* name)
    {
        if (name == null || *name == 0) { errno = EINVAL; return -1; }
        var key = Encoding.UTF8.GetString(name, strlen(name));
        if (key.IndexOf('=') >= 0) { errno = EINVAL; return -1; }
        Environment.SetEnvironmentVariable(key, null);
        return 0;
    }

    private static byte** _environ;
    private static readonly object _environLock = new();

    /// <summary>POSIX <c>char **environ</c> — the whole process environment as a
    /// NULL-terminated array of NUL-terminated <c>"NAME=value"</c> UTF-8 strings.
    /// Built lazily from <see cref="Environment.GetEnvironmentVariables"/> and
    /// cached (a one-shot snapshot — dotcc doesn't reflect later <c>setenv</c>
    /// mutations through it, which is enough for the read-only iterators that use
    /// it). SRFI-98's <c>get-environment-variables</c> walks it; surfaced by bare
    /// name through <c>using static Libc</c> so a program's <c>extern char
    /// **environ;</c> resolves here. Double-checked lock: concurrent first-callers
    /// build one shared array, not two (parallel tests / a threaded program).</summary>
    public static byte** environ
    {
        get
        {
            var existing = _environ;
            if (existing != null) { return existing; }
            lock (_environLock)
            {
                // `??=` isn't valid on a pointer type — explicit check.
                if (_environ == null) { _environ = BuildEnviron(); }
                return _environ;
            }
        }
    }

    private static byte** BuildEnviron()
    {
        var vars = Environment.GetEnvironmentVariables();
        var arr = (byte**)NativeMemory.Alloc((nuint)(vars.Count + 1), (nuint)sizeof(byte*));
        var i = 0;
        foreach (System.Collections.DictionaryEntry e in vars)
        {
            var entry = $"{e.Key}={e.Value}";
            var need = Encoding.UTF8.GetByteCount(entry) + 1;
            var p = (byte*)NativeMemory.Alloc((nuint)need);
            var n = Encoding.UTF8.GetBytes(entry, new Span<byte>(p, need));
            p[n] = 0;
            arr[i++] = p;
        }
        arr[i] = null;
        return arr;
    }

    /// <summary><c>exit(code)</c> — terminate the program with
    /// <paramref name="code"/>. Routes to <see cref="Environment.Exit(int)"/>.
    /// (dotcc does not yet run <c>atexit</c> handlers.)</summary>
    public static void exit(int code) => Environment.Exit(code);

    /// <summary><c>_Exit(code)</c> (C99) — terminate immediately without flushing
    /// or running handlers. Same backing as <see cref="exit"/> here.</summary>
    public static void _Exit(int code) => Environment.Exit(code);

    /// <summary><c>abort()</c> — abnormal termination. Routes to
    /// <see cref="Environment.FailFast(string)"/>.</summary>
    public static void abort() => Environment.FailFast("abort() called");

    /// <summary>
    /// <c>system(command)</c> — hand <paramref name="command"/> to the host
    /// command interpreter and return its exit status. With a null command,
    /// returns nonzero (a command processor is available). On a launch failure
    /// returns -1, matching the C library.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="System.Diagnostics.Process"/>, the only libc entry
    /// that spawns a child process. It costs nothing in programs that don't call
    /// it: <c>Process</c> ships in the shared framework (no extra dependency),
    /// and under NativeAOT the trimmer drops this method — and <c>Process</c>
    /// with it — when nothing reaches it. The interpreter matches the platform's
    /// CRT: <c>cmd.exe /c …</c> on Windows, <c>/bin/sh -c …</c> elsewhere. We set
    /// <c>UseShellExecute = false</c> with an explicit executable, which keeps
    /// the call AOT-analyzer-clean (no <c>[RequiresUnreferencedCode]</c> path).
    /// </remarks>
    public static int system(byte* command)
    {
        // C: system(NULL) probes for a command processor — we always have one.
        if (command == null) { return 1; }
        var cmd = Encoding.UTF8.GetString(command, strlen(command));
        var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false };
        if (OperatingSystem.IsWindows())
        {
            // Mirror the MSVC CRT: ComSpec (cmd.exe) with /c, the whole string
            // handed to cmd verbatim so cmd does the parsing (system's contract).
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/c " + cmd;
        }
        else
        {
            // POSIX: /bin/sh -c "<command>" — ArgumentList keeps the command a
            // single argv entry so the shell, not .NET, splits it.
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(cmd);
        }
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) { return -1; }
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch
        {
            return -1; // couldn't launch the interpreter
        }
    }

    // ---------------------------------------------------------------------
    // Generic sort / search (function-pointer comparator)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>qsort(base, n, size, cmp)</c> — sort <paramref name="n"/> elements of
    /// <paramref name="size"/> bytes in place using the comparator
    /// <paramref name="cmp"/> (returns &lt;0 / 0 / &gt;0). Implemented as an
    /// in-place insertion sort (stable, O(n²)) — correct for the contract; the
    /// element-swap temp comes from a single scratch allocation.
    /// </summary>
    public static void qsort(void* @base, int n, int size, delegate*<void*, void*, int> cmp)
    {
        if (n < 2 || size <= 0) { return; }
        byte* b = (byte*)@base;
        byte* tmp = (byte*)NativeMemory.Alloc((nuint)size);
        for (int i = 1; i < n; i++)
        {
            Buffer.MemoryCopy(b + (long)i * size, tmp, size, size);
            int j = i - 1;
            while (j >= 0 && cmp(b + (long)j * size, tmp) > 0)
            {
                Buffer.MemoryCopy(b + (long)j * size, b + (long)(j + 1) * size, size, size);
                j--;
            }
            Buffer.MemoryCopy(tmp, b + (long)(j + 1) * size, size, size);
        }
        NativeMemory.Free(tmp);
    }

    /// <summary>
    /// <c>bsearch(key, base, n, size, cmp)</c> — binary-search a sorted array.
    /// Returns a pointer to a matching element or <c>null</c>. The array must be
    /// sorted consistently with <paramref name="cmp"/>.
    /// </summary>
    public static void* bsearch(void* key, void* @base, int n, int size, delegate*<void*, void*, int> cmp)
    {
        byte* b = (byte*)@base;
        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            byte* el = b + (long)mid * size;
            int c = cmp(key, el);
            if (c < 0) { hi = mid - 1; }
            else if (c > 0) { lo = mid + 1; }
            else { return el; }
        }
        return null;
    }
}
