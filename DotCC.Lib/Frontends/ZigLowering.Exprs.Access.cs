#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions, access: dereference, subscript, slicing and optional unwrap.
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lowers a postfix access: dereference <c>p.*</c>, subscript, the four slicing forms and the optional unwrap <c>.?</c>.</summary>
    private CExpr LowerAccessExpr(Item expr)
    {
        switch (expr.Content)
        {
            // Postfix deref `p.*` and subscript `a[i]` → the C Unary(Deref)/Index IR.
            case Zig.Deref d:
            {
                var operand = LowerExpr(d.Arg0);
                // `s[a..b].*` (std.fmt's `specifier_arg[0..specifier_arg.len].*`): zig allows it only for a
                // comptime-known length and yields an array COPY. Bound to a `const`, the copy is never
                // written, so the slice itself stands for it; `&copy` coerces back (CoerceToSlice).
                if (operand.Type.Unqualified is CType.Slice) { return operand; }
                // `"ABC…".*` (std.base64's `standard_alphabet_chars`): a string literal is `*const [N:0]u8`, so `.*` is the
                // array VALUE, which dotcc represents by its element pointer, the literal itself (task #75). It had been
                // the first byte.
                if (operand.Type.Unqualified is CType.Array && IsStringLiteralValue(operand)) { return operand; }
                var pointee = operand.Type.Unqualified switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => operand.Type,
                };
                return new Unary(UnOp.Deref, operand) { Type = pointee, IsLValue = true };
            }
            case Zig.Index ix:
            {
                // A comptime member-list index (`field_names[0]`) folds to a literal — the base is a
                // comptime list with no runtime storage, so it must be caught before LowerExpr sees it.
                if (TryFoldTypeInfoListValue(expr, out var tiIdx)) { return tiIdx; }
                // `fmt[i]` over a comptime STRING with a comptime index (std.Io.Writer.print's format scan)
                // is that byte, a comptime value, so a `switch (fmt[i])` over it folds.
                if (AsLitStr(TryEvalComptime(ix.Arg0)) is LitStr cstr)
                {
                    CExpr ciExpr;
                    using (EnterThrowawayHoist()) { ciExpr = LowerUsizeOperand(ix.Arg2); }
                    if (_ir.ConstEval(ciExpr) is { } ci)
                    {
                        var bytes = DotCC.EmitHelpers.StringByteValues(cstr.Segments);
                        if (ci >= 0 && ci < bytes.Count)
                        {
                            return new LitInt(bytes[(int)ci].ToString(System.Globalization.CultureInfo.InvariantCulture), bytes[(int)ci])
                            { Type = LowerPrim("u8") };
                        }
                    }
                }
                var baseExpr = LowerExpr(ix.Arg0);
                // An index is a `usize` result location, so a cast builtin there infers it (std.base64's
                // `encoder.alphabet_chars[@truncate(bits >> 18 & 0x3f)]`).
                var idx = LowerUsizeOperand(ix.Arg2);
                // `v[i]` of a SIMD vector: a lane read (T5).
                if (baseExpr.Type.Unqualified is CType.Vector laneVector) { return VectorLane(baseExpr, idx, laneVector); }
                // `p[i]` through a single-item pointer to a vector (std.hash.XxHash3's `*align(1) const Block`, task #175) reads
                // a lane of the pointee, as indexing a `*[N]T` reads an element; a `[*]` pointer to vectors indexes vectors.
                if (baseExpr.Type.Unqualified is CType.Pointer { Pointee.Unqualified: CType.Vector pointeeVector }
                    && PointerSizeOfValue(ix.Arg0) is not ("many" or "c"))
                {
                    return VectorLane(new Unary(UnOp.Deref, baseExpr) { Type = pointeeVector, IsLValue = true }, idx, pointeeVector);
                }
                // A tuple subscript `t[N]` (N a literal) reads the Nth element → `.ItemN+1`
                // (Milestone G). A tuple has no runtime indexing (the field is statically named),
                // so a non-literal index is rejected.
                if (baseExpr.Type.Unqualified is CType.Tuple tup)
                {
                    if (idx is not LitInt { Value: { } n })
                    {
                        throw new IrUnsupportedException(
                            "zig tuple index must be an integer literal (a tuple has no runtime indexing)");
                    }
                    if (n < 0 || n >= tup.Elements.Count)
                    {
                        throw new IrUnsupportedException(
                            $"zig tuple index {n} is out of range (the tuple has {tup.Elements.Count} element(s))");
                    }
                    return new TupleIndex(baseExpr, (int)n, tup.Elements[(int)n]) { Type = tup.Elements[(int)n] };
                }
                // A slice subscript indexes through its data pointer: `s[i]` → `s.Ptr[i]`.
                if (baseExpr.Type.Unqualified is CType.Slice slc)
                {
                    var ptr = new Member(baseExpr, "Ptr", false) { Type = new CType.Pointer(slc.Element) };
                    return new DotCC.Ir.Index(ptr, idx) { Type = slc.Element, IsLValue = true };
                }
                // `buf[i]` through a `*[N]T` indexes the pointed-at ARRAY's elements (zig auto-derefs), not an
                // array of arrays: it used to lower to `(buf + i * N)`, a non-lvalue at the wrong stride.
                if (PointedArray(baseExpr) is ({ } pointed, { Element: var pointedElem }))
                {
                    return new DotCC.Ir.Index(pointed, idx) { Type = pointedElem, IsLValue = true };
                }
                var elem = baseExpr.Type switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => CType.Int,
                };
                return new DotCC.Ir.Index(baseExpr, idx) { Type = elem, IsLValue = true };
            }

            // Slicing `a[lo..hi]` → a sub-slice fat pointer `{ a.ptr + lo, hi - lo }`. The base
            // may be a slice (re-slice through `.Ptr`), a pointer, or an array (decays); the
            // element type + const-ness ride into the resulting `[]T` / `[]const T`.
            case Zig.SliceRange sr:
                return BuildSlice(LowerExpr(sr.Arg0), LowerUsizeOperand(sr.Arg2), LowerUsizeOperand(sr.Arg4));

            // Open-ended slicing `a[lo..]` → the high bound is the source length, so the
            // result is `{ a.ptr + lo, sourceLen - lo }`. Only a known-length source (slice
            // or array) has a length; a bare pointer is rejected (as Zig does).
            case Zig.SliceOpen so:
                return LowerOpenSlice(so.Arg0, so.Arg2);

            // Sentinel-terminated slicing `a[lo .. hi :s]` / `a[lo .. :s]`: the same slice, since a
            // sentinel is erased in the type (as `[:0]T`'s is). zig asserts `a[hi] == s` only in a SAFE
            // build mode, and dotcc reports `.ReleaseFast`, so the check is not emitted.
            case Zig.SliceRangeSentinel srs:
                return BuildSlice(LowerExpr(srs.Arg0), LowerUsizeOperand(srs.Arg2), LowerUsizeOperand(srs.Arg4));
            case Zig.SliceOpenSentinel sos:
                return LowerOpenSlice(sos.Arg0, sos.Arg2);

            // `.?` optional unwrap. A value optional (CType.Optional → C# `T?`) unwraps via
            // `.Value` (panics on none, matching Zig's `.?`-on-null). An optional POINTER is
            // a bare `T*`, so unwrapping is the identity (the non-null pointer is the same
            // value). [V1: the pointer form does not runtime-check for null.]
            case Zig.Unwrap u:
            {
                var operand = LowerExpr(u.Arg0);
                if (operand.Type.Unqualified is CType.Optional opt)
                {
                    return new Member(operand, "Value", false) { Type = opt.Inner };
                }
                return operand;
            }

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }
}
