#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// Fluent printf-format consumer. Each <c>Arg</c> overload only records its
/// argument, in order; <see cref="Done"/> then walks the format, formats every
/// recorded value per C99 <c>printf</c> rules (flags, width, precision, length
/// modifier) and writes the whole output to the bound <see cref="TextWriter"/>
/// in one write. Nothing is written, and nothing read through an argument
/// pointer, before <see cref="Done"/>, so the side effects of the argument
/// expressions land before the output (C11 6.5.2.2p10, dotcc #249).
/// </summary>
/// <remarks>
/// <para>
/// Why a ref struct rather than <c>params object[]</c>: pointer types
/// (<c>byte*</c>) can't be boxed, and printf's <c>%s</c> in our lowering
/// takes a <c>byte*</c>. The fluent shape sidesteps boxing entirely. The
/// struct holds a <see cref="TextWriter"/> reference (so it can be obtained
/// from <c>fprintf(stream, fmt)</c>) and the rest of the format as a
/// <see cref="ReadOnlySpan{T}"/>, so a literal format can be handed over as
/// <c>"…"u8</c> by safe code (<see cref="Libc.printf(ReadOnlySpan{byte})"/>): a span
/// keeps whatever it views alive and tracked, where a pointer held across the
/// <c>Arg</c> calls would need it pinned.
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
    private ReadOnlySpan<byte> _fmt;   // the format not consumed yet, up to (not including) its NUL
    private StringBuilder? _sb;   // the buffer Done formats into
    private Rec[]? _args;   // the recorded arguments, allocated by the first Arg
    private int _n;   // how many are recorded
    private int _count;   // UTF-8 bytes written so far — printf's return value (C99 §7.21.6.3)

    /// <summary>A builder over the NUL-terminated format at <paramref name="fmt"/>.</summary>
    public PrintfBuilder(TextWriter writer, byte* fmt)
    {
        _w = writer;
        _fmt = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(fmt);
        _count = 0;
    }

    /// <summary>A builder over the format in <paramref name="fmt"/>, which ends at its first NUL as a C string does
    /// (a literal's <c>"…\0"u8</c>), or at its end.</summary>
    public PrintfBuilder(TextWriter writer, ReadOnlySpan<byte> fmt)
    {
        _w = writer;
        var nul = fmt.IndexOf((byte)0);
        _fmt = nul >= 0 ? fmt[..nul] : fmt;
        _count = 0;
    }

    /// <summary>The format byte <paramref name="i"/> places ahead, or 0 past its end (C's NUL).</summary>
    private readonly byte At(int i) => i < _fmt.Length ? _fmt[i] : (byte)0;

    /// <summary>Step past <paramref name="n"/> format bytes.</summary>
    private void Skip(int n) => _fmt = _fmt[System.Math.Min(n, _fmt.Length)..];

    /// <summary>Write <paramref name="s"/> to the sink and accumulate its UTF-8
    /// byte length into the running output count that <see cref="Done"/> returns.
    /// Every emitting path routes through here (or bumps <c>_count</c> directly for
    /// a single literal byte) so the count matches the bytes actually transmitted.</summary>
    private void Emit(string s)
    {
        Out.Append(s);
        _count += System.Text.Encoding.UTF8.GetByteCount(s);
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

    /// <summary>Format an <c>int</c> for <c>%d %i %u %x %X %o %c</c> and the float specs.</summary>
    private void FmtInt(int v)
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
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>Format a <c>double</c> for the float specs (and an integer spec, as C would print its bits as a number).</summary>
    private void FmtDouble(double v)
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
        // An infinity or NaN is padded with spaces, the 0 flag notwithstanding, as glibc does.
        if (!double.IsFinite(v)) { spec.Zero = false; }
        Emit(ApplyWidth(s, spec));
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
        if (double.IsNaN(v))
        {
            // A negative NaN prints its sign, as glibc does (and as %f's does, FormatNonFinite).
            var nan = upper ? "NAN" : "nan";
            return double.IsNegative(v) ? "-" + nan : nan;
        }
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

    /// <summary>
    /// <c>_Float128</c> (binary128). The <c>L</c> length modifier is parsed and
    /// ignored by <see cref="ParseSpec"/> (this overload already carries the
    /// type), so <c>%Lf</c>/<c>%Le</c>/<c>%Lg</c> route here and format at full
    /// quad precision via <see cref="Float128"/>'s own correctly-rounded
    /// decimal conversion — no narrowing to double.
    /// </summary>
    private void FmtF128(Float128 v)
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
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>A zig <c>comptime_int</c> value (C# <see cref="System.Int128"/>, e.g. <c>const g = std.math.gcd(48, 180);</c>):
    /// formatted by the 64-bit overload that holds it, else as its full decimal / hex digits.</summary>
    private void FmtInt128(System.Int128 v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        var s = spec.Conv switch
        {
            (byte)'x' => (spec.Alt ? "0x" : "") + v.ToString("x", ci),
            (byte)'X' => (spec.Alt ? "0X" : "") + v.ToString("X", ci),
            _ => v.ToString(ci),
        };
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>Format a <c>long</c> (the extended integer family) at full 64-bit width.</summary>
    private void FmtLong(long v)
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
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>Format an <c>unsigned long</c> for <c>%u %d %x %X %o</c>.</summary>
    private void FmtULong(ulong v)
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
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>A zig byte slice for <c>%s</c> (<c>std.debug.print("{s}", .{slice})</c>): exactly <c>Len</c> bytes, with no
    /// NUL to look for, as zig's <c>{s}</c> prints; a precision still caps it.</summary>
    private void FmtSlice(byte* ptr, long len)
    {
        var spec = ConsumeUntilSpec();
        var s = len == 0 ? "" : System.Text.Encoding.UTF8.GetString(ptr, checked((int)len));
        if (spec.Precision >= 0 && spec.Precision < s.Length) { s = s[..spec.Precision]; }
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>A C string or any pointer: <c>%s</c> reads the NUL-terminated bytes, <c>%p</c> prints the address.</summary>
    private void FmtBytePtr(byte* v)
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
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>
    /// A wide-string (<c>wchar_t*</c> = C# <c>char*</c>) argument — the wide
    /// <c>%s</c> / <c>%ls</c> spec used by <c>wprintf</c>/<c>fwprintf</c>/<c>swprintf</c>
    /// (whose format is transcoded to UTF-8, so the spec is parsed identically; only
    /// the argument is UTF-16). Reads the NUL-terminated UTF-16 string and writes it,
    /// honoring precision. Narrow <c>printf</c> never passes a <c>char*</c> (its
    /// <c>%s</c> is a <c>byte*</c>), so this is wide-only.
    /// </summary>
    private void FmtCharPtr(char* v)
    {
        var spec = ConsumeUntilSpec();
        string s;
        if ((spec.Conv == (byte)'s' || spec.Conv == (byte)'S') && v != null)
        {
            int len = 0;
            while (v[len] != 0) { len++; }
            s = new string(v, 0, len);
            if (spec.Precision >= 0 && spec.Precision < s.Length) { s = s[..spec.Precision]; }
        }
        else if (v == null)
        {
            s = "(null)";
        }
        else
        {
            s = ((System.IntPtr)v).ToString("X");
        }
        Emit(ApplyWidth(s, spec));
    }

    /// <summary>One recorded argument: what <c>Arg</c> was handed, tagged by how <see cref="Done"/> formats it. <c>i</c> int,
    /// <c>l</c> long, <c>u</c> ulong, <c>d</c> double (bits in <c>A</c>), <c>F</c> Float128 (boxed), <c>H</c> Int128 beyond 64 bits
    /// (<c>A</c> low, <c>B</c> high), <c>s</c> byte* and <c>w</c> char* (the address in <c>A</c>, read only in Done),
    /// <c>S</c> a byte slice (<c>A</c> pointer, <c>B</c> length), <c>B</c> a copied NUL-terminated span (<c>Obj</c> a byte[]).</summary>
    private struct Rec
    {
        public char Kind;
        public long A;
        public long B;
        public object? Obj;
    }

    /// <summary>Record <paramref name="r"/> as the next argument. The array is allocated on the first one.</summary>
    private void Push(Rec r)
    {
        var a = _args;
        if (a == null) { a = _args = new Rec[4]; }
        else if (_n == a.Length) { Array.Resize(ref a, a.Length * 2); _args = a; }
        a[_n++] = r;
    }

    /// <summary>The buffer Done formats into.</summary>
    private readonly StringBuilder Out => _sb ?? throw new InvalidOperationException("PrintfBuilder formats only inside Done");

    /// <summary>Record an <c>int</c>; nothing is read or written until <see cref="Done"/>.</summary>
    public PrintfBuilder Arg(int v) { Push(new Rec { Kind = 'i', A = v }); return this; }

    /// <summary>Record a <c>long</c>.</summary>
    public PrintfBuilder Arg(long v) { Push(new Rec { Kind = 'l', A = v }); return this; }

    /// <summary>Record an <c>unsigned long</c>.</summary>
    public PrintfBuilder Arg(ulong v) { Push(new Rec { Kind = 'u', A = unchecked((long)v) }); return this; }

    /// <summary>Record a <c>double</c>.</summary>
    public PrintfBuilder Arg(double v) { Push(new Rec { Kind = 'd', A = BitConverter.DoubleToInt64Bits(v) }); return this; }

    /// <summary>Record a <c>_Float128</c>.</summary>
    public PrintfBuilder Arg(Float128 v) { Push(new Rec { Kind = 'F', Obj = v }); return this; }

    /// <summary>A zig <c>comptime_int</c> value: recorded as the 64-bit overload that holds it, else as its two halves.</summary>
    public PrintfBuilder Arg(System.Int128 v)
    {
        if (v >= long.MinValue && v <= long.MaxValue) { return Arg((long)v); }
        if (v >= 0 && v <= ulong.MaxValue) { return Arg((ulong)v); }
        Push(new Rec { Kind = 'H', A = unchecked((long)(ulong)(v & ulong.MaxValue)), B = unchecked((long)(ulong)(v >> 64)) });
        return this;
    }

    /// <summary>A zig byte slice for <c>%s</c>: its pointer and length are kept, the bytes are read in <see cref="Done"/>.</summary>
    public PrintfBuilder Arg(ConstSlice<byte> v) { Push(new Rec { Kind = 'S', A = (long)v.Ptr, B = (long)v.Len }); return this; }

    /// <summary>A mutable zig byte slice for <c>%s</c>: as <see cref="Arg(ConstSlice{byte})"/>.</summary>
    public PrintfBuilder Arg(Slice<byte> v) => Arg((ConstSlice<byte>)v);

    /// <summary>A string argument as a span: the C# backend's form for a string literal passed to <c>printf</c>
    /// (a literal, which a program cannot change). A ref struct cannot keep a span, so the bytes up to the first NUL are
    /// copied and treated as <see cref="Arg(byte*)"/> treats a C string.</summary>
    public PrintfBuilder Arg(ReadOnlySpan<byte> v)
    {
        var nul = v.IndexOf((byte)0);
        var copy = new byte[(nul >= 0 ? nul : v.Length) + 1];
        v[..(copy.Length - 1)].CopyTo(copy);
        Push(new Rec { Kind = 'B', Obj = copy });
        return this;
    }

    /// <summary>A C string or any pointer (<c>%s</c>, <c>%p</c>): the address is kept, what it points at is read in <see cref="Done"/>.</summary>
    public PrintfBuilder Arg(byte* v) { Push(new Rec { Kind = 's', A = (long)v }); return this; }

    /// <summary>A <c>void*</c> (or any typed <c>T*</c>), typically <c>%p</c>: as <see cref="Arg(byte*)"/>.</summary>
    public PrintfBuilder Arg(void* v) => Arg((byte*)v);

    /// <summary>A wide string (<c>wchar_t*</c>) for the wide <c>%s</c> / <c>%ls</c>: the address is kept, the string is read in <see cref="Done"/>.</summary>
    public PrintfBuilder Arg(char* v) { Push(new Rec { Kind = 'w', A = (long)v }); return this; }

    /// <summary>Float promotes to double, as a C vararg does.</summary>
    public PrintfBuilder Arg(float v) => Arg((double)v);

    /// <summary><c>bool</c> is 1 or 0 for <c>%d</c> and friends.</summary>
    public PrintfBuilder Arg(bool v) => Arg(v ? 1 : 0);

    /// <summary><c>uint</c> keeps its full 32-bit range through the <c>long</c> overload.</summary>
    public PrintfBuilder Arg(uint v) => Arg((long)v);

    [ThreadStatic] private static StringBuilder? s_cache;

    /// <summary>
    /// Format every recorded argument, in order, into a private buffer, then write the whole output to the sink in one write
    /// (as POSIX printf holds the stream for the call) and return the number of UTF-8 bytes it is: printf's C99 7.21.6.3
    /// return value. Nothing was written, and nothing read through an argument pointer, before this, so the side effects of
    /// the argument expressions have all happened (C11 6.5.2.2p10). dotcc never signals the negative output-error case; the
    /// sinks are managed writers that throw.
    /// </summary>
    public int Done()
    {
        var sb = s_cache ?? new StringBuilder();
        s_cache = null;
        sb.Clear();
        _sb = sb;
        var args = _args;
        for (var i = 0; i < _n && args != null; i++)
        {
            var r = args[i];
            switch (r.Kind)
            {
                case 'i': FmtInt((int)r.A); break;
                case 'l': FmtLong(r.A); break;
                case 'u': FmtULong(unchecked((ulong)r.A)); break;
                case 'd': FmtDouble(BitConverter.Int64BitsToDouble(r.A)); break;
                case 'F': FmtF128(r.Obj is Float128 f ? f : default); break;
                case 'H': FmtInt128(new System.Int128(unchecked((ulong)r.B), unchecked((ulong)r.A))); break;
                case 'S': FmtSlice((byte*)r.A, r.B); break;
                case 's': FmtBytePtr((byte*)r.A); break;
                case 'w': FmtCharPtr((char*)r.A); break;
                case 'B':
                    if (r.Obj is byte[] bytes) { fixed (byte* p = bytes) { FmtBytePtr(p); } }
                    break;
            }
        }
        while (At(0) != 0)
        {
            if (At(0) == (byte)'%' && At(1) == (byte)'%')
            {
                sb.Append('%');
                _count++;
                Skip(2);
                continue;
            }
            WriteUtf8Codepoint();
        }
        if (sb.Length > 0) { _w.Write(sb); }
        _sb = null;
        s_cache = sb;
        return _count;
    }

    private Spec ConsumeUntilSpec()
    {
        while (At(0) != 0)
        {
            if (At(0) == (byte)'%')
            {
                Skip(1);
                if (At(0) == (byte)'%')
                {
                    Out.Append('%');
                    _count++;
                    Skip(1);
                    continue;
                }
                return ParseSpec();
            }
            WriteUtf8Codepoint();
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
        while (At(0) != 0)
        {
            switch (At(0))
            {
                case (byte)'-': s.Left = true; Skip(1); continue;
                case (byte)'+': s.Plus = true; Skip(1); continue;
                case (byte)' ': s.Space = true; Skip(1); continue;
                case (byte)'0': s.Zero = true; Skip(1); continue;
                case (byte)'#': s.Alt = true; Skip(1); continue;
            }
            break;
        }
        while (At(0) >= (byte)'0' && At(0) <= (byte)'9')
        {
            if (s.Width < 0) { s.Width = 0; }
            s.Width = s.Width * 10 + (At(0) - (byte)'0');
            Skip(1);
        }
        if (At(0) == (byte)'.')
        {
            Skip(1);
            s.Precision = 0;
            while (At(0) >= (byte)'0' && At(0) <= (byte)'9')
            {
                s.Precision = s.Precision * 10 + (At(0) - (byte)'0');
                Skip(1);
            }
        }
        // Length modifiers — recognized but ignored (the Arg overload has
        // already supplied the type information).
        while (At(0) == (byte)'l' || At(0) == (byte)'L'
            || At(0) == (byte)'h' || At(0) == (byte)'z')
        {
            Skip(1);
        }
        s.Conv = At(0);
        if (s.Conv != 0) { Skip(1); }
        return s;
    }

    private static string FormatFloat(double v, Spec spec, CultureInfo ci)
    {
        if (!double.IsFinite(v)) { return FormatNonFinite(v, spec); }
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

    /// <summary>An infinity or NaN as C prints it under <c>%f %e %g</c> and their uppercase forms
    /// (C11 7.21.6.1p8): <c>inf</c> / <c>nan</c>, <c>INF</c> / <c>NAN</c> for <c>F E G</c>, the
    /// precision ignored, a <c>-</c> when the sign bit is set (a negative NaN's too, as glibc
    /// prints it), else <c>+</c> or a space by the flags.</summary>
    private static string FormatNonFinite(double v, Spec spec)
    {
        var upper = spec.Conv is (byte)'F' or (byte)'E' or (byte)'G';
        var body = double.IsNaN(v) ? (upper ? "NAN" : "nan") : (upper ? "INF" : "inf");
        return double.IsNegative(v) ? "-" + body : spec.Plus ? "+" + body : spec.Space ? " " + body : body;
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

    /// <summary>Write the format's next UTF-8 code point as text and step past it (a sequence the format cuts short
    /// is written as far as it goes).</summary>
    private void WriteUtf8Codepoint()
    {
        byte b = _fmt[0];
        if (b < 0x80) { Out.Append((char)b); _count++; Skip(1); return; }
        int len = 1;
        if ((b & 0xE0) == 0xC0) { len = 2; }
        else if ((b & 0xF0) == 0xE0) { len = 3; }
        else if ((b & 0xF8) == 0xF0) { len = 4; }
        len = System.Math.Min(len, _fmt.Length);
        Out.Append(System.Text.Encoding.UTF8.GetString(_fmt[..len]));
        _count += len;
        Skip(len);
    }
}
