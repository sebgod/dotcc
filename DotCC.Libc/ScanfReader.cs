#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// Fluent <c>scanf</c> / <c>fscanf</c> / <c>sscanf</c> reader. Each
/// <see cref="Read(int*)"/> / <see cref="Read(double*)"/> /
/// <see cref="Read(byte*)"/> only records its target pointer, in order. <see cref="Done"/>
/// then reads the input, stores every conversion and returns the count of successful matches
/// (real scanf's return value), so every argument expression is evaluated before any input
/// is consumed or any target stored (C11 6.5.2.2p10, dotcc #249).
/// </summary>
/// <remarks>
/// Supported conversions: <c>%d</c>/<c>%i</c>/<c>%u</c> (decimal int),
/// <c>%x</c>/<c>%X</c> (hex int), <c>%o</c> (octal int), <c>%f</c>/<c>%e</c>/<c>%g</c>
/// (double), <c>%s</c> (whitespace-delimited token → NUL-terminated UTF-8),
/// <c>%c</c> (byte(s)). A <b>maximum field width</b> (<c>%3d</c>, <c>%8s</c>) is
/// honored — the conversion consumes at most that many input characters. Length
/// modifiers (<c>l</c>/<c>L</c>/<c>h</c>/<c>z</c>/<c>j</c>/<c>t</c>) are accepted
/// and ignored (the receiving <c>Read</c> overload already carries the type).
/// Parsing rules mirror C's: leading whitespace skipped before each conversion
/// (except <c>%c</c>); numeric parsing stops at the first non-conforming char;
/// <c>%s</c> stops at whitespace or EOF.
/// <para>
/// A conversion the routed overload can't satisfy — a genuinely unsupported spec
/// (<c>%n</c>, a <c>%[…]</c> scanset), or a format/argument-type mismatch (a
/// float spec against an <c>int*</c>) — <b>throws</b> <see cref="FormatException"/>
/// rather than silently skipping it. dotcc fails loudly, never silently wrong.
/// Assignment-suppression (<c>%*d</c>) is not modeled (it would desync the fluent
/// <c>.Read(ptr)</c> chain); the <c>*</c> is skipped so the spec letter still parses.
/// </para>
/// </remarks>
public unsafe ref struct ScanfReader
{
    private enum Kind : byte { Int, Double, Bytes, Wide }

    private TextReader? _r;   // the stream, or null until Done builds it from a string source
    private readonly byte* _src;   // sscanf's source, read at Done (null otherwise)
    private readonly char* _wsrc;   // swscanf's source, read at Done (null otherwise)
    private byte* _fmt;
    private int _matched;
    private (Kind Kind, nint Ptr)[]? _targets;   // recorded by Read, in order; allocated by the first one
    private int _n;

    internal ScanfReader(TextReader r, byte* fmt)
    {
        _r = r;
        _src = null;
        _wsrc = null;
        _fmt = fmt;
        _matched = 0;
    }

    /// <summary>A reader over the NUL-terminated byte string <paramref name="src"/>, which is only
    /// read at <see cref="Done"/>, after every argument has been evaluated.</summary>
    internal ScanfReader(byte* src, byte* fmt)
    {
        _r = null;
        _src = src;
        _wsrc = null;
        _fmt = fmt;
        _matched = 0;
    }

    /// <summary>A reader over the NUL-terminated wide string <paramref name="src"/>, read at <see cref="Done"/>.</summary>
    internal ScanfReader(char* src, byte* fmt)
    {
        _r = null;
        _src = null;
        _wsrc = src;
        _fmt = fmt;
        _matched = 0;
    }

    private void Record(Kind kind, nint ptr)
    {
        _targets ??= new (Kind, nint)[4];
        if (_n == _targets.Length) { Array.Resize(ref _targets, _n * 2); }
        _targets[_n++] = (kind, ptr);
    }

    /// <summary>Records an <c>int*</c> target for the next conversion; nothing is read or stored until <see cref="Done"/>.</summary>
    public ScanfReader Read(int* dst) { Record(Kind.Int, (nint)dst); return this; }

    /// <summary>Records a <c>double*</c> target for the next conversion.</summary>
    public ScanfReader Read(double* dst) { Record(Kind.Double, (nint)dst); return this; }

    /// <summary>Records a <c>%s</c> / <c>%c</c> byte target for the next conversion.</summary>
    public ScanfReader Read(byte* dst) { Record(Kind.Bytes, (nint)dst); return this; }

    /// <summary>Records a wide <c>%s</c> / <c>%c</c> target (<c>wchar_t*</c>, C# <c>char*</c>) for the
    /// <c>w*scanf</c> family; narrow <c>scanf</c> never targets a <c>char*</c>.</summary>
    public ScanfReader Read(char* dst) { Record(Kind.Wide, (nint)dst); return this; }

    private void ScanInt(TextReader r, int* dst)
    {
        var spec = ExpectSpec(out int width);
        int @base = spec switch
        {
            (byte)'d' or (byte)'i' or (byte)'u' => 10,
            (byte)'x' or (byte)'X' => 16,
            (byte)'o' => 8,
            _ => throw Unsupported(spec, "an int (%d %i %u %x %X %o)"),
        };
        SkipInputWs(r);
        int consumed = 0;
        bool neg = false;
        int peek = r.Peek();
        if ((peek == '-' || peek == '+') && (width < 0 || consumed < width))
        {
            neg = peek == '-';
            r.Read();
            consumed++;
        }
        long val = 0;
        bool any = false;
        while ((width < 0 || consumed < width) && (peek = r.Peek()) != -1)
        {
            int d = DigitValue(peek, @base);
            if (d < 0) { break; }
            val = val * @base + d;
            any = true;
            r.Read();
            consumed++;
        }
        if (any)
        {
            *dst = (int)(neg ? -val : val);
            _matched++;
        }
    }

    private void ScanDouble(TextReader r, double* dst)
    {
        var spec = ExpectSpec(out int width);
        if (spec != (byte)'f' && spec != (byte)'e' && spec != (byte)'g'
            && spec != (byte)'F' && spec != (byte)'E' && spec != (byte)'G')
        {
            throw Unsupported(spec, "a double (%f %e %g)");
        }
        SkipInputWs(r);
        int consumed = 0;
        var sb = new StringBuilder();
        int peek = r.Peek();
        if ((peek == '-' || peek == '+') && (width < 0 || consumed < width))
        {
            sb.Append((char)r.Read());
            consumed++;
        }
        bool sawDigit = false;
        bool sawDot = false;
        bool sawExp = false;
        while ((width < 0 || consumed < width) && (peek = r.Peek()) != -1)
        {
            if (peek >= '0' && peek <= '9') { sawDigit = true; sb.Append((char)r.Read()); consumed++; }
            else if (peek == '.' && !sawDot && !sawExp) { sawDot = true; sb.Append((char)r.Read()); consumed++; }
            else if ((peek == 'e' || peek == 'E') && sawDigit && !sawExp && (width < 0 || consumed < width))
            {
                sawExp = true;
                sb.Append((char)r.Read());
                consumed++;
                int p2 = r.Peek();
                if ((p2 == '-' || p2 == '+') && (width < 0 || consumed < width)) { sb.Append((char)r.Read()); consumed++; }
            }
            else { break; }
        }
        if (sawDigit && double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            *dst = v;
            _matched++;
        }
    }

    private void ScanBytes(TextReader r, byte* dst)
    {
        var spec = ExpectSpec(out int width);
        if (spec != (byte)'s' && spec != (byte)'c')
        {
            throw Unsupported(spec, "a string/char (%s %c)");
        }
        // %c: default field width 1 (read exactly one byte); %s: unbounded unless capped.
        int max = width >= 0 ? width : (spec == (byte)'c' ? 1 : int.MaxValue);
        if (spec != (byte)'c') { SkipInputWs(r); }
        // Hoisted once — CA2014 (no per-iteration stackalloc inside the loop).
        Span<char> chBuf = stackalloc char[1];
        Span<byte> b = stackalloc byte[4];
        int written = 0;
        int count = 0;
        while (count < max)
        {
            int peek = r.Peek();
            if (peek == -1) { break; }
            if (spec == (byte)'s' && char.IsWhiteSpace((char)peek)) { break; }
            // ASCII fast path — caller's buffer is byte* (UTF-8). For
            // multi-byte chars, encode via Encoding.UTF8.
            int ch = r.Read();
            count++;
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
        }
        if (spec != (byte)'c') { dst[written] = 0; }
        if (written > 0) { _matched++; }
    }

    /// <summary>Wide <c>%s</c> / <c>%c</c> scan: stores UTF-16 code units straight from the reader (no
    /// UTF-8 encoding); <c>%s</c> stops at whitespace/EOF and NUL-terminates, <c>%c</c> reads a single
    /// unit (or <c>width</c> units).</summary>
    private void ScanWide(TextReader r, char* dst)
    {
        var spec = ExpectSpec(out int width);
        if (spec != (byte)'s' && spec != (byte)'c')
        {
            throw Unsupported(spec, "a wide string/char (%ls %lc)");
        }
        int max = width >= 0 ? width : (spec == (byte)'c' ? 1 : int.MaxValue);
        if (spec != (byte)'c') { SkipInputWs(r); }
        int written = 0;
        while (written < max)
        {
            int peek = r.Peek();
            if (peek == -1) { break; }
            if (spec == (byte)'s' && char.IsWhiteSpace((char)peek)) { break; }
            dst[written++] = (char)r.Read();
        }
        if (spec != (byte)'c') { dst[written] = '\0'; }
        if (written > 0) { _matched++; }
    }

    /// <summary>Reads the input and stores every conversion into its recorded target, in order, and returns the
    /// match count. An unsupported or mismatched conversion throws <see cref="FormatException"/> here.</summary>
    public int Done()
    {
        TextReader r = _r ?? new StringReader(_src != null
            ? System.Text.Encoding.UTF8.GetString(_src, Libc.strlen(_src))
            : new string(_wsrc, 0, Libc.wcslen(_wsrc)));
        if (_targets is { } targets)
        {
            for (int i = 0; i < _n; i++)
            {
                var (kind, ptr) = targets[i];
                switch (kind)
                {
                    case Kind.Int: ScanInt(r, (int*)ptr); break;
                    case Kind.Double: ScanDouble(r, (double*)ptr); break;
                    case Kind.Bytes: ScanBytes(r, (byte*)ptr); break;
                    default: ScanWide(r, (char*)ptr); break;
                }
            }
        }
        return _matched;
    }

    /// <summary>Consume the next <c>%</c> spec from the format: skip leading fmt
    /// whitespace, the <c>%</c>, an assignment-suppression <c>*</c> (unmodeled),
    /// the optional max field <paramref name="width"/>, and the length modifier;
    /// return the conversion letter (0 at end of format).</summary>
    private byte ExpectSpec(out int width)
    {
        width = -1;
        // Whitespace in fmt matches any (incl. zero) input whitespace.
        while (*_fmt != 0 && IsAsciiWs(*_fmt)) { _fmt++; }
        if (*_fmt != (byte)'%') { return 0; }
        _fmt++;
        // Assignment suppression '%*d' isn't modeled by the fluent .Read chain
        // (no arg is consumed, which would desync); skip so the letter parses.
        if (*_fmt == (byte)'*') { _fmt++; }
        // Maximum field width.
        while (*_fmt >= (byte)'0' && *_fmt <= (byte)'9')
        {
            if (width < 0) { width = 0; }
            width = width * 10 + (*_fmt - (byte)'0');
            _fmt++;
        }
        // Length modifiers — recognized, ignored (the Read overload has the type).
        while (*_fmt is (byte)'l' or (byte)'L' or (byte)'h' or (byte)'z' or (byte)'j' or (byte)'t') { _fmt++; }
        var spec = *_fmt;
        if (spec != 0) { _fmt++; }
        return spec;
    }

    private static void SkipInputWs(TextReader r)
    {
        int c;
        while ((c = r.Peek()) != -1 && char.IsWhiteSpace((char)c)) { r.Read(); }
    }

    /// <summary>The value of ASCII digit <paramref name="c"/> in <paramref name="base"/>,
    /// or -1 if it is not a digit of that base.</summary>
    private static int DigitValue(int c, int @base)
    {
        int v = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return v >= 0 && v < @base ? v : -1;
    }

    /// <summary>A loud, named failure for a conversion the routed overload can't
    /// satisfy — the dotcc "fail loudly, never silently wrong" contract for scanf.</summary>
    private static FormatException Unsupported(byte spec, string expected)
    {
        string s = spec == 0 ? "<end of format>" : "%" + (char)spec;
        return new FormatException(
            $"dotcc scanf: conversion '{s}' is not supported for {expected}. " +
            "Supported: %d %i %u %x %X %o (int), %f %e %g (double), %s %c (string/char); " +
            "%n and %[...] scansets are not implemented.");
    }

    private static bool IsAsciiWs(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
