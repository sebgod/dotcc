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
        if (rhs.Content is Zig.BuiltinCall b && Tok(b.Arg0) == "@import")
        {
            var bargs = Flatten(b.Arg2);
            if (bargs.Count == 1 && bargs[0].Content is Zig.StrLit sl)
            {
                var module = UnquoteStringLiteral(Tok(sl.Arg0));
                if (module != "std")
                {
                    throw new IrUnsupportedException(
                        $"zig `@import(\"{module}\")` is not modeled — only `@import(\"std\")` (and only its allocator paths: std.mem.Allocator, std.heap.page_allocator/c_allocator/FixedBufferAllocator)");
                }
                _imports[name] = module;
                return true;
            }
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
        if (TryTypeAliasRhs(rhs, out var aliasType))
        {
            _typeAliases[name] = aliasType;
            return true;
        }
        return false;
    }

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
    private bool TryTypeAliasRhs(Item rhs, out CType type)
    {
        switch (rhs.Content)
        {
            // Type-former prefixes/suffixes — unambiguously a type (no value spelling collides).
            case Zig.TyPointer or Zig.TyPtrConst or Zig.TyCPtr or Zig.TyCPtrConst
              or Zig.TyManyPtr or Zig.TyManyPtrConst or Zig.TySentPtr or Zig.TySentPtrConst
              or Zig.TyOptional or Zig.TySlice or Zig.TySliceConst or Zig.TySentSlice or Zig.TySentSliceConst
              or Zig.TyArray or Zig.TySentArray or Zig.ErrUnion or Zig.TyTuple
              or Zig.TyFn or Zig.TyFnNoArgs or Zig.TyFnErr or Zig.TyFnNoArgsErr:
                type = LowerType(rhs);
                return true;

            // `@TypeOf(expr)` — the operand's synthesized type, unevaluated.
            case Zig.BuiltinCall b when Tok(b.Arg0) == "@TypeOf":
                type = TypeOfBuiltin(b.Arg2);
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

    /// <summary>True when <paramref name="name"/> names a TYPE in the current lowering context — a
    /// registered container, an existing type alias, a container-scoped self-alias, an error set, or
    /// a Zig primitive (<see cref="TryLowerPrim"/>). The discriminator that keeps a bare-identifier
    /// <c>const</c> RHS (<c>const y = x;</c>) from being misread as a type alias.</summary>
    private bool IsTypeName(string name)
        => _typeAliases.ContainsKey(name)
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
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `@TypeOf` takes exactly one operand; got {args.Count}");
        }
        // `@TypeOf(anytypeParam)` while lowering an `anytype` generic's per-instance signature (wall-plan
        // W5): return the param's inferred concrete type directly — it is seeded at the call site but is
        // not yet an in-scope symbol, so the LowerExpr path below would fail to resolve it.
        if (args[0].Content is Zig.Ident aid && _anytypeSeeds.TryGetValue(Tok(aid.Arg0), out var seeded))
        {
            return seeded;
        }
        var savedBuf = _hoist;
        var savedImpure = _hoistImpureSeen;
        _hoist = new List<CStmt>();   // throwaway — @TypeOf's operand is unevaluated
        try
        {
            return LowerExpr(args[0]).Type
                ?? throw new IrUnsupportedException("zig `@TypeOf`: the operand has no statically known type");
        }
        finally
        {
            _hoist = savedBuf;
            _hoistImpureSeen = savedImpure;
        }
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
    private bool TryResolveStdPath(Item expr, out string path)
    {
        path = "";
        var segments = new List<string>();
        var cur = expr;
        while (cur.Content is Zig.Field f)
        {
            segments.Add(Tok(f.Arg2));
            cur = f.Arg0;
        }
        if (cur.Content is not Zig.Ident id || !_imports.TryGetValue(Tok(id.Arg0), out var module))
        {
            return false;
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
        // A dotted std type (Milestone F): `std.mem.Allocator` → the runtime Allocator fat
        // pointer; `std.heap.FixedBufferAllocator` → the concrete bump allocator. Any other std
        // path in type position errors clearly (`std` is a known-paths resolver, not a real model).
        Zig.Field => LowerStdType(type),
        // Pointer types. `*T` and the C-pointer `[*c]T` both lower to a plain
        // `T*` (the C-pointer's null/arithmetic semantics ARE C's pointer). The
        // pointee `const` rides as a TypeQual so const-correctness sees it; it
        // doesn't change the C# spelling (`[*c]const u8` and `[*c]u8` are both
        // `byte*`). `[*c]const u8` is exactly the type of printf's format param.
        Zig.TyPointer p    => PointerTo(LowerType(p.Arg1)),
        Zig.TyPtrConst p   => PointerTo(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TyCPtr p       => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyCPtrConst p  => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        // `[*]T` / `[*]const T` many-item pointers (Milestone O, part 2) — like `[*c]`,
        // a bare `T*`. They index/slice; `.len` is unavailable (a pointer has no length).
        Zig.TyManyPtr p     => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyManyPtrConst p => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
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
        Zig.TySlice s      => new CType.Slice(LowerType(s.Arg2)),
        Zig.TySliceConst s => new CType.Slice(LowerType(s.Arg3).WithQuals(TypeQual.Const)),
        // Sentinel-terminated types (Milestone O, part 3 — the C-string shape; V1 sentinel = 0).
        // `[*:0]T` is a NUL-terminated many-item pointer (C's `char*`) → a bare `T*`, like `[*]`;
        // `[:0]T` is a NUL-terminated slice → CType.Slice, like `[]T`. The sentinel is a type-level
        // annotation, not separately enforced (string literals are already NUL-terminated, so a
        // manual `while (p[n] != 0)` scan works); the auto-scan `p[0..]` on a sentinel pointer is
        // a documented cut. Const rides as a TypeQual on the element, same as the non-sentinel forms.
        Zig.TySentPtr p      => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TySentPtrConst p => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TySentSlice s      => new CType.Slice(LowerType(s.Arg1)),
        Zig.TySentSliceConst s => new CType.Slice(LowerType(s.Arg2).WithQuals(TypeQual.Const)),
        // `[N]T` fixed-size array → CType.Array(element, N). N must be an integer literal
        // (a general comptime const-expr size is deferred). A `var b: [N]T` local lowers to a
        // stackalloc'd C array (see DeclOf), so slicing it (`b[lo..hi]`) yields a stack-backed slice.
        Zig.TyArray a => new CType.Array(LowerType(a.Arg3), ConstEvalArraySize(a.Arg1)),
        // `[N:s]T` sentinel-terminated array (Milestone O, part 4; non-zero sentinel in Milestone Z)
        // → CType.Array(element, N) — the LOGICAL length N (so `.len` / slicing exclude the sentinel,
        // like Zig). The extra trailing sentinel slot (N+1 total storage) is materialized only at the
        // local decl site (see DeclOf / IsSentinelArrayType / SentinelArrayValue); the type itself
        // stays an ordinary N-element array, so a `[N:0]u8` buffer is a valid NUL-terminated C string
        // without writing the terminator. A zero sentinel rides C#'s zero-fill; a NON-ZERO sentinel is
        // written into the trailing slot explicitly (the sentinel VALUE isn't carried in the type).
        Zig.TySentArray a => new CType.Array(LowerType(a.Arg5), ConstEvalArraySize(a.Arg1)),
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
        Zig.InlineStructTypeEmpty      => ReifyInlineStruct(type, null),
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
        var name = $"__AnonStruct{_inlineStructNames.Count}";
        // Record the name BEFORE lowering the fields, so a self-referential field (`next: ?*Self`
        // resolved via @This()) or a re-entrant lowering of the same site sees the in-progress type.
        _inlineStructNames[occurrence] = name;
        var (fields, methods, consts) = fieldDecls is { } fd
            ? SplitMembers(fd)
            : (new List<Item>(), new List<Item>(), new List<Item>());
        if (methods.Count > 0 || consts.Count > 0)
        {
            throw new IrUnsupportedException(
                "zig: an inline `struct {…}` type is fields-only (road-to-zig-std S9) — a method or `const` "
                + "member needs a named container decl (`const T = struct { … };`)");
        }
        RegisterStruct(name, fields);
        return new CType.Named(name);
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

    /// <summary>Lower a dotted std type (Milestone F): a <see cref="StdTypes"/> row — e.g.
    /// <c>std.mem.Allocator</c> → the runtime <see cref="CType.Allocator"/> fat pointer,
    /// <c>std.heap.FixedBufferAllocator</c> → the concrete <see cref="CType.Named"/> bump
    /// allocator. Any other dotted type errors — either a chain not rooted at a std import
    /// (so not a known type at all) or an unmodeled std path.</summary>
    private CType LowerStdType(Item f)
    {
        if (TryResolveStdPath(f, out var path))
        {
            if (StdTypes.TryGetValue(path, out var make)) { return make(); }
            throw new IrUnsupportedException(
                $"zig type `{path}` is not modeled (std types: {string.Join(", ", StdTypes.Keys)})");
        }
        throw new IrUnsupportedException(
            $"zig type: a dotted type `{Tok(((Zig.Field)f.Content!).Arg2)}` that is not a modeled std path");
    }

    /// <summary>Lower a tuple TYPE body (the <c>T1, T2, …</c> inside <c>struct { … }</c> at a Type
    /// position) to a <see cref="CType.Tuple"/>. V1 supports arity 1..7 (an empty tuple and
    /// arity &gt; 7 — which would need ValueTuple's <c>TRest</c> nesting — are deferred with a clear
    /// error). Each element is itself a <see cref="LowerType"/>, so nested tuple types compose.</summary>
    private CType LowerTupleType(Item tupleTypes)
    {
        var elems = Flatten(tupleTypes).Select(LowerType).ToList();
        return new CType.Tuple(elems);
    }

    /// <summary>The type the enclosing container's <c>@This()</c> resolves to — the struct/enum
    /// whose method is currently being lowered. An error outside a method (no container in
    /// scope).</summary>
    private CType CurrentContainerType() =>
        _currentContainer is { } c && _containerTypes.TryGetValue(c, out var t)
            ? t
            : throw new IrUnsupportedException("zig `@This()` is only supported inside a container method");

    /// <summary>Resolve a Zig type spelled as a bare identifier: a container-scoped self alias
    /// (<c>const Self = @This();</c>) wins first, then a registered container (struct →
    /// <see cref="CType.Named"/>, enum → <see cref="CType.Enum"/>), then the primitive table — so a
    /// user type name (or a self alias inside its own method) resolves before <see cref="LowerPrim"/>
    /// would throw on it.</summary>
    private CType LowerTypeName(string name)
    {
        if (ResolveSelfAlias(name) is { } alias) { return alias; }
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
        // An error-set name used as a plain VALUE type — `fn f(e: E)`, `var x: E`, a non-`!T`
        // error return `fn g() E` — or the open `anyerror`. Lowers to the flat erased error code
        // (`CType.ErrorSet`, rendered `ushort`): the error VALUE itself, NOT an `E!T` error union
        // (handled separately as `Zig.ErrUnion`). Set membership stays erased at runtime; the
        // declared-set table only drives the compile-time rejection in part 3a. (Milestone X, part 3b.)
        if (name == "anyerror" || _errorSets.Contains(name)) { return CType.ErrorSet; }
        return LowerPrim(name);
    }

    /// <summary>Resolve a type name that is a container-scoped self alias (<c>const Self =
    /// @This();</c>), valid only while a method of the declaring container is being lowered
    /// (<see cref="_currentContainer"/> set). Returns <c>null</c> when it is not such an alias.</summary>
    private CType? ResolveSelfAlias(string name) =>
        _currentContainer is { } c
        && _selfAliases.TryGetValue(c, out var m)
        && m.TryGetValue(name, out var t)
            ? t : null;

    /// <summary>Look up the container type named at a use site — a registered struct/enum/union
    /// name, or a container-scoped self alias (<c>Self</c>) when inside that container's method.
    /// Drives <c>Type.func()</c> / <c>EnumName.member</c> resolution (a self alias maps through to
    /// the real container type, so <c>Self.init(…)</c> binds to the same mangled method as the
    /// explicit name).</summary>
    private bool TryLookupContainerType(string name, out CType type)
    {
        var alias = ResolveSelfAlias(name);
        if (alias is not null) { type = alias; return true; }
        return _containerTypes.TryGetValue(name, out type!);
    }

    /// <summary>Lower a Zig optional payload type: a pointer (or function-pointer) payload stays a
    /// bare nullable pointer (the niche — a `delegate*` / `T*` is null when none); any other payload
    /// is wrapped in <see cref="CType.Optional"/> (→ C# <c>T?</c>).</summary>
    private CType LowerOptional(Item innerType)
    {
        var inner = LowerType(innerType);
        return inner.Unqualified is CType.Pointer or CType.Func ? inner : new CType.Optional(inner);
    }

    /// <summary>Form a pointer to <paramref name="pointee"/>, collapsing a pointer-to-FUNCTION to
    /// the bare <see cref="CType.Func"/> (Milestone W, part 1a). In dotcc's IR a function pointer
    /// is a bare <c>Func</c> rendered as a <c>delegate*&lt;…&gt;</c> (the C-frontend convention), so
    /// <c>*const fn (…) R</c> / <c>*fn (…) R</c> lower to the same <c>Func</c> as a bare <c>fn</c>
    /// type — keeping every downstream call / coercion / sizeof path identical to C's.</summary>
    private static CType PointerTo(CType pointee) =>
        pointee.Unqualified is CType.Func ? pointee : new CType.Pointer(pointee);

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

    /// <summary>Const-evaluate a <c>[N]T</c> array size. A bare integer literal <c>N</c> takes a
    /// fast path through <see cref="DecodeZigInt"/> (so a radix / underscored size <c>[0x10]u8</c>
    /// is accepted with no symbol context); any other form is lowered and folded by the shared
    /// <see cref="IrBuilder.ConstEval"/> comptime interpreter (Milestone T) — so a computed size
    /// <c>[N * 2]</c> or a container-const size <c>[SIZE]</c> now works. Throws on a non-constant size.</summary>
    private int ConstEvalArraySize(Item sizeExpr)
    {
        if (sizeExpr.Content is Zig.IntLit i)
        {
            return (int)(DecodeZigInt(Tok(i.Arg0)).Value
                ?? throw new IrUnsupportedException("a `[N]T` array size literal is too large"));
        }
        return _ir.ConstEval(LowerExpr(sizeExpr)) is { } n
            ? (int)n
            : throw new IrUnsupportedException("a `[N]T` array size must be a constant integer expression");
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
    /// refinement). <c>comptime_int</c>/<c>comptime_float</c> and the bigger/arbitrary
    /// <c>iN</c>/<c>uN</c> widths are deferred.</summary>
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
