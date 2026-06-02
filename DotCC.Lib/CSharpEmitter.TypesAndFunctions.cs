#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // ---- Function signature visitors ------------------------------------
    // Each FnSig variant extracts (type, name, params) from its position,
    // sets `_currentFunctionName` (consumed by Visit(Var) for `__func__`),
    // and returns a structured EmitContent.FnHeader for the enclosing Fn
    // reduction to combine with the body.

    public EmitContent Visit(C.FnSig n)  // Type ID ( ParamList )
        => StartFn(type: T(n.Arg0), name: T(n.Arg1), pars: T(n.Arg3), isStatic: false, isInline: InlineOf(n.Arg0), isNoreturn: NoreturnOf(n.Arg0));
    public EmitContent Visit(C.FnSigNoArgs n)  // Type ID ( )
        => StartFn(type: T(n.Arg0), name: T(n.Arg1), pars: "", isStatic: false, isInline: InlineOf(n.Arg0), isNoreturn: NoreturnOf(n.Arg0));
    public EmitContent Visit(C.FnSigVoidArgs n)  // Type ID ( void )
        => StartFn(type: T(n.Arg0), name: T(n.Arg1), pars: "", isStatic: false, isInline: InlineOf(n.Arg0), isNoreturn: NoreturnOf(n.Arg0));
    public EmitContent Visit(C.FnSigStatic n)  // static Type ID ( ParamList )
        => StartFn(type: T(n.Arg1), name: T(n.Arg2), pars: T(n.Arg4), isStatic: true, isInline: InlineOf(n.Arg1), isNoreturn: NoreturnOf(n.Arg1));
    public EmitContent Visit(C.FnSigStaticNoArgs n)  // static Type ID ( )
        => StartFn(type: T(n.Arg1), name: T(n.Arg2), pars: "", isStatic: true, isInline: InlineOf(n.Arg1), isNoreturn: NoreturnOf(n.Arg1));
    public EmitContent Visit(C.FnSigStaticVoidArgs n)  // static Type ID ( void )
        => StartFn(type: T(n.Arg1), name: T(n.Arg2), pars: "", isStatic: true, isInline: InlineOf(n.Arg1), isNoreturn: NoreturnOf(n.Arg1));
    // Parenthesized function name `T (name)(args)` — the parens are pure grouping
    // around the declarator name (Lua's macro-guard idiom), so the name is Arg2
    // (inside the parens) and the rest is identical to the bare FnSig forms.
    public EmitContent Visit(C.FnSigParen n)  // Type ( ID ) ( ParamList )
        => StartFn(type: T(n.Arg0), name: T(n.Arg2), pars: T(n.Arg5), isStatic: false, isInline: InlineOf(n.Arg0), isNoreturn: NoreturnOf(n.Arg0));
    public EmitContent Visit(C.FnSigParenNoArgs n)  // Type ( ID ) ( )
        => StartFn(type: T(n.Arg0), name: T(n.Arg2), pars: "", isStatic: false, isInline: InlineOf(n.Arg0), isNoreturn: NoreturnOf(n.Arg0));
    public EmitContent Visit(C.FnSigParenVoidArgs n)  // Type ( ID ) ( void )
        => StartFn(type: T(n.Arg0), name: T(n.Arg2), pars: "", isStatic: false, isInline: InlineOf(n.Arg0), isNoreturn: NoreturnOf(n.Arg0));

    private EmitContent.FnHeader StartFn(string type, string name, string pars, bool isStatic, bool isInline = false, bool isNoreturn = false)
    {
        // Set the active-function name BEFORE Block reduces — this is the
        // whole point of the FnSig split. Any __func__ inside the body
        // resolves to this directly at Var-visit time.
        _currentFunctionName = name;
        _currentFunctionReturnType = type;
        _fnReturnTypes[name] = type;
        // Fresh per-function scopes (no nested functions in C).
        _fnMalloc.Clear();
        _fnStatics.Clear();
        _localNames.Clear();
        _localTypes.Clear();
        _localVolatile.Clear();
        _localVolatilePointee.Clear();
        _localAtomic.Clear();
        _localArrayInfo.Clear();
        // Fresh local-renaming state, then open the function's outermost frame
        // (the parameter scope). The body's `{` opens a nested frame inside it.
        _scopes.Clear();
        _usedLocalNames.Clear();
        PushScope();
        // Adopt the params staged during this FnSig's ParamList reduction. Each
        // is registered in the param frame with its RAW name (params keep their
        // spelling — DeclareLocal returns the raw name while the used-set is
        // empty), so a nested local that collides is the one that gets renamed.
        foreach (var p in _pendingParams)
        {
            _localNames.Add(p.Name);
            _localTypes[p.Name] = p.Type;
            DeclareLocal(p.Name);
        }
        _pendingParams.Clear();
        // Adopt volatile / pointer-to-volatile param qualifications (params keep
        // their raw spelling, so these keys match the recorded names).
        foreach (var pn in _pendingVolatileParams) { _localVolatile.Add(pn); }
        foreach (var pn in _pendingVolatilePointeeParams) { _localVolatilePointee.Add(pn); }
        foreach (var pn in _pendingAtomicParams) { _localAtomic.Add(pn); }
        _pendingVolatileParams.Clear();
        _pendingVolatilePointeeParams.Clear();
        _pendingAtomicParams.Clear();
        return new EmitContent.FnHeader(type, name, pars, isStatic, isInline, isNoreturn);
    }

    // ---- Function definition / prototype --------------------------------
    // `Fn → FnSig Block` and `Fn → FnSig ';'`. The FnSig has already run
    // and stashed (type/name/params/isStatic) into a typed FnHeader plus
    // set `_currentFunctionName` for the body's Var visits to consume.
    // Now we do the bookkeeping (MainArity, exports list) and emit/clear.

    public EmitContent Visit(C.FuncDef n) => EmitFuncDef(FH(n.Arg0), T(n.Arg1));

    // `extern T f(args) { … }` — an extern function DEFINITION. `extern` is the
    // default linkage for functions, so this is identical to a plain definition;
    // share the emit (bookkeeping, exports, malloc-promotion finalize, emit).
    public EmitContent Visit(C.ExternFnDef n) => EmitFuncDef(FH(n.Arg1), T(n.Arg2));

    // `extern T f(args);` — an extern function PROTOTYPE. Emits nothing (C#
    // methods hoist), but must unwind the FnSig state StartFn set, exactly like
    // a plain prototype.
    public EmitContent Visit(C.ExternFnProto n)
    {
        _currentFunctionName = null;
        _currentFunctionReturnType = null;
        _fnMalloc.Clear();
        _fnStatics.Clear();
        _scopes.Clear();
        _usedLocalNames.Clear();
        return string.Empty;
    }

    // `extern T x;` / `extern T a, b;` — extern VARIABLE declarations. These
    // declare without defining: the storage lives in another translation unit
    // (or another file in this program), so emit NO field — emitting one would
    // double-define against the real definition. Register each name's type so
    // same-file references still resolve as globals.
    public EmitContent Visit(C.ExternVarDecl n)
    {
        var type = T(n.Arg1);
        foreach (var e in DE(n.Arg2)) { _globalTypes[e.Name] = type; }
        return string.Empty;
    }

    private EmitContent EmitFuncDef(EmitContent.FnHeader sig, string body)
    {
        // Bookkeeping: `main` records arity (0 when params are empty,
        // CountCommas+1 otherwise — `int main()` is arity 0, not 1);
        // non-static non-main goes on the exports list for library-mode
        // [UnmanagedCallersOnly] wrappers.
        if (sig.Name == "main")
        {
            MainArity = string.IsNullOrEmpty(sig.Params) ? 0 : CountCommas(sig.Params) + 1;
        }
        else if (!sig.IsStatic) { _exports.Add(new Export(sig.Name, sig.Type, sig.Params)); }
        // Finalise stack-promotion decisions for this function: a candidate is
        // promotable iff its declared type matched `S*`, it's freed at least
        // once, and every reference is accounted for as either a `->` base or a
        // free arg (TotalRefs == ArrowRefs + FreeRefs — no escaping use).
        foreach (var (varName, mv) in _fnMalloc)
        {
            if (mv.TypeMatches && mv.FreeRefs >= 1
                && mv.TotalRefs == mv.ArrowRefs + mv.FreeRefs
                // Only promote genuine struct types — the plan is "stack struct
                // value". A scalar like `int*` can't reach here via `->` anyway,
                // but the gate keeps a degenerate malloc+free of a scalar from
                // lowering to a confusing `int p = new int();`.
                && _structFields.ContainsKey(mv.StructType))
            {
                _promotableOut.Add((sig.Name, varName));
            }
        }
        _fnMalloc.Clear();
        _fnStatics.Clear();
        _currentFunctionName = null;  // exit function scope
        _currentFunctionReturnType = null;
        // Drop the parameter frame (the body block already popped its own); a
        // clean stack between functions means file-scope references resolve as
        // globals, not against a stale frame.
        _scopes.Clear();
        _usedLocalNames.Clear();
        // Escape the method name for C# emission; sig.Name stays raw above for
        // the `main` check and the export list (the C-ABI EntryPoint keeps the
        // real name). A call to this function escapes identically via Visit(Var).
        // Function specifiers → C# attributes (faithful lowerings, not cosmetic;
        // attributes on local functions are legal C# 9+). C99 `inline` →
        // AggressiveInlining (a real JIT hint); C11 `_Noreturn` → [DoesNotReturn]
        // (informs C# flow analysis). The shell imports CompilerServices;
        // DoesNotReturn is fully qualified so no extra using is needed.
        var attr = "";
        if (sig.IsInline) { attr += "[MethodImpl(MethodImplOptions.AggressiveInlining)]\n"; }
        if (sig.IsNoreturn) { attr += "[System.Diagnostics.CodeAnalysis.DoesNotReturn]\n"; }
        return $"{attr}static unsafe {sig.Type} {Id(sig.Name)}({sig.Params})\n{body}";
    }

    public EmitContent Visit(C.FuncProto n)
    {
        // Prototypes emit nothing — C# methods hoist. We still need to
        // unwind the FnSig's _currentFunctionName since the body wasn't
        // visited but the name was set, and drop the param frame StartFn pushed
        // (no body Block ran to pop a nested one).
        _currentFunctionName = null;
        _currentFunctionReturnType = null;
        _scopes.Clear();
        _usedLocalNames.Clear();
        return string.Empty;
    }

    // `struct ID { fields } ;` — emit a C# struct declaration into the side
    // channel; contribute nothing to the function-emit stream. The struct
    // is marked `unsafe` so it can legally contain pointer fields; all our
    // C structs are by definition unmanaged (no GC refs in their fields)
    // so this is sound.
    public EmitContent Visit(C.StructDef n) => EmitTopLevelStructDef(T(n.Arg1), T(n.Arg3));

    // The `struct Tag { … };` emit, shared by the file-scope (StructDef) and
    // block-scope (StmtStructDef) productions — a type has no storage, so a local
    // definition hoists into the same top-level section, deduped by tag.
    private EmitContent EmitTopLevelStructDef(string name, string members)
    {
        DrainPendingFields(name);  // always — clears _pendingFields for the next struct
        if (_emittedTypes.Add(name))
        {
            var declText = "unsafe struct " + name + "\n{\n" + IndentEach(members) + "}\n\n";
            _structs.Append(declText);
            _typeDecls[name] = declText;
        }
        return string.Empty;
    }

    // `struct Node ;` — forward declaration. C# resolves type references
    // regardless of declaration order, so we emit nothing. The full
    // StructDef (if any) lands later in the same translation unit and
    // populates _structFields then.
    public EmitContent Visit(C.StructFwd n) => string.Empty;

    // File-scope variable declarations — `int x;`, `int x = 5;`, and the
    // multi-declarator forms `int a, b;` / `int a = 1, b = 2;` /
    // `int a, b = 5;`. Each declarator appends one `public static unsafe`
    // field to the _globals side channel; the shell wraps them in a
    // `static unsafe class DotCcGlobals` declared in the type-decls
    // section. Type runs through QualifyPredefinedTypeName so
    // `jmp_buf env;` (which after typedef lowering references
    // `LongJmpToken`) reaches `Libc.LongJmpToken` correctly — bare
    // nested-type names don't resolve at class-member-decl position
    // for the same reason the alias-emit path qualifies them.
    //
    // Reference types (currently just `LongJmpToken` + its `jmp_buf`
    // typedef alias) get an auto `= new T()` for no-init entries —
    // C# default-inits class-typed fields to null, and the longjmp
    // exception filter compares tokens by reference identity, so a
    // null env would silently break setjmp/longjmp dispatch.
    public EmitContent Visit(C.GlobalDeclList n)
    {
        EmitGlobalFields(T(n.Arg0), DE(n.Arg1));
        return string.Empty;
    }

    // File-scope `static T x;`. Internal linkage is a no-op for variables in
    // dotcc's single-program model (they're never exported), so it lowers
    // exactly like a plain global — the `static` keyword (Arg0) is consumed.
    public EmitContent Visit(C.GlobalStaticDeclList n)
    {
        EmitGlobalFields(T(n.Arg1), DE(n.Arg2));
        return string.Empty;
    }

    // Emit one `public static unsafe` field per init-declarator into the
    // DotCcGlobals side channel. `rawType` runs through QualifyPredefinedTypeName
    // so e.g. `jmp_buf`/`LongJmpToken` resolve at class-member position; ref
    // types get an auto `= new T()` for the no-init form (see GlobalDeclList's
    // original notes). Shared by file-scope globals AND function-static
    // locals (which pass already-mangled names).
    private void EmitGlobalFields(string rawType, IReadOnlyList<EmitContent.DeclEntry> entries)
    {
        var type = QualifyPredefinedTypeName(rawType);
        var isRefType = IsPredefinedRefTypeName(rawType) || _refTypeAliases.Contains(rawType);
        foreach (var entry in entries)
        {
            // Track global var → type, consulted by Visit(Var) for enum coercion
            // (gated on _enumTags there) AND by sizeof. Stored unconditionally so
            // `sizeof(globalScalar)` resolves; non-enum types simply never match
            // the _enumTags check, so this is safe.
            _globalTypes[entry.Name] = rawType;
            string init;
            if (entry.Init is not null)
            {
                // Reconcile enum-ness against the declared type (same as block scope).
                init = $" = {ReconcileEnumInit(rawType, entry)}";
            }
            else if (isRefType)
            {
                init = $" = new {type}()";
            }
            else
            {
                // Value-type field with no initializer — C# zero-inits
                // class fields, which matches C's static-storage default
                // (all zero-bits). No explicit initializer needed.
                init = string.Empty;
            }
            _globals.Append("    public static unsafe ").Append(type).Append(' ').Append(Id(entry.Name)).Append(init).Append(";\n");
        }
    }

    public EmitContent Visit(C.MembersCons n) => T(n.Arg0) + T(n.Arg1);
    public EmitContent Visit(C.MembersOne n)  => T(n.Arg0);
    // `Type ID ;` member — emit as public field. C convention is that all
    // struct fields are accessible to anyone with a pointer; matching that
    // requires `public` in C#. Field names also pushed onto _pendingFields
    // so the enclosing StructDef / TypedefStruct / UnionDef can index them
    // by struct name for the aggregate-init lookup later.
    public EmitContent Visit(C.StructMemberList n)
    {
        var type = T(n.Arg0);
        var isVolatile = VolatileOf(n.Arg0);
        var isVolatilePointee = VolatilePointeeOf(n.Arg0);
        var isAtomic = AtomicOf(n.Arg0);
        var entries = DE(n.Arg1);
        // Per-declarator type, same rule as EmitDecl: first uses Type, a
        // subsequent one with its own `*`s uses stripStars(Type) + that many.
        var baseTy = StripTrailingStars(type);
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var fieldType = i == 0 ? type : baseTy + new string('*', e.Stars);
            _pendingFields.Add(e.Name);  // raw name — keyed lookups stay un-escaped
            // Record the field's CType so a `s.field` / `p->field` read carries it
            // (enabling sizeof of a member, of `*member` / `member[i]`, and nested
            // member chains). A typedef'd pointer alias resolves to its underlying
            // (`StkId` → `StackValue*`) so a deref/subscript can peel the `*`.
            _pendingFieldTypeMap[e.Name] = new CType.Sized(ResolveTypedef(fieldType));
            // An enum-typed field is remembered so a `s.field` / `p->field` read
            // can be tagged enum-typed (see FieldEnum / the member-access visitors).
            if (_enumTags.Contains(fieldType)) { _pendingFieldEnumMap[e.Name] = fieldType; }
            // An atomic / volatile field of eligible scalar type → record it so
            // member access lowers to Atomic.* / Volatile.Read/Write (atomic wins).
            if (isAtomic && IsAtomicEligible(fieldType)) { _pendingAtomicFields[e.Name] = fieldType; }
            else if (isVolatile && IsVolatileEligible(fieldType)) { _pendingVolatileFields.Add(e.Name); }
            // A pointer-to-volatile field (`volatile int *p;`) → `s.p[i]` / `*s.p`
            // fence (phase V2).
            if (isVolatilePointee && VolatilePointeeEligible(fieldType) is not null) { _pendingVolatilePointeeFields.Add(e.Name); }
            sb.Append("public ").Append(fieldType).Append(' ').Append(Id(e.Name)).Append(";\n");
        }
        return sb.ToString();
    }

    // Named bit-field `Type ID : width ;`. C# has no bit-fields, so dotcc emits
    // a private backing field of the declared type plus a public accessor
    // PROPERTY that masks to the width on store and sign-extends on read — so
    // the VALUE semantics are faithful (`f.x = 8` with `x:3` wraps to 0; a
    // signed `x:3 = 5` reads back as -3). The struct's size & layout still
    // differ from C (each field is its own word — bit packing is implementation-
    // defined and gcc≠MSVC, so it couldn't match both oracles anyway); only the
    // values are guaranteed. A non-integer base type (`_Bool`) or a non-literal
    // width falls back to the old lossy full-field form. Documented in C-SUPPORT.
    public EmitContent Visit(C.StructBitField n)
    {
        var csType = StripOuterParens(T(n.Arg0));
        var fieldName = T(n.Arg1);
        _pendingFields.Add(fieldName);
        var widthText = StripOuterParens(T(n.Arg3));
        if (TryEmitMaskedBitField(csType, fieldName, widthText, out var emitted)) { return emitted; }
        // Fallback: full field, width dropped (correct only for in-range values).
        return $"public {csType} {Id(fieldName)}; // C bit-field :{widthText} (width dropped — non-maskable)\n";
    }

    // Build the backing-field + masked-accessor-property pair for a bit-field.
    // Returns false (→ caller falls back) for a non-literal width or a base type
    // that isn't a plain C# integer.
    private bool TryEmitMaskedBitField(string csType, string name, string widthText, out string emitted)
    {
        emitted = string.Empty;
        if (!int.TryParse(widthText, out int w) || w <= 0 || w > 64) { return false; }
        bool signed;
        switch (csType)
        {
            case "int": case "long": case "short": case "sbyte": signed = true; break;
            case "uint": case "ulong": case "ushort": case "byte": signed = false; break;
            default: return false; // _Bool/CBool, enum, typedef, … → lossy fallback
        }
        ulong mask = w >= 64 ? ulong.MaxValue : (1UL << w) - 1UL;
        ulong high = 1UL << (w - 1);
        var backing = $"__bf_{name}";
        var id = Id(name);
        // Store: keep the low `w` bits (`(ulong)value` sign-extends a signed
        // source first, so the kept bits are right either way), then cast back.
        var store = $"{backing} = unchecked(({csType})((ulong)value & {mask}UL));";
        // Read: unsigned returns the backing directly; signed sign-extends when
        // the field's top bit is set (OR in the bits above the width).
        var read = signed
            ? $"((ulong){backing} & {high}UL) != 0 ? unchecked(({csType})((ulong){backing} | ~{mask}UL)) : {backing}"
            : backing;
        emitted =
            $"private {csType} {backing}; // C bit-field :{w}\n" +
            $"public {csType} {id} {{ get => {read}; set => {store} }}\n";
        return true;
    }

    // Anonymous bit-field `Type : width ;` — pure padding/alignment in C, with
    // no accessible member. Nothing to emit.
    public EmitContent Visit(C.StructAnonBitField n) => string.Empty;

    // C# fixed-size buffers (`fixed T name[N];`) accept only these primitive
    // element types — used for both sized array members and the flexible array
    // member. dotcc's `char` is `byte`; `_Bool` (CBool), pointers, structs, and
    // enums aren't allowed and fail loudly.
    private static readonly HashSet<string> _fixedBufferElemTypes = new(StringComparer.Ordinal)
    { "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double" };

    // Sized array member `T name[N];` (C89). A primitive element → a C# `fixed`
    // buffer (no bounds check, decays to a pointer — exactly the struct-hack
    // semantics). A NON-primitive element (struct / union / pointer / enum) can't
    // be a `fixed` buffer, so it lowers to a C# 12 `[InlineArray(N)]` synth type
    // — the inline-storage equivalent that accepts any unmanaged element type.
    public EmitContent Visit(C.StructArrMember n)
    {
        // Resolve a typedef element to its underlying primitive (`lu_byte` → `byte`),
        // both for the fixed-buffer eligibility check and because C# `fixed` requires
        // a primitive KEYWORD (it rejects the alias). Fold a constant-expression bound
        // (`extra_[sizeof(void*)]` → `[8]`) — C# `fixed[N]`/`[InlineArray(N)]` need a
        // literal.
        var elem = ResolveTypedef(T(n.Arg0));
        var name = T(n.Arg1);
        // ArrDims folded each dimension to a literal (`[sizeof(void*)]` → 8, `[N]` →
        // enum value). A member can't be a true VLA, so a non-constant dim is an error.
        var dims = ParseDims(A(n.Arg2));
        if (dims.Count == 0)
        {
            throw new CompileException($"array member `{name}` needs constant dimension(s)");
        }
        var total = 1;
        foreach (var d in dims) { total *= d; }
        var totalText = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // A MULTI-dimensional member flattens to one buffer; record the strided
        // access CType (`Arr(Arr(elem, dn), …)`) so `s.grid[i][j]` rewrites to flat
        // pointer arithmetic — the same multi-dim subscript path block-scope arrays use.
        if (dims.Count >= 2)
        {
            CType arr = new CType.Sized(elem);
            for (var i = dims.Count - 1; i >= 0; i--) { arr = new CType.Arr(arr, dims[i]); }
            _pendingMultiDimMembers[name] = (elem, !_fixedBufferElemTypes.Contains(elem), arr);
        }
        if (_fixedBufferElemTypes.Contains(elem))
        {
            _pendingFields.Add(name);
            _pendingFixedBufferFields.Add(name);  // decays to a pointer (offsetof)
            // A 1-D fixed buffer carries its Arr CType (multi-dim is handled by
            // _structMultiDimMembers), so `sizeof(s.buf)` is count*sizeof(elem),
            // not the decayed pointer size. (`s.buf[i]` rides ElementType.)
            if (dims.Count == 1) { _pendingFieldTypeMap[name] = new CType.Arr(new CType.Sized(elem), total); }
            return $"public fixed {elem} {Id(name)}[{totalText}];\n";
        }
        return EmitInlineArrayMember(elem, name, totalText, record1D: dims.Count < 2);
    }

    // Flexible array member `T name[];` (C99) — an incomplete array as the last
    // struct member, sized at allocation time. Lowered to a one-element fixed
    // buffer: the field sits at the right offset and `s->name[i]` indexes into
    // the malloc'd tail verbatim. `sizeof(S)` is one element larger than C's
    // (C's FAM is zero-size), so `malloc(sizeof(S) + n*sizeof(T))` over-allocates
    // by one element — harmless. Should be the last member (as in valid C).
    public EmitContent Visit(C.StructFlexArrMember n)
    {
        Gate(1999, "flexible array member", n.Arg0);  // C99
        var elem = ResolveTypedef(T(n.Arg0));  // `lu_byte` → `byte` for the fixed-buffer check
        var name = T(n.Arg1);
        // Primitive element → a 1-element `fixed` buffer (over-indexes into the
        // malloc'd tail with no bounds check — the struct-hack idiom). A
        // non-primitive element uses a 1-element [InlineArray] instead (the same
        // single-slot-at-the-right-offset trick, for any unmanaged element type).
        if (_fixedBufferElemTypes.Contains(elem))
        {
            _pendingFields.Add(name);
            _pendingFixedBufferFields.Add(name);  // decays to a pointer (offsetof)
            return $"public fixed {elem} {Id(name)}[1]; // C99 flexible array member (sized at allocation)\n";
        }
        return EmitInlineArrayMember(elem, name, "1");
    }

    // Set of synthesized [InlineArray] element types already emitted, so the
    // nested type decl is written once per (element, count) shape.
    private readonly HashSet<string> _inlineArrayTypes = new(StringComparer.Ordinal);

    // Per-aggregate map of fieldName → (element C# type, count) for members
    // lowered to an [InlineArray] synth type. Drained into _structInlineArrFields
    // under the aggregate's name (like _pendingFieldEnumMap → _structFieldEnums),
    // so member-access of such a field can rewrite a subscript to faithful
    // pointer arithmetic (over-indexing into the malloc'd tail, which the
    // bounds-checked InlineArray indexer would reject).
    private readonly Dictionary<string, (string Elem, int Count)> _pendingInlineArrFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, (string Elem, int Count)>> _structInlineArrFields =
        new(StringComparer.Ordinal);

    // Per-aggregate set of fields lowered to a C# `fixed` buffer (primitive array
    // member). offsetof needs to know: a fixed buffer DECAYS to a pointer, so its
    // address is `(byte*)t.field` (no `&`), whereas a regular field / [InlineArray]
    // wrapper uses `&t.field`. Drained alongside the inline-array map.
    private readonly HashSet<string> _pendingFixedBufferFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _structFixedBufferMembers = new(StringComparer.Ordinal);

    // Per-aggregate map for MULTI-dimensional array members: fieldName → (element C#
    // type, whether it's [InlineArray]-backed vs a `fixed` buffer, the strided
    // multi-dim CType). The member-access visitors decay it to a flat pointer tagged
    // with the CType so `s.grid[i][j]` rewrites to flat striding. Drained alongside
    // the 1-D inline-array map (DrainInlineArrFields).
    private readonly Dictionary<string, (string Elem, bool IsInline, CType Arr)> _pendingMultiDimMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, (string Elem, bool IsInline, CType Arr)>> _structMultiDimMembers =
        new(StringComparer.Ordinal);

    // Emit a non-primitive array member `T name[N];` as a C# 12 [InlineArray(N)]
    // field. C# `fixed` buffers reject non-primitive elements; an [InlineArray]
    // gives the same N inline elements at the field's offset for any UNMANAGED
    // element type (struct / union / pointer / enum). The wrapper type holds one
    // instance field (the element-0 marker); the attribute makes it an N-element
    // inline buffer. `&field` is the address of element 0, so faithful access
    // (subscript / decay) goes through pointer arithmetic — see the member-access
    // visitors — which also restores the struct-hack over-indexing that `fixed`
    // gave for free (the InlineArray indexer itself is bounds-checked).
    private EmitContent EmitInlineArrayMember(string elem, string name, string sizeText, bool record1D = true)
    {
        if (!int.TryParse(sizeText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            throw new CompileException(
                $"array member `{name}[{sizeText}]` of element type `{elem}` needs a constant size >= 1 "
                + "(it lowers to a C# [InlineArray], which requires a literal length)");
        }
        // The wrapper's storage element only needs the right BYTE SIZE: access
        // always goes through `(elem*)&field` pointer arithmetic (see the
        // member-access visitors), never the InlineArray indexer. So a POINTER
        // element is stored as `nint` (pointer-sized, and a valid InlineArray
        // element — a `T*` element triggers CS9184, since a pointer type isn't a
        // valid type argument). Struct/union/enum elements store as themselves.
        // The REAL element type is recorded for access + sizeof either way.
        var isPtr = elem.EndsWith("*", System.StringComparison.Ordinal);
        var storage = isPtr ? "nint" : elem;
        // Sanitize the element type into a valid identifier fragment (`UpVal*` →
        // `UpVal_p`) for the synth wrapper type name; dedup by (element, count).
        var tag = elem.Replace("*", "_p").Replace(" ", "_");
        var wrapper = $"__ia_{tag}_{count}";
        if (_inlineArrayTypes.Add(wrapper))
        {
            _structs.Append("[global::System.Runtime.CompilerServices.InlineArray(").Append(count).Append(")]\n");
            _structs.Append("unsafe struct ").Append(wrapper).Append("\n{\n    private ").Append(storage)
                .Append(" __e0;\n}\n\n");
        }
        _pendingFields.Add(name);
        // 1-D members record (elem, count) for the single-subscript decay; a multi-dim
        // member records its strided CType separately (_pendingMultiDimMembers).
        if (record1D) { _pendingInlineArrFields[name] = (elem, count); }
        return $"public {wrapper} {Id(name)};\n";
    }

    // Anonymous struct member (C11): `struct { … };` with no name. Its fields are
    // promoted into the enclosing aggregate's namespace. For a struct (sequential,
    // no overlap) promotion == inlining: emit the inner member lines directly, so
    // they become real fields of the parent (and `s.x` works verbatim, no rewrite).
    // The inner StructMembers already pushed their names to _pendingFields, which
    // is exactly right — they ARE direct parent fields.
    public EmitContent Visit(C.AnonStructMember n)
    {
        Gate(2011, "anonymous struct/union members", n.Arg0);  // C11
        // MemberMark pushed a snapshot; an anon struct INLINES (its fields stay
        // in the parent's _pendingFields as direct parent fields), so just
        // discard the mark to keep the stack balanced and emit the inner lines.
        if (_memberMarks.Count > 0) { _memberMarks.Pop(); }
        return T(n.Arg3);  // inner member lines, inlined into the parent
    }

    // Epsilon marker before an anonymous union's inner member list — snapshot how
    // many fields the parent already had, so AnonUnionMember can slice off exactly
    // the inner union's fields.
    public EmitContent Visit(C.MemberMark n)
    {
        _memberMarks.Push(_pendingFields.Count);
        return string.Empty;
    }

    // Anonymous union member (C11): `union { … };` with no name. Its fields must
    // OVERLAP, so they can't inline like an anon struct. Lift them into a generated
    // nested [StructLayout(Explicit)] type, add one synthetic field of it to the
    // parent, and register each inner field as PROMOTED to that synth field — so
    // member access rewrites `o.i` → `o.<synth>.i` (see MemberDot/MemberArrow).
    public EmitContent Visit(C.AnonUnionMember n)
    {
        Gate(2011, "anonymous struct/union members", n.Arg0);  // C11
        var innerLines = T(n.Arg3);  // MemberList lines (public T f;\n …)
        var snapshot = _memberMarks.Count > 0 ? _memberMarks.Pop() : _pendingFields.Count;
        // The inner union's field names are _pendingFields[snapshot..]: they are
        // NOT direct parent fields, so lift them out and register as promoted.
        var innerNames = new List<string>();
        for (var i = snapshot; i < _pendingFields.Count; i++) { innerNames.Add(_pendingFields[i]); }
        if (snapshot < _pendingFields.Count) { _pendingFields.RemoveRange(snapshot, _pendingFields.Count - snapshot); }

        var id = _anonAggCounter++;
        var nestedType = $"__AnonU{id}";
        var synth = $"__anon{id}";
        EmitExplicitUnionType(nestedType, innerLines);
        _pendingFields.Add(synth);  // the one real parent field
        foreach (var fld in innerNames) { _pendingPromotions.Add((fld, synth)); }
        return $"public {nestedType} {synth};\n";
    }

    // Named nested aggregate member `struct { … } name;` / `union { … } name;` —
    // a real field `name` of a SYNTHESIZED nested type, accessed as `o.name.inner`
    // (NOT promoted, unlike the anonymous forms). The struct synth type is
    // sequential; the union one gets the [StructLayout(Explicit)] treatment. The
    // inner fields are sliced off _pendingFields (they belong to the nested type,
    // not the parent) and registered under the synth type so `o.name.inner`
    // resolves; only `name` joins the parent's fields. Valid since C89 (the
    // member is named), so no dialect gate — unlike the anonymous C11 forms.
    public EmitContent Visit(C.NamedNestedStruct n) => EmitNamedNestedAggregate(T(n.Arg3), T(n.Arg5), union: false);
    public EmitContent Visit(C.NamedNestedUnion n)  => EmitNamedNestedAggregate(T(n.Arg3), T(n.Arg5), union: true);

    // Tagged named nested aggregate `struct Tag { … } name;` — the type takes the
    // TAG name (a nested tag lives at the enclosing/file scope in C), so later
    // `struct Tag` references resolve to it. Member name is the trailing ID.
    public EmitContent Visit(C.NamedNestedTaggedStruct n) => EmitNamedNestedAggregate(T(n.Arg4), T(n.Arg6), union: false, typeName: T(n.Arg1));
    public EmitContent Visit(C.NamedNestedTaggedUnion n)  => EmitNamedNestedAggregate(T(n.Arg4), T(n.Arg6), union: true,  typeName: T(n.Arg1));

    // Anonymous struct type at declaration scope (`struct { … } x;` / `static
    // struct { … } tab[] = {…}`). Synthesize a name, emit the sequential struct
    // decl, record its field names + types, and return the synth name as the Type
    // — so the enclosing declaration composes through the ordinary Type-based
    // productions (incl. arrays + aggregate init). Because this is a Type, it also
    // SUBSUMES the anonymous named-nested-struct MEMBER form (`struct { … } m;`
    // inside another struct): LALR routes that through here + structMemberList
    // (which is strictly more capable — multi-declarator / per-declarator `*`),
    // so the dedicated namedNestedStruct production handles only what this can't.
    // Shares the `__NestS` synth-name family + counter with EmitNamedNestedAggregate.
    public EmitContent Visit(C.TypeAnonStruct n) => EmitAnonAggregateType(T(n.Arg3), union: false);

    // Anonymous union type at declaration scope (`union { … } v;` /
    // `static const union { … } v = {…}` — Lua lstrlib's native-endianness probe).
    // Same shape as TypeAnonStruct but explicit-layout (overlapping) storage; the
    // synth name is recorded in _unionTypes (by EmitExplicitUnionType) so the layout
    // model treats it as a union, and a brace init targets the first member.
    public EmitContent Visit(C.TypeAnonUnion n) => EmitAnonAggregateType(T(n.Arg3), union: true);

    private EmitContent EmitAnonAggregateType(string memberLines, bool union)
    {
        var snapshot = _memberMarks.Count > 0 ? _memberMarks.Pop() : _pendingFields.Count;
        var innerNames = new List<string>();
        for (var i = snapshot; i < _pendingFields.Count; i++) { innerNames.Add(_pendingFields[i]); }
        if (snapshot < _pendingFields.Count) { _pendingFields.RemoveRange(snapshot, _pendingFields.Count - snapshot); }
        var nestedType = (union ? "__NestU" : "__NestS") + _anonAggCounter;
        _anonAggCounter++;
        if (union) { EmitExplicitUnionType(nestedType, memberLines); }
        else { EmitSequentialStructType(nestedType, memberLines); }
        _structFields[nestedType] = innerNames;
        var innerTypes = new Dictionary<string, CType>(StringComparer.Ordinal);
        foreach (var inner in innerNames)
        {
            if (_pendingFieldTypeMap.TryGetValue(inner, out var ity))
            {
                innerTypes[inner] = ity;
                _pendingFieldTypeMap.Remove(inner);
            }
        }
        if (innerTypes.Count > 0) { _structFieldTypes[nestedType] = innerTypes; }
        return nestedType;
    }

    private EmitContent EmitNamedNestedAggregate(string innerLines, string fieldName, bool union, string? typeName = null)
    {
        var snapshot = _memberMarks.Count > 0 ? _memberMarks.Pop() : _pendingFields.Count;
        var innerNames = new List<string>();
        for (var i = snapshot; i < _pendingFields.Count; i++) { innerNames.Add(_pendingFields[i]); }
        if (snapshot < _pendingFields.Count) { _pendingFields.RemoveRange(snapshot, _pendingFields.Count - snapshot); }
        // Tagged → the C# type is the tag name (and `struct Tag` references it);
        // anonymous → a synthesized unique name.
        var nestedType = typeName ?? (union ? $"__NestU{_anonAggCounter}" : $"__NestS{_anonAggCounter}");
        if (typeName is null) { _anonAggCounter++; }
        if (union) { EmitExplicitUnionType(nestedType, innerLines); }
        else { EmitSequentialStructType(nestedType, innerLines); }
        _structFields[nestedType] = innerNames;  // so `o.name.inner` resolves its fields
        // Move the inner fields' CTypes from the pending (parent) map onto the
        // nested type's own field-type map — the type analogue of slicing the
        // inner NAMES off _pendingFields. Without this the inner types would
        // wrongly drain into the PARENT, and `o.name.inner` would carry no type,
        // so a chain through the member (`sizeof(*o.name.inner)`, Lua's
        // `sizeof(*p.dyd.actvar.arr)`) couldn't resolve.
        var innerTypes = new Dictionary<string, CType>(StringComparer.Ordinal);
        foreach (var inner in innerNames)
        {
            if (_pendingFieldTypeMap.TryGetValue(inner, out var ity))
            {
                innerTypes[inner] = ity;
                _pendingFieldTypeMap.Remove(inner);
            }
        }
        if (innerTypes.Count > 0) { _structFieldTypes[nestedType] = innerTypes; }
        _pendingFields.Add(fieldName);           // the named field IS a parent field
        // …and its type IS the synth nested type, so `o.name` carries CType.Sized
        // (nestedType) and the next `.inner` resolves via _structFieldTypes.
        _pendingFieldTypeMap[fieldName] = new CType.Sized(nestedType);
        return $"public {nestedType} {Id(fieldName)};\n";
    }

    // Emit a plain (sequential-layout) `unsafe struct name { … }` — the body
    // shape StructDef produces, factored out for the named-nested-struct synth
    // type. Deduped by name across TUs via _emittedTypes.
    private void EmitSequentialStructType(string name, string memberLines)
    {
        if (!_emittedTypes.Add(name)) { return; }
        var declText = "unsafe struct " + name + "\n{\n" + IndentEach(memberLines) + "}\n\n";
        _structs.Append(declText);
        _typeDecls[name] = declText;
    }

    private void DrainPendingFields(string typeName)
    {
        _structFields[typeName] = new List<string>(_pendingFields);
        _pendingFields.Clear();
        DrainFieldEnums(typeName);
        DrainFieldTypes(typeName);
        DrainInlineArrFields(typeName);
        DrainVolatileFields(typeName);
        DrainAtomicFields(typeName);
        DrainVolatilePointeeFields(typeName);
        DrainPromotions(typeName);
    }

    // Move the anon-union promotions collected while building this aggregate into
    // _promotedFields under its type name. Called from every field-draining path
    // (StructDef / UnionDef via DrainPendingFields, and the typedef-struct forms).
    private void DrainPromotions(string typeName)
    {
        if (_pendingPromotions.Count == 0) { return; }
        var map = _promotedFields.TryGetValue(typeName, out var existing)
            ? existing : (_promotedFields[typeName] = new Dictionary<string, string>(StringComparer.Ordinal));
        foreach (var (field, synth) in _pendingPromotions) { map[field] = synth; }
        _pendingPromotions.Clear();
    }
    // `struct ID` as a type reference — emit just the ID. C# doesn't use the
    // `struct` keyword in usage position (only in declaration), and the
    // generated struct decl shares the same name.
    public EmitContent Visit(C.TypeStruct n) => T(n.Arg1);

    // `enum ID` as a type reference — emit the enum tag name (a real C# enum
    // type shares it). The enum-typing synthesis inserts int↔enum casts at use
    // sites; the `struct`/`union` keyword likewise drops in usage position.
    public EmitContent Visit(C.TypeEnum n) => T(n.Arg1);

    // `union ID` as a type reference — emit just the ID. The
    // [StructLayout(LayoutKind.Explicit)] struct declaration shares the name.
    public EmitContent Visit(C.TypeUnion n) => T(n.Arg1);

    // `typeof(type)` (C23) — yields the operand type directly. (typeof_unqual is
    // folded onto the same terminal: dotcc already drops const/volatile.)
    public EmitContent Visit(C.TypeofType n) => T(n.Arg2);

    // `typeof(expr)` (C23) — yields the expression's type. Read the operand's
    // synthesized CType (the same layer `sizeof expr` uses): a Sized type (every
    // scalar, pointer, enum, struct value) gives its C# name directly. Array /
    // pointer-to-array operands don't have a clean C# type name (dotcc lowers a C
    // array to a `stackalloc` pointer), and an un-synthesized type (e.g. an
    // arithmetic result dotcc doesn't type yet) leaves the slot null — both fail
    // loudly rather than emit a wrong type.
    public EmitContent Visit(C.TypeofExpr n)
    {
        if (TyOf(n.Arg2) is CType.Sized s) { return s.CsType; }
        throw new CompileException(
            "typeof(expr): cannot determine the expression's type — only variables, "
            + "literals, casts, and simple expressions whose scalar/pointer/struct "
            + "type dotcc tracks are supported (arrays and untyped results aren't yet)");
    }

    // `union Name { Type f1; Type f2; … } ;` — emit a C# struct with
    // [StructLayout(LayoutKind.Explicit)] and [FieldOffset(0)] on each
    // member, giving C's overlapping-storage semantics. Reuses the
    // MemberList parsed for struct (one `Type ID ;` per member).
    public EmitContent Visit(C.UnionDef n) => EmitTopLevelUnionDef(T(n.Arg1), T(n.Arg3));

    // Shared by the file-scope (UnionDef) and block-scope (StmtUnionDef) union
    // definitions — same hoist-and-dedup as the struct case.
    private EmitContent EmitTopLevelUnionDef(string name, string members)
    {
        DrainPendingFields(name);
        EmitExplicitUnionType(name, members);
        return string.Empty;
    }

    // Block-scope (local) aggregate type definitions — `struct/union/enum Tag {
    // … };` used as a statement (see the `stmt*Def` grammar productions). A type
    // has no storage, so each delegates to the SAME emit as its file-scope
    // counterpart: the type hoists into the top-level type section (deduped by
    // tag) and the statement itself emits nothing.
    public EmitContent Visit(C.StmtStructDef n) => EmitTopLevelStructDef(T(n.Arg1), T(n.Arg3));
    public EmitContent Visit(C.StmtUnionDef n)  => EmitTopLevelUnionDef(T(n.Arg1), T(n.Arg3));
    public EmitContent Visit(C.StmtEnumDef n)   => EmitEnum(T(n.Arg1), EI(n.Arg3), "int");
    public EmitContent Visit(C.StmtEnumDefTyped n)
    {
        Gate(2023, "enum with fixed underlying type (`enum : T`)", n.Arg0);  // C23
        return EmitEnum(T(n.Arg1), EI(n.Arg5), T(n.Arg3));
    }

    // Emit a C# struct with [StructLayout(Explicit)] + [FieldOffset(0)] on each
    // member (overlapping storage = C union). `memberLines` are `public T NAME;`
    // lines from StructMember. Shared by `union` defs and the nested type a C11
    // anonymous-union member lifts its fields into.
    private void EmitExplicitUnionType(string name, string memberLines)
    {
        _unionTypes.Add(name);  // record kind even when the text is deduped (layout model)
        if (!_emittedTypes.Add(name)) { return; } // already emitted (shared header across TUs)
        var sb = new StringBuilder();
        sb.Append("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit)]\n");
        sb.Append("unsafe struct ").Append(name).Append("\n{\n");
        foreach (var line in memberLines.Split('\n'))
        {
            if (line.Length == 0) { continue; }
            // [FieldOffset] is legal only on storage (fields), not on a bit-field's
            // accessor PROPERTY (a `{ get; set; }` line) — that holds no storage and
            // its backing field already carries the offset. Pass property lines
            // through unattributed.
            if (!line.Contains('{')) { sb.Append("    [global::System.Runtime.InteropServices.FieldOffset(0)] "); }
            else { sb.Append("    "); }
            sb.Append(line).Append('\n');
        }
        sb.Append("}\n\n");
        var unionText = sb.ToString();
        _structs.Append(unionText);
        _typeDecls[name] = unionText;
    }

    // Map from enumerator name → containing enum name. Populated by
    // Visit(EnumDef); consulted by Visit(Var) so unqualified `Red` in user
    // code becomes `Color.Red` in the emitted C#. Keeps the C# namespace
    // clean (no top-level pollution by every enumerator) while preserving
    // the source-level convenience of writing the bare name.
    private readonly Dictionary<string, string> _enumerators = new(StringComparer.Ordinal);

    // Enumerator name → its resolved integer value (when known — auto-numbered or a
    // literal-valued enumerator). Lets an enumerator used as an integer constant
    // expression fold to a literal (e.g. an array bound `T a[TM_N]`).
    private readonly Dictionary<string, int> _enumeratorValues = new(StringComparer.Ordinal);

    // Set of enum tag names (the `Color` in `enum Color { … }`). Populated by
    // Visit(EnumDef); consulted to decide whether a variable's declared type is
    // an enum (so a read of it is enum-typed) and whether a `(T)` cast targets
    // an enum. TU-lifetime (enums are file-scope).
    private readonly HashSet<string> _enumTags = new(StringComparer.Ordinal);

    // Function (raw C name) → emitted C# return type, TU-lifetime. Populated at
    // each FnSig (StartFn); consulted by Visit(Call) to tag a call's result with
    // its enum type when the callee returns an enum, so the result reconciles in
    // a consuming context (e.g. `int x = next(c)` decays, `Color d = next(c)`
    // needs no cast). Misses only a call placed before the callee's definition.
    private readonly Dictionary<string, string> _fnReturnTypes = new(StringComparer.Ordinal);

    // Struct/union/typedef-struct field-name tracker. Same precedent as
    // `_enumerators`: visitor-time symbol table. StructMember pushes each
    // field's name onto `_pendingFields` during child-visit; the enclosing
    // StructDef / TypedefStruct / UnionDef drains it into `_structFields`
    // keyed by the type name. The struct-aggregate-init visitor consults
    // this so `Point p = {1, 2};` lands as `Point p = new Point { x = 1, y = 2 };`
    // (C# has no positional-init form for structs — it needs named members).
    private readonly List<string> _pendingFields = new();
    private readonly Dictionary<string, List<string>> _structFields = new(StringComparer.Ordinal);

    // Per-struct enum-typed fields: structType → (fieldName → enum type name).
    // Lets a member access of an enum field be enum-typed (so it decays in
    // operators and reconciles at sinks, exactly like an enum variable) —
    // closing the "struct enum field isn't enum-typed on read" gap. Populated
    // by StructMember into _pendingFieldEnumMap, drained per struct.
    private readonly Dictionary<string, Dictionary<string, string>> _structFieldEnums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingFieldEnumMap = new(StringComparer.Ordinal);

    // Per-struct/union field CType, so a member access `s.field` / `p->field`
    // carries the field's type — enabling `sizeof` of a member (and of `*member`
    // / `member[i]`) and nested member chains (`L->stack.p`). Populated by
    // StructMemberList into _pendingFieldTypeMap, drained per struct (same shape
    // as _structFieldEnums). Only scalar/pointer/struct-valued fields; array
    // members carry their shape via _structMultiDimMembers / _structInlineArrFields.
    private readonly Dictionary<string, Dictionary<string, CType>> _structFieldTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CType> _pendingFieldTypeMap = new(StringComparer.Ordinal);

    // C# type names already emitted into the type-decls section. Emitter-lifetime
    // (NOT reset per function): when several translation units `#include` the same
    // header, each TU re-parses the shared `struct`/`union`/`enum`/typedef, so the
    // emit would otherwise produce duplicate C# types (CS0101). The side tables
    // (_structFields/_enumTags/…) are populated by the first definition and stay
    // valid; only the duplicate C# text is suppressed.
    private readonly HashSet<string> _emittedTypes = new(StringComparer.Ordinal);

    // Snapshot the pending enum-field map onto each of the type's names and
    // reset it for the next struct body.
    private void DrainFieldEnums(params string[] typeNames)
    {
        if (_pendingFieldEnumMap.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structFieldEnums[tn] = new Dictionary<string, string>(_pendingFieldEnumMap, StringComparer.Ordinal);
            }
            _pendingFieldEnumMap.Clear();
        }
    }

    // Snapshot the pending field-CType map onto each of the type's names and
    // reset it for the next aggregate body (same shape as DrainFieldEnums).
    private void DrainFieldTypes(params string[] typeNames)
    {
        if (_pendingFieldTypeMap.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structFieldTypes[tn] = new Dictionary<string, CType>(_pendingFieldTypeMap, StringComparer.Ordinal);
            }
            _pendingFieldTypeMap.Clear();
        }
    }

    // Drain the pending InlineArray-member map under each aggregate name (same
    // shape as DrainFieldEnums), so a member-access of such a field can later
    // rewrite its subscript to pointer arithmetic.
    private void DrainInlineArrFields(params string[] typeNames)
    {
        if (_pendingInlineArrFields.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structInlineArrFields[tn] =
                    new Dictionary<string, (string, int)>(_pendingInlineArrFields, StringComparer.Ordinal);
            }
            _pendingInlineArrFields.Clear();
        }
        if (_pendingMultiDimMembers.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structMultiDimMembers[tn] =
                    new Dictionary<string, (string, bool, CType)>(_pendingMultiDimMembers, StringComparer.Ordinal);
            }
            _pendingMultiDimMembers.Clear();
        }
        if (_pendingFixedBufferFields.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structFixedBufferMembers[tn] = new HashSet<string>(_pendingFixedBufferFields, StringComparer.Ordinal);
            }
            _pendingFixedBufferFields.Clear();
        }
    }

    // Anonymous-union-member machinery. An anon `union { … };` member lifts its
    // inner fields into a generated nested explicit-layout type; those fields are
    // PROMOTED (accessed as `parent.field`, not `parent.synth.field`, in C). We
    // record, per containing aggregate, each promoted field → the synthetic field
    // name that holds the nested union, and rewrite member access accordingly.
    // _memberMarks: _pendingFields-count snapshots from MemberMark (epsilon), so
    // AnonUnionMember can isolate just its inner fields. _pendingPromotions: the
    // promotions for the aggregate currently being built, drained at DrainPending.
    private readonly Stack<int> _memberMarks = new();
    private int _anonAggCounter;
    private readonly List<(string Field, string Synth)> _pendingPromotions = new();
    private readonly Dictionary<string, Dictionary<string, string>> _promotedFields = new(StringComparer.Ordinal);

    // `enum Name { A, B = 5, C } ;` — emit a real C# `enum Name : int { … }`
    // into the type-decl side channel. A genuine enum (not the old `static class
    // { const int }`) preserves the C type name and makes `switch`/`case`,
    // type-safe params, and ToString work; the int↔enum casts C requires but C#
    // doesn't (`int x = c`, `c & MASK`, `Color c = 2`) are inserted by the
    // enum-typing synthesis at the consuming nodes. Enum members are compile-time
    // constants in C#, so `case Red:` and `int a[Blue]` still work. C# enum
    // auto-numbering matches C (start 0, prev+1), but we emit each resolved value
    // explicitly so a non-literal initializer (`1 << 2`, `A + 1`) round-trips.
    // Returns empty — type decls live in the side channel, after statements.
    public EmitContent Visit(C.EnumDef n) => EmitEnum(T(n.Arg1), EI(n.Arg3), "int");

    // C23 `enum Name : Type { … }` — the fixed underlying type. The Type
    // non-terminal already resolves an integer specifier to a C# integral type
    // (byte/sbyte/short/ushort/int/uint/long/ulong), each of which is a valid C#
    // enum base, so it passes straight through as the base. rhs indices:
    // enum(0) ID(1) :(2) Type(3) {(4) EnumList(5) }(6) ;(7).
    public EmitContent Visit(C.EnumDefTyped n)
    {
        Gate(2023, "enum with fixed underlying type (`enum : T`)", n.Arg0);  // C23
        return EmitEnum(T(n.Arg1), EI(n.Arg5), T(n.Arg3));
    }

    // `typedef enum { … } Alias ;` (anonymous) — emit the C# enum under the alias
    // name (C# has no separate tag namespace). `typedef enum Tag { … } Alias ;`
    // (tagged) — emit under the alias too, and bind `using Tag = Alias;` + mark
    // Tag an enum tag, so later `enum Tag` references resolve. rhs indices —
    // anon: enum(1) {(2) EnumList(3) }(4) Alias(5); tagged: enum(1) Tag(2) {(3)
    // EnumList(4) }(5) Alias(6).
    public EmitContent Visit(C.TypedefEnumAnon n) => EmitEnum(T(n.Arg5), EI(n.Arg3), "int");
    public EmitContent Visit(C.TypedefEnum n)
    {
        var tag = T(n.Arg2);
        var alias = T(n.Arg6);
        var result = EmitEnum(alias, EI(n.Arg4), "int");
        if (tag != alias)
        {
            _enumTags.Add(tag);
            if (_aliasNames.Add(tag)) { _aliases.Append("using ").Append(tag).Append(" = ").Append(alias).Append(";\n"); }
        }
        return result;
    }

    // Shared by plain `enum Name { … }` (base int) and the C23 fixed-underlying
    // form `enum Name : T { … }` (base = mapped C# integral type).
    private EmitContent EmitEnum(string enumName, IReadOnlyList<string> items, string baseType)
    {
        _enumTags.Add(enumName);
        // Duplicate definition (same enum from a header included by 2 TUs): the
        // first call populated _enumTags/_enumerators; skip re-emitting the type.
        if (!_emittedTypes.Add(enumName)) { return string.Empty; }
        var sb = new StringBuilder();
        sb.Append("enum ").Append(enumName).Append(" : ").Append(baseType).Append("\n{\n");
        var next = 0L;
        foreach (var raw in items)
        {
            var eq = raw.IndexOf('=');
            string itemName;
            string valueText;
            long? value = null;  // the resolved integer value, when known
            if (eq < 0)
            {
                itemName = raw;
                value = next;
                valueText = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                next++;
            }
            else
            {
                itemName = raw[..eq];
                var expr = raw[(eq + 1)..];
                // When the explicit value is a literal int we use it as the
                // numeric basis for downstream auto-numbering. If the
                // expression isn't a plain literal (e.g. `1 << 2`), emit it
                // verbatim and best-effort advance `next` by 1.
                if (long.TryParse(expr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                    valueText = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    next = parsed + 1;
                }
                else
                {
                    valueText = expr;
                    next++;
                }
            }
            _enumerators[itemName] = enumName;  // raw key — Visit(Var) looks up by raw name
            // Remember the integer value so an enumerator used as an integer constant
            // expression (e.g. an array bound `T a[TM_N]`) folds to a literal.
            if (value is >= int.MinValue and <= int.MaxValue) { _enumeratorValues[itemName] = (int)value.Value; }
            sb.Append("    ").Append(Id(itemName)).Append(" = ").Append(valueText).Append(",\n");
        }
        sb.Append("}\n\n");
        var enumText = sb.ToString();
        _structs.Append(enumText);
        _typeDecls[enumName] = enumText;
        return string.Empty;
    }

    // EnumList accumulator — produces a typed EmitContent.EnumItems list.
    // Each element is either "name" (no explicit value) or "name=expr" so
    // EnumDef can split with one IndexOf('='). No sentinel chars.
    public EmitContent Visit(C.EnumListOne n) => new EmitContent.EnumItems(new[] { T(n.Arg0) });
    public EmitContent Visit(C.EnumListCons n)
    {
        var prev = EI(n.Arg0);
        var next = T(n.Arg2);
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(next);
        return new EmitContent.EnumItems(combined);
    }
    public EmitContent Visit(C.EnumItem n)     => T(n.Arg0);
    public EmitContent Visit(C.EnumItemInit n) => $"{T(n.Arg0)}={T(n.Arg2)}";

    // `Type -> TYPE_NAME` — the rewriter-synthesised terminal carrying a
    // typedef'd name. The Content is the raw identifier string; emit it
    // verbatim since the using-alias (or struct decl) we emitted for the
    // typedef already binds that name in C#'s namespace.
    public EmitContent Visit(C.TypeName n)
    {
        var name = T(n.Arg0);
        // An `_Atomic` typedef alias (atomic_int, …) resolves to its underlying C#
        // type WITH the Atomic flag, so a decl using the alias lowers like `_Atomic T`.
        if (_atomicTypedefs.TryGetValue(name, out var under)) { return new EmitContent.Text(under, Atomic: true); }
        return name;
    }

    // `TypeSpecList TYPE_NAME` — a function-specifier / qualifier run preceding a
    // typedef-name (`inline Table *f()`, `static inline OpCode g()`). The TYPE_NAME
    // is the base type; the spec-list contributes only its inline / _Noreturn flags.
    // Any real type keyword in the run (invalid C — `int Table`) is dropped. Mirrors
    // Visit(C.TypeName) for the _Atomic-typedef-alias resolution. See the grammar note.
    public EmitContent Visit(C.TypeSpecThenName n)
    {
        var specs = S(n.Arg0);
        var hasInline = false;
        var hasNoreturn = false;
        foreach (var s in specs)
        {
            if (s == "inline") { hasInline = true; }
            else if (s == "_Noreturn") { hasNoreturn = true; if (_dialectGate is not null) { Gate(2011, "_Noreturn", n.Arg0); } }
        }
        var name = T(n.Arg1);
        var atomic = _atomicTypedefs.TryGetValue(name, out var under);
        return new EmitContent.Text(atomic ? under! : name, Inline: hasInline, Noreturn: hasNoreturn, Atomic: atomic);
    }

    // `volatile T` (leading qualifier prefix). Drop the qualifier from the emitted
    // C# type string — there is no C# type-level `volatile` — but FLAG the Type so
    // the decl/member visitors record the declared name/field as volatile (see
    // EmitDecl / StructMemberList). Reads/writes of a volatile lvalue then lower to
    // Volatile.Read/Volatile.Write at the access site (Var / MemberDot / MemberArrow
    // + the write-context parents). Preserve any inner flags (inline/noreturn/Ty)
    // — they don't co-occur with volatile in practice, but a Type prefix shouldn't
    // silently drop them. (Pointer-to-volatile — `volatile int *p` — attaches the
    // flag to the outer pointer for now; the pointee-fencing case is phase V2.)
    public EmitContent Visit(C.TypeVolatile n)
    {
        var text = T(n.Arg1);
        return n.Arg1.Content is EmitContent.Text inner
            ? inner with { Volatile = true }
            : new EmitContent.Text(text, Volatile: true);
    }

    // `_Atomic T` / `_Atomic(T)` (C11) — leading/parenthesized atomic specifier.
    // Drop the qualifier from the emitted C# type (no C#-level atomic type) but flag
    // the Type atomic; the decl/member visitors record the name/field so reads/writes
    // lower to the seq-cst Atomic.* helpers (see CSharpEmitter.Atomic). C11-gated.
    public EmitContent Visit(C.TypeAtomic n)
    {
        Gate(2011, "_Atomic", n.Arg0);  // C11
        var text = T(n.Arg1);
        return n.Arg1.Content is EmitContent.Text inner
            ? inner with { Atomic = true }
            : new EmitContent.Text(text, Atomic: true);
    }
    public EmitContent Visit(C.TypeAtomicParen n)
    {
        Gate(2011, "_Atomic", n.Arg0);  // C11
        var text = T(n.Arg2);
        return n.Arg2.Content is EmitContent.Text inner
            ? inner with { Atomic = true }
            : new EmitContent.Text(text, Atomic: true);
    }

    // `typedef Type ID ;` — register an `using unsafe Alias = Type;` line in
    // the aliases side channel. Suppressed when Alias == Type (e.g.
    // `typedef struct Foo Foo;` where Type already lowers to `Foo`) since
    // C# rejects a self-alias and the struct named Foo already exists.
    // Suppressed too when the alias was already emitted earlier in the same
    // translation unit (deduplication — real C allows redeclaration to the
    // same type, real C# rejects duplicate aliases).
    public EmitContent Visit(C.TypedefAlias n)
    {
        var rawType = T(n.Arg1);
        var type = QualifyPredefinedTypeName(rawType);
        var alias = T(n.Arg2);
        // `typedef _Atomic T Alias;` (e.g. <stdatomic.h>'s atomic_int) — record the
        // alias → underlying C# type and emit NO `using` alias: a use of `Alias`
        // resolves to the underlying type WITH the Atomic flag (Visit(TypeName)), so
        // `atomic_int x;` is treated exactly like `_Atomic int x;`.
        if (AtomicOf(n.Arg1)) { _atomicTypedefs[alias] = type; return string.Empty; }
        // Record alias → underlying (chained) so the fixed-buffer-element check and
        // sizeof folding can see through `typedef unsigned char lu_byte;` etc.
        if (alias != type) { _typedefUnderlying[alias] = ResolveTypedef(type); }
        // If the alias resolves (directly or transitively) to a predefined
        // reference type, record the alias so GlobalVar can auto-init
        // instances. `jmp_buf` → `LongJmpToken` is the canonical case.
        if (IsPredefinedRefTypeName(rawType) || _refTypeAliases.Contains(rawType))
        {
            _refTypeAliases.Add(alias);
        }
        if (alias != type && _aliasNames.Add(alias))
        {
            _aliases.Append("using unsafe ").Append(alias).Append(" = ").Append(type).Append(";\n");
        }
        return string.Empty;
    }

    /// <summary>
    /// Set of typedef'd alias names whose underlying type is a
    /// predefined C# reference type (currently just <c>LongJmpToken</c>
    /// from <c>&lt;setjmp.h&gt;</c>). Used by <see cref="Visit(C.GlobalVar)"/>
    /// to auto-instantiate the field — without an initializer the C#
    /// field would default to <c>null</c>, which breaks the longjmp
    /// exception filter that compares tokens by reference identity.
    /// </summary>
    private readonly HashSet<string> _refTypeAliases = new(StringComparer.Ordinal);

    private static bool IsPredefinedRefTypeName(string typeText)
    {
        foreach (var name in Compiler.PredefinedTypeNames)
        {
            if (typeText == name) { return true; }
        }
        return false;
    }

    /// <summary>
    /// In a <c>using unsafe Alias = X;</c> directive, C#'s name resolution
    /// for X does NOT consult <c>using static</c> directives in the same
    /// file — it only sees the enclosing namespace + type-alias usings.
    /// So a nested type like <c>Libc.LongJmpToken</c>, even with
    /// <c>using static Libc;</c> declared above, doesn't resolve as the
    /// bare <c>LongJmpToken</c> when used as the RHS of a type alias.
    /// Qualify it. The PredefinedTypeNames list (see Compiler) is small
    /// and known; we prefix those with <c>Libc.</c> when emitting alias
    /// directives. Inside method bodies the bare name still works via
    /// <c>using static</c>, so this only affects the alias-emit path.
    /// </summary>
    private static string QualifyPredefinedTypeName(string type)
    {
        foreach (var name in Compiler.PredefinedTypeNames)
        {
            if (type == name) { return "Libc." + name; }
        }
        return type;
    }

    // `typedef Ret (*Name)(args);` → `using unsafe Name = delegate*<args, Ret>;`.
    // C# function-pointer types put the return type LAST in the type arg
    // list (opposite of C's "return type first" syntax). The visitor strips
    // parameter names from the ParamList — C# function pointers are
    // by-type-only — by splitting on commas and dropping the trailing ID
    // from each "Type ID" chunk.
    public EmitContent Visit(C.TypedefFnPtr n)
    {
        var ret = T(n.Arg1);
        var name = T(n.Arg4);
        var pars = T(n.Arg7);
        var typesOnly = StripParamNames(pars);
        _pointerTypedefNames.Add(name);  // a function pointer sizes to 8/8 (layout model)
        // A function-pointer TYPE's parameters aren't real parameters of any
        // function definition — but the Param visitors still staged them into
        // _pendingParams. Discard them: only an FnSig's StartFn should adopt
        // staged params, and leaking these would corrupt the next function's
        // parameter scope (its names + local-rename used-set).
        _pendingParams.Clear();
        if (_aliasNames.Add(name))  // dedup across TUs (shared header)
        {
            _aliases.Append("using unsafe ").Append(name).Append(" = delegate*<")
                .Append(typesOnly).Append(", ").Append(ret).Append(">;\n");
        }
        return string.Empty;
    }

    public EmitContent Visit(C.TypedefFnPtrNoArgs n)
    {
        var ret = T(n.Arg1);
        var name = T(n.Arg4);
        _pointerTypedefNames.Add(name);  // a function pointer sizes to 8/8 (layout model)
        if (_aliasNames.Add(name))  // dedup across TUs (shared header)
        {
            _aliases.Append("using unsafe ").Append(name).Append(" = delegate*<")
                .Append(ret).Append(">;\n");
        }
        return string.Empty;
    }

    private static string StripParamNames(string paramList)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var p in paramList.Split(", "))
        {
            // Each "Type ID" — last space separates type from name. Type
            // emission has no internal spaces (`int**` not `int * *`), so
            // taking everything before the last space is safe.
            var sp = p.LastIndexOf(' ');
            var typeOnly = sp < 0 ? p : p[..sp];
            if (!first) { sb.Append(", "); }
            sb.Append(typeOnly);
            first = false;
        }
        return sb.ToString();
    }

    // `typedef struct ID { MemberList } ID ;` — emit the struct under the
    // alias name (the trailing ID). When tag != alias, also bind the tag as
    // a `using` alias so code using `struct Tag` typeref form also resolves.
    public EmitContent Visit(C.TypedefStruct n)
    {
        var tag = T(n.Arg2);
        var members = T(n.Arg4);
        var alias = T(n.Arg6);
        // Index fields under BOTH the alias and the tag — code may refer to
        // the type by either name (and `using unsafe Tag = Alias;` below
        // makes the tag a real type reference too).
        var fields = new List<string>(_pendingFields);
        _structFields[alias] = fields;
        if (tag != alias) { _structFields[tag] = fields; }
        _pendingFields.Clear();
        DrainFieldEnums(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainFieldTypes(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainInlineArrFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainVolatileFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainAtomicFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainVolatilePointeeFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainPromotions(alias);
        if (tag != alias && _promotedFields.TryGetValue(alias, out var aliasProm)) { _promotedFields[tag] = aliasProm; }
        if (_emittedTypes.Add(alias))
        {
            var declText = "unsafe struct " + alias + "\n{\n" + IndentEach(members) + "}\n\n";
            _structs.Append(declText);
            _typeDecls[alias] = declText;
        }
        if (tag != alias && _aliasNames.Add(tag))
        {
            _aliases.Append("using unsafe ").Append(tag).Append(" = ").Append(alias).Append(";\n");
        }
        return string.Empty;
    }

    // `typedef struct { MemberList } ID ;` — anonymous (tagless) struct + alias.
    // Same as TypedefStruct minus the tag: emit the C# struct under the alias
    // name and index its fields under the alias only (there's no `struct Tag`
    // form to bind). The trailing ID before `;` is what TypeNameRewriter binds
    // as the typedef-name, so later `Alias x;` lexes as a declaration.
    public EmitContent Visit(C.TypedefStructAnon n)
    {
        var members = T(n.Arg3);
        var alias = T(n.Arg5);
        _structFields[alias] = new List<string>(_pendingFields);
        _pendingFields.Clear();
        DrainFieldEnums(alias);
        DrainFieldTypes(alias);
        DrainInlineArrFields(alias);
        DrainVolatileFields(alias);
        DrainAtomicFields(alias);
        DrainVolatilePointeeFields(alias);
        DrainPromotions(alias);
        if (_emittedTypes.Add(alias))
        {
            var declText = "unsafe struct " + alias + "\n{\n" + IndentEach(members) + "}\n\n";
            _structs.Append(declText);
            _typeDecls[alias] = declText;
        }
        return string.Empty;
    }

    // `typedef union Tag { MemberList } Alias ;` — union definition + alias.
    // The union counterpart of TypedefStruct: index fields under the alias (and
    // the tag, if different), emit the [StructLayout(Explicit)] union struct
    // under the alias, and bind the tag as a using-alias when it differs.
    public EmitContent Visit(C.TypedefUnion n)
    {
        var tag = T(n.Arg2);
        var members = T(n.Arg4);
        var alias = T(n.Arg6);
        var fields = new List<string>(_pendingFields);
        _structFields[alias] = fields;
        if (tag != alias) { _structFields[tag] = fields; }
        _pendingFields.Clear();
        DrainFieldEnums(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainFieldTypes(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainInlineArrFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainVolatileFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainAtomicFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainVolatilePointeeFields(tag != alias ? new[] { alias, tag } : new[] { alias });
        DrainPromotions(alias);
        if (tag != alias && _promotedFields.TryGetValue(alias, out var aliasProm)) { _promotedFields[tag] = aliasProm; }
        EmitExplicitUnionType(alias, members);
        if (tag != alias) { _unionTypes.Add(tag); }  // the tag names the same union (layout model)
        if (tag != alias && _aliasNames.Add(tag))
        {
            _aliases.Append("using unsafe ").Append(tag).Append(" = ").Append(alias).Append(";\n");
        }
        return string.Empty;
    }

    // `typedef union { MemberList } Alias ;` — anonymous (tagless) union + alias.
    // Same as TypedefUnion minus the tag binding.
    public EmitContent Visit(C.TypedefUnionAnon n)
    {
        var members = T(n.Arg3);
        var alias = T(n.Arg5);
        _structFields[alias] = new List<string>(_pendingFields);
        _pendingFields.Clear();
        DrainFieldEnums(alias);
        DrainFieldTypes(alias);
        DrainInlineArrFields(alias);
        DrainVolatileFields(alias);
        DrainAtomicFields(alias);
        DrainVolatilePointeeFields(alias);
        DrainPromotions(alias);
        EmitExplicitUnionType(alias, members);
        return string.Empty;
    }

    public EmitContent Visit(C.FnsCons n) =>
        T(n.Arg0) + ((T(n.Arg0)).Length > 0 ? "\n\n" : "") + T(n.Arg1);

    public EmitContent Visit(C.FnsOne n) => T(n.Arg0);

    // Params
    public EmitContent Visit(C.Param n)
    {
        var type = T(n.Arg0);
        var name = T(n.Arg1);
        _pendingParams.Add((name, type));
        NoteVolatileParam(n.Arg0, type, name);  // `volatile int x` / `volatile int *reg` param
        return $"{type} {Id(name)}";
    }
    // Unnamed (abstract) parameter — `int f(int, char*)` or a function-pointer
    // type's params. C# requires a parameter name, so synthesize a unique one
    // (`_p0`, `_p1`, …). The counter only needs to be unique within a list and
    // deterministic across the analysis/emit passes — a monotonic counter is
    // both. For a fn-ptr type the name is dropped by StripParamNames; for a real
    // prototype/definition the body can't reference it (matching C).
    public EmitContent Visit(C.ParamUnnamed n)
    {
        var type = T(n.Arg0);
        var name = "_p" + _unnamedParamSeq++;
        _pendingParams.Add((name, type));
        return $"{type} {Id(name)}";
    }
    private int _unnamedParamSeq;

    // Function-pointer parameter — `int (*cmp)(int, int)`. Lowers to a
    // `delegate*<args…, ret>` parameter (return type last, as C# requires).
    public EmitContent Visit(C.ParamFnPtr n)        => ParamFnPtr(T(n.Arg0), T(n.Arg3), T(n.Arg6));
    public EmitContent Visit(C.ParamFnPtrNoArgs n)  => ParamFnPtr(T(n.Arg0), T(n.Arg3), "");

    private EmitContent ParamFnPtr(string ret, string name, string pars)
    {
        // The fn-ptr TYPE's own params just staged into _pendingParams (each
        // inner Param visit appended one) — they belong to the pointed-to type,
        // not the enclosing function. Pop them off the end and stage the fn-ptr
        // parameter itself instead.
        var innerCount = pars.Length == 0 ? 0 : pars.Split(',').Length;
        for (var k = 0; k < innerCount && _pendingParams.Count > 0; k++)
        {
            _pendingParams.RemoveAt(_pendingParams.Count - 1);
        }
        var type = pars.Length == 0 ? $"delegate*<{ret}>" : $"delegate*<{StripParamNames(pars)}, {ret}>";
        _pendingParams.Add((name, type));
        return $"{type} {Id(name)}";
    }
    // C array-parameter decay: `T arr[]` / `T arr[N]` ≡ `T* arr` per
    // C99 §6.7.5.3p7. The size in the sized form is informational only —
    // we discard it (intentionally don't evaluate Arg3) since C semantics
    // give the call site no way to observe a mismatch anyway.
    public EmitContent Visit(C.ParamArrayUnsized n) { _pendingParams.Add((T(n.Arg1), T(n.Arg0) + "*")); return $"{T(n.Arg0)}* {Id(T(n.Arg1))}"; }
    public EmitContent Visit(C.ParamArraySized n) { _pendingParams.Add((T(n.Arg1), T(n.Arg0) + "*")); return $"{T(n.Arg0)}* {Id(T(n.Arg1))}"; }
    public EmitContent Visit(C.ParamsCons n) => $"{T(n.Arg0)}, {T(n.Arg2)}";
    public EmitContent Visit(C.ParamsOne n) => T(n.Arg0);
    // A C variadic function `T f(fixed…, ...)` lowers to a trailing
    // `params VaArg[] _va`. VaArg (a Libc value type with implicit conversions
    // from every C arg type, pointers included) lets C# overload resolution
    // marshal each variadic actual at the call site with no boxing — object[]
    // couldn't carry raw pointers. va_start/va_arg/va_end read back through it.
    public EmitContent Visit(C.ParamsVararg n) => $"{T(n.Arg0)}, params VaArg[] _va";

    // Types — pointer composition + tag types stay direct; everything that
    // accumulates declaration specifiers (signed/unsigned, short/long, int/
    // char/float/double/void) goes through TypeSpec → TypeSpecList →
    // ResolveTypeSpec, matching how real C compilers handle the
    // free-order specifier sequence.
    public EmitContent Visit(C.TypePtr n)
    {
        var text = $"{T(n.Arg0)}*";
        // `volatile T *` — the POINTEE is volatile, so a deref/subscript of this
        // pointer is a volatile lvalue (phase V2). The pointee-volatile flag rides
        // forward as VolatilePointee; carry it through further pointer levels too
        // (`volatile T **`). Reading the inner Type's Volatile flag here is the
        // grammar's `(volatile T)*` binding — volatile binds to the base, then `*`,
        // matching C (`volatile int *p` is pointer-to-volatile-int, not a volatile
        // pointer; the latter is the `int * volatile p` ptr-qual form).
        var inner = n.Arg0.Content as EmitContent.Text;
        var pointeeVolatile = VolatileOf(n.Arg0) || inner?.VolatilePointee == true;
        // Carry the return-type's function-specifier flags forward through pointer
        // levels: a pointer-returning `inline`/`_Noreturn` function (`inline Table
        // *f()`) keeps its [MethodImpl]/[DoesNotReturn] — the flag rides the Type.
        var inline = inner?.Inline == true;
        var noreturn = inner?.Noreturn == true;
        return (pointeeVolatile || inline || noreturn)
            ? new EmitContent.Text(text, Inline: inline, Noreturn: noreturn, VolatilePointee: pointeeVolatile)
            : (EmitContent)text;
    }
    // `T * const` / `T * volatile` / `T * restrict` — a qualifier after the
    // pointer star. dotcc has no C# equivalent (no readonly locals, no aliasing
    // model), so the qualifier is dropped: the type is just the pointer. (A
    // future optimization could turn a `restrict` parameter into a by-ref /
    // no-alias hint.)
    public EmitContent Visit(C.TypePtrQualConst n)    => $"{T(n.Arg0)}*";
    public EmitContent Visit(C.TypePtrQualVolatile n) => $"{T(n.Arg0)}*";
    public EmitContent Visit(C.TypePtrQualRestrict n) => $"{T(n.Arg0)}*";

    // Each TypeSpec keyword maps to its own bracketed marker — `<int>`,
    // `<unsigned>`, `<_Bool>` etc. Bracketing makes the markers
    // self-delimiting (no opaque single-char shorthand to memorise) and
    // makes accumulated lists trivially parseable: `<unsigned><long><int>`.
    // TypeSpecList concatenates; ResolveTypeSpec splits on `<...>` segments.
    // TypeSpec visitors emit single-element SpecList; TypeSpecList* accumulate
    // them. TypeFromSpec resolves the multiset to a final C# type name.
    // No more sentinel-encoded marker strings — the list IS the schema.
    private static EmitContent.SpecList Spec(string kw) => new(new[] { kw });

    public EmitContent Visit(C.TsInt n)      => Spec("int");
    public EmitContent Visit(C.TsChar n)     => Spec("char");
    public EmitContent Visit(C.TsFloat n)    => Spec("float");
    public EmitContent Visit(C.TsDouble n)   => Spec("double");
    public EmitContent Visit(C.TsVoid n)     => Spec("void");
    public EmitContent Visit(C.TsShort n)    => Spec("short");
    public EmitContent Visit(C.TsLong n)     => Spec("long");
    public EmitContent Visit(C.TsUnsigned n) => Spec("unsigned");
    public EmitContent Visit(C.TsSigned n)   => Spec("signed");
    public EmitContent Visit(C.TsBool n)     => Spec("_Bool");
    public EmitContent Visit(C.TsFloat128 n) => Spec("Float128");
    // `const` type qualifier — accumulates into the spec list but ResolveTypeSpec
    // has no case for it, so it's silently dropped (C# has no equivalent).
    // `const char *p` lowers exactly like `char *p`. (`volatile` is NOT a TypeSpec
    // — it's the leading `Type → 'volatile' Type` prefix; see Visit(TypeVolatile).)
    public EmitContent Visit(C.TsConst n)    => Spec("const");
    // Function specifier — accumulated in the spec list like a qualifier, but
    // (unlike const/volatile) it is NOT silently dropped: TypeFromSpec detects
    // it and flags the resolved Type so the FnSig path emits AggressiveInlining.
    public EmitContent Visit(C.TsInline n)   => Spec("inline");
    // `_Noreturn` (C11) — same handling as inline: accumulated, dropped from the
    // resolved type, flagged by TypeFromSpec so the FnSig path emits [DoesNotReturn].
    public EmitContent Visit(C.TsNoreturn n) => Spec("_Noreturn");
    // `_Complex` (C99) — accumulated; ResolveTypeSpec maps a float base + _Complex
    // to System.Numerics.Complex.
    public EmitContent Visit(C.TsComplex n) => Spec("_Complex");

    public EmitContent Visit(C.TypeSpecListOne n)  => S(n.Arg0) is var specs
        ? new EmitContent.SpecList(specs) : throw new InvalidOperationException();

    public EmitContent Visit(C.TypeSpecListCons n)
    {
        var prev = S(n.Arg0);
        var next = S(n.Arg1);
        var combined = new List<string>(prev.Count + next.Count);
        combined.AddRange(prev);
        combined.AddRange(next);
        return new EmitContent.SpecList(combined);
    }

    public EmitContent Visit(C.TypeFromSpec n)
    {
        var specs = S(n.Arg0);
        // `inline` (C99) needs NO DialectGate: unlike `_Bool`/`long long` (always
        // lexed as keywords, so they reach here under any -std and `-pedantic`
        // rejects them), `inline` is rule-2 promoted by DialectKeywordRewriter
        // only from the C99 era on. Under c90 it stays an identifier, so
        // `inline int f()` is a parse error before this point — the dialect
        // rejection is structural, and a Gate(1999, …) here could never fire.
        // Dialect gates for type-specifier features (once per resolved type,
        // with a source line). `_Bool`/`long long` are C99; `_Float128` is C23.
        var hasInline = false;
        var hasNoreturn = false;
        var longs = 0;
        foreach (var s in specs)
        {
            if (s == "inline") { hasInline = true; }
            else if (s == "_Noreturn") { hasNoreturn = true; if (_dialectGate is not null) { Gate(2011, "_Noreturn", n.Arg0); } }
            else if (_dialectGate is not null)
            {
                if (s == "_Bool") { Gate(1999, "_Bool", n.Arg0); }
                else if (s == "Float128") { Gate(2023, "_Float128 / __float128", n.Arg0); }
                else if (s == "_Complex") { Gate(1999, "_Complex", n.Arg0); }
                else if (s == "long") { longs++; }
            }
        }
        if (_dialectGate is not null && longs >= 2) { Gate(1999, "long long", n.Arg0); }
        var resolved = ResolveTypeSpec(specs);
        // Carry the inline / _Noreturn flags up on the Type so the FnSig visitor
        // can read them; the resolved type string itself never mentions either.
        return hasInline || hasNoreturn
            ? new EmitContent.Text(resolved, Inline: hasInline, Noreturn: hasNoreturn)
            : (EmitContent)resolved;
    }

    /// <summary>
    /// Resolve a declaration-specifier marker string (concatenated by
    /// TypeSpec/TypeSpecList visitors) to a C# type name. Order-insensitive:
    /// `long unsigned int` and `unsigned int long` both produce <c>"LUi"</c>
    /// which resolves to <c>"ulong"</c>. Long and long-long both map to
    /// C# <c>long</c> (64-bit unconditionally in C#) — dotcc accepts the
    /// MSVC 32-bit `long` semantic loss as a documented quirk.
    /// </summary>
    private static string ResolveTypeSpec(IReadOnlyList<string> specs)
    {
        // Single-pass count of each specifier class. Duplicates AND
        // contradictions surface in the same loop. The input list IS the
        // typed schema (EmitContent.SpecList) — no string encoding, no
        // regex parsing, no Contains brittleness.
        var unsignedCount = 0;
        var signedCount = 0;
        var shortCount = 0;
        var longCount = 0;
        var boolCount = 0;
        var float128Count = 0;
        var complexCount = 0;
        string? baseKw = null;
        var baseCount = 0;
        var baseConflict = false;

        foreach (var kw in specs)
        {
            switch (kw)
            {
                case "unsigned": unsignedCount++; break;
                case "signed":   signedCount++; break;
                case "short":    shortCount++; break;
                case "long":     longCount++; break;
                case "_Bool":    boolCount++; break;
                case "Float128": float128Count++; break;
                case "_Complex": complexCount++; break;
                case "int":
                case "char":
                case "float":
                case "double":
                case "void":
                    if (baseKw is null) { baseKw = kw; baseCount = 1; }
                    else if (baseKw == kw) { baseCount++; }
                    else { baseConflict = true; }
                    break;
            }
        }

        // Validation. Each rule mirrors a real-C diagnostic.
        if (boolCount > 0 && (boolCount > 1 || baseKw is not null
            || unsignedCount > 0 || signedCount > 0 || shortCount > 0 || longCount > 0))
        {
            throw new CompileException(
                $"`_Bool` cannot be combined with other type specifiers (got `{PrettySpecs(specs)}`)");
        }
        if (float128Count > 0 && (float128Count > 1 || boolCount > 0 || baseKw is not null
            || unsignedCount > 0 || signedCount > 0 || shortCount > 0 || longCount > 0))
        {
            throw new CompileException(
                $"`_Float128` cannot be combined with other type specifiers (got `{PrettySpecs(specs)}`)");
        }
        if (unsignedCount > 0 && signedCount > 0)
        {
            throw new CompileException(
                $"cannot combine `signed` and `unsigned` (got `{PrettySpecs(specs)}`)");
        }
        if (unsignedCount > 1)
        {
            throw new CompileException(
                $"duplicate `unsigned` specifier (got `{PrettySpecs(specs)}`)");
        }
        if (signedCount > 1)
        {
            throw new CompileException(
                $"duplicate `signed` specifier (got `{PrettySpecs(specs)}`)");
        }
        if (shortCount > 0 && longCount > 0)
        {
            throw new CompileException(
                $"cannot combine `short` and `long` (got `{PrettySpecs(specs)}`)");
        }
        if (shortCount > 1)
        {
            throw new CompileException(
                $"duplicate `short` specifier (got `{PrettySpecs(specs)}`)");
        }
        if (longCount > 2)
        {
            throw new CompileException(
                $"cannot have more than two `long`s (got `{PrettySpecs(specs)}`)");
        }
        if (baseConflict)
        {
            throw new CompileException(
                $"cannot combine multiple base types (got `{PrettySpecs(specs)}`)");
        }
        if (baseCount > 1)
        {
            throw new CompileException(
                $"duplicate `{baseKw}` specifier (got `{PrettySpecs(specs)}`)");
        }
        if (complexCount > 0)
        {
            // `_Complex` requires a floating base (`float`/`double`/`long double`)
            // and no sign/short/_Bool/_Float128. (`long double _Complex` is the
            // [long, double, _Complex] form — allowed via the double base.)
            if (complexCount > 1 || baseKw is not ("float" or "double")
                || boolCount > 0 || float128Count > 0
                || unsignedCount > 0 || signedCount > 0 || shortCount > 0)
            {
                throw new CompileException(
                    $"`_Complex` requires a `float`, `double`, or `long double` base (got `{PrettySpecs(specs)}`)");
            }
        }

        // `float` / `void` take no size or sign modifiers. `double` takes the
        // single size modifier C allows on a real type — `long double` — which
        // dotcc lowers to C# `double`. The CLI is dotcc's target "ABI", and
        // `double` is the widest IEEE float it offers: there's no wider managed
        // type to map to and no hardware `long double` width (x87 80-bit,
        // aarch64 binary128) to chase, since we emit IL, not native code. This
        // mirrors dotcc's existing `long long` → C# `long` collapse (both
        // 64-bit on the CLI) — a documented narrowing on platforms whose native
        // `long double` is wider; `_Float128` remains the route to true 128-bit.
        // `double` still rejects `short`, sign, and a second `long`.
        if (baseKw is "float" or "void")
        {
            if (unsignedCount > 0 || signedCount > 0 || shortCount > 0 || longCount > 0)
            {
                throw new CompileException(
                    $"`{baseKw}` cannot take size or sign modifiers (got `{PrettySpecs(specs)}`)");
            }
        }
        else if (baseKw == "double")
        {
            if (unsignedCount > 0 || signedCount > 0 || shortCount > 0)
            {
                throw new CompileException(
                    $"`double` cannot take sign or `short` modifiers (got `{PrettySpecs(specs)}`)");
            }
            if (longCount > 1)
            {
                throw new CompileException(
                    $"`long long double` is not a valid type (got `{PrettySpecs(specs)}`)");
            }
            // longCount == 1 → `long double`, accepted; resolves to `double` below.
        }

        // Resolve. Order: _Bool first (mutually exclusive), then non-int
        // bases, then char (with signedness), then sized-int family.
        if (boolCount == 1) { return "CBool"; }
        if (float128Count == 1) { return "Float128"; }
        // Any `<float> _Complex` → System.Numerics.Complex (double-backed). The
        // shell imports System.Numerics, so the bare name resolves.
        if (complexCount == 1) { return "Complex"; }
        if (baseKw == "float")  { return "float"; }
        if (baseKw == "double") { return "double"; }  // incl. `long double`
        if (baseKw == "void")   { return "void"; }
        if (baseKw == "char")
        {
            // dotcc's `char` is `byte` (unsigned). `signed char` → sbyte.
            return signedCount > 0 ? "sbyte" : "byte";
        }
        if (shortCount > 0) { return unsignedCount > 0 ? "ushort" : "short"; }
        if (longCount > 0)  { return unsignedCount > 0 ? "ulong"  : "long"; }
        return unsignedCount > 0 ? "uint" : "int";
    }

    /// <summary>
    /// Render a typed specifier list back as space-separated C keywords,
    /// preserving the order the user wrote them. Used only for error
    /// messages so they read like a real compiler diagnostic.
    /// </summary>
    private static string PrettySpecs(IReadOnlyList<string> specs) =>
        string.Join(" ", specs);

}
