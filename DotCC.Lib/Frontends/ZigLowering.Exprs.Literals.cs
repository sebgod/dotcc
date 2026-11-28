#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions, literals: numbers, booleans, characters, strings, <c>null</c>/<c>undefined</c>, void values, enum literals, error literals and error sets.
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lowers a literal node: numbers, booleans, characters, strings, <c>null</c>, <c>undefined</c>, void values, enum literals, error literals and error sets.</summary>
    private CExpr LowerLiteralExpr(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.IntLit i: return DecodeZigInt(Tok(i.Arg0));
            case Zig.FloatLit f: return new LitFloat(LowerZigFloat(Tok(f.Arg0))) { Type = CType.Double };

            // `true`/`false` — boolean literals (a `bool` value, like `null`/`undefined`). Typed
            // `_Bool` (→ the store-normalising `CBool`, which takes a C# `bool`).
            case Zig.TrueLit:  return new LitBool(true) { Type = CType.Bool };
            case Zig.FalseLit: return new LitBool(false) { Type = CType.Bool };

            // A char literal `'x'` / `'\n'` / `'\xNN'` / `'\u{1F600}'` — Zig's `comptime_int` = the
            // codepoint. The `\u{…}` form is decoded Zig-side (the shared escape machinery has no
            // `\u{…}` arm — and adding one would change the C front-end's `\u` handling); everything
            // else reuses the shared decoder, then lowers to an int literal like a C char constant.
            case Zig.CharLit c:
            {
                var raw = Tok(c.Arg0);
                var body = raw.Length >= 2 ? raw[1..^1] : "";
                var v = body.StartsWith("\\u{", System.StringComparison.Ordinal) && body.EndsWith("}", System.StringComparison.Ordinal)
                    ? System.Convert.ToInt32(body[3..^1].Replace("_", ""), 16)
                    : DotCC.EmitHelpers.DecodeCharLiteral(body);
                return new LitInt(v.ToString(CultureInfo.InvariantCulture), v) { Type = CType.Int };
            }

            // A string literal. Zig's escape set overlaps C's for the common cases
            // (`\n`/`\t`/`\\`/`\"`/`\xNN`), so we reuse the C string machinery: the (escape-expanded)
            // quoted lexeme becomes a single LitStr segment, typed `char[N]` (decoded byte count incl.
            // NUL) so it decays to `char*` exactly like a C literal — the C# backend lowers it to the
            // same pooled `Libc.L("…"u8)` pointer. Two Zig-specific reshapes happen FIRST so the shared
            // decoder is untouched: a `\\`-prefixed multiline string is folded to one quoted lexeme of
            // its raw (un-escaped) content; each `\xNN` and `\u{…}` byte becomes an unambiguous octal escape (task #134).
            case Zig.StrLit s:
            {
                var raw = Tok(s.Arg0);
                var lexeme = raw.StartsWith("\\", System.StringComparison.Ordinal)
                    ? FoldZigMultilineString(raw)
                    : NormalizeZigByteEscapes(raw);
                var segs = new List<string> { lexeme };
                DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
                return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
            }

            // A bare enum literal `.member` or anonymous struct literal `.{…}` outside a
            // typed sink — Zig requires a known result type for both, so reject loudly here
            // (the sink-aware paths in LowerExprSink handle the valid cases).
            // zig types it `@EnumLiteral()`, a comptime-only value that coerces to an enum where it meets one (a tuple
            // element `.{ "if", .kw_if }`, task #113); see CType.EnumLiteral and LowerExprSink's coercion.
            case Zig.EnumLit bare:
                return new DefaultLit { Type = new CType.EnumLiteral(Tok(bare.Arg1)) };

            // `null` — reuse the C null-pointer node (renders C# `null`, valid for BOTH a
            // pointer sink `T*` and a value-optional sink `T?`). In Zig `null` only appears
            // at a typed sink, so the backend's store-coercion gives it the right form.
            case Zig.NullLit: return new NullPtr { Type = new CType.Pointer(CType.Void) };

            // `undefined` — uninitialized storage. Without a sink we can only emit a zeroed
            // `default` (an over-approximation of Zig's "any value"; a correct program writes
            // before reading). At a typed sink LowerExprSink types it precisely; an array local
            // takes the dedicated stackalloc path in DeclOf.
            case Zig.UndefinedLit: return new DefaultLit { Type = CType.Int };
            // `{}` — zig's void value. It has no runtime representation: the C# backend erases void
            // parameters and their arguments, and a discarded one emits nothing.
            case Zig.VoidArg or Zig.VoidValue: return new DefaultLit { Type = CType.Void };

            // A bare `error.Foo` value (Milestone N): the error's stable code in the flat global
            // set, typed `CType.ErrorSet` (rendered `ushort`). This makes error values usable
            // outside `return error.Foo;` — bound to a const/var and compared (`e == error.Foo`)
            // via the ordinary `==`/`!=` lowering, since equal codes mean equal error values. V1
            // still erases the named SET (an explicit `error{A,B}` decl / a named `E!T` is later
            // Milestone N work); the comparison just matches codes.
            case Zig.ErrorLit el:
                return LowerErrorLit(Tok(el.Arg2));

            // `error{A, B}` as a VALUE is not meaningful in dotcc's erased model — it is only a
            // `const E = error{…};` declaration (handled in TryComptimeConstBinding, emits nothing)
            // or an (ignored) set in an `E!T` return type.
            case Zig.ErrorSet:
                throw new IrUnsupportedException(
                    "zig `error{…}` set literal is only valid as a `const E = error{…};` declaration or an `E!T` return-type set");

            // `A || B` (error-set merge) as a VALUE is not meaningful in dotcc's erased model — it is
            // only a `const E = A || B;` declaration (handled in TryComptimeConstBinding, emits nothing).
            case Zig.ErrSetMerge:
                throw new IrUnsupportedException(
                    "zig error-set merge `A || B` is only valid as a `const E = A || B;` declaration (dotcc erases error sets)");

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }
}
