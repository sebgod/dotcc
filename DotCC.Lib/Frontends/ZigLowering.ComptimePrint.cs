#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>
/// <c>std.fmt.comptimePrint(fmt, args)</c> (task #128), formatted while lowering. zig runs its whole
/// <c>std.Io.Writer</c> engine at compile time and types the result <c>*const [count(fmt, args):0]u8</c>, a
/// return type that needs <c>count</c> evaluated before the call can bind; dotcc instead formats the
/// comptime-known arguments itself, by zig's rules, and the call becomes the string literal it produces
/// (the same <see cref="LitStr"/> a spelled literal lowers to, so it coerces the same way).
/// </summary>
/// <remarks>
/// Covered: integers (<c>{}</c>, <c>{d}</c>, <c>{x}</c>, <c>{X}</c>, <c>{b}</c>, <c>{o}</c>, <c>{c}</c>, <c>{u}</c>), bools
/// (<c>{}</c>, <c>{any}</c>), strings (<c>{s}</c>), positional arguments (<c>{1s}</c>), <c>{{</c> / <c>}}</c>, and
/// <c>:[fill][alignment][width][.precision]</c> (a precision only changes a float, so it is ignored), and named arguments
/// over a struct (<c>{[a]d}</c>). A runtime argument is refused, as zig refuses it; a float, an enum's <c>{t}</c> or another
/// specifier is a loud cut.
/// </remarks>
internal sealed partial class ZigLowering
{
    /// <summary>One comptime-known <c>comptimePrint</c> argument: exactly one of <see cref="Str"/>, <see cref="Int"/>
    /// and <see cref="Bool"/> is set. <see cref="Signed"/> is the integer's type signedness (a non-negative comptime_int
    /// formats as unsigned, as zig's <c>IntFittingRange</c> makes it).</summary>
    private readonly record struct ComptimeFmtArg(string? Str, System.Int128? Int, bool Signed, bool? Bool);

    /// <summary>Lower <c>std.fmt.comptimePrint(fmt, args)</c> to the string literal it formats (task #128).</summary>
    private CExpr LowerComptimePrint(IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != 2)
        {
            throw new IrUnsupportedException($"std.fmt.comptimePrint takes a format and an argument tuple (got {argItems.Count} arguments)");
        }
        var fmt = ComptimeByteText(argItems[0])
            ?? throw new IrUnsupportedException("std.fmt.comptimePrint: the format must be a comptime-known string");
        IReadOnlyList<Item> elems = argItems[1].Content switch
        {
            Zig.AnonStructInit a => Flatten(a.Arg2),
            Zig.AnonStructInitEmpty => [],
            _ => throw new IrUnsupportedException("std.fmt.comptimePrint: the arguments must be an anonymous tuple literal `.{…}`"),
        };
        // A tuple's arguments are taken in order or by position; a struct's (`.{ .a = 5 }`) by name, `{[a]d}`.
        var args = new List<ComptimeFmtArg>(elems.Count);
        var names = new List<string?>(elems.Count);
        foreach (var e in elems)
        {
            var (name, valueItem) = e.Content switch
            {
                Zig.FieldInitPositional pos => ((string?)null, pos.Arg0),
                Zig.FieldInit named => (Tok(named.Arg1), named.Arg3),
                _ => throw new IrUnsupportedException("std.fmt.comptimePrint: " + (e.Content?.GetType().Name ?? "null") + " in the argument tuple"),
            };
            args.Add(ComptimePrintArg(valueItem, args.Count));
            names.Add(name);
        }
        return ComptimePrintLiteral(FormatComptime(fmt, args, names));
    }

    /// <summary>The string literal for formatted <paramref name="bytes"/>, spelled readably: printable ASCII as itself and
    /// any other byte as a bounded octal escape (a C <c>\x</c> escape would run into a following hex digit).</summary>
    private static LitStr ComptimePrintLiteral(IReadOnlyList<int> bytes)
    {
        var sb = new StringBuilder("\"");
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and < 0x7F && b != '"' && b != '\\') { sb.Append((char)b); }
            else { AppendOctalByte(sb, (byte)b); }
        }
        sb.Append('"');
        var segs = new List<string> { sb.ToString() };
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
    }

    /// <summary>Evaluate one <c>comptimePrint</c> argument at compile time: a comptime string, a bool, or an integer
    /// (whose signedness is its type's, or a comptime_int's sign).</summary>
    private ComptimeFmtArg ComptimePrintArg(Item item, int index)
    {
        if (ComptimeByteText(item) is { } text) { return new ComptimeFmtArg(text, null, false, null); }
        var lowered = LowerExpr(item);
        var folded = _ir.EvalComptimeValue(lowered);
        if (lowered is LitBool lb) { return new ComptimeFmtArg(null, null, false, lb.Value); }
        if (folded is IrModule.CtBool cb) { return new ComptimeFmtArg(null, null, false, cb.Value); }
        System.Int128? value = folded is IrModule.CtInt ci ? ci.Value : ComptimeIntValue(lowered) is { } l ? l : null;
        if (value is { } v && lowered.Type?.Unqualified is CType.Prim { Integer: true } or CType.Enum or null)
        {
            var signed = IsComptimeIntOperand(item, lowered)
                ? v < 0
                : lowered.Type?.Unqualified is CType.Prim { Signed: true };
            return new ComptimeFmtArg(null, v, signed, null);
        }
        throw new IrUnsupportedException(
            $"std.fmt.comptimePrint: argument {index} is not a comptime-known integer, bool or string "
            + "(zig requires every argument comptime-known; dotcc formats those three kinds at compile time)");
    }

    /// <summary>A comptime string as its BYTES, one char per byte (the model this formatter works in, so a width counts
    /// bytes, as zig's does): a literal decodes its escapes through the shared string encoder; another comptime string
    /// (a capture, a const) is taken as spelled.</summary>
    private string? ComptimeByteText(Item item)
        => EvalComptimeValue(item) is LitStr lit
            ? new string(DotCC.EmitHelpers.StringByteValues(lit.Segments).Select(b => (char)b).ToArray())
            : ComptimeStringArg(item);

    /// <summary>Format <paramref name="fmt"/> over <paramref name="args"/> by zig's <c>std.Io.Writer.print</c> rules, to
    /// the bytes of the result. Placeholders take the next argument unless they name one by position, and every
    /// argument must be used, as zig checks.</summary>
    private static List<int> FormatComptime(string fmt, IReadOnlyList<ComptimeFmtArg> args, IReadOnlyList<string?> names)
    {
        var sb = new StringBuilder();
        var next = 0;
        var used = new bool[args.Count];
        for (var i = 0; i < fmt.Length; i++)
        {
            var ch = fmt[i];
            if (ch == '}')
            {
                if (i + 1 < fmt.Length && fmt[i + 1] == '}') { sb.Append('}'); i++; continue; }
                throw new IrUnsupportedException("std.fmt.comptimePrint: missing opening {");
            }
            if (ch != '{') { sb.Append(ch); continue; }
            if (i + 1 < fmt.Length && fmt[i + 1] == '{') { sb.Append('{'); i++; continue; }
            var close = fmt.IndexOf('}', i + 1);
            if (close < 0) { throw new IrUnsupportedException("std.fmt.comptimePrint: missing closing }"); }
            var spec = fmt.Substring(i + 1, close - i - 1);
            i = close;
            var colon = spec.IndexOf(':');
            var head = colon < 0 ? spec : spec[..colon];
            var options = colon < 0 ? "" : spec[(colon + 1)..];
            int argIndex;
            string specifier;
            if (head.StartsWith('[') && head.IndexOf(']') is var nameEnd and > 0)
            {
                var argName = head[1..nameEnd];
                argIndex = names.ToList().IndexOf(argName);
                if (argIndex < 0) { throw new IrUnsupportedException($"std.fmt.comptimePrint: no argument named '{argName}'"); }
                specifier = head[(nameEnd + 1)..];
            }
            else
            {
                var digits = 0;
                while (digits < head.Length && char.IsAsciiDigit(head[digits])) { digits++; }
                argIndex = digits > 0 ? int.Parse(head[..digits], CultureInfo.InvariantCulture) : next++;
                specifier = head[digits..];
            }
            if (argIndex >= args.Count) { throw new IrUnsupportedException("std.fmt.comptimePrint: too few arguments"); }
            used[argIndex] = true;
            var (fill, alignment, width) = ParseFmtOptions(options, spec);
            var arg = args[argIndex];
            var body = FormatComptimeArg(arg, specifier, width is > 0, spec);
            sb.Append(Pad(body, fill, alignment, width));
        }
        if (System.Array.IndexOf(used, false) is var unused and >= 0)
        {
            throw new IrUnsupportedException($"std.fmt.comptimePrint: unused argument {unused} in '{fmt}'");
        }
        return sb.ToString().Select(c => (int)c).ToList();
    }

    /// <summary>The <c>[fill][alignment][width]</c> after a placeholder's <c>:</c>. zig's default alignment is right,
    /// for numbers and strings alike, and its default fill a space. A precision (<c>.N</c>) only changes a float, which
    /// this formatter does not take, so it is read and ignored, as zig ignores it for an integer or a string.</summary>
    private static (string Fill, char Alignment, int? Width) ParseFmtOptions(string options, string spec)
    {
        if (options.IndexOf('.') is var dot and >= 0)
        {
            if (!options[(dot + 1)..].All(char.IsAsciiDigit))
            {
                throw new IrUnsupportedException($"std.fmt.comptimePrint: unsupported format options `{{{spec}}}`");
            }
            options = options[..dot];
        }
        var fill = " ";
        var alignment = '>';
        var rest = options;
        // A fill is one byte followed by an alignment; an alignment alone has the default fill. zig's Placeholder.parse
        // refuses a non-ASCII fill ("expected . or }"), so it is refused here too rather than padded with.
        if (rest.Length > 0 && rest[0] >= (char)0x80)
        {
            throw new IrUnsupportedException($"std.fmt.comptimePrint: expected . or }}, found a non-ASCII fill in `{{{spec}}}`");
        }
        const int firstLen = 1;
        if (rest.Length > firstLen && rest[firstLen] is '<' or '^' or '>')
        {
            fill = rest[..firstLen];
            alignment = rest[firstLen];
            rest = rest[(firstLen + 1)..];
        }
        else if (rest.Length > 0 && rest[0] is '<' or '^' or '>')
        {
            alignment = rest[0];
            rest = rest[1..];
        }
        if (rest.Length == 0) { return (fill, alignment, null); }
        if (!rest.All(char.IsAsciiDigit))
        {
            throw new IrUnsupportedException($"std.fmt.comptimePrint: unsupported format options `{{{spec}}}`");
        }
        return (fill, alignment, int.Parse(rest, CultureInfo.InvariantCulture));
    }

    /// <summary>One argument by its specifier, before padding. A signed integer shows a <c>+</c> when a width is given
    /// (zig's <c>formatInt</c>); a negative one prints its sign and magnitude in every base.</summary>
    private static string FormatComptimeArg(ComptimeFmtArg arg, string specifier, bool hasWidth, string spec)
    {
        if (arg.Str is { } s)
        {
            return specifier == "s"
                ? s
                : throw new IrUnsupportedException(
                    $"std.fmt.comptimePrint: cannot format a string with `{{{spec}}}` (zig requires {{s}} for a slice of bytes)");
        }
        if (arg.Bool is { } b)
        {
            return specifier is "" or "any"
                ? (b ? "true" : "false")
                : throw new IrUnsupportedException($"std.fmt.comptimePrint: cannot format a bool with `{{{spec}}}`");
        }
        var v = arg.Int ?? throw new System.InvalidOperationException("a comptimePrint argument with no value");
        switch (specifier)
        {
            case "c":
                return ((char)(byte)v).ToString();
            case "u":
                return new string(Encoding.UTF8.GetBytes(char.ConvertFromUtf32((int)v)).Select(u => (char)u).ToArray());
        }
        var magnitude = System.Int128.Abs(v);
        var digits = specifier switch
        {
            "" or "d" or "any" => magnitude.ToString(CultureInfo.InvariantCulture),
            "x" => ToBase(magnitude, 16, false),
            "X" => ToBase(magnitude, 16, true),
            "b" => ToBase(magnitude, 2, false),
            "o" => ToBase(magnitude, 8, false),
            _ => throw new IrUnsupportedException($"std.fmt.comptimePrint: the specifier `{{{spec}}}` is not supported yet"),
        };
        var sign = v < 0 ? "-" : arg.Signed && hasWidth ? "+" : "";
        return sign + digits;
    }

    /// <summary>A non-negative integer's digits in <paramref name="radix"/>.</summary>
    private static string ToBase(System.Int128 value, int radix, bool upper)
    {
        if (value == 0) { return "0"; }
        var alphabet = upper ? "0123456789ABCDEF" : "0123456789abcdef";
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, alphabet[(int)(value % radix)]);
            value /= radix;
        }
        return sb.ToString();
    }

    /// <summary>Pad <paramref name="body"/> to <paramref name="width"/> bytes with <paramref name="fill"/>: on the
    /// left for <c>&gt;</c>, the right for <c>&lt;</c>, and split for <c>^</c> (the smaller half on the left).</summary>
    private static string Pad(string body, string fill, char alignment, int? width)
    {
        var length = body.Length;
        if (width is not { } w || w <= length) { return body; }
        var pad = w - length;
        var (left, right) = alignment switch
        {
            '<' => (0, pad),
            '^' => (pad / 2, pad - pad / 2),
            _ => (pad, 0),
        };
        return string.Concat(Enumerable.Repeat(fill, left)) + body + string.Concat(Enumerable.Repeat(fill, right));
    }
}
