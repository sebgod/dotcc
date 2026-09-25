#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Types and the curated std resolver: <c>LowerType</c>/<c>LowerTypeName</c>,
/// type aliases + <c>@TypeOf</c> (wall-plan W1), primitives, and the comptime const
/// bindings (imports, allocators, error sets) with their std-path resolution. One
/// concern of the <see cref="ZigLowering"/> binder; class doc + shared state live in
/// the main file.</summary>
internal sealed partial class ZigLowering
{
    // ---- types -----------------------------------------------------------

    // ---- allocators / std (Milestone F) ----------------------------------

    /// <summary>Try to record a comptime <c>const</c> binding that carries no runtime value
    /// (Milestone F): <c>const X = @import("std");</c> registers a module alias in
    /// <see cref="_imports"/>; <c>const a = std.heap.page_allocator;</c> (or a const bound to
    /// another default binding) registers a known-default allocator in
    /// <see cref="_defaultAllocatorBindings"/>. Both emit NO decl (returns <c>true</c> → the
    /// caller drops the statement). A non-comptime RHS returns <c>false</c> (a normal decl). Only
    /// <c>const</c> bindings reach here. A non-<c>std</c> module errors clearly.</summary>
    private bool TryComptimeConstBinding(string name, Item rhs)
    {
        // `const tables = switch (DT) { u64 => &Backend64_TablesFull, … };` (std.fmt.float.render): a pointer to a container
        // TYPE is a comptime namespace (task #85). It binds as a type alias, so `tables.T` / `tables.mulShift(…)` resolve as
        // through the container, and it has no runtime value.
        if (TryComptimeTypePointer(rhs) is { } pointedType)
        {
            _typeAliases[name] = pointedType;
            return true;
        }
        if (rhs.Content is Zig.BuiltinCall b && Tok(b.Arg0) == "@import")
        {
            var bargs = Flatten(b.Arg2);
            if (bargs.Count == 1 && bargs[0].Content is Zig.StrLit sl)
            {
                var module = UnquoteStringLiteral(Tok(sl.Arg0));
                if (module == "std")
                {
                    _imports[name] = module;   // the curated-path namespace tag (std.mem/debug/heap/testing)
                    // If a std lib dir is configured, ALSO record the real std.zig as a navigable import,
                    // so a NON-curated `std.<x>` (e.g. `std.ascii`) falls through to real-root navigation
                    // (road-to-zig-std S1/S2) rather than erroring.
                    if (_moduleGraph?.StdRootPath is { } stdPath) { _importSpecs[name] = stdPath; }
                    return true;
                }
                // `@import("builtin")` / `@import("root")` — the compiler-provided modules, generated as
                // Zig source and resolved through the ordinary module path (road-to-zig-std S3). Recorded
                // for BOTH a root unit and a lazy std module: std's own files import `builtin` constantly,
                // and so does user code asking about the target.
                if (ZigSyntheticModules.IsSyntheticSpec(module) && _moduleGraph is not null)
                {
                    _importSpecs[name] = module;
                    return true;
                }
                // A relative sibling import `@import("./util.zig")` — record the spec; the module is
                // resolved + prepared LAZILY on first use (ResolveImport), so std.zig's 66 re-exports
                // don't fan out at prepare time. In a LAZY (prepared) module, ANY other spec — a bare
                // `@import("builtin")`/`@import("root")` (S3 synthetic modules) or a not-yet-modeled
                // sibling — is likewise recorded deferred and only errors if actually navigated, so
                // std.zig's own `@import("builtin")` doesn't sink its prepare. A ROOT unit keeps the loud
                // "not modeled" rejection for a non-.zig import.
                if (_moduleGraph is not null && _importerDir is not null
                    && (_lazy || module.EndsWith(".zig", System.StringComparison.Ordinal)))
                {
                    _importSpecs[name] = module;
                    // std's own files import their root by path (`const std = @import("std.zig");`). That
                    // binding is the `std` namespace too, so std-internal code names a curated surface
                    // (`mem.Allocator`, the runtime allocator `std.heap.page_allocator` produces) exactly
                    // as user code does.
                    if (IsStdRootSpec(module)) { _imports[name] = "std"; }
                    return true;
                }
                throw new IrUnsupportedException(
                    $"zig `@import(\"{module}\")` is not modeled — only `@import(\"std\")` (its curated allocator/mem/debug/testing paths) and a relative `@import(\"./sibling.zig\")` are supported");
            }
        }
        // `pub const MACH_PORT_RIGHT = @compileError("use MACH.PORT.RIGHT");` — a DEPRECATION TOMBSTONE
        // (road-to-zig-std S7; 25 of them in the pinned std, incl. std/meta.zig and std/os/windows.zig).
        // Zig analyses a declaration only when something references it, so the tombstone is inert until
        // named: record the message, emit no decl, and raise at the REFERENCE (see RaiseIfPoisoned).
        // Firing here instead would make importing those modules impossible.
        if (rhs.Content is Zig.BuiltinCall ce && Tok(ce.Arg0) == "@compileError")
        {
            var msgArgs = Flatten(ce.Arg2);
            _poisonedConsts[name] = msgArgs.Count == 1
                ? ComptimeMessageText(msgArgs[0]) ?? UnreadableMessage
                : UnreadableMessage;
            return true;
        }
        if (TryKnownAllocatorKind(rhs, out var kind))
        {
            _defaultAllocatorBindings[name] = kind;
            return true;
        }
        // `const a = fba.allocator();` over a known `FixedBufferAllocator` local — DEVIRTUALIZE the
        // site (Milestone U): record `a → Fba(fbaSym)` and emit NO decl, so a later `a.alloc(…)`/
        // `.free(…)` lowers to a direct FBA bump over `&fba` (no vtable). A value use of `a` later
        // materializes `ZigAlloc.FbaAllocator(&fba)` (see the VarRef value path / MaterializeFba), so
        // this is an optimization, not a restriction — an escaping `a` still works, just indirectly.
        if (rhs.Content is Zig.CallNoArgs { Arg0.Content: Zig.Field afld }
            && Tok(afld.Arg2) == "allocator"
            && afld.Arg0.Content is Zig.Ident fbaId
            && _symbols.Resolve(Tok(fbaId.Arg0)) is { } fbaSym
            && fbaSym.Type.Unqualified is CType.Named { Name: FbaTypeName })
        {
            _defaultAllocatorBindings[name] = AllocKind.Fba;
            _fbaAllocatorSites[name] = fbaSym;
            fbaSym.AddressTaken = true;
            return true;
        }
        // `const E = error{A, B};` — an explicit error-set declaration (Milestone N, part 5). dotcc
        // erases the set into the flat global code space, so register the member names (assigning
        // each a stable code, in declaration order) and emit NO decl; `E` is then used only as the
        // (erased) set in an `E!T` return type, where LowerType ignores the set name anyway. An empty
        // `error{}` (the never-erroring set) has no members to register.
        if (rhs.Content is Zig.ErrorSet es)
        {
            var members = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var member in WalkErrSetMembers(es.Arg2)) { ErrorCode(member); members.Add(member); }
            _errorSets.Add(name);
            _errorSetMembers[name] = members;   // Milestone X, part 3 — for the membership checks
            return true;
        }
        if (rhs.Content is Zig.ErrorSetEmpty)
        {
            _errorSets.Add(name);
            _errorSetMembers[name] = new HashSet<string>(System.StringComparer.Ordinal);  // `error{}` — no members
            return true;
        }
        // `const E = A || B || …;` — an error-set MERGE (road-to-zig-std). Zig's `||` unions error sets;
        // dotcc erases the set into the flat global code space, so a merge is just another (erased) set.
        // Register E as a known set name so `E!T` / an `IsTypeName(E)` check resolve, but leave it
        // UNCONSTRAINED (no `_errorSetMembers` entry) — the union's full membership spans operands that
        // may live in other, not-yet-resolved modules (`Io.Cancelable`), so `TryDeclaredErrorSet` treats
        // it as `anyerror`-like and enforces no membership (matching the erased model). Emit no decl.
        // Member `error{…}` literal operands still get their codes assigned (via LowerErrorLit paths on
        // use); here we only need the erased type name.
        if (rhs.Content is Zig.ErrSetMerge)
        {
            _errorSets.Add(name);
            return true;
        }
        // A type-as-value alias (wall-plan W1): `const T = i32;` / `const P = *T;` / `const List =
        // std.ArrayList(i32);` / `const T = @TypeOf(x);`. Recorded LAST — the import / allocator /
        // error-set forms above are the more specific comptime bindings; anything else that lowers to
        // a TYPE is an alias. Emits no decl (returns true → the caller drops the statement); a use of
        // `T` in a type position resolves through LowerTypeName. This serves BOTH the top-level pass-0
        // binding and the in-function `DeclOrComptime` path, so a local `const T = @TypeOf(a);` works
        // (the monomorphization-shaped case — the operand is in scope in a body).
        // A lazy module's type-former alias that names a container declared LATER in the file (std.base64's
        // `const decoderWithIgnoreProto = *const fn (…) Base64DecoderWithIgnore;`, task #75) cannot lower while the module is
        // still preparing: it is deferred to its first type-position use, as a top-level type CALL is.
        if (_lazy && _currentFnName.Length == 0 && IsTypeFormer(rhs))
        {
            try { _ = TryTypeAliasRhs(rhs, out _); }
            catch (IrUnsupportedException)
            {
                _deferredTypeCalls[name] = rhs;
                return true;
            }
        }
        if (TryTypeAliasRhs(rhs, out var aliasType))
        {
            _typeAliases[name] = aliasType;
            // `const Cp = u21;` — the DECLARED integer width rides the alias, since the lowered type
            // widened it away (see _declaredIntBits). Cleared when the RHS declares none, so a
            // re-binding of the same name never inherits the previous alias's width.
            SetDeclaredIntBits(name, DeclaredBitsOfTypeArg(rhs));
            // `const Ptr = @TypeOf(pointer);`: a pointer's spelled size class rides it the same way (task #119).
            SetDeclaredPtrSize(name, aliasType.Unqualified is CType.Pointer ? PointerSizeOfTypeArg(rhs) : null);
            // A body's own alias is also what an in-function struct's methods close over (task #124).
            if (_currentFnName.Length > 0) { _bodyTypeAliases.Add(name); }
            return true;
        }
        // `const info = @typeInfo(T);` / `const i = @typeInfo(T).int;` — a comptime reflection value
        // (road-to-zig-std S5). Recorded and the decl DROPPED (return true): a `std.builtin.Type` has
        // no runtime representation in dotcc, so the name exists only for later folds (`i.bits`, a
        // `switch (info)`). Placed after the alias check so `@typeInfo(T).pointer.child` — which IS a
        // type — is claimed there instead.
        if (TryEvalTypeInfo(rhs, out var tiBinding))
        {
            _typeInfoBindings[name] = tiBinding;
            return true;
        }
        // `const signedness = @typeInfo(ReturnType).int.signedness;` (std.mem.readVarInt, task #76) — a comptime enum
        // TAG, bound for a later `@Int(signedness, …)` / `==` / `switch`, and the decl dropped: it has no runtime value.
        if (rhs.Content is Zig.Field { Arg2: var tagField } && Tok(tagField) is "signedness" or "layout"
            && TryEvalComptimeTag(rhs, out var boundTag, out _))
        {
            _comptimeTagBindings[name] = boundTag;
            return true;
        }
        // `const names = @typeInfo(T).@"struct".field_names;` — a comptime member LIST (S5c), bound
        // and the decl DROPPED for the same reason: it has no runtime representation.
        if (TryFoldTypeInfoList(rhs, out var tiList))
        {
            _typeInfoLists[name] = tiList;
            return true;
        }
        // A comptime-known scalar/string literal (`const p = "foo";` / `const n = 3;`) — record its VALUE
        // (road-to-zig-std S5 seed) so a comptime context (a `++`/`**` operand or count) can resolve the
        // name and fold. SIDE EFFECT only: fall through to `return false` so the ordinary runtime decl
        // still emits (a runtime use of the name is unaffected). EvalComptimeValue never throws and
        // recognizes only pure string/int forms, so this is safe in every pass (incl. top-level pass 0).
        // A comptime AGGREGATE / enum-literal binding (road-to-zig-std S3a) — `const cpu: Cpu =
        // .{ .arch = .aarch64 };` / `const mode = .ReleaseFast;`. Recorded the same SIDE-EFFECT way and
        // for the same reason: a struct or enum constant is an ordinary runtime value too, so the decl
        // still emits and only a comptime QUESTION about it folds.
        RecordComptimeAggregateBinding(name, rhs);
        // `const Writer = std.Io.Writer;` is a path into another module, which may name a file-as-struct
        // TYPE (road-to-zig-std G3). Recorded unresolved; a type position resolves it on demand.
        if (rhs.Content is Zig.Field && IsImportRootedPath(rhs)) { _moduleAliasPaths[name] = rhs; }
        if (EvalComptimeValue(rhs) is { } comptimeVal) { _comptimeValues[name] = comptimeVal; }
        // A comptime ARRAY literal (`const a = [_]u8{1,2};`) — record its raw element-type + element
        // items (no lowering, so safe in any pass) for a later `++`/`**` fold (see TryArrayLiteralParts).
        else if (rhs.Content is Zig.TypedStructInit { Arg0.Content: Zig.TyArray ta } arrLit)
        {
            _comptimeArrayConsts[name] = (ta.Arg3, Flatten(arrLit.Arg2));
        }
        return false;
    }

    /// <summary>True when a dotted path is rooted, syntactically, at a name this unit binds to an
    /// import (<c>std.Io.Writer</c>). Resolves nothing, so it is safe while a module is being prepared.</summary>
    private bool IsImportRootedPath(Item expr) => expr.Content switch
    {
        Zig.Field f => IsImportRootedPath(f.Arg0),
        Zig.Ident id => _importSpecs.ContainsKey(Tok(id.Arg0)),
        // `const H = @import("h.zig").H;` — rooted at an INLINE import (ResolveModulePath resolves one), in a
        // ROOT unit only: std.zig re-exports dozens of names that way (`pub const BufMap =
        // @import("buf_map.zig").BufMap;`), and a lazy module must not pull those modules in at prepare time.
        Zig.BuiltinCall b => !_lazy && Tok(b.Arg0) == "@import",
        _ => false,
    };

    /// <summary>Recognize a <c>const</c> RHS that is a TYPE expression (wall-plan W1), lowering it to
    /// the aliased <see cref="CType"/>. Two unambiguous shapes plus a guarded identifier:
    /// <list type="bullet">
    /// <item>a type-former node (<c>*T</c>, <c>?T</c>, <c>[]T</c>, <c>[N]T</c>, <c>E!T</c>, a tuple /
    /// fn-pointer type, a curated <c>std.ArrayList(T)</c> / <c>std.mem.Allocator</c>) — these can only
    /// be types, so lower directly;</item>
    /// <item><c>@TypeOf(expr)</c> — the operand's synthesized type (unevaluated);</item>
    /// <item>a bare identifier that <see cref="IsTypeName"/> confirms is a type (a primitive, a
    /// container, an existing alias, a self-alias, or an error set) — so <c>const y = someValue;</c>
    /// (a value) is NOT misread as an alias and falls through to a normal const.</item>
    /// </list>
    /// Returns false for any non-type RHS (→ an ordinary value const / global).</summary>
    /// <summary>The type a <c>switch</c> over a TYPE subject selects (see <see cref="TrySelectTypeProng"/>), when the
    /// selected prong is an uncaptured type expression; false for anything else, so a value switch stays a value.</summary>
    private bool TrySelectedTypeArm(Item subject, Item prongs, out CType type)
    {
        type = CType.Int;
        // Over a TYPE subject, or a comptime BOOL one (`const W = switch (wide) { true => u32, false => u8 };`, task #84).
        if (TrySelectTypeProng(subject, prongs) is { Expr: { } capturedArm, CaptureName: { } typeCapture } && typeCapture != "_")
        {
            // `else => |T| T` over a type (std.math.gcd's `switch (@TypeOf(a, b)) { comptime_int => …, else => |T| T }`,
            // task #86): the capture is the subject type itself, bound as an alias while the arm resolves.
            if (!TryTypeAliasRhs(subject, out var subjectType)) { return false; }
            var hadPrev = _typeAliases.TryGetValue(typeCapture, out var prevAlias);
            var prevBits = _declaredIntBits.TryGetValue(typeCapture, out var pb) ? pb : (int?)null;
            _typeAliases[typeCapture] = subjectType;
            SetDeclaredIntBits(typeCapture, DeclaredBitsOfTypeArg(subject));
            try { return TryTypeAliasRhs(capturedArm, out type); }
            finally
            {
                if (hadPrev && prevAlias is { } restored) { _typeAliases[typeCapture] = restored; } else { _typeAliases.Remove(typeCapture); }
                SetDeclaredIntBits(typeCapture, prevBits);
            }
        }
        return (TrySelectTypeProng(subject, prongs) ?? TrySelectBoolProng(subject, prongs)) is { Expr: { } typeArm, CaptureName: null }
               && TryTypeAliasRhs(typeArm, out type);
    }

    private bool TryTypeAliasRhs(Item rhs, out CType type)
    {
        switch (rhs.Content)
        {
            // Type-former prefixes/suffixes — unambiguously a type (no value spelling collides).
            case var _ when IsTypeFormer(rhs):
                type = LowerType(rhs);
                return true;

            // `const Writer = @This();` names the innermost container, which at file scope is the file
            // itself when it has top-level fields (road-to-zig-std G3). A namespace-only file has no
            // type to name, so the binding falls through unchanged.
            case Zig.BuiltinCallNoArgs tb when Tok(tb.Arg0) == "@This" && (_currentContainer ?? _fileContainer) is not null:
                type = CurrentContainerType();
                return true;

            // `const M = switch (T) { f16, f32, f64 => u64, f80, f128 => u128, else => unreachable };` in a function
            // body (std.fmt.parse_float's mantissaType, inlined): a switch over a TYPE whose selected prong names a type.
            case Zig.SwitchExpr se when TrySelectedTypeArm(se.Arg2, se.Arg5, out var armType):
                type = armType;
                return true;
            case Zig.SwitchExprTrailing st when TrySelectedTypeArm(st.Arg2, st.Arg5, out var trailingArmType):
                type = trailingArmType;
                return true;

            // `@TypeOf(expr)` — the operand's synthesized type, unevaluated.
            case Zig.BuiltinCall b when Tok(b.Arg0) == "@TypeOf":
                type = TypeOfBuiltin(b.Arg2);
                return true;

            // `const MaskInt = @Int(.unsigned, @bitSizeOf(T));` — a CONSTRUCTED type bound to a name
            // (road-to-zig-std S7). The width it was built with rides the binding through
            // SetDeclaredIntBits / DeclaredBitsOfTypeArg, so `@typeInfo(MaskInt).int.bits` answers.
            case Zig.BuiltinCall rb when TryLowerReifyBuiltin(rb, out var reified):
                type = reified;
                return true;

            // `const List = std.ArrayList(i32);` — a curated generic std type in value position; the
            // CallArgs resolves through LowerType exactly as in type position (wall-plan W0).
            case Zig.CallArgs ca when TryResolveStdPath(ca.Arg0, out var gp) && StdGenericTypes.ContainsKey(gp):
                type = LowerType(rhs);
                return true;

            // `const P = Pair(i32);` / `const E = Empty();` — a call to a USER type-returning generic
            // (wall-plan W4) bound to a name. Reifies the returned struct and records the alias. (At
            // top level this resolves in the pass-1.5 re-try, once the fn is declared — see
            // LowerTopLevelGlobals.)
            case Zig.CallArgs or Zig.CallNoArgs when TryEvalTypeReturningCall(rhs, out var trt):
                type = trt;
                return true;

            // `const C = @typeInfo(T).pointer.child;` — a reflected child TYPE aliased to a name
            // (road-to-zig-std S5). Before the std-path case: no std path is rooted at a builtin call.
            case Zig.Field when TryFoldTypeInfoType(rhs, out var tiChild):
                type = tiChild;
                return true;

            // `const Tag = @typeInfo(E).@"enum".tag_type;` / `const F = @typeInfo(T).@"struct"
            // .field_types[0];` — a reflected TYPE from the member-list payloads (S5c).
            case Zig.Field or Zig.Index when TryFoldTypeInfoListType(rhs, out var tiListTy):
                type = tiListTy;
                return true;

            // `const Hasher = switch (@typeInfo(@TypeOf(hasher))) { .pointer => |ptr| ptr.child, else => @TypeOf(hasher) };`
            // (std.hash.autoHash): a switch over a comptime TAG whose selected prong is a type. A switch that
            // yields a value instead is left to the value path (the type evaluation declines, loudly or not).
            case Zig.SwitchExpr or Zig.SwitchExprTrailing when TrySwitchTypeAlias(rhs, out var switched):
                type = switched;
                return true;

            // `const DT = if (@bitSizeOf(T) <= 64) u64 else u128;` (std.fmt.float.render, task #77): a comptime condition
            // choosing between two types. A runtime condition, or an arm that is not a type, is left to the value path.
            case Zig.IfExpr ie when TryFoldTypeIfCondition(ie.Arg2) is { } takenArm
                                    && TryTypeAliasRhs(takenArm ? ie.Arg4 : ie.Arg6, out var ifType):
                type = ifType;
                return true;

            // `pub const Size = Unmanaged.Size;` (std.HashMap) — a container's nested type or type const,
            // named qualified. Before the std-path case: a local container name is never a std path.
            case Zig.Field when TryResolveQualifiedNestedType(rhs) is { } qualified:
                type = qualified;
                return true;

            // `const A = std.mem.Allocator;` — a dotted std TYPE path aliased to a name.
            case Zig.Field when TryResolveStdPath(rhs, out var fp) && StdTypes.ContainsKey(fp):
                type = LowerStdType(rhs);
                return true;

            // A bare identifier — an alias ONLY if it names a type (guarded so a value const isn't stolen).
            case Zig.Ident id when IsTypeName(Tok(id.Arg0)):
                type = LowerTypeName(Tok(id.Arg0));
                return true;

            default:
                type = CType.Int;
                return false;
        }
    }

    /// <summary>The value of an <c>if</c> condition in a type alias, when it is known at compile time (a comptime question,
    /// or anything the const folder settles: <c>@bitSizeOf(T) &lt;= 64</c>); null otherwise. The condition is lowered into a
    /// throwaway hoist, since a type alias emits no statement.</summary>
    private bool? TryFoldTypeIfCondition(Item cond)
    {
        if (TryFoldComptimeCondition(cond) is { } folded) { return folded; }
        using var _ = EnterThrowawayHoist();
        try { return _ir.ConstEval(LowerExpr(cond)) is { } v ? v != 0 : null; }
        catch (IrUnsupportedException) { return null; }
    }

    /// <summary>A switch over a comptime tag that selects a TYPE (see <see cref="TryTypeAliasRhs"/>): its type, or
    /// false when the subject is not a comptime tag or the selected prong is not a type.</summary>
    private bool TrySwitchTypeAlias(Item rhs, out CType type)
    {
        type = CType.Int;
        var (subject, prongs) = rhs.Content switch
        {
            Zig.SwitchExpr s => (s.Arg2, s.Arg5),
            Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
            _ => (null, null),
        };
        if (subject is null || prongs is null || !TryEvalComptimeTag(subject, out _, out _)) { return false; }
        try
        {
            type = LowerComptimeTypeSwitch("switch", subject, prongs).Type;
            return true;
        }
        catch (IrUnsupportedException)
        {
            return false;   // a value-yielding comptime switch: not an alias
        }
    }

    /// <summary>True for a type-FORMER node (<c>*T</c>, <c>?T</c>, <c>[]T</c>, <c>[N]T</c>, <c>E!T</c>, a fn or
    /// tuple type, and their aligned / sentinel forms): a spelling that can only be a type, so a caller
    /// may classify it as one before lowering anything.</summary>
    private static bool IsTypeFormer(Item item) => item.Content
        is Zig.TyPointer or Zig.TyPtrConst or Zig.TyCPtr or Zig.TyCPtrConst
        or Zig.TyManyPtr or Zig.TyManyPtrConst or Zig.TySentPtr or Zig.TySentPtrConst
        or Zig.TyOptional or Zig.TySlice or Zig.TySliceConst or Zig.TySentSlice or Zig.TySentSliceConst
        or Zig.TyPointerAlign or Zig.TyPtrConstAlign or Zig.TyManyPtrAlign or Zig.TyManyPtrConstAlign
        or Zig.TySliceAlign or Zig.TySliceConstAlign
        or Zig.TySentSliceExpr or Zig.TySentSliceConstExpr or Zig.TySentSliceAlignExpr
        or Zig.TySentSliceConstAlignExpr or Zig.TySentPtrExpr or Zig.TySentPtrConstExpr
        or Zig.TyArray or Zig.TySentArray or Zig.ErrUnion or Zig.TyTuple
        or Zig.TyFn or Zig.TyFnNoArgs or Zig.TyFnErr or Zig.TyFnNoArgsErr;

    /// <summary>True when a struct MEMBER <c>const</c>'s RHS is a TYPE: a type former, a call to a
    /// type-returning generic, a type name, or an <c>if</c> whose then-arm is one of those
    /// (<c>pub const Slice = if (alignment) |a| ([]align(a.toByteUnits()) T) else []T;</c>). Stricter
    /// than a type body's shape test, because a member may equally be a VALUE const
    /// (<c>pub const max = if (c) 3 else 4;</c>), which must keep its lazy value lowering.</summary>
    private bool IsTypeConstMember(Item rhs)
    {
        var cur = rhs;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        return cur.Content switch
        {
            Zig.IfExpr ie => IsTypeConstMember(ie.Arg4),
            Zig.IfExprCapture ic => IsTypeConstMember(ic.Arg7),
            Zig.IfExprTypeArms => true,
            _ => IsTypeFormer(cur) || TryTypeAliasRhs(cur, out _),
        };
    }

    /// <summary>True when <paramref name="name"/> names a TYPE in the current lowering context — a
    /// registered container, an existing type alias, a container-scoped self-alias, an error set, or
    /// a Zig primitive (<see cref="TryLowerPrim"/>). The discriminator that keeps a bare-identifier
    /// <c>const</c> RHS (<c>const y = x;</c>) from being misread as a type alias.</summary>
    private bool IsTypeName(string name)
        => _typeAliases.ContainsKey(name)
        || (_deferredTypeCalls.ContainsKey(name) && TryDeferredTypeAlias(name, out _))
        || _containerTypes.ContainsKey(name)
        || ResolveSelfAlias(name) is not null
        || _errorSets.Contains(name)
        || name == "anyerror"
        || TryLowerPrim(name, out _);

    /// <summary>The type of a <c>@TypeOf(expr)</c> (wall-plan W1): lower the single operand only to
    /// read its synthesized <see cref="CType"/>. Zig's <c>@TypeOf</c> does NOT evaluate its operand,
    /// so the lowering runs into a THROWAWAY hoist buffer — any incidental ANF temp is discarded, the
    /// operand's would-be side effects never reach the body. A wrong arity is a clear error.</summary>
    private CType TypeOfBuiltin(Item argList)
    {
        var args = Flatten(argList);
        if (args.Count == 0)
        {
            throw new IrUnsupportedException("zig `@TypeOf` takes at least one operand");
        }
        if (args.Count == 1) { return TypeOfOperand(args[0]).Type; }
        // `@TypeOf(val, lower, upper)` (std.math.clamp): the PEER type of the operands. A comptime_int operand
        // (an untyped literal) yields to a fixed-width one; among fixed-width integers the widest wins.
        CType? peer = null;
        foreach (var arg in args)
        {
            var (t, comptimeInt) = TypeOfOperand(arg);
            if (comptimeInt) { continue; }
            peer = peer is null || t.Unqualified.SizeOf > peer.Unqualified.SizeOf ? t : peer;
        }
        return peer ?? CType.ComptimeInt;
    }

    /// <summary>One <c>@TypeOf</c> operand's type, and whether it is a <c>comptime_int</c> (an untyped integer
    /// literal, or a value of <see cref="CType.ComptimeInt"/>), which yields to a fixed-width peer.</summary>
    private (CType Type, bool ComptimeInt) TypeOfOperand(Item arg)
    {
        // `@TypeOf(anytypeParam)` while lowering an `anytype` generic's per-instance signature (wall-plan
        // W5): return the param's inferred concrete type directly — it is seeded at the call site but is
        // not yet an in-scope symbol, so the LowerExpr path below would fail to resolve it.
        if (arg.Content is Zig.Ident aid && _anytypeSeeds.TryGetValue(Tok(aid.Arg0), out var seeded))
        {
            return (seeded, seeded.Unqualified is CType.Prim { IsComptimeInt: true });
        }
        using var _ = EnterThrowawayHoist();   // @TypeOf's operand is unevaluated
        var lowered = LowerExpr(arg);
        var type = lowered.Type
            ?? throw new IrUnsupportedException("zig `@TypeOf`: the operand has no statically known type");
        return (type, arg.Content is Zig.IntLit || type.Unqualified is CType.Prim { IsComptimeInt: true });
    }

    /// <summary>Walk an <c>error{ A, B, … }</c> member list (the right-recursive <c>ErrSetList</c>)
    /// into its member names, mirroring the grammar's one / trailing-comma / cons shapes.</summary>
    private static IEnumerable<string> WalkErrSetMembers(Item list)
    {
        var cur = list;
        while (true)
        {
            switch (cur.Content)
            {
                case Zig.ErrSetOne o:         yield return Tok(o.Arg0); yield break;
                case Zig.ErrSetOneTrailing o: yield return Tok(o.Arg0); yield break;
                case Zig.ErrSetCons c:        yield return Tok(c.Arg0); cur = c.Arg2; break;
                default:                      yield break;
            }
        }
    }

    /// <summary>Walk a dotted access chain (<see cref="Zig.Field"/> over a <see cref="Zig.Ident"/>
    /// root) rooted at a module-import alias, returning the canonical dotted path with the MODULE
    /// name as its root (e.g. <c>"std.heap.page_allocator"</c>) regardless of the alias spelling.
    /// Works in both expression and type position (same AST shape). Returns <c>false</c> for any
    /// chain not rooted at an <see cref="_imports"/> alias.</summary>
    private bool TryResolveStdPath(Item expr, out string path) => TryResolveStdPath(expr, out path, MaxAliasHops);

    /// <summary><see cref="TryResolveStdPath(Item, out string)"/>, following a module alias at the root
    /// (<c>const mem = std.mem;</c> in hash_map.zig, so <c>mem.Allocator</c> is <c>std.mem.Allocator</c>)
    /// at most <paramref name="hops"/> times.</summary>
    private bool TryResolveStdPath(Item expr, out string path, int hops)
    {
        path = "";
        var segments = new List<string>();
        var cur = expr;
        while (cur.Content is Zig.Field f)
        {
            segments.Add(Tok(f.Arg2));
            cur = f.Arg0;
        }
        if (cur.Content is not Zig.Ident id) { return false; }
        if (!_imports.TryGetValue(Tok(id.Arg0), out var module))
        {
            if (hops == 0 || !_moduleAliasPaths.TryGetValue(Tok(id.Arg0), out var aliasPath)
                || !TryResolveStdPath(aliasPath, out var aliased, hops - 1))
            {
                return false;
            }
            module = aliased;
        }
        segments.Add(module);
        segments.Reverse();
        path = string.Join(".", segments);
        return true;
    }

    /// <summary>True when <paramref name="expr"/> is provably the statically-known default
    /// allocator — a <c>const</c> bound to it (<see cref="_defaultAllocatorBindings"/>), or a
    /// direct <c>std.heap.page_allocator</c> / <c>std.heap.c_allocator</c> path. This is the
    /// devirtualization predicate; an opaque parameter or an <c>fba.allocator()</c> result is NOT
    /// provable here (→ indirect dispatch).</summary>
    private bool TryKnownAllocatorKind(Item expr, out AllocKind kind)
    {
        if (expr.Content is Zig.Ident id && _defaultAllocatorBindings.TryGetValue(Tok(id.Arg0), out kind))
        {
            return true;
        }
        if (TryResolveStdPath(expr, out var path) && StdAllocatorValues.TryGetValue(path, out kind))
        {
            return true;
        }
        kind = AllocKind.CHeap;
        return false;
    }

    /// <summary>True when <paramref name="name"/> is a comptime allocator/namespace binding
    /// recorded by <see cref="TryComptimeConstBinding"/> (a module import or a known-default
    /// allocator) — so pass 1 skips its (non-existent) top-level decl.</summary>
    private bool IsComptimeBound(string name)
        => _imports.ContainsKey(name) || _defaultAllocatorBindings.ContainsKey(name)
        || _errorSets.Contains(name) || _typeAliases.ContainsKey(name);

    /// <summary>Strip the surrounding double quotes from a Zig string-literal lexeme. Used only
    /// for the simple identifier-shaped module name in <c>@import("…")</c> (no escapes).</summary>
    private static string UnquoteStringLiteral(string raw)
        => raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;

    /// <summary>The materialized C-heap default allocator as a runtime <see cref="CType.Allocator"/>
    /// value (<c>ZigAlloc.CHeap()</c>) — emitted when the statically-known default flows into an
    /// opaque allocator sink (a value position, not a devirtualizable <c>.alloc</c> receiver).</summary>
    private static CExpr MaterializeCHeap()
        => new Call("ZigAlloc.CHeap", new List<CExpr>(), new List<CType>(), null) { Type = new CType.Allocator() };

    /// <summary>The materialized runtime <see cref="CType.Allocator"/> for a devirtualized
    /// <c>fba.allocator()</c> site (Milestone U) — <c>ZigAlloc.FbaAllocator(&amp;fba)</c>, emitted
    /// when the FBA-bound name flows into an opaque allocator sink (a value position rather than a
    /// devirtualizable <c>.alloc</c> receiver).</summary>
    private static CExpr MaterializeFba(Symbol fbaSym)
    {
        fbaSym.AddressTaken = true;
        var fbaRef = new VarRef(fbaSym) { Type = fbaSym.Type, IsLValue = true };   // &fba direct, not a copy
        var addr = new Unary(UnOp.AddrOf, fbaRef) { Type = new CType.Pointer(fbaSym.Type) };
        return new Call("ZigAlloc.FbaAllocator", new List<CExpr> { addr },
            new List<CType> { new CType.Pointer(new CType.Named(FbaTypeName)) }, null) { Type = new CType.Allocator() };
    }

    private CType LowerType(Item type) => type.Content switch
    {
        Zig.Ident id => LowerTypeName(Tok(id.Arg0)),
        // A parenthesized type (`fn add(…) (error{Overflow}!T)` in std.math) is its inner type.
        Zig.Grouped g => LowerType(g.Arg1),
        // `@typeInfo(T).<kind>.child` in a TYPE position (road-to-zig-std S5) — the child type of a
        // pointer / slice / optional / array kind. Checked before the std-path resolver: the base is
        // a comptime `std.builtin.Type` value, which no std path claims.
        Zig.Field when TryFoldTypeInfoType(type, out var tiChild) => tiChild,
        // `@typeInfo(E).@"enum".tag_type` — the same shape, from the member-list payloads (S5c).
        Zig.Field when TryFoldTypeInfoListType(type, out var tiTag) => tiTag,
        // `@typeInfo(T).@"struct".field_types[0]` — a TYPE element of a comptime member list (S5c).
        Zig.Index when TryFoldTypeInfoListType(type, out var tiElem) => tiElem,
        // A dotted std type (Milestone F): `std.mem.Allocator` → the runtime Allocator fat
        // pointer; `std.heap.FixedBufferAllocator` → the concrete bump allocator. Any other std
        // path in type position errors clearly (`std` is a known-paths resolver, not a real model).
        // `Number.Mode` — a NESTED container named through its parent (qualified). Checked before the
        // std-path resolver: a local container name is never a std path.
        Zig.Field when TryResolveQualifiedNestedType(type) is { } qualified => qualified,
        Zig.Field => LowerStdType(type),
        // Pointer types. `*T` and the C-pointer `[*c]T` both lower to a plain
        // `T*` (the C-pointer's null/arithmetic semantics ARE C's pointer). The
        // pointee `const` rides as a TypeQual so const-correctness sees it; it
        // doesn't change the C# spelling (`[*c]const u8` and `[*c]u8` are both
        // `byte*`). `[*c]const u8` is exactly the type of printf's format param.
        Zig.TyPointer p    => PointerTo(LowerPointee(p.Arg1)),
        Zig.TyPtrConst p   => PointerTo(LowerPointee(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TyCPtr p       => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyCPtrConst p  => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        // `[*]T` / `[*]const T` many-item pointers (Milestone O, part 2) — like `[*c]`,
        // a bare `T*`. They index/slice; `.len` is unavailable (a pointer has no length).
        Zig.TyManyPtr p     => new CType.Pointer(LowerDataType(p.Arg1)),
        Zig.TyManyPtrConst p => new CType.Pointer(LowerDataType(p.Arg2).WithQuals(TypeQual.Const)),
        // `?T` optional. An optional POINTER `?*T` lowers to a bare nullable `T*` (Zig's
        // own niche — null = none, zero cost; a non-optional `*T` loses its non-null
        // guarantee, a documented leniency). A `?T` over a value type lowers to C#
        // Nullable<T> via CType.Optional, so `null`/`.?`/`orelse` map to C#'s built-ins.
        Zig.TyOptional opt => LowerOptional(opt.Arg1),
        // `E!T` error-union type → CType.ErrorUnion(T). V1 erases the error SET (Arg0, the
        // Suffix naming the set), so `anyerror!T` and a named `E!T` lower identically — the
        // payload is what the backend renders (`ErrUnion<T>`). See [[CType.ErrorUnion]].
        Zig.ErrUnion eu => new CType.ErrorUnion(LowerType(eu.Arg2)),
        // `[]T` / `[]const T` slice → CType.Slice (the runtime Slice<T> / ConstSlice<T> fat
        // pointer). `[]const T` carries the `const` on the element, so the backend renders it
        // as `ConstSlice<T>` — element-only const, like the pointer forms above. See
        // [[CType.Slice]].
        Zig.TySlice s      => new CType.Slice(LowerDataType(s.Arg2)),
        Zig.TySliceConst s => new CType.Slice(LowerDataType(s.Arg3).WithQuals(TypeQual.Const)),
        // The aligned forms (`*align(4) const T`, `[]align(a) T`): the same types. Alignment is not tracked
        // (C# pointers carry none), so the `align(E)` operand is not even lowered.
        Zig.TyPointerAlign p      => PointerTo(LowerPointee(p.Arg2)),
        Zig.TyPtrConstAlign p     => PointerTo(LowerPointee(p.Arg3).WithQuals(TypeQual.Const)),
        Zig.TyManyPtrAlign p      => new CType.Pointer(LowerDataType(p.Arg2)),
        Zig.TyManyPtrConstAlign p => new CType.Pointer(LowerDataType(p.Arg3).WithQuals(TypeQual.Const)),
        Zig.TySliceAlign s        => new CType.Slice(LowerDataType(s.Arg3)),
        Zig.TySliceConstAlign s   => new CType.Slice(LowerDataType(s.Arg4).WithQuals(TypeQual.Const)),
        // A general sentinel (`[:s]T`, `[*:null]T`), erased in the type exactly as `[:0]`'s is.
        Zig.TySentSliceExpr s           => new CType.Slice(LowerDataType(s.Arg4)),
        Zig.TySentSliceConstExpr s      => new CType.Slice(LowerDataType(s.Arg5).WithQuals(TypeQual.Const)),
        Zig.TySentSliceAlignExpr s      => new CType.Slice(LowerDataType(s.Arg5)),
        Zig.TySentSliceConstAlignExpr s => new CType.Slice(LowerDataType(s.Arg6).WithQuals(TypeQual.Const)),
        Zig.TySentPtrExpr p             => new CType.Pointer(LowerDataType(p.Arg5)),
        Zig.TySentPtrConstExpr p        => new CType.Pointer(LowerDataType(p.Arg6).WithQuals(TypeQual.Const)),
        // Sentinel-terminated types (Milestone O, part 3 — the C-string shape; V1 sentinel = 0).
        // `[*:0]T` is a NUL-terminated many-item pointer (C's `char*`) → a bare `T*`, like `[*]`;
        // `[:0]T` is a NUL-terminated slice → CType.Slice, like `[]T`. The sentinel is a type-level
        // annotation, not separately enforced (string literals are already NUL-terminated, so a
        // manual `while (p[n] != 0)` scan works); the auto-scan `p[0..]` on a sentinel pointer is
        // a documented cut. Const rides as a TypeQual on the element, same as the non-sentinel forms.
        Zig.TySentPtr p      => new CType.Pointer(LowerDataType(p.Arg1)),
        Zig.TySentPtrConst p => new CType.Pointer(LowerDataType(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TySentSlice s      => new CType.Slice(LowerDataType(s.Arg1)),
        Zig.TySentSliceConst s => new CType.Slice(LowerDataType(s.Arg2).WithQuals(TypeQual.Const)),
        // `[N]T` fixed-size array → CType.Array(element, N). N must be an integer literal
        // (a general comptime const-expr size is deferred). A `var b: [N]T` local lowers to a
        // stackalloc'd C array (see DeclOf), so slicing it (`b[lo..hi]`) yields a stack-backed slice.
        Zig.TyArray a => new CType.Array(LowerDataType(a.Arg3), ConstEvalArraySize(a.Arg1)),
        // `[N:s]T` sentinel-terminated array (Milestone O, part 4; non-zero sentinel in Milestone Z)
        // → CType.Array(element, N) — the LOGICAL length N (so `.len` / slicing exclude the sentinel,
        // like Zig). The extra trailing sentinel slot (N+1 total storage) is materialized only at the
        // local decl site (see DeclOf / IsSentinelArrayType / SentinelArrayValue); the type itself
        // stays an ordinary N-element array, so a `[N:0]u8` buffer is a valid NUL-terminated C string
        // without writing the terminator. A zero sentinel rides C#'s zero-fill; a NON-ZERO sentinel is
        // written into the trailing slot explicitly (the sentinel VALUE isn't carried in the type).
        Zig.TySentArray a => new CType.Array(LowerDataType(a.Arg5), ConstEvalArraySize(a.Arg1)),
        // Tuple TYPE `struct { T1, T2, … }` (Milestone G) → CType.Tuple → C# System.ValueTuple<…>.
        // Used as a function return type or a var/param annotation; nested tuple types compose.
        Zig.TyTuple t => LowerTupleType(t.Arg2),
        // Function-pointer TYPE `fn (Params) RetType` (Milestone W, part 1a) → a bare CType.Func
        // (the C# backend renders it as a managed `delegate*<P…, Ret>`, the same shape the Zig
        // allocator vtable uses). `*const fn (…) R` / `?*const fn (…) R` reach here as the pointee
        // and are collapsed to the bare Func by PointerTo / LowerOptional. Params are named
        // (`IDENT : Type`); their names are irrelevant to the type, only the types matter.
        // An optional `callconv(Expr)` sits between `)` and the return type (nullable CallConv), so
        // the return type is one slot further right than the pre-CallConv layout. `callconv(.c)` /
        // `(.C)` honors the C ABI via IsNativeCallConv (→ `delegate* unmanaged[Cdecl]`); every other
        // convention (and the absent/epsilon case) stays managed. See IsCCallConv.
        Zig.TyFnSwitchRet => throw new IrUnsupportedException(
            "a function type whose return type is a `switch` expression is not lowered yet (std.Options' "
            + "`elf_debug_info_search_paths`); fold the switch into a type alias first"),
        Zig.TyFn f       => new CType.Func(LowerType(f.Arg5), LowerFnTypeParams(f.Arg2), Variadic: false) { IsNativeCallConv = IsCCallConv(f.Arg4) },
        Zig.TyFnNoArgs f => new CType.Func(LowerType(f.Arg4), System.Array.Empty<CType>(), Variadic: false) { IsNativeCallConv = IsCCallConv(f.Arg3) },
        // `!T`-returning fn-pointer types: the return is an error union `!T` (like fnDefErr). The
        // Func's Return carries the CType.ErrorUnion, so a bound fn-ptr's result is an ErrUnion<T>.
        Zig.TyFnErr f       => new CType.Func(new CType.ErrorUnion(LowerType(f.Arg6)), LowerFnTypeParams(f.Arg2), Variadic: false) { IsNativeCallConv = IsCCallConv(f.Arg4) },
        Zig.TyFnNoArgsErr f => new CType.Func(new CType.ErrorUnion(LowerType(f.Arg5)), System.Array.Empty<CType>(), Variadic: false) { IsNativeCallConv = IsCCallConv(f.Arg3) },
        // `@This()` — Zig's reflective self-type → the container currently being lowered, so
        // `self: @This()` / `self: *@This()` name the receiver without repeating the type name.
        // The `const Self = @This();` alias form (the common Zig idiom) is also supported — it
        // registers a container-scoped type alias (see RegisterContainerConsts / ResolveSelfAlias)
        // so `Self` resolves here through LowerTypeName.
        Zig.BuiltinCallNoArgs b when Tok(b.Arg0) == "@This" => CurrentContainerType(),
        // `@TypeOf(expr)` in TYPE position (wall-plan W1) — e.g. `var y: @TypeOf(x) = x;` or a
        // param/return annotation. The operand's synthesized type; unevaluated (see TypeOfBuiltin).
        Zig.BuiltinCall b when Tok(b.Arg0) == "@TypeOf" => TypeOfBuiltin(b.Arg2),
        // A type-CONSTRUCTING builtin (road-to-zig-std S7): `@Int(.unsigned, @bitSizeOf(T))` builds an
        // integer type; the aggregate constructors of the same family are loud cuts. See
        // TryLowerReifyBuiltin — checked after @TypeOf/@This, which are their own cases above.
        Zig.BuiltinCall rb when TryLowerReifyBuiltin(rb, out var reified) => reified,
        // A curated GENERIC std type in TYPE position (`std.ArrayList(T)`, wall-plan W0). A
        // call parses in type position via the ordinary Suffix chain (Type → ErrUnion →
        // Suffix → callArgs), so NO grammar change: resolve the callee's std path against
        // the StdGenericTypes registry and instantiate over the lowered element. A composed
        // form (`*std.ArrayList(T)`, `?std.ArrayList(T)`, a fn param/return) rides the
        // surrounding Type productions.
        Zig.CallArgs ca when TryResolveStdPath(ca.Arg0, out var gp) && StdGenericTypes.TryGetValue(gp, out var makeGeneric)
            => makeGeneric(LowerSingleTypeArg(ca.Arg2, gp)),
        // A USER type-returning generic (wall-plan W4) in TYPE position — `Pair(i32)` / a no-arg
        // `Empty()`. Reifies (or reuses) the returned struct per resolved type argument. Checked after
        // the std generic (disjoint: a std generic has a dotted `std.…` Field callee, a user one a bare
        // identifier). A composed form (`*Pair(i32)`, `?Pair(i32)`, `[]Pair(i32)`) rides the surrounding
        // Type productions, exactly like the std generic.
        Zig.CallArgs when TryEvalTypeReturningCall(type, out var userTy) => userTy,
        Zig.CallNoArgs when TryEvalTypeReturningCall(type, out var userTyNoArg) => userTyNoArg,
        // An INLINE named-field struct type (`fn f() struct { a: u8 }`, a field/param/var annotation —
        // road-to-zig-std S9, grammar #90) → a synthesized named struct type, reified once per source
        // site. See ReifyInlineStruct.
        Zig.InlineStructType ist       => ReifyInlineStruct(type, ist.Arg2),
        Zig.InlineEnumType iet         => ReifyInlineEnum(type, iet.Arg2),
        Zig.InlineUnionEnumType iut    => ReifyInlineUnion(type, iut.Arg5, tagged: true),
        Zig.InlineUnionType iuu        => ReifyInlineUnion(type, iuu.Arg2, tagged: false),
        Zig.InlineStructTypeEmpty      => ReifyInlineStruct(type, null),
        // A type chosen by a comptime switch in any type position (a parameter's, as well as a field's): the selected
        // prong's type (task #73).
        Zig.SwitchExpr or Zig.SwitchExprTrailing => LowerSwitchType(type),
        // A call in a type position that no case above could evaluate: name the callee and where it is
        // written, since "CallArgs" alone gave no way to find which of a std module's calls it was.
        Zig.CallArgs uca => throw UnevaluatedTypeCall(uca.Arg0),
        Zig.CallNoArgs ucn => throw UnevaluatedTypeCall(ucn.Arg0),
        _ => throw new IrUnsupportedException("zig type: " + (type.Content?.GetType().Name ?? "null")),
    };

    /// <summary>Reify an INLINE named-field struct type (<c>fn f() struct { a: u8 }</c>, a field / param
    /// / typed-var annotation — road-to-zig-std S9, grammar #90) into a synthesized named struct type.
    /// A Zig struct type is nominal by its declaration SITE, so each inline <c>struct {…}</c> occurrence
    /// is its own type: the synthesized name is memoized by the AST occurrence
    /// (<see cref="_inlineStructNames"/>, reference-keyed), so the same site lowered across passes reifies
    /// ONE registered type, while two distinct sites get distinct types. The field layout registers into
    /// the shared IR aggregate table exactly like a named <c>struct {…}</c> — so a <c>.{ … }</c> literal
    /// against this type and <c>p.field</c> access resolve through the ordinary named-struct machinery.
    /// V1 is fields-only: a method / <c>const</c> / nested-container member is a loud cut (it needs a
    /// named container decl — symmetric with the W2 in-fn container and the W4 returned struct).</summary>
    private CType ReifyInlineStruct(Item occurrence, Item? fieldDecls)
    {
        if (_inlineStructNames.TryGetValue(occurrence, out var existing)) { return new CType.Named(existing); }
        var name = QualifyTypeName($"__AnonStruct{_inlineStructNames.Count}");   // the counter is per module
        // Record the name BEFORE lowering the fields, so a self-referential field (`next: ?*Self`
        // resolved via @This()) or a re-entrant lowering of the same site sees the in-progress type.
        _inlineStructNames[occurrence] = name;
        var (fields, methods, consts, containers) = fieldDecls is { } fd
            ? SplitMembers(fd)
            : (new List<Item>(), new List<Item>(), new List<Item>(), new List<Item>());
        if (methods.Count > 0 || consts.Count > 0 || containers.Count > 0)
        {
            throw new IrUnsupportedException(
                "zig: an inline `struct {…}` type is fields-only (road-to-zig-std S9) — a method, `const`, or "
                + "nested-container member needs a named container decl (`const T = struct { … };`)");
        }
        RegisterStruct(name, fields);
        return new CType.Named(name);
    }

    /// <summary>The error for a call in a type position that is not a type-returning generic dotcc could
    /// evaluate, naming the callee's dotted spelling and its file and line.</summary>
    private IrUnsupportedException UnevaluatedTypeCall(Item callee)
    {
        static string Spell(Item e) => e.Content switch
        {
            Zig.Ident id => Tok(id.Arg0),
            Zig.Field f => Spell(f.Arg0) + "." + Tok(f.Arg2),
            _ => e.Content?.GetType().Name ?? "?",
        };
        static int Line(Item e) => e.Content switch
        {
            Zig.Ident id => id.Arg0.Position.Line,
            Zig.Field f => Line(f.Arg0),
            _ => 0,
        };
        // A declaration the resilient parse SKIPPED is the real wall (hash_map's `Custom`, reached through
        // `pub const HashMapUnmanaged = Custom;`): raise its parse error, in whichever module owns it.
        switch (callee.Content)
        {
            case Zig.Ident id:
                RaiseIfSkippedAlongAliases(Tok(id.Arg0));
                break;
            case Zig.Field f when ResolveModulePath(f.Arg0)?.Lowering is { } owner:
                owner.RaiseIfSkippedAlongAliases(Tok(f.Arg2));
                break;
        }
        return new IrUnsupportedException(
            $"zig type: `{Spell(callee)}(…)` in a type position is not a type-returning generic dotcc could "
            + $"evaluate ({_fileStem ?? "?"}.zig line {Line(callee)})");
    }

    /// <summary>The enum twin of <see cref="ReifyInlineStruct"/>: an anonymous <c>enum { pos, neg }</c> in a
    /// type slot (std's <c>parseIntWithSign(…, comptime sign: enum { pos, neg })</c>) reifies ONE enum per
    /// source site (<c>__AnonEnum&lt;n&gt;</c>, module-qualified in an imported module), memoized by the
    /// occurrence, so every instance of a generic whose parameter spells it shares the type. Fields-only,
    /// like the inline struct: a method or <c>const</c> member needs a named <c>const E = enum {…};</c>.</summary>
    private CType ReifyInlineEnum(Item occurrence, Item enumFields)
    {
        if (_inlineStructNames.TryGetValue(occurrence, out var existing)) { return _containerTypes[existing]; }
        var (_, methods, consts) = SplitEnumMembers(enumFields);
        if (methods.Count > 0 || consts.Count > 0)
        {
            throw new IrUnsupportedException(
                "zig: an inline `enum {…}` type is fields-only — a method or `const` member needs a named "
                + "enum decl (`const E = enum { … };`)");
        }
        var name = QualifyTypeName($"__AnonEnum{_inlineStructNames.Count}");   // shares the per-module counter
        _inlineStructNames[occurrence] = name;
        using (EnterContainer(name)) { RegisterEnumZig(name, null, enumFields); }
        return _containerTypes[name];
    }

    /// <summary>Reify an inline <c>union(enum) { … }</c> / <c>union { … }</c> type at its occurrence (task #104,
    /// <c>fn f(x: union(enum) { a: u8, b: u16 })</c>): registered once per occurrence under an anonymous name, exactly as a
    /// named <c>const U = union(enum) { … };</c> is, so a switch over it and its capture prongs resolve the same way.
    /// Fields only, like the inline enum and struct: a method needs a named union.</summary>
    private CType ReifyInlineUnion(Item occurrence, Item variants, bool tagged)
    {
        if (_inlineStructNames.TryGetValue(occurrence, out var existing)) { return _containerTypes[existing]; }
        var name = QualifyTypeName($"__AnonUnion{_inlineStructNames.Count}");   // shares the per-module counter
        _inlineStructNames[occurrence] = name;
        _containerTypes[name] = new CType.Named(name);
        List<Item> methods;
        using (EnterContainer(name))
        {
            methods = tagged ? RegisterUnion(name, variants) : RegisterUnionUntagged(name, variants);
        }
        if (methods.Count > 0)
        {
            throw new IrUnsupportedException(
                "zig: an inline `union {…}` type is fields-only; a method needs a named union decl (`const U = union(enum) { … };`)");
        }
        return _containerTypes[name];
    }

    /// <summary>Lower the single type argument of a curated generic std type
    /// (<c>std.ArrayList(i32)</c> — wall-plan W0): flatten the parsed ArgList, require exactly
    /// one argument, and lower it as a Type. A wrong arity is a clear error naming the path.</summary>
    private CType LowerSingleTypeArg(Item argList, string path)
    {
        var args = Flatten(argList);
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `{path}(…)` takes exactly one type argument; got {args.Count}");
        }
        return LowerType(args[0]);
    }

    // ---- the curated-std registry ------------------------------------------
    //
    // ONE row per modeled std path, consulted by every position that resolves a
    // dotted std path (type position, type-alias RHS, value position, the
    // known-allocator predicate) — so adding a curated path is a table edit, not a
    // hunt across dispatch ladders, and the "not modeled" error messages list the
    // curated set straight from the table keys. Bespoke METHOD-call handling
    // (the std.mem helper cluster, FixedBufferAllocator/ArenaAllocator .init, the
    // ArrayList member set) stays hand-written in LowerMethodCall — each of those
    // is genuine lowering logic, not a name→node mapping.

    /// <summary>Non-generic std paths that resolve in TYPE position → the CType factory.
    /// (The runtime carrier types — VTable / Alignment / FBA / Arena — live in ZigAlloc.cs.)</summary>
    private static readonly Dictionary<string, System.Func<CType>> StdTypes = new(System.StringComparer.Ordinal)
    {
        ["std.mem.Allocator"] = static () => new CType.Allocator(),
        // A user-constructed custom allocator (Milestone W, part 1b): the vtable struct type
        // and the alignment a vtable function receives.
        ["std.mem.Allocator.VTable"] = static () => new CType.Named(VTableTypeName),
        ["std.mem.Alignment"] = static () => new CType.Named(AlignmentTypeName),
        ["std.heap.FixedBufferAllocator"] = static () => new CType.Named(FbaTypeName),
        ["std.heap.ArenaAllocator"] = static () => new CType.Named(ArenaTypeName),
    };

    /// <summary>GENERIC std paths — spelled as a CALL with one type argument in type position
    /// (<c>std.ArrayList(i32)</c>, wall-plan W0) → the factory over the lowered element type.</summary>
    private static readonly Dictionary<string, System.Func<CType, CType>> StdGenericTypes = new(System.StringComparer.Ordinal)
    {
        ["std.ArrayList"] = static elem => new CType.ZigList(elem),
    };

    /// <summary>Std paths that are a VALUE — the statically-known default allocators, each →
    /// its <see cref="AllocKind"/> (the devirtualization discriminator; both rows today are the
    /// C heap, so a value use materializes <c>ZigAlloc.CHeap()</c>).</summary>
    private static readonly Dictionary<string, AllocKind> StdAllocatorValues = new(System.StringComparer.Ordinal)
    {
        ["std.heap.page_allocator"] = AllocKind.CHeap,
        ["std.heap.c_allocator"] = AllocKind.CHeap,
    };

    /// <summary>The functions each curated std NAMESPACE lowers by hand (<see cref="LowerStdMemCall"/>,
    /// <see cref="LowerStdDebugCall"/>, <see cref="LowerStdTestingCall"/>). A namespace is not a curated
    /// PATH the way a type is: <c>std.mem</c> also holds hundreds of functions dotcc never modeled, so only
    /// these members claim the call; with a real std tree configured any other member falls through to
    /// source navigation (road-to-zig-std G2, the curated-first rule applied per member).</summary>
    private static readonly Dictionary<string, HashSet<string>> CuratedStdNamespaceFns = new(System.StringComparer.Ordinal)
    {
        ["std.mem"] = new(System.StringComparer.Ordinal)
            { "eql", "copyForwards", "span", "zeroes", "asBytes", "sliceAsBytes", "bytesAsValue", "bytesToValue" },
        ["std.debug"] = new(System.StringComparer.Ordinal) { "print" },
        ["std.testing"] = new(System.StringComparer.Ordinal)
            { "expect", "expectEqual", "expectError", "expectEqualStrings", "expectEqualSlices" },
    };

    /// <summary>True when the call <c>namespace.method(…)</c> takes the curated lowering: the member is
    /// curated, or no std source tree is configured (so the curated "not modeled" message, which lists
    /// what IS modeled, is the most useful error). False sends it to the module graph instead.</summary>
    private bool TakesCuratedStdCall(string ns, string method)
        => CuratedStdNamespaceFns[ns].Contains(method) || _moduleGraph?.StdRootPath is null;

    /// <summary>True when a dotted expression resolves to a std path the CURATED model OWNS — a
    /// <see cref="StdTypes"/>, <see cref="StdGenericTypes"/> or <see cref="StdAllocatorValues"/> row.
    /// The discriminator that keeps real-std source navigation (road-to-zig-std S1/S2, active when a
    /// <c>DOTCC_ZIG_LIB_DIR</c> std tree is configured) from shadowing a curated path: upstream re-exports
    /// its allocators as whole files, so <c>std.heap.FixedBufferAllocator</c> is BOTH a curated type and a
    /// navigable module, and only the curated model can lower it. Registry-driven, so curating a new path
    /// automatically makes it win — no second list to keep in sync.</summary>
    private bool IsCuratedStdPath(Item expr)
        => TryResolveStdPath(expr, out var path)
        && (StdTypes.ContainsKey(path) || StdGenericTypes.ContainsKey(path) || StdAllocatorValues.ContainsKey(path));

    /// <summary>Lower a dotted std type (Milestone F): a <see cref="StdTypes"/> row — e.g.
    /// <c>std.mem.Allocator</c> → the runtime <see cref="CType.Allocator"/> fat pointer,
    /// <c>std.heap.FixedBufferAllocator</c> → the concrete <see cref="CType.Named"/> bump
    /// allocator — or, when no curated row claims the path, the type the MODULE GRAPH resolves it to
    /// (road-to-zig-std S4d: <c>util.Point</c>, <c>std.ascii.Pair</c>). Errors when neither has it: a
    /// navigable module that declares no such type says so by file + name, an unmodeled <c>std.…</c>
    /// path lists the curated types, and a chain rooted at neither is not a known type at all.</summary>
    private CType LowerStdType(Item f)
    {
        // The CURATED model is checked FIRST, in type position as in every other (road-to-zig-std S1's
        // rule; see IsCuratedStdPath for why that ordering is load-bearing).
        var isStdPath = TryResolveStdPath(f, out var path);
        if (isStdPath && StdTypes.TryGetValue(path, out var make))
        {
            return make();
        }
        // Not a curated path: fall back to REAL-SOURCE navigation (road-to-zig-std S4d) — the type
        // analogue of the expression-position fallback in LowerMethodCall. `mod.Name` (`util.Point`,
        // `std.ascii.Pair`) resolves `mod` to its module and reads `Name` from the container types that
        // module registered when it was prepared. A resolvable module that declares no such type is a
        // LOUD error naming both, not a fall-through: the program did reach it.
        var member = (Zig.Field)f.Content!;
        if (!IsCuratedStdPath(f) && ResolveModulePath(member.Arg0) is { } navMod)
        {
            var name = Tok(member.Arg2);
            if (navMod.Lowering?.ResolveExportedType(name) is { } navType) { return navType; }
            // `std.Io.Writer` spelled directly: the member is itself a file-as-struct module.
            if (ResolveModulePath(f)?.Lowering?.FileStructType is { } fileType) { return fileType; }
            navMod.Lowering?.RaiseIfSkippedDecl(name);
            throw new IrUnsupportedException(
                $"zig module '{System.IO.Path.GetFileName(navMod.Path)}' declares no type '{name}'");
        }
        // `std.Target.Cpu.Arch`: a type NESTED in a container another module declares — the module prefix,
        // then the owner's nested containers / type consts, segment by segment.
        if (!IsCuratedStdPath(f) && TryResolveModuleNestedType(f) is { } nestedInModule) { return nestedInModule.Type; }
        if (isStdPath)
        {
            throw new IrUnsupportedException(
                $"zig type `{path}` is not modeled (std types: {string.Join(", ", StdTypes.Keys)})");
        }
        throw new IrUnsupportedException(
            $"zig type: a dotted type `{Tok(member.Arg2)}` that is not a modeled std path");
    }

    /// <summary>An expression with each read of a comptime const whose initializer did not fold where it was declared
    /// (<see cref="_unfoldedConstInits"/>: a call, <c>const ceil_bytes = comptime math.divCeil(u16, bits, 8) catch
    /// unreachable;</c> in std.Random.int, task #117) replaced by that initializer, through arithmetic, casts and parentheses,
    /// so a comptime position (an array extent, <c>@Int</c>'s width <c>ceil_bytes * 8</c>) can evaluate it now.</summary>
    private CExpr InlineUnfoldedConsts(CExpr e) => e switch
    {
        VarRef { Sym: var sym } when _unfoldedConstInits.TryGetValue(sym, out var init) => InlineUnfoldedConsts(init),
        Binary b => b with { Left = InlineUnfoldedConsts(b.Left), Right = InlineUnfoldedConsts(b.Right) },
        Unary u => u with { Operand = InlineUnfoldedConsts(u.Operand) },
        Cast c => c with { Operand = InlineUnfoldedConsts(c.Operand) },
        Paren p => p with { Inner = InlineUnfoldedConsts(p.Inner) },
        _ => e,
    };

    /// <summary>zig's <c>void</c> as DATA (task #114): the runtime's empty <c>Unit</c> struct, since C# has no <c>void</c>
    /// element, generic argument or storage.</summary>
    private static readonly CType ZigUnitType = new CType.Named("Unit");

    /// <summary>Lower an element type of a slice, many-item pointer, array, optional or tuple. A <c>void</c> element
    /// (std.StaticStringMap(void)'s <c>[*]const V</c>, a <c>?void</c> result, <c>[3]void{ {}, {}, {} }</c>, task #114) is
    /// <see cref="ZigUnitType"/>: zig stores nothing, and C# needs a real type there. A single pointer to void stays an
    /// opaque <c>void*</c> (see <see cref="LowerPointee"/>), as does a C pointer.</summary>
    private CType LowerDataType(Item type)
    {
        var lowered = LowerType(type);
        return lowered.Unqualified is CType.VoidType ? ZigUnitType : lowered;
    }

    /// <summary>Lower a tuple TYPE body (the <c>T1, T2, …</c> inside <c>struct { … }</c> at a Type
    /// position) to a <see cref="CType.Tuple"/>. V1 supports arity 1..7 (an empty tuple and
    /// arity &gt; 7 — which would need ValueTuple's <c>TRest</c> nesting — are deferred with a clear
    /// error). Each element is itself a <see cref="LowerType"/>, so nested tuple types compose.</summary>
    private CType LowerTupleType(Item tupleTypes)
    {
        var elems = Flatten(tupleTypes).Select(LowerDataType).ToList();
        return new CType.Tuple(elems);
    }

    /// <summary>The type the enclosing container's <c>@This()</c> resolves to — the struct/enum
    /// whose method is currently being lowered, else the file-as-struct type when the file has
    /// top-level fields (road-to-zig-std G3). An error when neither is in scope.</summary>
    private CType CurrentContainerType() =>
        (_currentContainer ?? _fileContainer) is { } c && _containerTypes.TryGetValue(c, out var t)
            ? t
            : throw new IrUnsupportedException("zig `@This()` is only supported inside a container method");

    /// <summary>Resolve a Zig type spelled as a bare identifier: a container-scoped self alias
    /// (<c>const Self = @This();</c>) wins first, then a registered container (struct →
    /// <see cref="CType.Named"/>, enum → <see cref="CType.Enum"/>), then the primitive table — so a
    /// user type name (or a self alias inside its own method) resolves before <see cref="LowerPrim"/>
    /// would throw on it.</summary>
    private CType LowerTypeName(string name)
    {
        // A `const X = @compileError("…");` tombstone named in a TYPE position (road-to-zig-std S7) —
        // the reference is what zig analyses, so this is where the author's message is raised.
        RaiseIfPoisoned(name);
        if (ResolveSelfAlias(name) is { } alias) { return alias; }
        if (ResolveContainerTypeConst(name) is { } typeConst) { return typeConst; }
        // A nested container type (`const Inner = struct {…};` inside the current container — S9 #89),
        // resolved by plain name while a method of the parent is being lowered.
        if (ResolveNestedType(name) is { } nested) { return nested; }
        // A `const T = <type>;` type alias (wall-plan W1) — resolved ahead of containers/primitives so
        // an aliased name (`var x: T = 5;`, `*T`, `[]T`) composes through the ordinary type prefixes.
        if (_typeAliases.TryGetValue(name, out var aliased)) { return aliased; }
        // `type` is Zig's type-of-types — a comptime-ONLY type. A runtime `var t: type` (or a
        // `fn f(t: type)` param, which is W3's comptime param) is illegal in real zig too; reject
        // loudly rather than fall through to LowerPrim's opaque "not supported" message.
        if (name == "type")
        {
            throw new IrUnsupportedException(
                "zig: `type` is a comptime-only type — a runtime `var`/param of type `type` is illegal "
                + "(use a `const Alias = SomeType;` type alias, or a `comptime` parameter once generics land)");
        }
        if (_containerTypes.TryGetValue(name, out var ct)) { return ct; }
        // A name bound to a file-as-struct MODULE (`const Writer = std.Io.Writer;`), resolved on demand.
        if (TryResolveModuleTypeAlias(name, out var fileType)) { return fileType; }
        if (TryDeferredTypeAlias(name, out var deferredAlias)) { return deferredAlias; }
        // An error-set name used as a plain VALUE type — `fn f(e: E)`, `var x: E`, a non-`!T`
        // error return `fn g() E` — or the open `anyerror`. Lowers to the flat erased error code
        // (`CType.ErrorSet`, rendered `ushort`): the error VALUE itself, NOT an `E!T` error union
        // (handled separately as `Zig.ErrUnion`). Set membership stays erased at runtime; the
        // declared-set table only drives the compile-time rejection in part 3a. (Milestone X, part 3b.)
        if (name == "anyerror" || _errorSets.Contains(name)) { return CType.ErrorSet; }
        if (TryLowerPrim(name, out var prim)) { return prim; }
        RaiseIfSkippedDecl(name);   // a type this module declares, whose declaration did not parse
        return LowerPrim(name);
    }

    /// <summary>Resolve a type name that is a container-scoped self alias (<c>const Self =
    /// @This();</c>), valid only while a method of the declaring container is being lowered
    /// (<see cref="_currentContainer"/> set). Returns <c>null</c> when it is not such an alias.</summary>
    private CType? ResolveSelfAlias(string name)
    {
        // Innermost first, then outward: a nested container sees its enclosing container's aliases and type
        // consts (hash_map's `Iterator` names `Custom`'s `Size`), zig's lexical scoping.
        for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
        {
            if (_selfAliases.TryGetValue(c, out var m) && m.TryGetValue(name, out var t)) { return t; }
        }
        return null;
    }

    /// <summary>Resolve a type name that is a TYPE const member of the container in scope or of one enclosing
    /// it (hash_map's <c>const Metadata = packed struct { const FingerPrint = u7; fingerprint: FingerPrint, … }</c>),
    /// evaluated on first use with that container current and cached as a scoped alias, so a field, a
    /// signature or a body names it plainly. A reified instance evaluates its own type consts eagerly, while
    /// its seeds are live; this is the lazy path for every other container. <c>null</c> when not such a name.</summary>
    private CType? ResolveContainerTypeConst(string name)
    {
        for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
        {
            if (TryContainerTypeConst(c, name) is { } resolved) { return resolved; }
        }
        return null;
    }

    /// <summary>The TYPE const <paramref name="name"/> of exactly <paramref name="container"/>, evaluated on
    /// first use in that container's scope and cached as a scoped alias; null when it declares no such type
    /// const. The one-container step of <see cref="ResolveContainerTypeConst"/>, and what a qualified
    /// <c>Shapes.Bytes</c> asks.</summary>
    private CType? TryContainerTypeConst(string container, string name)
    {
        if (_selfAliases.TryGetValue(container, out var known) && known.TryGetValue(name, out var cached)) { return cached; }
        if (!_containerConsts.TryGetValue(container, out var consts)
            || !consts.TryGetValue(name, out var entry)
            || entry.Item1 is not null
            || !_typeConstsInFlight.Add((container, name)))
        {
            return null;
        }
        try
        {
            CType resolved;
            using (EnterContainer(container))
            {
                // The shape test inside the scope too: it may evaluate a member call (`Pair(u8)`).
                if (!IsTypeConstMember(entry.Item2)) { return null; }
                var (lowered, bits) = LowerComptimeTypeExpr(container, entry.Item2);
                resolved = lowered;
                if (bits is { } b) { _typeConstBits[(container, name)] = b; }
            }
            if (!_selfAliases.TryGetValue(container, out var scoped))
            {
                scoped = new Dictionary<string, CType>(System.StringComparer.Ordinal);
                _selfAliases[container] = scoped;
            }
            scoped[name] = resolved;
            return resolved;
        }
        finally { _typeConstsInFlight.Remove((container, name)); }
    }

    /// <summary>The declared integer width each container TYPE const spelled (hash_map's
    /// <c>pub const Hash = u64;</c>, Metadata's <c>const FingerPrint = u7;</c>), keyed by (container, name), so
    /// <c>@typeInfo(FingerPrint).int.bits</c> answers 7, not the 8 of the byte it lowers to.</summary>
    private readonly Dictionary<(string Container, string Name), int> _typeConstBits = new();

    /// <summary>The declared width of the container type const <paramref name="name"/> visible from the current
    /// container (innermost first), or null.</summary>
    private int? ContainerTypeConstBits(string name)
    {
        for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
        {
            if (_typeConstBits.TryGetValue((c, name), out var bits)) { return bits; }
            if (_selfAliases.TryGetValue(c, out var aliases) && aliases.ContainsKey(name)) { return null; }
        }
        return null;
    }

    /// <summary>The (container, name) type consts <see cref="ResolveContainerTypeConst"/> is evaluating, so a
    /// const whose shape test names itself does not recurse.</summary>
    private readonly HashSet<(string Container, string Name)> _typeConstsInFlight = new();

    /// <summary>Resolve a type name that is a NESTED container decl of the container currently in scope
    /// (<c>const Inner = struct {…};</c> inside <c>Parent</c> — road-to-zig-std S9, grammar #89) or of
    /// any container enclosing it, valid only while <see cref="_currentContainer"/> is that container or
    /// a descendant — its fields, consts, method signatures and bodies — so the plain name <c>Inner</c>
    /// resolves without leaking, and two parents may nest a same-named type without colliding.
    /// <c>null</c> when not such a name.</summary>
    private CType? ResolveNestedType(string name)
    {
        // Innermost first, then outward through the enclosing containers — zig's lexical scoping, so a
        // nested type's own method or field can name a sibling nested type (or an uncle) plainly.
        for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
        {
            if (_nestedContainerTypes.TryGetValue(c, out var m) && m.TryGetValue(name, out var t)) { return t; }
        }
        return null;
    }

    /// <summary>A module-qualified NESTED type (<c>std.Target.Cpu.Arch</c>): the first segment after a module is a
    /// type that module declares, and each further one a nested container or type const of the previous, looked
    /// up in the module that owns it. Returns the type with its owner, or null.</summary>
    private (CType Type, ZigLowering Owner)? TryResolveModuleNestedType(Item dotted)
    {
        if (dotted.Content is not Zig.Field f) { return null; }
        var name = Tok(f.Arg2);
        if (ResolveModulePath(f.Arg0) is { Lowering: { } module } && module.ResolveExportedType(name) is { } top)
        {
            return (top, module);
        }
        if (f.Arg0.Content is not Zig.Field || TryResolveModuleNestedType(f.Arg0) is not { } outer) { return null; }
        var outerName = outer.Type.Unqualified switch
        {
            CType.Named n => n.Name,
            CType.Enum e => e.Name,
            _ => null,
        };
        if (outerName is null) { return null; }
        if (outer.Owner._nestedContainerTypes.TryGetValue(outerName, out var nested) && nested.TryGetValue(name, out var inner))
        {
            return (inner, outer.Owner);
        }
        return outer.Owner.TryContainerTypeConst(outerName, name) is { } typeConst ? (typeConst, outer.Owner) : null;
    }

    /// <summary>Resolve <c>Parent.Inner</c> (or <c>Outer.Mid.Inner</c>) to a NESTED container type, or to a
    /// TYPE const of the parent (<c>Shapes.Bytes</c>, <c>Map.KeyIterator</c>), or null when the base is not a
    /// container this module declares or has no such member — so the caller falls through to the std /
    /// module-graph resolvers unchanged. The base resolves the way a
    /// bare type name does (<see cref="TryLookupContainerType"/>, which also sees an in-scope nested
    /// name), then each segment steps into that container's nested map.</summary>
    private CType? TryResolveQualifiedNestedType(Item dotted)
    {
        if (dotted.Content is not Zig.Field f) { return null; }
        // `h.H` with `const h = @import("h.zig");`: a type the MODULE declares (so `h.H.hash(…)` is a static
        // call). A root unit's named import only: a lazily prepared module must not fan out into what its
        // top-level aliases name (std.zig's `pub const BufMap = @import("buf_map.zig").BufMap;` would prepare
        // buf_map.zig at every std import), and a std path is left to the std resolvers. Inside a function BODY a lazy
        // module resolves the same way (no preparation is running then): std.Io.Writer.Allocating.sendFile's
        // `File.Handle` is File.zig's `pub const Handle`, and File's own struct (platform state) never has to lower.
        // `pub const Md5 = @import("crypto/md5.zig").Md5;` inside std.crypto's `hash` namespace: an INLINE import as the base,
        // reached only when that container type const is resolved on demand (so preparing crypto.zig never fans out).
        if ((f.Arg0.Content is Zig.Ident && (!_lazy || _currentFnName.Length > 0) && !TryResolveStdPath(dotted, out _)
             || f.Arg0.Content is Zig.BuiltinCall { Arg0: var importTok } && Tok(importTok) == "@import"
                && (!_lazy || _typeConstsInFlight.Count > 0))
            && ResolveModulePath(f.Arg0) is { Lowering: { } moduleLowering }
            && moduleLowering.ResolveExportedType(Tok(f.Arg2)) is { } moduleType)
        {
            return moduleType;
        }
        CType? baseType = f.Arg0.Content switch
        {
            Zig.Ident id when TryLookupContainerType(Tok(id.Arg0), out var ct) => ct,
            Zig.Field => TryResolveQualifiedNestedType(f.Arg0),
            // Through a type call: `Outer(u16, null).Managed`, `std.ArrayList(u8).Slice`-shaped.
            Zig.CallArgs or Zig.CallNoArgs => TryEvalTypeReturningCall(f.Arg0, out var called) ? called : null,
            _ => null,
        };
        var baseName = baseType?.Unqualified switch
        {
            CType.Named n => n.Name,
            CType.Enum e => e.Name,
            _ => null,
        };
        if (baseName is null) { return null; }
        return _nestedContainerTypes.TryGetValue(baseName, out var nested) && nested.TryGetValue(Tok(f.Arg2), out var inner)
            ? inner
            : TryContainerTypeConst(baseName, Tok(f.Arg2));
    }

    /// <summary>Look up the container type named at a use site — a registered struct/enum/union
    /// name, a container-scoped self alias (<c>Self</c>) when inside that container's method, or a
    /// type ALIAS bound to one. Drives <c>Type.func()</c> / <c>EnumName.member</c> resolution (a self
    /// alias maps through to the real container type, so <c>Self.init(…)</c> binds to the same mangled
    /// method as the explicit name).</summary>
    /// <summary>The container TYPE a comptime pointer-to-type expression names (task #85): <c>&amp;Backend64_TablesFull</c>, a
    /// name already bound to one, or a comptime <c>if</c> / <c>switch</c> whose taken arm is one. Null for anything else,
    /// so a caller may probe.</summary>
    private CType? TryComptimeTypePointer(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.Grouped g:
                return TryComptimeTypePointer(g.Arg1);
            case Zig.PreAddrOf { Arg1.Content: Zig.Ident id } when _symbols.Resolve(Tok(id.Arg0)) is null
                                                                   && TryLookupContainerType(Tok(id.Arg0), out var named)
                                                                   && named.Unqualified is CType.Named:
                return named;
            case Zig.PreAddrOf { Arg1: { Content: Zig.Field } dotted } when TryResolveQualifiedNestedType(dotted) is { Unqualified: CType.Named } nested:
                return nested;
            case Zig.IfExpr ie when TryFoldComptimeCondition(ie.Arg2) is { } taken:
                return TryComptimeTypePointer(taken ? ie.Arg4 : ie.Arg6);
            case Zig.SwitchExpr or Zig.SwitchExprTrailing:
            {
                var (subject, prongs) = expr.Content switch
                {
                    Zig.SwitchExpr s => (s.Arg2, s.Arg5),
                    Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
                    _ => (expr, expr),
                };
                // Only a switch whose EVERY non-`unreachable` arm is a type pointer: an ordinary value switch must not be
                // selected (or lowered) here.
                if (!Flatten(prongs).All(p => DecomposeProng(p).Expr is not { } arm || IsUnreachableItem(arm)
                                              || LooksLikeTypePointer(arm)))
                {
                    return null;
                }
                if (SelectComptimeProng(subject, prongs, out var payload) is not { Expr: { } armItem } prong) { return null; }
                EnterComptimeProng(prong, payload);
                try { return TryComptimeTypePointer(armItem); }
                finally { ExitComptimeProng(); }
            }
            default:
                return null;
        }
    }

    /// <summary>True for an expression SHAPED like a pointer to a type: <c>&amp;Name</c>, <c>&amp;a.b</c>, or a comptime
    /// <c>if</c> / <c>switch</c> of those (checked without lowering anything).</summary>
    private static bool LooksLikeTypePointer(Item e) => e.Content switch
    {
        Zig.Grouped g => LooksLikeTypePointer(g.Arg1),
        Zig.PreAddrOf { Arg1.Content: Zig.Ident or Zig.Field } => true,
        Zig.IfExpr ie => LooksLikeTypePointer(ie.Arg4) && LooksLikeTypePointer(ie.Arg6),
        _ => false,
    };

    private bool TryLookupContainerType(string name, out CType type)
    {
        var alias = ResolveSelfAlias(name);
        if (alias is not null) { type = alias; return true; }
        if (ResolveContainerTypeConst(name) is { } typeConst) { type = typeConst; return true; }
        var nested = ResolveNestedType(name);
        if (nested is not null) { type = nested; return true; }
        if (_containerTypes.TryGetValue(name, out type!)) { return true; }
        if (!_typeAliases.ContainsKey(name) && TryResolveModuleTypeAlias(name, out type)) { return true; }
        if (TryDeferredTypeAlias(name, out type)) { return true; }
        // A type ALIAS naming a container — `const S = Stack(u8, 4); S.init()` (road-to-zig-std G4). A
        // REIFIED type-returning generic has no source-level name of its own (its mangled name is
        // synthesized), so the alias is the only way to reach its static methods / consts; treat it
        // exactly like the container's own name. Safe for a non-container alias (`const I = i32;`):
        // every caller additionally guards on ContainerTypeName / CType.Enum, so such an alias falls
        // through to the ordinary value paths unchanged.
        return _typeAliases.TryGetValue(name, out type!);
    }

    /// <summary>Lower a Zig optional payload type: a pointer (or function-pointer) payload stays a
    /// bare nullable pointer (the niche — a `delegate*` / `T*` is null when none); any other payload
    /// is wrapped in <see cref="CType.Optional"/> (→ C# <c>T?</c>).</summary>
    private CType LowerOptional(Item innerType)
    {
        var inner = LowerDataType(innerType);
        return inner.Unqualified is CType.Pointer or CType.Func ? inner : new CType.Optional(inner);
    }

    /// <summary>Form a pointer to <paramref name="pointee"/>, collapsing a pointer-to-FUNCTION to
    /// the bare <see cref="CType.Func"/> (Milestone W, part 1a). In dotcc's IR a function pointer
    /// is a bare <c>Func</c> rendered as a <c>delegate*&lt;…&gt;</c> (the C-frontend convention), so
    /// <c>*const fn (…) R</c> / <c>*fn (…) R</c> lower to the same <c>Func</c> as a bare <c>fn</c>
    /// type — keeping every downstream call / coercion / sizeof path identical to C's.</summary>
    private static CType PointerTo(CType pointee) =>
        pointee.Unqualified is CType.Func ? pointee : new CType.Pointer(pointee);

    /// <summary>Lower a single-item pointer's pointee, or <c>void</c> (an OPAQUE pointer) when the pointee
    /// is a lazy module's container whose registration failed (road-to-zig-std G3). A pointer needs no
    /// layout of what it points to, so a signature that merely passes one along lowers: <c>std.Io.Writer</c>'s
    /// <c>VTable.sendFile</c> takes a <c>*File.Reader</c>, and <c>File</c> sits on the platform floor
    /// (<c>handle: std.posix.fd_t</c>), yet a program formatting into a buffer never touches a file. A use
    /// that needs the layout (a field access, a deref, a by-value copy) still fails loudly, on the
    /// <c>void*</c>. Only a lazy module's container can be failed (a root unit's is an error where it
    /// stands), so user code keeps its direct diagnostics.</summary>
    private CType LowerPointee(Item pointee)
    {
        CType lowered;
        try { lowered = LowerType(pointee); }
        catch (ZigFailedContainerException) { return CType.Void; }
        // A `*T` with `T = void` (std.mem.swap(void, …) in std.StaticStringMap(void), task #114) points at DATA, as a slice
        // of void does; only `*anyopaque` is an opaque `void*`.
        return lowered.Unqualified is CType.VoidType && !IsAnyopaqueSpelling(pointee) ? ZigUnitType : lowered;
    }

    /// <summary>Is this type spelled <c>anyopaque</c>, zig's opaque pointee (a <c>void*</c>, never data)?</summary>
    private static bool IsAnyopaqueSpelling(Item type) => type.Content is Zig.Ident id && Tok(id.Arg0) == "anyopaque";

    /// <summary>Lower a function-pointer type's parameter list (the reused <c>Params</c>: each a
    /// named <c>IDENT : Type</c>) to its element types — the names are irrelevant to the type. A
    /// variadic marker (<c>...</c>) in a fn-pointer type is rejected (deferred).</summary>
    private IReadOnlyList<CType> LowerParamTypes(Item paramsItem)
    {
        var types = new List<CType>();
        foreach (var p in Flatten(paramsItem))
        {
            if (p.Content is not Zig.Param pm)
            {
                throw new IrUnsupportedException(
                    "a variadic / unnamed parameter in a function-pointer type is not supported yet");
            }
            types.Add(LowerType(pm.Arg2));
        }
        return types;
    }

    /// <summary>Extract the parameter types of a function-pointer TYPE's <c>FnTypeParams</c> list —
    /// each element is either a bare <c>Type</c> (<see cref="Zig.FnTypeParamUnnamed"/>, the common
    /// unnamed form) or <c>IDENT : Type</c> (<see cref="Zig.FnTypeParamNamed"/>, the name ignored —
    /// only the types matter to the Func type).</summary>
    private IReadOnlyList<CType> LowerFnTypeParams(Item paramsItem)
    {
        var types = new List<CType>();
        foreach (var p in Flatten(paramsItem))
        {
            types.Add(p.Content switch
            {
                Zig.FnTypeParamUnnamed u => LowerType(u.Arg0),
                Zig.FnTypeParamNamed n   => LowerType(n.Arg2),
                Zig.FnTypeParamComptime  => throw new IrUnsupportedException(
                    "a function TYPE with a `comptime` parameter is a generic function type, which has no function-pointer "
                    + "form (std.Options' `logFn`)"),
                _ => throw new IrUnsupportedException(
                    "a function-pointer-type parameter must be a `Type` or `IDENT : Type`"),
            });
        }
        return types;
    }

    /// <summary>True when an optional <c>CallConv</c> node names the C calling convention
    /// (<c>callconv(.c)</c> / <c>callconv(.C)</c>) — the only convention dotcc honors on a
    /// fn-pointer type, marking the <see cref="CType.Func"/> native so the C# backend renders
    /// a <c>delegate* unmanaged[Cdecl]&lt;…&gt;</c> instead of the managed <c>delegate*&lt;…&gt;</c>.
    /// The absent (epsilon) <c>CallConv</c> — a null <c>Content</c> — and every other convention
    /// return false (managed). Real Zig 0.17 spells it lowercase <c>.c</c>; <c>.C</c> is accepted
    /// for the older spelling.</summary>
    private static bool IsCCallConv(Item callConv) =>
        callConv.Content is Zig.CallConv cc
        && cc.Arg2.Content is Zig.EnumLit e
        && Tok(e.Arg1) is "c" or "C";

    /// <summary>The compile-time sentinel value of a <c>[N:s]T</c> array type (Milestone Z lifts the
    /// earlier zero-only restriction). V1 requires a literal sentinel; it is materialized into the
    /// trailing storage slot at the decl site (a zero rides C#'s zero-fill, a non-zero is written
    /// explicitly). Returns 0 for a non-sentinel type.</summary>
    private long SentinelArrayValue(Item? typeItem)
    {
        if (typeItem?.Content is not Zig.TySentArray a) { return 0; }
        if (a.Arg3.Content is not Zig.IntLit i || DecodeZigInt(Tok(i.Arg0)).Value is not { } v)
        {
            throw new IrUnsupportedException(
                "a `[N:s]T` sentinel array requires a compile-time integer literal sentinel");
        }
        return v;
    }

    /// <summary>True when a declaration's type annotation is a <c>[N:s]T</c> sentinel array
    /// (Milestone O, part 4). Its storage reserves N+1 elements (the trailing slot is the sentinel),
    /// so a LOCAL decl lays down one extra slot beyond the <c>CType.Array(element, N)</c> logical
    /// length (see <see cref="DeclOf"/>); the symbol's type stays the N-element array.</summary>
    private static bool IsSentinelArrayType(Item? typeItem) => typeItem?.Content is Zig.TySentArray;

    /// <summary><c>comptime_int</c> <c>const</c> locals whose initializer did not fold where they were declared (a call), by
    /// symbol, with the lowered initializer: a comptime position that names one (an array extent) runs it then.</summary>
    private readonly Dictionary<Symbol, CExpr> _unfoldedConstInits = new();

    /// <summary>Const-evaluate a <c>[N]T</c> array size. A bare integer literal <c>N</c> takes a
    /// fast path through <see cref="DecodeZigInt"/> (so a radix / underscored size <c>[0x10]u8</c>
    /// is accepted with no symbol context); any other form is lowered and folded by the shared
    /// <see cref="IrModule.ConstEval"/> comptime interpreter (Milestone T) — so a computed size
    /// <c>[N * 2]</c> or a container-const size <c>[SIZE]</c> now works, and a call runs at compile time
    /// (<c>[lenFor(u8)]u8</c>, the comptime engine's E2). Throws on a non-constant size.</summary>
    private int ConstEvalArraySize(Item sizeExpr)
    {
        if (sizeExpr.Content is Zig.IntLit i)
        {
            return (int)(DecodeZigInt(Tok(i.Arg0)).Value
                ?? throw new IrUnsupportedException("a `[N]T` array size literal is too large"));
        }
        // An extent is a comptime position, so a CALL in it runs at compile time (`[lenFor(u8)]u8`): the
        // interpreter lowers the callee's body now if it is still pending (the comptime engine's E2).
        CExpr size;
        _comptimeDepth++;   // an extent is evaluated at compile time (task #92)
        try { size = LowerExpr(sizeExpr); }
        finally { _comptimeDepth--; }
        // A comptime_int local bound to a call (`var stack: [stack_size]Range` in std.sort.pdq) folds its initializer.
        size = InlineUnfoldedConsts(size);
        return (_ir.ConstEval(size) ?? (_ir.ResolveComptimeFold(size) is { } folded ? _ir.ConstEval(folded) : null)) is { } n
            ? (int)n
            : throw new IrUnsupportedException("a `[N]T` array size must be a constant integer expression"
                + (_ir.ComptimeMiss is { } why ? $" (the interpreter stopped at {why})" : ""));
    }

    /// <summary>Decode a Zig integer literal — decimal, <c>0x</c>/<c>0o</c>/<c>0b</c> radix, with
    /// <c>_</c> digit separators (UNLIKE C's bare-<c>0</c> octal and <c>'</c> separator) — to a
    /// <see cref="LitInt"/>. The numeric core is normalized to decimal (the backend re-spells it +
    /// adds a type suffix); the signed-long <c>Value</c> is set when it fits (drives const-folding,
    /// left null past <c>long.MaxValue</c>); the carrier type is the narrowest of int/uint/long/ulong
    /// that holds the magnitude. (A Zig <c>comptime_int</c> has no fixed type — at a typed sink
    /// <see cref="LowerExprSink"/> casts it; the literal just needs a representable carrier.)</summary>
    private static LitInt DecodeZigInt(string raw)
    {
        var t = raw.Replace("_", "");
        var inv = CultureInfo.InvariantCulture;
        ulong mag = 0; bool magOk; long? val = null;
        string body; int radix;
        if (t.Length >= 2 && t[0] == '0' && t[1] is 'x' or 'X')
        {
            body = t[2..]; radix = 16;
            magOk = ulong.TryParse(body, NumberStyles.HexNumber, inv, out mag);
            if (long.TryParse(body, NumberStyles.HexNumber, inv, out var hv)) { val = hv; }
        }
        else if (t.Length >= 2 && t[0] == '0' && t[1] is 'o' or 'O')
        {
            body = t[2..]; radix = 8;
            try { mag = System.Convert.ToUInt64(body, 8); magOk = true; } catch { magOk = false; }
            if (magOk && mag <= long.MaxValue) { val = (long)mag; }
        }
        else if (t.Length >= 2 && t[0] == '0' && t[1] is 'b' or 'B')
        {
            body = t[2..]; radix = 2;
            try { mag = System.Convert.ToUInt64(body, 2); magOk = true; } catch { magOk = false; }
            if (magOk && mag <= long.MaxValue) { val = (long)mag; }
        }
        else
        {
            body = t; radix = 10;
            magOk = ulong.TryParse(t, NumberStyles.None, inv, out mag);
            if (long.TryParse(t, inv, out var dv)) { val = dv; }
        }
        if (magOk)
        {
            // The literal's decimal CORE — radix/underscores are gone; the backend re-adds a suffix
            // from the type below. (A non-decimal radix would also be valid C#, but decimal is uniform.)
            var core = mag.ToString(inv);
            var type = mag <= int.MaxValue ? CType.Int
                : mag <= uint.MaxValue ? CType.UInt
                : mag <= long.MaxValue ? CType.Long
                : CType.ULong;
            return new LitInt(core, val) { Type = type };
        }
        // Magnitude exceeds ulong: carry it as a 128-bit literal when it fits u128 (Value stays null —
        // it can't fit a long; a typed i128/u128 sink casts the carrier). Beyond u128 a literal is out
        // of scope, so keep the legacy ulong-ish carrier (any downstream use rejects it).
        if (TryParseRadix128(body, radix, out var mag128))
        {
            return new LitInt(mag128.ToString(inv), null) { Type = CType.UInt128 };
        }
        return new LitInt(t, null) { Type = CType.Long };
    }

    /// <summary>Parse a (radix-stripped) integer body into a <see cref="System.UInt128"/>, with an
    /// exact overflow guard — the >64-bit-literal path for <c>i128</c>/<c>u128</c>. Radix-uniform
    /// (10/16/8/2), since the BCL only parses <c>UInt128</c> in decimal/hex; returns false on a bad
    /// digit, an empty body, or a magnitude past <see cref="System.UInt128.MaxValue"/>.</summary>
    private static bool TryParseRadix128(string body, int radix, out System.UInt128 result)
    {
        result = 0;
        if (body.Length == 0) { return false; }
        var r = (System.UInt128)radix;
        var max = System.UInt128.MaxValue;
        System.UInt128 acc = 0;
        foreach (var ch in body)
        {
            int d = ch is >= '0' and <= '9' ? ch - '0'
                  : ch is >= 'a' and <= 'f' ? ch - 'a' + 10
                  : ch is >= 'A' and <= 'F' ? ch - 'A' + 10
                  : -1;
            if (d < 0 || d >= radix) { return false; }
            if (acc > (max - (System.UInt128)d) / r) { return false; }  // would overflow u128
            acc = acc * r + (System.UInt128)d;
        }
        result = acc;
        return true;
    }

    /// <summary>Lower a Zig float literal: strip <c>_</c> separators, and convert a hex float
    /// (<c>0x1.8p3</c>, no C# syntax) to a round-trippable decimal via the shared
    /// <see cref="EmitHelpers.LowerHexFloat"/>. A decimal float passes through (C# accepts it
    /// verbatim, typed <c>double</c> here). Zig has no <c>f</c>/<c>l</c> literal suffix.</summary>
    private static string LowerZigFloat(string raw)
    {
        var t = raw.Replace("_", "");
        return t.Length > 2 && t[0] == '0' && t[1] is 'x' or 'X' ? EmitHelpers.LowerHexFloat(t) : t;
    }

    /// <summary>Expand Zig's <c>\u{NNNN}</c> unicode escapes in a quoted string lexeme to the
    /// equivalent <c>\xNN</c> UTF-8 byte escapes, so the SHARED string decoder (which has no
    /// <c>\u{…}</c> arm) handles them unchanged. Every OTHER escape (incl. a literal <c>\\</c>)
    /// is copied verbatim, so a <c>\\u{</c> (escaped backslash then a <c>u{</c>) is not mistaken
    /// for a unicode escape. The input/output keep the surrounding quotes.</summary>
    private static string ExpandZigUnicodeEscapes(string quoted)
    {
        if (!quoted.Contains("\\u{", System.StringComparison.Ordinal)) { return quoted; }
        var sb = new System.Text.StringBuilder(quoted.Length);
        var i = 0;
        while (i < quoted.Length)
        {
            if (quoted[i] == '\\' && i + 2 < quoted.Length && quoted[i + 1] == 'u' && quoted[i + 2] == '{')
            {
                var close = quoted.IndexOf('}', i + 3);
                if (close < 0) { throw new IrUnsupportedException("unterminated `\\u{…}` escape in string literal"); }
                var cp = System.Convert.ToInt32(quoted[(i + 3)..close].Replace("_", ""), 16);
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)))
                {
                    sb.Append("\\x").Append(b.ToString("X2"));
                }
                i = close + 1;
            }
            else if (quoted[i] == '\\' && i + 1 < quoted.Length)
            {
                sb.Append(quoted[i]).Append(quoted[i + 1]);   // keep any other escape (incl. `\\`) intact
                i += 2;
            }
            else { sb.Append(quoted[i]); i++; }
        }
        return sb.ToString();
    }

    /// <summary>Fold a Zig multiline string token (a run of <c>\\</c>-prefixed lines) into a single
    /// QUOTED lexeme whose decoded content is the raw concatenation joined by <c>\n</c>. Zig
    /// multiline strings process NO escapes, so each line's content after its <c>\\</c> prefix is
    /// taken verbatim — then re-escaped for the shared C-string decoder (only <c>"</c>/<c>\\</c>/
    /// control chars need escaping; printable + UTF-8 source chars pass through and the decoder
    /// UTF-8-encodes them).</summary>
    private static string FoldZigMultilineString(string token)
    {
        var lines = token.Replace("\r", "").Split('\n');
        var parts = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = raw.TrimStart(' ', '\t');
            if (line.StartsWith("\\\\", System.StringComparison.Ordinal)) { parts.Add(line[2..]); }
        }
        var content = string.Join("\n", parts);
        var sb = new System.Text.StringBuilder(content.Length + 2);
        sb.Append('"');
        foreach (var c in content)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) { sb.Append("\\x").Append(((int)c).ToString("X2")); }
                    else { sb.Append(c); }   // printable ASCII + non-ASCII (UTF-8-encoded by the decoder)
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Map a Zig primitive type name to its faithful C# lowering. The
    /// fixed-width integers carry real signedness (i8 → <c>sbyte</c>, u8 → <c>byte</c>,
    /// …), unlike the earlier slice that collapsed both 8-bit forms to <c>byte</c>.
    /// <c>usize</c>/<c>isize</c> map to the LP64 64-bit <c>size_t</c>/<c>long</c>
    /// (width-correct on dotcc's target; a dedicated pointer-width type is a later
    /// refinement). <c>comptime_int</c> is the interpreter's 128 bits; <c>comptime_float</c> is
    /// deferred.</summary>
    private static CType LowerPrim(string name)
        => TryLowerPrim(name, out var t)
            ? t
            : throw new IrUnsupportedException($"zig type '{name}' not supported yet (slice)");

    /// <summary>Resolve a Zig primitive type name to its <see cref="CType"/> WITHOUT throwing on a
    /// miss — the non-throwing sibling of <see cref="LowerPrim"/>, so <see cref="IsTypeName"/> (and
    /// the type-alias recognizer) can probe "is this a primitive type?" as a boolean rather than
    /// catching an exception for control flow.</summary>
    private static bool TryLowerPrim(string name, out CType type)
    {
        CType? t = name switch
        {
            "void" => CType.Void,
            // `anyopaque` (Milestone W, part 1a) — Zig's opaque type, used only behind a pointer
            // (`*anyopaque` / `?*anyopaque`) as a type-erased context. Maps to C's `void`, so `*anyopaque`
            // → `void*` and `?*anyopaque` → a nullable `void*` (the pointer niche), exactly like C.
            "anyopaque" => CType.Void,
            "bool" => CType.Bool,
            "i8"  => CType.SChar,    // → C# sbyte
            "u8"  => CType.UChar,    // → C# byte
            "i16" => CType.Short,
            "u16" => CType.UShort,
            "i32" => CType.Int,
            "u32" => CType.UInt,
            "i64" => CType.Long,
            "u64" => CType.ULong,
            "i128" => CType.Int128,  // → C# System.Int128
            "u128" => CType.UInt128, // → C# System.UInt128
            // `comptime_int` (the comptime engine): only a comptime evaluation ever holds one (a `var n:
            // comptime_int` in a function a comptime call runs), so it is the interpreter's own 128 bits.
            "comptime_int" => CType.ComptimeInt,
            "isize" => CType.Long,   // LP64: pointer-width signed
            "usize" => CType.ULong,  // LP64: pointer-width unsigned (== size_t)
            "f32" => CType.Float,
            "f64" => CType.Double,
            // C-ABI types for `extern fn` libc FFI (LP64, matching dotcc's __LP64__ trio:
            // `c_long`/`c_ulong` are 8 bytes). These map onto the same well-known prims the
            // C frontend uses, so RenderType + the coercion tables already cover them.
            "c_char" => CType.Char,
            "c_short" => CType.Short,
            "c_ushort" => CType.UShort,
            "c_int" => CType.Int,
            "c_uint" => CType.UInt,
            "c_long" => CType.Long,
            "c_ulong" => CType.ULong,
            "c_longlong" => CType.LongLong,
            "c_ulonglong" => CType.ULongLong,
            _ => null,
        };
        if (t is { } resolved) { type = resolved; return true; }
        // An ARBITRARY-WIDTH integer `uN` / `iN` (road-to-zig-std B3 — `u21` alone has 58 uses and
        // `std.unicode` is unusable without them). dotcc has no sub-word integer type, so a `uN`/`iN`
        // lowers to the smallest STANDARD width that holds N bits (a `u4` → `byte`, `u12` → `ushort`,
        // `i7` → `sbyte`). @sizeOf matches zig (both round up to whole bytes); the extra representable
        // range means overflow does NOT wrap at N bits — the SAME documented leniency dotcc already
        // takes for plain `+` (no overflow trap). Sub-byte bit-PACKING inside a `packed struct` is a
        // separate concern (dotcc byte-packs). N &gt; 128 needs BigInteger — a loud cut for now.
        if (TryArbitraryWidthInt(name, out var awInt)) { type = awInt; return true; }
        type = CType.Void;
        return false;
    }

    /// <summary>Recognize a Zig arbitrary-width integer keyword <c>u&lt;N&gt;</c> / <c>i&lt;N&gt;</c>
    /// (any 1..128 bit width, not just the standard powers of two the switch lists) and map it to the
    /// smallest standard <see cref="CType"/> that holds N bits. Returns false for a non-<c>uN</c>/<c>iN</c>
    /// name, a non-numeric tail, a zero/overlarge width (&gt; 128), or a leading zero (<c>u08</c> is not a
    /// zig type) — so <see cref="TryLowerPrim"/> falls through to its miss.</summary>
    private static bool TryArbitraryWidthInt(string name, out CType type)
    {
        type = CType.Void;
        if (name.Length < 2 || (name[0] != 'u' && name[0] != 'i')) { return false; }
        var digits = name.AsSpan(1);
        if (digits.Length > 1 && digits[0] == '0') { return false; }   // no leading zero (u08, i007)
        foreach (var ch in digits) { if (ch is < '0' or > '9') { return false; } }
        if (!int.TryParse(digits, out var bits) || bits < 1 || bits > 128) { return false; }
        var signed = name[0] == 'i';
        type = bits switch
        {
            <= 8  => signed ? CType.SChar : CType.UChar,
            <= 16 => signed ? CType.Short : CType.UShort,
            <= 32 => signed ? CType.Int   : CType.UInt,
            <= 64 => signed ? CType.Long  : CType.ULong,
            _     => signed ? CType.Int128 : CType.UInt128,   // 65..128
        };
        return true;
    }

}
