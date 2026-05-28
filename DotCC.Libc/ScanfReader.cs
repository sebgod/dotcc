#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// Fluent <c>scanf</c> / <c>fscanf</c> / <c>sscanf</c> reader. Each
/// <see cref="Read(int*)"/> / <see cref="Read(double*)"/> /
/// <see cref="Read(byte*)"/> consumes the next <c>%</c> spec from the
/// format pointer and writes the parsed value to the out pointer.
/// <see cref="Done"/> returns the count of successful matches (real
/// scanf's return value).
/// </summary>
/// <remarks>
/// v1 supported specs: <c>%d</c>/<c>%i</c> (int), <c>%f</c>/<c>%e</c>/<c>%g</c>
/// (double), <c>%s</c> (whitespace-delimited token → NUL-terminated UTF-8),
/// <c>%c</c> (single byte). Length modifiers (<c>l</c>/<c>h</c>/<c>z</c>)
/// and width specs are accepted and ignored. Parsing rules mirror C's:
/// leading whitespace skipped before each conversion (except <c>%c</c>);
/// numeric parsing stops at the first non-conforming char; <c>%s</c> stops
/// at whitespace or EOF.
/// </remarks>
public unsafe ref struct ScanfReader
{
    private readonly TextReader _r;
    private byte* _fmt;
    private int _matched;

    internal ScanfReader(TextReader r, byte* fmt)
    {
        _r = r;
        _fmt = fmt;
        _matched = 0;
    }

    public ScanfReader Read(int* dst)
    {
        var spec = ExpectSpec();
        if (spec != (byte)'d' && spec != (byte)'i') { return this; }
        SkipInputWs();
        var sb = new StringBuilder();
        int peek = _r.Peek();
        if (peek == '-' || peek == '+') { sb.Append((char)_r.Read()); }
        while ((peek = _r.Peek()) != -1 && peek >= '0' && peek <= '9')
        {
            sb.Append((char)_r.Read());
        }
        if (sb.Length > 0 && int.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
        {
            *dst = v;
            _matched++;
        }
        return this;
    }

    public ScanfReader Read(double* dst)
    {
        var spec = ExpectSpec();
        if (spec != (byte)'f' && spec != (byte)'e' && spec != (byte)'g' && spec != (byte)'l') { return this; }
        SkipInputWs();
        var sb = new StringBuilder();
        int peek = _r.Peek();
        if (peek == '-' || peek == '+') { sb.Append((char)_r.Read()); }
        bool sawDigit = false;
        bool sawDot = false;
        bool sawExp = false;
        while ((peek = _r.Peek()) != -1)
        {
            if (peek >= '0' && peek <= '9') { sawDigit = true; sb.Append((char)_r.Read()); }
            else if (peek == '.' && !sawDot && !sawExp) { sawDot = true; sb.Append((char)_r.Read()); }
            else if ((peek == 'e' || peek == 'E') && sawDigit && !sawExp)
            {
                sawExp = true;
                sb.Append((char)_r.Read());
                int p2 = _r.Peek();
                if (p2 == '-' || p2 == '+') { sb.Append((char)_r.Read()); }
            }
            else { break; }
        }
        if (sawDigit && double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            *dst = v;
            _matched++;
        }
        return this;
    }

    public ScanfReader Read(byte* dst)
    {
        var spec = ExpectSpec();
        if (spec != (byte)'s' && spec != (byte)'c') { return this; }
        if (spec != (byte)'c') { SkipInputWs(); }
        // Hoisted once — CA2014 (no per-iteration stackalloc inside the loop).
        Span<char> chBuf = stackalloc char[1];
        Span<byte> b = stackalloc byte[4];
        int written = 0;
        while (true)
        {
            int peek = _r.Peek();
            if (peek == -1) { break; }
            if (spec == (byte)'s' && char.IsWhiteSpace((char)peek)) { break; }
            // ASCII fast path — caller's buffer is byte* (UTF-8). For
            // multi-byte chars, encode via Encoding.UTF8.
            int ch = _r.Read();
            if (ch < 0x80)
            {
                dst[written++] = (byte)ch;
            }
            else
            {
                chBuf[0] = (char)ch;
                int n = Encoding.UTF8.GetBytes(chBuf, b);
                for (int i = 0; i < n; i++) { dst[written++] = b[i]; }
            }
            if (spec == (byte)'c') { break; }
        }
        if (spec != (byte)'c') { dst[written] = 0; }
        if (written > 0) { _matched++; }
        return this;
    }

    public int Done() => _matched;

    private byte ExpectSpec()
    {
        // Whitespace in fmt matches any (incl. zero) input whitespace.
        // Skip ws bytes in fmt first; then expect %; then flags/width;
        // then the spec letter.
        while (*_fmt != 0 && IsAsciiWs(*_fmt)) { _fmt++; }
        if (*_fmt != (byte)'%') { return 0; }
        _fmt++;
        while (*_fmt != 0 && IsFlagOrWidthChar(*_fmt)) { _fmt++; }
        var spec = *_fmt;
        if (spec != 0) { _fmt++; }
        return spec;
    }

    private void SkipInputWs()
    {
        int c;
        while ((c = _r.Peek()) != -1 && char.IsWhiteSpace((char)c)) { _r.Read(); }
    }

    private static bool IsAsciiWs(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsFlagOrWidthChar(byte b) =>
        b is (byte)'*' or (byte)'.' or (byte)'l' or (byte)'L' or (byte)'h' or (byte)'z'
           or >= (byte)'0' and <= (byte)'9';
}
