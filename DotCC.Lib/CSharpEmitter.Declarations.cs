#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // Declarations
    // `Type DeclItemList` — covers single (`int x;`), single-with-init
    // (`int x = 5;`), and multi-declarator (`int x, y, z;`,
    // `int x = 1, y = 2;`) forms. C# accepts the same `int x, y = 5, z;`
    // syntax so the lowering is verbatim.
    public EmitContent Visit(C.Decl n) => EmitDecl(T(n.Arg0), DE(n.Arg1));

    // Pre-C23 `auto` storage class — `auto int x = i;`. `auto` means "automatic
    // storage duration", which is already the default for a block-scope local,
    // so it's redundant: drop it and emit the declaration exactly as if the
    // `auto` weren't there. (C89, no dialect gate.)
    public EmitContent Visit(C.DeclAutoStorage n) => EmitDecl(T(n.Arg1), DE(n.Arg2));

    // C23 `auto` type inference — `auto x = E;` deduces x's type from the
    // initializer, exactly like C# `var` (and C++ `auto`, and gcc's older
    // `__auto_type`). So lower straight to `var x = E;` and let Roslyn infer.
    // Requires an initializer and no other type specifier (the grammar enforces
    // both). Gated as C23 under -pedantic; the `auto` keyword itself is C89.
    public EmitContent Visit(C.DeclAutoInfer n)
    {
        Gate(2023, "`auto` type inference", n.Arg0);  // C23
        var name = T(n.Arg1);
        var init = T(n.Arg3);
        NoteLocal(name);
        // Record the inferred type when the initializer's CType is a plain
        // scalar, so `sizeof(x)` and enum coercion still resolve; arrays/unknown
        // just leave it untracked (C# `var` still compiles).
        if (_currentFunctionName is not null && TyOf(n.Arg3) is CType.Sized sz) { _localTypes[name] = sz.CsType; }
        return $"var {Id(DeclareLocal(name))} = {init}";
    }

    private EmitContent EmitDecl(string type, IReadOnlyList<EmitContent.DeclEntry> entries)
    {
        // Per-declarator type. The FIRST declarator uses `type` verbatim (its
        // `*`s were absorbed into Type by the greedy `Type → Type *` rule); a
        // SUBSEQUENT declarator with its own `*`s uses stripStars(type) + that
        // many `*`s. So `int *a, *b;` gives both `int*`, `int *a, b;` gives
        // `int*` then `int`.
        var baseTy = StripTrailingStars(type);
        string Eff(int i) => i == 0 ? type : baseTy + new string('*', entries[i].Stars);

        // Register name + (effective) declared type for shadow resolution and
        // enum typing, and assign each declarator its (possibly renamed) C#
        // identifier. Raw names stay in `entries`; `renamed` maps raw → emitted.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            NoteLocal(e.Name);
            if (_currentFunctionName is not null) { _localTypes[e.Name] = Eff(i); }
            renamed[e.Name] = DeclareLocal(e.Name);
        }

        // malloc → stack-value peephole: only single-declarator
        // `S* p = (S*)malloc(sizeof(S));` is eligible. Record whether the
        // declared type matched `S*` (needed by the analysis pass) and, in the
        // emit pass, swap the heap allocation for a stack value when the
        // variable was found promotable.
        if (entries.Count == 1 && entries[0].MallocStructType is string structType
            && _currentFunctionName is string fn)
        {
            var typeMatches = type.Replace(" ", "") == structType + "*";
            if (_fnMalloc.TryGetValue(entries[0].Name, out var mv)) { mv.TypeMatches = typeMatches; }
            if (typeMatches && _promotableIn.Contains((fn, entries[0].Name)))
            {
                // `new S()` value-initialises (zero) the struct on the stack —
                // no native heap alloc, matching free dropped. Reads happen
                // after explicit field writes in any promotable function (the
                // usage analysis guarantees `->`-only access), so zero-init vs
                // C's uninitialised malloc is unobservable.
                return $"{structType} {Id(renamed[entries[0].Name])} = new {structType}()";
            }
        }

        // Uniform list (every declarator the same type — all single-declarator
        // decls, and `int *a, *b;`): emit one C# multi-declarator. C# binds `*`
        // to the type, so `T* a, b;` makes both `T*`, matching C when uniform.
        var uniform = true;
        for (var i = 1; i < entries.Count; i++) { if (Eff(i) != type) { uniform = false; break; } }
        if (uniform)
        {
            // Reconcile each initializer's enum-ness with the declared type
            // (`Color c = 2` → `(Color)(2)`; `int x = c` → `(int)(c)`), then swap
            // in the (possibly renamed) declarator name for emission.
            var joined = entries.Select(e => e with { Init = ReconcileEnumInit(type, e), Name = renamed[e.Name] }).ToList();
            return $"{type} {DeclEntriesToBlockScopeString(joined)}";
        }

        // Non-uniform (`int *a, b;`): C binds `*` per-declarator but C# binds it
        // to the type, so emit SEPARATE declarations, each with its own type.
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) { sb.Append("; "); }
            var et = Eff(i);
            sb.Append(et).Append(' ').Append(Id(renamed[entries[i].Name]))
              .Append(" = ").Append(ReconcileEnumInit(et, entries[i]) ?? "default");
        }
        return sb.ToString();
    }

    // Strip trailing pointer stars (and any whitespace) from a lowered type
    // string: `int*` → `int`, `char **` → `char`. The base for a multi-declarator
    // list's subsequent declarators, which add their own `*`s.
    private static string StripTrailingStars(string type)
    {
        var t = type.TrimEnd();
        while (t.EndsWith("*", StringComparison.Ordinal)) { t = t[..^1].TrimEnd(); }
        return t;
    }

    // Reconcile a declared type with an initializer's enum-ness, inserting the
    // int↔enum cast C# requires when they disagree (C lets enums and ints flow
    // freely; C# doesn't). Returns the maybe-wrapped init; null passes through.
    private string? ReconcileEnumInit(string declType, EmitContent.DeclEntry e)
    {
        if (e.Init is not { } init) { return null; }
        // void* → T* : implicit in C, explicit in C#. A cast-less `T* p =
        // malloc(...)` (or any void*-returning initializer) lands here; insert
        // the `(T*)` the C# compiler requires. Skipped for `void* p = …` itself.
        if (e.InitIsVoidPtr && declType.EndsWith("*", StringComparison.Ordinal) && declType != "void*")
        {
            return $"({declType})({init})";
        }
        if (_enumTags.Contains(declType))
        {
            // Enum-typed slot: cast a non-matching source (int→enum, or a
            // different enum). A source already of this enum needs nothing.
            return e.InitEnumType == declType ? init : $"({declType})({init})";
        }
        // Non-enum slot fed an enum value: decay to int (int→numeric is implicit).
        return e.InitEnumType is null ? init : $"(int)({init})";
    }

    // DeclItemList: structured accumulator of (name, init?) pairs.
    // Returning a typed DeclEntries instead of a pre-joined string lets
    // the file-scope GlobalDeclList visitor apply per-entry policy
    // (e.g. auto-init LongJmpToken with `new T()` for the no-init form)
    // while the block-scope Decl visitor still gets the same C# multi-
    // declarator join shape via DeclEntriesToBlockScopeString.
    public EmitContent Visit(C.DeclItemListOne n)  => (EmitContent.DeclEntries)n.Arg0.Content;
    public EmitContent Visit(C.DeclItemListCons n)
    {
        var prev = DE(n.Arg0);
        var next = DE(n.Arg2);
        var combined = new List<EmitContent.DeclEntry>(prev.Count + next.Count);
        combined.AddRange(prev);
        combined.AddRange(next);
        return new EmitContent.DeclEntries(combined);
    }

    // DeclItem: a single declarator. Carries the (name, init?) pair
    // upward via DeclEntries so the consuming Decl/GlobalDeclList can
    // decide how to materialise it. The plain `int x;` form has Init=null;
    // the consumer fills in `= default` (block scope) or omits the
    // initializer (file scope, value types — C# zero-inits class fields).
    public EmitContent Visit(C.DeclItem n) =>
        new EmitContent.DeclEntries(new[] { new EmitContent.DeclEntry(T(n.Arg0), null) });
    public EmitContent Visit(C.DeclItemInit n)
    {
        var name = T(n.Arg0);
        // `S* p = (S*)malloc(sizeof(S))` — the init reduced to a MallocSizeof
        // marker. Register the variable as a stack-promotion candidate for
        // this function and remember the struct type on the entry so Visit(Decl)
        // can emit the promoted form. T() renders the marker's low-level text
        // for the (default) non-promoted path.
        string? mallocType = null;
        if (n.Arg2.Content is EmitContent.MallocSizeof ms && _currentFunctionName is not null)
        {
            mallocType = ms.StructType;
            if (!_fnMalloc.ContainsKey(name)) { _fnMalloc[name] = new MallocVar { StructType = ms.StructType }; }
        }
        // A `void*` initializer (malloc/calloc/… or a bare `malloc(sizeof(S))`
        // marker without an explicit cast) needs a `(T*)` cast in C# — implicit
        // in C, explicit in C#. EmitDecl inserts it when the declared type is a
        // pointer. (A MallocSizeof that's AlreadyCast carries its own cast.)
        var initVoidPtr = (n.Arg2.Content is EmitContent.MallocSizeof m && !m.AlreadyCast)
            || TyOf(n.Arg2) is CType.Sized { CsType: "void*" };
        return new EmitContent.DeclEntries(new[] { new EmitContent.DeclEntry(name, T(n.Arg2), mallocType, EnumOf(n.Arg2), initVoidPtr) });
    }

    // DeclItemTail — a non-first (post-comma) init-declarator. Plain → the inner
    // DeclItem's entry unchanged (0 extra stars). Pointer → take the inner tail's
    // single entry and add one pointer level (`int *a, *b;` → `b` gets Stars=1).
    public EmitContent Visit(C.DeclItemTailPlain n) => (EmitContent.DeclEntries)n.Arg0.Content;
    public EmitContent Visit(C.DeclItemTailPtr n)
    {
        var inner = DE(n.Arg1);
        var e = inner[0];
        return new EmitContent.DeclEntries(new[] { e with { Stars = e.Stars + 1 } });
    }

    // Join structured DeclEntries into the block-scope C# multi-declarator
    // form: `name = default, name = expr, ...`. Block scope can't omit the
    // initializer on locals (C# enforces definite-assignment for struct
    // fields used after declaration), so we fill no-init entries with
    // `= default` — zero-initialized for all our types (0 / null / empty
    // struct), which matches the observable behavior of well-written C.
    private static string DeclEntriesToBlockScopeString(IReadOnlyList<EmitContent.DeclEntry> entries)
    {
        return string.Join(", ", entries.Select(e => $"{Id(e.Name)} = {e.Init ?? "default"}"));
    }
    // C `T arr[D1][D2]…` → C# `T* arr = stackalloc T[D1*D2*…]`. C# stackalloc is
    // 1-D, so a multi-dimensional array is FLATTENED to a single contiguous
    // block (matching C's row-major layout); the subscript visitor rewrites
    // `a[i][j]` to flat pointer arithmetic. stackalloc keeps the array in the
    // same lifetime as locals (no heap, no GC pin), matching C automatics.
    // ArrDims (Arg2) carries the dimension expression texts, outer→inner.
    public EmitContent Visit(C.DeclArr n) => EmitArrDecl(T(n.Arg0), T(n.Arg1), A(n.Arg2));

    // `T name[dims]` with no initializer (or an empty `= {}` one) → a zeroed
    // `stackalloc`. C# zero-initialises stackalloc by default, so this is also
    // exactly the lowering for the C23 empty-array-initializer form.
    private EmitContent EmitArrDecl(string elem, string name, IReadOnlyList<string> dims)
    {
        NoteLocal(name);
        var emitted = DeclareLocal(name);

        // Try every dimension as a literal int.
        var sizes = new List<int>(dims.Count);
        foreach (var d in dims)
        {
            if (int.TryParse(d, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) { sizes.Add(v); }
            else { sizes.Clear(); break; }
        }

        if (sizes.Count == 0)
        {
            // A non-literal extent. 1-D runtime size (VLA-ish) lowers directly
            // to `stackalloc T[expr]`; multi-dim with a non-constant dimension
            // can't be flattened at compile time — reject clearly.
            if (dims.Count != 1)
            {
                throw new CompileException(
                    $"multi-dimensional array `{name}` needs constant dimensions");
            }
            return $"{elem}* {Id(emitted)} = stackalloc {elem}[{StripOuterParens(dims[0])}]";
        }

        // Build the nested CType (outer→inner) + total element count, record it
        // (keyed by RAW name), and flatten the allocation.
        CType cty = new CType.Sized(elem);
        var total = 1;
        for (var i = sizes.Count - 1; i >= 0; i--) { cty = new CType.Arr(cty, sizes[i]); total *= sizes[i]; }
        NoteLocalArray(name, cty);
        return $"{elem}* {Id(emitted)} = stackalloc {elem}[{total}]";
    }

    // `T x = {};` (C23 empty initializer) — zero-init a scalar/struct/pointer.
    // Routed through EmitDecl as a `default` initializer so it picks up scope
    // renaming, type tracking, and enum reconciliation for free.
    public EmitContent Visit(C.DeclEmptyInit n)
    {
        Gate(2023, "empty initializer", n.Arg0);  // C23
        return EmitDecl(T(n.Arg0), new[] { new EmitContent.DeclEntry(T(n.Arg1), "default") });
    }

    // `T a[N] = {};` (C23 empty initializer) — a zeroed array. Identical to the
    // no-initializer array decl (C# zeroes stackalloc), so share EmitArrDecl.
    public EmitContent Visit(C.DeclArrEmptyInit n)
    {
        Gate(2023, "empty initializer", n.Arg0);  // C23
        return EmitArrDecl(T(n.Arg0), T(n.Arg1), A(n.Arg2));
    }

    // C `T arr[N] = {1, 2, 3}` (or `T arr[] = {…}`) → C# `T* arr = stackalloc T[]{ 1, 2, 3 }`.
    // The explicit-size form ignores the size operand because C# infers it
    // from the initializer; both shapes share the same emit. ArgList arrives
    // as a typed EmitContent.Args (read via A()) — no sentinel decoding. The
    // element count for sizeof is the initializer length.
    // `T arr[dims] = { … }` (sized, 1-D or multi-dim). Arg2 = ArrDims,
    // Arg5 = InitList (an InitGroup tree). A struct element → an array of
    // `new T{…}`; a scalar element → flattened (with C's per-dim zero-fill).
    public EmitContent Visit(C.DeclArrInit n)
    {
        var elem = T(n.Arg0);
        var name = T(n.Arg1);
        var dims = A(n.Arg2);
        var group = (EmitContent.InitGroup)n.Arg5.Content;
        NoteLocal(name);
        var emitted = DeclareLocal(name);
        var sizes = ParseDims(dims);
        if (HasDesignators(group))
        {
            return EmitDesignatedArray(elem, emitted, name, group, sizes.Count == 1 ? sizes[0] : -1, sizes.Count);
        }
        if (_structFields.ContainsKey(elem))
        {
            NoteLocalArray(name, new CType.Arr(new CType.Sized(elem), sizes.Count == 1 ? sizes[0] : group.Items.Count));
            return EmitStructArrayInit(elem, emitted, group);
        }
        if (sizes.Count == 0)
        {
            throw new CompileException($"array `{name}` with a brace initializer needs constant dimensions");
        }
        var flat = FlattenScalarInit(group, sizes);
        CType cty = new CType.Sized(elem);
        for (var i = sizes.Count - 1; i >= 0; i--) { cty = new CType.Arr(cty, sizes[i]); }
        NoteLocalArray(name, cty);
        return $"{elem}* {Id(emitted)} = stackalloc {elem}[]{{ {string.Join(", ", flat)} }}";
    }
    // `T arr[] = { … }` (implicit 1-D size, derived from the initializer).
    public EmitContent Visit(C.DeclArrInitImplicit n)
    {
        var elem = T(n.Arg0);
        var name = T(n.Arg1);
        var group = (EmitContent.InitGroup)n.Arg6.Content;
        NoteLocal(name);
        var emitted = DeclareLocal(name);
        if (HasDesignators(group))
        {
            return EmitDesignatedArray(elem, emitted, name, group, -1, 1);  // implicit: size from max index
        }
        if (_structFields.ContainsKey(elem))
        {
            NoteLocalArray(name, new CType.Arr(new CType.Sized(elem), group.Items.Count));
            return EmitStructArrayInit(elem, emitted, group);
        }
        var vals = Leaves(group);  // implicit-size scalar array is 1-D (flat leaves)
        NoteLocalArray(name, new CType.Arr(new CType.Sized(elem), vals.Count));
        return $"{elem}* {Id(emitted)} = stackalloc {elem}[]{{ {string.Join(", ", vals)} }}";
    }

    // `char s[] = "…"` — char array from a string literal (C89). Decode the
    // string to its bytes and `stackalloc` a MUTABLE copy with a trailing NUL
    // (a bare string literal is a pinned read-only RVA; `char[]` is writable).
    // Byte values (incl. > 0x7F) are fine here — it's a byte array, not a u8
    // literal, so no UTF-8 re-encoding limitation.
    public EmitContent Visit(C.DeclCharArrStr n)  // Type ID [ ] = StringSeq
    {
        var elem = T(n.Arg0);
        var name = T(n.Arg1);
        var bytes = DecodeStrPartsToBytes(n.Arg5);
        NoteLocal(name);
        NoteLocalArray(name, new CType.Arr(new CType.Sized(elem), bytes.Count + 1));
        return EmitCharArr(elem, DeclareLocal(name), bytes, bytes.Count + 1);
    }
    public EmitContent Visit(C.DeclCharArrStrSized n)  // Type ID ArrDims = StringSeq
    {
        var elem = T(n.Arg0);
        var name = T(n.Arg1);
        var bytes = DecodeStrPartsToBytes(n.Arg4);
        NoteLocal(name);
        var sizes = ParseDims(A(n.Arg2));
        var total = bytes.Count + 1;
        if (sizes.Count > 0) { total = 1; foreach (var s in sizes) { total *= s; } }  // explicit: zero-pad to N
        NoteLocalArray(name, new CType.Arr(new CType.Sized(elem), total));
        return EmitCharArr(elem, DeclareLocal(name), bytes, total);
    }

    // Emit `T* name = stackalloc T[]{ b0, b1, …, 0 [, padding 0s] }` for a char
    // array of `total` elements: the decoded string bytes, a NUL terminator,
    // then zero-padding up to `total` (C zero-fills the unspecified tail). If
    // the string + NUL already exceeds `total`, C drops the NUL — emit just the
    // first `total` bytes (no terminator), matching C.
    private static string EmitCharArr(string elem, string name, List<int> bytes, int total) =>
        $"{elem}* {Id(name)} = stackalloc {elem}[]{{ {string.Join(", ", BuildCharArrValues(bytes, total))} }}";

    // Build the element list for a char array of `total` slots: the decoded
    // string bytes, a NUL terminator, then zero-padding up to `total` (C
    // zero-fills the unspecified tail). If string + NUL already exceeds `total`,
    // C drops the NUL — keep just the first `total` bytes. Shared by the
    // block-scope (stackalloc) and file-scope (GlobalArrayFrom) char-array paths.
    private static List<int> BuildCharArrValues(List<int> bytes, int total)
    {
        var values = new List<int>(System.Math.Max(total, bytes.Count + 1));
        values.AddRange(bytes);
        values.Add(0);                                   // NUL terminator
        while (values.Count < total) { values.Add(0); }  // zero-pad to N
        if (values.Count > total) { values = values.GetRange(0, total); }  // truncate (no NUL)
        return values;
    }

    // Decode a StringSeq's segments to a flat byte list (escapes decoded; source
    // chars UTF-8-encoded). Unlike the u8-literal path, high bytes are fine here
    // — the destination is a byte array.
    private static List<int> DecodeStrPartsToBytes(Item strSeq)
    {
        var parts = (EmitContent.StrParts)strSeq.Content;
        var items = new List<StrItem>();
        foreach (var body in parts.Bodies) { DecodeCStringBody(body, items); }
        var bytes = new List<int>(items.Count);
        foreach (var it in items)
        {
            if (it.IsByte) { bytes.Add(it.Value & 0xFF); }
            else
            {
                var ch = (char)it.Value;
                if (ch < 0x80) { bytes.Add(ch); }
                else { foreach (var b in System.Text.Encoding.UTF8.GetBytes(ch.ToString())) { bytes.Add(b); } }
            }
        }
        return bytes;
    }

    // Function-pointer local declarator: `int (*fp)(int) [= E]` → C#
    // `delegate*<int, int> fp [= …]`. C# puts the return type LAST in the type
    // arg list (opposite of C). The fn-ptr TYPE's parameters reduce through the
    // Param visitors and stage into _pendingParams — discard them (only an
    // FnSig's StartFn should adopt staged params; see TypedefFnPtr). A bare
    // function-name initializer (`= f`, which C decays to a pointer) is given
    // the `&` C# requires; an explicit `&f` passes through.
    public EmitContent Visit(C.DeclFnPtr n)            => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), T(n.Arg6), null);
    public EmitContent Visit(C.DeclFnPtrNoArgs n)      => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), "",        null);
    public EmitContent Visit(C.DeclFnPtrInit n)        => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), T(n.Arg6), n.Arg9);
    public EmitContent Visit(C.DeclFnPtrNoArgsInit n)  => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), "",        n.Arg8);

    // Pointer-to-array declarator: `int (*p)[3] [= E]` → C# `int* p [= …]`. p is
    // a flat pointer that subscripts with the array's stride (CType.PtrToArr),
    // reusing the multi-dim subscript machinery; its `sizeof` is the pointer
    // size. The init (commonly a 2-D array variable, itself a flat `int*`) lowers
    // verbatim. Arg5 is the bracket dims (ArrDims), Arg7 the optional init.
    public EmitContent Visit(C.DeclPtrToArr n)     => EmitPtrToArr(T(n.Arg0), T(n.Arg3), A(n.Arg5), null);
    public EmitContent Visit(C.DeclPtrToArrInit n) => EmitPtrToArr(T(n.Arg0), T(n.Arg3), A(n.Arg5), n.Arg7);

    private EmitContent EmitPtrToArr(string elem, string name, IReadOnlyList<string> dims, Item? init)
    {
        NoteLocal(name);
        // Build the pointed-to array CType from the (literal) bracket dims and
        // record `p` as a pointer-to-that, so subscripting strides correctly.
        var sizes = new List<int>(dims.Count);
        foreach (var d in dims)
        {
            if (int.TryParse(d, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) { sizes.Add(v); }
            else { sizes.Clear(); break; }
        }
        if (sizes.Count == 0)
        {
            throw new CompileException(
                $"pointer-to-array `{name}` needs constant array dimensions");
        }
        CType inner = new CType.Sized(elem);
        for (var i = sizes.Count - 1; i >= 0; i--) { inner = new CType.Arr(inner, sizes[i]); }
        NoteLocalArray(name, new CType.PtrToArr(inner));
        var emitted = DeclareLocal(name);
        var initText = init is null ? "default" : DecayFnName(T(init));
        return $"{elem}* {Id(emitted)} = {initText}";
    }

    private EmitContent EmitFnPtrLocal(string ret, string name, string pars, Item? init)
    {
        _pendingParams.Clear();
        NoteLocal(name);
        var type = pars.Length == 0
            ? $"delegate*<{ret}>"
            : $"delegate*<{StripParamNames(pars)}, {ret}>";
        if (_currentFunctionName is not null) { _localTypes[name] = type; }
        var emitted = DeclareLocal(name);
        if (init is null) { return $"{type} {Id(emitted)} = default"; }
        var initText = T(init);
        // `= f` (C decays a function to its address) needs `&f` in C#.
        if (_fnReturnTypes.ContainsKey(Unescape(initText))) { initText = "&" + initText; }
        return $"{type} {Id(emitted)} = {initText}";
    }

    // ArrDims — the `[D1][D2]…` dimension list of a multi-dimensional array
    // declarator. Produces the dimension expression texts (outer→inner) as an
    // Args list for Visit(C.DeclArr) to flatten. `[` E `]`: E is Arg1 (one) /
    // Arg2 (cons).
    public EmitContent Visit(C.ArrDimsOne n) =>
        new EmitContent.Args(new[] { StripOuterParens(T(n.Arg1)) });
    public EmitContent Visit(C.ArrDimsCons n)
    {
        var prev = A(n.Arg0);
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(StripOuterParens(T(n.Arg2)));
        return new EmitContent.Args(combined);
    }

    // Record a block-scope array's full CType for sizeof / multi-dim subscript
    // rewriting. No-op at file scope (globals handled in EmitGlobalFields; not
    // yet array-aware).
    private void NoteLocalArray(string rawName, CType arrayType)
    {
        if (_currentFunctionName is not null) { _localArrayInfo[rawName] = arrayType; }
    }

    // `Point p = { .x = 1, .y = 2 };` — designated initializer (C99). The
    // user named the fields directly so we don't need _structFields here:
    // the MemberInitList already emits `field = value` pairs in the right
    // shape for C#'s object-initializer syntax. Order of fields can differ
    // from declaration order (C99 allows it; C# does too).
    public EmitContent Visit(C.DeclStructDesignated n)
    {
        Gate(1999, "designated initializers", n.Arg0);  // C99
        var type = T(n.Arg0);
        var name = T(n.Arg1);
        NoteLocal(name);
        var members = IM(n.Arg4);  // typed InitMembers list
        return $"{type} {Id(DeclareLocal(name))} = new {type} {{ {string.Join(", ", members)} }}";
    }

    // MemberInitListOne / MemberInitListCons accumulate `.field = expr`
    // items as a typed EmitContent.InitMembers. MemberInit emits the
    // individual `field = expr` snippet (plain text, joined at the
    // DeclStructDesignated consumer).
    public EmitContent Visit(C.MemberInitListOne n) =>
        new EmitContent.InitMembers(new[] { T(n.Arg0) });
    public EmitContent Visit(C.MemberInitListCons n)
    {
        var prev = IM(n.Arg0);
        var next = T(n.Arg2);
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(next);
        return new EmitContent.InitMembers(combined);
    }
    // Trailing comma in a designated initializer — the list is unchanged.
    public EmitContent Visit(C.MemberInitListTrail n) => (EmitContent.InitMembers)n.Arg0.Content;
    public EmitContent Visit(C.MemberInit n) => $"{Id(T(n.Arg1))} = {T(n.Arg3)}";

    // InitList / InitElem — a brace initializer as a recursive InitNode tree so
    // nested aggregate initializers (`{{1,2},{3,4}}`) keep their structure. An
    // element is a scalar (InitLeaf) or a nested group (InitGroup); the whole
    // list reduces to one InitGroup.
    public EmitContent Visit(C.InitElemExpr n) => new EmitContent.InitLeaf(T(n.Arg0));
    public EmitContent Visit(C.InitElemNest n) => (EmitContent.InitGroup)n.Arg1.Content;
    // `[index] = value` array designator (C99). Index/value captured as text;
    // the decl visitor places the value at the (constant) index in a dense array.
    public EmitContent Visit(C.InitElemDesignated n)
    {
        Gate(1999, "array designators", n.Arg0);  // C99
        return new EmitContent.InitDesignated(T(n.Arg1), T(n.Arg4));
    }
    public EmitContent Visit(C.InitListOne n) =>
        new EmitContent.InitGroup(new[] { (EmitContent.InitNode)n.Arg0.Content });
    public EmitContent Visit(C.InitListCons n)
    {
        var prev = ((EmitContent.InitGroup)n.Arg0.Content).Items;
        var combined = new List<EmitContent.InitNode>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add((EmitContent.InitNode)n.Arg2.Content);
        return new EmitContent.InitGroup(combined);
    }
    public EmitContent Visit(C.InitListTrail n) => (EmitContent.InitGroup)n.Arg0.Content;

    // ---- brace-initializer interpretation -------------------------------
    // The immediate items of a group as leaf value strings; throws on a nested
    // group (for 1-D / single-struct contexts that don't nest).
    private static List<string> Leaves(EmitContent.InitGroup g)
    {
        var vals = new List<string>(g.Items.Count);
        foreach (var it in g.Items)
        {
            if (it is EmitContent.InitLeaf leaf) { vals.Add(leaf.Value); }
            else { throw new CompileException("a nested brace initializer isn't valid here"); }
        }
        return vals;
    }

    // True if a brace initializer contains any C99 array designator (`[i] = v`).
    private static bool HasDesignators(EmitContent.InitGroup g)
    {
        foreach (var it in g.Items) { if (it is EmitContent.InitDesignated) { return true; } }
        return false;
    }

    // Emit a 1-D scalar array with C99 array designators into a dense, zero-filled
    // `stackalloc`. A designator `[i] = v` sets the cursor to `i`; an undesignated
    // element fills the current cursor; both advance it (later writes to the same
    // index win, per C). `declaredSize` is the array's constant size, or -1 to
    // derive it from the highest index touched (the implicit `[]` form). Scalar
    // only — a struct/nested value or a non-constant index fails loudly.
    private EmitContent EmitDesignatedArray(
        string elem, string emitted, string name, EmitContent.InitGroup group, int declaredSize, int dimCount)
    {
        if (dimCount > 1)
        {
            throw new CompileException($"array designators on `{name}` are only supported for 1-D arrays");
        }
        if (_structFields.ContainsKey(elem))
        {
            throw new CompileException($"array designators on a struct array (`{name}`) aren't supported yet");
        }
        var output = DesignatedValues(elem, group, declaredSize);
        NoteLocalArray(name, new CType.Arr(new CType.Sized(elem), output.Count));
        return $"{elem}* {Id(emitted)} = stackalloc {elem}[]{{ {string.Join(", ", output)} }}";
    }

    // Build the dense, zero-filled value list for a 1-D scalar array with C99
    // array designators (the body of EmitDesignatedArray, shared with the
    // array-compound-literal form). Callers do the struct/dim guards.
    private List<string> DesignatedValues(string elem, EmitContent.InitGroup group, int declaredSize)
    {
        var slots = new Dictionary<int, string>();
        var cursor = 0;
        var maxIndex = -1;
        foreach (var it in group.Items)
        {
            switch (it)
            {
                case EmitContent.InitDesignated d:
                    if (!int.TryParse(d.Index, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out cursor) || cursor < 0)
                    {
                        throw new CompileException($"array designator `[{d.Index}]` must be a constant non-negative integer");
                    }
                    slots[cursor] = d.Value;
                    break;
                case EmitContent.InitLeaf l:
                    slots[cursor] = l.Value;
                    break;
                default:
                    throw new CompileException("nested/aggregate values aren't supported with array designators");
            }
            if (cursor > maxIndex) { maxIndex = cursor; }
            cursor++;
        }
        var size = declaredSize >= 0 ? declaredSize : maxIndex + 1;
        if (maxIndex >= size)
        {
            throw new CompileException($"array designator index {maxIndex} is out of bounds for [{size}]");
        }
        var output = new List<string>(size);
        for (var i = 0; i < size; i++) { output.Add(slots.TryGetValue(i, out var v) ? v : "0"); }
        return output;
    }

    // Parse a dimension-text list to literal ints; empty if any isn't a literal.
    private static List<int> ParseDims(IReadOnlyList<string> dims)
    {
        var sizes = new List<int>(dims.Count);
        foreach (var d in dims)
        {
            if (int.TryParse(d, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) { sizes.Add(v); }
            else { sizes.Clear(); break; }
        }
        return sizes;
    }

    // Flatten a (possibly nested) SCALAR array initializer against `dims`,
    // applying C's per-dimension zero-fill, to exactly product(dims) values.
    // Handles the two regular shapes — fully-flat (`{1,2,3,4,5,6}`) and
    // fully-nested (`{{1,2,3},{4,5,6}}`); an irregular/mixed shape raises a clear
    // error rather than miscompile (C's full brace-elision rules aren't modelled).
    private static List<string> FlattenScalarInit(EmitContent.InitGroup g, IReadOnlyList<int> dims)
    {
        var total = 1;
        foreach (var d in dims) { total *= d; }
        var output = new List<string>(total);
        if (g.Items.All(it => it is EmitContent.InitLeaf))
        {
            foreach (var it in g.Items) { output.Add(((EmitContent.InitLeaf)it).Value); }
            if (output.Count > total) { throw new CompileException("too many initializers for array"); }
        }
        else
        {
            FlattenNested(g, dims, 0, output);
        }
        while (output.Count < total) { output.Add("0"); }  // zero-fill the tail
        return output;
    }

    private static void FlattenNested(EmitContent.InitNode node, IReadOnlyList<int> dims, int dimIdx, List<string> output)
    {
        if (node is not EmitContent.InitGroup g)
        {
            throw new CompileException("irregular nested array initializer (mixed braces and scalars)");
        }
        if (g.Items.Count > dims[dimIdx]) { throw new CompileException("too many initializers for an array dimension"); }
        if (dimIdx == dims.Count - 1)
        {
            foreach (var it in g.Items)
            {
                if (it is EmitContent.InitLeaf leaf) { output.Add(leaf.Value); }
                else { throw new CompileException("irregular nested array initializer"); }
            }
            for (var k = g.Items.Count; k < dims[dimIdx]; k++) { output.Add("0"); }
        }
        else
        {
            var subSize = 1;
            for (var i = dimIdx + 1; i < dims.Count; i++) { subSize *= dims[i]; }
            foreach (var it in g.Items) { FlattenNested(it, dims, dimIdx + 1, output); }
            for (var k = g.Items.Count; k < dims[dimIdx]; k++)
            {
                for (var z = 0; z < subSize; z++) { output.Add("0"); }
            }
        }
    }

    // Build each top-level group of a struct-array initializer into a C#
    // object-creation `new T { f = v, … }`. Shared by the decl form and the
    // array-compound-literal form.
    private List<string> StructArrayElems(string structType, EmitContent.InitGroup g)
    {
        var fields = _structFields[structType];
        var elems = new List<string>(g.Items.Count);
        foreach (var it in g.Items)
        {
            if (it is not EmitContent.InitGroup grp)
            {
                throw new CompileException($"each element of a `{structType}` array initializer must be a `{{ … }}` group");
            }
            var vals = Leaves(grp);
            var sb = new StringBuilder("new ").Append(structType).Append(" { ");
            var count = System.Math.Min(vals.Count, fields.Count);
            for (var i = 0; i < count; i++)
            {
                if (i > 0) { sb.Append(", "); }
                sb.Append(Id(fields[i])).Append(" = ").Append(vals[i]);
            }
            sb.Append(" }");
            elems.Add(sb.ToString());
        }
        return elems;
    }

    // Emit a struct-array initializer: each top-level group → `new T { f = v, … }`.
    private string EmitStructArrayInit(string structType, string name, EmitContent.InitGroup g) =>
        $"{structType}* {Id(name)} = stackalloc {structType}[]{{ {string.Join(", ", StructArrayElems(structType, g))} }}";

    // `Point p = {1, 2};` — struct aggregate init. C# can't take positional
    // initializers on a struct, so we look up the struct's field names (from
    // _structFields, populated by StructDef / TypedefStruct / UnionDef) and
    // emit a named-initializer: `Point p = new Point { x = 1, y = 2 };`.
    // If the type isn't a known struct (e.g. user wrote it for a typedef'd
    // primitive), fall back to a zero init with a comment — Roslyn will
    // surface the real error if the user pursued it.
    public EmitContent Visit(C.DeclStructInit n)
    {
        var type = T(n.Arg0);
        var name = T(n.Arg1);
        NoteLocal(name);
        var emittedName = DeclareLocal(name);
        var values = Leaves((EmitContent.InitGroup)n.Arg4.Content);  // positional field values

        if (!_structFields.TryGetValue(type, out var fields))
        {
            return $"{type} {Id(emittedName)} = default /* dotcc: unknown struct '{type}' for aggregate init */";
        }

        var sb = new StringBuilder();
        sb.Append(type).Append(' ').Append(Id(emittedName)).Append(" = new ").Append(type).Append(" { ");
        var count = Math.Min(values.Count, fields.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            sb.Append(Id(fields[i])).Append(" = ").Append(values[i]);
        }
        sb.Append(" }");
        return sb.ToString();
    }

    // `(Point){1, 2}` — C99 compound literal (positional). An unnamed object of
    // struct/union type, usable in any expression position (init, argument,
    // return, …). Lowers to a C# object-creation `new Point { x = 1, y = 2 }`,
    // reusing the same field-name lookup as DeclStructInit. Carries the struct's
    // CType up so the result can flow into casts / sizeof / further use. A
    // non-struct Type (e.g. `(int[]){…}` — not parseable anyway — or a typedef'd
    // primitive) fails loudly rather than emit something wrong.
    public EmitContent Visit(C.CompoundLit n)
    {
        Gate(1999, "compound literals", n.Arg1);  // C99
        var type = T(n.Arg1);
        var values = Leaves((EmitContent.InitGroup)n.Arg4.Content);  // positional field values
        if (!_structFields.TryGetValue(type, out var fields))
        {
            // Scalar / pointer / enum compound literal `(int){5}` — an unnamed
            // object of that type with exactly one initializer; lower to a cast
            // of the value. (Array compound literals take a separate production.)
            if (values.Count == 1)
            {
                return Typed($"(({type})({values[0]}))", new CType.Sized(type));
            }
            throw new CompileException(
                $"compound literal `({type}){{ … }}` of a non-struct type needs exactly one "
                + $"initializer (got {values.Count})");
        }
        var sb = new StringBuilder("new ").Append(type).Append(" { ");
        var count = Math.Min(values.Count, fields.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            sb.Append(Id(fields[i])).Append(" = ").Append(values[i]);
        }
        sb.Append(" }");
        return Typed(sb.ToString(), new CType.Sized(type));
    }

    // `(Point){ .x = 1, .y = 2 }` — designated compound literal (C99). The user
    // named the fields, so (like DeclStructDesignated) no _structFields lookup
    // is needed; the MemberInitList already emits `field = value` pairs in C#
    // object-initializer shape. Omitted fields zero-fill (C99 / C# default).
    public EmitContent Visit(C.CompoundLitDesignated n)
    {
        Gate(1999, "compound literals", n.Arg1);  // C99
        var type = T(n.Arg1);
        var members = IM(n.Arg4);  // typed InitMembers list
        return Typed($"new {type} {{ {string.Join(", ", members)} }}", new CType.Sized(type));
    }

    // `(Type){}` — empty compound literal (C23). A zero-valued unnamed object.
    // `default(Type)` is universal: scalar, struct, union, pointer all zero.
    public EmitContent Visit(C.CompoundLitEmpty n)
    {
        Gate(2023, "empty initializer", n.Arg1);  // C23 (empty {} is the C23 part)
        var type = T(n.Arg1);
        return Typed($"default({type})", new CType.Sized(type));
    }

    // `(int[3]){1,2,3}` — sized array compound literal (C99).
    public EmitContent Visit(C.CompoundLitArr n)
    {
        Gate(1999, "compound literals", n.Arg1);
        return ArrayCompoundLit(T(n.Arg1), ParseDims(A(n.Arg2)), (EmitContent.InitGroup)n.Arg5.Content);
    }
    // `(int[]){1,2,3}` — implicit-size array compound literal (C99).
    public EmitContent Visit(C.CompoundLitArrImplicit n)
    {
        Gate(1999, "compound literals", n.Arg1);
        return ArrayCompoundLit(T(n.Arg1), new List<int>(), (EmitContent.InitGroup)n.Arg6.Content);
    }

    // Array compound literal → a `stackalloc T[]{ … }` expression. Valid in
    // initializer position (`T* p = (int[]){…}`); other positions can't escape a
    // stackalloc to a pointer in C# (Roslyn error — documented). Scalar element
    // types only for now; sized forms zero-fill to the declared length (C
    // semantics), implicit `[]` takes the initializer's length.
    private EmitContent ArrayCompoundLit(string elem, List<int> sizes, EmitContent.InitGroup group)
    {
        List<string> vals;
        if (_structFields.ContainsKey(elem))
        {
            // Struct-element array compound literal `(struct P[]){{1,2},{3,4}}`.
            if (HasDesignators(group))
            {
                throw new CompileException("array designators on a struct element type aren't supported yet");
            }
            vals = StructArrayElems(elem, group);
        }
        else if (HasDesignators(group))
        {
            // Designated array compound literal `(int[]){[2]=9, [4]=1}`.
            vals = DesignatedValues(elem, group, sizes.Count > 0 ? sizes[0] : -1);
        }
        else
        {
            vals = sizes.Count > 0 ? FlattenScalarInit(group, sizes) : Leaves(group);
        }
        // A `stackalloc` expression is valid in initializer position
        // (`T* p = (int[]){…}`); other positions can't escape it to a pointer in
        // C# (Roslyn error — needs statement hoisting; documented in C-SUPPORT).
        return Typed($"stackalloc {elem}[]{{ {string.Join(", ", vals)} }}",
            new CType.Arr(new CType.Sized(elem), vals.Count));
    }

}
