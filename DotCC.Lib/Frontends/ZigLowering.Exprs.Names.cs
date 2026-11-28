#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Expressions, names: identifiers and <c>base.field</c> access.
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live
/// in the main file.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lowers a bare identifier to the value, type or declaration it names.</summary>
    private CExpr LowerIdentExpr(Zig.Ident id)
    {
        var name = Tok(id.Arg0);
        // A const bound to a provable allocator (Milestone F/U) emitted no runtime decl; used
        // here as a VALUE (e.g. passed to a `std.mem.Allocator` parameter) it materializes the
        // matching fat pointer — `ZigAlloc.CHeap()` for the C-heap default, or
        // `ZigAlloc.FbaAllocator(&fba)` for a devirtualized `fba.allocator()` site. (As a
        // `.alloc`/`.free` RECEIVER it never reaches this case — LowerMethodCall
        // short-circuits to the devirt path.)
        if (_defaultAllocatorBindings.TryGetValue(name, out var boundKind))
        {
            return boundKind == AllocKind.Fba ? MaterializeFba(_fbaAllocatorSites[name]) : MaterializeCHeap();
        }
        if (_symbols.Resolve(name) is { } sym)
        {
            // A `comptime var`/`comptime const` (Milestone T) — substitute its CURRENT
            // lowering-time value as a literal, so an `inline while` condition / body folds.
            if (_comptimeVars.TryGetValue(sym, out var cv)) { return ComptimeVarLit(cv.Value, cv.Type); }
            // A comptime OPTIONAL (a `comptime x: ?T` seed, `const arg_pos = comptime switch (…) { .none => null, … }`):
            // `null` or its payload, as a literal the interpreter and a runtime use both read.
            if (_comptimeOptionalVars.TryGetValue(sym, out var co))
            {
                var optType = new CType.Optional(co.Inner);
                return co.HasValue
                    ? new Cast(optType, ComptimeVarLit(co.Value, co.Inner)) { Type = optType }
                    : new DefaultLit { Type = optType };
            }
            // A comptime STRING var (`comptime var literal: []const u8 = "";`): its current value.
            if (_comptimeStringVars.TryGetValue(sym, out var csv)) { return csv; }
            // A comptime AGGREGATE var (the comptime engine's E3): a live reference the interpreter
            // reads and mutates, rendered at runtime as the value it has here.
            if (_ir.ComptimeGlobals.TryGetValue(sym, out var agg))
            {
                return new ComptimeFold(new VarRef(sym) { Type = sym.Type, IsLValue = true })
                {
                    Type = sym.Type,
                    Live = true,
                    Resolved = _ir.SpliceComptimeValue(agg),
                };
            }
            return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
        }
        // A lazy module's top-level function named as a VALUE (`.drain = fixedDrain` in
        // std.Io.Writer.fixed): declared on demand, like a bare call to it, then a function
        // reference exactly as a root unit's is. A generic template has no one address to take.
        if (_lazy && EnsureDeclLowered(name) is { } lazyFn)
        {
            if (_genericFns.ContainsKey(lazyFn) || _typeReturningGenerics.ContainsKey(lazyFn))
            {
                throw new IrUnsupportedException(
                    $"zig: generic function '{name}' used as a value (it has no single address) is not supported");
            }
            return new VarRef(lazyFn) { Type = lazyFn.Type };
        }
        // A bare (unqualified) sibling container const (Milestone R, part 6): inside a
        // container const's RHS re-lower (`_currentConstContainer` set), an unresolved name may
        // name a SIBLING const — inline it (comptime), or one of an ENCLOSING container's, zig's
        // lexical scoping for a nested container. A METHOD body sees its container's consts the same
        // way (`self == slot_tombstone` in hash_map's Metadata). Outside those, the unresolved error holds.
        if (TryLowerSiblingMember(name, null) is { } sibling) { return sibling; }
        // A sibling FUNCTION named bare as a value, from the same scopes (std.Io.Writer.Allocating's
        // `.rebase = growingRebase` in its vtable const).
        for (var fc = _currentConstContainer ?? _currentContainer; fc is not null; fc = _containerParents.GetValueOrDefault(fc))
        {
            if (EnsureMethodDeclared(fc, name) is { Kind: SymKind.Func } siblingFn && !_genericFns.ContainsKey(siblingFn))
            {
                return new VarRef(siblingFn) { Type = siblingFn.Type };
            }
        }
        // A name bound to a COMPTIME value with no runtime symbol — today an `inline for`
        // capture over a member list of strings or enum values (road-to-zig-std S6), which
        // deliberately emits no `const`. Substituting the folded literal is what makes the
        // capture usable as an ordinary value (printed, compared, passed) inside the body.
        // Narrow by construction: an ordinary comptime `const` also emits its runtime decl,
        // so it resolves as a symbol above and never reaches here.
        if (_comptimeValues.TryGetValue(name, out var comptimeBound)) { return comptimeBound; }
        // A `const X = @compileError("…");` tombstone named in a VALUE position (road-to-zig-std
        // S7) — the declaration was inert; the reference is what zig analyses, so raise here.
        RaiseIfPoisoned(name);
        // A bound IMPORT reaching the value path means a navigation into it found nothing and
        // fell back to lowering the base. Saying so beats "unresolved identifier", which reads
        // as if the import itself were missing — and it is the failure a synthetic module
        // produces most (road-to-zig-std S3: `root` is empty by design, so every probe misses).
        if (_importSpecs.TryGetValue(name, out var importedSpec))
        {
            throw new IrUnsupportedException(
                $"zig: `{name}` is the imported module `{importedSpec}`, not a value — the declaration "
                + "named on it is one dotcc does not model");
        }
        // A comptime FUNCTION alias or parameter named as a value: its function's address.
        if (_fnAliases.TryGetValue(name, out var fnValue))
        {
            return new VarRef(fnValue.Sym) { Type = fnValue.Sym.Type };
        }
        // A lazy module's top-level value const (`use_vectors_for_comparison` in mem.zig).
        if (LowerLazyValueConst(name) is { } lazyConst) { return lazyConst; }
        RaiseIfSkippedDecl(name);   // declared here, but the declaration did not parse
        if (_externVars.Contains(name))
        {
            throw new IrUnsupportedException(
                $"zig: `{name}` is an `extern var`, data a linked library defines; dotcc binds extern functions, not extern data");
        }
        // `unreachable` (a keyword; an identifier in this grammar) as a statement or a statement
        // prong (`0 => unreachable,` in std.Io.Writer.print): zig's safe builds panic "reached
        // unreachable code", and C23's `unreachable()` already lowers to that loud throw.
        if (name == "unreachable") { return UnreachableCall(); }
        // The enclosing function is named, since a deep std wall is otherwise hard to place.
        throw new IrUnsupportedException(_currentFnName.Length > 0
            ? $"unresolved identifier '{name}' (in '{_currentFnName}')"
            : $"unresolved identifier '{name}'");
    }

    /// <summary>Lowers <c>base.field</c>: a container-namespace value, an enum member, a <c>@typeInfo</c> payload field or a struct field access.</summary>
    // `base.field` (Suffix '.' IDENT) — three meanings split here. When the base is a bare
    // identifier naming a container TYPE (not a variable): `Type.NAME` resolves to a
    // namespaced VALUE const if the container declares one (the comptime RHS, inlined fresh
    // here with its annotation as the sink); else `EnumName.member` → an EnumConstRef.
    // Otherwise it's struct field access on a value/pointer; Zig has no `->`, so `p.x` on a
    // pointer auto-derefs (emit `->`). The field type comes from the shared aggregate table.
    private CExpr LowerFieldExpr(Item expr, Zig.Field fld)
    {
        // A `@typeInfo(T)` payload field (road-to-zig-std S5) folds to a literal before any
        // runtime meaning is considered — the base is a comptime `std.builtin.Type` value, not
        // a variable or a container, so no other arm below could resolve it. The member-LIST
        // use (`field_names.len`) is checked first: its base is itself a payload field, so the
        // plain payload fold would see `.len` on a list and have nothing to say about it.
        if (TryFoldTypeInfoListValue(expr, out var tiListValue)) { return tiListValue; }
        if (TryFoldTypeInfoValue(expr, out var tiValue)) { return tiValue; }
        // A comptime constant a MODULE exports — `builtin.link_libc`, `builtin.cpu.arch`
        // (road-to-zig-std S3a). Restricted to module-rooted reads on purpose: a local struct
        // constant's field access still lowers to an ordinary runtime field read, so nothing
        // that worked before changes shape. A cross-module constant had no lowering at all.
        if (TryFoldImportedComptimeValue(expr, out var importedValue)) { return importedValue; }
        var fieldName = Tok(fld.Arg2);
        // A dotted std path used as a VALUE (Milestone F): the C-heap default
        // (`std.heap.page_allocator`/`c_allocator`) materializes a runtime Allocator; a std
        // TYPE used as a value, or any unmodeled std path, errors. (A `.alloc(…)` /
        // `.init(…)` CALL never reaches here — the callee Field goes through LowerMethodCall.)
        // `std.Target.x86.cpu.x86_64_v3` — a VALUE const (or an enum member) of a container ANOTHER
        // module declares, named through the module path: lowered in the owning module, where its
        // initializer resolves (the target-identity segment T3: a CPU model for builtin.cpu).
        if (fld.Arg0.Content is Zig.Field && !IsCuratedStdPath(fld.Arg0)
            && TryResolveModuleNestedType(fld.Arg0) is { Type: var moduleType, Owner: var moduleOwner })
        {
            if (moduleType.Unqualified is CType.Enum moduleEnum) { return moduleOwner.ResolveEnumLit(fieldName, moduleEnum); }
            if (ContainerTypeName(moduleType) is { } moduleContainer
                && moduleOwner._containerConsts.TryGetValue(moduleContainer, out var moduleConsts)
                && moduleConsts.TryGetValue(fieldName, out var moduleEntry))
            {
                return moduleOwner.LowerContainerConst(moduleContainer, fieldName, moduleEntry.typeItem, moduleEntry.rhs);
            }
        }
        // `builtin.cpu` as a VALUE (`cacheLineForCpu(builtin.cpu)`): a top-level value const of the module
        // the base names, lowered there on demand.
        if (!IsCuratedStdPath(fld.Arg0) && !TryResolveStdPath(expr, out _)
            && ResolveModulePath(fld.Arg0) is { Lowering: { } constModule }
            && constModule.LowerExportedValueConst(fieldName) is { } moduleConst)
        {
            return moduleConst;
        }
        // A module's top-level FUNCTION as a value (`const f = util.helper;`, `&root.helper`): declared on
        // demand in its own module, then a function reference exactly as a bare `helper` is.
        if (!IsCuratedStdPath(fld.Arg0) && !TryResolveStdPath(expr, out _)
            && ResolveModulePath(fld.Arg0) is { Lowering: { } fnModule }
            && fnModule.ResolveExportedDecl(fieldName, raiseIfSkipped: true) is { Sym.Kind: SymKind.Func } fnDecl)
        {
            if (fnDecl.Owner.IsGenericTemplate(fnDecl.Sym))
            {
                throw new IrUnsupportedException(
                    $"zig: generic function '{fieldName}' used as a value (it has no single address) is not supported");
            }
            return new VarRef(fnDecl.Sym) { Type = fnDecl.Sym.Type };
        }
        // `std.options.fmt_max_depth`: a FIELD of a module's value const (`pub const options: Options = …`
        // in std.zig), the const lowered in its own module and the field read off it.
        if (fld.Arg0.Content is Zig.Field { Arg0: var constModulePath, Arg2: var constNameTok }
            && !IsCuratedStdPath(constModulePath)
            && ResolveModulePath(constModulePath) is { Lowering: { } aggregateModule })
        {
            // A default-initialized const: only the field's default, so its struct need not lower whole.
            if (aggregateModule.LowerDefaultedConstField(Tok(constNameTok), fieldName) is { } defaulted) { return defaulted; }
            if (aggregateModule.LowerExportedValueConst(Tok(constNameTok)) is { } aggregateConst
                && _ir.StructFieldType(aggregateConst.Type, fieldName) is { } aggregateFieldType)
            {
                return new Member(aggregateConst, fieldName, false) { Type = aggregateFieldType };
            }
        }
        if (TryResolveStdPath(expr, out var stdPath))
        {
            // Both default-allocator value rows are the C heap today, so a value use
            // materializes the one runtime fat pointer; a distinct AllocKind row would
            // switch on the kind here. A curated TYPE path used as a value gets the
            // specific "type, not a value" message; the rest the registry-derived list.
            if (ZigCuratedStd.Find(stdPath, CuratedShape.Value) is not null) { return MaterializeCHeap(); }
            // Any other std VALUE (`std.atomic.cache_line`, array_list's growth policy) is the real
            // std's: a top-level value const of the module the path names, lowered there.
            if (!IsCuratedTypePath(stdPath)
                && ResolveModulePath(fld.Arg0) is { Lowering: { } valueOwner }
                && valueOwner.LowerExportedValueConst(fieldName) is { } exportedValue)
            {
                return exportedValue;
            }
            throw new IrUnsupportedException(
                IsCuratedTypePath(stdPath)
                    ? $"zig `{stdPath}` is a type, not a value"
                    : $"zig std path `{stdPath}` is not modeled (values: {string.Join(" / ", ZigCuratedStd.Paths(CuratedShape.Value))})");
        }
        if (fld.Arg0.Content is Zig.Ident cbid
            && _symbols.Resolve(Tok(cbid.Arg0)) is null
            && TryLookupContainerType(Tok(cbid.Arg0), out var cbaseTy)
            && ContainerTypeName(cbaseTy) is { } cContainer
            && _containerConsts.TryGetValue(cContainer, out var cconsts)
            && cconsts.TryGetValue(fieldName, out var centry))
        {
            // A namespaced container const — re-lower its RHS (comptime; inlined per use).
            return LowerContainerConst(cContainer, fieldName, centry.typeItem, centry.rhs);
        }
        // The same through an alias of a container ANOTHER module reified (`const D = std.enums.EnumIndexer(E);`
        // then `D.count`): the const lowers in the module that declares it.
        if (fld.Arg0.Content is Zig.Ident obid
            && _symbols.Resolve(Tok(obid.Arg0)) is null
            && TryLookupContainerType(Tok(obid.Arg0), out var obaseTy)
            && ContainerTypeName(obaseTy) is { } oContainer
            && TryLowerContainerConstAnywhere(oContainer, fieldName) is { } otherConst)
        {
            return otherConst;
        }
        // `Decimal(T).min_exponent` — a const of the struct a type-returning CALL names (std.fmt.parse_float's
        // convertSlow): the call reifies (memoized), and the const lowers in the module that declares it.
        if (fld.Arg0.Content is Zig.CallArgs
            && TryEvalTypeReturningCall(fld.Arg0, out var calledType) && ContainerTypeName(calledType) is { } calledContainer
            && TryLowerContainerConstAnywhere(calledContainer, fieldName) is { } calledConst)
        {
            return calledConst;
        }
        // A namespaced container `var` (Milestone R, part 6) — `Type.name` resolves to the
        // mangled global's VarRef (an lvalue, so `Type.name = x` / `+= x` write through it).
        if (fld.Arg0.Content is Zig.Ident cvid
            && _symbols.Resolve(Tok(cvid.Arg0)) is null
            && TryLookupContainerType(Tok(cvid.Arg0), out var cvBaseTy)
            && ContainerTypeName(cvBaseTy) is { } cvContainer
            && EnsureContainerVar(cvContainer, fieldName) is { } cvSym)
        {
            return new VarRef(cvSym) { Type = cvSym.Type, IsLValue = true };
        }
        if (fld.Arg0.Content is Zig.Ident bid
            && TryLookupContainerType(Tok(bid.Arg0), out var baseTy)
            && baseTy.Unqualified is CType.Enum en)
        {
            return ResolveEnumLit(fieldName, en);
        }
        // `Number.Mode.decimal` / `Outer.Inner.NAME` — the same two forms through a QUALIFIED
        // nested container base (a nested enum's member, a nested container's const).
        if (fld.Arg0.Content is Zig.Field && TryResolveQualifiedNestedType(fld.Arg0) is { } qualifiedBase)
        {
            if (qualifiedBase.Unqualified is CType.Enum qen) { return ResolveEnumLit(fieldName, qen); }
            if (ContainerTypeName(qualifiedBase) is { } qContainer
                && _containerConsts.TryGetValue(qContainer, out var qconsts)
                && qconsts.TryGetValue(fieldName, out var qentry))
            {
                return LowerContainerConst(qContainer, fieldName, qentry.typeItem, qentry.rhs);
            }
        }
        // `E.member` where E is a registered error set (Milestone X, part 2) — the
        // set-qualified form of `error.member`, resolving to the same flat code (membership
        // erased). A USE as a value: bound to a const/var, compared (`x == E.member`), a
        // `catch`/`switch` operand. The error-RETURN form is handled in LowerReturn. Part 3
        // rejects a member not declared in the set (a good compiler rejects illegal programs).
        if (TryErrorSetMember(expr, out var esSet, out var esMember))
        {
            ValidateSetMember(esSet, esMember);
            return LowerErrorLit(esMember);
        }
        // `Allocating.drain` — a container's own FUNCTION named as a value (std.Io.Writer.Allocating's vtable
        // literal, `.drain = Allocating.drain`): declared on demand, then a function reference as a bare one is.
        if (fld.Arg0.Content is Zig.Ident methodBase && _symbols.Resolve(Tok(methodBase.Arg0)) is null
            && TryLookupContainerType(Tok(methodBase.Arg0), out var methodBaseType)
            && ContainerTypeName(methodBaseType) is { } methodContainer
            && EnsureMethodDeclared(methodContainer, fieldName) is { Kind: SymKind.Func } containerFn
            && !_genericFns.ContainsKey(containerFn))
        {
            return new VarRef(containerFn) { Type = containerFn.Type };
        }
        var structExpr = LowerExpr(fld.Arg0);
        var arrow = structExpr.Type.Unqualified is CType.Pointer;   // Zig `p.x` auto-derefs
        // Slice `.len` / `.ptr` — the runtime Slice<T> exposes `Len` (ulong) and
        // `Ptr` (T*); a `[]const T`'s `.ptr` is a pointer-to-const. Through a single pointer to a slice too, which zig
        // auto-dereferences (`e.key_ptr.len` with `key_ptr: *[]const u8`, std.StringHashMap's iterator, task #118).
        if ((structExpr.Type.Unqualified as CType.Slice
             ?? (structExpr.Type.Unqualified is CType.Pointer { Pointee.Unqualified: CType.Slice pointedSlice } ? pointedSlice : null))
            is { } slc)
        {
            return fieldName switch
            {
                "len" => new Member(structExpr, "Len", arrow) { Type = CType.ULong, IsLValue = true },
                "ptr" => new Member(structExpr, "Ptr", arrow) { Type = new CType.Pointer(slc.Element), IsLValue = true },
                _ => throw new IrUnsupportedException($"slice has no field '{fieldName}' (only .len / .ptr)"),
            };
        }
        // A field row of the receiver's curated runtime type (wall-plan W0: `std.ArrayList(T)`'s `items`, the
        // occupied prefix as a mutable `[]T`, and `capacity`); anything else is a clear error.
        if (ZigCuratedStd.PathOfType(structExpr.Type) is { } fieldOwner && ZigCuratedStd.MembersOf(fieldOwner, CuratedShape.Field).Any())
        {
            return ZigCuratedStd.FindMember(fieldOwner, fieldName, CuratedShape.Field) is { } fieldRow
                ? LowerCuratedMember(fieldRow, new CuratedMemberCall(fieldName, [], structExpr, fld.Arg0, Arrow: arrow))
                : throw new IrUnsupportedException(
                    $"zig {fieldOwner} has no modeled field '{fieldName}' (only "
                    + string.Join(" / ", ZigCuratedStd.MembersOf(fieldOwner, CuratedShape.Field).Select(r => "." + r.Member)) + ")");
        }
        // Fixed-array `.len` — a `[N]T`'s length is the comptime-known element count N
        // (Zig). The array lowered to a pointer (no runtime length field), so fold to a
        // literal. A fixed array has no `.ptr` (that's a slice / many-item-pointer field —
        // take `&arr` for a pointer); reject any other field clearly.
        if (structExpr.Type.Unqualified is CType.Array arrTy)
        {
            if (fieldName != "len")
            {
                throw new IrUnsupportedException($"array has no field '{fieldName}' (only .len)");
            }
            if (arrTy.Count is not int arrLen)
            {
                throw new IrUnsupportedException("array `.len` requires a compile-time-known length");
            }
            // A string literal is `*const [N:0]u8` — its `.len` is N, while the lowered array
            // type counts the NUL too (C's `char[N+1]`), as CoerceToSlice already accounts for.
            if (IsStringLiteralValue(structExpr)) { arrLen--; }
            return new LitInt(arrLen.ToString(System.Globalization.CultureInfo.InvariantCulture), arrLen) { Type = CType.ULong };
        }
        // A TUPLE's `.len` is its element count (std.StaticStringMap.initComptime's `kvs_list.len` over `.{ .{ "one", 1 }, … }`).
        if (fieldName == "len" && structExpr.Type.Unqualified is CType.Tuple lenTuple)
        {
            var tupleLen = lenTuple.Elements.Count;
            return new LitInt(tupleLen.ToString(System.Globalization.CultureInfo.InvariantCulture), tupleLen) { Type = CType.ULong };
        }
        // A TUPLE's `@"0"` field (std.StaticStringMap's `kv.@"0"`, task #100): its element by position, as `t[0]`. The
        // name arrives legalized (`_0`); a tuple has no named fields, so a numeric one is always a position.
        if (structExpr.Type.Unqualified is CType.Tuple fieldTuple
            && int.TryParse(fieldName.TrimStart('_'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position))
        {
            if (position >= fieldTuple.Elements.Count)
            {
                throw new CompileException(
                    $"zig: index {position} outside tuple of length {fieldTuple.Elements.Count}");
            }
            return new TupleIndex(structExpr, position, fieldTuple.Elements[position]) { Type = fieldTuple.Elements[position] };
        }
        // `.len` through a pointer to an array (`tables.len` with `tables = &small`, std.fmt.float.render's table
        // pointers): the pointee's comptime count, as zig reads it.
        if (fieldName == "len" && structExpr.Type.Unqualified is CType.Pointer { Pointee: var lenPointee }
            && lenPointee.Unqualified is CType.Array { Count: int ptrArrLen })
        {
            return new LitInt(ptrArrLen.ToString(System.Globalization.CultureInfo.InvariantCulture), ptrArrLen) { Type = CType.ULong };
        }
        // `.ptr` through a pointer to an array (`slice.ptr` with `slice = &arr`, std.mem.reverseIterator, task #147): the
        // many-item pointer to its first element, the same address.
        if (fieldName == "ptr" && structExpr.Type.Unqualified is CType.Pointer { Pointee: var ptrPointee }
            && ptrPointee.Unqualified is CType.Array ptrArray)
        {
            var firstElem = new CType.Pointer(ptrPointee.IsConst ? ptrArray.Element.WithQuals(TypeQual.Const) : ptrArray.Element);
            return new Cast(firstElem, structExpr) { Type = firstElem };
        }
        // Tagged-union payload access `u.variant` → `u.__payload.variant` (unchecked,
        // like Zig's release-mode field access; the tag isn't a user-facing field).
        if (TryContainerName(structExpr.Type, out var cname)
            && _unions.TryGetValue(cname, out var uinfo)
            && uinfo.Variants.TryGetValue(fieldName, out var vpayload) && vpayload is not null)
        {
            var payloadBase = new Member(structExpr, uinfo.PayloadFieldName, arrow) { Type = new CType.Named(uinfo.PayloadTypeName!), IsLValue = true };
            return new Member(payloadBase, fieldName, false) { Type = vpayload, IsLValue = true };
        }
        var ftype = _ir.StructFieldType(structExpr.Type, fieldName)
            ?? throw new IrUnsupportedException($"no field '{fieldName}' on type {structExpr.Type.Describe()}");
        return new Member(structExpr, fieldName, arrow) { Type = ftype, IsLValue = true };
    }
}
