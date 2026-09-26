#nullable enable

using System.Collections.Generic;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Zig SIMD vectors, <c>@Vector(N, T)</c> (road-to-zig-std, the target-identity segment T5, by the
/// maintainer's choice: lower to .NET's own vector types). A numeric vector is a
/// <c>System.Runtime.Intrinsics.Vector64/128/256/512&lt;T&gt;</c> chosen by its total width, so element-wise
/// arithmetic is the JIT's SIMD code, with a software fallback on a CPU without it; a BOOL vector, what a
/// comparison yields, is a lane bitmask in a <c>ulong</c>. <c>@splat</c> is <c>VectorN.Create(x)</c>, an array or
/// a slice at a vector sink is a load from its first element, a comparison is a mask (<c>ZigVec.Eq</c>, …), and
/// <c>@reduce</c> / <c>@select</c> / a lane read route through <c>DotCC.Libc.ZigVec</c>. A width .NET has no
/// vector type for (a <c>@Vector(3, u8)</c>) is a loud cut at runtime; at compile time it is held as an array
/// (task #108, see <see cref="VectorTypeOf"/>; the runtime fallback is GitHub issue #127).</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lower <c>@Vector(len, T)</c> to its <see cref="CType.Vector"/>, loud when no .NET vector fits, except while
    /// evaluating at compile time: there the shape is an array <c>[len]T</c> (task #108: std.meta.FieldEnum's
    /// <c>&amp;std.simd.iota(u8, 3)</c>, a vector read through as <c>*const [3]u8</c>), and a generic instance whose
    /// signature needed one is kept from runtime calls (<see cref="_comptimeVectorArrays"/>).</summary>
    private CType VectorTypeOf(Zig.BuiltinCall call)
    {
        var args = Flatten(call.Arg2);
        if (args.Count != 2)
        {
            throw new IrUnsupportedException($"zig `@Vector` expects (len, T); got {args.Count} argument(s)");
        }
        var len = ConstEvalArraySize(args[0]);
        var element = LowerType(args[1]);
        var vector = new CType.Vector(element, len);
        if (!vector.IsMask && (element.Unqualified is not CType.Prim { Name: not "_Bool" } || vector.NetFamily is null))
        {
            if (element.Unqualified is CType.Prim { Name: not "_Bool" } && (_shared.TypeBodyDepth > 0 || _comptimeDepth > 0 || _loweringForComptimeEval > 0))
            {
                _comptimeVectorArrays++;
                return new CType.Array(element, len);
            }
            throw new IrUnsupportedException(
                $"zig {vector.Describe()}: dotcc lowers a vector to .NET's Vector64/128/256/512, so its lanes must be "
                + $"an integer or float type filling 8, 16, 32 or 64 bytes ({vector.Bits / 8} here)");
        }
        if (vector.IsMask && len > 64)
        {
            throw new IrUnsupportedException($"zig {vector.Describe()}: a bool vector is a 64-bit lane mask, so at most 64 lanes");
        }
        return vector;
    }

    /// <summary>How many times a vector shape .NET cannot hold was lowered as a compile-time array (<see cref="VectorTypeOf"/>).
    /// A generic instance whose signature raised it may only be called at compile time (<see cref="ResolveGenericInstance"/>).</summary>
    private int _comptimeVectorArrays;

    /// <summary>The <c>System.Runtime.Intrinsics</c> class of a numeric vector (<c>System.Runtime.Intrinsics.Vector128</c>).</summary>
    private static string VectorClass(CType.Vector v) => "System.Runtime.Intrinsics." + v.NetFamily;

    /// <summary><c>@splat(x)</c> at a vector sink: every lane <c>x</c>. For a bool vector, every bit.</summary>
    private CExpr LowerSplat(IReadOnlyList<Item> args, CType.Vector vector)
    {
        if (args.Count != 1) { throw new IrUnsupportedException($"zig `@splat` expects one argument; got {args.Count}"); }
        var lane = LowerExprSink(args[0], vector.Element);
        if (vector.IsMask)
        {
            // `@splat(true)` is the full mask, `@splat(false)` the empty one; a runtime bool picks between them.
            var full = new Call("ZigVec.Full", new List<CExpr> { IntLit(vector.Count) }) { Type = CType.ULong };
            return new CondExpr(lane, full, new LitInt("0", 0) { Type = CType.ULong }) { Type = vector };
        }
        return new Call(VectorClass(vector) + ".Create", new List<CExpr> { new Cast(vector.Element, lane) { Type = vector.Element } })
        {
            Type = vector,
        };
    }

    /// <summary>A positional list literal at a vector sink: <c>VectorN.Create(e0, e1, …)</c>, one lane per element,
    /// each at the lane type. A bool vector's literal must be comptime-known and becomes its mask constant.</summary>
    private CExpr LowerVectorLiteral(IReadOnlyList<Item> inits, CType.Vector vector)
    {
        if (inits.Count != vector.Count)
        {
            throw new IrUnsupportedException(
                $"zig {vector.Describe()}: a literal needs exactly {vector.Count} element(s); got {inits.Count}");
        }
        var lanes = new List<CExpr>(inits.Count);
        foreach (var init in inits)
        {
            if (init.Content is not Zig.FieldInitPositional pos)
            {
                throw new IrUnsupportedException($"zig {vector.Describe()}: a vector literal lists its lanes positionally");
            }
            lanes.Add(LowerExprSink(pos.Arg0, vector.Element));
        }
        if (vector.IsMask)
        {
            ulong bits = 0;
            for (var k = 0; k < lanes.Count; k++)
            {
                if (_ir.ConstEval(lanes[k]) is not { } on)
                {
                    throw new IrUnsupportedException($"zig {vector.Describe()}: a bool-vector literal must be comptime-known");
                }
                if (on != 0) { bits |= 1UL << k; }
            }
            var mask = new LitInt(bits.ToString(System.Globalization.CultureInfo.InvariantCulture), unchecked((long)bits)) { Type = CType.ULong };
            return new Cast(vector, mask) { Type = vector };
        }
        return new Call(VectorClass(vector) + ".Create",
            lanes.Select(l => (CExpr)new Cast(vector.Element, l) { Type = vector.Element }).ToList())
        {
            Type = vector,
        };
    }

    /// <summary>An array, a pointer to one, or a slice at a vector sink (<c>const block: Block =
    /// slice[i..][0..N].*;</c>): the vector loaded from its first element.</summary>
    private CExpr? TryCoerceToVector(CExpr value, CType.Vector vector)
    {
        // A vector of narrower integer lanes widens lane by lane (std.unicode's `@Vector(16, u8)` chunk passed where a
        // `@Vector(16, u16)` is expected, task #144): zig coerces when every value fits, as for a scalar.
        if (!vector.IsMask && value.Type.Unqualified is CType.Vector { IsMask: false } source && source.Count == vector.Count
            && source.Element.Unqualified is CType.Prim { Integer: true } from && vector.Element.Unqualified is CType.Prim { Integer: true } to
            && to.Bytes > from.Bytes && (to.Signed || !from.Signed))
        {
            var witness = new Cast(vector.Element, IntLit(0)) { Type = vector.Element };
            return new Call("ZigVec.Widen" + vector.Bits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new List<CExpr> { value, witness }) { Type = vector };
        }
        if (vector.IsMask || value.Type.Unqualified is CType.Vector) { return null; }
        CExpr? first = value.Type.Unqualified switch
        {
            CType.Array => PointedArray(value) is ({ } arr, _) ? arr : value,   // an array is its element pointer
            CType.Pointer => PointedArray(value) is ({ } parr, _) ? parr : value,
            CType.Slice => new Member(value, "Ptr", false) { Type = new CType.Pointer(vector.Element) },
            _ => null,
        };
        if (first is null) { return null; }
        return new Call("ZigVec.Load" + vector.Bits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new List<CExpr> { first })
        {
            Type = vector,
        };
    }

    /// <summary>The operands of a shift whose amount is <c>@splat(n)</c> (std.math.rotr's <c>x &gt;&gt; @splat(ar)</c>), or
    /// null for any other operator (the caller lowers it as usual). zig gives a shift amount the result type
    /// <c>@Vector(N, Log2Int(T))</c>; the splatted count is the same for every lane, so it stays the one scalar count
    /// .NET's vector shift operator takes. Any other operand has no result type in zig (<c>v + @splat(1)</c> is
    /// "@splat must have a known result type"), and neither has a shift of a non-vector by a splat.</summary>
    private (CExpr Left, CExpr Right)? TrySplatBesideVector(BinOp op, Item l, Item r)
    {
        static Item? SplatArg(Item it) =>
            it.Content is Zig.BuiltinCall { Arg0: var tok } call && Tok(tok) == "@splat" && Flatten(call.Arg2) is [var one] ? one : null;
        if (op is not (BinOp.Shl or BinOp.Shr))
        {
            if (SplatArg(l) is not null || SplatArg(r) is not null)
            {
                throw new CompileException("zig: @splat must have a known result type (an operand of a binary operator has none; write `@as(V, @splat(x))`)");
            }
            return null;
        }
        if (SplatArg(l) is not null)
        {
            throw new CompileException("zig: @splat must have a known result type (the shifted operand has none; write `@as(V, @splat(x))`)");
        }
        if (SplatArg(r) is not { } count)
        {
            return null;
        }
        var left = LowerExpr(l);
        if (left.Type.Unqualified is not CType.Vector)
        {
            throw new CompileException($"zig: a `@splat` shift amount needs a vector to shift; `{left.Type.Describe()}` is not one");
        }
        return (left, LowerExpr(count));
    }

    /// <summary>A binary operator with a vector operand: arithmetic and bitwise ops are .NET's element-wise
    /// operators; a comparison is a lane mask (<c>ZigVec.Eq</c>, …). A scalar operand is splatted to the vector's
    /// lanes, as zig coerces it. Null when neither operand is a vector.</summary>
    private CExpr? TryVectorBinary(BinOp op, CExpr left, CExpr right)
    {
        var vector = left.Type.Unqualified as CType.Vector ?? right.Type.Unqualified as CType.Vector;
        if (vector is null) { return null; }
        CExpr Lanes(CExpr operand) => operand.Type.Unqualified is CType.Vector ? operand
            : new Call(VectorClass(vector) + ".Create", new List<CExpr> { new Cast(vector.Element, operand) { Type = vector.Element } }) { Type = vector };
        // A shift by one scalar count (a splatted amount) is .NET's vector shift operator, a logical right shift for unsigned
        // lanes. A count PER LANE has `Log2Int(T)` lanes, narrower than the value's, which .NET has no operator for.
        if (op is BinOp.Shl or BinOp.Shr && !vector.IsMask && left.Type.Unqualified is CType.Vector)
        {
            if (right.Type.Unqualified is CType.Vector)
            {
                throw new IrUnsupportedException(
                    $"zig {vector.Describe()}: a shift by a vector of per-lane counts is not lowered yet (a `@splat` count is)");
            }
            return new Binary(op, left, new Cast(CType.Int, right) { Type = CType.Int }) { Type = vector };
        }
        var (l, r) = (Lanes(left), Lanes(right));
        if (vector.IsMask)
        {
            // A bool vector's `and` / `or` / `!=` are bit operations on its mask.
            return op switch
            {
                BinOp.BitAnd or BinOp.LogAnd => new Binary(BinOp.BitAnd, l, r) { Type = vector },
                BinOp.BitOr or BinOp.LogOr => new Binary(BinOp.BitOr, l, r) { Type = vector },
                BinOp.BitXor or BinOp.Ne => new Binary(BinOp.BitXor, l, r) { Type = vector },
                _ => throw new IrUnsupportedException($"zig {vector.Describe()}: `{op}` on a bool vector is not lowered yet"),
            };
        }
        var mask = new CType.Vector(CType.Bool, vector.Count);
        string? compare = op switch
        {
            BinOp.Eq => "Eq", BinOp.Ne => "Ne", BinOp.Lt => "Lt", BinOp.Gt => "Gt", BinOp.Le => "Le", BinOp.Ge => "Ge",
            _ => null,
        };
        if (compare is not null) { return new Call("ZigVec." + compare, new List<CExpr> { l, r }) { Type = mask }; }
        if (op is BinOp.Div or BinOp.Mod or BinOp.Shl or BinOp.Shr)
        {
            throw new IrUnsupportedException($"zig {vector.Describe()}: `{op}` on vectors is not lowered yet");
        }
        return new Binary(op, l, r) { Type = vector };
    }

    /// <summary><c>@reduce(op, v)</c>: over a bool vector a mask test (<c>.Or</c> any lane, <c>.And</c> every lane,
    /// <c>.Xor</c> an odd count), over a numeric one a <c>ZigVec.Reduce*</c>.</summary>
    private CExpr LowerReduce(IReadOnlyList<Item> args)
    {
        if (args.Count != 2 || EnumLitName(args[0]) is not { } op)
        {
            throw new IrUnsupportedException("zig `@reduce` expects (.Op, vector) with an enum-literal operator");
        }
        var v = LowerExpr(args[1]);
        if (v.Type.Unqualified is not CType.Vector vector)
        {
            throw new IrUnsupportedException($"zig `@reduce` needs a vector operand; got {v.Type.Describe()}");
        }
        if (vector.IsMask)
        {
            var zero = new LitInt("0", 0) { Type = CType.ULong };
            var full = new Call("ZigVec.Full", new List<CExpr> { IntLit(vector.Count) }) { Type = CType.ULong };
            return op switch
            {
                "Or" => new Binary(BinOp.Ne, v, zero) { Type = CType.Bool },
                "And" => new Binary(BinOp.Eq, v, full) { Type = CType.Bool },
                "Xor" => new Binary(BinOp.Ne,
                    new Binary(BinOp.BitAnd, new Call("System.Numerics.BitOperations.PopCount", new List<CExpr> { v }) { Type = CType.Int },
                        IntLit(1)) { Type = CType.Int },
                    IntLit(0)) { Type = CType.Bool },
                _ => throw new IrUnsupportedException($"zig `@reduce(.{op}, …)` over a bool vector is not meaningful"),
            };
        }
        if (op is not ("Add" or "Min" or "Max"))
        {
            throw new IrUnsupportedException($"zig `@reduce(.{op}, …)` over a numeric vector is not lowered yet (.Add / .Min / .Max are)");
        }
        return new Call("ZigVec.Reduce" + op, new List<CExpr> { v }) { Type = vector.Element };
    }

    /// <summary><c>@select(T, mask, a, b)</c>: lane-wise <c>a</c> where the mask is set, else <c>b</c>.</summary>
    private CExpr LowerSelect(IReadOnlyList<Item> args, CType? sink)
    {
        if (args.Count != 4) { throw new IrUnsupportedException($"zig `@select` expects (T, mask, a, b); got {args.Count}"); }
        var mask = LowerExpr(args[1]);
        if (mask.Type.Unqualified is not CType.Vector { IsMask: true } maskType)
        {
            throw new IrUnsupportedException($"zig `@select` needs a bool-vector mask; got {mask.Type.Describe()}");
        }
        var vector = sink?.Unqualified as CType.Vector ?? new CType.Vector(LowerType(args[0]), maskType.Count);
        var a = LowerExprSink(args[2], vector);
        var b = LowerExprSink(args[3], vector);
        return new Call("ZigVec.Select", new List<CExpr> { mask, a, b }) { Type = vector };
    }

    /// <summary><c>@shuffle(E, a, b, mask)</c> (std.hash.XxHash3's <c>@shuffle(u64, data, undefined, [_]i32{ 1, 0, 3, 2, … })</c>,
    /// task #179): lane i of the result is <c>a[m]</c> for a non-negative mask element m and <c>b[~m]</c> for a negative one, the
    /// mask comptime-known. An operand no lane reads may be <c>undefined</c>; one read by several lanes is evaluated once.</summary>
    private CExpr LowerShuffle(IReadOnlyList<Item> args)
    {
        if (args.Count != 4) { throw new IrUnsupportedException($"zig `@shuffle` expects (E, a, b, mask); got {args.Count}"); }
        var element = LowerType(args[0]);
        CExpr maskValue;
        using (EnterThrowawayHoist()) { maskValue = LowerExpr(args[3]); }
        if (_ir.EvalComptimeValue(maskValue) is not IrModule.CtArray { Elems: var maskElems }
            || maskElems.Any(e => e is not IrModule.CtInt))
        {
            throw new IrUnsupportedException("zig `@shuffle` needs a comptime-known integer mask");
        }
        var mask = maskElems.Select(e => (long)((IrModule.CtInt)e).Value).ToList();
        CExpr? Operand(Item item, bool read)
        {
            if (!read) { return null; }
            if (item.Content is Zig.UndefinedLit) { throw new IrUnsupportedException("zig `@shuffle`: a lane reads an `undefined` operand"); }
            var lowered = LowerExpr(item);
            if (lowered.Type.Unqualified is not CType.Vector) { throw new IrUnsupportedException($"zig `@shuffle` needs vector operands; got {lowered.Type.Describe()}"); }
            if (lowered is VarRef) { return lowered; }
            var temp = _symbols.Declare(new Symbol { Name = "__shuf" + _anfTempCounter++, Kind = SymKind.Var, Type = lowered.Type });
            RequireHoistable("@shuffle").Add(new DeclStmt(new List<LocalDecl> { new(temp, lowered) }));
            return new VarRef(temp) { Type = temp.Type };
        }
        var a = Operand(args[1], mask.Any(m => m >= 0));
        var b = Operand(args[2], mask.Any(m => m < 0));
        var lanes = new List<CExpr>(mask.Count);
        foreach (var m in mask)
        {
            var (source, index) = m >= 0 ? (a, m) : (b, ~m);
            if (source is not { Type.Unqualified: CType.Vector sourceVector } || index >= sourceVector.Count)
            {
                throw new CompileException($"zig: `@shuffle` mask element {m} is out of range of its operand");
            }
            var lane = VectorLane(source, new LitInt(index.ToString(System.Globalization.CultureInfo.InvariantCulture), index) { Type = CType.Int }, sourceVector);
            lanes.Add(new Cast(element, lane) { Type = element });
        }
        var result = new CType.Vector(element, mask.Count);
        return new Call(VectorClass(result) + ".Create", lanes) { Type = result };
    }

    /// <summary><c>v[i]</c>: one lane of a numeric vector, or one bit of a bool vector's mask.</summary>
    private static CExpr VectorLane(CExpr vector, CExpr index, CType.Vector type) => type.IsMask
        ? new Call("ZigVec.Bit", new List<CExpr> { vector, index }) { Type = CType.Bool }
        : new Call("ZigVec.Get", new List<CExpr> { vector, index }) { Type = type.Element };

    /// <summary>A store to one LANE of a SIMD vector, <c>v[i] = x</c> or <c>v[i] op= x</c> (std.crypto.blake3's
    /// <c>result[i] = counterLow(counter + i);</c> in an <c>inline for</c>, task #155): a .NET vector is immutable, so the
    /// vector with that lane replaced is assigned back, <c>v = ZigVec.With(v, i, x)</c>. zig requires a store's index to
    /// be comptime-known ("vector index not comptime known"; a READ may take a runtime one), and so does dotcc. Null
    /// when the target is not a vector lane.</summary>
    private CExpr? TryVectorLaneStore(Item targetItem, BinOp? op, Item valueItem)
    {
        if (targetItem.Content is not Zig.Index ix) { return null; }
        CExpr probe;
        using (EnterThrowawayHoist()) { probe = LowerExpr(ix.Arg0); }
        if (probe.Type.Unqualified is not CType.Vector vector) { return null; }
        if (vector.IsMask)
        {
            throw new IrUnsupportedException($"zig {vector.Describe()}: a store to one lane of a bool vector is not lowered yet");
        }
        var vec = LowerExpr(ix.Arg0);
        if (vec is not (VarRef or Member or DotCC.Ir.Index or Unary { Op: UnOp.Deref }))
        {
            throw new IrUnsupportedException("zig vector lane store: the vector must be a variable, a field, an element (`vecs[i][j]`, std.crypto.blake3's transposeNxN) or a dereference");
        }
        var idx = LowerUsizeOperand(ix.Arg2);
        if (_ir.ConstEval(idx) is null)
        {
            throw new CompileException("zig: vector index not comptime known (a store to a vector lane needs a comptime-known index)");
        }
        var value = LowerExprSink(valueItem, vector.Element);
        if (op is { } binOp)
        {
            // `v[i] +%= x`: the lane's new value, narrowed back to the lane type (C# widens a narrow operand to int).
            var combined = new Binary(binOp, VectorLane(vec, idx, vector), value) { Type = vector.Element };
            value = new Cast(vector.Element, combined) { Type = vector.Element };
        }
        var with = new Call("ZigVec.With", new List<CExpr> { vec, idx, value }) { Type = vector };
        return new Assign(null, vec, with) { Type = vector };
    }

    /// <summary>An <c>int</c> literal.</summary>
    private static LitInt IntLit(int n) => new(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n) { Type = CType.Int };
}
