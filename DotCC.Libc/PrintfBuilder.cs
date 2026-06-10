#nullable enable

using System;
using System.Globalization;
using System.IO;

namespace DotCC.Libc;

/// <summary>
/// Fluent printf-format consumer. Each <c>Arg</c> overload finds the next
/// <c>%</c> spec in the format pointer, formats the value per C99
/// <c>printf</c> rules (flags, width, precision, length modifier), and
/// writes the result to the bound <see cref="TextWriter"/>. Literal text
/// between specs (and after the last one) is flushed by <see cref="Done"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why a ref struct rather than <c>params object[]</c>: pointer types
/// (<c>byte*</c>) can't be boxed, and printf's <c>%s</c> in our lowering
/// takes a <c>byte*</c>. The fluent shape sidesteps boxing entirely. The
/// struct holds a <see cref="TextWriter"/> reference (so it can be obtained
/// from <c>fprintf(stream, fmt)</c>) and a <c>byte*</c> cursor.
/// </para>
/// <para>
/// Supported format string: <c>%[-+0 #][width][.precision][lhzL]conv</c>
/// where <c>conv</c> ∈ <c>d i x X c f e g s u %</c>. Length modifiers
/// are parsed and discarded — the receiving <c>Arg</c> overload already
/// carries the type information.
/// </para>
/// </remarks>
public unsafe ref struct PrintfBuilder
{
    private readonly TextWriter _w;
    private byte* _fmt;

    public PrintfBuilder(TextWriter writer, byte* fmt)
    {
        _w = writer;
        _fmt = fmt;
    }

    /// <summary>
    /// Parsed printf conversion spec. <see cref="Width"/> = -1 and
    /// <see cref="Precision"/> = -1 mean "unspecified".
    /// </summary>
    private struct Spec
    {
        public byte Conv;
        public int Width;
        public int Precision;
        public bool Left;
        public bool Zero;
        public bool Plus;
        public bool Space;
        public bool Alt;   // the `#` flag (e.g. %#o forces a leading 0)
    }

    /// <summary>Format <paramref name="v"/> as unsigned octal (no prefix). C's
    /// <c>%o</c> treats the argument as unsigned, so callers widen the bit
    /// pattern (e.g. <c>(uint)</c> / <c>(ulong)</c>) before calling.</summary>
    private static string FormatOctal(ulong v)
    {
        if (v == 0) { return "0"; }
        Span<char> buf = stackalloc char[22]; // 64-bit octal is ≤ 22 digits
        int i = buf.Length;
        while (v != 0) { buf[--i] = (char)('0' + (int)(v & 7)); v >>= 3; }
        return new string(buf[i..]);
    }

    /// <summary>Apply the <c>#</c> flag to an octal string: force a leading
    /// zero unless it already has one.</summary>
    private static string AltOctal(string s, Spec spec) =>
        spec.Alt && s.Length > 0 && s[0] != '0' ? "0" + s : s;

    public PrintfBuilder Arg(int v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        string s;
        switch (spec.Conv)
        {
            case (byte)'d': case (byte)'i':
                s = PadInt(v, spec, ci);
                break;
            case (byte)'u': s = PadInt((uint)v, spec, ci); break; // unsigned bit pattern
            case (byte)'x': s = (spec.Alt ? "0x" : "") + v.ToString("x", ci); break;
            case (byte)'X': s = (spec.Alt ? "0X" : "") + v.ToString("X", ci); break;
            case (byte)'o': s = AltOctal(FormatOctal((uint)v), spec); break; // # handled by AltOctal
            case (byte)'c': s = ((char)v).ToString(); break;
            case (byte)'f': case (byte)'e': case (byte)'E': case (byte)'g': case (byte)'G':
                // Integer formatted via the float path — same precision rules apply.
                s = FormatFloat((double)v, spec, ci);
                break;
            default: s = v.ToString(ci); break;
        }
        _w.Write(ApplyWidth(s, spec));
        return this;
    }

    public PrintfBuilder Arg(double v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        string s;
        switch (spec.Conv)
        {
            case (byte)'d': case (byte)'i':
                s = ((int)v).ToString(ci);
                break;
            case (byte)'a':
                s = FormatHexFloat(v, upper: false, ci, spec.Precision);
                if (spec.Plus && !double.IsNegative(v) && v != 0.0) { s = "+" + s; }
                else if (spec.Space && !double.IsNegative(v) && v != 0.0) { s = " " + s; }
                break;
            case (byte)'A':
                s = FormatHexFloat(v, upper: true, ci, spec.Precision);
                if (spec.Plus && !double.IsNegative(v) && v != 0.0) { s = "+" + s; }
                else if (spec.Space && !double.IsNegative(v) && v != 0.0) { s = " " + s; }
                break;
            case (byte)'f': case (byte)'e': case (byte)'E': case (byte)'g': case (byte)'G':
            default:
                s = FormatFloat(v, spec, ci);
                break;
        }
        _w.Write(ApplyWidth(s, spec));
        return this;
    }

    /// <summary>Apply precision to an integer or unsigned string.</summary>
    private static string PadInt(long v, Spec spec, CultureInfo ci)
    {
        string sign = "";
        ulong abs;
        if (v < 0) { sign = "-"; abs = unchecked((ulong)(-v)); }
        else if (spec.Plus) { sign = "+"; abs = (ulong)v; }
        else if (spec.Space) { sign = " "; abs = (ulong)v; }
        else { abs = (ulong)v; }
        // Precision 0 with value 0 → empty string (C99 §7.21.6.1).
        if (spec.Precision == 0 && abs == 0) return sign;
        string s = abs.ToString(ci);
        if (spec.Precision > s.Length)
            s = s.PadLeft(spec.Precision, '0');
        return sign + s;
    }

    private static string PadInt(ulong v, Spec spec, CultureInfo ci)
    {
        // Precision 0 with value 0 → empty string.
        if (spec.Precision == 0 && v == 0) return "";
        string s = v.ToString(ci);
        if (spec.Precision > s.Length)
            s = s.PadLeft(spec.Precision, '0');
        return s;
    }

    /// <summary>Format a double as a C99 <c>%a</c> hex float (e.g.
    /// <c>0x1.921fb54442d18p+1</c> for pi). The hex representation is
    /// round-trippable (exact in both directions), which is exactly what
    /// Lua's <c>quotefloat</c> needs for <c>%q</c>.</summary>
    private static string FormatHexFloat(double v, bool upper, CultureInfo ci, int precision = -1)
    {
        if (double.IsNaN(v)) { return upper ? "NAN" : "nan"; }
        if (double.IsInfinity(v))
        {
            var hex = upper ? "INF" : "inf";
            return v < 0 ? "-" + hex : hex;
        }
        if (v == 0.0)
        {
            var sb0 = new System.Text.StringBuilder();
            if (double.IsNegative(v)) { sb0.Append('-'); }
            sb0.Append(upper ? "0X0" : "0x0");
            if (precision > 0 || (precision < 0 && precision != 0))
            {
                sb0.Append('.');
                int count = precision >= 0 ? precision : 1;
                sb0.Append('0', count);
            }
            sb0.Append(upper ? "P+0" : "p+0");
            return sb0.ToString();
        }
        bool neg = double.IsNegative(v);
        if (neg) { v = -v; }
        // Extract mantissa and exponent from the IEEE 754 representation
        ulong bits = BitConverter.DoubleToUInt64Bits(v);
        long biasedExp = (long)((bits >> 52) & 0x7FF);
        ulong mantissa = bits & 0x000F_FFFF_FFFF_FFFF;
        long exp;
        if (biasedExp == 0)
        {
            exp = 1 - 1023;
        }
        else
        {
            mantissa |= 1UL << 52;
            exp = biasedExp - 1023;
        }
        int firstNibble = (int)(mantissa >> 52);
        ulong fracPart  = mantissa & 0x000F_FFFF_FFFF_FFFF;
        // Build 13 hex fractional nibbles (52 bits / 4).
        int[] nibbles = new int[13];
        for (int i = 0; i < 13; i++)
            nibbles[i] = (int)(fracPart >> (48 - i * 4)) & 0xF;
        // If precision is specified, round to that many fractional hex digits.
        int fullLen = 13;
        while (fullLen > 0 && nibbles[fullLen - 1] == 0) { fullLen--; }
        int outLen = precision >= 0 ? precision : fullLen;
        if (outLen < 13)
        {
            // Round at position outLen.
            int roundPos = outLen;
            if (roundPos < 13 && nibbles[roundPos] >= 8)
            {
                // Round up: propagate carry.
                int carry = 1;
                for (int i = roundPos - 1; i >= 0 && carry > 0; i--)
                {
                    int vv = nibbles[i] + 1;
                    nibbles[i] = vv & 0xF;
                    carry = vv >> 4;
                }
                if (carry > 0)
                {
                    // Fraction overflowed into the integer part.
                    firstNibble++;
                    if (firstNibble >= 16) { firstNibble = 1; exp += 4; }
                }
            }
        }
        // Build the result string.
        char toHex(int n) => n < 10 ? (char)('0' + n)
            : upper ? (char)('A' + n - 10) : (char)('a' + n - 10);
        var sb = new System.Text.StringBuilder(30);
        if (neg) { sb.Append('-'); }
        sb.Append(upper ? "0X" : "0x");
        sb.Append(toHex(firstNibble));
        if (outLen > 0)
        {
            sb.Append('.');
            for (int i = 0; i < outLen; i++)
                sb.Append(toHex(nibbles[i]));
        }
        sb.Append(upper ? 'P' : 'p');
        sb.Append(exp >= 0 ? "+" : "");
        sb.Append(exp.ToString(ci));
        return sb.ToString();
    }

    /// <summary>Float gets promoted to double — real C vararg rule.</summary>
    public PrintfBuilder Arg(float v) => Arg((double)v);

    /// <summary>
    /// <c>_Float128</c> (binary128). The <c>L</c> length modifier is parsed and
    /// ignored by <see cref="ParseSpec"/> (this overload already carries the
    /// type), so <c>%Lf</c>/<c>%Le</c>/<c>%Lg</c> route here and format at full
    /// quad precision via <see cref="Float128"/>'s own correctly-rounded
    /// decimal conversion — no narrowing to double.
    /// </summary>
    public PrintfBuilder Arg(Float128 v)
    {
        var spec = ConsumeUntilSpec();
        var prec = spec.Precision >= 0 ? spec.Precision : 6;
        bool nonNeg = !Float128.IsNegative(v) && !Float128.IsNaN(v);
        string s = spec.Conv switch
        {
            (byte)'d' or (byte)'i' => Float128.ToInt64(v).ToString(CultureInfo.InvariantCulture),
            (byte)'e' => v.ToScientificString(prec, upper: false),
            (byte)'E' => v.ToScientificString(prec, upper: true),
            (byte)'g' => v.ToGeneralString(spec.Precision >= 0 ? prec : 6, upper: false),
            (byte)'G' => v.ToGeneralString(spec.Precision >= 0 ? prec : 6, upper: true),
            (byte)'F' => v.ToFixedString(prec).ToUpperInvariant(), // INF/NAN
            _ => v.ToFixedString(prec),                            // 'f' and fallback
        };
        if (spec.Plus && nonNeg) { s = "+" + s; }
        else if (spec.Space && nonNeg) { s = " " + s; }
        _w.Write(ApplyWidth(s, spec));
        return this;
    }

    /// <summary>
    /// <c>bool</c> → 1/0 for <c>%d</c> and friends. Without this overload,
    /// C# would route <c>_w.Write(bool)</c> to the <c>Boolean.ToString()</c>
    /// form ("True"/"False"), which is wrong for C printf semantics.
    /// </summary>
    public PrintfBuilder Arg(bool v) => Arg(v ? 1 : 0);

    /// <summary>
    /// Long overload for the extended integer type family. Format logic
    /// mirrors <see cref="Arg(int)"/> but with <c>long.ToString</c> so the
    /// full 64-bit value survives.
    /// </summary>
    public PrintfBuilder Arg(long v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        string s;
        switch (spec.Conv)
        {
            case (byte)'d': case (byte)'i':
                s = PadInt(v, spec, ci);
                break;
            case (byte)'u': s = PadInt((ulong)v, spec, ci); break; // unsigned bit pattern
            case (byte)'x': s = (spec.Alt ? "0x" : "") + v.ToString("x", ci); break;
            case (byte)'X': s = (spec.Alt ? "0X" : "") + v.ToString("X", ci); break;
            case (byte)'o': s = AltOctal(FormatOctal((ulong)v), spec); break; // # handled by AltOctal
            case (byte)'f': case (byte)'e': case (byte)'E': case (byte)'g': case (byte)'G':
                s = FormatFloat((double)v, spec, ci);
                break;
            default: s = v.ToString(ci); break;
        }
        _w.Write(ApplyWidth(s, spec));
        return this;
    }

    public PrintfBuilder Arg(ulong v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        string s;
        switch (spec.Conv)
        {
            case (byte)'u':
            case (byte)'d': case (byte)'i':
                s = v.ToString(ci);
                break;
            case (byte)'x': s = v.ToString("x", ci); break;
            case (byte)'X': s = v.ToString("X", ci); break;
            case (byte)'o': s = AltOctal(FormatOctal(v), spec); break;
            default: s = v.ToString(ci); break;
        }
        _w.Write(ApplyWidth(s, spec));
        return this;
    }

    /// <summary>
    /// <c>uint</c> → routed through <see cref="Arg(long)"/>. The implicit
    /// promotion preserves the full 32-bit range; printf semantics don't
    /// distinguish signed/unsigned at the format-string level.
    /// </summary>
    public PrintfBuilder Arg(uint v) => Arg((long)v);

    public PrintfBuilder Arg(byte* v)
    {
        var spec = ConsumeUntilSpec();
        string s;
        if (spec.Conv == (byte)'s' && v != null)
        {
            int len = 0;
            while (v[len] != 0) { len++; }
            s = System.Text.Encoding.UTF8.GetString(v, len);
            // Precision on `%s` caps the string length per C99.
            if (spec.Precision >= 0 && spec.Precision < s.Length)
            {
                s = s[..spec.Precision];
            }
        }
        else if (spec.Conv == (byte)'p')
        {
            // glibc-shaped %p: a null pointer is "(nil)"; otherwise "0x" + the address
            // in lowercase hex with no leading zeros. Matches clang/gcc (and the wat
            // backend), so the same C program prints the same shape on both targets.
            s = v == null
                ? "(nil)"
                : "0x" + ((ulong)v).ToString("x", CultureInfo.InvariantCulture);
        }
        else if (v == null)
        {
            s = "(null)";
        }
        else
        {
            // Wrong spec for a pointer → print the address. System-qualified
            // because emitted user code can `typedef int* IntPtr;` and
            // shadow the BCL type at file scope via `using unsafe IntPtr = int*;`.
            s = ((System.IntPtr)v).ToString("X");
        }
        _w.Write(ApplyWidth(s, spec));
        return this;
    }

    /// <summary>
    /// A <c>void*</c> (or any typed <c>T*</c>, which implicitly converts to
    /// <c>void*</c>) argument — typically a <c>%p</c> spec. Reuses the
    /// <see cref="Arg(byte*)"/> path (glibc-shaped <c>0x…</c>/<c>(nil)</c> for
    /// <c>%p</c>, address-print for other non-<c>%s</c> specs). A dedicated overload
    /// is required because <c>void*</c> does NOT implicitly convert to <c>byte*</c>
    /// in C#, so without it a <c>%p</c> pointer arg fails to bind.
    /// </summary>
    public PrintfBuilder Arg(void* v) => Arg((byte*)v);

    /// <summary>
    /// Flush the literal text remaining after the last consumed <c>%</c>
    /// spec. Returns <c>0</c> for simplicity — real C printf returns the
    /// byte count, which our callers don't currently consult.
    /// </summary>
    public int Done()
    {
        while (*_fmt != 0)
        {
            if (*_fmt == (byte)'%' && _fmt[1] == (byte)'%')
            {
                _w.Write('%');
                _fmt += 2;
                continue;
            }
            WriteUtf8Codepoint(ref _fmt);
        }
        return 0;
    }

    private Spec ConsumeUntilSpec()
    {
        while (*_fmt != 0)
        {
            if (*_fmt == (byte)'%')
            {
                _fmt++;
                if (*_fmt == (byte)'%')
                {
                    _w.Write('%');
                    _fmt++;
                    continue;
                }
                return ParseSpec();
            }
            WriteUtf8Codepoint(ref _fmt);
        }
        return new Spec { Conv = 0, Width = -1, Precision = -1 };
    }

    /// <summary>
    /// Parse <c>[flags][width][.precision][length]conv</c> from the byte
    /// stream starting just past the leading <c>%</c>. Advances
    /// <c>_fmt</c> through the entire spec, including the conversion char.
    /// </summary>
    private Spec ParseSpec()
    {
        var s = new Spec { Width = -1, Precision = -1 };
        while (*_fmt != 0)
        {
            switch (*_fmt)
            {
                case (byte)'-': s.Left = true; _fmt++; continue;
                case (byte)'+': s.Plus = true; _fmt++; continue;
                case (byte)' ': s.Space = true; _fmt++; continue;
                case (byte)'0': s.Zero = true; _fmt++; continue;
                case (byte)'#': s.Alt = true; _fmt++; continue;
            }
            break;
        }
        while (*_fmt >= (byte)'0' && *_fmt <= (byte)'9')
        {
            if (s.Width < 0) { s.Width = 0; }
            s.Width = s.Width * 10 + (*_fmt - (byte)'0');
            _fmt++;
        }
        if (*_fmt == (byte)'.')
        {
            _fmt++;
            s.Precision = 0;
            while (*_fmt >= (byte)'0' && *_fmt <= (byte)'9')
            {
                s.Precision = s.Precision * 10 + (*_fmt - (byte)'0');
                _fmt++;
            }
        }
        // Length modifiers — recognized but ignored (the Arg overload has
        // already supplied the type information).
        while (*_fmt == (byte)'l' || *_fmt == (byte)'L'
            || *_fmt == (byte)'h' || *_fmt == (byte)'z')
        {
            _fmt++;
        }
        s.Conv = *_fmt;
        if (s.Conv != 0) { _fmt++; }
        return s;
    }

    private static string FormatFloat(double v, Spec spec, CultureInfo ci)
    {
        var prec = spec.Precision >= 0 ? spec.Precision : 6;
        string s = spec.Conv switch
        {
            (byte)'e' => v.ToString("e" + prec.ToString(ci), ci),
            (byte)'E' => v.ToString("E" + prec.ToString(ci), ci),
            (byte)'g' => spec.Precision >= 0
                ? v.ToString("g" + prec.ToString(ci), ci)
                : v.ToString("g", ci),
            (byte)'G' => spec.Precision >= 0
                ? v.ToString("G" + prec.ToString(ci), ci)
                : v.ToString("G", ci),
            _ => v.ToString("F" + prec.ToString(ci), ci),
        };
        // '#' flag for floats: force a decimal point even when precision is 0
        // (e.g. %#.0f with value 100 → "100.")
        if (spec.Alt && !s.Contains('.')) { s += "."; }
        if (spec.Plus && v >= 0) { s = "+" + s; }
        else if (spec.Space && v >= 0) { s = " " + s; }
        return s;
    }

    private static string ApplyWidth(string s, Spec spec)
    {
        if (spec.Width <= 0 || s.Length >= spec.Width) { return s; }
        if (spec.Left) { return s.PadRight(spec.Width, ' '); }
        // Zero-padding only when right-aligned and no precision (per C).
        // When the value carries a leading sign, the zero-pad goes between
        // the sign and the digits — handle that case.
        if (spec.Zero)
        {
            if (s.Length > 0 && (s[0] == '-' || s[0] == '+' || s[0] == ' '))
            {
                return s[0] + new string('0', spec.Width - s.Length) + s[1..];
            }
            return s.PadLeft(spec.Width, '0');
        }
        return s.PadLeft(spec.Width, ' ');
    }

    private void WriteUtf8Codepoint(ref byte* p)
    {
        byte b = *p;
        if (b < 0x80) { _w.Write((char)b); p++; return; }
        int len = 1;
        if ((b & 0xE0) == 0xC0) { len = 2; }
        else if ((b & 0xF0) == 0xE0) { len = 3; }
        else if ((b & 0xF8) == 0xF0) { len = 4; }
        _w.Write(System.Text.Encoding.UTF8.GetString(p, len));
        p += len;
    }
}
