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
    }

    public PrintfBuilder Arg(int v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        string s;
        switch (spec.Conv)
        {
            case (byte)'d': case (byte)'i':
                s = v.ToString(ci);
                if (spec.Plus && v >= 0) { s = "+" + s; }
                else if (spec.Space && v >= 0) { s = " " + s; }
                break;
            case (byte)'x': s = v.ToString("x", ci); break;
            case (byte)'X': s = v.ToString("X", ci); break;
            case (byte)'c': _w.Write((char)v); return this;
            case (byte)'f': case (byte)'e': case (byte)'g':
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
            case (byte)'f': case (byte)'e': case (byte)'g':
            default:
                s = FormatFloat(v, spec, ci);
                break;
        }
        _w.Write(ApplyWidth(s, spec));
        return this;
    }

    /// <summary>Float gets promoted to double — real C vararg rule.</summary>
    public PrintfBuilder Arg(float v) => Arg((double)v);

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
                s = v.ToString(ci);
                if (spec.Plus && v >= 0) { s = "+" + s; }
                else if (spec.Space && v >= 0) { s = " " + s; }
                break;
            case (byte)'x': s = v.ToString("x", ci); break;
            case (byte)'X': s = v.ToString("X", ci); break;
            case (byte)'f': case (byte)'e': case (byte)'g':
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
                case (byte)'#': _fmt++; continue;
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
            (byte)'e' => v.ToString("E" + prec.ToString(ci), ci),
            (byte)'g' => spec.Precision >= 0
                ? v.ToString("G" + prec.ToString(ci), ci)
                : v.ToString("G", ci),
            _ => v.ToString("F" + prec.ToString(ci), ci),
        };
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
