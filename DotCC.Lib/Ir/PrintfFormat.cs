#nullable enable

using System.Collections.Generic;

namespace DotCC.Ir;

/// <summary>
/// Backend-neutral parser for C99 <c>printf</c> format strings. Splits a format
/// into an ordered list of <see cref="Segment"/>s — literal byte runs and
/// conversion specs — so a backend can walk them and decide how to lower each.
/// <para>This deliberately mirrors the grammar of the runtime
/// <c>DotCC.Libc.PrintfBuilder.ParseSpec</c>; the two cannot literally share code
/// (the compiler library is safe/managed and lives in a different assembly than
/// the unsafe ref-struct runtime, and neither references the other), so they are
/// kept in lockstep by tests instead. The wat backend uses this for compile-time
/// printf expansion; the C# backend defers parsing to the runtime builder and
/// does not use it.</para>
/// </summary>
internal static class PrintfFormat
{
    /// <summary>A parsed conversion spec — <c>%[flags][width][.precision][length]conv</c>.
    /// <see cref="Width"/>/<see cref="Precision"/> are -1 when unspecified;
    /// <see cref="Length"/> is <c>'\0'</c> when no length modifier was present.
    /// Length modifiers are recorded but a backend that keys off the IR argument
    /// type (as the wat one does) need not consult them.</summary>
    internal readonly record struct Spec(
        char Conv, int Width, int Precision,
        bool Left, bool Zero, bool Plus, bool Space, bool Alt, char Length)
    {
        /// <summary>True when any flag, a width, or a precision is set — the
        /// formatting features a minimal backend may not implement yet.</summary>
        internal bool HasFormatting =>
            Width >= 0 || Precision >= 0 || Left || Zero || Plus || Space || Alt;
    }

    /// <summary>One piece of a format string: a run of literal bytes
    /// (<see cref="Literal"/> set, with <c>%%</c> already folded to a single
    /// <c>%</c>), or a conversion (<see cref="Conversion"/> set). Exactly one is
    /// non-null.</summary>
    internal readonly record struct Segment(IReadOnlyList<int>? Literal, Spec? Conversion)
    {
        internal bool IsLiteral => Literal is not null;
    }

    /// <summary>Parse <paramref name="fmt"/> (raw byte values, e.g. from
    /// <c>EmitHelpers.StringByteValues</c>) into ordered segments.</summary>
    internal static List<Segment> Parse(IReadOnlyList<int> fmt)
    {
        var segs = new List<Segment>();
        var lit = new List<int>();
        void FlushLit()
        {
            if (lit.Count > 0) { segs.Add(new Segment(new List<int>(lit), null)); lit.Clear(); }
        }

        var i = 0;
        while (i < fmt.Count)
        {
            var b = fmt[i] & 0xFF;
            if (b != '%') { lit.Add(b); i++; continue; }
            // "%%" is a literal percent; any other "%" begins a conversion.
            if (i + 1 < fmt.Count && (fmt[i + 1] & 0xFF) == '%') { lit.Add('%'); i += 2; continue; }
            FlushLit();
            i++;
            segs.Add(new Segment(null, ParseSpec(fmt, ref i)));
        }
        FlushLit();
        return segs;
    }

    /// <summary>Parse one spec starting just past the leading <c>%</c>, advancing
    /// <paramref name="i"/> past the conversion char (or to end-of-string for a
    /// truncated spec). Mirrors <c>PrintfBuilder.ParseSpec</c>.</summary>
    private static Spec ParseSpec(IReadOnlyList<int> fmt, ref int i)
    {
        bool left = false, zero = false, plus = false, space = false, alt = false;
        int width = -1, prec = -1;
        char length = '\0';

        // Flags. A leading '0' is the zero-pad flag; subsequent digits are width.
        while (i < fmt.Count)
        {
            switch (fmt[i] & 0xFF)
            {
                case '-': left = true; i++; continue;
                case '+': plus = true; i++; continue;
                case ' ': space = true; i++; continue;
                case '0': zero = true; i++; continue;
                case '#': alt = true; i++; continue;
            }
            break;
        }
        while (i < fmt.Count && (fmt[i] & 0xFF) is >= '0' and <= '9')
        {
            if (width < 0) { width = 0; }
            width = width * 10 + ((fmt[i] & 0xFF) - '0');
            i++;
        }
        if (i < fmt.Count && (fmt[i] & 0xFF) == '.')
        {
            i++;
            prec = 0;
            while (i < fmt.Count && (fmt[i] & 0xFF) is >= '0' and <= '9')
            {
                prec = prec * 10 + ((fmt[i] & 0xFF) - '0');
                i++;
            }
        }
        // Length modifiers — recorded (last one wins, e.g. "ll" → 'l'), but the
        // wat backend keys conversions off the IR argument type instead.
        while (i < fmt.Count && (fmt[i] & 0xFF) is 'l' or 'L' or 'h' or 'z')
        {
            length = (char)(fmt[i] & 0xFF);
            i++;
        }
        var conv = '\0';
        if (i < fmt.Count) { conv = (char)(fmt[i] & 0xFF); i++; }
        return new Spec(conv, width, prec, left, zero, plus, space, alt, length);
    }
}
