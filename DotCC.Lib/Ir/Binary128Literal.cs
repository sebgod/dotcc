#nullable enable

using System.Globalization;
using System.Numerics;

namespace DotCC.Ir;

/// <summary>Compile-time IEEE binary128 for a float literal (zig's <c>f128</c>, task #214). The runtime's <c>Float128</c>
/// can hold a literal the nearest <c>double</c> cannot (<c>0.1</c> needs 113 significand bits), so a literal typed
/// binary128 is rounded here, exactly and to nearest-even, into the bit pattern the backend spells. The value is kept as
/// an exact rational (a <see cref="BigInteger"/> mantissa times a power of ten or two) until the one rounding.</summary>
internal static class Binary128Literal
{
    private const int MantissaBits = 112;
    private const int ExponentBias = 16383;
    private const int MaxBiasedExponent = 32767;

    /// <summary>Parse a decimal literal (<c>2.5</c>, <c>1e30</c>, <c>.5</c>, <c>6.02E23</c>; no separators, no suffix)
    /// into its exact value <c>(-1)^neg · mant · 10^exp10</c>.</summary>
    public static bool TryParseDecimal(string text, out bool neg, out BigInteger mant, out int exp10)
    {
        neg = false;
        mant = BigInteger.Zero;
        exp10 = 0;
        var i = 0;
        if (i < text.Length && text[i] is '+' or '-') { neg = text[i] == '-'; i++; }
        var digits = false;
        var dot = false;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is >= '0' and <= '9')
            {
                mant = mant * 10 + (ch - '0');
                digits = true;
                if (dot) { exp10--; }
            }
            else if (ch == '.' && !dot) { dot = true; }
            else { break; }
        }
        if (!digits) { return false; }
        if (i < text.Length)
        {
            if (text[i] is not ('e' or 'E')
                || !int.TryParse(text.AsSpan(i + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var e))
            {
                return false;
            }
            exp10 += e;
        }
        return true;
    }

    /// <summary>True when the nearest <c>double</c> to the decimal <paramref name="text"/> IS its value, so the literal
    /// widens to binary128 exactly through <c>double</c>.</summary>
    public static bool IsExactDouble(string text)
    {
        if (!TryParseDecimal(text, out var neg, out var mant, out var exp10)) { return false; }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) { return false; }
        if (mant.IsZero) { return d == 0; }
        if ((d < 0) != neg) { return false; }
        var (m2, e2) = Decompose(System.Math.Abs(d));
        var (numA, denA) = exp10 >= 0 ? (mant * BigInteger.Pow(10, exp10), BigInteger.One) : (mant, BigInteger.Pow(10, -exp10));
        var (numB, denB) = e2 >= 0 ? (m2 << e2, BigInteger.One) : (m2, BigInteger.One << -e2);
        return numA * denB == numB * denA;
    }

    /// <summary>The binary128 bit pattern (high and low 64 bits) nearest the decimal <paramref name="text"/>, ties to
    /// even. A value outside binary128's normal range is a loud cut rather than a guessed infinity or subnormal.</summary>
    public static (ulong Hi, ulong Lo) ToBits(string text)
    {
        if (!TryParseDecimal(text, out var neg, out var mant, out var exp10))
        {
            throw new IrUnsupportedException($"float literal `{text}` is not a decimal literal dotcc can round to f128");
        }
        var sign = neg ? BigInteger.One << 127 : BigInteger.Zero;
        if (mant.IsZero) { return Split(sign); }
        var num = exp10 >= 0 ? mant * BigInteger.Pow(10, exp10) : mant;
        var den = exp10 >= 0 ? BigInteger.One : BigInteger.Pow(10, -exp10);
        // e = floor(log2(num / den)); the bit-length estimate is off by at most one either way.
        var e = (int)(num.GetBitLength() - den.GetBitLength());
        var (q, rem, divisor) = Scaled(num, den, MantissaBits - e);
        if (q.GetBitLength() > MantissaBits + 1) { e++; (q, rem, divisor) = Scaled(num, den, MantissaBits - e); }
        else if (q.GetBitLength() <= MantissaBits) { e--; (q, rem, divisor) = Scaled(num, den, MantissaBits - e); }
        var twice = rem << 1;
        if (twice > divisor || (twice == divisor && !q.IsEven)) { q += 1; }
        if (q.GetBitLength() > MantissaBits + 1) { q >>= 1; e++; }
        var biased = e + ExponentBias;
        if (biased < 1 || biased >= MaxBiasedExponent)
        {
            throw new IrUnsupportedException($"float literal `{text}` is outside f128's normal range");
        }
        var trailing = q - (BigInteger.One << MantissaBits);
        return Split(sign | ((BigInteger)biased << MantissaBits) | trailing);
    }

    /// <summary>The exact decimal spelling of <c>m · 2^e2</c> (every binary fraction is a finite decimal), for a hex
    /// float literal whose value <c>double</c> cannot hold.</summary>
    public static string ExactDecimal(BigInteger m, int e2)
    {
        if (e2 >= 0) { return (m << e2).ToString(CultureInfo.InvariantCulture) + ".0"; }
        // m / 2^k == m · 5^k / 10^k
        var k = -e2;
        var digits = (m * BigInteger.Pow(5, k)).ToString(CultureInfo.InvariantCulture).PadLeft(k + 1, '0');
        return digits[..^k] + "." + digits[^k..];
    }

    /// <summary>A hex float literal (<c>0x1.8p3</c>; no separators, no suffix) as its exact <c>m · 2^e2</c>.</summary>
    public static bool TryParseHex(string text, out BigInteger m, out int e2)
    {
        m = BigInteger.Zero;
        e2 = 0;
        if (text.Length < 3 || text[0] != '0' || text[1] is not ('x' or 'X')) { return false; }
        var body = text[2..];
        var p = body.IndexOfAny(new[] { 'p', 'P' });
        var mantissa = p >= 0 ? body[..p] : body;
        if (p >= 0 && !int.TryParse(body.AsSpan(p + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out e2)) { return false; }
        var dot = false;
        foreach (var ch in mantissa)
        {
            if (ch == '.' && !dot) { dot = true; continue; }
            if (!System.Uri.IsHexDigit(ch)) { return false; }
            m = (m << 4) + System.Convert.ToInt32(ch.ToString(), 16);
            if (dot) { e2 -= 4; }
        }
        return true;
    }

    /// <summary>True when <c>m · 2^e2</c> is a finite <c>double</c> exactly (53 significand bits, in range).</summary>
    public static bool FitsDouble(BigInteger m, int e2)
    {
        if (m.IsZero) { return true; }
        while (m.IsEven) { m >>= 1; e2++; }
        var bits = (int)m.GetBitLength();
        var top = e2 + bits - 1;                       // exponent of the leading bit
        return bits <= 53 && top <= 1023 && e2 >= -1074;
    }

    /// <summary>The quotient and remainder of <c>num · 2^s / den</c>, with the divisor the remainder is against.</summary>
    private static (BigInteger Q, BigInteger Rem, BigInteger Divisor) Scaled(BigInteger num, BigInteger den, int s)
    {
        var n = s >= 0 ? num << s : num;
        var d = s >= 0 ? den : den << -s;
        return (BigInteger.DivRem(n, d, out var rem), rem, d);
    }

    /// <summary>A finite positive double as its exact <c>m · 2^e</c>.</summary>
    private static (BigInteger M, int E) Decompose(double d)
    {
        var bits = System.BitConverter.DoubleToInt64Bits(d);
        var exp = (int)((bits >> 52) & 0x7FF);
        var frac = bits & 0xFFFFFFFFFFFFFL;
        return exp == 0 ? (frac, -1074) : (frac | (1L << 52), exp - 1075);
    }

    /// <summary>A 128-bit pattern as its high and low halves.</summary>
    private static (ulong Hi, ulong Lo) Split(BigInteger bits)
        => ((ulong)(bits >> 64), (ulong)(bits & ulong.MaxValue));
}
