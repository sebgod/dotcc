#nullable enable

using System.Collections.Generic;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // ---- compile-time struct/union layout model ---------------------------
    // A few C positions need `offsetof(T, member)` as an integer CONSTANT, not a
    // runtime value — most notably an array-member bound that lowers to a C#
    // `fixed[N]` / `[InlineArray(N)]`. Lua's ltable.c builds an alignment union
    // around exactly that idiom:
    //
    //     typedef struct { Node *dummy; Node follows_pNode; } Limbox_aux;
    //     typedef union {
    //       Node *lastfree;
    //       char padding[offsetof(Limbox_aux, follows_pNode)];
    //     } Limbox;
    //
    // so the union is sized to hold `lastfree` AND to align a `Node` that follows
    // it in the same malloc'd block.
    //
    // dotcc models the layout with the standard C ABI rules — which a .NET
    // blittable struct (`LayoutKind.Sequential`, natural alignment, no `Pack`) /
    // union (`LayoutKind.Explicit`, every member at offset 0) ALSO follow on the
    // same platform — so the folded constant agrees with what C# `sizeof`/.NET
    // would compute at runtime. The model is deliberately CONSERVATIVE: it folds
    // only when every member of the transitive type graph is precisely
    // modellable (primitives, pointers, nested known aggregates, 1-D arrays).
    // Anything it can't size exactly (a bit-field, an unknown type) makes the
    // whole computation return null, and `offsetof` falls back to its runtime
    // helper. The 64-bit data model matches dotcc's <stdint.h> (pointer/long = 8).

    // Aggregate type names that are UNIONS (overlapping storage), as opposed to
    // sequential structs. Populated by EmitExplicitUnionType / the typedef-union
    // forms. The layout walk uses max-of-members for a union, running-offset for a
    // struct. Emitter-lifetime (types are file-scope, shared across TUs).
    private readonly HashSet<string> _unionTypes = new(System.StringComparer.Ordinal);

    // Typedef names that alias a C# function pointer (`delegate*<…>`), recorded by
    // the function-pointer-typedef visitors. A function pointer is a pointer, so it
    // sizes to 8/8 — but the alias text ends in `>` not `*`, so the pointer-suffix
    // check can't catch it. Lua's `lua_CFunction` (a member of `union Value`) needs
    // this for the Node/Limbox graph to size.
    private readonly HashSet<string> _pointerTypedefNames = new(System.StringComparer.Ordinal);

    // Cycle guard: a self-referential typedef alias would otherwise recurse forever
    // (a struct can't contain itself by value — illegal C — but an alias loop is
    // possible). Names currently on the layout stack; a re-entry returns null.
    private readonly HashSet<string> _layoutVisiting = new(System.StringComparer.Ordinal);

    private static int RoundUp(int x, int align) => align <= 1 ? x : (x + align - 1) / align * align;

    // Byte offset of `member` within aggregate `typeName`, or null when the layout
    // isn't modellable (so the caller keeps offsetof's runtime form). A union member
    // is always at offset 0; a struct member is at its alignment-padded running
    // offset.
    private int? TryOffsetOf(string typeName, string member)
    {
        var name = ResolveTypedef(typeName);
        if (!_structFields.TryGetValue(name, out var fields)) { return null; }
        if (_unionTypes.Contains(name)) { return fields.Contains(member) ? 0 : (int?)null; }
        var offset = 0;
        foreach (var f in fields)
        {
            if (!TryMemberSizeAlign(name, f, out var fsize, out var falign)) { return null; }
            offset = RoundUp(offset, falign);
            if (f == member) { return offset; }
            offset += fsize;
        }
        return null;  // member not found (shouldn't happen for valid offsetof)
    }

    // (size, align) of aggregate `name`. Struct: running offset with per-member
    // padding, size rounded up to the max member alignment. Union: max member size,
    // rounded up to the max member alignment. Null if any member is unmodellable.
    private bool TryAggregateLayout(string name, out int size, out int align)
    {
        size = 0; align = 1;
        if (!_structFields.TryGetValue(name, out var fields)) { return false; }
        if (!_layoutVisiting.Add(name)) { return false; }  // cycle
        try
        {
            var isUnion = _unionTypes.Contains(name);
            var offset = 0; var maxSize = 0; var maxAlign = 1;
            foreach (var f in fields)
            {
                if (!TryMemberSizeAlign(name, f, out var fsize, out var falign)) { return false; }
                if (falign > maxAlign) { maxAlign = falign; }
                if (isUnion) { if (fsize > maxSize) { maxSize = fsize; } }
                else { offset = RoundUp(offset, falign) + fsize; }
            }
            align = maxAlign;
            size = RoundUp(isUnion ? maxSize : offset, maxAlign);
            // A complete C aggregate has size >= 1 (no empty struct in standard C).
            if (size == 0) { size = RoundUp(1, maxAlign); }
            return true;
        }
        finally { _layoutVisiting.Remove(name); }
    }

    // (size, align) of one member of `structName`. Array members (a C# `fixed`
    // buffer / [InlineArray] / multi-dim) size as count*element; everything else
    // uses the recorded field CType. Null if the member type can't be sized (a
    // bit-field has no _structFieldTypes entry, so it lands here as null → the
    // whole aggregate bails, which is correct since dotcc's bit-field layout
    // doesn't match C anyway).
    private bool TryMemberSizeAlign(string structName, string field, out int size, out int align)
    {
        size = 0; align = 1;
        // 1-D fixed buffer (primitive array member) → count*sizeof(elem), align elem.
        if (_structFieldTypes.TryGetValue(structName, out var ftypes)
            && ftypes.TryGetValue(field, out var arrCt) && arrCt is CType.Arr)
        {
            return TryLayoutOfCType(arrCt, out size, out align);
        }
        // [InlineArray]-backed member (non-primitive element array).
        if (_structInlineArrFields.TryGetValue(structName, out var inl)
            && inl.TryGetValue(field, out var ia))
        {
            if (!TrySizeAlignOfCsType(ia.Elem, out var es, out var ea)) { return false; }
            size = es * ia.Count; align = ea; return true;
        }
        // Scalar / pointer / nested-aggregate field.
        if (ftypes is not null && ftypes.TryGetValue(field, out var ct))
        {
            return TryLayoutOfCType(ct, out size, out align);
        }
        return false;
    }

    // (size, align) of a CType. An array is count*element (align = element's).
    private bool TryLayoutOfCType(CType t, out int size, out int align)
    {
        size = 0; align = 1;
        switch (t)
        {
            case CType.Arr a:
                if (!TryLayoutOfCType(a.Element, out var es, out var ea)) { return false; }
                size = es * a.Count; align = ea; return true;
            case CType.InlineArr ia:
                if (!TryLayoutOfCType(ia.Element, out var is2, out var ia2)) { return false; }
                size = is2 * ia.Count; align = ia2; return true;
            case CType.PtrToArr:
                size = 8; align = 8; return true;  // a pointer
            case CType.Sized s:
                return TrySizeAlignOfCsType(s.CsType, out size, out align);
            default:
                return false;
        }
    }

    // (size, align) of a C# type name as it appears in a field. Resolves a scalar
    // typedef chain, treats anything pointer-shaped (a `*` suffix or a
    // function-pointer typedef) as 8/8, sizes the primitive keywords from a table,
    // and otherwise recurses into a known aggregate. Null for an unknown type.
    private bool TrySizeAlignOfCsType(string csType, out int size, out int align)
    {
        size = 0; align = 1;
        var cs = csType.Trim();
        if (cs.EndsWith("*", System.StringComparison.Ordinal) || _pointerTypedefNames.Contains(cs))
        {
            size = 8; align = 8; return true;
        }
        var resolved = ResolveTypedef(cs);
        if (resolved.EndsWith("*", System.StringComparison.Ordinal) || _pointerTypedefNames.Contains(resolved))
        {
            size = 8; align = 8; return true;
        }
        switch (resolved)
        {
            case "byte": case "sbyte": case "bool": size = 1; align = 1; return true;
            case "short": case "ushort": size = 2; align = 2; return true;
            case "int": case "uint": case "float": size = 4; align = 4; return true;
            case "long": case "ulong": case "double": case "nint": case "nuint":
                size = 8; align = 8; return true;
        }
        if (_structFields.ContainsKey(resolved)) { return TryAggregateLayout(resolved, out size, out align); }
        if (resolved != cs && _structFields.ContainsKey(cs)) { return TryAggregateLayout(cs, out size, out align); }
        return false;
    }
}
