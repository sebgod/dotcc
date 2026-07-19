#nullable enable

using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// The runtime backing for the Zig front-end's SATURATING arithmetic operators
/// (<c>+| -| *|</c> and their compound forms <c>+|= -|= *|=</c>; Milestone P, part 2), the
/// integer math builtins (<c>@min</c>/<c>@max</c>/<c>@rem</c>/<c>@divTrunc</c>/<c>@mod</c>/
/// <c>@divFloor</c>/<c>@popCount</c>/<c>@clz</c>/<c>@ctz</c>/<c>@byteSwap</c>/<c>@abs</c>), and the
/// overflow-detecting builtins (<c>@addWithOverflow</c>/<c>@subWithOverflow</c>/<c>@mulWithOverflow</c>/
/// <c>@shlWithOverflow</c>; road-to-zig-std B3). Auto-spliced into every emitted program (the <c>DotCC.Libc/*.cs</c>
/// <c>&lt;EmbeddedResource&gt;</c> glob) and compiled into <c>DotCC.Libc.dll</c> for the unit
/// tests, exactly like <see cref="Slice{T}"/> / <see cref="ErrUnion{T}"/> / the allocator types.
/// </summary>
/// <remarks>
/// <para>
/// Zig's <c>a +| b</c> clamps the true mathematical result to the operand type's range rather
/// than wrapping (<c>+%</c>) or trapping (plain <c>+</c>): <c>u8: 200 +| 100 == 255</c>,
/// <c>i8: 100 +| 100 == 127</c>, <c>i8: -100 -| 100 == -128</c>. The wrapping sibling lives in
/// the emitter (a truncating cast in the project's unchecked context); saturation needs a clamp,
/// which has no native C# operator, so it routes here.
/// </para>
/// <para>
/// <b>Design — exact-in-128-bit, then clamp.</b> Every integer type the Zig front-end produces is
/// at most 64-bit (<c>usize</c>→<c>ulong</c>; arbitrary <c>iN</c>/<c>uN</c> are unsupported), so
/// the true result of an add/sub/mul of two such operands always fits in a 128-bit accumulator
/// (the largest case, <c>ulong * ulong</c>, is below <see cref="System.UInt128"/>'s max). So each
/// operation widens both operands to <see cref="System.Int128"/> / <see cref="System.UInt128"/>,
/// performs the EXACT operation there (no overflow, no exceptions, no division-trap edge cases),
/// and clamps to <c>[T.MinValue, T.MaxValue]</c> before truncating back to <typeparamref name="T"/>.
/// This is branch-light and exception-free — unlike a <c>checked(…)</c>/<c>catch</c> form, the
/// saturating path costs nothing extra when no saturation occurs.
/// </para>
/// <para>
/// Signedness is read from <c>T.IsNegative(T.MinValue)</c> (true for a signed <typeparamref name="T"/>,
/// false for an unsigned one whose <c>MinValue</c> is <c>0</c>) — a per-instantiation constant the
/// JIT folds, so the dead branch is eliminated in the specialized body. Subtraction always uses the
/// SIGNED accumulator: its intermediate can be negative even for an unsigned <typeparamref name="T"/>
/// (<c>u8: 5 -| 10</c>), and a signed clamp with <c>lo == T.MinValue == 0</c> saturates that to
/// <c>0</c> correctly — whereas a <c>UInt128</c> subtraction would wrap to a huge value and
/// mis-saturate to the max.
/// </para>
/// </remarks>
public static class ZigMath
{
    /// <summary>Saturating add — <c>a +| b</c> clamped to <typeparamref name="T"/>'s range.</summary>
    public static T SatAdd<T>(T a, T b)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
        => T.IsNegative(T.MinValue)
            ? ClampSigned<T>(System.Int128.CreateTruncating(a) + System.Int128.CreateTruncating(b))
            : ClampUnsigned<T>(System.UInt128.CreateTruncating(a) + System.UInt128.CreateTruncating(b));

    /// <summary>Saturating subtract — <c>a -| b</c> clamped to <typeparamref name="T"/>'s range.
    /// Always evaluated in the signed accumulator (the difference can be negative even for an
    /// unsigned <typeparamref name="T"/>, whose <c>MinValue</c> of <c>0</c> is then the floor).</summary>
    public static T SatSub<T>(T a, T b)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
        => ClampSigned<T>(System.Int128.CreateTruncating(a) - System.Int128.CreateTruncating(b));

    /// <summary>Saturating multiply — <c>a *| b</c> clamped to <typeparamref name="T"/>'s range.</summary>
    public static T SatMul<T>(T a, T b)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
        => T.IsNegative(T.MinValue)
            ? ClampSigned<T>(System.Int128.CreateTruncating(a) * System.Int128.CreateTruncating(b))
            : ClampUnsigned<T>(System.UInt128.CreateTruncating(a) * System.UInt128.CreateTruncating(b));

    // ---- Overflow-detecting arithmetic (road-to-zig-std B3) — @addWithOverflow & friends.
    // Each returns Zig's `struct { T, u1 }` as a C# `(T, byte)` ValueTuple: the WRAPPED result
    // (truncated to T, exactly like `+%`/`-%`/`*%`) plus a 0/1 overflow flag. Computed EXACTLY in a
    // 128-bit accumulator — every operand the lowering routes here is <= 64-bit (a 128-bit operand is
    // a loud cut, since detecting its overflow would need a still-wider accumulator) — then the flag
    // is set when truncating the exact result back to T changed the value. Signedness is the
    // per-instantiation constant `T.IsNegative(T.MinValue)` (the JIT folds the dead branch), and
    // subtraction always uses the signed accumulator (an unsigned difference can go negative, e.g.
    // `u8: 5 -% 10`) — mirroring the saturating helpers above.

    /// <summary><c>@addWithOverflow(a, b)</c> → <c>.{ a +% b, overflow }</c>.</summary>
    public static (T, byte) AddWithOverflow<T>(T a, T b)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
        => T.IsNegative(T.MinValue)
            ? OverflowSigned<T>(System.Int128.CreateTruncating(a) + System.Int128.CreateTruncating(b))
            : OverflowUnsigned<T>(System.UInt128.CreateTruncating(a) + System.UInt128.CreateTruncating(b));

    /// <summary><c>@subWithOverflow(a, b)</c> → <c>.{ a -% b, overflow }</c>. Always the signed
    /// accumulator (the difference can be negative even for an unsigned <typeparamref name="T"/>).</summary>
    public static (T, byte) SubWithOverflow<T>(T a, T b)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
        => OverflowSigned<T>(System.Int128.CreateTruncating(a) - System.Int128.CreateTruncating(b));

    /// <summary><c>@mulWithOverflow(a, b)</c> → <c>.{ a *% b, overflow }</c>.</summary>
    public static (T, byte) MulWithOverflow<T>(T a, T b)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
        => T.IsNegative(T.MinValue)
            ? OverflowSigned<T>(System.Int128.CreateTruncating(a) * System.Int128.CreateTruncating(b))
            : OverflowUnsigned<T>(System.UInt128.CreateTruncating(a) * System.UInt128.CreateTruncating(b));

    /// <summary><c>@shlWithOverflow(a, shift)</c> → <c>.{ a &lt;&lt;% shift, overflow }</c>; the overflow
    /// bit is set when the shift loses any high bits (truncating the exact 128-bit shift changed the
    /// value). The shift amount is a small non-negative count (Zig's <c>Log2(T)</c>).</summary>
    public static (T, byte) ShlWithOverflow<T>(T a, int shift)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
        => T.IsNegative(T.MinValue)
            ? OverflowSigned<T>(System.Int128.CreateTruncating(a) << shift)
            : OverflowUnsigned<T>(System.UInt128.CreateTruncating(a) << shift);

    // ---- Zig math builtins (road-to-zig-std B3) — each maps to a BCL/generic-math primitive.
    // The Zig front-end coerces both operands to their peer integer type, so C# infers T and the
    // op runs at the right width. @min/@max/@rem/@divTrunc are ordinary; @mod/@divFloor differ from
    // C#'s truncating `%`/`/` for a negative divisor (they follow the DIVISOR's sign / round toward
    // negative infinity); @popCount counts set bits (width-agnostic — leading zero bits add nothing).

    /// <summary><c>@min(a, b)</c> — the lesser operand (Zig's variadic <c>@min</c>, V1 binary).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Min<T>(T a, T b) where T : System.Numerics.IBinaryInteger<T> => a < b ? a : b;

    /// <summary><c>@max(a, b)</c> — the greater operand.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Max<T>(T a, T b) where T : System.Numerics.IBinaryInteger<T> => a > b ? a : b;

    /// <summary><c>@rem(a, b)</c> — truncated remainder (sign of the DIVIDEND) — identical to C#'s
    /// <c>%</c>, wrapped for a uniform builtin surface.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Rem<T>(T a, T b) where T : System.Numerics.IBinaryInteger<T> => a % b;

    /// <summary><c>@divTrunc(a, b)</c> — division rounding toward zero — identical to C#'s <c>/</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T DivTrunc<T>(T a, T b) where T : System.Numerics.IBinaryInteger<T> => a / b;

    /// <summary><c>@mod(a, b)</c> — floored modulo: the result takes the sign of the DIVISOR (unlike
    /// C#'s <c>%</c>, sign of the dividend). For an unsigned <typeparamref name="T"/> the two coincide.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Mod<T>(T a, T b) where T : System.Numerics.IBinaryInteger<T>
    {
        var r = a % b;
        return !T.IsZero(r) && (T.IsNegative(r) != T.IsNegative(b)) ? r + b : r;
    }

    /// <summary><c>@divFloor(a, b)</c> — division rounding toward negative infinity (unlike C#'s <c>/</c>,
    /// toward zero). For non-negative operands the two coincide.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T DivFloor<T>(T a, T b) where T : System.Numerics.IBinaryInteger<T>
    {
        var q = a / b;
        var r = a % b;
        return !T.IsZero(r) && (T.IsNegative(r) != T.IsNegative(b)) ? q - T.One : q;
    }

    /// <summary><c>@popCount(x)</c> — the number of set bits. Width-agnostic: the value's leading
    /// zero bits (in <typeparamref name="T"/>'s storage) contribute nothing, so counting in
    /// <typeparamref name="T"/>'s own width matches Zig's logical width. Returns <c>int</c>
    /// (a small count), as Zig's <c>@popCount</c> yields a suitably-sized integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount<T>(T x) where T : System.Numerics.IBinaryInteger<T>
        => int.CreateTruncating(T.PopCount(x));

    /// <summary><c>@clz(x)</c> — count LEADING zero bits, within <typeparamref name="T"/>'s bit width
    /// (<c>T.LeadingZeroCount</c> counts in T's represented width, so <c>@clz(u8)</c> counts in 8 bits,
    /// <c>@clz(u16)</c> in 16 — matching Zig for the standard widths dotcc maps 1:1). <c>@clz(0)</c> is
    /// the full bit width. Returns <c>int</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clz<T>(T x) where T : System.Numerics.IBinaryInteger<T>
        => int.CreateTruncating(T.LeadingZeroCount(x));

    /// <summary><c>@ctz(x)</c> — count TRAILING zero bits, within <typeparamref name="T"/>'s bit width.
    /// <c>@ctz(0)</c> is the full bit width (<c>T.TrailingZeroCount(T.Zero)</c> yields it). Returns
    /// <c>int</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Ctz<T>(T x) where T : System.Numerics.IBinaryInteger<T>
        => int.CreateTruncating(T.TrailingZeroCount(x));

    /// <summary><c>@byteSwap(x)</c> — reverse the byte order of <typeparamref name="T"/>. Writes the
    /// value's bytes little-endian, reverses them, and reads them back — a generic form that works for
    /// any <see cref="System.Numerics.IBinaryInteger{T}"/> width (exact for the whole-byte standard
    /// widths dotcc maps 1:1; a `u8` byteswap is the identity). The read signedness is derived from
    /// <c>T.AllBitsSet</c> (−1 for a signed <typeparamref name="T"/>, max for unsigned) — immaterial at
    /// the exact width, but the API requires it.</summary>
    public static T ByteSwap<T>(T x) where T : System.Numerics.IBinaryInteger<T>
    {
        var n = x.GetByteCount();
        System.Span<byte> buf = stackalloc byte[n];
        x.WriteLittleEndian(buf);
        buf.Reverse();
        return T.ReadLittleEndian(buf, isUnsigned: !T.IsNegative(T.AllBitsSet));
    }

    /// <summary>The magnitude of an integer as an unsigned 128-bit value — the exact-width backbone of
    /// <c>@abs</c>, which returns the UNSIGNED peer of <c>iN</c> (so <c>@abs(i8 -128) == u8 128</c>,
    /// which a signed <c>Math.Abs</c> would overflow). The lowering casts this result to the operand's
    /// unsigned peer type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static System.UInt128 Abs128<T>(T x) where T : System.Numerics.IBinaryInteger<T>
        => System.UInt128.CreateTruncating(System.Int128.Abs(System.Int128.CreateTruncating(x)));

    /// <summary>Clamp an exact signed 128-bit result to <c>[T.MinValue, T.MaxValue]</c> and
    /// truncate back to <typeparamref name="T"/> (used for every signed op and for all
    /// subtraction).</summary>
    private static T ClampSigned<T>(System.Int128 wide)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
    {
        var lo = System.Int128.CreateTruncating(T.MinValue);
        var hi = System.Int128.CreateTruncating(T.MaxValue);
        if (wide < lo) { return T.MinValue; }
        if (wide > hi) { return T.MaxValue; }
        return T.CreateTruncating(wide);
    }

    /// <summary>Clamp an exact unsigned 128-bit result to <c>[0, T.MaxValue]</c> and truncate back
    /// to <typeparamref name="T"/> (used for unsigned add/mul, whose result is never negative).</summary>
    private static T ClampUnsigned<T>(System.UInt128 wide)
        where T : System.Numerics.IBinaryInteger<T>, System.Numerics.IMinMaxValue<T>
    {
        var hi = System.UInt128.CreateTruncating(T.MaxValue);
        return wide > hi ? T.MaxValue : T.CreateTruncating(wide);
    }

    /// <summary>Truncate an exact SIGNED 128-bit result to <typeparamref name="T"/> and pair it with a
    /// 0/1 overflow flag — set when widening the truncated value back doesn't recover the exact result
    /// (i.e. bits were lost). Used for every signed op, all subtraction, and unsigned results carried
    /// through the signed accumulator.</summary>
    private static (T, byte) OverflowSigned<T>(System.Int128 wide)
        where T : System.Numerics.IBinaryInteger<T>
    {
        T wrapped = T.CreateTruncating(wide);
        return (wrapped, System.Int128.CreateTruncating(wrapped) != wide ? (byte)1 : (byte)0);
    }

    /// <summary>Truncate an exact UNSIGNED 128-bit result to <typeparamref name="T"/> and pair it with a
    /// 0/1 overflow flag (set when truncation lost bits). Used for unsigned add/mul/shl.</summary>
    private static (T, byte) OverflowUnsigned<T>(System.UInt128 wide)
        where T : System.Numerics.IBinaryInteger<T>
    {
        T wrapped = T.CreateTruncating(wide);
        return (wrapped, System.UInt128.CreateTruncating(wrapped) != wide ? (byte)1 : (byte)0);
    }
}
