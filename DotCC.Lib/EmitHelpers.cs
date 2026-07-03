#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace DotCC;

/// <summary>
/// Stateless C#-emission helpers shared by the typed-IR backend
/// (<see cref="DotCC.Ir.IrBuilder"/> / <see cref="DotCC.Backends.CSharpBackend"/>) and the
/// <see cref="Compiler"/> shell: C#-keyword escaping (<see cref="Id"/>),
/// C string-literal decoding/encoding (<see cref="EncodeStringLiteral(IReadOnlyList{string})"/>
/// / <see cref="StringByteValues"/>), and the <see cref="Export"/> descriptor for
/// library-mode wrappers.
/// </summary>
/// <remarks>
/// These were carved out of the original syntax-directed <c>CSharpEmitter</c>
/// visitor when the typed IR became the sole backend (the visitor itself was
/// deleted): single source of truth for identifier/string lowering so the IR
/// codegen reuses the exact escape logic rather than reimplementing it.
/// </remarks>
internal static class EmitHelpers
{
    // Exports list: each non-static (external-linkage) C function definition.
    // Tuple is (cName, csharpReturnType, csharpParamList). Library mode reads
    // this list to emit a matching [UnmanagedCallersOnly(EntryPoint = "name")]
    // wrapper per entry; the wrappers delegate to the user-method body so
    // both internal C-to-C calls (direct method invocation) and external
    // C-to-native consumers work without each other knowing.
    public readonly record struct Export(string Name, string ReturnType, string Params);

    // ---- C#-keyword escaping --------------------------------------------
    // A C identifier can be a C# reserved keyword (`new`, `lock`, `is`,
    // `string`, `this`, `ref`, …) — all valid C names but illegal as bare C#
    // identifiers. C# allows them when prefixed with `@`, so we escape any such
    // name wherever a C identifier is emitted AS a C# identifier (declarators,
    // references, params, fields, member access, labels, enum constants).
    // Escaping is purely a function of the name, so a declaration and all its
    // references escape identically — consistency is automatic. CRUCIALLY this
    // is applied only at the *emit* point: structured data, side-table keys,
    // and the static/malloc name-mangling all keep the RAW C name, so they
    // never see an `@`.
    private static readonly HashSet<string> _csReservedKeywords = new(StringComparer.Ordinal)
    {
        // `true` and `false` ARE escaped: they now lower to the integer
        // literals 1/0 (via <stdbool.h> and the c23 LitTrue/LitFalse path), so
        // the spelling `true`/`false` only ever reaches Visit(Var) as a real
        // user identifier — safe to @-escape. `null` is still EXCLUDED: dotcc
        // emits it as the bare C# `null` literal (the only expression that
        // implicitly converts to any pointer type — see <stddef.h>'s
        // `#define NULL null`), and a macro-supplied `null` is indistinguishable
        // from a user variable named `null`, so a variable named `null` stays
        // the lone residual edge. `default` is also omitted: it's a C keyword
        // (never a C identifier) and dotcc emits it for value-init.
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal",
        "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while",
    };

    /// <summary>
    /// Escape a C identifier for emission as a C# identifier: prefix it with
    /// <c>@</c> when it collides with a C# reserved keyword, otherwise return
    /// it unchanged. (Most C keywords here — <c>int</c>, <c>for</c>, … — can
    /// never be C identifiers, so the rule fires only for the C#-only reserved
    /// words like <c>new</c>/<c>lock</c>/<c>string</c>.)
    /// </summary>
    internal static string Id(string name) =>
        _csReservedKeywords.Contains(name) ? "@" + name : name;

    // ---- C string/char escape decoding ----------------------------------
    // One element of a decoded body: either a literal source character (to be
    // UTF-8 encoded by the C# u8 literal) or a decoded escape byte (0–255).
    private readonly record struct StrItem(bool IsByte, int Value);

    private static string StripStrQuotes(string raw) =>
        raw is { Length: >= 2 } ? raw[1..^1] : "";

    // Decode the char or escape sequence starting at body[i], advancing i past
    // it. Handles the named escapes, GNU `\e`, 1–3-digit octal, and greedy
    // `\xNN` hex — each to its value masked to <paramref name="escapeMask"/>
    // (0xFF for a byte string, 0xFFFF for a char16_t string); an unknown escape
    // yields the char.
    private static StrItem DecodeEscapeOrChar(string body, ref int i, int escapeMask = 0xFF)
    {
        char c = body[i];
        if (c != '\\' || i + 1 >= body.Length) { i++; return new StrItem(false, c); }
        i++;                  // consume the backslash
        char e = body[i];
        i++;                  // consume the escape selector (octal/hex re-advance below)
        switch (e)
        {
            case 'n':  return new StrItem(true, 0x0A);
            case 't':  return new StrItem(true, 0x09);
            case 'r':  return new StrItem(true, 0x0D);
            case '\\': return new StrItem(true, 0x5C);
            case '"':  return new StrItem(true, 0x22);
            case '\'': return new StrItem(true, 0x27);
            case '?':  return new StrItem(true, 0x3F);
            case 'a':  return new StrItem(true, 0x07);
            case 'b':  return new StrItem(true, 0x08);
            case 'f':  return new StrItem(true, 0x0C);
            case 'v':  return new StrItem(true, 0x0B);
            case 'e':  return new StrItem(true, 0x1B);  // GNU \e (ESC)
            case 'x':
            {
                int val = 0, cnt = 0;
                while (i < body.Length && Uri.IsHexDigit(body[i])) { val = val * 16 + HexVal(body[i]); i++; cnt++; }
                if (cnt == 0) { throw new CompileException("`\\x` used with no following hex digits"); }
                return new StrItem(true, val & escapeMask);
            }
            case >= '0' and <= '7':
            {
                int val = e - '0', cnt = 1;
                while (i < body.Length && cnt < 3 && body[i] is >= '0' and <= '7') { val = val * 8 + (body[i] - '0'); i++; cnt++; }
                return new StrItem(true, val & escapeMask);
            }
            default: return new StrItem(false, e);  // unknown escape → the char itself
        }
    }

    private static void DecodeCStringBody(string body, List<StrItem> into, int escapeMask = 0xFF)
    {
        var i = 0;
        while (i < body.Length) { into.Add(DecodeEscapeOrChar(body, ref i, escapeMask)); }
    }

    private static int HexVal(char c) => c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;

    /// <summary>Decode a character-literal body (the chars BETWEEN the quotes) to its codepoint
    /// value — a single char, a named/GNU escape, octal, or <c>\xNN</c> hex — reusing the same
    /// escape machinery as the string lowering (<see cref="DecodeEscapeOrChar"/>). Used by the Zig
    /// front-end for <c>'x'</c> literals (Zig char literals are <c>comptime_int</c> = the codepoint;
    /// their escape set overlaps C's for the cases dotcc lexes). An empty body yields 0.</summary>
    internal static int DecodeCharLiteral(string body)
    {
        if (body.Length == 0) { return 0; }
        var i = 0;
        return DecodeEscapeOrChar(body, ref i).Value;
    }

    // Re-emit decoded items as a greedy-safe C# u8-literal body, returning the
    // escaped text and byte length. Source chars pass through (the u8 literal
    // UTF-8-encodes them — matching C's UTF-8 source bytes); decoded escape
    // bytes become a named escape or `\xHH`, and a `\xHH` is never left next to
    // a literal hex digit (C# would greedily fold it into the escape). A decoded
    // escape byte > 0x7F can't be one byte in a u8 literal (C# UTF-8-encodes
    // `\x80`+ as multi-byte); the string-literal lowering routes such strings to
    // EmitByteArray before reaching here, so the guard below is defensive.
    private static (string Escaped, int ByteLen) EmitU8(List<StrItem> items)
    {
        var sb = new StringBuilder(items.Count + 8);
        var len = 0;
        var prevHex = false;
        foreach (var it in items)
        {
            if (!it.IsByte)
            {
                char ch = (char)it.Value;
                if (ch < 0x80)
                {
                    len += 1;
                    if (ch == '"') { sb.Append("\\\""); prevHex = false; }
                    else if (ch == '\\') { sb.Append("\\\\"); prevHex = false; }
                    else if (ch is >= (char)0x20 and <= (char)0x7E)
                    {
                        if (prevHex && Uri.IsHexDigit(ch)) { sb.Append("\\x").Append(((int)ch).ToString("X2")); prevHex = true; }
                        else { sb.Append(ch); prevHex = false; }
                    }
                    else { sb.Append("\\x").Append(((int)ch).ToString("X2")); prevHex = true; }
                }
                else
                {
                    // Non-ASCII source char → emit literally; the u8 literal
                    // UTF-8-encodes it (matches C's UTF-8 source bytes).
                    len += System.Text.Encoding.UTF8.GetByteCount(ch.ToString());
                    sb.Append(ch);
                    prevHex = false;
                }
            }
            else
            {
                int b = it.Value;
                if (b > 0x7F)
                {
                    // Defensive: the string lowering sends high-byte strings to
                    // the byte-array path, so this should be unreachable.
                    throw new CompileException(
                        $"string escape byte 0x{b:X2} > 0x7F reached the u8-literal path "
                        + "(expected the byte-array lowering) — please report this.");
                }
                len += 1;
                switch (b)
                {
                    case 0x0A: sb.Append("\\n"); prevHex = false; break;
                    case 0x0D: sb.Append("\\r"); prevHex = false; break;
                    case 0x09: sb.Append("\\t"); prevHex = false; break;
                    case 0x22: sb.Append("\\\""); prevHex = false; break;
                    case 0x5C: sb.Append("\\\\"); prevHex = false; break;
                    default: sb.Append("\\x").Append(b.ToString("X2")); prevHex = true; break;
                }
            }
        }
        return (sb.ToString(), len);
    }

    // Build the EXACT C byte sequence as a C# constant byte-array initializer
    // (`new byte[]{ 0xHH, …, 0 }`, NUL-terminated). Used when a decoded escape
    // byte > 0x7F can't ride a u8 literal (C# would UTF-8 re-encode `\x80`+ into
    // two bytes). Each decoded escape byte goes in verbatim; a source char is
    // expanded to its UTF-8 bytes (matching C's UTF-8 source encoding, exactly
    // as the u8 path does). Roslyn lowers `new byte[]{consts}` in a
    // ReadOnlySpan<byte> position to an RVA blob — fixed address, no allocation,
    // no GC move — so L()'s pinned pointer stays valid for the program lifetime,
    // identical to the u8-literal case. Returns the initializer text and the
    // byte length (excluding the NUL the caller accounts for).
    private static (string Text, int ByteLen) EmitByteArray(List<StrItem> items)
    {
        var bytes = new List<int>(items.Count + 1);
        foreach (var it in items)
        {
            if (it.IsByte) { bytes.Add(it.Value & 0xFF); }
            else
            {
                foreach (var u in System.Text.Encoding.UTF8.GetBytes(((char)it.Value).ToString()))
                {
                    bytes.Add(u);
                }
            }
        }
        var sb = new StringBuilder("new byte[]{ ");
        foreach (var b in bytes) { sb.Append("0x").Append(b.ToString("X2")).Append(", "); }
        sb.Append("0 }");  // NUL terminator
        return (sb.ToString(), bytes.Count);
    }

    /// <summary>Decode adjacent C string-literal segments to their exact byte
    /// values — UTF-8 for source chars, verbatim for escapes — EXCLUDING the NUL
    /// terminator (the caller appends and zero-pads). Used by the IR to lower
    /// <c>char s[] = "…"</c> to a mutable byte buffer.</summary>
    internal static List<int> StringByteValues(IReadOnlyList<string> rawQuotedSegments)
    {
        var items = new List<StrItem>();
        foreach (var seg in rawQuotedSegments) { DecodeCStringBody(StripStrQuotes(seg), items); }
        var bytes = new List<int>(items.Count);
        foreach (var it in items)
        {
            if (it.IsByte) { bytes.Add(it.Value & 0xFF); }
            else { foreach (var u in System.Text.Encoding.UTF8.GetBytes(((char)it.Value).ToString())) { bytes.Add(u); } }
        }
        return bytes;
    }

    /// <summary>
    /// Encode one or more adjacent C string-literal segments (each the RAW
    /// quoted lexeme, e.g. <c>"a\n"</c>) to the lowered <c>Libc.L(…)</c>
    /// expression — decoding escapes per-segment and concatenating. Exposed so
    /// the C# backend (<see cref="DotCC.Backends.CSharpBackend"/>) reuses this escape logic
    /// rather than reimplementing it — single source of truth for string lowering.
    /// </summary>
    internal static string EncodeStringLiteral(IReadOnlyList<string> rawQuotedSegments)
        => EncodeStringLiteral(rawQuotedSegments, out _);

    /// <summary>
    /// As <see cref="EncodeStringLiteral(IReadOnlyList{string})"/>, additionally
    /// reporting <paramref name="byteLength"/> — the C array size of the literal
    /// (decoded byte count INCLUDING the NUL terminator). That is exactly
    /// <c>sizeof</c> of the string literal, which the IR uses to type a string as
    /// <c>char[N]</c> (a string literal does NOT decay under <c>sizeof</c>).
    /// </summary>
    internal static string EncodeStringLiteral(IReadOnlyList<string> rawQuotedSegments, out int byteLength)
    {
        var items = new List<StrItem>();
        foreach (var seg in rawQuotedSegments) { DecodeCStringBody(StripStrQuotes(seg), items); }
        if (items.Exists(it => it.IsByte && it.Value > 0x7F))
        {
            var (arr, n) = EmitByteArray(items);
            byteLength = n + 1;   // + NUL
            return $"Libc.L({arr})";
        }
        var (escaped, len) = EmitU8(items);
        byteLength = len + 1;     // + NUL
        return $"Libc.L(\"{escaped}\\0\"u8)";
    }

    // ---- char16_t (UTF-16) literals -------------------------------------
    // A char16_t literal is a sequence of UTF-16 code units. A decoded source
    // char's StrItem.Value is ALREADY a UTF-16 code unit (the .NET `char` read
    // from the source — a non-BMP char arrives as a surrogate PAIR, i.e. two
    // items, which is exactly correct UTF-16). An escape's value is taken at full
    // 16-bit width (escapeMask 0xFFFF) rather than the byte path's 0xFF. So for
    // char16_t the code unit is simply `it.Value` for both kinds — no UTF-8
    // re-encoding (that's the byte path's job).

    /// <summary>Decode adjacent <c>u"…"</c> segments (each already <c>u</c>-prefix
    /// stripped to a plain quoted lexeme) to their UTF-16 code units, EXCLUDING the
    /// NUL terminator (the caller appends / zero-pads). Used to lower
    /// <c>char16_t s[] = u"…"</c> to a mutable C# <c>char</c> buffer.</summary>
    internal static List<int> StringU16Values(IReadOnlyList<string> rawQuotedSegments)
    {
        var items = new List<StrItem>();
        foreach (var seg in rawQuotedSegments) { DecodeCStringBody(StripStrQuotes(seg), items, 0xFFFF); }
        var units = new List<int>(items.Count);
        foreach (var it in items) { units.Add(it.Value & 0xFFFF); }
        return units;
    }

    /// <summary>Encode adjacent <c>u"…"</c> segments to the lowered
    /// <c>Libc.L16("…")</c> expression — a pooled, pinned <c>char*</c> (= char16_t*)
    /// over UTF-16 data. The emitted C# string literal carries the same code units
    /// plus a trailing NUL (<c> </c>), so the pointer is NUL-terminated like a
    /// C string literal.</summary>
    internal static string EncodeU16StringLiteral(IReadOnlyList<string> rawQuotedSegments)
    {
        var units = StringU16Values(rawQuotedSegments);
        var sb = new StringBuilder("Libc.L16(\"");
        foreach (var cu in units) { AppendCsStringChar(sb, cu); }
        sb.Append("\\u0000\")");   // NUL terminator
        return sb.ToString();
    }

    /// <summary>Append one UTF-16 code unit to a C# string-literal body: printable
    /// ASCII verbatim (escaping <c>"</c> and <c>\</c>), everything else as the
    /// unambiguous <c>\uXXXX</c> form.</summary>
    private static void AppendCsStringChar(StringBuilder sb, int cu)
    {
        switch (cu)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case >= 0x20 and <= 0x7E: sb.Append((char)cu); break;
            default: sb.Append("\\u").Append(cu.ToString("X4")); break;
        }
    }

    // ---- char32_t (UTF-32) literals -------------------------------------
    // A char32_t literal is a sequence of UTF-32 code units (one per Unicode
    // scalar). Decoding yields UTF-16 StrItems — a source astral char arrives as a
    // surrogate PAIR (two items), which is exactly correct UTF-16 — so we let the
    // BCL fold UTF-16 → UTF-32 (System.Text.Rune) rather than hand-rolling
    // surrogate arithmetic. That's the char32_t payoff: `U"😀"` is ONE code unit
    // (+ NUL), whereas the char16_t `u"😀"` is two surrogates (+ NUL).

    /// <summary>Rebuild the decoded body of adjacent wide segments as a UTF-16 C#
    /// string: source chars pass through (an astral char stays a surrogate pair) and
    /// a decoded escape ≥ U+10000 is expanded to its surrogate pair. The shared step
    /// for both the char32_t code-unit count and the emitted <c>L32</c> literal.</summary>
    private static string DecodeWideBodyToUtf16(IReadOnlyList<string> rawQuotedSegments)
    {
        // 0x1FFFFF is a CONTIGUOUS 21-bit mask that covers the whole Unicode range
        // (max scalar U+10FFFF) — NOT 0x10FFFF, which is the max value but not a
        // bitmask (0x1F600 & 0x10FFFF would clear the 0x0F0000 nibble → 0xF600).
        var items = new List<StrItem>();
        foreach (var seg in rawQuotedSegments) { DecodeCStringBody(StripStrQuotes(seg), items, 0x1FFFFF); }
        var sb = new StringBuilder(items.Count);
        foreach (var it in items)
        {
            if (it.Value >= 0x10000 && Rune.IsValid(it.Value)) { sb.Append(char.ConvertFromUtf32(it.Value)); }
            else { sb.Append((char)(it.Value & 0xFFFF)); }
        }
        return sb.ToString();
    }

    /// <summary>Decode adjacent <c>U"…"</c> segments (each already <c>U</c>-prefix
    /// stripped to a plain quoted lexeme) to their UTF-32 code units — Unicode
    /// scalars — EXCLUDING the NUL terminator (the caller appends / zero-pads). The
    /// BCL's rune enumeration folds UTF-16 surrogate pairs into single scalars. Used
    /// to lower <c>char32_t s[] = U"…"</c> to a mutable C# <c>uint</c> buffer.</summary>
    internal static List<int> StringU32Values(IReadOnlyList<string> rawQuotedSegments)
    {
        var body = DecodeWideBodyToUtf16(rawQuotedSegments);
        var units = new List<int>(body.Length);
        foreach (var r in body.EnumerateRunes()) { units.Add(r.Value); }
        return units;
    }

    /// <summary>Encode adjacent <c>U"…"</c> segments to the lowered
    /// <c>Libc.L32("…")</c> expression — a pooled, pinned <c>uint*</c> (= char32_t*)
    /// over UTF-32 data. The emitted argument is a C# UTF-16 string literal (an astral
    /// scalar as its surrogate pair); <c>Libc.L32</c> folds it back to UTF-32 at
    /// runtime via the BCL. A trailing <c> </c> NUL-terminates the pointer.</summary>
    internal static string EncodeU32StringLiteral(IReadOnlyList<string> rawQuotedSegments)
    {
        var body = DecodeWideBodyToUtf16(rawQuotedSegments);
        var sb = new StringBuilder("Libc.L32(\"");
        foreach (var ch in body) { AppendCsStringChar(sb, ch); }
        sb.Append("\\u0000\")");   // NUL terminator
        return sb.ToString();
    }

    // ---- hexadecimal floating constants ---------------------------------

    /// <summary>Parse a hexadecimal floating constant (<c>0xH.HHp±E</c>, C99 / Zig
    /// <c>0x1.8p3</c>) to its exact binary value and render it as a round-trippable C#
    /// decimal literal (C# has no hex-float syntax). The optional trailing C suffix
    /// (<c>f</c>/<c>F</c> → preserved as a <c>float</c>; <c>l</c>/<c>L</c> → dropped) is
    /// honored for the C front-end; a Zig caller passes a suffix-free spelling. All bits
    /// come from the hex mantissa and binary exponent, so an exactly representable source
    /// value lowers bit-for-bit identically. Shared by the C IR builder and the Zig
    /// front-end (single source of truth — Zig's <c>0x1.8p3</c> reuses this verbatim).</summary>
    internal static string LowerHexFloat(string raw)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var s = raw;
        var isFloat = false;
        var last = s[^1];
        if (last is 'f' or 'F') { isFloat = true; s = s[..^1]; }
        else if (last is 'l' or 'L') { s = s[..^1]; }

        var body = s[2..];                                // strip the 0x prefix
        var pIdx = body.IndexOfAny(new[] { 'p', 'P' });
        var mant = pIdx >= 0 ? body[..pIdx] : body;
        var exp = pIdx >= 0 ? int.Parse(body[(pIdx + 1)..], inv) : 0;

        var dot = mant.IndexOf('.');
        var intPart = dot < 0 ? mant : mant[..dot];
        var fracPart = dot < 0 ? "" : mant[(dot + 1)..];

        double value = intPart.Length > 0 ? Convert.ToUInt64(intPart, 16) : 0;
        var scale = 1.0 / 16.0;
        foreach (var ch in fracPart)
        {
            value += Convert.ToInt32(ch.ToString(), 16) * scale;
            scale /= 16.0;
        }
        value *= Math.Pow(2, exp);

        return isFloat
            ? ((float)value).ToString("R", inv) + "f"
            : value.ToString("R", inv);
    }
}
