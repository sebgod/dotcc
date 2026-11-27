#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

// ---------------------------------------------------------------------------
// The one Zig comptime evaluator (GH #232).
//
// "Resolve this AST node to a compile-time value" used to be answered by about eight helpers that disagreed (one
// returned the raw source text of a string, another its decoded bytes, and the `++` fold and each comptime binding
// map were resolved separately in each). TryEvalComptime is now the single reader of the binding maps
// (_comptimeValues, _comptimeVars, _constStringLocals, _comptimeStringVars, _comptimeStrings, _ir.ComptimeGlobals),
// and callers ask it plus an As* projection directly (the old one-line wrapper helpers are gone).
//
// A comptime string is the value IrModule gives a lowered string literal: a byte array, here viewed as a CtSlice
// (so a window of it, `fmt[a..b]`, is the same shape). There is deliberately NO AsType projection: IrModule's value
// domain has no type variant (the value/type firewall in IrModule.Comptime.cs), so a type stays the business of the
// type-lowering paths.
// ---------------------------------------------------------------------------
internal sealed partial class ZigLowering
{
    /// <summary>How far <see cref="TryEvalComptime"/> may go. <see cref="Syntactic"/> lowers nothing that can resolve a
    /// module path: it runs while a <c>const</c> is being recorded in pass 0, where preparing a module as a side effect
    /// is the eager fan-out S2's laziness prevents. <see cref="Lowering"/> is for lowering-time callers: a form the
    /// syntactic rules do not know is lowered (throwaway) and run through the IR interpreter.</summary>
    private enum ComptimeTier
    {
        /// <summary>Recognised syntactic forms and the binding maps only.</summary>
        Syntactic,

        /// <summary>The syntactic forms, then lowering plus <see cref="IrModule.EvalComptimeValue"/>.</summary>
        Lowering,
    }

    /// <summary>The type of a comptime string slice: <c>[]const u8</c>.</summary>
    private static readonly CType ComptimeStringType = new CType.Slice(CType.UChar.WithQuals(TypeQual.Const));

    /// <summary>The comptime value of <paramref name="item"/>, or null when it is not comptime-known in a form modelled
    /// here. Handles: a string or integer literal, a parenthesised form, a name bound in any of the comptime maps (each
    /// name-keyed map keeps its <c>_symbols.Resolve(...) is null</c> shadow guard), <c>++</c>, <c>**</c>,
    /// <c>fmt[a..b]</c> / <c>fmt[a..]</c>, <c>.*</c> and <c>&amp;</c> of a string, <c>@typeName</c>, <c>@tagName</c>, a
    /// member list at a comptime index, and a byte-slice field of a comptime aggregate. Never throws.</summary>
    private IrModule.ComptimeValue? TryEvalComptime(Item item, ComptimeTier tier = ComptimeTier.Syntactic)
    {
        switch (item.Content)
        {
            case Zig.Grouped g:
                return TryEvalComptime(g.Arg1, tier);
            case Zig.StrLit:
                return LowerExpr(item) is LitStr lit ? FromExpr(lit) : null;
            case Zig.IntLit:
                return LowerExpr(item) is LitInt li ? FromExpr(li) : null;
            case Zig.Ident id:
                return EvalComptimeName(id);
            case Zig.SliceRange sr:
                return SliceString(sr.Arg0, sr.Arg2, sr.Arg4, tier);
            case Zig.SliceOpen so:
                return SliceString(so.Arg0, so.Arg2, null, tier);
            // `fmt[a..b].*` (a comptime slice deref'd to its array) and `&arr` of one: the same bytes, still a string.
            case Zig.Deref d:
                return AsBytes(TryEvalComptime(d.Arg0, tier)) is { } deref ? StringValue(deref) : null;
            case Zig.PreAddrOf a:
                return AsBytes(TryEvalComptime(a.Arg1, tier)) is { } addr ? StringValue(addr) : null;
            case Zig.Concat c:
                return AsBytes(TryEvalComptime(c.Arg0, tier)) is { } left && AsBytes(TryEvalComptime(c.Arg2, tier)) is { } right
                    ? StringValue(left.Concat(right)) : null;
            case Zig.Repeat r:
                return AsBytes(TryEvalComptime(r.Arg0, tier)) is { } unit
                    && AsLong(TryEvalComptime(r.Arg2, tier)) is { } n && n >= 0 && n <= MaxRepeat
                    ? StringValue(Enumerable.Repeat(unit, (int)n).SelectMany(x => x)) : null;
            case Zig.BuiltinCall { Arg2: not null } b:
                return EvalComptimeBuiltin(b, tier);
            case Zig.Index ix when tier == ComptimeTier.Lowering && TryFoldTypeInfoList(ix.Arg0, out var names) && names.Strings is { } nameList:
                return StringValue(Encoding.UTF8.GetBytes(nameList[ComptimeListIndex(names, ix.Arg2)]));
            // A byte-slice field of a comptime AGGREGATE the interpreter holds. Not a module-qualified constant in the
            // syntactic tier (see ComptimeTier): that folds at lowering time, in LowerExpr's Field case.
            case Zig.Field:
                return TryEvalTypeInfoValue(item, tier) is { } info ? info
                    : TryComptimeAggregateString(item) is { } agg ? FromExpr(agg)
                    : tier == ComptimeTier.Lowering ? EvalByLowering(item) : null;
            case Zig.CallArgs:
                return tier == ComptimeTier.Lowering ? EvalByLowering(item) : null;
            default:
                return null;
        }
    }

    /// <summary>A name bound in one of the comptime maps, as a comptime value.</summary>
    private IrModule.ComptimeValue? EvalComptimeName(Zig.Ident id)
    {
        var name = Tok(id.Arg0);
        var sym = _symbols.Resolve(name);
        if (sym is not null)
        {
            if (_comptimeStringVars.TryGetValue(sym, out var stringVar)) { return FromExpr(stringVar); }
            if (_constStringLocals.TryGetValue(sym, out var constText)) { return StringValue(constText.Select(c => (byte)c)); }
        }
        if (_comptimeValues.TryGetValue(name, out var bound)) { return FromExpr(bound); }
        // Name-keyed and function-flat, so guarded on the name not being a real symbol.
        if (sym is null && _comptimeStrings.TryGetValue(name, out var text)) { return StringValue(Encoding.UTF8.GetBytes(text)); }
        // A name bound to a comptime tag (`const signedness = @typeInfo(T).int.signedness;`): an enum literal.
        return sym is null && _comptimeTagBindings.TryGetValue(name, out var tag) ? new IrModule.CtEnumLiteral(tag) : null;
    }

    /// <summary>A <c>@typeInfo</c> VALUE field (<c>bits</c>, <c>len</c>, <c>is_const</c>, <c>attrs.const</c>) as a comptime
    /// value, through <see cref="TryFoldTypeInfoValue"/>. That fold raises a loud cut for a field it cannot recover; the
    /// lowering tier lets it propagate, the syntactic (speculative) tier answers null. The typeInfo PAYLOAD stays in
    /// <see cref="TryEvalTypeInfo"/>: a type is not a comptime value.</summary>
    private IrModule.ComptimeValue? TryEvalTypeInfoValue(Item item, ComptimeTier tier)
    {
        try
        {
            using (EnterThrowawayHoist())
            {
                return TryFoldTypeInfoValue(item, out var folded) ? FromExpr(folded) : null;
            }
        }
        catch (IrUnsupportedException) when (tier == ComptimeTier.Syntactic)
        {
            return null;
        }
    }

    /// <summary><c>@typeName(T)</c> and <c>@tagName(x)</c> as comptime strings.</summary>
    private IrModule.ComptimeValue? EvalComptimeBuiltin(Zig.BuiltinCall b, ComptimeTier tier)
    {
        switch (Tok(b.Arg0))
        {
            case "@typeName":
                return TryEvalTypeNameBuiltin(b) is { } typeName ? FromExpr(typeName) : null;
            case "@tagName":
            {
                var args = Flatten(b.Arg2);
                // A generic's `comptime family: Arch.Family` seed.
                if (args is [{ Content: Zig.Ident { Arg0: var argTok } }]
                    && _symbols.Resolve(Tok(argTok)) is { } seed && _comptimeVars.TryGetValue(seed, out var seedValue)
                    && seedValue.Type.Unqualified is CType.Enum seedEnum
                    && _enumMembers.TryGetValue(seedEnum.Name, out var members))
                {
                    return members.FirstOrDefault(m => m.Value.ConstValue == seedValue.Value).Key is { } member
                        ? StringValue(Encoding.UTF8.GetBytes(member)) : null;
                }
                return tier == ComptimeTier.Lowering && args is [var tagged] && TryComptimeTagName(tagged) is { } tag
                    ? StringValue(Encoding.UTF8.GetBytes(tag)) : null;
            }
            default:
                return null;
        }
    }

    /// <summary><c>fmt[lo..hi]</c> over a comptime string with comptime bounds, or null.</summary>
    private IrModule.ComptimeValue? SliceString(Item baseItem, Item loItem, Item? hiItem, ComptimeTier tier)
    {
        if (AsBytes(TryEvalComptime(baseItem, tier)) is not { } bytes) { return null; }
        long? lo, hi;
        using (EnterThrowawayHoist())
        {
            lo = ComptimeLong(LowerUsizeOperand(loItem));
            hi = hiItem is { } h ? ComptimeLong(LowerUsizeOperand(h)) : bytes.Length;
        }
        if (lo is not { } l || hi is not { } e || l < 0 || e > bytes.Length || l > e) { return null; }
        return StringValue(bytes.Skip((int)l).Take((int)(e - l)));
    }

    /// <summary>The lowering tier's fallback: lower <paramref name="item"/> (throwaway, so no hoisted statement
    /// escapes) and run the IR interpreter over it. Null when it does not lower or is not a comptime value.</summary>
    private IrModule.ComptimeValue? EvalByLowering(Item item)
    {
        CExpr lowered;
        using (EnterThrowawayHoist())
        {
            try { lowered = LowerExpr(item); }
            catch (IrUnsupportedException) { return null; }
        }
        return FromExpr(lowered);
    }

    /// <summary>A lowered expression as a comptime value: a string literal as its bytes, an integer or bool literal
    /// directly, anything else through the IR interpreter.</summary>
    private IrModule.ComptimeValue? FromExpr(CExpr e) => e switch
    {
        LitStr s => StringValue(DotCC.EmitHelpers.StringByteValues(s.Segments).Select(x => (byte)x)),
        LitInt { Value: { } v } => new IrModule.CtInt(v, e.Type ?? CType.Long),
        LitBool b => new IrModule.CtBool(b.Value),
        _ => _ir.EvalComptimeValue(e),
    };

    /// <summary>The comptime string holding <paramref name="bytes"/>: the byte array IrModule builds for a string
    /// literal (NUL included in the backing, not in the slice), viewed as a <c>[]const u8</c>.</summary>
    private static IrModule.CtSlice StringValue(IEnumerable<byte> bytes)
    {
        var list = bytes.ToList();
        var elems = new IrModule.ComptimeValue[list.Count + 1];
        for (var k = 0; k < list.Count; k++) { elems[k] = new IrModule.CtInt(list[k], CType.UChar); }
        elems[list.Count] = new IrModule.CtInt(0, CType.UChar);
        var backing = new IrModule.CtArray(elems, CType.UChar, new CType.Array(CType.UChar, elems.Length));
        return new IrModule.CtSlice(backing, 0, list.Count, ComptimeStringType);
    }

    /// <summary>A comptime string's bytes, or null when the value is not a byte slice.</summary>
    private static byte[]? AsBytes(IrModule.ComptimeValue? v)
    {
        if (v is not IrModule.CtSlice { Type: CType.Slice } s || s.Offset < 0 || s.Offset + s.Length > s.Backing.Elems.Length)
        {
            return null;
        }
        var bytes = new byte[s.Length];
        for (var k = 0; k < s.Length; k++)
        {
            if (s.Backing.Elems[s.Offset + k] is not IrModule.CtInt b || b.Value < 0 || b.Value > 255) { return null; }
            bytes[k] = (byte)b.Value;
        }
        return bytes;
    }

    /// <summary>A comptime string as a NAME: its bytes read as UTF-8, the spelling a declared field name has (a
    /// non-ASCII name differs from its one-char-per-byte form).</summary>
    private static string? AsString(IrModule.ComptimeValue? v) => AsBytes(v) is { } b ? Encoding.UTF8.GetString(b) : null;

    /// <summary>A comptime string as its bytes, one char per byte (the model the comptime formatter works in, so a width
    /// counts bytes as zig's does).</summary>
    private static string? AsByteString(IrModule.ComptimeValue? v) => AsBytes(v) is { } b ? new string(b.Select(x => (char)x).ToArray()) : null;

    /// <summary>A comptime string spliced back as a string literal (each byte spelled <c>\xNN</c>), for a caller that
    /// wants an expression.</summary>
    private static LitStr? AsLitStr(IrModule.ComptimeValue? v) => AsBytes(v) is { } b ? ComptimeStringFromBytes(b.Select(x => (int)x)) : null;

    /// <summary>A comptime integer that fits a <see cref="long"/>, or null.</summary>
    private static long? AsLong(IrModule.ComptimeValue? v)
        => v is IrModule.CtInt { Value: var n } && n >= long.MinValue && n <= long.MaxValue ? (long)n : null;

    /// <summary>A comptime integer at full 128-bit width, or null. A comptime bool reads as 0 / 1 (the C rule
    /// <see cref="IrModule.ConstEval"/> has).</summary>
    private static System.Int128? AsInt128(IrModule.ComptimeValue? v) => v switch
    {
        IrModule.CtInt { Value: var n } => n,
        IrModule.CtBool { Value: var b } => b ? 1 : 0,
        _ => null,
    };

    /// <summary>The ONE IR-level integer read: an already-lowered expression's comptime integer in 128 bits, calls
    /// allowed (a comptime integer initializer may call, <c>cacheLineForCpu(builtin.cpu)</c>), or null. The call-free
    /// fold is tried first, so a plain constant never reaches the interpreter.</summary>
    private System.Int128? ComptimeInt128(CExpr e) => _ir.ConstEval128(e) ?? AsInt128(_ir.EvalComptimeValue(e));

    /// <summary>The <see cref="long"/> projection of <see cref="ComptimeInt128"/>: null when the value is outside long.</summary>
    private long? ComptimeLong(CExpr e) => ComptimeInt128(e) is { } v && v >= long.MinValue && v <= long.MaxValue ? (long)v : null;

    /// <summary>A comptime integer that fits an <see cref="int"/>, or null.</summary>
    private static int? AsInt(IrModule.ComptimeValue? v) => AsLong(v) is { } n && n >= int.MinValue && n <= int.MaxValue ? (int)n : null;

    /// <summary>A comptime boolean, or null.</summary>
    private static bool? AsBool(IrModule.ComptimeValue? v) => v is IrModule.CtBool { Value: var b } ? b : null;

    /// <summary>The name of a comptime enum literal (<c>.kw_if</c>), or null.</summary>
    private static string? AsTag(IrModule.ComptimeValue? v) => v is IrModule.CtEnumLiteral { Name: var n } ? n : null;

    /// <summary>A comptime value spliced back as an expression: a string as a literal, anything else through
    /// <see cref="IrModule.SpliceComptimeValue"/>. Null for a null value.</summary>
    private CExpr? AsExpr(IrModule.ComptimeValue? v) => v switch
    {
        null => null,
        _ when AsBytes(v) is not null => AsLitStr(v),
        _ => _ir.SpliceComptimeValue(v),
    };
}
