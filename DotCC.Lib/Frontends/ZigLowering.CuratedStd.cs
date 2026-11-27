#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>The lowerings behind the curated-std registry (<see cref="ZigCuratedStd"/>, #233): one method per namespace
/// function row, and the one dispatcher that finds the row for a call and checks its arity.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>The call <c>receiver.method(args)</c> when it names a curated namespace function row, lowered through the
    /// registry; null when the call is not a curated one (it then continues to source navigation). With no std tree
    /// configured an unmodeled member of <c>std.mem</c> / <c>std.debug</c> / <c>std.testing</c> is a clear "not modeled"
    /// error listing the rows, the most useful message when nothing else could resolve it.</summary>
    private CExpr? TryLowerCuratedStdCall(Item receiver, string method, IReadOnlyList<Item> argItems)
    {
        if (!TryResolveStdPath(receiver, out var ns) || !ZigCuratedStd.IsNamespace(ns)) { return null; }
        if (ZigCuratedStd.FindFunction(ns, method) is { } row) { return LowerCuratedStdFn(row, argItems); }
        if (_moduleGraph?.StdRootPath is not null) { return null; }
        var members = string.Join(", ", ZigCuratedStd.Members(ns));
        return ns switch
        {
            "std.mem" => throw new IrUnsupportedException($"zig `std.mem.{method}` is not modeled yet (supported: {members})"),
            "std.debug" => throw new IrUnsupportedException(
                $"zig `std.debug.{method}` is not supported yet; modeled: {members} (wall-plan W6)"),
            "std.testing" => throw new IrUnsupportedException(
                $"zig `std.testing.{method}` is not supported yet; `dotcc zig test` models {members} "
                + "(road-to-zig-std; more assertions grow on demand)"),
            _ => null,
        };
    }

    /// <summary>Lower a call of a namespace function <paramref name="row"/>: the argument count is checked against the
    /// row, then the row's lowering runs.</summary>
    private CExpr LowerCuratedStdFn(CuratedStdEntry row, IReadOnlyList<Item> argItems)
    {
        if (row.Arity >= 0 && argItems.Count != row.Arity)
        {
            throw new IrUnsupportedException($"zig `{row.Path}` expects {row.ArgShape}; got {argItems.Count} argument(s)");
        }
        return row.Lower is { } lower
            ? lower(this, argItems)
            : throw new IrUnsupportedException($"zig `{row.Path}` has no lowering");
    }

    /// <summary>Lower the curated <c>std.mem.eql</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemEql(IReadOnlyList<Item> argItems)
    {
        var eqElem = LowerType(argItems[0]).Unqualified;
        var eqSink = new CType.Slice(eqElem.WithQuals(TypeQual.Const));
        var eqA = LowerExprSink(argItems[1], eqSink);
        var eqB = LowerExprSink(argItems[2], eqSink);
        return new ZigMemCall("Eql", eqElem, new List<CExpr> { eqA, eqB }) { Type = CType.Bool };
    }

    /// <summary>Lower the curated <c>std.mem.copyForwards</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemCopyForwards(IReadOnlyList<Item> argItems)
    {
        var cpElem = LowerType(argItems[0]).Unqualified;
        var cpDest = LowerExprSink(argItems[1], new CType.Slice(cpElem));
        var cpSrc = LowerExprSink(argItems[2], new CType.Slice(cpElem.WithQuals(TypeQual.Const)));
        return new ZigMemCall("CopyForwards", cpElem, new List<CExpr> { cpDest, cpSrc }) { Type = CType.Void };
    }

    /// <summary>Lower the curated <c>std.mem.span</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemSpan(IReadOnlyList<Item> argItems)
    {
        // std.mem.span(ptr) — a NUL-sentinel pointer `[*:0]T` → the `[]const T` slice before
        // the sentinel (dotcc's V1 sentinel = 0, erased in the type; the common `[*:0]const u8`
        // C-string case). The element comes from the pointer's pointee.
        var spArg = LowerExpr(argItems[0]);
        if (spArg.Type.Unqualified is not CType.Pointer sp)
        {
            throw new IrUnsupportedException(
                $"zig `std.mem.span` expects a sentinel-terminated pointer (`[*:0]T`), got {spArg.Type.Describe()}");
        }
        var spElem = sp.Pointee.Unqualified;
        return new ZigMemCall("SpanZ", spElem, new List<CExpr> { spArg })
        {
            Type = new CType.Slice(spElem.WithQuals(TypeQual.Const)),
        };
    }

    /// <summary>Lower the curated <c>std.mem.zeroes</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemZeroes(IReadOnlyList<Item> argItems)
    {
        // std.mem.zeroes(T) — an all-zero value of T → C#'s `default(T)` (zero-fills a scalar
        // or a struct uniformly). An ARRAY/slice type is a documented cut (arrays lower to a
        // pointer, so `default` would be a null pointer, not a zeroed array).
        var zt = LowerType(argItems[0]);
        // An ARRAY (std.mem.zeroInit's `std.mem.zeroes(@TypeOf(@field(value, f_name)))` over a `[3]u8` field, task
        // #162) is its elements zeroed, one flat run as a multi-dimensional array is laid out: the array value
        // `@splat(0)` builds, which a `default` (a null pointer) is not.
        if (zt.Unqualified is CType.Array { Count: int } zeroArray && FlatElementCount(zeroArray) is var zeroCount and <= 4096)
        {
            var zeroElement = zeroArray.Element;
            while (zeroElement.Unqualified is CType.Array innerRow) { zeroElement = innerRow.Element; }
            return new StackArray(zeroElement,
                Enumerable.Repeat<CExpr>(new DefaultLit { Type = zeroElement }, zeroCount).ToList()) { Type = zeroArray };
        }
        if (zt.Unqualified is CType.Array or CType.Slice)
        {
            throw new IrUnsupportedException(
                "zig `std.mem.zeroes` of a slice (or an array past 4096 elements) is not modeled yet");
        }
        return new DefaultLit { Type = zt };
    }

    /// <summary>Lower the curated <c>std.mem.asBytes</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemAsBytes(IReadOnlyList<Item> argItems)
    {
        // std.mem.asBytes(ptr) — the bytes of the single item `ptr` points at (Wyhash's input in
        // std.hash_map's auto-hash). zig types it `*[@sizeOf(T)]u8`; dotcc gives the byte SLICE over
        // the same memory, which is what every consumer coerces it to (`[]const u8`), since a pointer
        // to an array would render as a pointer to the element pointer. Curated because the source
        // (`AsBytesReturnType` / `CopyPtrAttrs`) builds a pointer type from comptime attribute
        // structs and the pointer size class, which dotcc does not model.
        var abPtr = LowerExpr(argItems[0]);
        if (abPtr.Type.Unqualified is not CType.Pointer { Pointee: var abPointee }
            || _ir.SizeOfConst(abPointee) is not { } abSize)
        {
            throw new IrUnsupportedException(
                $"zig `std.mem.asBytes` expects a pointer to a sized item, got {abPtr.Type.Describe()}");
        }
        var abElem = abPointee.IsConst ? CType.UChar.WithQuals(TypeQual.Const) : CType.UChar;
        var abBytes = new Cast(new CType.Pointer(abElem), abPtr) { Type = new CType.Pointer(abElem) };
        var abLen = new LitInt(abSize.ToString(CultureInfo.InvariantCulture), abSize) { Type = CType.ULong };
        return new SliceNew(abBytes, abLen, CType.UChar, abElem.IsConst) { Type = new CType.Slice(abElem) };
    }

    /// <summary>Lower the curated <c>std.mem.sliceAsBytes</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemSliceAsBytes(IReadOnlyList<Item> argItems)
    {
        // std.mem.sliceAsBytes(slice) — the same memory as a byte slice, `len * @sizeOf(T)` long (std.mem.indexOf /
        // eql's haystack and needle). Curated for the same reason as asBytes: its source return type is built
        // by `CopyPtrAttrs` through `@Pointer(size, attrs, child, null)`, which dotcc does not model.
        var sbImpure = _hoistImpureSeen;
        var sbSlice = LowerExpr(argItems[0]);
        if (sbSlice.Type.Unqualified is not CType.Slice { Element: var sbElemType })
        {
            throw new IrUnsupportedException($"zig `std.mem.sliceAsBytes` expects a slice, got {sbSlice.Type.Describe()}");
        }
        var sbByte = sbElemType.IsConst ? CType.UChar.WithQuals(TypeQual.Const) : CType.UChar;
        var (sbData, sbLen) = SliceParts(sbSlice, sbElemType, "std.mem.sliceAsBytes", sbImpure);
        var sbPtr = new Cast(new CType.Pointer(sbByte), sbData) { Type = new CType.Pointer(sbByte) };
        if (sbElemType.Unqualified.SizeOf != 1)
        {
            sbLen = new Binary(BinOp.Mul, sbLen, new SizeOfExpr(sbElemType.Unqualified) { Type = CType.ULong }) { Type = CType.ULong };
        }
        return new SliceNew(sbPtr, sbLen, CType.UChar, sbByte.IsConst) { Type = new CType.Slice(sbByte) };
    }

    /// <summary>Lower the curated <c>std.mem.bytesAsValue</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemBytesAsValue(IReadOnlyList<Item> argItems, string methodName)
    {
        // std.mem.bytesAsValue(T, bytes) — the bytes read as a `*T` (const when the bytes are); bytesToValue(T, bytes)
        // is the value itself, a copy (task #126). zig's `@ptrCast` checks neither length nor alignment: the source
        // return type, `CopyPtrAttrs(B, .one, T)`, lowers the alignment to the bytes' own, so the read is unaligned.
        // Curated for asBytes' reason: that return type is reified through `@Pointer(size, attrs, child, null)`.
        var bvType = LowerType(argItems[0]);
        var bvBytes = LowerExpr(argItems[1]);
        // A string literal is zig's `*const [N:0]u8`; dotcc types it as the array, so it is read through a const slice
        // over it. Any other array VALUE is refused below, as zig refuses it (the parameter wants a pointer).
        if (argItems[1].Content is Zig.StrLit && bvBytes.Type.Unqualified is CType.Array { Element: var bvLitElem })
        {
            bvBytes = CoerceToSlice(bvBytes, new CType.Slice(bvLitElem.WithQuals(TypeQual.Const)));
        }
        var (bvData, bvConst) = bvBytes.Type.Unqualified switch
        {
            CType.Slice { Element: var bvElem } =>
                ((CExpr)new Member(bvBytes, "Ptr", false) { Type = new CType.Pointer(bvElem) }, bvElem.IsConst),
            CType.Pointer { Pointee: var bvPointee } =>
                (bvBytes, bvPointee.IsConst || bvPointee.Unqualified is CType.Array { Element.IsConst: true }),
            _ => throw new IrUnsupportedException(
                $"zig `std.mem.{methodName}` expects a pointer to bytes or a byte slice, got {bvBytes.Type.Describe()}"),
        };
        var bvTarget = new CType.Pointer(bvConst ? bvType.WithQuals(TypeQual.Const) : bvType);
        var bvPtr = new Cast(bvTarget, bvData) { Type = bvTarget };
        return methodName == "bytesAsValue" ? bvPtr : new Unary(UnOp.Deref, bvPtr) { Type = bvType };
    }

    /// <summary>Lower the curated <c>std.mem.bytesAsSlice</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemBytesAsSlice(IReadOnlyList<Item> argItems)
    {
        // std.mem.bytesAsSlice(T, bytes) — the bytes read as a `[]T` of `bytes.len / @sizeOf(T)` elements, const when
        // the bytes are (task #137). Curated for asBytes' reason: its return type is `CopyPtrAttrs(B, .slice, T)`. The
        // source divides with `@divExact`, which dotcc lowers unchecked everywhere; a zero-size T gives an empty slice.
        var bsType = LowerType(argItems[0]);
        var bsImpure = _hoistImpureSeen;
        var bsBytes = LowerExpr(argItems[1]);
        CExpr bsData, bsLen;
        bool bsConst;
        if (bsBytes.Type.Unqualified is CType.Slice { Element: var bsElem })
        {
            (bsData, bsLen) = SliceParts(bsBytes, bsElem, "std.mem.bytesAsSlice", bsImpure);
            bsConst = bsElem.IsConst;
        }
        else if (bsBytes.Type.Unqualified is CType.Pointer { Pointee: var bsPointee }
            && bsPointee.Unqualified is CType.Array { Count: { } bsCount } bsArr)
        {
            (bsData, bsLen) = (bsBytes, new LitInt(bsCount.ToString(CultureInfo.InvariantCulture), bsCount) { Type = CType.ULong });
            bsConst = bsPointee.IsConst || bsArr.Element.IsConst;
        }
        else
        {
            throw new IrUnsupportedException(
                $"zig `std.mem.bytesAsSlice` expects a pointer to a byte array or a byte slice, got {bsBytes.Type.Describe()}");
        }
        var bsElemType = bsConst ? bsType.WithQuals(TypeQual.Const) : bsType;
        var bsPtr = new Cast(new CType.Pointer(bsElemType), bsData) { Type = new CType.Pointer(bsElemType) };
        CExpr bsCountExpr = _ir.SizeOfConst(bsType) is 0
            ? new LitInt("0", 0) { Type = CType.ULong }
            : new Binary(BinOp.Div, bsLen, new SizeOfExpr(bsType.Unqualified) { Type = CType.ULong }) { Type = CType.ULong };
        return new SliceNew(bsPtr, bsCountExpr, bsType.Unqualified, bsConst) { Type = new CType.Slice(bsElemType) };
    }

    /// <summary>Lower the curated <c>std.mem.sliceTo</c> call (a registry row; the dispatcher checks the arity).</summary>
    internal CExpr LowerMemSliceTo(IReadOnlyList<Item> argItems)
    {
        // std.mem.sliceTo(ptr, end) — the elements before the first `end` (task #127). Curated for asBytes' reason: the
        // source's return type, `SliceTo(T, end)`, is reified through `@Pointer(.slice, attrs, Elem, sentinel)`.
        // A slice, an array pointer or a string literal is scanned within its length; a many-item or C pointer
        // is scanned unbounded, `end` being its sentinel. Constness is kept (a `[]T` in, a `[]T` out). The result's
        // own sentinel (zig keeps `end` as one when the input had it) is erased, as dotcc erases sentinels.
        var stPtr = LowerExpr(argItems[0]);
        if (stPtr.Type.Unqualified is CType.Array { Element: var stArrElem })
        {
            // A string literal (zig's `*const [N:0]u8`) or an array reached through `&`: viewed as a slice.
            stPtr = CoerceToSlice(stPtr, new CType.Slice(argItems[0].Content is Zig.StrLit ? stArrElem.WithQuals(TypeQual.Const) : stArrElem));
        }
        else if (stPtr.Type.Unqualified is CType.Pointer { Pointee.Unqualified: CType.Array { Element: var stPtrArrElem } } stArrPtr)
        {
            stPtr = CoerceToSlice(new Unary(UnOp.Deref, stPtr) { Type = stArrPtr.Pointee }, new CType.Slice(stPtrArrElem));
        }
        switch (stPtr.Type.Unqualified)
        {
            case CType.Slice { Element: var stElem }:
            {
                var stEnd = LowerExprSink(argItems[1], stElem.Unqualified);
                return new ZigMemCall("SliceTo", stElem.Unqualified, new List<CExpr> { stPtr, stEnd }) { Type = new CType.Slice(stElem) };
            }
            case CType.Pointer { Pointee: var stPointee } when stPointee.Unqualified is not CType.VoidType:
            {
                var stEnd = LowerExprSink(argItems[1], stPointee.Unqualified);
                return new ZigMemCall("SliceToSentinel", stPointee.Unqualified, new List<CExpr> { stPtr, stEnd })
                {
                    Type = new CType.Slice(stPointee.Unqualified.WithQuals(TypeQual.Const)),
                };
            }
            default:
                throw new IrUnsupportedException(
                    $"zig `std.mem.sliceTo` expects a pointer to an array, a many-item pointer or a slice, got {stPtr.Type.Describe()}");
        }
    }
}
