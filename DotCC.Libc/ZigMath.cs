#nullable enable

namespace DotCC.Libc;

/// <summary>
/// The runtime backing for the Zig front-end's SATURATING arithmetic operators
/// (<c>+| -| *|</c> and their compound forms <c>+|= -|= *|=</c>; Milestone P, part 2).
/// Auto-spliced into every emitted program (the <c>DotCC.Libc/*.cs</c>
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
}
