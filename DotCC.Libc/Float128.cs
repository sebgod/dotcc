#nullable enable

using System;
using System.Globalization;
using System.Numerics;

namespace DotCC.Libc;

/// <summary>
/// IEEE-754 <b>binary128</b> (quadruple precision) — dotcc's MIT, self-contained
/// software <c>_Float128</c> / <c>__float128</c> backing type. Implemented
/// clean-room from the IEEE-754 spec (not ported from any LGPL source).
/// </summary>
/// <remarks>
/// Layout (a single 128-bit value, low limb first in <see cref="UInt128"/>):
/// <list type="bullet">
///   <item>bit 127      — sign</item>
///   <item>bits 126..112 — biased exponent (15 bits, bias 16383)</item>
///   <item>bits 111..0   — trailing significand (112 bits; the leading 1 is
///         implicit for normals, giving 113 bits of precision → ~33.9 decimal
///         digits, <c>LDBL_DIG = 33</c> on platforms where this is
///         <c>long double</c>)</item>
/// </list>
/// Complete: representation, constants, predicates, exact
/// <c>double</c>→binary128 widening, correctly-rounded binary128→<c>double</c>
/// narrowing, sign ops, IEEE comparison, correctly-rounded arithmetic
/// (+ − × ÷ √ fma), the transcendental set, decimal formatting/parsing, and
/// the generic-math interface surface — validated bit-for-bit against gcc's
/// <c>long double</c> (binary128) oracle.
/// </remarks>
public readonly struct Float128 : IEquatable<Float128>, IComparable<Float128>,
    IBinaryFloatingPointIeee754<Float128>, IMinMaxValue<Float128>
{
    private const int MantissaBits = 112;       // stored fraction bits
    private const int ExponentBits = 15;
    private const int ExponentBias = 16383;
    private const int MaxBiasedExponent = 0x7FFF; // 32767 → inf/NaN
    private const int SignShift = 127;
    private const int ExponentShift = 112;

    // The 112-bit fraction mask and the implicit-leading-bit position.
    private static readonly UInt128 MantissaMask = (UInt128.One << MantissaBits) - 1;
    private static readonly UInt128 ImplicitBit = UInt128.One << MantissaBits;

    private readonly UInt128 _bits;

    private Float128(UInt128 bits) => _bits = bits;

    /// <summary>Reinterpret a raw 128-bit pattern as a binary128 value.</summary>
    public static Float128 FromBits(UInt128 bits) => new(bits);

    /// <summary>The raw IEEE-754 binary128 bit pattern.</summary>
    public UInt128 Bits => _bits;

    // ── field decomposition ────────────────────────────────────────────────
    private bool SignBit => (_bits >> SignShift) != UInt128.Zero;
    private int BiasedExponent => (int)((_bits >> ExponentShift) & (UInt128)MaxBiasedExponent);
    private UInt128 TrailingSignificand => _bits & MantissaMask;

    private static UInt128 Assemble(bool sign, int biasedExp, UInt128 trailing)
        => ((sign ? UInt128.One : UInt128.Zero) << SignShift)
         | (((UInt128)(uint)biasedExp & (UInt128)MaxBiasedExponent) << ExponentShift)
         | (trailing & MantissaMask);

    // ── constants ──────────────────────────────────────────────────────────
    public static Float128 Zero => new(UInt128.Zero);
    public static Float128 NegativeZero => new(UInt128.One << SignShift);
    public static Float128 One => new(Assemble(false, ExponentBias, UInt128.Zero));
    public static Float128 PositiveInfinity => new(Assemble(false, MaxBiasedExponent, UInt128.Zero));
    public static Float128 NegativeInfinity => new(Assemble(true, MaxBiasedExponent, UInt128.Zero));
    // Quiet NaN: max exponent, MSB of the fraction set.
    public static Float128 NaN => new(Assemble(false, MaxBiasedExponent, UInt128.One << (MantissaBits - 1)));

    /// <summary>Largest finite binary128 (exponent 0x7FFE, all fraction bits set).</summary>
    private static UInt128 MaxFiniteBits => ((UInt128)(MaxBiasedExponent - 1) << ExponentShift) | MantissaMask;
    public static Float128 MaxValue => new(MaxFiniteBits);
    public static Float128 MinValue => new(MaxFiniteBits | (UInt128.One << SignShift));
    /// <summary>Smallest positive subnormal (.NET <c>Epsilon</c> convention).</summary>
    public static Float128 Epsilon => new(UInt128.One);
    public static Float128 Pi => FromFixedSigned(FpPi);
    public static Float128 Tau => FromFixedSigned(FpPi << 1);
    public static Float128 E => Exp(One);

    // ── predicates ───────────────────────────────────────────────────────────
    public static bool IsNaN(Float128 v) => v.BiasedExponent == MaxBiasedExponent && v.TrailingSignificand != UInt128.Zero;
    public static bool IsInfinity(Float128 v) => v.BiasedExponent == MaxBiasedExponent && v.TrailingSignificand == UInt128.Zero;
    public static bool IsFinite(Float128 v) => v.BiasedExponent != MaxBiasedExponent;
    public static bool IsZero(Float128 v) => (v._bits & ~(UInt128.One << SignShift)) == UInt128.Zero;
    public static bool IsNegative(Float128 v) => v.SignBit;

    // ── sign ops ─────────────────────────────────────────────────────────────
    public static Float128 Negate(Float128 v) => new(v._bits ^ (UInt128.One << SignShift));
    public static Float128 Abs(Float128 v) => new(v._bits & ~(UInt128.One << SignShift));

    // ── double (binary64) → binary128 : always EXACT (no rounding) ───────────
    // binary128 has strictly more exponent range and precision than binary64,
    // so every double — normal, subnormal, zero, inf, NaN — maps exactly.
    public static Float128 FromDouble(double d)
    {
        ulong b = BitConverter.DoubleToUInt64Bits(d);
        bool sign = (b >> 63) != 0;
        int exp = (int)((b >> 52) & 0x7FF);   // biased binary64 exponent
        ulong frac = b & 0x000F_FFFF_FFFF_FFFFUL; // 52-bit fraction

        if (exp == 0x7FF)
        {
            // inf / NaN. Preserve a NaN payload (widened into the high fraction
            // bits) and the quiet bit; map infinity to infinity.
            if (frac == 0) { return sign ? NegativeInfinity : PositiveInfinity; }
            UInt128 widenedNaN = (UInt128)frac << (MantissaBits - 52);
            return new(Assemble(sign, MaxBiasedExponent, widenedNaN));
        }
        if (exp == 0)
        {
            // Zero or binary64 subnormal. A double subnormal is a binary128
            // NORMAL (its magnitude is far inside binary128's normal range),
            // so normalise: shift the leading set bit of the 52-bit fraction
            // up to the implicit position. (This is the case naive libraries
            // get wrong.)
            if (frac == 0) { return sign ? NegativeZero : Zero; }
            int msb = 63 - BitOperations.LeadingZeroCount(frac); // 0..51
            // Unbiased value exponent of a double subnormal: frac * 2^-1074,
            // normalised to 1.f * 2^(msb-1074).
            int unbiased = msb - 1074;
            int biased128 = unbiased + ExponentBias;
            // Drop the leading bit, left-align the remaining fraction to 112 bits.
            ulong fracNoLead = frac & ~(1UL << msb);
            UInt128 trailing = (UInt128)fracNoLead << (MantissaBits - msb);
            return new(Assemble(sign, biased128, trailing));
        }
        // Normal binary64: rebias the exponent, left-align the 52-bit fraction
        // into the 112-bit field.
        int biased = (exp - 1023) + ExponentBias;
        UInt128 trailing128 = (UInt128)frac << (MantissaBits - 52); // <<60
        return new(Assemble(sign, biased, trailing128));
    }

    // ── binary128 → double (binary64) : correctly rounded (nearest, ties even) ─
    public static double ToDouble(Float128 v)
    {
        bool sign = v.SignBit;
        ulong signBit = sign ? (1UL << 63) : 0;
        int exp = v.BiasedExponent;
        UInt128 trailing = v.TrailingSignificand;

        if (exp == MaxBiasedExponent)
        {
            if (trailing == UInt128.Zero)
            {
                return sign ? double.NegativeInfinity : double.PositiveInfinity;
            }
            // NaN — narrow the payload from the top fraction bits, keep it quiet.
            ulong payload = (ulong)(trailing >> (MantissaBits - 52)) & 0x000F_FFFF_FFFF_FFFFUL;
            return BitConverter.UInt64BitsToDouble(signBit | 0x7FF0_0000_0000_0000UL | (1UL << 51) | payload);
        }

        if (exp == 0)
        {
            // binary128 zero or subnormal. The largest binary128 subnormal
            // (~2^-16383) is astronomically smaller than the smallest double
            // subnormal (~2^-1074), so everything here narrows to signed zero.
            return BitConverter.UInt64BitsToDouble(signBit);
        }

        // Normal binary128. Full significand = implicit 1 + 112 trailing bits.
        int unbiased = exp - ExponentBias;
        UInt128 significand = ImplicitBit | trailing; // 113 significant bits

        // Target a binary64 with an implicit-1 significand of 53 bits. The
        // double's unbiased exponent must land in [-1022, 1023] for a normal;
        // below that we build a subnormal; above, we overflow to infinity.
        if (unbiased > 1023)
        {
            return sign ? double.NegativeInfinity : double.PositiveInfinity;
        }

        // Round the 113-bit significand to a binary64 significand,
        // round-to-nearest, ties-to-even. For a normal double we keep 53 bits
        // (1 implicit + 52); for a subnormal we shift further so the value
        // scales at the pinned 2^-1022 exponent.
        int dropBits;          // low significand bits discarded
        int doubleBiasedExp;   // binary64 biased exponent (0 ⇒ subnormal target)
        if (unbiased >= -1022)
        {
            dropBits = MantissaBits - 52;       // 60 → keep 53 bits
            doubleBiasedExp = unbiased + 1023;
        }
        else
        {
            int extra = -1022 - unbiased;        // ≥ 1
            dropBits = (MantissaBits - 52) + extra;
            doubleBiasedExp = 0;
            if (dropBits >= 114)
            {
                // Below half the smallest subnormal → ±0 (round bit lies beyond
                // the 113-bit significand). dropBits == 113 still rounds.
                return BitConverter.UInt64BitsToDouble(signBit);
            }
        }

        UInt128 rounded = RoundShiftRight(significand, dropBits);
        ulong fracOut;
        int finalExp;
        if (doubleBiasedExp == 0)
        {
            // Subnormal target. Rounding can lift it to the smallest normal
            // when the (implicit) bit 52 becomes set — that bit IS the normal's
            // implicit 1, so the exponent steps to 1 with a zero fraction.
            if ((rounded >> 52) != UInt128.Zero)
            {
                finalExp = 1;
                fracOut = (ulong)(rounded & ((UInt128.One << 52) - 1));
            }
            else
            {
                finalExp = 0;
                fracOut = (ulong)rounded;
            }
        }
        else if ((rounded >> 53) != UInt128.Zero)
        {
            // Normal significand carried past 2^53 (e.g. 1.11..1 + 1ulp): the
            // exponent advances and the fraction resets to zero.
            finalExp = doubleBiasedExp + 1;
            fracOut = 0;
            if (finalExp >= 0x7FF)
            {
                return sign ? double.NegativeInfinity : double.PositiveInfinity;
            }
        }
        else
        {
            finalExp = doubleBiasedExp;
            fracOut = (ulong)(rounded & ((UInt128.One << 52) - 1)); // strip implicit 1
        }

        ulong bits = signBit | ((ulong)finalExp << 52) | fracOut;
        return BitConverter.UInt64BitsToDouble(bits);
    }

    /// <summary>
    /// Shift <paramref name="value"/> right by <paramref name="shift"/> bits with
    /// round-to-nearest, ties-to-even on the discarded low bits. The returned
    /// value may be one bit wider than <c>value &gt;&gt; shift</c> when rounding
    /// carries; the caller inspects the top bit to adjust the exponent.
    /// </summary>
    private static UInt128 RoundShiftRight(UInt128 value, int shift)
    {
        if (shift <= 0) { return value; }

        UInt128 kept = value >> shift;
        UInt128 roundBitMask = UInt128.One << (shift - 1);
        bool roundBit = (value & roundBitMask) != UInt128.Zero;
        bool sticky = (value & (roundBitMask - 1)) != UInt128.Zero;

        if (roundBit && (sticky || (kept & UInt128.One) != UInt128.Zero))
        {
            kept += UInt128.One;
        }
        return kept;
    }

    // ── arithmetic ───────────────────────────────────────────────────────────
    // Correctness-first: the value of each finite operand is the exact rational
    // `sig * 2^q` (sig a non-negative integer significand), so add/sub/mul are
    // computed EXACTLY with BigInteger and then rounded once via
    // RoundToBinary128. This is the reference shape; a later brick can swap the
    // BigInteger core for pure UInt128 math, guarded bit-for-bit by the gcc
    // binary128 oracle. Rounding is round-to-nearest, ties-to-even.

    private const int MinNormalExponent = 1 - ExponentBias;           // -16382
    // Quantum exponent of an integer significand: value = sigInt * 2^q.
    private const int SubnormalQuantum = MinNormalExponent - MantissaBits; // -16494

    private static BigInteger ToBig(UInt128 x)
        => ((BigInteger)(ulong)(x >> 64) << 64) | (ulong)x;

    private static UInt128 ToU128(BigInteger b)
        => ((UInt128)(ulong)((b >> 64) & ulong.MaxValue) << 64) | (ulong)(b & ulong.MaxValue);

    /// <summary>Decompose a FINITE value into (sign, integer significand, quantum
    /// exponent q) such that value = (-1)^sign · sig · 2^q. Callers handle NaN /
    /// inf / zero before this.</summary>
    private static void Decompose(Float128 v, out bool sign, out BigInteger sig, out int q)
    {
        sign = v.SignBit;
        int e = v.BiasedExponent;
        UInt128 trailing = v.TrailingSignificand;
        if (e == 0)
        {
            sig = ToBig(trailing);          // subnormal (or zero)
            q = SubnormalQuantum;
        }
        else
        {
            sig = ToBig(ImplicitBit | trailing); // normal: implicit 1 + fraction
            q = e - ExponentBias - MantissaBits;
        }
    }

    /// <summary>
    /// Round the exact value (-1)^<paramref name="sign"/> · <paramref name="mag"/>
    /// · 2^<paramref name="q"/> (mag ≥ 0) to binary128, nearest-ties-to-even.
    /// <paramref name="extraSticky"/> marks a non-zero residual below mag's LSB
    /// (e.g. a division remainder), nudging exact-half cases upward.
    /// </summary>
    private static Float128 RoundToBinary128(bool sign, BigInteger mag, int q, bool extraSticky)
    {
        UInt128 signMask = sign ? (UInt128.One << SignShift) : UInt128.Zero;
        if (mag.IsZero)
        {
            return new Float128(signMask); // signed zero
        }

        int bitlen = (int)mag.GetBitLength();
        int unbiased = q + bitlen - 1;            // exponent of the leading bit
        int biased = unbiased + ExponentBias;

        if (biased >= MaxBiasedExponent)           // overflow → ±inf
        {
            return new Float128(signMask | ((UInt128)MaxBiasedExponent << ExponentShift));
        }

        if (biased >= 1)
        {
            // Normal target: keep 113 significand bits (implicit 1 + 112).
            BigInteger kept = RoundToBits(mag, bitlen - (MantissaBits + 1), extraSticky);
            if ((int)kept.GetBitLength() == MantissaBits + 2)
            {
                // Rounding carried 1.11..1 → 10.00..0: drop a bit, bump exponent.
                kept >>= 1;
                biased++;
                if (biased >= MaxBiasedExponent)
                {
                    return new Float128(signMask | ((UInt128)MaxBiasedExponent << ExponentShift));
                }
            }
            UInt128 trailing = ToU128(kept) & MantissaMask;   // strip implicit 1
            return new Float128(signMask | ((UInt128)(uint)biased << ExponentShift) | trailing);
        }

        // Subnormal / underflow target: value goes on the fixed 2^SubnormalQuantum
        // grid, exponent field 0. frac = round(mag · 2^(q - SubnormalQuantum)).
        BigInteger frac = RoundToBits(mag, SubnormalQuantum - q, extraSticky);
        if ((int)frac.GetBitLength() >= MantissaBits + 1)
        {
            // Rounded up into the smallest normal (frac == 2^112): exponent 1, frac 0.
            return new Float128(signMask | (UInt128.One << ExponentShift));
        }
        return new Float128(signMask | (ToU128(frac) & MantissaMask)); // exp field 0
    }

    /// <summary>
    /// Drop the low <paramref name="dropBits"/> bits of <paramref name="mag"/>
    /// with round-to-nearest, ties-to-even (a non-empty residual carried in
    /// <paramref name="extraSticky"/> counts toward the tie). A non-positive
    /// drop is an exact left shift. The result may be one bit wider on carry.
    /// </summary>
    private static BigInteger RoundToBits(BigInteger mag, int dropBits, bool extraSticky)
    {
        if (dropBits <= 0) { return mag << -dropBits; }
        BigInteger kept = mag >> dropBits;
        BigInteger rem = mag - (kept << dropBits);
        BigInteger half = BigInteger.One << (dropBits - 1);
        bool roundUp = rem > half
            || (rem == half && (extraSticky || !kept.IsEven));
        return roundUp ? kept + BigInteger.One : kept;
    }

    public static Float128 Add(Float128 a, Float128 b)
    {
        // NaN propagates.
        if (IsNaN(a) || IsNaN(b)) { return NaN; }
        if (IsInfinity(a))
        {
            // inf + (-inf) is invalid → NaN; otherwise the infinity wins.
            if (IsInfinity(b) && a.SignBit != b.SignBit) { return NaN; }
            return a;
        }
        if (IsInfinity(b)) { return b; }
        if (IsZero(a) && IsZero(b))
        {
            // -0 + -0 = -0; every other zero combination is +0 (round-nearest).
            return (a.SignBit && b.SignBit) ? NegativeZero : Zero;
        }
        if (IsZero(a)) { return b; }
        if (IsZero(b)) { return a; }

        Decompose(a, out bool sa, out BigInteger ma, out int qa);
        Decompose(b, out bool sb, out BigInteger mb, out int qb);
        int common = Math.Min(qa, qb);
        BigInteger ia = (sa ? -ma : ma) << (qa - common);
        BigInteger ib = (sb ? -mb : mb) << (qb - common);
        BigInteger sum = ia + ib;
        if (sum.IsZero) { return Zero; } // exact cancellation → +0 in round-nearest
        return RoundToBinary128(sum.Sign < 0, BigInteger.Abs(sum), common, extraSticky: false);
    }

    public static Float128 Subtract(Float128 a, Float128 b) => Add(a, Negate(b));

    public static Float128 Multiply(Float128 a, Float128 b)
    {
        bool sign = a.SignBit ^ b.SignBit;
        if (IsNaN(a) || IsNaN(b)) { return NaN; }
        if (IsInfinity(a) || IsInfinity(b))
        {
            // inf · 0 is invalid → NaN; otherwise the product is a signed inf.
            if (IsZero(a) || IsZero(b)) { return NaN; }
            return sign ? NegativeInfinity : PositiveInfinity;
        }
        if (IsZero(a) || IsZero(b)) { return sign ? NegativeZero : Zero; }

        Decompose(a, out _, out BigInteger ma, out int qa);
        Decompose(b, out _, out BigInteger mb, out int qb);
        // Exact product significand; round once. (qa+qb can't overflow int —
        // both are within ±~16500.)
        return RoundToBinary128(sign, ma * mb, qa + qb, extraSticky: false);
    }

    public static Float128 Divide(Float128 a, Float128 b)
    {
        bool sign = a.SignBit ^ b.SignBit;
        if (IsNaN(a) || IsNaN(b)) { return NaN; }
        if (IsInfinity(a))
        {
            if (IsInfinity(b)) { return NaN; }       // inf / inf
            return sign ? NegativeInfinity : PositiveInfinity;
        }
        if (IsInfinity(b)) { return sign ? NegativeZero : Zero; } // finite / inf
        if (IsZero(b))
        {
            if (IsZero(a)) { return NaN; }           // 0 / 0
            return sign ? NegativeInfinity : PositiveInfinity;    // x / 0
        }
        if (IsZero(a)) { return sign ? NegativeZero : Zero; }

        Decompose(a, out _, out BigInteger ma, out int qa);
        Decompose(b, out _, out BigInteger mb, out int qb);
        // Quotient with ~130 significant bits + an exact remainder for sticky.
        // P keeps the scaled numerator wide enough that floor-division yields a
        // quotient with round room regardless of the operands' bit lengths.
        int p = 130 - (int)ma.GetBitLength() + (int)mb.GetBitLength();
        BigInteger num = ma << p;
        BigInteger q = BigInteger.DivRem(num, mb, out BigInteger r);
        return RoundToBinary128(sign, q, qa - qb - p, extraSticky: r != BigInteger.Zero);
    }

    public static Float128 Sqrt(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsZero(x)) { return x; }                 // sqrt(±0) = ±0
        if (x.SignBit) { return NaN; }               // sqrt(negative) = NaN
        if (IsInfinity(x)) { return PositiveInfinity; }

        Decompose(x, out _, out BigInteger m, out int q);
        // Make the radicand exponent even so value = sqrt(m·2^q) factors as
        // sqrt(m')·2^(q'/2) with q' even.
        if ((q & 1) != 0) { m <<= 1; q -= 1; }
        // Scale up by 2^(2k) so floor(sqrt(·)) has ~130 bits, then sqrt.
        int k = 130 - (int)m.GetBitLength() / 2;
        BigInteger n = m << (2 * k);
        BigInteger s = ISqrt(n);                      // floor(sqrt(n))
        bool inexact = s * s != n;                    // residual ⇒ sticky
        // value = sqrt(n)·2^(q/2 - k); sqrt(n) ∈ [s, s+1) ⇒ mag s, sticky inexact.
        return RoundToBinary128(false, s, q / 2 - k, extraSticky: inexact);
    }

    public static Float128 FusedMultiplyAdd(Float128 a, Float128 b, Float128 c)
    {
        if (IsNaN(a) || IsNaN(b) || IsNaN(c)) { return NaN; }
        bool sp = a.SignBit ^ b.SignBit;             // sign of the a·b product
        bool aInf = IsInfinity(a), bInf = IsInfinity(b);
        bool aZero = IsZero(a), bZero = IsZero(b);
        if ((aInf && bZero) || (aZero && bInf)) { return NaN; } // 0·inf invalid
        if (aInf || bInf)
        {
            // Product is a signed infinity; adding an opposite infinity is NaN.
            if (IsInfinity(c) && c.SignBit != sp) { return NaN; }
            return sp ? NegativeInfinity : PositiveInfinity;
        }
        if (IsInfinity(c)) { return c; }             // finite·finite + inf = inf
        if (aZero || bZero)
        {
            // Product is ±0; result is that signed zero combined with c.
            return Add(sp ? NegativeZero : Zero, c);
        }

        Decompose(a, out _, out BigInteger ma, out int qa);
        Decompose(b, out _, out BigInteger mb, out int qb);
        BigInteger mp = ma * mb;                      // EXACT product significand
        int qp = qa + qb;
        if (IsZero(c)) { return RoundToBinary128(sp, mp, qp, extraSticky: false); }

        Decompose(c, out bool sc, out BigInteger mc, out int qc);
        int common = Math.Min(qp, qc);
        BigInteger ip = (sp ? -mp : mp) << (qp - common);
        BigInteger ic = (sc ? -mc : mc) << (qc - common);
        BigInteger sum = ip + ic;                     // EXACT a·b + c
        if (sum.IsZero) { return Zero; }
        return RoundToBinary128(sum.Sign < 0, BigInteger.Abs(sum), common, extraSticky: false);
    }

    // ── exact integral / algebraic functions (correctly rounded ⇒ bit-exact) ─
    public static Float128 Truncate(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; }
        Decompose(x, out bool s, out BigInteger m, out int q);
        if (q >= 0) { return x; }                    // already integral
        int shift = -q;
        if (shift >= (int)m.GetBitLength()) { return s ? NegativeZero : Zero; } // |x| < 1
        return RoundToBinary128(s, m >> shift, 0, extraSticky: false); // drop fraction → toward 0
    }

    public static Float128 Floor(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; }
        Float128 t = Truncate(x);
        // Negative non-integers truncate up (toward 0), so step down to -inf.
        return (x.SignBit && t != x) ? Subtract(t, One) : t;
    }

    public static Float128 Ceiling(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; }
        Float128 t = Truncate(x);
        return (!x.SignBit && t != x) ? Add(t, One) : t;
    }

    /// <summary>Round to nearest integer, ties-to-even (matches C <c>rintl</c>
    /// under the default rounding mode).</summary>
    public static Float128 Round(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; }
        Decompose(x, out bool s, out BigInteger m, out int q);
        if (q >= 0) { return x; }
        int shift = -q;
        int bits = (int)m.GetBitLength();
        if (shift > bits) { return s ? NegativeZero : Zero; } // |x| < 0.5
        BigInteger kept = m >> shift;
        BigInteger rem = m - (kept << shift);
        BigInteger half = BigInteger.One << (shift - 1);
        if (rem > half || (rem == half && !kept.IsEven)) { kept += BigInteger.One; }
        return kept.IsZero ? (s ? NegativeZero : Zero) : RoundToBinary128(s, kept, 0, false);
    }

    /// <summary>Magnitude of <paramref name="x"/>, sign of <paramref name="y"/>.</summary>
    public static Float128 CopySign(Float128 x, Float128 y)
    {
        UInt128 signMask = UInt128.One << SignShift;
        return new Float128((x._bits & ~signMask) | (y._bits & signMask));
    }

    /// <summary>C <c>fmod</c>: x − trunc(x/y)·y, sign of x. Exact (no rounding).</summary>
    public static Float128 Fmod(Float128 x, Float128 y)
    {
        if (IsNaN(x) || IsNaN(y) || IsInfinity(x) || IsZero(y)) { return NaN; }
        if (IsInfinity(y) || IsZero(x)) { return x; }
        bool s = x.SignBit;
        Decompose(x, out _, out BigInteger mx, out int qx);
        Decompose(y, out _, out BigInteger my, out int qy);
        int c = Math.Min(qx, qy);
        BigInteger r = (mx << (qx - c)) % (my << (qy - c));   // exact remainder
        if (r.IsZero) { return s ? NegativeZero : Zero; }
        return RoundToBinary128(s, r, c, extraSticky: false); // r exact ⇒ identity
    }

    public static Float128 Cbrt(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsZero(x) || IsInfinity(x)) { return x; } // cbrt(±0)=±0, cbrt(±inf)=±inf
        bool s = x.SignBit;                            // cbrt preserves sign
        Decompose(x, out _, out BigInteger m, out int q);
        // Make q a multiple of 3 so value = cbrt(m')·2^(q'/3).
        while ((((q % 3) + 3) % 3) != 0) { m <<= 1; q--; }
        int k = 130 - (int)m.GetBitLength() / 3;
        BigInteger n = m << (3 * k);
        BigInteger c = ICbrt(n);
        return RoundToBinary128(s, c, q / 3 - k, extraSticky: c * c * c != n);
    }

    public static Float128 Hypot(Float128 x, Float128 y)
    {
        if (IsInfinity(x) || IsInfinity(y)) { return PositiveInfinity; } // even if other is NaN
        if (IsNaN(x) || IsNaN(y)) { return NaN; }
        if (IsZero(x)) { return Abs(y); }
        if (IsZero(y)) { return Abs(x); }
        Decompose(x, out _, out BigInteger mx, out int qx);
        Decompose(y, out _, out BigInteger my, out int qy);
        // x² + y² computed EXACTLY at a common exponent, then correctly-rounded
        // sqrt — so hypot itself is correctly rounded.
        int c = Math.Min(2 * qx, 2 * qy);
        BigInteger sum = ((mx * mx) << (2 * qx - c)) + ((my * my) << (2 * qy - c));
        if ((c & 1) != 0) { sum <<= 1; c--; }          // even exponent for sqrt
        int k = 130 - (int)sum.GetBitLength() / 2;
        BigInteger n = sum << (2 * k);
        BigInteger sq = ISqrt(n);
        return RoundToBinary128(false, sq, c / 2 - k, extraSticky: sq * sq != n);
    }

    /// <summary>Integer floor-cube-root of a non-negative BigInteger.</summary>
    private static BigInteger ICbrt(BigInteger n)
    {
        if (n <= BigInteger.Zero) { return BigInteger.Zero; }
        BigInteger x = BigInteger.One << (((int)n.GetBitLength() + 2) / 3);
        while (true)
        {
            BigInteger x2 = (2 * x + n / (x * x)) / 3;
            if (x2 >= x) { break; }
            x = x2;
        }
        while (x * x * x > n) { x -= BigInteger.One; }
        while ((x + BigInteger.One) * (x + BigInteger.One) * (x + BigInteger.One) <= n) { x += BigInteger.One; }
        return x;
    }

    /// <summary>x · 2ⁿ (C <c>scalbn</c>), with overflow/underflow rounding.</summary>
    public static Float128 ScaleB(Float128 x, int n)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        return RoundToBinary128(s, sig, q + n, extraSticky: false);
    }

    /// <summary>floor(log₂|x|) as an int (C <c>ilogb</c>). 0/NaN → int.MinValue,
    /// inf → int.MaxValue.</summary>
    public static int ILogB(Float128 x)
    {
        if (IsNaN(x) || IsZero(x)) { return int.MinValue; }
        if (IsInfinity(x)) { return int.MaxValue; }
        Decompose(x, out _, out BigInteger sig, out int q);
        return q + (int)sig.GetBitLength() - 1;
    }

    /// <summary>IEEE remainder x − n·y, n = round-to-nearest-even(x/y). Exact.</summary>
    public static Float128 Ieee754Remainder(Float128 x, Float128 y)
    {
        if (IsNaN(x) || IsNaN(y) || IsInfinity(x) || IsZero(y)) { return NaN; }
        if (IsInfinity(y) || IsZero(x)) { return x; }
        Decompose(x, out bool xs, out BigInteger mx, out int qx);
        Decompose(y, out _, out BigInteger my, out int qy);
        int c = Math.Min(qx, qy);
        BigInteger bigX = (xs ? -mx : mx) << (qx - c);
        BigInteger absY = my << (qy - c);
        BigInteger n = BigInteger.DivRem(bigX, absY, out BigInteger rem);
        BigInteger twice = BigInteger.Abs(rem) << 1;
        if (twice > absY || (twice == absY && !n.IsEven)) { n += bigX.Sign >= 0 ? 1 : -1; }
        BigInteger r = bigX - n * absY;
        if (r.IsZero) { return xs ? NegativeZero : Zero; }
        return RoundToBinary128(r.Sign < 0, BigInteger.Abs(r), c, extraSticky: false);
    }

    /// <summary>Next representable value toward +∞ (.NET <c>BitIncrement</c>).</summary>
    public static Float128 BitIncrement(Float128 x)
    {
        if (IsNaN(x)) { return x; }
        UInt128 signMask = UInt128.One << SignShift;
        if (IsInfinity(x)) { return x.SignBit ? new Float128(signMask | MaxFiniteBits) : x; }
        if (IsZero(x)) { return Epsilon; }
        return x.SignBit ? new Float128(x._bits - 1) : new Float128(x._bits + 1);
    }

    /// <summary>Next representable value toward −∞ (.NET <c>BitDecrement</c>).</summary>
    public static Float128 BitDecrement(Float128 x) => Negate(BitIncrement(Negate(x)));

    /// <summary>n-th root x^(1/n). sqrt/cbrt for n=2/3; otherwise exp(log|x|/n)
    /// with sign handling for odd roots of negatives.</summary>
    public static Float128 RootN(Float128 x, int n)
    {
        if (n == 2) { return Sqrt(x); }
        if (n == 3) { return Cbrt(x); }
        if (IsNaN(x)) { return NaN; }
        bool negBase = x.SignBit && !IsZero(x);
        if (negBase && (n & 1) == 0) { return NaN; } // even root of a negative
        Float128 mag = Pow(Abs(x), Divide(One, FromInt64(n)));
        return (negBase && (n & 1) == 1) ? Negate(mag) : mag;
    }

    /// <summary>Integer floor-sqrt of a non-negative BigInteger (Newton's
    /// method with a correcting clamp).</summary>
    private static BigInteger ISqrt(BigInteger n)
    {
        if (n <= BigInteger.Zero) { return BigInteger.Zero; }
        BigInteger x = BigInteger.One << (((int)n.GetBitLength() + 1) / 2);
        while (true)
        {
            BigInteger y = (x + n / x) >> 1;
            if (y >= x) { break; }
            x = y;
        }
        while (x * x > n) { x -= BigInteger.One; }
        while ((x + BigInteger.One) * (x + BigInteger.One) <= n) { x += BigInteger.One; }
        return x;
    }

    // ── integer conversions ─────────────────────────────────────────────────
    /// <summary>Exact widening from a 64-bit integer (every long fits in the
    /// 113-bit significand).</summary>
    public static Float128 FromInt64(long v)
    {
        if (v == 0) { return Zero; }
        return RoundToBinary128(v < 0, BigInteger.Abs((BigInteger)v), 0, extraSticky: false);
    }

    /// <summary>Narrow to a 64-bit integer, truncating toward zero (C cast
    /// semantics). NaN → 0; out-of-range / infinities saturate to the long
    /// bounds (the C standard leaves these cases undefined).</summary>
    public static long ToInt64(Float128 v)
    {
        if (IsNaN(v)) { return 0; }
        if (IsInfinity(v)) { return v.SignBit ? long.MinValue : long.MaxValue; }
        if (IsZero(v)) { return 0; }
        Decompose(v, out bool sign, out BigInteger sig, out int q);
        BigInteger ival = q >= 0 ? sig << q : sig >> -q; // drop fraction → toward zero
        if (sign) { ival = -ival; }
        if (ival > long.MaxValue) { return long.MaxValue; }
        if (ival < long.MinValue) { return long.MinValue; }
        return (long)ival;
    }

    // ── operators & conversions (so emitted C# `_Float128` code Just Works) ──
    public static Float128 operator +(Float128 a, Float128 b) => Add(a, b);
    public static Float128 operator -(Float128 a, Float128 b) => Subtract(a, b);
    public static Float128 operator *(Float128 a, Float128 b) => Multiply(a, b);
    public static Float128 operator /(Float128 a, Float128 b) => Divide(a, b);
    public static Float128 operator -(Float128 a) => Negate(a);
    public static Float128 operator +(Float128 a) => a;

    // Relational operators (IEEE: a NaN operand makes every one false).
    public static bool operator <(Float128 a, Float128 b) => Compare(a, b) is int c && c < 0;
    public static bool operator >(Float128 a, Float128 b) => Compare(a, b) is int c && c > 0;
    public static bool operator <=(Float128 a, Float128 b) => Compare(a, b) is int c && c <= 0;
    public static bool operator >=(Float128 a, Float128 b) => Compare(a, b) is int c && c >= 0;

    // C widening conversions are implicit; narrowing ones explicit.
    public static implicit operator Float128(long v) => FromInt64(v);
    public static implicit operator Float128(double d) => FromDouble(d);
    public static explicit operator double(Float128 v) => ToDouble(v);
    public static explicit operator long(Float128 v) => ToInt64(v);
    public static explicit operator int(Float128 v) => (int)ToInt64(v);

    // ── comparison (IEEE: NaN unordered, +0 == -0) ───────────────────────────
    public bool Equals(Float128 other)
    {
        if (IsNaN(this) || IsNaN(other)) { return false; }
        if (IsZero(this) && IsZero(other)) { return true; } // +0 == -0
        return _bits == other._bits;
    }

    public override bool Equals(object? obj) => obj is Float128 f && Equals(f);

    // Hash on the canonicalised bit pattern so +0/-0 collide; NaNs hash by bits.
    public override int GetHashCode()
    {
        if (IsZero(this)) { return 0; }
        return _bits.GetHashCode();
    }

    public static bool operator ==(Float128 a, Float128 b) => a.Equals(b);
    public static bool operator !=(Float128 a, Float128 b) => !a.Equals(b);

    /// <summary>IEEE ordering: −1 / 0 / +1, or <c>null</c> when unordered (a
    /// NaN operand). +0 and −0 compare equal.</summary>
    private static int? Compare(Float128 a, Float128 b)
    {
        if (IsNaN(a) || IsNaN(b)) { return null; }
        if (IsZero(a) && IsZero(b)) { return 0; }      // +0 == -0
        if (a.SignBit != b.SignBit) { return a.SignBit ? -1 : 1; } // negatives < positives
        // Same sign: the low 127 bits (exponent then mantissa) are monotonic in
        // magnitude. For negatives, larger magnitude means smaller value.
        UInt128 magMask = ~(UInt128.One << SignShift);
        int cmp = (a._bits & magMask).CompareTo(b._bits & magMask);
        return a.SignBit ? -cmp : cmp;
    }

    /// <summary>Total-order comparison for sorting (.NET <see cref="IComparable{T}"/>
    /// convention: NaN sorts below everything and equal to itself), distinct
    /// from the IEEE relational operators where NaN is unordered.</summary>
    public int CompareTo(Float128 other)
    {
        if (Compare(this, other) is int c) { return c; }
        bool meNaN = IsNaN(this), otherNaN = IsNaN(other);
        if (meNaN && otherNaN) { return 0; }
        return meNaN ? -1 : 1;
    }

    // ── transcendentals (high-precision BigInteger fixed-point) ──────────────
    // Computed in a fixed-point format with FpBits fraction bits (well beyond
    // binary128's 113), then rounded to binary128. The ~80 guard bits make the
    // final rounding correct to far better than 1 ULP for essentially all
    // inputs — and since glibc's own transcendentals aren't correctly rounded,
    // these are validated against gcc within a small ULP tolerance, not bit-
    // exact. (Argument reduction keeps the series arguments small; the result
    // is irrational so a definite sticky direction can't be claimed — the guard
    // bits carry the rounding instead.)
    private const int FpBits = 192;
    private static readonly BigInteger FpOne = BigInteger.One << FpBits;
    private static readonly BigInteger FpLn2 = ComputeLn2();
    private static readonly BigInteger FpLn10 = ComputeLn10();

    private static BigInteger FpMul(BigInteger a, BigInteger b) => (a * b) >> FpBits;

    /// <summary>round(a / b) to nearest, ties away (b &gt; 0; a signed).</summary>
    private static BigInteger DivNearest(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        BigInteger twice = BigInteger.Abs(r) << 1;
        if (twice >= b) { q += a.Sign >= 0 ? BigInteger.One : BigInteger.MinusOne; }
        return q;
    }

    /// <summary>Exact value (-1)^neg · mag · 2^q as a fixed-point integer
    /// round(value · 2^FpBits).</summary>
    private static BigInteger ToFixed(bool neg, BigInteger mag, int q)
    {
        int shift = q + FpBits;
        BigInteger r = shift >= 0 ? mag << shift : DivNearest(mag, BigInteger.One << -shift);
        return neg ? -r : r;
    }

    /// <summary>atanh(t) in fixed-point: t + t³/3 + t⁵/5 + … (|t| small).</summary>
    private static BigInteger FpAtanh(BigInteger t)
    {
        BigInteger tsq = FpMul(t, t);
        BigInteger term = t, acc = t;
        // Terminate on the CONTRIBUTION reaching zero, not `term`: BigInteger
        // `>>` floors, so for negative t a tiny `term` sticks at −1 and never
        // hits 0 — `term/(2n+1)` does truncate to 0. (200-iteration backstop.)
        for (int n = 1; n < 200; n++)
        {
            term = FpMul(term, tsq);
            BigInteger contribution = term / (2 * n + 1);
            if (contribution == BigInteger.Zero) { break; }
            acc += contribution;
        }
        return acc;
    }

    private static BigInteger ComputeLn2() => 2 * FpAtanh((BigInteger.One << FpBits) / 3);

    // ln10 = ln(2^3 · 1.25) = 3·ln2 + ln(1.25), ln(1.25) = 2·atanh(1/9).
    private static BigInteger ComputeLn10() => 3 * FpLn2 + 2 * FpAtanh((BigInteger.One << FpBits) / 9);

    /// <summary>exp(r) in fixed-point for small |r| (≤ ln2/2) via Taylor series.</summary>
    private static BigInteger FpExpSmall(BigInteger r)
    {
        BigInteger term = FpOne, acc = FpOne;
        int n = 1;
        while (n < 200)
        {
            term = FpMul(term, r) / n;       // term *= r/n
            if (term == BigInteger.Zero) { break; }
            acc += term;
            n++;
        }
        return acc;                           // ≈ exp(r) · 2^FpBits, positive
    }

    /// <summary>exp(arg·2⁻ᶠᵖᴮⁱᵗˢ) → binary128, via range reduction k·ln2 + r.</summary>
    private static Float128 ExpOfFixed(BigInteger arg)
    {
        BigInteger k = DivNearest(arg, FpLn2);             // round(arg / ln2)
        if (k > 20000) { return PositiveInfinity; }
        if (k < -20000) { return Zero; }
        BigInteger r = arg - k * FpLn2;                    // |r| ≤ ln2/2
        return RoundToBinary128(false, FpExpSmall(r), (int)k - FpBits, extraSticky: false);
    }

    /// <summary>log(x)·2ᶠᵖᴮⁱᵗˢ for finite positive x. x = m·2ᵉ, m ∈ [1,2);
    /// log = e·ln2 + 2·atanh((m−1)/(m+1)).</summary>
    private static BigInteger FpLogFixed(Float128 x)
    {
        Decompose(x, out _, out BigInteger sig, out int q);
        int bitlen = (int)sig.GetBitLength();
        int e = q + bitlen - 1;
        BigInteger mFixed = sig << (FpBits - bitlen + 1);
        BigInteger t = ((mFixed - FpOne) << FpBits) / (mFixed + FpOne);
        return (BigInteger)e * FpLn2 + 2 * FpAtanh(t);
    }

    public static Float128 Exp(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsInfinity(x)) { return x.SignBit ? Zero : PositiveInfinity; }
        if (IsZero(x)) { return One; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        return ExpOfFixed(ToFixed(s, sig, q));
    }

    public static Float128 Exp2(Float128 x)   // 2^x = exp(x·ln2)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsInfinity(x)) { return x.SignBit ? Zero : PositiveInfinity; }
        if (IsZero(x)) { return One; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        return ExpOfFixed(FpMul(ToFixed(s, sig, q), FpLn2));
    }

    public static Float128 Exp10(Float128 x)  // 10^x = exp(x·ln10)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsInfinity(x)) { return x.SignBit ? Zero : PositiveInfinity; }
        if (IsZero(x)) { return One; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        return ExpOfFixed(FpMul(ToFixed(s, sig, q), FpLn10));
    }

    public static Float128 Expm1(Float128 x)  // exp(x) − 1, accurate near 0
    {
        if (IsNaN(x)) { return NaN; }
        if (IsInfinity(x)) { return x.SignBit ? Negate(One) : PositiveInfinity; }
        if (IsZero(x)) { return x; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        BigInteger xFixed = ToFixed(s, sig, q);
        BigInteger k = DivNearest(xFixed, FpLn2);
        if (k == BigInteger.Zero)
        {
            // |x| ≤ ln2/2: form exp(x)−1 in fixed-point (no cancellation loss).
            BigInteger d = FpExpSmall(xFixed) - FpOne;
            return d.IsZero ? Zero : RoundToBinary128(d.Sign < 0, BigInteger.Abs(d), -FpBits, false);
        }
        if (k > 20000) { return PositiveInfinity; }
        if (k < -20000) { return Negate(One); }            // exp→0 ⇒ expm1→−1
        return Subtract(ExpOfFixed(xFixed), One);
    }

    public static Float128 Log(Float128 x)
    {
        if (IsNaN(x) || x.SignBit) { return IsZero(x) ? NegativeInfinity : NaN; }
        if (IsZero(x)) { return NegativeInfinity; }
        if (IsInfinity(x)) { return PositiveInfinity; }
        BigInteger lf = FpLogFixed(x);
        return lf.IsZero ? Zero : RoundToBinary128(lf.Sign < 0, BigInteger.Abs(lf), -FpBits, false);
    }

    public static Float128 Log2(Float128 x)   // log(x)/ln2
    {
        if (IsNaN(x) || x.SignBit) { return IsZero(x) ? NegativeInfinity : NaN; }
        if (IsZero(x)) { return NegativeInfinity; }
        if (IsInfinity(x)) { return PositiveInfinity; }
        BigInteger l2 = DivNearest(FpLogFixed(x) << FpBits, FpLn2);
        return l2.IsZero ? Zero : RoundToBinary128(l2.Sign < 0, BigInteger.Abs(l2), -FpBits, false);
    }

    public static Float128 Log10(Float128 x)  // log(x)/ln10
    {
        if (IsNaN(x) || x.SignBit) { return IsZero(x) ? NegativeInfinity : NaN; }
        if (IsZero(x)) { return NegativeInfinity; }
        if (IsInfinity(x)) { return PositiveInfinity; }
        BigInteger l10 = DivNearest(FpLogFixed(x) << FpBits, FpLn10);
        return l10.IsZero ? Zero : RoundToBinary128(l10.Sign < 0, BigInteger.Abs(l10), -FpBits, false);
    }

    public static Float128 Log1p(Float128 x)  // log(1+x), accurate near 0
    {
        if (IsNaN(x)) { return NaN; }
        if (IsInfinity(x)) { return x.SignBit ? NaN : PositiveInfinity; }
        if (IsZero(x)) { return x; }
        Float128 negOne = Negate(One);
        if (x <= negOne) { return x == negOne ? NegativeInfinity : NaN; }
        // For |x| < 1/2, log(1+x) = 2·atanh(x/(2+x)) keeps full precision near 0.
        if (Abs(x) < FromDouble(0.5))
        {
            Decompose(x, out bool s, out BigInteger sig, out int q);
            BigInteger xFixed = ToFixed(s, sig, q);
            BigInteger t = (xFixed << FpBits) / (2 * FpOne + xFixed);
            BigInteger lf = 2 * FpAtanh(t);
            return lf.IsZero ? Zero : RoundToBinary128(lf.Sign < 0, BigInteger.Abs(lf), -FpBits, false);
        }
        return Log(Add(x, One));
    }

    public static Float128 Pow(Float128 x, Float128 y)
    {
        if (IsZero(y)) { return One; }                     // pow(anything, ±0) = 1
        if (x == One) { return One; }                      // pow(1, anything) = 1
        if (IsNaN(x) || IsNaN(y)) { return NaN; }

        bool yInt = y.Equals(Truncate(y));
        bool yOdd = yInt && Fmod(Abs(y), FromInt64(2)) == One;
        bool xNeg = x.SignBit;

        if (IsZero(x))
        {
            if (y.SignBit) { return (xNeg && yOdd) ? NegativeInfinity : PositiveInfinity; }
            return (xNeg && yOdd) ? NegativeZero : Zero;
        }
        if (IsInfinity(y))
        {
            Float128 ax = Abs(x);
            if (ax == One) { return One; }                 // pow(±1, ±inf) = 1
            return (ax > One) != y.SignBit ? PositiveInfinity : Zero;
        }
        if (IsInfinity(x))
        {
            if (y.SignBit) { return (xNeg && yOdd) ? NegativeZero : Zero; }
            return (xNeg && yOdd) ? NegativeInfinity : PositiveInfinity;
        }
        if (xNeg && !yInt) { return NaN; }                 // negative base, non-integer exponent

        // |x|^y = exp(y · log|x|).
        Decompose(y, out bool ys, out BigInteger ysig, out int yq);
        BigInteger arg = FpMul(ToFixed(ys, ysig, yq), FpLogFixed(Abs(x)));
        Float128 mag = ExpOfFixed(arg);
        return (xNeg && yOdd) ? Negate(mag) : mag;
    }

    // ── trigonometric functions (fixed-point, π range reduction) ─────────────
    private static readonly BigInteger FpPi = ComputePi();
    private static readonly BigInteger FpHalfPi = FpPi >> 1;

    // Machin: π = 16·atan(1/5) − 4·atan(1/239).
    private static BigInteger ComputePi()
        => 16 * FpAtan(FpOne / 5) - 4 * FpAtan(FpOne / 239);

    /// <summary>fixed-point sqrt of a fixed-point value (a·2⁻ᶠᵖᴮⁱᵗˢ ⇒ √a·2⁻ᶠᵖᴮⁱᵗˢ).</summary>
    private static BigInteger FpSqrt(BigInteger aFixed) => ISqrt(aFixed << FpBits);

    /// <summary>atan(t) in fixed-point for any t: halve the argument via
    /// atan(t)=2·atan(t/(1+√(1+t²))) until small, sum the (alternating) series,
    /// then scale back by 2ᵏ.</summary>
    private static BigInteger FpAtan(BigInteger t)
    {
        int k = 0;
        BigInteger tt = t;
        while (BigInteger.Abs(tt) > (FpOne >> 4))      // until |tt| ≤ 1/16
        {
            BigInteger s = FpSqrt(FpOne + FpMul(tt, tt));
            tt = (tt << FpBits) / (FpOne + s);
            k++;
        }
        BigInteger tsq = FpMul(tt, tt), term = tt, acc = tt;
        int sign = -1;
        for (int n = 1; n < 200; n++)
        {
            term = FpMul(term, tsq);
            BigInteger c = term / (2 * n + 1);
            if (c == BigInteger.Zero) { break; }
            acc += sign * c;
            sign = -sign;
        }
        return acc << k;
    }

    /// <summary>(sin r, cos r) in fixed-point for |r| ≤ π/4 via Taylor series.</summary>
    private static (BigInteger Sin, BigInteger Cos) FpSinCos(BigInteger r)
    {
        BigInteger rsq = FpMul(r, r);
        BigInteger sinAcc = r, cosAcc = FpOne, termS = r, termC = FpOne;
        for (int k = 1; k < 100; k++)
        {
            termC = -FpMul(termC, rsq) / ((BigInteger)(2 * k - 1) * (2 * k));
            termS = -FpMul(termS, rsq) / ((BigInteger)(2 * k) * (2 * k + 1));
            cosAcc += termC;
            sinAcc += termS;
            if (termC == BigInteger.Zero && termS == BigInteger.Zero) { break; }
        }
        return (sinAcc, cosAcc);
    }

    // Reduce x (fixed) to quadrant k mod 4 and r ∈ [−π/4, π/4]; return sin/cos of r.
    private static (int Quadrant, BigInteger Sin, BigInteger Cos) ReduceTrig(BigInteger xFixed)
    {
        BigInteger k = DivNearest(xFixed, FpHalfPi);
        BigInteger r = xFixed - k * FpHalfPi;          // |r| ≤ π/4
        var (sin, cos) = FpSinCos(r);
        int quad = (int)(((k % 4) + 4) % 4);
        return (quad, sin, cos);
    }

    private static Float128 FromFixedSigned(BigInteger v)
        => v.IsZero ? Zero : RoundToBinary128(v.Sign < 0, BigInteger.Abs(v), -FpBits, false);

    public static Float128 Sin(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x)) { return NaN; }
        if (IsZero(x)) { return x; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        var (quad, sin, cos) = ReduceTrig(ToFixed(s, sig, q));
        return FromFixedSigned(quad switch { 0 => sin, 1 => cos, 2 => -sin, _ => -cos });
    }

    public static Float128 Cos(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x)) { return NaN; }
        if (IsZero(x)) { return One; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        var (quad, sin, cos) = ReduceTrig(ToFixed(s, sig, q));
        return FromFixedSigned(quad switch { 0 => cos, 1 => -sin, 2 => -cos, _ => sin });
    }

    public static Float128 Tan(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x)) { return NaN; }
        if (IsZero(x)) { return x; }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        var (quad, sin, cos) = ReduceTrig(ToFixed(s, sig, q));
        // tan repeats every quadrant as sin/cos with a swap+sign on odd quadrants.
        BigInteger num = (quad & 1) == 0 ? sin : cos;
        BigInteger den = (quad & 1) == 0 ? cos : -sin;
        if (den.IsZero) { return num.Sign < 0 ? NegativeInfinity : PositiveInfinity; }
        return FromFixedSigned((num << FpBits) / den);
    }

    public static Float128 Atan(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsZero(x)) { return x; }
        if (IsInfinity(x)) { return FromFixedSigned(x.SignBit ? -FpHalfPi : FpHalfPi); }
        Decompose(x, out bool s, out BigInteger sig, out int q);
        return FromFixedSigned(FpAtan(ToFixed(s, sig, q)));
    }

    public static Float128 Asin(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsZero(x)) { return x; }
        Float128 ax = Abs(x);
        if (ax > One) { return NaN; }
        if (ax == One) { return FromFixedSigned(x.SignBit ? -FpHalfPi : FpHalfPi); }
        // asin(x) = atan(x / sqrt(1 − x²)).
        Decompose(x, out bool s, out BigInteger sig, out int q);
        BigInteger xf = ToFixed(s, sig, q);
        BigInteger denom = FpSqrt(FpOne - FpMul(xf, xf));
        return FromFixedSigned(FpAtan((xf << FpBits) / denom));
    }

    public static Float128 Acos(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        Float128 ax = Abs(x);
        if (ax > One) { return NaN; }
        // acos(x) = π/2 − asin(x), formed in fixed-point.
        Decompose(x, out bool s, out BigInteger sig, out int q);
        BigInteger xf = ToFixed(s, sig, q);
        BigInteger asin = ax == One
            ? (x.SignBit ? -FpHalfPi : FpHalfPi)
            : FpAtan((xf << FpBits) / FpSqrt(FpOne - FpMul(xf, xf)));
        return FromFixedSigned(FpHalfPi - asin);
    }

    public static Float128 Atan2(Float128 y, Float128 x)
    {
        if (IsNaN(y) || IsNaN(x)) { return NaN; }
        bool ys = y.SignBit, xs = x.SignBit;
        if (IsZero(y))
        {
            // atan2(±0, x): x≥0 (incl. +inf) → ±0; x<0 (incl. −inf) → ±π.
            if (!xs) { return y; }
            return FromFixedSigned(ys ? -FpPi : FpPi);
        }
        if (IsZero(x)) { return FromFixedSigned(ys ? -FpHalfPi : FpHalfPi); }
        if (IsInfinity(x) || IsInfinity(y))
        {
            // Standard atan2 infinity lattice.
            if (IsInfinity(x) && IsInfinity(y))
            {
                BigInteger oct = xs ? (3 * FpPi) >> 2 : FpPi >> 2; // 3π/4 or π/4
                return FromFixedSigned(ys ? -oct : oct);
            }
            if (IsInfinity(y)) { return FromFixedSigned(ys ? -FpHalfPi : FpHalfPi); }
            // x infinite, y finite: x>0 → ±0, x<0 → ±π.
            return xs ? FromFixedSigned(ys ? -FpPi : FpPi) : (ys ? NegativeZero : Zero);
        }
        // Finite, nonzero. base = atan(y/x); adjust by quadrant of x.
        Decompose(y, out bool yds, out BigInteger ysig, out int yq);
        Decompose(x, out bool xds, out BigInteger xsig, out int xq);
        BigInteger yf = ToFixed(yds, ysig, yq), xf = ToFixed(xds, xsig, xq);
        BigInteger atanRatio = FpAtan((yf << FpBits) / xf);
        if (!xs) { return FromFixedSigned(atanRatio); }           // x>0
        return FromFixedSigned(ys ? atanRatio - FpPi : atanRatio + FpPi); // x<0
    }

    // ── hyperbolic functions (composed on exp/expm1/log1p/sqrt) ──────────────
    public static Float128 Sinh(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; }
        // (expm1(x) − expm1(−x))/2 — opposite signs, so no cancellation near 0.
        return Multiply(Subtract(Expm1(x), Expm1(Negate(x))), FromDouble(0.5));
    }

    public static Float128 Cosh(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsInfinity(x)) { return PositiveInfinity; }
        return Multiply(Add(Exp(x), Exp(Negate(x))), FromDouble(0.5));
    }

    public static Float128 Tanh(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsInfinity(x)) { return x.SignBit ? Negate(One) : One; }
        if (IsZero(x)) { return x; }
        // tanh = expm1(2x) / (expm1(2x) + 2).
        Float128 e2 = Expm1(Multiply(FromInt64(2), x));
        if (IsInfinity(e2)) { return One; }            // x large positive ⇒ +1
        return Divide(e2, Add(e2, FromInt64(2)));
    }

    public static Float128 Asinh(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; } // odd; ±inf→±inf
        Float128 ax = Abs(x);
        // asinh(x) = log1p(|x| + |x|²/(1 + sqrt(1+|x|²))), sign-applied.
        Float128 axsq = Multiply(ax, ax);
        Float128 u = Add(ax, Divide(axsq, Add(Sqrt(Add(axsq, One)), One)));
        Float128 r = Log1p(u);
        return x.SignBit ? Negate(r) : r;
    }

    public static Float128 Acosh(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (x < One) { return NaN; }                   // domain x ≥ 1 (incl. negatives)
        if (IsInfinity(x)) { return PositiveInfinity; }
        // acosh(x) = log1p((x−1) + sqrt((x−1)(x+1))), accurate near x = 1.
        Float128 xm1 = Subtract(x, One);
        return Log1p(Add(xm1, Sqrt(Multiply(xm1, Add(x, One)))));
    }

    public static Float128 Atanh(Float128 x)
    {
        if (IsNaN(x)) { return NaN; }
        if (IsZero(x)) { return x; }
        Float128 ax = Abs(x);
        if (ax == One) { return x.SignBit ? NegativeInfinity : PositiveInfinity; }
        if (ax > One) { return NaN; }
        // atanh(x) = (log1p(x) − log1p(−x))/2.
        return Multiply(Subtract(Log1p(x), Log1p(Negate(x))), FromDouble(0.5));
    }

    // ── π-scaled trig, base-2/10 m1/p1 variants, misc (generic-math surface) ─
    private static Float128 Ln2 => FromFixedSigned(FpLn2);
    private static Float128 Ln10 => FromFixedSigned(FpLn10);

    public static (Float128 Sin, Float128 Cos) SinCos(Float128 x) => (Sin(x), Cos(x));

    public static Float128 SinPi(Float128 x)   // sin(πx); exact 0 at integers
    {
        if (IsNaN(x) || IsInfinity(x)) { return NaN; }
        Float128 n = Round(x), r = Subtract(x, n);     // r ∈ [−½, ½]
        Float128 s = Sin(Multiply(Pi, r));
        return IsOddInteger(n) ? Negate(s) : s;
    }

    public static Float128 CosPi(Float128 x)   // cos(πx)
    {
        if (IsNaN(x) || IsInfinity(x)) { return NaN; }
        Float128 n = Round(x), r = Subtract(x, n);
        Float128 c = Cos(Multiply(Pi, r));
        return IsOddInteger(n) ? Negate(c) : c;
    }

    public static Float128 TanPi(Float128 x) => Divide(SinPi(x), CosPi(x));
    public static Float128 AsinPi(Float128 x) => Divide(Asin(x), Pi);
    public static Float128 AcosPi(Float128 x) => Divide(Acos(x), Pi);
    public static Float128 AtanPi(Float128 x) => Divide(Atan(x), Pi);
    public static Float128 Atan2Pi(Float128 y, Float128 x) => Divide(Atan2(y, x), Pi);
    public static (Float128 SinPi, Float128 CosPi) SinCosPi(Float128 x) => (SinPi(x), CosPi(x));

    public static Float128 Exp2M1(Float128 x) => Expm1(Multiply(x, Ln2));   // 2^x − 1
    public static Float128 Exp10M1(Float128 x) => Expm1(Multiply(x, Ln10)); // 10^x − 1
    public static Float128 Log2P1(Float128 x) => Divide(Log1p(x), Ln2);     // log2(1+x)
    public static Float128 Log10P1(Float128 x) => Divide(Log1p(x), Ln10);   // log10(1+x)

    public static Float128 DegreesToRadians(Float128 x) => Divide(Multiply(x, Pi), FromInt64(180));
    public static Float128 RadiansToDegrees(Float128 x) => Divide(Multiply(x, FromInt64(180)), Pi);
    public static Float128 ReciprocalEstimate(Float128 x) => Divide(One, x);
    public static Float128 ReciprocalSqrtEstimate(Float128 x) => Divide(One, Sqrt(x));

    // ── value predicates ─────────────────────────────────────────────────────
    public static bool IsNormal(Float128 v) => v.BiasedExponent is not (0 or MaxBiasedExponent);
    public static bool IsSubnormal(Float128 v) => v.BiasedExponent == 0 && !IsZero(v);
    public static bool IsInteger(Float128 v) => IsFinite(v) && v.Equals(Truncate(v));
    public static bool IsEvenInteger(Float128 v) => IsInteger(v) && IsZero(Fmod(v, FromInt64(2)));
    public static bool IsOddInteger(Float128 v) => IsInteger(v) && !IsZero(Fmod(v, FromInt64(2)));
    public static bool IsPositive(Float128 v) => !v.SignBit;

    // ── min / max / clamp / sign ─────────────────────────────────────────────
    public static Float128 Max(Float128 a, Float128 b)        // NaN-propagating
        => IsNaN(a) || IsNaN(b) ? NaN : (a < b || (IsZero(a) && IsZero(b) && a.SignBit) ? b : a);
    public static Float128 Min(Float128 a, Float128 b)
        => IsNaN(a) || IsNaN(b) ? NaN : (a < b || (IsZero(a) && IsZero(b) && b.SignBit) ? a : b);
    public static Float128 MaxNumber(Float128 a, Float128 b)  // NaN-ignoring
        => IsNaN(a) ? b : IsNaN(b) ? a : Max(a, b);
    public static Float128 MinNumber(Float128 a, Float128 b)
        => IsNaN(a) ? b : IsNaN(b) ? a : Min(a, b);
    public static Float128 MaxMagnitude(Float128 a, Float128 b)
        => IsNaN(a) || IsNaN(b) ? NaN : (Abs(a) < Abs(b) ? b : Abs(b) < Abs(a) ? a : Max(a, b));
    public static Float128 MinMagnitude(Float128 a, Float128 b)
        => IsNaN(a) || IsNaN(b) ? NaN : (Abs(b) < Abs(a) ? b : Abs(a) < Abs(b) ? a : Min(a, b));
    public static Float128 Clamp(Float128 v, Float128 lo, Float128 hi)
        => v < lo ? lo : v > hi ? hi : v;
    /// <summary>−1 / 0 / +1 (NaN throws, matching .NET <c>INumber.Sign</c>).</summary>
    public static int Sign(Float128 v)
    {
        if (IsNaN(v)) { throw new ArithmeticException("Sign of NaN is undefined."); }
        if (IsZero(v)) { return 0; }
        return v.SignBit ? -1 : 1;
    }

    // ── decimal formatting (correctly rounded via BigInteger) ────────────────
    // Backs printf %Lf / %Le / %Lg. value = (-1)^sign · sig · 2^q exactly, so
    // |value|·10^power is the exact rational sig·2^q·10^power; we round it to a
    // nearest integer (ties-to-even) and lay the digits out. No double round-trip.

    /// <summary>round(|value| · 10^<paramref name="power"/>) to nearest, ties-even.
    /// Finite, non-NaN values only.</summary>
    private BigInteger AbsScaledByPow10(int power)
    {
        Decompose(this, out _, out BigInteger m, out int q);
        BigInteger num = m, den = BigInteger.One;
        if (power >= 0) { num *= BigInteger.Pow(10, power); } else { den *= BigInteger.Pow(10, -power); }
        if (q >= 0) { num <<= q; } else { den <<= -q; }
        BigInteger quo = BigInteger.DivRem(num, den, out BigInteger rem);
        BigInteger twiceRem = rem << 1;
        if (twiceRem > den || (twiceRem == den && !quo.IsEven)) { quo += BigInteger.One; }
        return quo;
    }

    /// <summary>Estimated floor(log10(|value|)) from the binary exponent; the
    /// scientific formatter corrects any ±1 error against the actual digits.</summary>
    private int EstimateExp10()
    {
        Decompose(this, out _, out BigInteger m, out int q);
        double approxLog2 = (q + m.GetBitLength() - 1);
        return (int)Math.Floor(approxLog2 * 0.30102999566398114); // log10(2)
    }

    /// <summary>printf <c>%f</c>: fixed notation with <paramref name="prec"/>
    /// fractional digits. Leading <c>-</c> only; the caller adds <c>+</c>/space.</summary>
    public string ToFixedString(int prec)
    {
        if (IsNaN(this)) { return "nan"; }
        if (IsInfinity(this)) { return SignBit ? "-inf" : "inf"; }
        BigInteger n = AbsScaledByPow10(prec);
        string digits = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string body;
        if (prec == 0)
        {
            body = digits;
        }
        else
        {
            if (digits.Length <= prec) { digits = new string('0', prec - digits.Length + 1) + digits; }
            int split = digits.Length - prec;
            body = digits[..split] + "." + digits[split..];
        }
        return SignBit && n != BigInteger.Zero ? "-" + body : body;
    }

    /// <summary>printf <c>%e</c>: scientific with <paramref name="prec"/>
    /// fractional digits (so prec+1 significant digits).</summary>
    public string ToScientificString(int prec, bool upper)
    {
        if (IsNaN(this)) { return upper ? "NAN" : "nan"; }
        if (IsInfinity(this)) { return (SignBit ? "-" : "") + (upper ? "INF" : "inf"); }
        char e = upper ? 'E' : 'e';
        if (IsZero(this))
        {
            string mant0 = prec == 0 ? "0" : "0." + new string('0', prec);
            return (SignBit ? "-" : "") + mant0 + e + "+00";
        }
        int exp = EstimateExp10();
        BigInteger s = AbsScaledByPow10(prec - exp);
        string ds = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Correct a ±1 estimate so we land on exactly prec+1 digits.
        if (ds.Length == prec + 2) { exp++; s = AbsScaledByPow10(prec - exp); ds = s.ToString(); }
        else if (ds.Length == prec) { exp--; s = AbsScaledByPow10(prec - exp); ds = s.ToString(); }
        string mant = prec == 0 ? ds : ds[..1] + "." + ds[1..];
        string expStr = (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        return (SignBit ? "-" : "") + mant + e + expStr;
    }

    /// <summary>printf <c>%g</c>: shortest of <c>%e</c>/<c>%f</c> for
    /// <paramref name="prec"/> significant digits, trailing zeros stripped.</summary>
    public string ToGeneralString(int prec, bool upper)
    {
        if (IsNaN(this)) { return upper ? "NAN" : "nan"; }
        if (IsInfinity(this)) { return (SignBit ? "-" : "") + (upper ? "INF" : "inf"); }
        if (prec == 0) { prec = 1; }
        int exp = IsZero(this) ? 0 : EstimateExp10();
        // Refine exp via the rounded significant digits (handles the ±1 estimate).
        if (!IsZero(this))
        {
            BigInteger probe = AbsScaledByPow10(prec - 1 - exp);
            int len = probe.ToString().Length;
            if (len == prec + 1) { exp++; } else if (len == prec - 1) { exp--; }
        }
        string s = (exp < -4 || exp >= prec)
            ? ToScientificString(prec - 1, upper)
            : ToFixedString(prec - 1 - exp);
        return StripTrailingZeros(s, upper);
    }

    private static string StripTrailingZeros(string s, bool upper)
    {
        int e = s.IndexOf(upper ? 'E' : 'e');
        string mant = e < 0 ? s : s[..e];
        string suffix = e < 0 ? "" : s[e..];
        if (mant.Contains('.'))
        {
            mant = mant.TrimEnd('0').TrimEnd('.');
        }
        return mant + suffix;
    }

    /// <summary>Full-precision round-trippable form (36 significant digits is
    /// more than binary128's ~34, then trailing zeros trimmed).</summary>
    public override string ToString() => ToGeneralString(36, upper: false);

    // ── IBinaryFloatingPointIeee754 plumbing ─────────────────────────────────
    // The math/value members live above; this section is the structural surface
    // the generic-math interface requires (identities, bitwise ops, parsing,
    // formatting, conversions, byte serialization). dotcc itself lowers C math
    // to direct method calls — this is for idiomatic .NET generic-math interop.

    static int INumberBase<Float128>.Radix => 2;
    public static Float128 AdditiveIdentity => Zero;
    public static Float128 MultiplicativeIdentity => One;
    public static Float128 NegativeOne => FromInt64(-1);
    public static Float128 AllBitsSet => new(~UInt128.Zero);

    public static bool IsCanonical(Float128 v) => true;
    public static bool IsComplexNumber(Float128 v) => false;
    public static bool IsImaginaryNumber(Float128 v) => false;
    public static bool IsRealNumber(Float128 v) => !IsNaN(v);
    public static bool IsPositiveInfinity(Float128 v) => IsInfinity(v) && !v.SignBit;
    public static bool IsNegativeInfinity(Float128 v) => IsInfinity(v) && v.SignBit;

    public static bool IsPow2(Float128 v)
    {
        if (v.SignBit || !IsFinite(v) || IsZero(v)) { return false; }
        Decompose(v, out _, out BigInteger sig, out _);
        return (sig & (sig - BigInteger.One)) == BigInteger.Zero; // single bit set
    }

    public static Float128 MaxMagnitudeNumber(Float128 a, Float128 b)
        => IsNaN(a) ? b : IsNaN(b) ? a : MaxMagnitude(a, b);
    public static Float128 MinMagnitudeNumber(Float128 a, Float128 b)
        => IsNaN(a) ? b : IsNaN(b) ? a : MinMagnitude(a, b);

    public static Float128 Log(Float128 x, Float128 newBase) => Divide(Log(x), Log(newBase));

    // Bitwise / increment / decrement / modulus operators.
    public static Float128 operator &(Float128 a, Float128 b) => new(a._bits & b._bits);
    public static Float128 operator |(Float128 a, Float128 b) => new(a._bits | b._bits);
    public static Float128 operator ^(Float128 a, Float128 b) => new(a._bits ^ b._bits);
    public static Float128 operator ~(Float128 v) => new(~v._bits);
    public static Float128 operator ++(Float128 v) => Add(v, One);
    public static Float128 operator --(Float128 v) => Subtract(v, One);
    public static Float128 operator %(Float128 a, Float128 b) => Fmod(a, b);

    public int CompareTo(object? obj)
        => obj is null ? 1 : obj is Float128 f ? CompareTo(f)
         : throw new ArgumentException("Object must be a Float128.", nameof(obj));

    public static Float128 Round(Float128 v, int digits, MidpointRounding mode)
    {
        if (!IsFinite(v)) { return v; }
        if (digits == 0) { return RoundToInteger(v, mode); }
        Float128 scale = Pow(FromInt64(10), FromInt64(digits));
        return Divide(RoundToInteger(Multiply(v, scale), mode), scale);
    }

    private static Float128 RoundToInteger(Float128 v, MidpointRounding mode) => mode switch
    {
        MidpointRounding.ToZero => Truncate(v),
        MidpointRounding.ToNegativeInfinity => Floor(v),
        MidpointRounding.ToPositiveInfinity => Ceiling(v),
        MidpointRounding.AwayFromZero => RoundHalfAway(v),
        _ => Round(v),                                   // ToEven
    };

    private static Float128 RoundHalfAway(Float128 x)
    {
        if (IsNaN(x) || IsInfinity(x) || IsZero(x)) { return x; }
        Decompose(x, out bool s, out BigInteger m, out int q);
        if (q >= 0) { return x; }
        int shift = -q, bits = (int)m.GetBitLength();
        if (shift > bits) { return s ? NegativeZero : Zero; } // |x| < 0.5
        BigInteger kept = m >> shift;
        if (m - (kept << shift) >= (BigInteger.One << (shift - 1))) { kept += BigInteger.One; }
        return kept.IsZero ? (s ? NegativeZero : Zero) : RoundToBinary128(s, kept, 0, false);
    }

    // IEEE field serialization (value = significand · 2^exponent).
    int IFloatingPoint<Float128>.GetExponentByteCount() => sizeof(short);
    int IFloatingPoint<Float128>.GetExponentShortestBitLength()
        => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(FieldExponent()));
    int IFloatingPoint<Float128>.GetSignificandByteCount() => 16;
    int IFloatingPoint<Float128>.GetSignificandBitLength() => 113;

    private int FieldExponent()
    {
        if (!IsFinite(this) || IsZero(this)) { return 0; }
        Decompose(this, out _, out _, out int q);
        return q;
    }
    private UInt128 FieldSignificand()
    {
        if (BiasedExponent is 0 or MaxBiasedExponent) { return TrailingSignificand; }
        return ImplicitBit | TrailingSignificand;
    }

    bool IFloatingPoint<Float128>.TryWriteExponentBigEndian(Span<byte> dst, out int written)
        => TryWriteShort(dst, out written, bigEndian: true);
    bool IFloatingPoint<Float128>.TryWriteExponentLittleEndian(Span<byte> dst, out int written)
        => TryWriteShort(dst, out written, bigEndian: false);
    bool IFloatingPoint<Float128>.TryWriteSignificandBigEndian(Span<byte> dst, out int written)
        => TryWriteU128(dst, out written, bigEndian: true);
    bool IFloatingPoint<Float128>.TryWriteSignificandLittleEndian(Span<byte> dst, out int written)
        => TryWriteU128(dst, out written, bigEndian: false);

    private bool TryWriteShort(Span<byte> dst, out int written, bool bigEndian)
    {
        written = 0;
        if (dst.Length < sizeof(short)) { return false; }
        short e = (short)FieldExponent();
        if (bigEndian) { dst[0] = (byte)(e >> 8); dst[1] = (byte)e; }
        else { dst[0] = (byte)e; dst[1] = (byte)(e >> 8); }
        written = sizeof(short);
        return true;
    }
    private bool TryWriteU128(Span<byte> dst, out int written, bool bigEndian)
    {
        written = 0;
        if (dst.Length < 16) { return false; }
        UInt128 s = FieldSignificand();
        for (int i = 0; i < 16; i++)
        {
            dst[bigEndian ? 15 - i : i] = (byte)(s >> (8 * i));
        }
        written = 16;
        return true;
    }

    // ── parsing (decimal → binary128, correctly rounded) ─────────────────────
    public static Float128 Parse(string s, IFormatProvider? provider) => Parse(s.AsSpan(), NumberStyles.Float | NumberStyles.AllowThousands, provider);
    public static Float128 Parse(string s, NumberStyles style, IFormatProvider? provider) => Parse(s.AsSpan(), style, provider);
    public static Float128 Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider);
    public static Float128 Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider)
        => TryParse(s, style, provider, out var r) ? r : throw new FormatException($"'{s.ToString()}' is not a valid Float128.");

    public static bool TryParse(string? s, IFormatProvider? provider, out Float128 result)
        => TryParse(s.AsSpan(), NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
    public static bool TryParse(string? s, NumberStyles style, IFormatProvider? provider, out Float128 result)
        => TryParse(s.AsSpan(), style, provider, out result);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Float128 result)
        => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);

    public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out Float128 result)
    {
        result = Zero;
        s = s.Trim();
        if (s.IsEmpty) { return false; }
        bool neg = false;
        if (s[0] is '+' or '-') { neg = s[0] == '-'; s = s[1..]; }
        if (s.Equals("inf", StringComparison.OrdinalIgnoreCase) || s.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            result = neg ? NegativeInfinity : PositiveInfinity;
            return true;
        }
        if (s.Equals("nan", StringComparison.OrdinalIgnoreCase)) { result = NaN; return true; }

        BigInteger mant = BigInteger.Zero;
        int decExp = 0;
        bool any = false, dot = false;
        int i = 0;
        for (; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is >= '0' and <= '9') { mant = mant * 10 + (ch - '0'); any = true; if (dot) { decExp--; } }
            else if (ch == '.' && !dot) { dot = true; }
            else if (ch is 'e' or 'E') { break; }
            else { return false; }
        }
        if (!any) { return false; }
        if (i < s.Length)                                // exponent part
        {
            var exp = s[(i + 1)..];
            if (!int.TryParse(exp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int e10)) { return false; }
            decExp += e10;
        }
        result = DecimalToBinary128(neg, mant, decExp);
        return true;
    }

    private static Float128 DecimalToBinary128(bool neg, BigInteger mant, int exp10)
    {
        if (mant.IsZero) { return neg ? NegativeZero : Zero; }
        if (exp10 >= 0)
        {
            return RoundToBinary128(neg, mant * BigInteger.Pow(10, exp10), 0, extraSticky: false);
        }
        int e = -exp10;
        int g = 160 + (int)Math.Ceiling(e * 3.321928094887362);   // ~log2(10) bits per decimal place + margin
        BigInteger q = BigInteger.DivRem(mant << g, BigInteger.Pow(10, e), out BigInteger r);
        return RoundToBinary128(neg, q, -g, extraSticky: r != BigInteger.Zero);
    }

    // ── formatting ───────────────────────────────────────────────────────────
    public string ToString(string? format, IFormatProvider? provider)
    {
        if (string.IsNullOrEmpty(format)) { return ToString(); }
        char kind = char.ToUpperInvariant(format[0]);
        int prec = format.Length > 1 && int.TryParse(format[1..], out int p) ? p : (kind == 'G' ? 36 : 6);
        bool upper = char.IsUpper(format[0]);
        return kind switch
        {
            'F' => ToFixedString(prec),
            'E' => ToScientificString(prec, upper),
            _ => ToGeneralString(prec, upper),           // 'G' and fallback
        };
    }

    public bool TryFormat(Span<char> dst, out int written, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        string s = ToString(format.IsEmpty ? null : format.ToString(), provider);
        if (s.Length > dst.Length) { written = 0; return false; }
        s.AsSpan().CopyTo(dst);
        written = s.Length;
        return true;
    }

    // ── generic numeric conversions ──────────────────────────────────────────
    static bool INumberBase<Float128>.TryConvertFromChecked<TOther>(TOther value, out Float128 result) => TryConvertFrom(value, out result);
    static bool INumberBase<Float128>.TryConvertFromSaturating<TOther>(TOther value, out Float128 result) => TryConvertFrom(value, out result);
    static bool INumberBase<Float128>.TryConvertFromTruncating<TOther>(TOther value, out Float128 result) => TryConvertFrom(value, out result);

    private static bool TryConvertFrom<TOther>(TOther value, out Float128 result) where TOther : INumberBase<TOther>
    {
        // Float128 is wider than every primitive numeric type, so widening is
        // exact and checked/saturating/truncating coincide.
        if (typeof(TOther) == typeof(double)) { result = FromDouble((double)(object)value!); return true; }
        if (typeof(TOther) == typeof(float)) { result = FromDouble((float)(object)value!); return true; }
        if (typeof(TOther) == typeof(Half)) { result = FromDouble((double)(Half)(object)value!); return true; }
        if (typeof(TOther) == typeof(long)) { result = FromInt64((long)(object)value!); return true; }
        if (typeof(TOther) == typeof(int)) { result = FromInt64((int)(object)value!); return true; }
        if (typeof(TOther) == typeof(short)) { result = FromInt64((short)(object)value!); return true; }
        if (typeof(TOther) == typeof(sbyte)) { result = FromInt64((sbyte)(object)value!); return true; }
        if (typeof(TOther) == typeof(ulong)) { result = RoundToBinary128(false, (BigInteger)(ulong)(object)value!, 0, false); return true; }
        if (typeof(TOther) == typeof(uint)) { result = FromInt64((uint)(object)value!); return true; }
        if (typeof(TOther) == typeof(ushort)) { result = FromInt64((ushort)(object)value!); return true; }
        if (typeof(TOther) == typeof(byte)) { result = FromInt64((byte)(object)value!); return true; }
        result = Zero;
        return false;
    }

    static bool INumberBase<Float128>.TryConvertToChecked<TOther>(Float128 value, out TOther result) => TryConvertTo(value, out result);
    static bool INumberBase<Float128>.TryConvertToSaturating<TOther>(Float128 value, out TOther result) => TryConvertTo(value, out result);
    static bool INumberBase<Float128>.TryConvertToTruncating<TOther>(Float128 value, out TOther result) => TryConvertTo(value, out result);

    private static bool TryConvertTo<TOther>(Float128 value, out TOther result) where TOther : INumberBase<TOther>
    {
        if (typeof(TOther) == typeof(double)) { result = (TOther)(object)ToDouble(value); return true; }
        if (typeof(TOther) == typeof(float)) { result = (TOther)(object)(float)ToDouble(value); return true; }
        if (typeof(TOther) == typeof(long)) { result = (TOther)(object)ToInt64(value); return true; }
        if (typeof(TOther) == typeof(int)) { result = (TOther)(object)(int)ToInt64(value); return true; }
        result = default!;
        return false;
    }
}
