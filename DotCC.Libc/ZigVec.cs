#nullable enable

namespace DotCC.Libc;

/// <summary>
/// The runtime backing for Zig's SIMD vectors (<c>@Vector(N, T)</c>; road-to-zig-std, the target-identity
/// segment T5). A numeric vector is .NET's own <see cref="System.Runtime.Intrinsics.Vector128{T}"/> (or the
/// 64 / 256 / 512-bit sibling that fits <c>N * @sizeOf(T)</c>), so element-wise arithmetic and comparisons are
/// the JIT's SIMD instructions, with software fallbacks on a CPU that lacks them. A BOOL vector, what a
/// comparison yields, is a lane BITMASK in a <see cref="ulong"/> (bit <c>i</c> is lane <c>i</c>), the shape
/// <c>ExtractMostSignificantBits</c> produces: <c>@reduce(.Or, m)</c> is <c>m != 0</c>, and the first set lane
/// is a trailing-zero count. The methods here cover what has no .NET operator: loads, comparisons to a mask,
/// reductions, <c>@select</c> and lane reads.
/// </summary>
public static class ZigVec
{
    // ---- loads: `slice[i..][0..N].*` / an array at a vector sink ---------------------------------------

    /// <summary>Load a 64-bit vector from <paramref name="p"/>.</summary>
    public static unsafe System.Runtime.Intrinsics.Vector64<T> Load64<T>(T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector64.Load(p);

    /// <summary>Load a 128-bit vector from <paramref name="p"/>.</summary>
    public static unsafe System.Runtime.Intrinsics.Vector128<T> Load128<T>(T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector128.Load(p);

    /// <summary>Load a 256-bit vector from <paramref name="p"/>.</summary>
    public static unsafe System.Runtime.Intrinsics.Vector256<T> Load256<T>(T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector256.Load(p);

    /// <summary>Load a 512-bit vector from <paramref name="p"/>.</summary>
    public static unsafe System.Runtime.Intrinsics.Vector512<T> Load512<T>(T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector512.Load(p);

    // ---- stores: `dest[i..][0..N].* = v;` -------------------------------------------------------------

    /// <summary>Store a 64-bit vector's lanes to <paramref name="p"/>.</summary>
    public static unsafe void Store<T>(System.Runtime.Intrinsics.Vector64<T> v, T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector64.Store(v, p);

    /// <summary>Store a 128-bit vector's lanes to <paramref name="p"/>.</summary>
    public static unsafe void Store<T>(System.Runtime.Intrinsics.Vector128<T> v, T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector128.Store(v, p);

    /// <summary>Store a 256-bit vector's lanes to <paramref name="p"/>.</summary>
    public static unsafe void Store<T>(System.Runtime.Intrinsics.Vector256<T> v, T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector256.Store(v, p);

    /// <summary>Store a 512-bit vector's lanes to <paramref name="p"/>.</summary>
    public static unsafe void Store<T>(System.Runtime.Intrinsics.Vector512<T> v, T* p) where T : unmanaged
        => System.Runtime.Intrinsics.Vector512.Store(v, p);

    // ---- widening: a narrower-lane vector at a wider-lane vector sink ---------------------------------

    /// <summary>Widen each lane of a 64-bit vector to <typeparamref name="TTo"/> in a 128-bit one (the lane
    /// count is kept; <paramref name="witness"/> only names the target lane type).</summary>
    public static System.Runtime.Intrinsics.Vector128<TTo> Widen128<TFrom, TTo>(System.Runtime.Intrinsics.Vector64<TFrom> v, TTo witness)
        where TFrom : unmanaged, System.Numerics.IBinaryInteger<TFrom> where TTo : unmanaged, System.Numerics.IBinaryInteger<TTo>
    {
        System.Span<TTo> lanes = stackalloc TTo[System.Runtime.Intrinsics.Vector128<TTo>.Count];
        for (var i = 0; i < System.Runtime.Intrinsics.Vector64<TFrom>.Count; i++) { lanes[i] = TTo.CreateTruncating(v[i]); }
        return System.Runtime.Intrinsics.Vector128.Create<TTo>(lanes);
    }

    /// <summary>Widen each lane of a 64-bit vector to <typeparamref name="TTo"/> in a 256-bit one (the lane
    /// count is kept; <paramref name="witness"/> only names the target lane type).</summary>
    public static System.Runtime.Intrinsics.Vector256<TTo> Widen256<TFrom, TTo>(System.Runtime.Intrinsics.Vector64<TFrom> v, TTo witness)
        where TFrom : unmanaged, System.Numerics.IBinaryInteger<TFrom> where TTo : unmanaged, System.Numerics.IBinaryInteger<TTo>
    {
        System.Span<TTo> lanes = stackalloc TTo[System.Runtime.Intrinsics.Vector256<TTo>.Count];
        for (var i = 0; i < System.Runtime.Intrinsics.Vector64<TFrom>.Count; i++) { lanes[i] = TTo.CreateTruncating(v[i]); }
        return System.Runtime.Intrinsics.Vector256.Create<TTo>(lanes);
    }

    /// <summary>Widen each lane of a 128-bit vector to <typeparamref name="TTo"/> in a 256-bit one (the lane
    /// count is kept; <paramref name="witness"/> only names the target lane type).</summary>
    public static System.Runtime.Intrinsics.Vector256<TTo> Widen256<TFrom, TTo>(System.Runtime.Intrinsics.Vector128<TFrom> v, TTo witness)
        where TFrom : unmanaged, System.Numerics.IBinaryInteger<TFrom> where TTo : unmanaged, System.Numerics.IBinaryInteger<TTo>
    {
        System.Span<TTo> lanes = stackalloc TTo[System.Runtime.Intrinsics.Vector256<TTo>.Count];
        for (var i = 0; i < System.Runtime.Intrinsics.Vector128<TFrom>.Count; i++) { lanes[i] = TTo.CreateTruncating(v[i]); }
        return System.Runtime.Intrinsics.Vector256.Create<TTo>(lanes);
    }

    /// <summary>Widen each lane of a 64-bit vector to <typeparamref name="TTo"/> in a 512-bit one (the lane
    /// count is kept; <paramref name="witness"/> only names the target lane type).</summary>
    public static System.Runtime.Intrinsics.Vector512<TTo> Widen512<TFrom, TTo>(System.Runtime.Intrinsics.Vector64<TFrom> v, TTo witness)
        where TFrom : unmanaged, System.Numerics.IBinaryInteger<TFrom> where TTo : unmanaged, System.Numerics.IBinaryInteger<TTo>
    {
        System.Span<TTo> lanes = stackalloc TTo[System.Runtime.Intrinsics.Vector512<TTo>.Count];
        for (var i = 0; i < System.Runtime.Intrinsics.Vector64<TFrom>.Count; i++) { lanes[i] = TTo.CreateTruncating(v[i]); }
        return System.Runtime.Intrinsics.Vector512.Create<TTo>(lanes);
    }

    /// <summary>Widen each lane of a 128-bit vector to <typeparamref name="TTo"/> in a 512-bit one (the lane
    /// count is kept; <paramref name="witness"/> only names the target lane type).</summary>
    public static System.Runtime.Intrinsics.Vector512<TTo> Widen512<TFrom, TTo>(System.Runtime.Intrinsics.Vector128<TFrom> v, TTo witness)
        where TFrom : unmanaged, System.Numerics.IBinaryInteger<TFrom> where TTo : unmanaged, System.Numerics.IBinaryInteger<TTo>
    {
        System.Span<TTo> lanes = stackalloc TTo[System.Runtime.Intrinsics.Vector512<TTo>.Count];
        for (var i = 0; i < System.Runtime.Intrinsics.Vector128<TFrom>.Count; i++) { lanes[i] = TTo.CreateTruncating(v[i]); }
        return System.Runtime.Intrinsics.Vector512.Create<TTo>(lanes);
    }

    /// <summary>Widen each lane of a 256-bit vector to <typeparamref name="TTo"/> in a 512-bit one (the lane
    /// count is kept; <paramref name="witness"/> only names the target lane type).</summary>
    public static System.Runtime.Intrinsics.Vector512<TTo> Widen512<TFrom, TTo>(System.Runtime.Intrinsics.Vector256<TFrom> v, TTo witness)
        where TFrom : unmanaged, System.Numerics.IBinaryInteger<TFrom> where TTo : unmanaged, System.Numerics.IBinaryInteger<TTo>
    {
        System.Span<TTo> lanes = stackalloc TTo[System.Runtime.Intrinsics.Vector512<TTo>.Count];
        for (var i = 0; i < System.Runtime.Intrinsics.Vector256<TFrom>.Count; i++) { lanes[i] = TTo.CreateTruncating(v[i]); }
        return System.Runtime.Intrinsics.Vector512.Create<TTo>(lanes);
    }

    // ---- comparisons to a lane mask -------------------------------------------------------------------

    /// <summary>Lane-wise <c>a == b</c> as a lane mask.</summary>
    public static ulong Eq<T>(System.Runtime.Intrinsics.Vector64<T> a, System.Runtime.Intrinsics.Vector64<T> b)
        => System.Runtime.Intrinsics.Vector64.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector64.Equals(a, b));
    /// <summary>Lane-wise <c>a == b</c> as a lane mask.</summary>
    public static ulong Eq<T>(System.Runtime.Intrinsics.Vector128<T> a, System.Runtime.Intrinsics.Vector128<T> b)
        => System.Runtime.Intrinsics.Vector128.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector128.Equals(a, b));
    /// <summary>Lane-wise <c>a == b</c> as a lane mask.</summary>
    public static ulong Eq<T>(System.Runtime.Intrinsics.Vector256<T> a, System.Runtime.Intrinsics.Vector256<T> b)
        => System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.Equals(a, b));
    /// <summary>Lane-wise <c>a == b</c> as a lane mask.</summary>
    public static ulong Eq<T>(System.Runtime.Intrinsics.Vector512<T> a, System.Runtime.Intrinsics.Vector512<T> b)
        => System.Runtime.Intrinsics.Vector512.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector512.Equals(a, b));

    /// <summary>Lane-wise <c>a != b</c> as a lane mask.</summary>
    public static ulong Ne<T>(System.Runtime.Intrinsics.Vector64<T> a, System.Runtime.Intrinsics.Vector64<T> b)
        => ~Eq(a, b) & Full(System.Runtime.Intrinsics.Vector64<T>.Count);
    /// <summary>Lane-wise <c>a != b</c> as a lane mask.</summary>
    public static ulong Ne<T>(System.Runtime.Intrinsics.Vector128<T> a, System.Runtime.Intrinsics.Vector128<T> b)
        => ~Eq(a, b) & Full(System.Runtime.Intrinsics.Vector128<T>.Count);
    /// <summary>Lane-wise <c>a != b</c> as a lane mask.</summary>
    public static ulong Ne<T>(System.Runtime.Intrinsics.Vector256<T> a, System.Runtime.Intrinsics.Vector256<T> b)
        => ~Eq(a, b) & Full(System.Runtime.Intrinsics.Vector256<T>.Count);
    /// <summary>Lane-wise <c>a != b</c> as a lane mask.</summary>
    public static ulong Ne<T>(System.Runtime.Intrinsics.Vector512<T> a, System.Runtime.Intrinsics.Vector512<T> b)
        => ~Eq(a, b) & Full(System.Runtime.Intrinsics.Vector512<T>.Count);

    /// <summary>Lane-wise <c>a &lt; b</c> as a lane mask.</summary>
    public static ulong Lt<T>(System.Runtime.Intrinsics.Vector64<T> a, System.Runtime.Intrinsics.Vector64<T> b)
        => System.Runtime.Intrinsics.Vector64.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector64.LessThan(a, b));
    /// <summary>Lane-wise <c>a &lt; b</c> as a lane mask.</summary>
    public static ulong Lt<T>(System.Runtime.Intrinsics.Vector128<T> a, System.Runtime.Intrinsics.Vector128<T> b)
        => System.Runtime.Intrinsics.Vector128.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector128.LessThan(a, b));
    /// <summary>Lane-wise <c>a &lt; b</c> as a lane mask.</summary>
    public static ulong Lt<T>(System.Runtime.Intrinsics.Vector256<T> a, System.Runtime.Intrinsics.Vector256<T> b)
        => System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.LessThan(a, b));
    /// <summary>Lane-wise <c>a &lt; b</c> as a lane mask.</summary>
    public static ulong Lt<T>(System.Runtime.Intrinsics.Vector512<T> a, System.Runtime.Intrinsics.Vector512<T> b)
        => System.Runtime.Intrinsics.Vector512.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector512.LessThan(a, b));
    /// <summary>Lane-wise <c>a &gt; b</c> as a lane mask.</summary>
    public static ulong Gt<T>(System.Runtime.Intrinsics.Vector64<T> a, System.Runtime.Intrinsics.Vector64<T> b)
        => System.Runtime.Intrinsics.Vector64.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector64.GreaterThan(a, b));
    /// <summary>Lane-wise <c>a &gt; b</c> as a lane mask.</summary>
    public static ulong Gt<T>(System.Runtime.Intrinsics.Vector128<T> a, System.Runtime.Intrinsics.Vector128<T> b)
        => System.Runtime.Intrinsics.Vector128.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector128.GreaterThan(a, b));
    /// <summary>Lane-wise <c>a &gt; b</c> as a lane mask.</summary>
    public static ulong Gt<T>(System.Runtime.Intrinsics.Vector256<T> a, System.Runtime.Intrinsics.Vector256<T> b)
        => System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.GreaterThan(a, b));
    /// <summary>Lane-wise <c>a &gt; b</c> as a lane mask.</summary>
    public static ulong Gt<T>(System.Runtime.Intrinsics.Vector512<T> a, System.Runtime.Intrinsics.Vector512<T> b)
        => System.Runtime.Intrinsics.Vector512.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector512.GreaterThan(a, b));
    /// <summary>Lane-wise <c>a &lt;= b</c> as a lane mask.</summary>
    public static ulong Le<T>(System.Runtime.Intrinsics.Vector64<T> a, System.Runtime.Intrinsics.Vector64<T> b)
        => System.Runtime.Intrinsics.Vector64.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector64.LessThanOrEqual(a, b));
    /// <summary>Lane-wise <c>a &lt;= b</c> as a lane mask.</summary>
    public static ulong Le<T>(System.Runtime.Intrinsics.Vector128<T> a, System.Runtime.Intrinsics.Vector128<T> b)
        => System.Runtime.Intrinsics.Vector128.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector128.LessThanOrEqual(a, b));
    /// <summary>Lane-wise <c>a &lt;= b</c> as a lane mask.</summary>
    public static ulong Le<T>(System.Runtime.Intrinsics.Vector256<T> a, System.Runtime.Intrinsics.Vector256<T> b)
        => System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.LessThanOrEqual(a, b));
    /// <summary>Lane-wise <c>a &lt;= b</c> as a lane mask.</summary>
    public static ulong Le<T>(System.Runtime.Intrinsics.Vector512<T> a, System.Runtime.Intrinsics.Vector512<T> b)
        => System.Runtime.Intrinsics.Vector512.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector512.LessThanOrEqual(a, b));
    /// <summary>Lane-wise <c>a &gt;= b</c> as a lane mask.</summary>
    public static ulong Ge<T>(System.Runtime.Intrinsics.Vector64<T> a, System.Runtime.Intrinsics.Vector64<T> b)
        => System.Runtime.Intrinsics.Vector64.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector64.GreaterThanOrEqual(a, b));
    /// <summary>Lane-wise <c>a &gt;= b</c> as a lane mask.</summary>
    public static ulong Ge<T>(System.Runtime.Intrinsics.Vector128<T> a, System.Runtime.Intrinsics.Vector128<T> b)
        => System.Runtime.Intrinsics.Vector128.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector128.GreaterThanOrEqual(a, b));
    /// <summary>Lane-wise <c>a &gt;= b</c> as a lane mask.</summary>
    public static ulong Ge<T>(System.Runtime.Intrinsics.Vector256<T> a, System.Runtime.Intrinsics.Vector256<T> b)
        => System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.GreaterThanOrEqual(a, b));
    /// <summary>Lane-wise <c>a &gt;= b</c> as a lane mask.</summary>
    public static ulong Ge<T>(System.Runtime.Intrinsics.Vector512<T> a, System.Runtime.Intrinsics.Vector512<T> b)
        => System.Runtime.Intrinsics.Vector512.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector512.GreaterThanOrEqual(a, b));

    /// <summary>The mask with the low <paramref name="lanes"/> bits set: every lane true.</summary>
    public static ulong Full(int lanes) => lanes >= 64 ? ulong.MaxValue : (1UL << lanes) - 1;

    // ---- reductions (`@reduce(op, v)`) ----------------------------------------------------------------

    /// <summary><c>@reduce(.Add, v)</c>: the lanes' wrapping sum.</summary>
    public static T ReduceAdd<T>(System.Runtime.Intrinsics.Vector64<T> v) => System.Runtime.Intrinsics.Vector64.Sum(v);
    /// <summary><c>@reduce(.Add, v)</c>: the lanes' wrapping sum.</summary>
    public static T ReduceAdd<T>(System.Runtime.Intrinsics.Vector128<T> v) => System.Runtime.Intrinsics.Vector128.Sum(v);
    /// <summary><c>@reduce(.Add, v)</c>: the lanes' wrapping sum.</summary>
    public static T ReduceAdd<T>(System.Runtime.Intrinsics.Vector256<T> v) => System.Runtime.Intrinsics.Vector256.Sum(v);
    /// <summary><c>@reduce(.Add, v)</c>: the lanes' wrapping sum.</summary>
    public static T ReduceAdd<T>(System.Runtime.Intrinsics.Vector512<T> v) => System.Runtime.Intrinsics.Vector512.Sum(v);

    /// <summary><c>@reduce(.Min, v)</c>.</summary>
    public static T ReduceMin<T>(System.Runtime.Intrinsics.Vector64<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector64<T>.Count; i++) { r = T.Min(r, v[i]); }
        return r;
    }
    /// <summary><c>@reduce(.Min, v)</c>.</summary>
    public static T ReduceMin<T>(System.Runtime.Intrinsics.Vector128<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector128<T>.Count; i++) { r = T.Min(r, v[i]); }
        return r;
    }
    /// <summary><c>@reduce(.Min, v)</c>.</summary>
    public static T ReduceMin<T>(System.Runtime.Intrinsics.Vector256<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector256<T>.Count; i++) { r = T.Min(r, v[i]); }
        return r;
    }
    /// <summary><c>@reduce(.Min, v)</c>.</summary>
    public static T ReduceMin<T>(System.Runtime.Intrinsics.Vector512<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector512<T>.Count; i++) { r = T.Min(r, v[i]); }
        return r;
    }

    /// <summary><c>@reduce(.Max, v)</c>.</summary>
    public static T ReduceMax<T>(System.Runtime.Intrinsics.Vector64<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector64<T>.Count; i++) { r = T.Max(r, v[i]); }
        return r;
    }
    /// <summary><c>@reduce(.Max, v)</c>.</summary>
    public static T ReduceMax<T>(System.Runtime.Intrinsics.Vector128<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector128<T>.Count; i++) { r = T.Max(r, v[i]); }
        return r;
    }
    /// <summary><c>@reduce(.Max, v)</c>.</summary>
    public static T ReduceMax<T>(System.Runtime.Intrinsics.Vector256<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector256<T>.Count; i++) { r = T.Max(r, v[i]); }
        return r;
    }
    /// <summary><c>@reduce(.Max, v)</c>.</summary>
    public static T ReduceMax<T>(System.Runtime.Intrinsics.Vector512<T> v) where T : System.Numerics.INumber<T>
    {
        var r = v[0];
        for (var i = 1; i < System.Runtime.Intrinsics.Vector512<T>.Count; i++) { r = T.Max(r, v[i]); }
        return r;
    }

    // ---- `@select(T, mask, a, b)` and lane reads ------------------------------------------------------

    /// <summary><c>@select(T, mask, a, b)</c>: lane <c>i</c> from <paramref name="a"/> where bit <c>i</c> of the mask
    /// is set, else from <paramref name="b"/>.</summary>
    public static System.Runtime.Intrinsics.Vector128<T> Select<T>(ulong mask, System.Runtime.Intrinsics.Vector128<T> a, System.Runtime.Intrinsics.Vector128<T> b)
    {
        var r = b;
        for (var i = 0; i < System.Runtime.Intrinsics.Vector128<T>.Count; i++)
        {
            if (((mask >> i) & 1) != 0) { r = WithLane(r, i, a[i]); }
        }
        return r;
    }
    /// <summary><c>@select(T, mask, a, b)</c> for 256-bit vectors.</summary>
    public static System.Runtime.Intrinsics.Vector256<T> Select<T>(ulong mask, System.Runtime.Intrinsics.Vector256<T> a, System.Runtime.Intrinsics.Vector256<T> b)
    {
        var r = b;
        for (var i = 0; i < System.Runtime.Intrinsics.Vector256<T>.Count; i++)
        {
            if (((mask >> i) & 1) != 0) { r = WithLane(r, i, a[i]); }
        }
        return r;
    }

    /// <summary><c>@select(T, mask, a, b)</c> for 64-bit vectors.</summary>
    public static System.Runtime.Intrinsics.Vector64<T> Select<T>(ulong mask, System.Runtime.Intrinsics.Vector64<T> a, System.Runtime.Intrinsics.Vector64<T> b)
    {
        var r = b;
        for (var i = 0; i < System.Runtime.Intrinsics.Vector64<T>.Count; i++)
        {
            if (((mask >> i) & 1) != 0) { r = WithLane(r, i, a[i]); }
        }
        return r;
    }
    /// <summary><c>@select(T, mask, a, b)</c> for 512-bit vectors.</summary>
    public static System.Runtime.Intrinsics.Vector512<T> Select<T>(ulong mask, System.Runtime.Intrinsics.Vector512<T> a, System.Runtime.Intrinsics.Vector512<T> b)
    {
        var r = b;
        for (var i = 0; i < System.Runtime.Intrinsics.Vector512<T>.Count; i++)
        {
            if (((mask >> i) & 1) != 0) { r = WithLane(r, i, a[i]); }
        }
        return r;
    }

    /// <summary><c>v[i] = x</c>: the vector with lane <paramref name="i"/> replaced (a .NET vector is immutable).</summary>
    public static System.Runtime.Intrinsics.Vector64<T> With<T>(System.Runtime.Intrinsics.Vector64<T> v, ulong i, T x) => WithLane(v, (int)i, x);
    /// <summary><c>v[i] = x</c>: the vector with lane <paramref name="i"/> replaced (a .NET vector is immutable).</summary>
    public static System.Runtime.Intrinsics.Vector128<T> With<T>(System.Runtime.Intrinsics.Vector128<T> v, ulong i, T x) => WithLane(v, (int)i, x);
    /// <summary><c>v[i] = x</c>: the vector with lane <paramref name="i"/> replaced (a .NET vector is immutable).</summary>
    public static System.Runtime.Intrinsics.Vector256<T> With<T>(System.Runtime.Intrinsics.Vector256<T> v, ulong i, T x) => WithLane(v, (int)i, x);
    /// <summary><c>v[i] = x</c>: the vector with lane <paramref name="i"/> replaced (a .NET vector is immutable).</summary>
    public static System.Runtime.Intrinsics.Vector512<T> With<T>(System.Runtime.Intrinsics.Vector512<T> v, ulong i, T x) => WithLane(v, (int)i, x);

    /// <summary>Replace one lane of a 64-bit vector.</summary>
    private static System.Runtime.Intrinsics.Vector64<T> WithLane<T>(System.Runtime.Intrinsics.Vector64<T> v, int i, T x)
        => System.Runtime.Intrinsics.Vector64.WithElement(v, i, x);
    /// <summary>Replace one lane of a 128-bit vector.</summary>
    private static System.Runtime.Intrinsics.Vector128<T> WithLane<T>(System.Runtime.Intrinsics.Vector128<T> v, int i, T x)
        => System.Runtime.Intrinsics.Vector128.WithElement(v, i, x);
    /// <summary>Replace one lane of a 256-bit vector.</summary>
    private static System.Runtime.Intrinsics.Vector256<T> WithLane<T>(System.Runtime.Intrinsics.Vector256<T> v, int i, T x)
        => System.Runtime.Intrinsics.Vector256.WithElement(v, i, x);
    /// <summary>Replace one lane of a 512-bit vector.</summary>
    private static System.Runtime.Intrinsics.Vector512<T> WithLane<T>(System.Runtime.Intrinsics.Vector512<T> v, int i, T x)
        => System.Runtime.Intrinsics.Vector512.WithElement(v, i, x);

    /// <summary><c>v[i]</c>: one lane.</summary>
    public static T Get<T>(System.Runtime.Intrinsics.Vector64<T> v, ulong i) => v[(int)i];
    /// <summary><c>v[i]</c>: one lane.</summary>
    public static T Get<T>(System.Runtime.Intrinsics.Vector128<T> v, ulong i) => v[(int)i];
    /// <summary><c>v[i]</c>: one lane.</summary>
    public static T Get<T>(System.Runtime.Intrinsics.Vector256<T> v, ulong i) => v[(int)i];
    /// <summary><c>v[i]</c>: one lane.</summary>
    public static T Get<T>(System.Runtime.Intrinsics.Vector512<T> v, ulong i) => v[(int)i];

    /// <summary><c>m[i]</c> of a bool vector: bit <c>i</c> of the lane mask.</summary>
    public static bool Bit(ulong mask, ulong i) => ((mask >> (int)i) & 1) != 0;
}
