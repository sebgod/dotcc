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

    /// <summary>The curated <c>std.ArrayList(T)</c> the member call's receiver is.</summary>
    private static CType.ZigList ListOf(CuratedMemberCall c)
        => c.Recv?.Type.Unqualified as CType.ZigList ?? throw new IrUnsupportedException($"zig `.{c.Member}` needs a std.ArrayList receiver");

    /// <summary>The lowered receiver of a list member call; a list member always has one.</summary>
    private static CExpr ListRecv(CuratedMemberCall c)
        => c.Recv ?? throw new IrUnsupportedException($"zig `.{c.Member}` needs a std.ArrayList receiver");

    /// <summary>The slice argument <paramref name="item"/> of a list call, lowered at <c>[]T</c> and coerced when it is not one yet.</summary>
    private CExpr LowerListSliceArg(Item item, CType elem)
    {
        var sliceType = new CType.Slice(elem);
        var sliceArg = LowerExprSink(item, sliceType);
        return sliceArg.Type?.Unqualified is CType.Slice ? sliceArg : CoerceToSlice(sliceArg, sliceType);
    }

    /// <summary>Lower <c>list.append(alloc, item)</c> (a registry row).</summary>
    internal CExpr LowerListAppend(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 2, "(alloc, item)");
        var a = LowerListAllocatorArg(c.Member, c.Args[0]);
        var item = LowerExprSink(c.Args[1], ListOf(c).Element);   // result-located at the element type
        return new ZigListCall(ListRecv(c), "Append", new List<CExpr> { a, item, OomLit() }) { Type = new CType.ErrorUnion(CType.Void) };
    }

    /// <summary>Lower <c>list.appendSlice(alloc, slice)</c> (a registry row). <c>&amp;arr</c> / a <c>[N]T</c> value /
    /// <c>&amp;.{ 1, 1 }</c> coerces to a slice as at any other slice sink.</summary>
    internal CExpr LowerListAppendSlice(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 2, "(alloc, slice)");
        var a = LowerListAllocatorArg(c.Member, c.Args[0]);
        var s = LowerListSliceArg(c.Args[1], ListOf(c).Element);
        return new ZigListCall(ListRecv(c), "AppendSlice", new List<CExpr> { a, s, OomLit() }) { Type = new CType.ErrorUnion(CType.Void) };
    }

    /// <summary>Lower <c>list.pop()</c> (a registry row): <c>?T</c>.</summary>
    internal CExpr LowerListPop(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 0, "()");
        return new ZigListCall(ListRecv(c), "Pop", new List<CExpr>()) { Type = new CType.Optional(ListOf(c).Element) };
    }

    /// <summary>Lower <c>list.deinit(alloc)</c> (a registry row).</summary>
    internal CExpr LowerListDeinit(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 1, "(alloc)");
        var a = LowerListAllocatorArg(c.Member, c.Args[0]);
        return new ZigListCall(ListRecv(c), "Deinit", new List<CExpr> { a }) { Type = CType.Void };
    }

    /// <summary>Lower <c>list.clearRetainingCapacity()</c> (a registry row).</summary>
    internal CExpr LowerListClearRetainingCapacity(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 0, "()");
        return new ZigListCall(ListRecv(c), "ClearRetainingCapacity", new List<CExpr>()) { Type = CType.Void };
    }

    /// <summary>Lower <c>list.insert(alloc, index, item)</c> (a registry row; the index members are task #74).</summary>
    internal CExpr LowerListInsert(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 3, "(alloc, index, item)");
        var a = LowerListAllocatorArg(c.Member, c.Args[0]);
        var at = LowerExprSink(c.Args[1], CType.ULong);
        var item = LowerExprSink(c.Args[2], ListOf(c).Element);
        return new ZigListCall(ListRecv(c), "Insert", new List<CExpr> { a, at, item, OomLit() }) { Type = new CType.ErrorUnion(CType.Void) };
    }

    /// <summary>Lower <c>list.insertSlice(alloc, index, slice)</c> (a registry row; task #138): the tail moves up by <c>slice.len</c>.</summary>
    internal CExpr LowerListInsertSlice(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 3, "(alloc, index, slice)");
        var a = LowerListAllocatorArg(c.Member, c.Args[0]);
        var at = LowerExprSink(c.Args[1], CType.ULong);
        var s = LowerListSliceArg(c.Args[2], ListOf(c).Element);
        return new ZigListCall(ListRecv(c), "InsertSlice", new List<CExpr> { a, at, s, OomLit() }) { Type = new CType.ErrorUnion(CType.Void) };
    }

    /// <summary>Lower <c>list.appendNTimes(alloc, value, n)</c> or its <paramref name="assume"/>-capacity form (a registry row; task #172).</summary>
    internal CExpr LowerListAppendNTimes(CuratedMemberCall c, bool assume)
    {
        RequireListArgs(c.Member, c.Args, assume ? 2 : 3, assume ? "(value, n)" : "(alloc, value, n)");
        var nArgs = new List<CExpr>();
        if (!assume) { nArgs.Add(LowerListAllocatorArg(c.Member, c.Args[0])); }
        nArgs.Add(LowerExprSink(c.Args[assume ? 0 : 1], ListOf(c).Element));
        nArgs.Add(LowerExprSink(c.Args[assume ? 1 : 2], CType.ULong));
        if (!assume) { nArgs.Add(OomLit()); }
        return new ZigListCall(ListRecv(c), assume ? "AppendNTimesAssumeCapacity" : "AppendNTimes", nArgs)
        { Type = assume ? CType.Void : new CType.ErrorUnion(CType.Void) };
    }

    /// <summary>Lower <c>list.replaceRange(alloc, start, len, new_items)</c> or its <paramref name="assume"/>-capacity form (a registry row; task #172).</summary>
    internal CExpr LowerListReplaceRange(CuratedMemberCall c, bool assume)
    {
        RequireListArgs(c.Member, c.Args, assume ? 3 : 4, assume ? "(start, len, new_items)" : "(alloc, start, len, new_items)");
        var rArgs = new List<CExpr>();
        if (!assume) { rArgs.Add(LowerListAllocatorArg(c.Member, c.Args[0])); }
        var first = assume ? 0 : 1;
        rArgs.Add(LowerExprSink(c.Args[first], CType.ULong));
        rArgs.Add(LowerExprSink(c.Args[first + 1], CType.ULong));
        rArgs.Add(LowerListSliceArg(c.Args[first + 2], ListOf(c).Element));
        if (!assume) { rArgs.Add(OomLit()); }
        return new ZigListCall(ListRecv(c), assume ? "ReplaceRangeAssumeCapacity" : "ReplaceRange", rArgs)
        { Type = assume ? CType.Void : new CType.ErrorUnion(CType.Void) };
    }

    /// <summary>Lower <c>list.orderedRemove(i)</c> or <c>list.swapRemove(i)</c> (a registry row).</summary>
    internal CExpr LowerListRemove(CuratedMemberCall c, bool ordered)
    {
        RequireListArgs(c.Member, c.Args, 1, "(index)");
        var at = LowerExprSink(c.Args[0], CType.ULong);
        return new ZigListCall(ListRecv(c), ordered ? "OrderedRemove" : "SwapRemove", new List<CExpr> { at }) { Type = ListOf(c).Element };
    }

    /// <summary>Lower <c>list.getLast()</c> (a registry row): zig 0.17-dev, <c>?T</c>, null for an empty list.</summary>
    internal CExpr LowerListGetLast(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 0, "()");
        return new ZigListCall(ListRecv(c), "GetLast", new List<CExpr>()) { Type = new CType.Optional(ListOf(c).Element) };
    }

    /// <summary>Lower <c>list.ensureTotalCapacity(alloc, n)</c> or <c>ensureUnusedCapacity</c> (a registry row).</summary>
    internal CExpr LowerListEnsureCapacity(CuratedMemberCall c, bool total)
    {
        RequireListArgs(c.Member, c.Args, 2, "(alloc, n)");
        var a = LowerListAllocatorArg(c.Member, c.Args[0]);
        var n = LowerExprSink(c.Args[1], CType.ULong);
        return new ZigListCall(ListRecv(c), total ? "EnsureTotalCapacity" : "EnsureUnusedCapacity", new List<CExpr> { a, n, OomLit() })
        { Type = new CType.ErrorUnion(CType.Void) };
    }

    /// <summary>Lower <c>list.appendAssumeCapacity(item)</c> (a registry row).</summary>
    internal CExpr LowerListAppendAssumeCapacity(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 1, "(item)");
        var item = LowerExprSink(c.Args[0], ListOf(c).Element);
        return new ZigListCall(ListRecv(c), "AppendAssumeCapacity", new List<CExpr> { item }) { Type = CType.Void };
    }

    /// <summary>Lower <c>list.shrinkRetainingCapacity(n)</c> (a registry row).</summary>
    internal CExpr LowerListShrinkRetainingCapacity(CuratedMemberCall c)
    {
        RequireListArgs(c.Member, c.Args, 1, "(n)");
        var n = LowerExprSink(c.Args[0], CType.ULong);
        return new ZigListCall(ListRecv(c), "ShrinkRetainingCapacity", new List<CExpr> { n }) { Type = CType.Void };
    }

    /// <summary>Lower the field <c>list.items</c> (a registry row): the occupied prefix as a mutable <c>[]T</c>, so a
    /// subscript, <c>.len</c> and <c>for (list.items) |x|</c> ride the ordinary slice lowering on the result.</summary>
    internal CExpr LowerListItems(CuratedMemberCall c)
        => new Member(ListRecv(c), "Items", c.Arrow) { Type = new CType.Slice(ListOf(c).Element) };

    /// <summary>Lower the field <c>list.capacity</c> (a registry row).</summary>
    internal CExpr LowerListCapacity(CuratedMemberCall c)
        => new Member(ListRecv(c), "Cap", c.Arrow) { Type = CType.ULong, IsLValue = true };

    /// <summary>Lower the decl literal <c>.empty</c> at a list sink (a registry row): zig's empty unmanaged list is exactly
    /// <c>default</c>, a null pointer with zero length and capacity (the first growing call allocates).</summary>
    internal CExpr LowerListEmpty(CuratedMemberCall c)
        => new DefaultLit { Type = c.Sink ?? throw new IrUnsupportedException("zig `.empty` needs a std.ArrayList sink") };

    /// <summary>The allocator as a value for a runtime helper: the lowered receiver, or the devirtualised default or FBA site materialised.</summary>
    private CExpr AllocatorValue(CuratedMemberCall c)
        => c.Recv
            ?? (c.Kind == AllocKind.Fba && c.Base?.Content is Zig.Ident { Arg0: var fbaTok }
                ? MaterializeFba(_fbaAllocatorSites[Tok(fbaTok)])
                : MaterializeCHeap());

    /// <summary>Lower <c>a.alloc(T, n)</c> or <c>a.alignedAlloc(T, alignment, n)</c> (a registry row). The comptime alignment
    /// is not threaded through: dotcc's alignment model caps at 16 (ZigAlloc.AlignOf), met by construction on the C heap
    /// and arena while the FBA aligns up to it, so over-alignment is the same documented cut as elsewhere.</summary>
    internal CExpr LowerAllocatorAlloc(CuratedMemberCall c, bool aligned)
    {
        int wantArgs = aligned ? 3 : 2;
        if (c.Args.Count != wantArgs)
        {
            throw new IrUnsupportedException($"zig allocator `.{c.Member}` expects ({(aligned ? "type, alignment, count" : "type, count")}); got {c.Args.Count} argument(s)");
        }
        var elem = LowerType(c.Args[0]);
        var count = LowerExpr(c.Args[wantArgs - 1]);
        return new AllocCall(c.Recv, elem, count, ErrorCode("OutOfMemory"), c.FbaCtx)
        {
            Type = new CType.ErrorUnion(new CType.Slice(elem)),
        };
    }

    /// <summary>Lower <c>a.dupe(T, slice)</c> (a registry row): a fresh allocation holding a copy (std.mem.join's
    /// <c>dupe(u8, &amp;[1]u8{0})</c>), one runtime call so the source is evaluated once.</summary>
    internal CExpr LowerAllocatorDupe(CuratedMemberCall c)
    {
        if (c.Args.Count != 2)
        {
            throw new IrUnsupportedException($"zig allocator `.dupe` expects (type, slice); got {c.Args.Count} argument(s)");
        }
        var elem = LowerType(c.Args[0]);
        // At a `[]const T` sink, so C# infers Dupe's `T` from the argument (no Slice to ConstSlice step).
        var src = LowerExprSink(c.Args[1], new CType.Slice(elem.WithQuals(TypeQual.Const)));
        return new Call("ZigAlloc.Dupe", new List<CExpr> { AllocatorValue(c), src, OomLit() })
        {
            Type = new CType.ErrorUnion(new CType.Slice(elem)),
        };
    }

    /// <summary>Lower <c>a.dupeSentinel(T, slice, s)</c> or <c>a.allocSentinel(T, n, s)</c> (a registry row; task #107): one
    /// element more, the sentinel stored past the end. dotcc's slice carries no sentinel, so the result is the
    /// <c>len</c>-long slice before it.</summary>
    internal CExpr LowerAllocatorSentinel(CuratedMemberCall c, bool dupe)
    {
        if (c.Args.Count != 3)
        {
            throw new IrUnsupportedException(
                $"zig allocator `.{c.Member}` expects (type, {(dupe ? "slice" : "count")}, sentinel); got {c.Args.Count} argument(s)");
        }
        var elem = LowerType(c.Args[0]);
        var source = dupe
            ? LowerExprSink(c.Args[1], new CType.Slice(elem.WithQuals(TypeQual.Const)))
            : LowerExprSink(c.Args[1], CType.ULong);
        var sentinel = LowerExprSink(c.Args[2], elem);
        var helper = dupe ? "ZigAlloc.DupeSentinel" : "ZigAlloc.AllocSentinel";
        return new Call(helper, new List<CExpr> { AllocatorValue(c), source, new Cast(elem, sentinel) { Type = elem }, OomLit() })
        {
            Type = new CType.ErrorUnion(new CType.Slice(elem)),
        };
    }

    /// <summary>Lower the vtable's byte-level calls <c>rawAlloc</c> / <c>rawResize</c> / <c>rawRemap</c> / <c>rawFree</c> (the
    /// registry rows; std.Io.Writer.Allocating grows its buffer through them): one runtime helper each, over the allocator
    /// as a value.</summary>
    internal CExpr LowerAllocatorRaw(CuratedMemberCall c, string method)
    {
        var bytes = new CType.Slice(CType.UChar);
        var alignmentType = new CType.Named(AlignmentTypeName);
        var (helper, paramTypes, resultType) = method switch
        {
            "rawAlloc" => ("ZigAlloc.RawAlloc", new[] { CType.ULong, alignmentType, CType.ULong }, (CType)new CType.Pointer(CType.UChar)),
            "rawResize" => ("ZigAlloc.RawResize", new[] { bytes, alignmentType, CType.ULong, CType.ULong }, CType.Bool),
            "rawRemap" => ("ZigAlloc.RawRemap", new[] { bytes, alignmentType, CType.ULong, CType.ULong }, new CType.Pointer(CType.UChar)),
            _ => ("ZigAlloc.RawFree", new[] { bytes, alignmentType, CType.ULong }, CType.Void),
        };
        if (c.Args.Count != paramTypes.Length)
        {
            throw new IrUnsupportedException($"zig allocator `.{method}` expects {paramTypes.Length} argument(s); got {c.Args.Count}");
        }
        var rawArgs = new List<CExpr> { AllocatorValue(c) };
        for (var i = 0; i < paramTypes.Length; i++) { rawArgs.Add(LowerExprSink(c.Args[i], paramTypes[i])); }
        return new Call(helper, rawArgs) { Type = resultType };
    }

    /// <summary>Lower <c>a.free(slice)</c> (a registry row).</summary>
    internal CExpr LowerAllocatorFree(CuratedMemberCall c)
    {
        if (c.Args.Count != 1)
        {
            throw new IrUnsupportedException($"zig allocator `.free` expects (slice); got {c.Args.Count} argument(s)");
        }
        var sliceExpr = LowerExpr(c.Args[0]);
        if (sliceExpr.Type.Unqualified is not CType.Slice slc)
        {
            throw new IrUnsupportedException($"zig allocator `.free` expects a slice argument, got {sliceExpr.Type.Describe()}");
        }
        return new FreeCall(c.Recv, sliceExpr, slc.Element, c.FbaCtx) { Type = CType.Void };
    }

    /// <summary>Lower <c>a.create(T)</c> (a registry row; Milestone U): <c>Error!*T</c> is <c>ErrorUnion(Pointer(T))</c> at the
    /// IR-type level (so <c>try</c> unwraps to a <c>*T</c>); the runtime carrier is <c>ErrUnion&lt;nuint&gt;</c> (a pointer can't
    /// be an ErrUnion generic argument), and the <c>try</c> lowering casts the unwrapped nuint to <c>T*</c>.</summary>
    internal CExpr LowerAllocatorCreate(CuratedMemberCall c)
    {
        if (c.Args.Count != 1)
        {
            throw new IrUnsupportedException($"zig allocator `.create` expects (type); got {c.Args.Count} argument(s)");
        }
        var elem = LowerType(c.Args[0]);
        return new CreateCall(c.Recv, elem, ErrorCode("OutOfMemory"), c.FbaCtx)
        {
            Type = new CType.ErrorUnion(new CType.Pointer(elem)),
        };
    }

    /// <summary>Lower <c>a.destroy(p)</c> (a registry row): frees a single object from <c>.create</c>.</summary>
    internal CExpr LowerAllocatorDestroy(CuratedMemberCall c)
    {
        if (c.Args.Count != 1)
        {
            throw new IrUnsupportedException($"zig allocator `.destroy` expects (pointer); got {c.Args.Count} argument(s)");
        }
        var ptrExpr = LowerExpr(c.Args[0]);
        if (ptrExpr.Type.Unqualified is not CType.Pointer pp)
        {
            throw new IrUnsupportedException($"zig allocator `.destroy` expects a pointer argument, got {ptrExpr.Type.Describe()}");
        }
        return new DestroyCall(c.Recv, ptrExpr, pp.Pointee, c.FbaCtx) { Type = CType.Void };
    }

    /// <summary>Lower <c>a.realloc(slice, n)</c> (a registry row): grows or shrinks a slice, <c>Error![]T</c>.</summary>
    internal CExpr LowerAllocatorRealloc(CuratedMemberCall c)
    {
        if (c.Args.Count != 2)
        {
            throw new IrUnsupportedException($"zig allocator `.realloc` expects (slice, new count); got {c.Args.Count} argument(s)");
        }
        var oldSlice = LowerExpr(c.Args[0]);
        if (oldSlice.Type.Unqualified is not CType.Slice rslc)
        {
            throw new IrUnsupportedException($"zig allocator `.realloc` expects a slice argument, got {oldSlice.Type.Describe()}");
        }
        var newCount = LowerExpr(c.Args[1]);
        return new ReallocCall(c.Recv, oldSlice, rslc.Element, newCount, ErrorCode("OutOfMemory"), c.FbaCtx)
        {
            Type = new CType.ErrorUnion(new CType.Slice(rslc.Element)),
        };
    }

    /// <summary>Lower <c>a.resize(slice, n)</c> (in place, <c>bool</c>) or <c>a.remap(slice, n)</c> (possibly moving,
    /// <c>?[]T</c>) (the registry rows). A provable FBA site devirtualises (FbaCtx set); an opaque Allocator dispatches
    /// through the vtable (Recv set). Only the statically-known C-heap default stays deferred: its in-place result is
    /// page-dependent (real zig answers from malloc_usable_size / page rounding), so a devirtualised guess would diverge
    /// from the oracle; use <c>.realloc</c> there.</summary>
    internal CExpr LowerAllocatorResize(CuratedMemberCall c, bool remap)
    {
        if (c.FbaCtx is null && c.Recv is null)
        {
            throw new IrUnsupportedException(
                $"zig allocator `.{c.Member}` on the statically-known C-heap default is deferred "
                + "(the in-place page/malloc_usable_size result would diverge from real zig); "
                + "use `.realloc`, or route through an opaque `std.mem.Allocator` / a FixedBufferAllocator");
        }
        if (c.Args.Count != 2)
        {
            throw new IrUnsupportedException($"zig allocator `.{c.Member}` expects (slice, new count); got {c.Args.Count} argument(s)");
        }
        var oldSlice = LowerExpr(c.Args[0]);
        if (oldSlice.Type.Unqualified is not CType.Slice sl)
        {
            throw new IrUnsupportedException($"zig allocator `.{c.Member}` expects a slice argument, got {oldSlice.Type.Describe()}");
        }
        var newCount = LowerExpr(c.Args[1]);
        return remap
            ? new RemapCall(c.Recv, oldSlice, sl.Element, newCount, c.FbaCtx) { Type = new CType.Optional(new CType.Slice(sl.Element)) }
            : new ResizeCall(c.Recv, oldSlice, sl.Element, newCount, c.FbaCtx) { Type = CType.Bool };
    }

    /// <summary>Lower <c>fba.allocator()</c> / <c>arena.allocator()</c> (the registry rows): the Allocator fat pointer over the
    /// runtime allocator itself, which needs <c>&amp;self</c> as the vtable context; the result is opaque (the indirect dispatch path).</summary>
    internal CExpr LowerRuntimeAllocatorAllocator(CuratedMemberCall c)
    {
        var recv = c.Recv ?? throw new IrUnsupportedException("zig `.allocator()` needs a receiver");
        var allocTy = recv.Type.Unqualified is CType.Named named ? named.Name : "";
        if (c.Args.Count != 0)
        {
            throw new IrUnsupportedException($"zig `{allocTy}.allocator()` takes no arguments");
        }
        if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
        {
            s.AddressTaken = true;
        }
        var selfAddr = new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
        var factory = allocTy == FbaTypeName ? "ZigAlloc.FbaAllocator" : "ZigAlloc.ArenaToAllocator";
        return new Call(factory, new List<CExpr> { selfAddr },
            new List<CType> { new CType.Pointer(new CType.Named(allocTy)) }, null) { Type = new CType.Allocator() };
    }

    /// <summary>Lower <c>arena.deinit()</c> (a registry row; Milestone U): frees the whole chunk chain, a static wrapper
    /// called by-ref on the local. The headline use is <c>defer arena.deinit();</c>.</summary>
    internal CExpr LowerArenaDeinit(CuratedMemberCall c)
    {
        var recv = c.Recv ?? throw new IrUnsupportedException("zig `arena.deinit()` needs a receiver");
        if (c.Args.Count != 0)
        {
            throw new IrUnsupportedException("zig `arena.deinit()` takes no arguments");
        }
        if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } sd })
        {
            sd.AddressTaken = true;
        }
        var arenaAddr = new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
        return new Call("ZigAlloc.ArenaDeinit", new List<CExpr> { arenaAddr },
            new List<CType> { new CType.Pointer(new CType.Named(ArenaTypeName)) }, null) { Type = CType.Void };
    }

    /// <summary>Lower an instance method of the curated <c>std.mem.Alignment</c> carrier (the registry rows; std-internal code
    /// names them, hash_map's <c>max_align.forward(total)</c>): a static helper on the runtime <c>Alignment</c>, receiver
    /// first, taking <paramref name="arity"/> address arguments.</summary>
    internal CExpr LowerAlignmentMethod(CuratedMemberCall c, string helper, CType ret, int arity)
    {
        var recv = c.Recv ?? throw new IrUnsupportedException($"zig std.mem.Alignment `.{c.Member}` needs a receiver");
        if (c.Args.Count != arity)
        {
            throw ZigCuratedStd.UnmodeledMember("std.mem.Alignment", c.Member, c.Args.Count);
        }
        var alignArgs = new List<CExpr> { recv };
        var alignTypes = new List<CType> { recv.Type };
        foreach (var a in c.Args)
        {
            alignArgs.Add(LowerExprSink(a, CType.ULong));
            alignTypes.Add(CType.ULong);
        }
        return new Call(helper, alignArgs, alignTypes, null) { Type = ret };
    }

    /// <summary>Lower <c>Alignment.fromByteUnits(n)</c> (a registry row), spelled through the type or as a decl literal
    /// (std.MultiArrayList's <c>.big_align = mem.Alignment.fromByteUnits(big_align)</c>, task #108).</summary>
    internal CExpr LowerAlignmentFromByteUnits(CuratedMemberCall c)
    {
        if (c.Args.Count != 1) { throw AlignmentStaticArity(c); }
        return new Call("Alignment.fromByteUnits", new List<CExpr> { LowerExprSink(c.Args[0], CType.ULong) },
            new List<CType> { CType.ULong }, null) { Type = new CType.Named(AlignmentTypeName) };
    }

    /// <summary>Lower <c>Alignment.of(T)</c> (a registry row; std.Io.Writer.Allocating's <c>.of(u8)</c>): T's own alignment.</summary>
    internal CExpr LowerAlignmentOf(CuratedMemberCall c)
    {
        if (c.Args.Count != 1) { throw AlignmentStaticArity(c); }
        var ofAlign = _ir.AlignOfConst(LowerType(c.Args[0]));
        return new Call("Alignment.fromByteUnits",
            new List<CExpr> { new LitInt(ofAlign.ToString(CultureInfo.InvariantCulture), ofAlign) { Type = CType.ULong } },
            new List<CType> { CType.ULong }, null) { Type = new CType.Named(AlignmentTypeName) };
    }

    /// <summary>The error for an Alignment static called with the wrong argument count, as a decl literal of an unknown function reports it.</summary>
    private static IrUnsupportedException AlignmentStaticArity(CuratedMemberCall c)
        => new($"decl literal `.{c.Member}(…)`: '{AlignmentTypeName}' has no function '{c.Member}'");

    /// <summary>Lower the call of a curated member row <paramref name="row"/> with the call context <paramref name="call"/>.</summary>
    private CExpr LowerCuratedMember(CuratedStdEntry row, CuratedMemberCall call)
        => row.LowerMember is { } lower ? lower(this, call) : throw new IrUnsupportedException($"zig `{row.Path}` has no lowering");
}
