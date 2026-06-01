#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DotCC.Libc;

/// <summary>
/// Seq-cst atomic primitives backing C11 <c>_Atomic</c> and <c>&lt;stdatomic.h&gt;</c>.
/// C11 atomic operations default to <c>memory_order_seq_cst</c>; these use
/// <see cref="Interlocked"/> (full fences) on a SAME-WIDTH integer reinterpretation
/// of the location, so any 4- or 8-byte unmanaged scalar — <c>int</c>/<c>uint</c>/
/// <c>long</c>/<c>ulong</c>/<c>nint</c>/<c>nuint</c>/<c>float</c>/<c>double</c> —
/// is covered by one generic implementation. The compare-and-swap loops do the
/// arithmetic/bitwise step in the value type's own space (<see cref="INumber{T}"/> /
/// <see cref="IBinaryInteger{T}"/>) and the CAS compares BIT patterns, so
/// <c>float</c>/<c>double</c> (and <c>±0</c>/<c>NaN</c>) behave correctly.
/// </summary>
/// <remarks>
/// dotcc only routes ELIGIBLE scalars here (the 4-/8-byte set above): a 1-/2-byte
/// atomic (<c>_Atomic char</c>) would over-read on reinterpretation, and a pointer
/// type can't be a generic argument — those fall back to a plain access (documented).
/// Fence note (same as <c>volatile</c>): on .NET these are full barriers, which is
/// at least as strong as C11 seq-cst requires.
/// <para>
/// The functions come in two RMW flavours mirroring C: <c>FetchX</c> returns the
/// OLD value (what C11's <c>atomic_fetch_*</c> yields), and <c>XFetch</c> returns
/// the NEW value (what the compound assignment <c>x += n</c> yields).
/// </para>
/// </remarks>
public static class Atomic
{
    // ---- load / store / exchange (4- or 8-byte reinterpretation) ----------

    public static unsafe T Load<T>(ref T loc) where T : unmanaged
    {
        if (sizeof(T) == 8)
        {
            long bits = Interlocked.Read(ref Unsafe.As<T, long>(ref loc));
            return Unsafe.As<long, T>(ref bits);
        }
        // 4-byte: a CAS against 0 is a full-fence atomic read (writes the same
        // bits only if already 0). There is no Interlocked.Read(ref int).
        int b = Interlocked.CompareExchange(ref Unsafe.As<T, int>(ref loc), 0, 0);
        return Unsafe.As<int, T>(ref b);
    }

    // Returns the stored value, so the C assignment expression `x = v` yields `v`.
    public static unsafe T Store<T>(ref T loc, T value) where T : unmanaged
    {
        if (sizeof(T) == 8) { Interlocked.Exchange(ref Unsafe.As<T, long>(ref loc), Unsafe.As<T, long>(ref value)); }
        else { Interlocked.Exchange(ref Unsafe.As<T, int>(ref loc), Unsafe.As<T, int>(ref value)); }
        return value;
    }

    // Atomically replace and return the OLD value (C11 atomic_exchange).
    public static unsafe T Exchange<T>(ref T loc, T value) where T : unmanaged
    {
        if (sizeof(T) == 8)
        {
            long old = Interlocked.Exchange(ref Unsafe.As<T, long>(ref loc), Unsafe.As<T, long>(ref value));
            return Unsafe.As<long, T>(ref old);
        }
        int o = Interlocked.Exchange(ref Unsafe.As<T, int>(ref loc), Unsafe.As<T, int>(ref value));
        return Unsafe.As<int, T>(ref o);
    }

    // Raw CAS: write `desired` iff the location's bits equal `comparand`'s; returns
    // whether it succeeded (bit comparison — correct for float ±0 / NaN).
    private static unsafe bool TryCas<T>(ref T loc, T desired, T comparand) where T : unmanaged
    {
        if (sizeof(T) == 8)
        {
            long c = Unsafe.As<T, long>(ref comparand);
            return Interlocked.CompareExchange(ref Unsafe.As<T, long>(ref loc), Unsafe.As<T, long>(ref desired), c) == c;
        }
        int c4 = Unsafe.As<T, int>(ref comparand);
        return Interlocked.CompareExchange(ref Unsafe.As<T, int>(ref loc), Unsafe.As<T, int>(ref desired), c4) == c4;
    }

    private static unsafe bool BitsEqual<T>(T a, T b) where T : unmanaged =>
        sizeof(T) == 8
            ? Unsafe.As<T, long>(ref a) == Unsafe.As<T, long>(ref b)
            : Unsafe.As<T, int>(ref a) == Unsafe.As<T, int>(ref b);

    /// <summary>
    /// C11 <c>atomic_compare_exchange_strong</c>: if <paramref name="loc"/> holds
    /// <paramref name="expected"/>, store <paramref name="desired"/> and return
    /// true; otherwise load the actual value into <paramref name="expected"/> and
    /// return false. (No spurious failures — <see cref="Interlocked"/> CAS is
    /// strong; the C11 _weak form maps to the same primitive.)
    /// </summary>
    public static bool CompareExchange<T>(ref T loc, ref T expected, T desired) where T : unmanaged
    {
        // The CAS is the single atomic step; on failure, report the actual value.
        if (TryCas(ref loc, desired, expected)) { return true; }
        expected = Load(ref loc);
        return false;
    }

    // ---- arithmetic RMW (INumber): Fetch* = old, *Fetch = new --------------

    public static T FetchAdd<T>(ref T loc, T arg) where T : unmanaged, INumber<T>
    { T old; do { old = Load(ref loc); } while (!TryCas(ref loc, old + arg, old)); return old; }
    public static T AddFetch<T>(ref T loc, T arg) where T : unmanaged, INumber<T>
    { T old, neu; do { old = Load(ref loc); neu = old + arg; } while (!TryCas(ref loc, neu, old)); return neu; }

    public static T FetchSub<T>(ref T loc, T arg) where T : unmanaged, INumber<T>
    { T old; do { old = Load(ref loc); } while (!TryCas(ref loc, old - arg, old)); return old; }
    public static T SubFetch<T>(ref T loc, T arg) where T : unmanaged, INumber<T>
    { T old, neu; do { old = Load(ref loc); neu = old - arg; } while (!TryCas(ref loc, neu, old)); return neu; }

    // ---- bitwise RMW (IBinaryInteger) --------------------------------------

    public static T FetchAnd<T>(ref T loc, T arg) where T : unmanaged, IBinaryInteger<T>
    { T old; do { old = Load(ref loc); } while (!TryCas(ref loc, old & arg, old)); return old; }
    public static T AndFetch<T>(ref T loc, T arg) where T : unmanaged, IBinaryInteger<T>
    { T old, neu; do { old = Load(ref loc); neu = old & arg; } while (!TryCas(ref loc, neu, old)); return neu; }

    public static T FetchOr<T>(ref T loc, T arg) where T : unmanaged, IBinaryInteger<T>
    { T old; do { old = Load(ref loc); } while (!TryCas(ref loc, old | arg, old)); return old; }
    public static T OrFetch<T>(ref T loc, T arg) where T : unmanaged, IBinaryInteger<T>
    { T old, neu; do { old = Load(ref loc); neu = old | arg; } while (!TryCas(ref loc, neu, old)); return neu; }

    public static T FetchXor<T>(ref T loc, T arg) where T : unmanaged, IBinaryInteger<T>
    { T old; do { old = Load(ref loc); } while (!TryCas(ref loc, old ^ arg, old)); return old; }
    public static T XorFetch<T>(ref T loc, T arg) where T : unmanaged, IBinaryInteger<T>
    { T old, neu; do { old = Load(ref loc); neu = old ^ arg; } while (!TryCas(ref loc, neu, old)); return neu; }

    // ---- fences ------------------------------------------------------------
    // C11 atomic_thread_fence / atomic_signal_fence. A seq-cst thread fence is a
    // full memory barrier; the signal fence is a compiler barrier (single-thread
    // ordering w.r.t. a signal handler) — on .NET the conservative mapping is also
    // a full barrier. The memory_order argument is accepted and ignored (every
    // order we honour maps to "at least a full barrier here").
    public static void ThreadFence() => Interlocked.MemoryBarrier();
    public static void SignalFence() => Interlocked.MemoryBarrier();
}
