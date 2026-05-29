#nullable enable

using System;
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
/// This file is Stage 1a: representation, constants, predicates, exact
/// <c>double</c>→binary128 widening, correctly-rounded binary128→<c>double</c>
/// narrowing, sign ops, and IEEE comparison. Correctly-rounded arithmetic
/// (+ − × ÷ √ fma) and the generic-math interface surface land in later
/// stages, validated bit-for-bit against gcc's <c>long double</c> (binary128)
/// oracle.
/// </remarks>
public readonly struct Float128 : IEquatable<Float128>, IComparable<Float128>
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
}
