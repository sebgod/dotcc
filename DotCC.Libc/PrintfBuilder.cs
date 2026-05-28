#nullable enable

using System;
using System.Globalization;
using System.IO;

namespace DotCC.Libc;

/// <summary>
/// Fluent printf-format consumer. Each <c>Arg</c> overload finds the next
/// <c>%</c> spec in the format pointer and writes the value to the bound
/// <see cref="TextWriter"/>. Literal text between specs (and after the last
/// one) is flushed by <see cref="Done"/>.
/// </summary>
/// <remarks>
/// Why a ref struct rather than <c>params object[]</c>: pointer types
/// (<c>byte*</c>) can't be boxed, and printf's <c>%s</c> in our lowering
/// takes a <c>byte*</c>. The fluent shape sidesteps boxing entirely. The
/// struct holds a <see cref="TextWriter"/> reference (so it can be obtained
/// from <c>fprintf(stream, fmt)</c>) and a <c>byte*</c> cursor.
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

    public PrintfBuilder Arg(int v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        switch (spec)
        {
            case (byte)'d': case (byte)'i': _w.Write(v); break;
            case (byte)'x': _w.Write(v.ToString("x", ci)); break;
            case (byte)'X': _w.Write(v.ToString("X", ci)); break;
            case (byte)'c': _w.Write((char)v); break;
            // ISO C: %f default precision is 6. Match real printf so the
            // same source produces identical output under dotcc and clang.
            case (byte)'f': _w.Write(((double)v).ToString("F6", ci)); break;
            case (byte)'e': _w.Write(((double)v).ToString("E6", ci)); break;
            case (byte)'g': _w.Write(((double)v).ToString("G", ci)); break;
            default: _w.Write(v); break;
        }
        return this;
    }

    public PrintfBuilder Arg(double v)
    {
        var spec = ConsumeUntilSpec();
        var ci = CultureInfo.InvariantCulture;
        switch (spec)
        {
            case (byte)'f': _w.Write(v.ToString("F6", ci)); break;
            case (byte)'e': _w.Write(v.ToString("E6", ci)); break;
            case (byte)'g': _w.Write(v.ToString("G", ci)); break;
            case (byte)'d': case (byte)'i': _w.Write((int)v); break;
            default: _w.Write(v.ToString("F6", ci)); break;
        }
        return this;
    }

    // Float gets promoted to double — real C vararg rule.
    public PrintfBuilder Arg(float v) => Arg((double)v);

    public PrintfBuilder Arg(byte* v)
    {
        var spec = ConsumeUntilSpec();
        if (spec == (byte)'s' && v != null)
        {
            int len = 0;
            while (v[len] != 0) { len++; }
            _w.Write(System.Text.Encoding.UTF8.GetString(v, len));
        }
        else if (v == null)
        {
            _w.Write("(null)");
        }
        else
        {
            // Wrong spec for a pointer → print the address.
            _w.Write(((IntPtr)v).ToString("X"));
        }
        return this;
    }

    public int Done()
    {
        // Flush trailing literal portion after the last consumed % spec.
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
        return 0; // real printf returns int (count); 0 here for simplicity
    }

    private byte ConsumeUntilSpec()
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
                while (*_fmt != 0 && IsFlagOrWidthChar(*_fmt)) { _fmt++; }
                var spec = *_fmt;
                if (spec != 0) { _fmt++; }
                return spec;
            }
            WriteUtf8Codepoint(ref _fmt);
        }
        return 0;
    }

    private static bool IsFlagOrWidthChar(byte b) =>
        b is (byte)'-' or (byte)'+' or (byte)' ' or (byte)'#' or (byte)'0'
           or (byte)'.' or (byte)'l' or (byte)'L' or (byte)'h' or (byte)'z'
           or >= (byte)'1' and <= (byte)'9';

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
