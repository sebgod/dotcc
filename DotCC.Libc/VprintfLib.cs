#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace DotCC.Libc;

/// <summary>
/// The <c>v…printf</c> family (C11 7.21.6.8 to 7.21.6.14): the printf family with its arguments in a
/// <c>va_list</c>. The fluent builders take each argument at its static type from the call site; here the
/// types come from the format instead, as C's own implementation reads them: each conversion takes the next
/// argument at the type its length modifier and specifier name. CPython's <c>PyOS_snprintf</c> and
/// <c>PyErr_Format</c> reach every formatted message through these.
/// </summary>
public static unsafe partial class Libc
{
    /// <summary>One argument a format pulled from a <see cref="VaList"/>, at the type its conversion names.</summary>
    private readonly record struct VFormatArg(char Kind, long Signed, ulong Unsigned, double Real, nint Pointer);

    /// <summary><c>vfprintf(stream, fmt, ap)</c>: <c>fprintf</c> with its arguments taken from <paramref name="ap"/>.
    /// Returns the bytes written.</summary>
    public static int vfprintf(FILE* stream, byte* fmt, VaList ap) => VFormat(WriterFor(stream), fmt, ap);

    /// <summary><c>vprintf(fmt, ap)</c> is <c>vfprintf(stdout, fmt, ap)</c>.</summary>
    public static int vprintf(byte* fmt, VaList ap) => vfprintf(stdout, fmt, ap);

    /// <summary><c>vsnprintf(dst, n, fmt, ap)</c>: <c>snprintf</c> with its arguments taken from
    /// <paramref name="ap"/>. At most <paramref name="n"/> - 1 bytes are stored, NUL-terminated (nothing when
    /// <paramref name="n"/> is 0); returns the length the whole output would have had.</summary>
    public static int vsnprintf(byte* dst, ulong n, byte* fmt, VaList ap)
    {
        var bytes = VFormatBytes(fmt, ap);
        if (n > 0)
        {
            var keep = (int)Math.Min((ulong)bytes.Length, n - 1);
            for (var i = 0; i < keep; i++) { dst[i] = bytes[i]; }
            dst[keep] = 0;
        }
        return bytes.Length;
    }

    /// <summary><c>vsprintf(dst, fmt, ap)</c>: <c>sprintf</c> with its arguments taken from <paramref name="ap"/>.</summary>
    public static int vsprintf(byte* dst, byte* fmt, VaList ap)
    {
        var bytes = VFormatBytes(fmt, ap);
        for (var i = 0; i < bytes.Length; i++) { dst[i] = bytes[i]; }
        dst[bytes.Length] = 0;
        return bytes.Length;
    }

    /// <summary>The bytes <paramref name="fmt"/> formats to, as <see cref="SprintfBuilder.Done"/> stores them: one
    /// byte per char (ISO-8859-1), the byte values <c>%c</c> produced.</summary>
    private static byte[] VFormatBytes(byte* fmt, VaList ap)
    {
        var buf = new StringWriter();
        VFormat(buf, fmt, ap);
        return System.Text.Encoding.Latin1.GetBytes(buf.ToString());
    }

    /// <summary>Write <paramref name="fmt"/> to <paramref name="writer"/>, its conversions filled from
    /// <paramref name="ap"/>: a <c>*</c> width or precision takes an <c>int</c> (a negative width is the
    /// <c>-</c> flag, a negative precision none), a length modifier picks the argument's width, and the
    /// resolved format and typed arguments go to a <see cref="PrintfBuilder"/>.</summary>
    private static int VFormat(TextWriter writer, byte* fmt, VaList ap)
    {
        var spec = new List<byte>();
        var args = new List<VFormatArg>();
        var p = fmt;
        while (*p != 0)
        {
            if (*p != (byte)'%') { spec.Add(*p++); continue; }
            spec.Add(*p++);
            if (*p == (byte)'%') { spec.Add(*p++); continue; }
            while (*p is (byte)'-' or (byte)'+' or (byte)' ' or (byte)'#' or (byte)'0') { spec.Add(*p++); }
            if (*p == (byte)'*')
            {
                p++;
                var width = (int)ap.Next();
                if (width < 0) { spec.Add((byte)'-'); width = -width; }
                AppendDecimal(spec, width);
            }
            else
            {
                while (*p is >= (byte)'0' and <= (byte)'9') { spec.Add(*p++); }
            }
            if (*p == (byte)'.')
            {
                p++;
                if (*p == (byte)'*')
                {
                    p++;
                    var precision = (int)ap.Next();
                    if (precision >= 0) { spec.Add((byte)'.'); AppendDecimal(spec, precision); }
                }
                else
                {
                    spec.Add((byte)'.');
                    while (*p is >= (byte)'0' and <= (byte)'9') { spec.Add(*p++); }
                }
            }
            // The length modifier: `long`-sized for l, ll, j, z, t, q (LP64), `long double` (a double) for L.
            var wide = false;
            while (*p is (byte)'h' or (byte)'l' or (byte)'L' or (byte)'j' or (byte)'z' or (byte)'t' or (byte)'q')
            {
                wide |= *p is (byte)'l' or (byte)'j' or (byte)'z' or (byte)'t' or (byte)'q';
                p++;
            }
            var conv = *p;
            if (conv == 0) { break; }
            p++;
            switch ((char)conv)
            {
                case 'd' or 'i':
                    args.Add(new VFormatArg(wide ? 'l' : 'i', wide ? (long)ap.Next() : (int)ap.Next(), 0, 0, 0));
                    break;
                case 'u' or 'o' or 'x' or 'X':
                    args.Add(new VFormatArg(wide ? 'L' : 'I', 0, wide ? (ulong)ap.Next() : (uint)ap.Next(), 0, 0));
                    break;
                case 'c':
                    args.Add(new VFormatArg('i', (int)ap.Next(), 0, 0, 0));
                    break;
                case 'f' or 'F' or 'e' or 'E' or 'g' or 'G' or 'a' or 'A':
                    args.Add(new VFormatArg('d', 0, 0, (double)ap.Next(), 0));
                    break;
                case 's':
                    args.Add(new VFormatArg('s', 0, 0, 0, (nint)ap.NextPtr()));
                    break;
                case 'p':
                    args.Add(new VFormatArg('p', 0, 0, 0, (nint)ap.NextPtr()));
                    break;
                case 'n':
                    // `%n` stores the count so far and prints nothing: its pointer is consumed, the
                    // store is not modeled, and the conversion leaves the format.
                    ap.NextPtr();
                    spec.RemoveRange(spec.LastIndexOf((byte)'%'), spec.Count - spec.LastIndexOf((byte)'%'));
                    continue;
            }
            spec.Add(conv);
        }
        spec.Add(0);
        var resolved = spec.ToArray();
        fixed (byte* f = resolved)
        {
            var builder = new PrintfBuilder(writer, f);
            foreach (var a in args)
            {
                builder = a.Kind switch
                {
                    'i' => builder.Arg((int)a.Signed),
                    'l' => builder.Arg(a.Signed),
                    'I' => builder.Arg((uint)a.Unsigned),
                    'L' => builder.Arg(a.Unsigned),
                    'd' => builder.Arg(a.Real),
                    's' => builder.Arg((byte*)a.Pointer),
                    _ => builder.Arg((void*)a.Pointer),
                };
            }
            return builder.Done();
        }
    }

    /// <summary>Append <paramref name="value"/>'s decimal digits to a format being resolved.</summary>
    private static void AppendDecimal(List<byte> spec, int value)
    {
        foreach (var ch in value.ToString(System.Globalization.CultureInfo.InvariantCulture)) { spec.Add((byte)ch); }
    }
}
