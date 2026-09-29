#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DotCC.Libc;

/// <summary>
/// Seq-cst atomic primitives backing C11 <c>_Atomic</c> and <c>&lt;stdatomic.h&gt;</c>.
/// C11 atomic operations default to <c>memory_order_seq_cst</c>; these use
/// <see cref="Interlocked"/> (full fences) on a SAME-WIDTH integer reinterpretation
/// of the location (<c>byte</c>, <c>ushort</c>, <c>int</c> or <c>long</c>), so any 1-,
/// 2-, 4- or 8-byte unmanaged scalar (<c>byte</c>/<c>sbyte</c>/<c>short</c>/<c>ushort</c>/
/// <c>char</c>/<c>CBool</c>/<c>int</c>/<c>uint</c>/<c>long</c>/<c>ulong</c>/<c>nint</c>/
/// <c>nuint</c>/<c>float</c>/<c>double</c>) is covered by one generic implementation. The
/// compare-and-swap loops do the arithmetic/bitwise step in the value type's own space
/// (<see cref="INumber{T}"/> / <see cref="IBinaryInteger{T}"/>) and the CAS compares BIT
/// patterns, so <c>float</c>/<c>double</c> (and <c>±0</c>/<c>NaN</c>) behave correctly.
/// </summary>
/// <remarks>
/// The width is exact: a 1-byte atomic touches only its byte (CPython's
/// <c>_PyOnceFlag</c> and <c>PyMutex</c> are <c>uint8_t</c> beside other fields). Any
/// other size (an <c>_Atomic</c> struct) throws <see cref="System.NotSupportedException"/>
/// rather than reach past the object. A pointer type can't be a generic argument, so
/// pointers have their own overloads below.
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
    // ---- load / store / exchange (same-width reinterpretation) ------------

    /// <summary>Atomically read <paramref name="loc"/>. Below 8 bytes a CAS against 0 is
    /// the full-fence read (it writes the same bits, and only when they are already 0):
    /// <see cref="Interlocked.Read(ref long)"/> exists only for 8 bytes.</summary>
    public static unsafe T Load<T>(ref T loc) where T : unmanaged
    {
        switch (sizeof(T))
        {
            case 1: { var v = Interlocked.CompareExchange(ref Unsafe.As<T, byte>(ref loc), 0, 0); return Unsafe.As<byte, T>(ref v); }
            case 2: { var v = Interlocked.CompareExchange(ref Unsafe.As<T, ushort>(ref loc), 0, 0); return Unsafe.As<ushort, T>(ref v); }
            case 4: { var v = Interlocked.CompareExchange(ref Unsafe.As<T, int>(ref loc), 0, 0); return Unsafe.As<int, T>(ref v); }
            case 8: { var v = Interlocked.Read(ref Unsafe.As<T, long>(ref loc)); return Unsafe.As<long, T>(ref v); }
            default: throw Unsupported<T>();
        }
    }

    /// <summary>Atomically store <paramref name="value"/>; returns it, so the C assignment
    /// expression <c>x = v</c> yields <c>v</c>.</summary>
    public static T Store<T>(ref T loc, T value) where T : unmanaged
    {
        Exchange(ref loc, value);
        return value;
    }

    /// <summary>Atomically replace <paramref name="loc"/> and return the OLD value (C11
    /// <c>atomic_exchange</c>).</summary>
    public static unsafe T Exchange<T>(ref T loc, T value) where T : unmanaged
    {
        switch (sizeof(T))
        {
            case 1: { var o = Interlocked.Exchange(ref Unsafe.As<T, byte>(ref loc), Unsafe.As<T, byte>(ref value)); return Unsafe.As<byte, T>(ref o); }
            case 2: { var o = Interlocked.Exchange(ref Unsafe.As<T, ushort>(ref loc), Unsafe.As<T, ushort>(ref value)); return Unsafe.As<ushort, T>(ref o); }
            case 4: { var o = Interlocked.Exchange(ref Unsafe.As<T, int>(ref loc), Unsafe.As<T, int>(ref value)); return Unsafe.As<int, T>(ref o); }
            case 8: { var o = Interlocked.Exchange(ref Unsafe.As<T, long>(ref loc), Unsafe.As<T, long>(ref value)); return Unsafe.As<long, T>(ref o); }
            default: throw Unsupported<T>();
        }
    }

    /// <summary>Raw CAS: write <paramref name="desired"/> iff the location's bits equal
    /// <paramref name="comparand"/>'s; returns whether it succeeded (a bit comparison,
    /// correct for float <c>±0</c> / <c>NaN</c>).</summary>
    private static unsafe bool TryCas<T>(ref T loc, T desired, T comparand) where T : unmanaged
    {
        switch (sizeof(T))
        {
            case 1:
            {
                var c = Unsafe.As<T, byte>(ref comparand);
                return Interlocked.CompareExchange(ref Unsafe.As<T, byte>(ref loc), Unsafe.As<T, byte>(ref desired), c) == c;
            }
            case 2:
            {
                var c = Unsafe.As<T, ushort>(ref comparand);
                return Interlocked.CompareExchange(ref Unsafe.As<T, ushort>(ref loc), Unsafe.As<T, ushort>(ref desired), c) == c;
            }
            case 4:
            {
                var c = Unsafe.As<T, int>(ref comparand);
                return Interlocked.CompareExchange(ref Unsafe.As<T, int>(ref loc), Unsafe.As<T, int>(ref desired), c) == c;
            }
            case 8:
            {
                var c = Unsafe.As<T, long>(ref comparand);
                return Interlocked.CompareExchange(ref Unsafe.As<T, long>(ref loc), Unsafe.As<T, long>(ref desired), c) == c;
            }
            default: throw Unsupported<T>();
        }
    }

    /// <summary>The error for an atomic of a size no <see cref="Interlocked"/> width
    /// matches (an <c>_Atomic</c> struct): loud, instead of touching bytes past it.</summary>
    private static System.NotSupportedException Unsupported<T>() where T : unmanaged =>
        new($"dotcc: no {Unsafe.SizeOf<T>()}-byte atomic ({typeof(T).Name}); atomics are 1-, 2-, 4- or 8-byte scalars");

    /// <summary>
    /// C11 <c>atomic_compare_exchange_strong</c>: if <paramref name="loc"/> holds
    /// <paramref name="expected"/>, store <paramref name="desired"/> and return
    /// true; otherwise load the actual value into <paramref name="expected"/> and
    /// return false. (No spurious failures: <see cref="Interlocked"/> CAS is
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

    // ---- pointers ----------------------------------------------------------
    // A pointer cannot be a generic argument (CS0306), so the pointer forms (CPython's
    // _Py_atomic_*_ptr over `void **`) take the location as `ref void*` and run the same
    // Interlocked operations on it as an 8-byte integer.

    /// <summary>Atomically read a pointer.</summary>
    public static unsafe void* Load(ref void* loc)
    {
        fixed (void** p = &loc) { return (void*)Interlocked.Read(ref *(long*)p); }
    }

    /// <summary>Atomically store a pointer; returns it, as the C assignment yields it.</summary>
    public static unsafe void* Store(ref void* loc, void* value)
    {
        fixed (void** p = &loc) { Interlocked.Exchange(ref *(nint*)p, (nint)value); }
        return value;
    }

    /// <summary>Atomically replace a pointer and return the old one.</summary>
    public static unsafe void* Exchange(ref void* loc, void* value)
    {
        fixed (void** p = &loc) { return (void*)Interlocked.Exchange(ref *(nint*)p, (nint)value); }
    }

    /// <summary><see cref="CompareExchange{T}"/> for a pointer: store <paramref name="desired"/> iff
    /// the location holds <paramref name="expected"/>, else load the actual pointer into it.</summary>
    public static unsafe bool CompareExchange(ref void* loc, ref void* expected, void* desired)
    {
        fixed (void** p = &loc)
        {
            var e = (nint)expected;
            var actual = Interlocked.CompareExchange(ref *(nint*)p, (nint)desired, e);
            if (actual == e) { return true; }
            expected = (void*)actual;
            return false;
        }
    }

    // ---- volatile aggregates -----------------------------------------------
    // C volatile access of a type System.Threading.Volatile has no overload for (a struct,
    // union or enum): the access itself, fenced on the side C's ordering needs (a read
    // before later accesses, a write after earlier ones).

    /// <summary>A volatile read of any unmanaged value.</summary>
    public static T VolatileLoad<T>(ref T loc) where T : unmanaged
    {
        var value = loc;
        Interlocked.MemoryBarrier();
        return value;
    }

    /// <summary>A volatile write of any unmanaged value; returns it, as the C assignment yields it.</summary>
    public static T VolatileStore<T>(ref T loc, T value) where T : unmanaged
    {
        Interlocked.MemoryBarrier();
        loc = value;
        return value;
    }

    // ---- fences ------------------------------------------------------------
    // C11 atomic_thread_fence / atomic_signal_fence. A seq-cst thread fence is a
    // full memory barrier; the signal fence is a compiler barrier (single-thread
    // ordering w.r.t. a signal handler) — on .NET the conservative mapping is also
    // a full barrier. The memory_order argument is accepted and ignored (every
    // order we honour maps to "at least a full barrier here").
    public static void ThreadFence() => Interlocked.MemoryBarrier();
    public static void SignalFence() => Interlocked.MemoryBarrier();
}
