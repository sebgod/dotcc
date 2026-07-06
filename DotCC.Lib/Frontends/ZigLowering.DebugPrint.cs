#nullable enable

using System.Collections.Generic;
using System.Text;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Curated <c>std.debug.print</c> (wall-plan W6) — the biggest remaining <c>std</c> idiom, and
/// the last brick of the monomorphization arc. <c>std.debug.print("{d} {s}\n", .{n, s})</c> writes a
/// formatted line to STDERR (exactly as real Zig does — <c>std.debug.print</c> is stderr, not stdout).
///
/// <para>No comptime reflection is needed (the AOT rule): the format string is a comptime literal, so it
/// is parsed AT LOWERING TIME and its <c>{…}</c> placeholders paired POSITIONALLY with the argument
/// tuple's elements — the tuple is right there in the call, its arity and element types already known.
/// Each placeholder is translated to the equivalent C <c>printf</c> conversion, and the whole thing
/// lowers to <c>fprintf(stderr, "&lt;C-fmt&gt;").Arg(…).Done()</c> over dotcc's existing printf-builder
/// (the "printf-builder precedent"). Because the runtime <c>PrintfBuilder</c> keys formatting off the
/// actual <c>.Arg(…)</c> overload (int / long / ulong / byte* / …), the C conversion needs no length
/// modifier — <c>{d}</c>/<c>{}</c> → <c>%d</c> for every integer width, <c>{x}</c> → <c>%x</c>,
/// <c>{c}</c> → <c>%c</c>, <c>{s}</c> → <c>%s</c>.</para>
///
/// <para>Curated subset (the rest is a loud, specific cut — <c>std</c> stays a curated-paths resolver):
/// the placeholders <c>{}</c>, <c>{d}</c>, <c>{s}</c>, <c>{c}</c>, <c>{x}</c>, <c>{X}</c> on integer /
/// string-pointer arguments; <c>{{</c> / <c>}}</c> escapes; a plain string-literal format. Cuts: a width /
/// alignment / named specifier (<c>{d:0>5}</c>, <c>{[name]}</c>), <c>{any}</c>, a float / bool / slice /
/// struct argument, and a non-literal format or non-<c>.{…}</c> argument tuple.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>A synthetic reference to libc's <c>stderr</c> stream (a <c>FILE*</c>). Rendered verbatim
    /// as <c>stderr</c> — the emitted program's <c>using static Libc;</c> surfaces it, and the runtime
    /// <c>fprintf</c> routes it to <see cref="System.Console.Error"/>. Marked <c>FromSystemHeader</c> so
    /// no declaration is emitted for it (it is provided by the spliced runtime, exactly like a libc
    /// prototype a Zig <c>extern fn</c> call routes to by bare name).</summary>
    private static readonly Symbol StderrSymbol = new()
    {
        Name = "stderr",
        TargetName = "stderr",
        Kind = SymKind.Var,
        Type = new CType.Pointer(CType.Void),
        IsGlobal = true,
        FromSystemHeader = true,
    };

    /// <summary>Lower a curated <c>std.debug</c> call (wall-plan W6). Only <c>std.debug.print(fmt,
    /// .{args})</c> is modeled; any other member (<c>assert</c>, <c>panic</c>, …) is a clear cut.</summary>
    private CExpr LowerStdDebugCall(string method, IReadOnlyList<Item> argItems)
    {
        if (method != "print")
        {
            throw new IrUnsupportedException(
                $"zig `std.debug.{method}` is not supported yet — only `std.debug.print` is modeled (wall-plan W6)");
        }
        if (argItems.Count != 2)
        {
            throw new IrUnsupportedException(
                "zig `std.debug.print` takes exactly two arguments — a comptime format string and an argument tuple `.{…}`");
        }
        // The format string must be a plain string literal (a comptime-const format is a V1 cut).
        if (argItems[0].Content is not Zig.StrLit fmtLit)
        {
            throw new IrUnsupportedException(
                "zig `std.debug.print`: the format string must be a string literal (wall-plan W6)");
        }
        // The arguments are the positional elements of the `.{…}` tuple literal — lowered here so the
        // format translation can pair each `{…}` placeholder with its argument's type.
        var argExprs = ExtractDebugPrintArgs(argItems[1]);

        // Translate the Zig format → a C printf format string, pairing placeholders with arg types.
        var cFmtLexeme = TranslateZigDebugFormat(Tok(fmtLit.Arg0), argExprs);
        var segs = new List<string> { cFmtLexeme };
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        var fmtExpr = new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };

        // `fprintf(stderr, fmt).Arg(a).Arg(b)….Done()` — the backend's printf-family fluent lowering
        // (fixed args: the stream + the format; the rest become `.Arg(…)` in order).
        var callArgs = new List<CExpr> { new VarRef(StderrSymbol) { Type = StderrSymbol.Type }, fmtExpr };
        callArgs.AddRange(argExprs);
        return new Call("fprintf", callArgs, ParamTypes: null, CalleeSym: null) { Type = CType.Void };
    }

    /// <summary>Extract the argument tuple's positional elements (<c>.{a, b, …}</c>) and lower each. An
    /// empty <c>.{}</c> yields no arguments; a non-literal tuple or a named element is a V1 cut (the
    /// elements must be known syntactically to pair with the format placeholders).</summary>
    private List<CExpr> ExtractDebugPrintArgs(Item tupleItem)
    {
        IReadOnlyList<Item> elems;
        switch (tupleItem.Content)
        {
            case Zig.AnonStructInit a: elems = Flatten(a.Arg2); break;   // `.{ e0, e1, … }`
            case Zig.AnonStructInitEmpty: return new List<CExpr>();       // `.{}`
            default:
                throw new IrUnsupportedException(
                    "zig `std.debug.print`: the arguments must be an anonymous tuple literal `.{…}` (wall-plan W6)");
        }
        var result = new List<CExpr>(elems.Count);
        foreach (var e in elems)
        {
            if (e.Content is not Zig.FieldInitPositional pos)
            {
                throw new IrUnsupportedException(
                    "zig `std.debug.print`: the argument tuple must be positional (`.{a, b}`), not `.{ .field = … }`");
            }
            result.Add(LowerExpr(pos.Arg0));
        }
        return result;
    }

    /// <summary>Translate a Zig format string (the raw quoted lexeme) into a C <c>printf</c> format
    /// string (a re-quoted lexeme fed back through the shared string-literal encoder), pairing each
    /// <c>{…}</c> placeholder with the corresponding argument's type. The walk runs on the ESCAPED
    /// lexeme so backslash escapes (<c>\n</c>, <c>\xNN</c>, …) pass through untouched for the shared
    /// decoder; <c>{{</c>/<c>}}</c> fold to literal braces, a literal <c>%</c> is doubled to <c>%%</c>
    /// (printf-escaped), and the placeholder count must match the argument count (as real Zig
    /// enforces at comptime).</summary>
    private string TranslateZigDebugFormat(string rawLexeme, IReadOnlyList<CExpr> args)
    {
        // A `\\`-prefixed multiline string as a format is a rare V1 cut; a normal `"…"` literal expands
        // its `\u{…}` escapes to `\xNN` first (same reshaping the ordinary StrLit path does).
        if (rawLexeme.StartsWith("\\", System.StringComparison.Ordinal))
        {
            throw new IrUnsupportedException(
                "zig `std.debug.print`: a multiline (`\\\\`) format string is not supported yet (wall-plan W6)");
        }
        var inner = UnquoteStringLiteral(ExpandZigUnicodeEscapes(rawLexeme));
        var sb = new StringBuilder(inner.Length + 8);
        int ai = 0;
        int i = 0;
        while (i < inner.Length)
        {
            char ch = inner[i];
            if (ch == '\\')
            {
                // An escape sequence — copy the backslash and its selector verbatim; the remaining
                // characters (hex/octal digits) are ordinary literals and copied on later iterations.
                sb.Append(ch);
                if (i + 1 < inner.Length) { sb.Append(inner[i + 1]); i += 2; } else { i++; }
                continue;
            }
            if (ch == '{')
            {
                if (i + 1 < inner.Length && inner[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                int close = inner.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new IrUnsupportedException("zig `std.debug.print`: unmatched `{` in the format string");
                }
                var spec = inner.Substring(i + 1, close - (i + 1));
                if (ai >= args.Count)
                {
                    throw new IrUnsupportedException(
                        $"zig `std.debug.print`: more `{{…}}` placeholders than the {args.Count} argument(s) supplied");
                }
                sb.Append(DebugConv(spec, args[ai].Type));
                ai++;
                i = close + 1;
                continue;
            }
            if (ch == '}')
            {
                if (i + 1 < inner.Length && inner[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                throw new IrUnsupportedException(
                    "zig `std.debug.print`: unmatched `}` in the format string (use `}}` for a literal brace)");
            }
            if (ch == '%') { sb.Append("%%"); i++; continue; }   // printf-escape a literal percent
            sb.Append(ch);
            i++;
        }
        if (ai != args.Count)
        {
            throw new IrUnsupportedException(
                $"zig `std.debug.print`: {args.Count} argument(s) but {ai} `{{…}}` placeholder(s) — they must match");
        }
        return "\"" + sb + "\"";
    }

    /// <summary>Map one Zig format placeholder spec (the text between the braces) + its argument's type
    /// to the equivalent C <c>printf</c> conversion. The runtime builder keys off the actual argument
    /// overload, so no length modifier is needed. The curated subset (wall-plan W6): <c>{}</c>/<c>{d}</c>
    /// (integer decimal), <c>{c}</c> (char), <c>{x}</c>/<c>{X}</c> (hex), <c>{s}</c> (string pointer).
    /// A width / alignment / named specifier, <c>{any}</c>, or a type the conversion doesn't fit is a
    /// loud, specific cut.</summary>
    private static string DebugConv(string spec, CType? argType)
    {
        spec = spec.Trim();
        if (spec.Contains(':') || spec.Contains('['))
        {
            throw new IrUnsupportedException(
                $"zig `std.debug.print`: the format specifier `{{{spec}}}` (width / alignment / named) is not supported yet (wall-plan W6)");
        }
        switch (spec)
        {
            case "":
            case "d": RequireInt(spec, argType); return "%d";
            case "c": RequireInt(spec, argType); return "%c";
            case "x": RequireInt(spec, argType); return "%x";
            case "X": RequireInt(spec, argType); return "%X";
            case "s": RequireStr(argType); return "%s";
            default:
                throw new IrUnsupportedException(
                    $"zig `std.debug.print`: the format placeholder `{{{spec}}}` is not supported yet — "
                    + "wall-plan W6 supports {}, {d}, {s}, {c}, {x}, {X}");
        }
    }

    /// <summary>Require an integer argument for an integer conversion (<c>{}</c>/<c>{d}</c>/<c>{c}</c>/
    /// <c>{x}</c>). A <c>bool</c> is excluded (Zig prints it as <c>true</c>/<c>false</c>, which C
    /// <c>%d</c> can't match); a float / slice / struct is a clear cut.</summary>
    private static void RequireInt(string spec, CType? argType)
    {
        if (argType?.Unqualified is CType.Prim { Name: not "_Bool" } p && p.IsInteger) { return; }
        var shown = spec.Length == 0 ? "{}" : $"{{{spec}}}";
        throw new IrUnsupportedException(
            $"zig `std.debug.print`: the `{shown}` placeholder needs an integer argument, got "
            + $"`{argType?.Describe() ?? "?"}` (float / bool / slice / struct formatting is not supported yet — wall-plan W6)");
    }

    /// <summary>Require a NUL-terminated string-pointer argument for <c>{s}</c> — a string literal /
    /// <c>[*:0]const u8</c> / <c>[*c]const u8</c> (a byte pointer or char array). A slice (<c>[]const
    /// u8</c>) is a V1 cut: Zig's <c>{s}</c> prints exactly <c>.len</c> bytes, while C <c>%s</c> reads to
    /// a NUL — they can diverge, so it's rejected rather than silently mismatched.</summary>
    private static void RequireStr(CType? argType)
    {
        var t = argType?.Unqualified;
        bool ok = t switch
        {
            CType.Pointer ptr => IsByteSized(ptr.Pointee),
            CType.Array arr => IsByteSized(arr.Element),
            _ => false,
        };
        if (ok) { return; }
        throw new IrUnsupportedException(
            $"zig `std.debug.print`: the `{{s}}` placeholder needs a NUL-terminated string pointer "
            + $"(a string literal / `[*:0]const u8`), got `{argType?.Describe() ?? "?"}` — a slice `{{s}}` is not supported yet (wall-plan W6)");
    }

    /// <summary>True for a one-byte integer element (a <c>char</c> / <c>u8</c> / <c>i8</c>) — the element
    /// of a C string, so a pointer/array of it decays to the <c>byte*</c> that <c>%s</c> expects.</summary>
    private static bool IsByteSized(CType elem)
        => elem.Unqualified is CType.Prim p && p.IsInteger && p.Bytes == 1;
}
