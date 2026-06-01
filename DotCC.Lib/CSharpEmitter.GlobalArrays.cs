#nullable enable

using System.Collections.Generic;
using System.Globalization;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // ---- file-scope (global) array declarations -------------------------
    //
    // A C file-scope array has static storage duration and decays to a pointer.
    // dotcc lowers each to a `T*` field in DotCcGlobals backed by a pinned,
    // rooted managed array (the `Libc.GlobalArray*` helpers) — the SAME `T*`
    // shape a block-scope `stackalloc` array gets, so the existing subscript /
    // sizeof machinery (which reads the array's CType from _globalArrayInfo via
    // VarCType) works identically for globals. `static`/plain forms lower the
    // same way (internal linkage is a no-op for a never-exported variable);
    // `extern` forms emit no storage (it lives in another TU) and only register
    // the name's type so same-TU references resolve.
    //
    // The visitors are thin: they reuse the same brace/dim/string helpers as the
    // block-scope `Decl` array visitors (ParseDims / FlattenScalarInit / Leaves /
    // DesignatedValues / StructArrayElems / DecodeStrPartsToBytes / BuildCharArr-
    // Values), then route the computed values to a global field instead of a
    // stackalloc local.

    // `T a[dims];` (and `static`) — zeroed.
    public EmitContent Visit(C.GlobalArr n)        => EmitGlobalArrZeroed(T(n.Arg0), T(n.Arg1), A(n.Arg2));
    public EmitContent Visit(C.GlobalStaticArr n)  => EmitGlobalArrZeroed(T(n.Arg1), T(n.Arg2), A(n.Arg3));
    // `T a[];` tentative definition — C completes an un-`extern`'d incomplete
    // file-scope array to one element. Rare, but accept it (gcc does).
    public EmitContent Visit(C.GlobalArrIncomplete n)       => EmitGlobalArrZeroed(T(n.Arg0), T(n.Arg1), OneElem);
    public EmitContent Visit(C.GlobalStaticArrIncomplete n) => EmitGlobalArrZeroed(T(n.Arg1), T(n.Arg2), OneElem);

    // `T a[dims] = { … }` (and `static`).
    public EmitContent Visit(C.GlobalArrInit n)       => EmitGlobalArrInit(T(n.Arg0), T(n.Arg1), A(n.Arg2), Group(n.Arg5));
    public EmitContent Visit(C.GlobalStaticArrInit n) => EmitGlobalArrInit(T(n.Arg1), T(n.Arg2), A(n.Arg3), Group(n.Arg6));
    // `T a[] = { … }` (implicit size — no ArrDims) (and `static`).
    public EmitContent Visit(C.GlobalArrInitImplicit n)       => EmitGlobalArrInit(T(n.Arg0), T(n.Arg1), NoDims, Group(n.Arg6));
    public EmitContent Visit(C.GlobalStaticArrInitImplicit n) => EmitGlobalArrInit(T(n.Arg1), T(n.Arg2), NoDims, Group(n.Arg7));

    // `T a[dims] = "…"` (and `static`).
    public EmitContent Visit(C.GlobalCharArrStrSized n)       => EmitGlobalCharArrStr(T(n.Arg0), T(n.Arg1), n.Arg4, A(n.Arg2));
    public EmitContent Visit(C.GlobalStaticCharArrStrSized n) => EmitGlobalCharArrStr(T(n.Arg1), T(n.Arg2), n.Arg5, A(n.Arg3));
    // `T a[] = "…"` (implicit size) (and `static`).
    public EmitContent Visit(C.GlobalCharArrStr n)       => EmitGlobalCharArrStr(T(n.Arg0), T(n.Arg1), n.Arg5, NoDims);
    public EmitContent Visit(C.GlobalStaticCharArrStr n) => EmitGlobalCharArrStr(T(n.Arg1), T(n.Arg2), n.Arg6, NoDims);

    // `extern T a[dims];` / `extern T a[];` — declaration only, emit nothing.
    public EmitContent Visit(C.ExternArr n)           => EmitExternArr(T(n.Arg1), T(n.Arg2), A(n.Arg3));
    public EmitContent Visit(C.ExternArrIncomplete n) => EmitExternArrIncomplete(T(n.Arg1), T(n.Arg2));

    private static readonly string[] OneElem = { "1" };
    private static readonly string[] NoDims = System.Array.Empty<string>();

    private static EmitContent.InitGroup Group(Item initList) => (EmitContent.InitGroup)initList.Content;

    // Build the nested array CType (outer→inner) from literal dimensions; the
    // innermost element is `elemCs` (the already-lowered C# element type).
    private static CType MakeArrCType(string elemCs, IReadOnlyList<int> sizes)
    {
        CType cty = new CType.Sized(elemCs);
        for (var i = sizes.Count - 1; i >= 0; i--) { cty = new CType.Arr(cty, sizes[i]); }
        return cty;
    }

    // `T a[dims];` → a pinned zeroed global array. All-literal dims give a known
    // total + a full CType for sizeof; a single non-literal extent (`[N+2]` after
    // macro expansion, `[ENUM_CONST]`) is passed straight through as the runtime
    // length (C# folds a constant expression), with the decayed pointer type
    // registered since the exact count isn't known to dotcc. Multi-dim with a
    // non-literal extent can't be flattened — reject clearly.
    private EmitContent EmitGlobalArrZeroed(string elemCs, string name, IReadOnlyList<string> dims)
    {
        var sizes = ParseDims(dims);
        string lengthExpr;
        if (sizes.Count == dims.Count && sizes.Count > 0)
        {
            var total = 1;
            foreach (var s in sizes) { total *= s; }
            _globalArrayInfo[name] = MakeArrCType(elemCs, sizes);
            lengthExpr = total.ToString(CultureInfo.InvariantCulture);
        }
        else if (dims.Count == 1)
        {
            _globalTypes[name] = elemCs + "*";
            lengthExpr = StripOuterParens(dims[0]);
        }
        else
        {
            throw new CompileException($"file-scope array `{name}` needs constant dimensions");
        }
        var elem = QualifyPredefinedTypeName(elemCs);
        _globals.Append("    public static unsafe ").Append(elem).Append("* ").Append(Id(name))
            .Append(" = Libc.GlobalArrayZeroed<").Append(elem).Append(">(").Append(lengthExpr).Append(");\n");
        return string.Empty;
    }

    // `T a[dims] = { … }` / `T a[] = { … }` → a pinned global array initialized
    // from the brace values. Mirrors DeclArrInit's struct / designated / scalar
    // branches; the only difference is the emit target (a global field, flat
    // initializer literal) and that a non-literal sized extent falls back to the
    // initializer's own length (the common Lua case `tab[MACRO] = { …full… }`).
    private EmitContent EmitGlobalArrInit(string elemCs, string name, IReadOnlyList<string> dims, EmitContent.InitGroup group)
    {
        var sizes = ParseDims(dims);
        List<string> values;
        CType cty;
        if (_structFields.ContainsKey(elemCs))
        {
            if (HasDesignators(group))
            {
                throw new CompileException($"array designators on a struct array (`{name}`) aren't supported yet");
            }
            values = StructArrayElems(elemCs, group);
            cty = new CType.Arr(new CType.Sized(elemCs), values.Count);
        }
        else if (HasDesignators(group))
        {
            if (dims.Count > 1)
            {
                throw new CompileException($"array designators on `{name}` are only supported for 1-D arrays");
            }
            values = DesignatedValues(elemCs, group, sizes.Count == 1 ? sizes[0] : -1);
            cty = new CType.Arr(new CType.Sized(elemCs), values.Count);
        }
        else if (sizes.Count > 0 && sizes.Count == dims.Count)
        {
            // All dims literal — flatten with C's per-dimension zero-fill.
            values = FlattenScalarInit(group, sizes);
            cty = MakeArrCType(elemCs, sizes);
        }
        else
        {
            // Implicit `[]` or a non-literal single extent — take the initializer
            // as-is (1-D). C# infers the length; a full initializer (the common
            // case) needs no zero-fill.
            values = Leaves(group);
            cty = new CType.Arr(new CType.Sized(elemCs), values.Count);
        }
        _globalArrayInfo[name] = cty;
        var elem = QualifyPredefinedTypeName(elemCs);
        _globals.Append("    public static unsafe ").Append(elem).Append("* ").Append(Id(name))
            .Append(" = Libc.GlobalArrayFrom<").Append(elem).Append(">(new ").Append(elem)
            .Append("[]{ ").Append(string.Join(", ", values)).Append(" });\n");
        return string.Empty;
    }

    // `T a[dims] = "…"` / `T a[] = "…"` → a pinned global char array. Same byte
    // decode + NUL + zero-pad/truncate as the block-scope char-array decl.
    private EmitContent EmitGlobalCharArrStr(string elemCs, string name, Item strSeq, IReadOnlyList<string> dims)
    {
        var bytes = DecodeStrPartsToBytes(strSeq);
        var total = bytes.Count + 1;  // bytes + NUL
        var sizes = ParseDims(dims);
        if (sizes.Count > 0) { total = 1; foreach (var s in sizes) { total *= s; } }  // explicit: pad to N
        var values = BuildCharArrValues(bytes, total);
        _globalArrayInfo[name] = new CType.Arr(new CType.Sized(elemCs), values.Count);
        var elem = QualifyPredefinedTypeName(elemCs);
        _globals.Append("    public static unsafe ").Append(elem).Append("* ").Append(Id(name))
            .Append(" = Libc.GlobalArrayFrom<").Append(elem).Append(">(new ").Append(elem)
            .Append("[]{ ").Append(string.Join(", ", values)).Append(" });\n");
        return string.Empty;
    }

    // `extern T a[dims];` — declaration only (storage in another TU): emit no
    // field, just register the type so same-TU references resolve. A literal
    // sized extent registers full array info (enables sizeof); otherwise the
    // decayed pointer type.
    private EmitContent EmitExternArr(string elemCs, string name, IReadOnlyList<string> dims)
    {
        var sizes = ParseDims(dims);
        if (sizes.Count > 0 && sizes.Count == dims.Count) { _globalArrayInfo[name] = MakeArrCType(elemCs, sizes); }
        else { _globalTypes[name] = elemCs + "*"; }
        return string.Empty;
    }

    // `extern T a[];` — incomplete extern array: unknown size, decays to a
    // pointer for any same-TU reference (sizeof of an incomplete array is
    // illegal in C anyway).
    private EmitContent EmitExternArrIncomplete(string elemCs, string name)
    {
        _globalTypes[name] = elemCs + "*";
        return string.Empty;
    }
}
