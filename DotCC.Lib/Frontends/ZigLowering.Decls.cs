#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Declarations: the top-level pass bodies (globals, fn/extern-fn declaration)
/// and container registration (structs / enums / unions, their consts, vars and methods).
/// One concern of the <see cref="ZigLowering"/> binder; class doc + shared state live in
/// the main file.</summary>
internal sealed partial class ZigLowering
{
    // ---- top level -------------------------------------------------------

    /// <summary>Pass 1: declare a function's signature (return + parameter types) in
    /// the global scope and bundle its body for pass 2. Declaring all signatures up
    /// front is what lets a call forward-reference a function defined later.</summary>
    private (Symbol sym, List<(string name, CType type)> ps, Item body) DeclareFn(
        Item nameTok, Item? paramsItem, Item retType, Item body, bool errUnion = false, string? mangledName = null)
    {
        var declared = DeclareFnCore(nameTok, paramsItem, retType, body, errUnion, mangledName);
        // The raw parameter ASTs, so the body can give each parameter the width its type spelled, and the
        // return width, so a call's result carries it.
        _fnParamInfos[declared.sym] = CollectParamInfos(paramsItem, out _);
        if (!_genericFns.ContainsKey(declared.sym) && DeclaredBitsOfTypeArg(retType) is { } retBits)
        {
            _fnReturnBits[declared.sym] = retBits;
        }
        return declared;
    }

    private (Symbol sym, List<(string name, CType type)> ps, Item body) DeclareFnCore(
        Item nameTok, Item? paramsItem, Item retType, Item body, bool errUnion, string? mangledName)
    {
        // Classify the parameters (raw type ASTs — lowered lazily, since a type-param generic's runtime
        // parameter/return types depend on `T`). Detect the variadic marker too.
        var allParams = CollectParamInfos(paramsItem, out var variadic);
        // Zig allows `...` ONLY in an extern prototype — a non-extern variadic fn is
        // a compile error. Reject it the same way (faithful to Zig; our subset has no
        // way to access varargs from a Zig body anyway).
        if (variadic)
        {
            throw new IrUnsupportedException(
                $"function '{Tok(nameTok)}': a non-extern Zig function cannot be variadic (use `extern fn`)");
        }

        // A `type`-RETURNING function (wall-plan W4): `fn Pair(comptime T: type) type { return struct
        // {…}; }`. It is a COMPTIME type constructor, not a runtime function — it emits no code; each
        // use in a type position REIFIES a fresh struct per resolved type argument. Retain the template
        // (params + body) for EvalTypeReturningCall; the placeholder symbol is never called directly.
        // A MEMBER of a container (hash_map's `fn FieldIterator(comptime T: type) type`, array_list's
        // `SentinelSlice`) is the same template under the mangled name, remembering its OWNER: its body is
        // evaluated in the owner's scope, with the owner's comptime seeds live.
        if (IsTypeKeyword(retType))
        {
            if (allParams.Any(p => p.Kind is ParamKind.Runtime or ParamKind.AnyType))
            {
                throw new IrUnsupportedException(
                    $"function '{Tok(nameTok)}': a `type`-returning function's parameters must be `comptime` "
                    + "(a `comptime T: type` or a `comptime x: V` value/optional param; road-to-zig-std S4b) — "
                    + "a runtime or `anytype` parameter is not supported");
            }
            var tRet = DeclareFnSymbol(new Symbol
            {
                Name = mangledName ?? Tok(nameTok),
                Kind = SymKind.Func,
                Type = new CType.Func(CType.Void, new List<CType>(), false),
                IsGlobal = true,
            });
            _typeReturningGenerics[tRet] = new TypeReturningGenericInfo(tRet, allParams, body,
                mangledName is not null ? _currentContainer : null);
            return (tRet, new List<(string name, CType type)>(), body);
        }

        var hasComptime = allParams.Any(p => p.IsComptime);
        var hasTypeParam = allParams.Any(p => p.Kind == ParamKind.ComptimeType);
        var hasAnyType = allParams.Any(p => p.Kind == ParamKind.AnyType);
        // A generic (any comptime OR `anytype` param) is a TEMPLATE. A generic METHOD (a `mangledName`:
        // hash_map's `fetchRemoveAdapted(self, key: anytype, ctx: anytype)`) is the same template under its
        // mangled name, remembering its OWNER, whose scope and comptime seeds its instances are lowered in;
        // the shared import scope records which module holds it, so a call from any module instantiates it.
        var owner = mangledName is not null ? _currentContainer : null;

        // A generic METHOD always defers its signature: a comptime VALUE parameter may spell one of its
        // types (array_list's `toOwnedSliceSentinel(…, comptime sentinel: T) !SentinelSlice(sentinel)`).
        // So does a VALUE-only generic whose signature spells a comptime parameter (std.mem's
        // `inline fn reverseVector(comptime N: usize, comptime T: type, …) [N]T`, or `fn f(comptime N: usize) [N]u8`).
        if (hasTypeParam || hasAnyType || (hasComptime && (owner is not null || !SignatureLowersNow(retType, allParams))))
        {
            // A `comptime T: type` TYPE parameter (wall-plan W3b) OR an `a: anytype` inferred-type
            // parameter (wall-plan W5) makes later parameter / return types depend on a type not known at
            // template time — an explicit type argument (W3b) or the actual argument's inferred
            // `@TypeOf` (W5) — so the signature CANNOT be lowered here; it is lowered per instantiation
            // once the type(s) are bound (InstantiateGeneric).
            // The template symbol carries a placeholder signature — it is never called directly; every
            // call routes through InstantiateGeneric to a mangled, concretely-typed instance.
            var tmpl = DeclareFnSymbol(new Symbol
            {
                Name = mangledName ?? Tok(nameTok),
                Kind = SymKind.Func,
                Type = new CType.Func(CType.Void, new List<CType>(), false),
                IsGlobal = true,
            });
            _genericFns[tmpl] = new GenericFnInfo(tmpl, allParams, retType, errUnion, body, owner);
            if (owner is not null) { _shared.GenericMethodOwners[tmpl] = this; }
            return (tmpl, new List<(string name, CType type)>(), body);
        }

        // No type param: the signature is concrete at template time (runtime + comptime-VALUE parameter
        // types don't depend on a type param). Lower it now — a `!T` return (Zig's inferred error set)
        // wraps the payload in an error union (V1 erases the set, so the leading `!` just marks it).
        var ret = LowerType(retType);
        if (errUnion) { ret = new CType.ErrorUnion(ret); }
        // A `comptime`-value parameter (wall-plan W3a) has NO runtime storage — it is a
        // monomorphization key, not a signature slot. The RUNTIME parameters (the rest) are what the
        // function symbol's signature and body carry; the comptime ones are baked per instantiation.
        var runtimeParams = allParams.Where(p => p.Kind == ParamKind.Runtime)
            .Select(p => (p.Name, LowerType(p.TypeAst))).ToList();

        var funcSym = DeclareFnSymbol(new Symbol
        {
            // A method is lowered to a free function under its mangled `TypeName_method` name
            // (so it can be `&fn`-addressed and called directly); a plain function keeps its name.
            Name = mangledName ?? Tok(nameTok),
            Kind = SymKind.Func,
            Type = new CType.Func(ret, runtimeParams.Select(p => p.Item2).ToList(), false),
            IsGlobal = true,
        }, qualify: mangledName is null);
        // Stash the raw return-type AST so the body can resolve its declared error set in pass 2
        // (the set decls aren't processed until pass 1.5) for the foreign-error return check.
        if (ret is CType.ErrorUnion) { _fnErrorReturnTypes[funcSym] = (retType, errUnion); }

        // A comptime-VALUE-only generic (wall-plan W3a): retain the template + DON'T lower a base body
        // (the caller skips it via `AddFnEntry`); a call instantiates a specialized body per value.
        if (hasComptime)
        {
            _genericFns[funcSym] = new GenericFnInfo(funcSym, allParams, retType, errUnion, body, owner);
            if (owner is not null) { _shared.GenericMethodOwners[funcSym] = this; }
        }
        return (funcSym, runtimeParams, body);
    }

    /// <summary>Whether a comptime-VALUE generic's return and runtime parameter types lower before any
    /// comptime parameter is bound: false when one spells a comptime parameter (<c>[N]u8</c> for
    /// <c>comptime N: usize</c>), so the signature is lowered per instance instead, with the value seeded.</summary>
    private bool SignatureLowersNow(Item retType, IReadOnlyList<ParamInfo> allParams)
    {
        try
        {
            LowerType(retType);
            foreach (var p in allParams.Where(p => p.Kind == ParamKind.Runtime)) { LowerType(p.TypeAst); }
            return true;
        }
        catch (IrUnsupportedException)
        {
            return false;
        }
    }

    /// <summary>Declare a free function's symbol. In an IMPORTED module its EMITTED name is
    /// module-qualified (<c>util__f</c>), the function analogue of <see cref="QualifyTypeName"/>: every
    /// module's functions land in one emitted class, so an imported <c>util.f</c> and the root's own
    /// <c>f</c> (or two std files' <c>init</c>) would otherwise be two same-signature C# methods, a
    /// CS0111 bad emit. The source <see cref="Symbol.Name"/> is untouched, so lookup inside the module
    /// and from its importers is unchanged. A root unit's names stay as spelled; a method passes
    /// <paramref name="qualify"/> false, since its mangled name already carries the qualified container.</summary>
    private Symbol DeclareFnSymbol(Symbol sym, bool qualify = true)
    {
        _symbols.Declare(sym);
        // Qualify the SOURCE name and escape once: the legalizer escapes a C# keyword (`double` →
        // `@double`), and a prefix glued onto that (`m__@double`) is not an identifier.
        if (qualify && _modulePrefix is { } prefix) { sym.TargetName = _names.Escape($"{prefix}__{sym.Name}"); }
        return sym;
    }

    // Monotonic index for synthesized test-function names (`__zigtest_0`, …) — program-unique.
    private int _testSeq;

    /// <summary>Test mode (<c>dotcc zig test</c>): lower a <c>test "name"/ident/(anon) { … }</c> block
    /// to a runnable <c>anyerror!void</c> free function named <c>__zigtest_N</c>, register it in the IR
    /// test manifest (<see cref="IrModule.Tests"/>) with its display name, and return the pass-1 entry
    /// so its body lowers in pass 2 exactly like a normal function. A test PASSES when its body returns
    /// normally and FAILS when it returns an error — a <c>try</c> that propagates, or an explicit
    /// <c>return error.X</c> — so the synthesized return type is an error union, and (unlike
    /// <see cref="DeclareFn"/>) NO declared error set is recorded, leaving it unconstrained
    /// (<c>anyerror</c>), the permissive behavior a test body needs. The fn is left non-static so the
    /// generated runner (BuildShell test mode) can call it by bare name through
    /// <c>using static DotCcProgram;</c>, like <c>main</c>.</summary>
    private (Symbol sym, List<(string name, CType type)> ps, Item body) DeclareTest(string? displayName, Item body)
    {
        var index = _testSeq++;
        var sym = _symbols.Declare(new Symbol
        {
            Name = "__zigtest_" + index,
            Kind = SymKind.Func,
            Type = new CType.Func(new CType.ErrorUnion(CType.Void), new List<CType>(), false),
            IsGlobal = true,
        });
        _ir.Tests.Add((displayName ?? "test." + index, sym));
        return (sym, new List<(string name, CType type)>(), body);
    }

    /// <summary>True when <paramref name="fn"/> is a TEMPLATE rather than a function with a body of its own:
    /// a generic (instantiated per call) or a type-returning one (evaluated in type positions). A method
    /// declaration that returns one queues no body.</summary>
    private bool IsFnTemplate(Symbol fn) => _genericFns.ContainsKey(fn) || _typeReturningGenerics.ContainsKey(fn);

    /// <summary>True when a container member function returns <c>type</c>: a comptime type constructor
    /// (<c>fn FieldIterator(comptime T: type) type</c>), not a runtime method.</summary>
    private static bool IsTypeReturningFnDef(Item fnDef) => fnDef.Content switch
    {
        Zig.FnDef f => IsTypeKeyword(f.Arg6),
        Zig.FnDefNoArgs f => IsTypeKeyword(f.Arg5),
        _ => false,
    };

    /// <summary>Declare a container's TYPE-returning member functions now, and return the others. A
    /// template's declaration lowers no type, so it can run before the container's type consts and fields
    /// (hash_map's <c>pub const KeyIterator = FieldIterator(K);</c> names one), and it has no runtime body
    /// to queue. <see cref="_currentContainer"/> survives the call (DeclareMethod clears it).</summary>
    private List<Item> DeclareTypeReturningMembers(string container, IReadOnlyList<Item> fnDefs)
    {
        var runtime = new List<Item>();
        foreach (var fnDef in fnDefs)
        {
            if (!IsTypeReturningFnDef(fnDef)) { runtime.Add(fnDef); continue; }
            var prev = _currentContainer;
            try { DeclareMethod(container, fnDef); }
            finally { _currentContainer = prev; }
        }
        return runtime;
    }

    /// <summary>A method declaration's own name, without declaring anything — so a lazily-prepared
    /// module can index its methods by name (see <c>_lazyMethodDecls</c>) and declare one only when it
    /// is actually called. The four shapes match <see cref="DeclareMethod"/>'s switch.</summary>
    private static string MethodNameOf(Item fnDef) => Tok(fnDef.Content switch
    {
        Zig.FnDef f          => f.Arg1,
        Zig.FnDefNoArgs f    => f.Arg1,
        Zig.FnDefErr f       => f.Arg1,
        Zig.FnDefNoArgsErr f => f.Arg1,
        _ => throw new IrUnsupportedException("zig method: " + (fnDef.Content?.GetType().Name ?? "null")),
    });

    /// <summary>Pass 1 for a struct method: declare it as a free function named
    /// <c>TypeName_method</c> (its receiver, if any, is the ordinary first parameter, so the
    /// body lowers exactly like a free function — <c>self.x</c> is plain field access) and record
    /// it in <see cref="_methods"/> for call rewriting. <see cref="_currentContainer"/> is set so
    /// a <c>@This()</c> in a parameter type resolves to the container.</summary>
    private (Symbol sym, List<(string name, CType type)> ps, Item body, string? container) DeclareMethod(
        string container, Item fnDef)
    {
        Item nameTok; Item? paramsItem; Item retType; Item body; bool errUnion;
        switch (fnDef.Content)
        {
            // The optional CallConv (Milestone R, part 5) shifts the return type + body one slot right.
            case Zig.FnDef f:          nameTok = f.Arg1; paramsItem = f.Arg3; retType = f.Arg6; body = f.Arg7; errUnion = false; break;
            case Zig.FnDefNoArgs f:    nameTok = f.Arg1; paramsItem = null;   retType = f.Arg5; body = f.Arg6; errUnion = false; break;
            case Zig.FnDefErr f:       nameTok = f.Arg1; paramsItem = f.Arg3; retType = f.Arg7; body = f.Arg8; errUnion = true;  break;
            case Zig.FnDefNoArgsErr f: nameTok = f.Arg1; paramsItem = null;   retType = f.Arg6; body = f.Arg7; errUnion = true;  break;
            default: throw new IrUnsupportedException("zig method: " + (fnDef.Content?.GetType().Name ?? "null"));
        }
        var methodName = Tok(nameTok);

        // The container is the signature's scope, and the caller's is RESTORED after: a method declared on
        // demand in the middle of another body (a lazy module's `H.init(seed)` inside `H.hash`) must not
        // clear that body's container, or its next bare container const (`secret[1]`) goes unresolved.
        (Symbol sym, List<(string name, CType type)> ps, Item body) e;
        using (EnterContainer(container))
        {
            e = DeclareFn(nameTok, paramsItem, retType, body, errUnion: errUnion, mangledName: container + "_" + methodName);
        }

        if (!_methods.TryGetValue(container, out var methods))
        {
            methods = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
            _methods[container] = methods;
        }
        if (!methods.TryAdd(methodName, e.sym))
        {
            throw new IrUnsupportedException($"struct '{container}' declares '{methodName}' more than once");
        }
        return AsEntry(e, container);
    }

    /// <summary>Collect a parameter list's <see cref="ParamInfo"/>s in source order — each carrying its
    /// <c>(name, raw-type-AST, kind)</c> WITHOUT lowering the type (a type-param generic's runtime /
    /// return types depend on <c>T</c>, so they lower lazily per instantiation) — and detecting the
    /// variadic marker <c>...</c> (Zig's <c>DOT3</c> ParamDecl). The marker carries no name/type, so it
    /// is excluded from the infos and instead sets <paramref name="variadic"/>; it must be the LAST
    /// parameter (C / Zig both require the fixed params to precede the pack). A <c>comptime</c>-qualified
    /// parameter (wall-plan W3a/W3b) is classified <see cref="ParamKind.ComptimeType"/> when its type is
    /// the <c>type</c> keyword, else <see cref="ParamKind.ComptimeValue"/> — <see cref="DeclareFn"/>
    /// splits those out as the monomorphization keys.</summary>
    private List<ParamInfo> CollectParamInfos(Item? paramsItem, out bool variadic)
    {
        variadic = false;
        var infos = new List<ParamInfo>();
        if (paramsItem is null) { return infos; }

        var ps = Flatten(paramsItem);
        for (var i = 0; i < ps.Count; i++)
        {
            switch (ps[i].Content)
            {
                case Zig.ParamVariadic:
                    if (i != ps.Count - 1)
                    {
                        throw new IrUnsupportedException("zig `...` must be the final parameter");
                    }
                    variadic = true;
                    break;
                case Zig.Param pm:
                    // `a: anytype` (wall-plan W5) — an inferred-type parameter (a monomorphization key
                    // AND a runtime slot); a plain `a: T` is an ordinary runtime parameter.
                    infos.Add(new ParamInfo(Tok(pm.Arg0), pm.Arg2,
                        IsAnyTypeKeyword(pm.Arg2) ? ParamKind.AnyType : ParamKind.Runtime));
                    break;
                case Zig.ParamComptime pm:   // 'comptime' IDENT ':' Type
                    infos.Add(new ParamInfo(Tok(pm.Arg1), pm.Arg3,
                        IsTypeKeyword(pm.Arg3) ? ParamKind.ComptimeType : ParamKind.ComptimeValue));
                    break;
                default:
                    throw new IrUnsupportedException("zig param: " + (ps[i].Content?.GetType().Name ?? "null"));
            }
        }
        return infos;
    }

    /// <summary>True when a type-position AST is the <c>type</c> keyword (Zig's type-of-types) spelled
    /// as a bare identifier — the discriminator between a <c>comptime T: type</c> TYPE parameter
    /// (wall-plan W3b) and a <c>comptime N: i32</c> VALUE parameter (wall-plan W3a).</summary>
    private static bool IsTypeKeyword(Item typeItem)
        => typeItem.Content is Zig.Ident id && Tok(id.Arg0) == "type";

    /// <summary>True when a parameter's type-position AST is the <c>anytype</c> keyword (Zig's
    /// inferred-type parameter, wall-plan W5) spelled as a bare identifier — the discriminator that
    /// classifies a <see cref="ParamKind.AnyType"/> parameter. Like <c>type</c> (W1/W3b), <c>anytype</c>
    /// is not a reserved lexer token; it lexes as an ordinary identifier and parses as a bare
    /// <c>Type</c>, so no grammar change is needed.</summary>
    private static bool IsAnyTypeKeyword(Item typeItem)
        => typeItem.Content is Zig.Ident id && Tok(id.Arg0) == "anytype";

    /// <summary>Declare an <c>extern fn</c> prototype: a function symbol with no body
    /// (so no <see cref="FuncDef"/>). <c>FromSystemHeader = true</c> marks it as
    /// externally provided (libc, linked with <c>-lc</c>) — exactly the marker the C
    /// frontend puts on a libc prototype — so <see cref="LowerCall"/> renders the call
    /// by its bare name (no <c>CalleeSym</c>), routing it to dotcc's <c>Libc</c> runtime
    /// the same way a C program's libc call does. A trailing <c>...</c> (the
    /// <c>fn(fixed, ...)</c> form, e.g. printf) sets the function type's
    /// <c>Variadic</c> flag: the fixed params still coerce at the call, while the
    /// trailing args take C's default argument promotions.</summary>
    private void DeclareExternFn(Item nameTok, Item? paramsItem, Item retType)
    {
        var ret = LowerType(retType);
        var paramInfos = CollectParamInfos(paramsItem, out var variadic);
        // An `extern fn` is a C-ABI prototype — a `comptime` or `anytype` parameter (a monomorphization
        // key, not an ABI slot) makes no sense on one, and real zig rejects it too. Reject loudly.
        if (paramInfos.Any(p => p.IsComptime || p.Kind == ParamKind.AnyType))
        {
            throw new IrUnsupportedException(
                $"extern fn '{Tok(nameTok)}': an `extern` prototype cannot have a `comptime` or `anytype` parameter");
        }
        _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok),
            Kind = SymKind.Func,
            Type = new CType.Func(ret, paramInfos.Select(p => LowerType(p.TypeAst)).ToList(), variadic),
            IsGlobal = true,
            FromSystemHeader = true,
        });
    }

    /// <summary>Pass 2: lower a function body. Params share the function's top scope
    /// (a top-block redecl of a param name is an error in C; Zig likewise), so they
    /// are declared inside the function scope before the body. A plain (non-generic) body has no
    /// comptime seeds — a generic INSTANCE body (wall-plan W3a/W3b) routes through
    /// <see cref="LowerFnBodyCore"/> directly with its comptime value / type seeds.</summary>
    private void LowerFnBody(Symbol funcSym, List<(string name, CType type)> paramInfos, Item body)
        => LowerFnBodyCore(funcSym, paramInfos, body, comptimeSeeds: null);

    /// <summary>The shared body-lowering core (pass 2 for a plain function, the worklist drain for a
    /// generic instance). <paramref name="comptimeSeeds"/> — non-null only for a monomorphized instance
    /// (wall-plan W3a) — declares each <c>comptime</c>-value parameter as an in-scope symbol with NO
    /// runtime decl and records its resolved value in <see cref="_comptimeVars"/>, so body references
    /// substitute the literal (the <c>comptime var</c> mechanism). <paramref name="typeSeeds"/> —
    /// non-null only for a comptime-TYPE-param instance (wall-plan W3b) — seeds each <c>T ↦ concrete</c>
    /// into <see cref="_typeAliases"/>, SHADOW-SAVED via <see cref="_typeAliasShadows"/> and restored at
    /// body exit, so the body resolves <c>T</c> (in a local type / <c>@sizeOf(T)</c> / cast) through
    /// <see cref="LowerTypeName"/> without leaking the binding into the next drained instance or a nested
    /// instantiation whose type param shares the name. Because each instance seeds FRESH bindings and
    /// draining is sequential, no per-instance frame stack is needed (see the class doc's scoping
    /// note).</summary>
    private void LowerFnBodyCore(Symbol funcSym, IReadOnlyList<(string name, CType type)> paramInfos, Item body,
        IReadOnlyList<(string name, long value, CType type)>? comptimeSeeds,
        IReadOnlyList<TypeSeed>? typeSeeds = null,
        IReadOnlyList<(string name, bool hasValue, long value, CType inner)>? optionalSeeds = null,
        IReadOnlyList<(string name, LitStr value)>? stringSeeds = null)
    {
        // A generic INSTANCE body (comptime value / type / optional seeds present) unlocks comptime
        // control flow — comptime-if folding + dead-code-after-a-comptime-terminator (wall-plan W3a). Set
        // here; the drain never nests a body in another, so a plain set/overwrite per call is sufficient.
        _inGenericInstance = comptimeSeeds is not null || typeSeeds is not null || optionalSeeds is not null
            || stringSeeds is not null;
        _currentFnRet = (funcSym.Type as CType.Func)?.Return;
        _currentFnName = funcSym.Name;   // the mangle prefix for an in-function container (wall-plan W2)
        _localContainerShadows.Clear();  // per-function: local containers scope to this body
        _typeAliasShadows.Clear();       // per-function: comptime-type-param seeds scope to this body
        _currentFnHasErrdefer = false;   // set lazily as `errdefer`s are encountered (Milestone H)
        // The declared error set for the foreign-error return check (Milestone X, part 3); resolved
        // NOW (pass 2 — all `const E = error{…}` set decls are processed by here). Null (unconstrained)
        // for an inferred `!T` / `anyerror!T` or a non-error-union function.
        _currentFnErrorSet =
            _fnErrorReturnTypes.TryGetValue(funcSym, out var rt)
            && TryDeclaredErrorSet(rt.retType, rt.errUnion, out var esName, out var esMembers)
                ? (esName, esMembers)
                : null;
        _symbols.BeginFunction();
        _symbols.EnterScope();
        // Seed comptime-TYPE parameters (wall-plan W3b): `T ↦ concrete` into _typeAliases, shadow-saved
        // so a colliding outer/sibling alias name is restored at body exit — the instance body then
        // resolves `T` (in a local type / cast / @sizeOf(T)) to the concrete type through LowerTypeName.
        if (typeSeeds is not null)
        {
            foreach (var (name, type, bits) in typeSeeds)
            {
                _typeAliasShadows.Add((name,
                                       _typeAliases.TryGetValue(name, out var prev) ? prev : (CType?)null,
                                       _declaredIntBits.TryGetValue(name, out var pb) ? pb : (int?)null));
                _typeAliases[name] = type;
                // The DECLARED width rides with the type (see _declaredIntBits) so the body's
                // `@typeInfo(T).int.bits` answers what the caller spelled, not the widened lowering.
                SetDeclaredIntBits(name, bits);
            }
        }
        // Seed comptime-value parameters BEFORE the runtime params + body (wall-plan W3a): a fresh
        // in-scope symbol per seed, its value in _comptimeVars, no runtime decl — references fold to
        // the literal. (A runtime param and a comptime param never share a name in valid Zig.)
        if (comptimeSeeds is not null)
        {
            foreach (var (name, value, type) in comptimeSeeds)
            {
                var seedSym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type });
                _comptimeVars[seedSym] = (value, type);
            }
        }
        // Seed comptime-OPTIONAL value parameters (road-to-zig-std S4b): a fresh in-scope `?T` symbol per
        // seed recorded in _comptimeOptionalVars, no runtime decl. A captured `if (x) |y| … else …` on one
        // folds to the taken branch during lowering (see the fold in LowerIfCapture / LowerIfCaptureExpr).
        if (optionalSeeds is not null)
        {
            foreach (var (name, hasValue, value, inner) in optionalSeeds)
            {
                var seedSym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = new CType.Optional(inner) });
                _comptimeOptionalVars[seedSym] = (hasValue, value, inner);
            }
        }
        // Seed comptime STRING parameters (road-to-zig-std G3 — `comptime fmt: []const u8`): the name binds
        // the literal in _comptimeValues with no runtime symbol, so a reference substitutes the string and
        // a comptime use (`fmt ++ "!"`, a nested generic call passing `fmt` on) folds. Shadow-saved,
        // since the map is function-flat; restored at body exit below.
        var stringShadows = new List<(string name, CExpr? prev)>();
        foreach (var (name, value) in stringSeeds ?? [])
        {
            stringShadows.Add((name, _comptimeValues.TryGetValue(name, out var prevValue) ? prevValue : null));
            _comptimeValues[name] = value;
        }
        var paramSyms = paramInfos
            .Select(p => _symbols.Declare(new Symbol { Name = p.name, Kind = SymKind.Param, Type = p.type }))
            .ToList();
        RecordParamBits(funcSym, paramSyms);
        var blk = LowerBlock(body);
        // Milestone O part 5 — demote a non-escaping, freed, constant-size byte slice allocated
        // through the devirtualized C-heap default (`page_allocator`/`c_allocator`) to a `stackalloc`
        // backing. Runs BEFORE ExitScope so the synthetic backing-buffer temp uniquifies against this
        // function's names (BeginFunction cleared `_usedNames`; ExitScope would not).
        blk = PromoteStackSlices(blk);
        _symbols.ExitScope();
        // Un-shadow: restore any `_containerTypes` plain-name binding an in-function container
        // overwrote (wall-plan W2), so a local `const Point = struct{…}` doesn't leak into the next
        // function. Reverse order handles a name shadowed twice in one body. The mangled IR type
        // stays registered globally (it must — the backend emits it once).
        for (int i = _localContainerShadows.Count - 1; i >= 0; i--)
        {
            var (nm, prev) = _localContainerShadows[i];
            if (prev is { } p) { _containerTypes[nm] = p; } else { _containerTypes.Remove(nm); }
        }
        _localContainerShadows.Clear();
        // Un-seed the comptime-type-param aliases (wall-plan W3b), reverse order, so a type param `T`
        // does not leak into the next drained instance / a sibling function (see the value-param note).
        for (int i = _typeAliasShadows.Count - 1; i >= 0; i--)
        {
            var (nm, prev, prevBits) = _typeAliasShadows[i];
            SetDeclaredIntBits(nm, prevBits);
            if (prev is { } p) { _typeAliases[nm] = p; } else { _typeAliases.Remove(nm); }
        }
        _typeAliasShadows.Clear();
        for (int i = stringShadows.Count - 1; i >= 0; i--)
        {
            var (nm, prev) = stringShadows[i];
            if (prev is { } p) { _comptimeValues[nm] = p; } else { _comptimeValues.Remove(nm); }
        }

        _ir.Functions.Add(new FuncDef(funcSym, paramSyms, blk, false));
    }

    // ---- containers (structs / enums) ------------------------------------

    /// <summary>Register a Zig <c>struct</c> declaration: build its field layout (each
    /// <c>name: Type</c> field's type resolved through <see cref="LowerType"/>, so it can
    /// reference any container registered in pass 0) and hand it to the shared IR aggregate
    /// table via <see cref="IrModule.RegisterStructType"/>. <paramref name="fieldItems"/> are
    /// the body's field members (each a <see cref="Zig.StructField"/>), already split out from
    /// any methods by <see cref="SplitMembers"/>; empty for a <c>struct {}</c>.</summary>
    private void RegisterStruct(string name, IReadOnlyList<Item> fieldItems, AggregateLayout layout = AggregateLayout.Default)
    {
        // Every defaulted field's type and default ASTs, recorded BEFORE any field type lowers: a struct whose
        // registration fails part-way (std.Options, on `ScopeLevel`'s `@EnumLiteral()`) can still answer a
        // read of one field of its default value (`std.options.fmt_max_depth`, see LowerDefaultedConstField).
        foreach (var fd in fieldItems)
        {
            if (fd.Content is Zig.StructFieldDefault pre) { _structFieldDecls[(name, Tok(pre.Arg0))] = (pre.Arg2, pre.Arg4); }
        }
        var fields = new List<StructField>();
        foreach (var fd in fieldItems)
        {
            switch (fd.Content)
            {
                case Zig.StructField f:          // FieldDecl -> IDENT ':' Type
                    fields.Add(PackedAwareField(Tok(f.Arg0), f.Arg2, layout));
                    break;
                case Zig.StructFieldSwitch f:    // FieldDecl -> IDENT ':' SwitchExpr (a comptime-selected type)
                    fields.Add(new StructField(Tok(f.Arg0), LowerSwitchType(f.Arg2)));
                    break;
                case Zig.StructFieldDefault f:   // FieldDecl -> IDENT ':' Type '=' RhsExpr
                    var fname = Tok(f.Arg0);
                    fields.Add(PackedAwareField(fname, f.Arg2, layout));
                    _structFieldDefaults[(name, fname)] = f.Arg4;   // raw default AST — lowered lazily on omission
                    break;
                default:
                    throw new IrUnsupportedException("zig struct field: " + (fd.Content?.GetType().Name ?? "null"));
            }
        }
        _ir.RegisterStructType(name, fields, isUnion: false, layout);
    }

    /// <summary>The TYPE a comptime <c>switch</c> selects (std.mem.SplitIterator's
    /// <c>delimiter: switch (delimiter_type) { .sequence, .any =&gt; []const T, .scalar =&gt; T }</c>): the subject must be
    /// comptime-known, and the selected prong's value is lowered as a type.</summary>
    private CType LowerSwitchType(Item switchItem)
    {
        var (subject, prongs) = switchItem.Content switch
        {
            Zig.SwitchExpr s => (s.Arg2, s.Arg5),
            Zig.SwitchExprTrailing s => (s.Arg2, s.Arg5),
            _ => throw new IrUnsupportedException("zig: a switch type expected; got " + (switchItem.Content?.GetType().Name ?? "null")),
        };
        if (SelectComptimeProng(subject, prongs, out var payload) is not { Expr: { } typeItem } prong)
        {
            throw new IrUnsupportedException(
                "zig: a type spelled as a `switch` needs a comptime-known subject and a prong that names a type");
        }
        EnterComptimeProng(prong, payload);
        try { return LowerType(typeItem); }
        finally { ExitComptimeProng(); }
    }

    /// <summary>A struct field, as a BIT-field in a <c>packed struct</c> when its integer type is declared narrower than
    /// the type it lowers to (std.hash_map's <c>Metadata = packed struct { fingerprint: u7, used: u1 }</c>, one byte in
    /// zig, which <c>@bitCast</c>s to a <c>u8</c>): the backend packs a run of bit-fields into shared storage units, so
    /// the struct's size and bit positions match zig's for these byte-sized runs.</summary>
    private StructField PackedAwareField(string name, Item typeAst, AggregateLayout layout)
    {
        var type = LowerType(typeAst);
        if (layout == AggregateLayout.Packed && type.Unqualified is CType.Prim { Integer: true, Name: not "_Bool", Bytes: var bytes }
            && DeclaredBitsOfTypeArg(typeAst) is { } bits && bits < bytes * 8)
        {
            return new StructField(name, type, bits);
        }
        return new StructField(name, type);
    }

    /// <summary>Register an in-function <c>const P = struct { … };</c> (wall-plan W2) on the fly
    /// during body lowering. Top-level containers pre-register in pass 0; a LOCAL one is first seen
    /// here, so it registers its field layout into the shared IR aggregate table under a
    /// function-mangled name (<c>&lt;fn&gt;__&lt;P&gt;</c>) — unique per (function, container), so two
    /// bodies' like-named locals never collide in the IR — and maps the PLAIN name to that type in
    /// <see cref="_containerTypes"/> (shadow-saved, restored at body exit) so the rest of the body
    /// resolves <c>P</c> / <c>.{ … }</c> / <c>p.field</c> exactly like a top-level struct. Emits no
    /// statement (a type decl is not runtime code). Methods and <c>const</c> members (std.sort's local
    /// <c>Context</c> with <c>lessThan</c> / <c>swap</c>) are declared under the mangled name, their bodies
    /// deferred like a reified generic's (a body cannot lower inside the one being lowered) and carrying the
    /// enclosing generic instance's comptime seeds. A nested container member stays a loud cut.</summary>
    private CStmt LowerLocalStruct(string name, Item? membersItem, AggregateLayout layout,
        IReadOnlyList<(string name, long value, CType type)>? extraValueSeeds = null)
    {
        var (fields, methods, consts, containers) = membersItem is { } m
            ? SplitMembers(m)
            : (new List<Item>(), new List<Item>(), new List<Item>(), new List<Item>());
        if (containers.Count > 0)
        {
            throw new IrUnsupportedException(
                $"zig: an in-function container (`{name}`) declares a nested container, which needs the top-level container "
                + "machinery; declare it at top/container level");
        }
        var mangled = _currentFnName.Length > 0 ? $"{_currentFnName}__{name}" : name;
        if (!_localContainers.Add(mangled))
        {
            throw new IrUnsupportedException($"zig: duplicate in-function container `{name}` in `{_currentFnName}`");
        }
        // Shadow the plain name → the mangled type BEFORE registering the layout, so a
        // self-referential field (`next: *P`) resolves; the previous binding (a top-level type of the
        // same name, or nothing) is restored at body exit (see LowerFnBody).
        _localContainerShadows.Add((name, _containerTypes.TryGetValue(name, out var prev) ? prev : null));
        _containerTypes[name] = new CType.Named(mangled);
        RegisterStruct(mangled, fields, layout);
        if (methods.Count > 0 || consts.Count > 0)
        {
            var inst = _currentInstantiation;
            var typeSeeds = inst?.TypeSeeds ?? System.Array.Empty<TypeSeed>();
            // A comptime-selected struct's capture (`|vec_size|`) is one more value seed of the struct's members.
            IReadOnlyList<(string, long, CType)> valueSeeds = [.. inst?.ValueSeeds ?? System.Array.Empty<(string, long, CType)>(),
                                                               .. extraValueSeeds ?? System.Array.Empty<(string, long, CType)>()];
            var optionalSeeds = inst?.OptionalSeeds ?? System.Array.Empty<(string, bool, long, CType)>();
            _reifiedSeeds[mangled] = (typeSeeds, valueSeeds, optionalSeeds);
            // The mangled name is the container a method's `@This()` / `Self` resolves to (the plain name is only
            // the body's alias, withdrawn at its exit, while the deferred method bodies lower later).
            _containerTypes[mangled] = new CType.Named(mangled);
            using var container = EnterContainer(mangled);
            RegisterContainerConsts(mangled, consts);
            foreach (var methodDef in methods)
            {
                var me = DeclareMethod(mangled, methodDef);
                if (IsFnTemplate(me.sym)) { continue; }
                _pendingReifiedMethods.Add(new PendingReifiedMethod(me.sym, mangled, me.ps, me.body,
                    typeSeeds, valueSeeds, optionalSeeds, inst?.FnSeeds));
            }
        }
        return new Seq(new List<CStmt>());   // no runtime decl — mirrors a top-level container
    }

    /// <summary>Split a struct container body (<c>FieldDecls</c> = a list of <c>Member</c>) into
    /// its field declarations (each a <see cref="Zig.StructField"/>, for the layout), its methods
    /// (the inner <c>FnDef</c> item of each <c>fn</c>/<c>pub fn</c> member, declared as mangled free
    /// functions in pass 1), and its <c>const</c> members (each a <c>VarDecl</c> item, processed by
    /// <see cref="RegisterContainerConsts"/>). A method is always lowered to an internal free
    /// function, so <c>pub</c> carries no extra meaning yet (export visibility for a single-file
    /// program is a no-op) and is dropped here. A NESTED container decl member
    /// (<c>const Inner = struct {…};</c>, road-to-zig-std S9 grammar #89) is collected into
    /// <paramref name="containers"/> (the inner <c>ContainerDecl</c> item), which pass 0 registers as an
    /// ordinary container under a parent-mangled name (see <c>CollectPass0Decls</c>).</summary>
    private static (List<Item> fields, List<Item> methods, List<Item> consts, List<Item> containers) SplitMembers(Item membersItem)
    {
        var fields = new List<Item>();
        var methods = new List<Item>();
        var consts = new List<Item>();
        var containers = new List<Item>();
        foreach (var m in Flatten(membersItem))
        {
            switch (m.Content)
            {
                case Zig.MemberField mf:     fields.Add(mf.Arg0); break;       // FieldDecl ','  → StructField
                case Zig.MemberFieldLast mf: fields.Add(mf.Arg0); break;       // FieldDecl       → StructField
                case Zig.MemberMethod mm:    methods.Add(mm.Arg0); break;      // FnDef
                case Zig.MemberPubMethod mm: methods.Add(mm.Arg1); break;      // 'pub' FnDef
                case Zig.MemberInlineMethod mm:    methods.Add(mm.Arg1); break; // 'inline' FnDef
                case Zig.MemberPubInlineMethod mm: methods.Add(mm.Arg2); break; // 'pub' 'inline' FnDef
                case Zig.MemberComptime: break;   // `comptime { … }`: analysis-only, dropped like the top-level form
                case Zig.MemberTest: break;       // a `test` block: dropped, like the top-level form
                case Zig.MemberConst mc:     consts.Add(mc.Arg0); break;       // VarDecl
                case Zig.MemberPubConst mc:  consts.Add(mc.Arg1); break;       // 'pub' VarDecl
                case Zig.MemberContainer cc:    containers.Add(cc.Arg0); break; // ContainerDecl
                case Zig.MemberPubContainer cc: containers.Add(cc.Arg1); break; // 'pub' ContainerDecl
                default: throw new IrUnsupportedException("zig container member: " + (m.Content?.GetType().Name ?? "null"));
            }
        }
        return (fields, methods, consts, containers);
    }

    /// <summary>Split an enum body (<c>EnumFields</c> = a list of <c>EnumMember</c>) into its value
    /// fields (each a <see cref="Zig.EnumField"/> / <see cref="Zig.EnumFieldInit"/>), its methods
    /// (the inner <c>FnDef</c> of each <c>fn</c>/<c>pub fn</c> member), and its <c>const</c> members
    /// (each a <c>VarDecl</c>) — the enum analogue of <see cref="SplitMembers"/>.</summary>
    private static (List<Item> fields, List<Item> methods, List<Item> consts) SplitEnumMembers(Item membersItem)
    {
        var fields = new List<Item>();
        var methods = new List<Item>();
        var consts = new List<Item>();
        foreach (var m in Flatten(membersItem))
        {
            switch (m.Content)
            {
                case Zig.EnumMemberField mf:     fields.Add(mf.Arg0); break;   // EnumField ','
                case Zig.EnumMemberFieldLast mf: fields.Add(mf.Arg0); break;   // EnumField
                case Zig.EnumMemberMethod mm:    methods.Add(mm.Arg0); break;  // FnDef
                case Zig.EnumMemberPubMethod mm: methods.Add(mm.Arg1); break;  // 'pub' FnDef
                case Zig.EnumMemberInlineMethod mm:    methods.Add(mm.Arg1); break;  // 'inline' FnDef
                case Zig.EnumMemberPubInlineMethod mm: methods.Add(mm.Arg2); break;  // 'pub' 'inline' FnDef
                case Zig.EnumMemberComptime: break;   // `comptime { … }`: analysis-only, dropped
                case Zig.EnumMemberTest: break;       // a `test` block (std.math.Order's `test invert`): dropped
                case Zig.EnumMemberConst mc:     consts.Add(mc.Arg0); break;   // VarDecl
                case Zig.EnumMemberPubConst mc:  consts.Add(mc.Arg1); break;   // 'pub' VarDecl
                // A NESTED container (std.Target.Cpu.Arch's `pub const Family = enum {…}`): registered by pass 0
                // under a parent-mangled name (NestedContainerItems), not here.
                case Zig.EnumMemberContainer or Zig.EnumMemberPubContainer: break;
                default: throw new IrUnsupportedException("zig enum member: " + (m.Content?.GetType().Name ?? "null"));
            }
        }
        return (fields, methods, consts);
    }

    /// <summary>The nested container decl items of a container of ANY kind: a struct body's (SplitMembers), an
    /// enum's (std.Target.Cpu.Arch nests `Family`) and a union's. Empty for a container with a body of none.</summary>
    private static IReadOnlyList<Item> NestedContainerItems(object? content)
    {
        IEnumerable<Item> FromEnum(Item members) => Flatten(members).Select(m => m.Content switch
        {
            Zig.EnumMemberContainer c => c.Arg0,
            Zig.EnumMemberPubContainer c => c.Arg1,
            _ => null,
        }).OfType<Item>();
        IEnumerable<Item> FromUnion(Item members) => Flatten(members).Select(m => m.Content switch
        {
            Zig.UnionMemberContainer c => c.Arg0,
            Zig.UnionMemberPubContainer c => c.Arg1,
            _ => null,
        }).OfType<Item>();
        return content switch
        {
            Zig.StructDecl s => SplitMembers(s.Arg5).containers,
            Zig.ExternStructDecl s => SplitMembers(s.Arg6).containers,
            Zig.PackedStructDecl s => SplitMembers(s.Arg6).containers,
            Zig.PackedStructDeclBacked s => SplitMembers(s.Arg9).containers,
            Zig.EnumDecl e => FromEnum(e.Arg5).ToList(),
            Zig.EnumDeclTyped e => FromEnum(e.Arg8).ToList(),
            Zig.UnionDeclEnum u => FromUnion(u.Arg8).ToList(),
            Zig.UnionDeclTagged u => FromUnion(u.Arg8).ToList(),
            Zig.UnionDeclUntagged u => FromUnion(u.Arg5).ToList(),
            _ => System.Array.Empty<Item>(),
        };
    }

    /// <summary>Split a union body (<c>UnionVariants</c> = a list of <c>UnionMember</c>) into its
    /// variants (each a <see cref="Zig.UnionVariantPayload"/> / <see cref="Zig.UnionVariantVoid"/>),
    /// its methods, and its <c>const</c> members — the union analogue of
    /// <see cref="SplitMembers"/>.</summary>
    private static (List<Item> variants, List<Item> methods, List<Item> consts) SplitUnionMembers(Item membersItem)
    {
        var variants = new List<Item>();
        var methods = new List<Item>();
        var consts = new List<Item>();
        foreach (var m in Flatten(membersItem))
        {
            switch (m.Content)
            {
                case Zig.UnionMemberVariant mv:     variants.Add(mv.Arg0); break;   // UnionVariant ','
                case Zig.UnionMemberVariantLast mv: variants.Add(mv.Arg0); break;   // UnionVariant
                case Zig.UnionMemberMethod mm:      methods.Add(mm.Arg0); break;    // FnDef
                case Zig.UnionMemberPubMethod mm:   methods.Add(mm.Arg1); break;    // 'pub' FnDef
                case Zig.UnionMemberInlineMethod mm:    methods.Add(mm.Arg1); break;    // 'inline' FnDef
                case Zig.UnionMemberPubInlineMethod mm: methods.Add(mm.Arg2); break;    // 'pub' 'inline' FnDef
                case Zig.UnionMemberComptime: break;   // `comptime { … }`: analysis-only, dropped
                case Zig.UnionMemberTest: break;       // a `test` block: dropped
                case Zig.UnionMemberConst mc:       consts.Add(mc.Arg0); break;     // VarDecl
                case Zig.UnionMemberPubConst mc:    consts.Add(mc.Arg1); break;     // 'pub' VarDecl
                case Zig.UnionMemberContainer or Zig.UnionMemberPubContainer: break;   // nested: see NestedContainerItems
                default: throw new IrUnsupportedException("zig union member: " + (m.Content?.GetType().Name ?? "null"));
            }
        }
        return (variants, methods, consts);
    }

    /// <summary>Register a container's <c>const</c> members. Two forms: the self-type alias
    /// <c>const Self = @This();</c> (any alias name; RHS <c>@This()</c>) → records <c>alias → the
    /// container's own type</c> in <see cref="_selfAliases"/> (so a method can spell its receiver /
    /// return / local type as the alias); and a namespaced VALUE const <c>const NAME = expr;</c> (or
    /// <c>const NAME: T = expr;</c>) → records the RHS in <see cref="_containerConsts"/> for inlining
    /// at each <c>Type.NAME</c> use site (container-level consts are comptime in Zig). Runs in pass
    /// 0b (after <see cref="_containerTypes"/> is populated, before signatures) so a method signature
    /// can use a self alias. A container-level <c>var</c> (a namespaced mutable global) is COLLECTED
    /// here and lowered to a real global in pass 1.5 (<see cref="LowerContainerVar"/>).</summary>
    private void RegisterContainerConsts(string container, IReadOnlyList<Item> constItems)
    {
        foreach (var c in constItems)
        {
            Item nameTok; Item? typeItem; Item rhs; bool isVar;
            switch (c.Content)
            {
                case Zig.ConstDecl d:      nameTok = d.Arg1; typeItem = null;   rhs = d.Arg3; isVar = false; break;  // const IDENT = RhsExpr ;
                case Zig.ConstDeclTyped d: nameTok = d.Arg1; typeItem = d.Arg3; rhs = d.Arg5; isVar = false; break;  // const IDENT : Type = RhsExpr ;
                case Zig.VarDecl d:        nameTok = d.Arg1; typeItem = null;   rhs = d.Arg3; isVar = true;  break;
                case Zig.VarDeclTyped d:   nameTok = d.Arg1; typeItem = d.Arg3; rhs = d.Arg5; isVar = true;  break;
                default: throw new IrUnsupportedException("zig container const: " + (c.Content?.GetType().Name ?? "null"));
            }
            var cname = Tok(nameTok);

            // A container-level `var` is a namespaced mutable GLOBAL (Milestone R, part 6). Collect it
            // now (pass 0b); it's lowered to a real GlobalVar in pass 1.5 (LowerContainerVar) — deferred
            // so its initializer can reference functions (declared in pass 1). A `Type.name` read/write
            // then resolves to the global's VarRef (see the `Zig.Field` case + the container-var path).
            if (isVar)
            {
                _pendingContainerVars.Add((container, cname, typeItem, rhs));
                continue;
            }

            // `const Alias = @This();` — the self-type alias. `@This()` is a no-arg builtin; the
            // transparent expression productions collapse, so the RHS content is it directly.
            if (typeItem is null && rhs.Content is Zig.BuiltinCallNoArgs b && Tok(b.Arg0) == "@This")
            {
                if (!_selfAliases.TryGetValue(container, out var aliases))
                {
                    aliases = new Dictionary<string, CType>(System.StringComparer.Ordinal);
                    _selfAliases[container] = aliases;
                }
                aliases[cname] = _containerTypes[container];
                continue;
            }

            // A namespaced VALUE const — store its (annotation + ) RHS to inline at each `Type.NAME`
            // use (see the `Zig.Field` case in LowerExpr).
            if (!_containerConsts.TryGetValue(container, out var consts))
            {
                consts = new Dictionary<string, (Item?, Item)>(System.StringComparer.Ordinal);
                _containerConsts[container] = consts;
                _shared.ContainerConstOwners[container] = this;
            }
            if (!consts.TryAdd(cname, (typeItem, rhs)))
            {
                throw new IrUnsupportedException($"container '{container}' declares const '{cname}' more than once");
            }
        }
    }

    /// <summary>Register a Zig <c>union(enum)</c> declaration as the faithful C tagged-union shape
    /// (see <see cref="ZigUnionInfo"/>): synthesize the tag enum <c>U_Tag</c> (a member per variant,
    /// value = its index) + per-member symbols (so <c>.variant</c> resolves to a tag constant); a
    /// nested overlapping-payload union <c>U_Payload</c> (<c>IsUnion=true</c> → every payload
    /// variant at <c>[FieldOffset(0)]</c>, reusing the shared C union machinery); and the outer
    /// struct <c>U</c> = <c>{ __tag, __payload }</c>. A union with only void variants gets no
    /// <c>__payload</c>. Records the <see cref="ZigUnionInfo"/> for construction + <c>switch</c>.
    /// Variant payload types resolve through pass 0a, so a variant may name any container. Returns
    /// the body's method items (each a <c>FnDef</c>) for declaration in pass 1, and registers its
    /// consts (e.g. <c>const Self = @This();</c> → the outer struct type).</summary>
    private List<Item> RegisterUnion(string name, Item variantsItem)
    {
        var (variantItems, methods, consts) = SplitUnionMembers(variantsItem);
        var variants = ParseUnionVariants(variantItems);

        // Synthesize the tag enum `U_Tag` + its member symbols (variant name → tag constant = index).
        var tagName = name + TagSuffix;
        var tagType = new CType.Enum(tagName, CType.Int);
        var tagMembers = new List<EnumMember>();
        var tagSyms = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
        long idx = 0;
        foreach (var (vname, _) in variants)
        {
            tagMembers.Add(new EnumMember(vname, idx));
            tagSyms[vname] = new Symbol { Name = vname, Kind = SymKind.EnumConst, Type = tagType, ConstValue = idx, IsGlobal = true };
            idx++;
        }
        _ir.RegisterEnumType(tagName, CType.Int, tagMembers);
        _containerTypes[tagName] = tagType;
        _enumMembers[tagName] = tagSyms;

        FinishUnion(name, variants, tagType);
        RegisterContainerConsts(name, consts);   // e.g. `const Self = @This();` → the outer struct type
        return methods;
    }

    /// <summary>Register a Zig <c>union(SomeEnum)</c> declaration — a tagged union whose discriminant
    /// is an EXISTING, named enum rather than an auto-synthesized one (Milestone R). Reuses the named
    /// enum (registered fully in pass 0a, so it's available here in pass 0b) as the tag type, then
    /// builds the same payload-union + outer-struct shape as <see cref="RegisterUnion"/> via
    /// <see cref="FinishUnion"/> — so construction (<see cref="BuildUnionInit"/>) and <c>switch</c>
    /// (<see cref="LowerUnionSwitch"/>) work unchanged (they key off <see cref="ZigUnionInfo.TagType"/>
    /// + the tag enum's member symbols, which a named tag enum already carries). Each variant must
    /// name a member of the tag enum; an extra enum member with no variant is tolerated (a V1 leniency
    /// — Zig requires the sets to correspond exactly).</summary>
    private List<Item> RegisterUnionTagged(string name, string tagEnumName, Item variantsItem)
    {
        var (variantItems, methods, consts) = SplitUnionMembers(variantsItem);
        var variants = ParseUnionVariants(variantItems);

        if (!_containerTypes.TryGetValue(tagEnumName, out var t) || t is not CType.Enum tagType
            || !_enumMembers.TryGetValue(tagEnumName, out var tagSyms))
        {
            throw new IrUnsupportedException(
                $"zig `union({tagEnumName})` tag must name a declared enum type; '{tagEnumName}' is not one");
        }
        foreach (var (vname, _) in variants)
        {
            if (!tagSyms.ContainsKey(vname))
            {
                throw new IrUnsupportedException(
                    $"zig `union({tagEnumName})` variant '{vname}' is not a member of enum '{tagEnumName}'");
            }
        }

        FinishUnion(name, variants, tagType);
        RegisterContainerConsts(name, consts);
        return methods;
    }

    /// <summary>Parse a union body's variant items into <c>(name, payload?)</c> pairs — a
    /// payload variant (<c>name: Type</c>) or a void/tag-only variant (<c>name</c>).</summary>
    private List<(string name, CType? payload)> ParseUnionVariants(IReadOnlyList<Item> variantItems)
    {
        var variants = new List<(string name, CType? payload)>();
        foreach (var v in variantItems)
        {
            switch (v.Content)
            {
                case Zig.UnionVariantPayload vp: variants.Add((Tok(vp.Arg0), LowerType(vp.Arg2))); break;  // IDENT ':' Type
                case Zig.UnionVariantVoid vv:    variants.Add((Tok(vv.Arg0), null)); break;                 // IDENT
                default: throw new IrUnsupportedException("zig union variant: " + (v.Content?.GetType().Name ?? "null"));
            }
        }
        return variants;
    }

    /// <summary>Finish registering a tagged union once its variants + tag enum are known (shared by
    /// the auto-tag <c>union(enum)</c> and the explicit-tag <c>union(SomeEnum)</c> forms): build the
    /// nested overlapping-payload union <c>U_Payload</c> (one <c>[FieldOffset(0)]</c> field per PAYLOAD
    /// variant — none for an all-void union), the outer discriminated struct <c>U = { __tag,
    /// (__payload?) }</c>, and record the <see cref="ZigUnionInfo"/> for construction + <c>switch</c>.</summary>
    private void FinishUnion(string name, IReadOnlyList<(string name, CType? payload)> variants, CType.Enum tagType)
    {
        var variantMap = new Dictionary<string, CType?>(System.StringComparer.Ordinal);
        var payloadFields = new List<StructField>();
        foreach (var (vname, payload) in variants)
        {
            variantMap[vname] = payload;
            if (payload is not null) { payloadFields.Add(new StructField(vname, payload)); }
        }
        string? payloadTypeName = null;
        if (payloadFields.Count > 0)
        {
            payloadTypeName = name + PayloadSuffix;
            _ir.RegisterStructType(payloadTypeName, payloadFields, isUnion: true);   // [StructLayout(Explicit)], all at offset 0
        }
        var fields = new List<StructField> { new StructField(TagFieldName, tagType) };
        if (payloadTypeName is not null) { fields.Add(new StructField(PayloadFieldName, new CType.Named(payloadTypeName))); }
        _ir.RegisterStructType(name, fields, isUnion: false);
        _unions[name] = new ZigUnionInfo(name, tagType, TagFieldName, payloadTypeName, PayloadFieldName, variantMap);
    }

    /// <summary>Register a Zig UNTAGGED <c>union { a: T, b: U, … }</c> (Milestone R, part 3) — a bare
    /// overlapping-storage union with NO discriminant. Unlike a tagged union it has no outer
    /// <c>{ __tag, __payload }</c> wrapper and is NOT a <see cref="ZigUnionInfo"/>: the union TYPE
    /// itself is the overlay struct (<c>[StructLayout(Explicit)]</c>, every variant at
    /// <c>[FieldOffset(0)]</c>, via the shared C-union machinery — <c>isUnion: true</c>). Construction
    /// (<c>U{ .a = v }</c> / <c>.{ .a = v }</c>) and access (<c>u.a</c>) therefore route through the
    /// ordinary struct-init / member paths, not <see cref="BuildUnionInit"/>. Each variant must carry a
    /// payload type — a void variant needs a tagged <c>union(enum)</c> (there is no tag here to select
    /// it). Zig's safe-mode active-field tracking / type-pun checks are NOT modeled (same-field
    /// read/write is faithful; a `switch` on an untagged union is rejected, as Zig forbids it).
    /// Returns the body's method items for declaration in pass 1, and registers its consts.</summary>
    private List<Item> RegisterUnionUntagged(string name, Item variantsItem)
    {
        var (variantItems, methods, consts) = SplitUnionMembers(variantsItem);
        var variants = ParseUnionVariants(variantItems);
        var fields = new List<StructField>();
        foreach (var (vname, payload) in variants)
        {
            if (payload is null)
            {
                throw new IrUnsupportedException(
                    $"untagged union '{name}' variant '{vname}' must have a type — a void variant needs a tagged `union(enum)`");
            }
            fields.Add(new StructField(vname, payload));
        }
        _ir.RegisterStructType(name, fields, isUnion: true);   // [StructLayout(Explicit)], all at offset 0
        RegisterContainerConsts(name, consts);
        return methods;
    }

    /// <summary>Register a Zig <c>enum</c> declaration: assign each member its value
    /// (explicit <c>= value</c> via <see cref="ZigConstEval"/>, else auto-incremented from
    /// the previous, starting at 0), build the shared <see cref="EnumTypeDef"/> via
    /// <see cref="IrModule.RegisterEnumType"/>, and record a per-member
    /// <see cref="SymKind.EnumConst"/> symbol (so <c>Color.red</c> / a sink-typed
    /// <c>.red</c> resolve to an <see cref="EnumConstRef"/>). The underlying type is the
    /// <c>enum(T)</c> base, else C's default <see cref="CType.Int"/>. Returns the body's method
    /// items (each a <c>FnDef</c>) for declaration in pass 1, and registers its consts (e.g.
    /// <c>const Self = @This();</c> → the enum type).</summary>
    private List<Item> RegisterEnumZig(string name, Item? underlyingType, Item membersItem)
    {
        var (fieldItems, methods, consts) = SplitEnumMembers(membersItem);
        var underlying = underlyingType is not null ? LowerType(underlyingType) : CType.Int;
        // Remember whether the SOURCE spelled the tag type. zig INFERS one for an untyped enum (the
        // smallest unsigned int holding the largest member — `u2` for four members) while dotcc
        // defaults to `int`, so reporting an inferred tag through `@typeInfo(E).@"enum".tag_type`
        // would disagree with zig on its width and @sizeOf. Only a spelled tag is answered there.
        if (underlyingType is not null) { _enumsWithSpelledTag.Add(name); }
        var enumType = new CType.Enum(name, underlying);
        var members = new List<EnumMember>();
        var memberSyms = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
        long next = 0;
        foreach (var emItem in fieldItems)
        {
            string mName;
            Item? valExpr;
            switch (emItem.Content)
            {
                case Zig.EnumField ef:     mName = Tok(ef.Arg0); valExpr = null; break;       // IDENT
                case Zig.EnumFieldInit ef: mName = Tok(ef.Arg0); valExpr = ef.Arg2; break;    // IDENT '=' Expr
                default: throw new IrUnsupportedException("zig enum member: " + (emItem.Content?.GetType().Name ?? "null"));
            }
            if (valExpr is not null)
            {
                var lowered = LowerExpr(valExpr);
                next = ZigConstEval(lowered)
                    // An `enum(u64)` / `enum(usize)` member above long.MaxValue (std.Io.Limit's
                    // `unlimited = std.math.maxInt(usize)`) is kept as its 64-bit pattern.
                    ?? (underlying.Unqualified is CType.Prim { Integer: true, Signed: false, Bytes: 8 }
                        && _ir.ConstEval128(lowered) is { } wide && wide >= 0 && wide <= ulong.MaxValue
                            ? unchecked((long)(ulong)wide)
                            : (long?)null)
                    ?? throw new IrUnsupportedException($"enum '{name}' member '{mName}': value must be a constant integer expression");
            }
            members.Add(new EnumMember(mName, next));
            memberSyms[mName] = new Symbol
            {
                Name = mName, Kind = SymKind.EnumConst, Type = enumType, ConstValue = next, IsGlobal = true,
            };
            next++;
        }
        _ir.RegisterEnumType(name, underlying, members);
        _containerTypes[name] = enumType;
        _enumMembers[name] = memberSyms;
        RegisterContainerConsts(name, consts);   // e.g. `const Self = @This();` → the enum type
        return methods;
    }

    /// <summary>Const-fold a lowered enum-member initializer to its integer value, or null
    /// if it is not a compile-time constant. Routed through the shared
    /// <see cref="IrModule.ConstEval"/> interpreter (Milestone T), so a Zig enum value
    /// may now be any constant expression — binary arithmetic/bitwise/shift, parens,
    /// sizeof, a reference to an earlier member — not just a literal or unary of one.</summary>
    private long? ZigConstEval(CExpr e) => _ir.ConstEval(e);

    /// <summary>Resolve a bare enum literal <c>.member</c> against the enum type its sink
    /// names — an <see cref="EnumConstRef"/> the shared backend renders as
    /// <c>EnumName.member</c>.</summary>
    private CExpr ResolveEnumLit(string member, CType.Enum en)
    {
        if (_enumMembers.TryGetValue(en.Name, out var syms) && syms.TryGetValue(member, out var sym))
        {
            return new EnumConstRef(sym) { Type = en };
        }
        throw new IrUnsupportedException($"enum '{en.Name}' has no member '{member}'");
    }

    /// <summary>Lower an anonymous struct literal <c>.{ .f = v, … }</c> against the struct
    /// type its sink names (Zig's result-location inference). Each <c>.field = value</c>
    /// pairs the field with its declared type (looked up via
    /// <see cref="IrModule.StructFieldType"/>) so the value coerces as C would at the
    /// store; the value is itself lowered at that field type as its sink (so a nested
    /// <c>.{…}</c> or <c>.member</c> resolves). An omitted field takes C#'s zero default —
    /// matching C's partial-init / Zig's required-field rule isn't enforced in D1. An empty
    /// <c>.{}</c> zero-inits every field.</summary>
    private CExpr LowerStructInit(Item initItem, CType? sink)
    {
        // The empty `.{}` (AnonStructInitEmpty) carries no field list — zero-init every field.
        IReadOnlyList<Item> fields = initItem.Content is Zig.AnonStructInit a ? Flatten(a.Arg2) : [];   // Primary -> '.' '{' FieldInits '}'
        // A `.{…}` is a TUPLE when its elements are positional (`.{a, b}`) and a STRUCT/UNION when
        // they are named (`.{.f = v}`). Zig never mixes the two in one literal — reject that early.
        bool anyPositional = false, anyNamed = false;
        foreach (var f in fields)
        {
            if (f.Content is Zig.FieldInitPositional) { anyPositional = true; } else { anyNamed = true; }
        }
        if (anyPositional && anyNamed)
        {
            throw new IrUnsupportedException(
                "zig `.{…}` mixes positional and named fields — a tuple literal is all-positional, a struct literal all-named");
        }
        // Array literal: a positional `.{e0, e1, …}` whose sink is a `[N]T` array (Milestone K) →
        // a stackalloc'd array value. Checked BEFORE the tuple sink so `[N]T` wins over a same-arity
        // tuple. A named element here is a mistake (an array literal is all-positional).
        if (sink?.Unqualified is CType.Array arrSink)
        {
            if (anyNamed)
            {
                throw new IrUnsupportedException(
                    "zig array literal `.{…}` must use positional elements, not `.field = …`");
            }
            return BuildArrayInit(fields, arrSink);
        }
        // Tuple literal: a positional list, or a (positional/empty) `.{…}` whose sink IS a tuple.
        // Element types come from the sink, or are inferred from the elements when there's none.
        if (sink?.Unqualified is CType.Tuple tupleSink)
        {
            if (anyNamed)
            {
                throw new IrUnsupportedException(
                    "zig tuple `.{…}` at a tuple type must use positional elements, not `.field = …`");
            }
            return BuildTupleInit(fields, tupleSink);
        }
        if (anyPositional)
        {
            return BuildTupleInit(fields, null);   // inferred tuple (no sink, e.g. `const t = .{a, b};`)
        }
        // An EMPTY `.{}` with no struct/union sink is an empty tuple (`const t = .{};`) — a zero-field
        // ValueTuple. (At a Named struct/union sink it zero-inits that aggregate, handled below.)
        if (fields.Count == 0 && sink?.Unqualified is not CType.Named)
        {
            return BuildTupleInit(fields, sink?.Unqualified as CType.Tuple);
        }
        // Named struct / union — needs a known struct result type.
        if (sink?.Unqualified is not CType.Named named)
        {
            throw new IrUnsupportedException(
                "zig anonymous struct literal `.{…}` needs a known struct result type (a typed const/var, a return, or a field)");
        }
        // A tagged-union sink → a union literal (sets the tag + exactly one payload variant).
        if (_unions.TryGetValue(named.Name, out var uinfo)) { return BuildUnionInit(fields, uinfo); }
        return BuildStructInit(fields, named);
    }

    /// <summary>Build a tuple literal <c>.{ a, b, … }</c> (Milestone G) → <see cref="TupleNew"/>.
    /// With a <paramref name="sink"/> tuple type, each element lowers at its declared element type
    /// as its sink (so a nested <c>.{…}</c>/<c>.member</c> resolves) and the count must match; with
    /// no sink the element types are inferred from the elements themselves
    /// (<c>const t = .{a, b};</c>). Arity 1..7 (empty and &gt; 7 deferred — see
    /// <see cref="LowerTupleType"/>).</summary>
    private CExpr BuildTupleInit(IReadOnlyList<Item> posItems, CType.Tuple? sink)
    {
        if (sink is not null && posItems.Count != sink.Elements.Count)
        {
            throw new IrUnsupportedException(
                $"zig tuple literal has {posItems.Count} element(s) but the target tuple has {sink.Elements.Count}");
        }
        var elems = new List<CExpr>();
        var types = new List<CType>();
        for (int i = 0; i < posItems.Count; i++)
        {
            var pos = (Zig.FieldInitPositional)posItems[i].Content!;   // FieldInit -> Expr
            if (sink is not null)
            {
                var et = sink.Elements[i];
                elems.Add(LowerExprSink(pos.Arg0, et));
                types.Add(et);
            }
            else
            {
                var e = LowerExpr(pos.Arg0);
                elems.Add(e);
                // A string literal element is zig's `*const [N:0]u8`: the element type counts the
                // N bytes and not the sentinel NUL the lowered LitStr carries, so a later slice
                // coercion of the field (`{s}` in std.fmt) gets zig's `.len`.
                types.Add(IsStringLiteralValue(e) && e.Type.Unqualified is CType.Array { Count: > 0 and var n } lit
                    ? lit with { Count = n - 1 }
                    : e.Type);
            }
        }
        var tt = sink ?? new CType.Tuple(types);
        return new TupleNew(elems, tt) { Type = tt };
    }

    /// <summary>Build an array literal (Milestone K) — a positional `.{e0, e1, …}` at a `[N]T` sink,
    /// or a typed `[N]T{…}` / `[_]T{…}` — as a <see cref="StackArray"/> (a stackalloc'd array value;
    /// the backend hoists it to a block-local pointer temp when used outside an initializer). Each
    /// element lowers at the array's element type as its sink (so a nested `.{…}` / `.member`
    /// resolves). A fixed extent must match the element count; an inferred `[_]T` (Count null) takes
    /// the element count. An empty literal is a zero-length array (<c>&amp;[0]u8{}</c>, std.mem.join's empty result, or
    /// <c>[_]T{}</c>); at a non-zero extent it is rejected, since a zeroed array uses `undefined`.</summary>
    private CExpr BuildArrayInit(IReadOnlyList<Item> posItems, CType.Array arr)
    {
        if (posItems.Count == 0 && arr.Count is not (null or 0))
        {
            throw new IrUnsupportedException(
                "zig empty array literal is not supported — initialize a `[N]T` with `undefined` for a zeroed array");
        }
        if (arr.Count is { } n && posItems.Count != n)
        {
            throw new IrUnsupportedException(
                $"zig array literal has {posItems.Count} element(s) but the target array `[{n}]…` expects {n}");
        }
        var elems = LowerArrayElems(posItems, arr.Element);
        var arrType = arr.Count is null ? new CType.Array(arr.Element, posItems.Count) : arr;
        return new StackArray(arr.Element, elems) { Type = arrType };
    }

    /// <summary>Lower the positional elements of an array literal, each at <paramref name="element"/>
    /// as its sink (so a nested literal / bare `.member` resolves). A named `.field = …` element is
    /// rejected — an array literal is all-positional.</summary>
    private List<CExpr> LowerArrayElems(IReadOnlyList<Item> fields, CType element)
    {
        var elems = new List<CExpr>(fields.Count);
        foreach (var f in fields)
        {
            if (f.Content is not Zig.FieldInitPositional pos)
            {
                throw new IrUnsupportedException(
                    "zig array literal must use positional elements (`.{a, b}` / `[N]T{a, b}`), not `.field = …`");
            }
            elems.Add(LowerExprSink(pos.Arg0, element));
        }
        return elems;
    }

    /// <summary>Lower a TYPED struct literal `Type{ .field = … }` — Zig's CurlySuffixExpr
    /// (`CurlySuffix -> Type '{' FieldInits '}'`). Unlike the anonymous `.{…}` form, the struct
    /// type is named explicitly by the leading <c>Type</c>, so this needs NO sink and is valid in
    /// any expression position (e.g. <c>&amp;Point{…}</c>, a bare subexpression). The leading type
    /// must resolve to a registered struct.</summary>
    private CExpr LowerTypedStructInit(Item typeItem, IReadOnlyList<Item> fieldInitItems)
    {
        // `[N]T{…}` / `[_]T{…}` — a typed array literal (Milestone K). Resolve the element type and
        // extent WITHOUT lowering the whole `[_]T` type (LowerType can't const-eval the inferred `_`);
        // `[_]T` takes the element count, `[N]T` the literal N (which must match the elements).
        if (typeItem.Content is Zig.TyArray ta)
        {
            var element = LowerType(ta.Arg3);
            var inferred = ta.Arg1.Content is Zig.Ident id && Tok(id.Arg0) == "_";
            var arr = inferred
                ? new CType.Array(element, null)
                : new CType.Array(element, ConstEvalArraySize(ta.Arg1));
            return BuildArrayInit(fieldInitItems, arr);
        }
        var t = LowerType(typeItem);
        // A user-constructed custom allocator (Milestone W, part 1b): `std.mem.Allocator{ .ptr, .vtable }`
        // and the `std.mem.Allocator.VTable{ .alloc, .resize, .remap, .free }` literal it points at.
        if (t.Unqualified is CType.Allocator) { return BuildAllocatorLiteral(fieldInitItems); }
        if (t.Unqualified is CType.Named { Name: VTableTypeName }) { return BuildAllocatorVTableLiteral(fieldInitItems); }
        if (t.Unqualified is not CType.Named named)
        {
            throw new IrUnsupportedException(
                $"zig typed struct literal `Type{{…}}` requires a struct type, got {t.Describe()}");
        }
        // A typed tagged-union literal `U{ .variant = … }` sets the tag + the one payload variant.
        if (_unions.TryGetValue(named.Name, out var uinfo)) { return BuildUnionInit(fieldInitItems, uinfo); }
        return BuildStructInit(fieldInitItems, named);
    }

    /// <summary>Shared back half of both struct-literal forms (anonymous `.{…}` and typed
    /// `Type{…}`): turn the `.field = expr` items into <see cref="FieldInit"/>s against a known
    /// struct type. Each field's declared type (via <see cref="IrModule.StructFieldType"/>) is
    /// the value's sink, so a nested `.{…}`/`.member` resolves; an unknown field errors
    /// precisely. An omitted field takes C#'s zero default (D1 doesn't enforce Zig's
    /// required-field rule).</summary>
    private CExpr BuildStructInit(IReadOnlyList<Item> fieldInitItems, CType.Named named)
    {
        var members = new List<FieldInit>();
        var written = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var fiItem in fieldInitItems)
        {
            var fi = (Zig.FieldInit)fiItem.Content!;   // FieldInit -> '.' IDENT '=' Expr
            var fname = Tok(fi.Arg1);
            var ftype = _ir.StructFieldType(named, fname)
                ?? throw new IrUnsupportedException($"struct '{named.Name}' has no field '{fname}'");
            written.Add(fname);   // set before the array check, so the defaults pass doesn't re-add it
            if (IsInlineArrayMember(named.Name, fname, ftype, fi.Arg3)) { continue; }
            members.Add(new FieldInit(fname, ftype, LowerExprSink(fi.Arg3, ftype)));
        }
        // Materialize a declared default (`field: T = expr`, std S9) for any field OMITTED from the
        // literal — Zig fills it from the field's default. Fields with NO default that are omitted keep
        // C#'s zero-init (a documented leniency; Zig would require them to be set).
        if (_ir.StructFieldsOf(named.Name) is { } allFields)
        {
            foreach (var f in allFields)
            {
                if (written.Contains(f.Name)) { continue; }
                if (_structFieldDefaults.TryGetValue((named.Name, f.Name), out var defItem))
                {
                    if (IsInlineArrayMember(named.Name, f.Name, f.Type, defItem)) { continue; }
                    // A reified struct's default may read its comptime params (`n: u8 = n`), and any
                    // default may name a sibling const (`fingerprint: FingerPrint = free` in hash_map's
                    // Metadata): it is evaluated in its container's scope, not the literal's.
                    using var seeds = EnterReifiedSeeds(named.Name);
                    var prevConstContainer = _currentConstContainer;
                    _currentConstContainer = named.Name;
                    try
                    {
                        using (EnterContainer(named.Name))
                        {
                            members.Add(new FieldInit(f.Name, f.Type, LowerExprSink(defItem, f.Type)));
                        }
                    }
                    finally { _currentConstContainer = prevConstContainer; }
                }
            }
        }
        return new StructInit(members) { Type = named };
    }

    /// <summary>True when a struct-literal member targets an ARRAY field and must be DROPPED from the
    /// initializer. An array field is INLINE storage — the C# backend renders it as a <c>fixed</c> buffer
    /// (or an <c>[InlineArray]</c> wrapper), neither of which can be assigned inside an object
    /// initializer: <c>new B { items = … }</c> is <b>CS1666</b> ("cannot use fixed size buffers contained
    /// in unfixed expressions"), which dotcc used to emit silently — a bad emit, the worst failure class.
    /// Zig's <c>undefined</c> asks for no particular contents, so the member is simply dropped and C#'s
    /// zero-init stands (a zeroed over-approximation, exactly as for <c>var x: T = undefined</c>); an all-zero
    /// <c>@splat(0)</c> is exactly that zero-init. Any
    /// other value would need element-wise stores into a pinned buffer, which an initializer EXPRESSION
    /// cannot express, so it is a precise loud cut naming the workaround.</summary>
    private static bool IsInlineArrayMember(string structName, string fieldName, CType fieldType, Item valueItem)
    {
        if (fieldType.Unqualified is not CType.Array) { return false; }
        if (valueItem.Content is Zig.UndefinedLit) { return true; }
        // `@splat(0)` (std.Target.Cpu.Feature.Set's `empty = .{ .ints = @splat(0) }`): all zeros, which C#'s
        // zero-init of the buffer already is.
        if (valueItem.Content is Zig.BuiltinCall { Arg0: var splatTok } splat && Tok(splatTok) == "@splat"
            && Flatten(splat.Arg2) is [{ Content: Zig.IntLit { Arg0: var zeroTok } }] && Tok(zeroTok) == "0")
        {
            return true;
        }
        throw new IrUnsupportedException(
            $"struct '{structName}': field '{fieldName}' is an array — inline array storage can't be "
            + "initialized from a struct literal (only `undefined` can); build the value first and assign "
            + $"the elements (`var v: {structName} = undefined; v.{fieldName}[0] = …;`)");
    }

    /// <summary>The field types of the runtime <c>AllocatorVTable</c> — Zig's
    /// <c>std.mem.Allocator.VTable</c> shape (Milestone W, part 1b). Each is a MANAGED function
    /// pointer (<c>delegate*&lt;…&gt;</c>) whose signature matches the corresponding user function's
    /// lowering, so a <c>&amp;fn</c> reference in a <c>VTable{…}</c> literal binds cleanly. Returns
    /// <c>null</c> for an unknown field. Mirrors <c>AllocatorVTable</c> in <c>DotCC.Libc/ZigAlloc.cs</c>:
    /// <c>*anyopaque</c>→<c>void*</c>, <c>?[*]u8</c>→<c>byte*</c>, <c>[]u8</c>→<c>Slice&lt;byte&gt;</c>,
    /// <c>usize</c>→<c>ulong</c>, <c>std.mem.Alignment</c>→<c>Alignment</c>.</summary>
    private static CType? VTableFieldType(string field)
    {
        CType ctx = new CType.Pointer(CType.Void);     // *anyopaque
        CType optPtr = new CType.Pointer(CType.UChar); // ?[*]u8
        CType mem = new CType.Slice(CType.UChar);      // []u8
        CType usz = CType.ULong;                       // usize
        CType aln = new CType.Named(AlignmentTypeName); // std.mem.Alignment
        return field switch
        {
            "alloc"  => new CType.Func(optPtr, new[] { ctx, usz, aln, usz }, false),
            "resize" => new CType.Func(CType.Bool, new[] { ctx, mem, aln, usz, usz }, false),
            "remap"  => new CType.Func(optPtr, new[] { ctx, mem, aln, usz, usz }, false),
            "free"   => new CType.Func(CType.Void, new[] { ctx, mem, aln, usz }, false),
            _ => null,
        };
    }

    /// <summary>Lower a <c>std.mem.Allocator.VTable{ .alloc = f, .resize = g, .remap = h, .free = k }</c>
    /// literal (Milestone W, part 1b) to a <see cref="StructInit"/> of the runtime
    /// <c>AllocatorVTable</c>. Each function reference is result-located against its
    /// <see cref="VTableFieldType"/>, so a bare function name decays to <c>&amp;fn</c> matching the
    /// managed fn-pointer field. The C# backend renders <c>new AllocatorVTable { alloc = &amp;f, … }</c>
    /// purely from the node (no registered field metadata needed).</summary>
    private CExpr BuildAllocatorVTableLiteral(IReadOnlyList<Item> fieldInitItems)
    {
        var members = new List<FieldInit>();
        foreach (var fiItem in fieldInitItems)
        {
            var fi = (Zig.FieldInit)fiItem.Content!;   // FieldInit -> '.' IDENT '=' Expr
            var fname = Tok(fi.Arg1);
            var ftype = VTableFieldType(fname)
                ?? throw new IrUnsupportedException(
                    $"std.mem.Allocator.VTable has no field '{fname}' (expected alloc / resize / remap / free)");
            members.Add(new FieldInit(fname, ftype, LowerExprSink(fi.Arg3, ftype)));
        }
        return new StructInit(members) { Type = new CType.Named(VTableTypeName) };
    }

    /// <summary>Lower a <c>std.mem.Allocator{ .ptr = p, .vtable = &amp;vt }</c> literal (Milestone W,
    /// part 1b) to a <see cref="StructInit"/> of the runtime <c>Allocator</c> fat pointer. <c>.ptr</c>
    /// (a <c>*anyopaque</c> context) maps to the runtime <c>Ctx</c> (<c>void*</c>); <c>.vtable</c> is a
    /// <c>*const VTable</c> in Zig but the runtime carries the table BY VALUE, so the <c>&amp;vt</c> is
    /// dereferenced to the vtable value (<see cref="LowerVtableByValue"/>). The resulting
    /// <see cref="CType.Allocator"/> value routes through the existing indirect dispatch
    /// (<c>a.alloc</c>/<c>a.free</c> → the vtable functions).</summary>
    private CExpr BuildAllocatorLiteral(IReadOnlyList<Item> fieldInitItems)
    {
        CExpr? ctx = null;
        CExpr? vtable = null;
        foreach (var fiItem in fieldInitItems)
        {
            var fi = (Zig.FieldInit)fiItem.Content!;
            var fname = Tok(fi.Arg1);
            switch (fname)
            {
                case "ptr": ctx = LowerExprSink(fi.Arg3, new CType.Pointer(CType.Void)); break;
                case "vtable": vtable = LowerVtableByValue(fi.Arg3); break;
                default:
                    throw new IrUnsupportedException(
                        $"std.mem.Allocator literal has no field '{fname}' (expected ptr / vtable)");
            }
        }
        if (ctx is null || vtable is null)
        {
            throw new IrUnsupportedException("std.mem.Allocator literal requires both .ptr and .vtable");
        }
        var members = new List<FieldInit>
        {
            new FieldInit("Ctx", new CType.Pointer(CType.Void), ctx),
            new FieldInit("Vtable", new CType.Named(VTableTypeName), vtable),
        };
        return new StructInit(members) { Type = new CType.Allocator() };
    }

    /// <summary>Lower a <c>.vtable = &amp;vt</c> initializer to the vtable VALUE: Zig's
    /// <c>Allocator.vtable</c> is a <c>*const VTable</c>, but dotcc's runtime <c>Allocator</c> holds
    /// the table by value, so drop a leading <c>&amp;</c> (or dereference a general <c>*VTable</c>).</summary>
    private CExpr LowerVtableByValue(Item vtableExpr)
    {
        var e = LowerExpr(vtableExpr);
        if (e is Unary { Op: UnOp.AddrOf } u) { return u.Operand; }
        if (e.Type.Unqualified is CType.Pointer p) { return new Unary(UnOp.Deref, e) { Type = p.Pointee }; }
        return e;
    }

    /// <summary>Build a tagged-union PAYLOAD literal — <c>.{ .variant = value }</c> or
    /// <c>U{ .variant = value }</c> — as a <see cref="StructInit"/> that sets BOTH the
    /// <see cref="TagFieldName"/> discriminant (to the variant's tag constant) and the variant's
    /// payload field (the value lowered at the payload type as its sink). Exactly one variant must
    /// be set; a void variant is constructed with the bare <c>.variant</c> form
    /// (<see cref="BuildVoidVariant"/>), not this one.</summary>
    private CExpr BuildUnionInit(IReadOnlyList<Item> fieldInitItems, ZigUnionInfo info)
    {
        if (fieldInitItems.Count != 1)
        {
            throw new IrUnsupportedException(
                $"zig tagged-union literal for '{info.Name}' must set exactly one variant (got {fieldInitItems.Count})");
        }
        var fi = (Zig.FieldInit)fieldInitItems[0].Content!;   // FieldInit -> '.' IDENT '=' Expr
        var variant = Tok(fi.Arg1);
        if (!info.Variants.TryGetValue(variant, out var payloadType))
        {
            throw new IrUnsupportedException($"union '{info.Name}' has no variant '{variant}'");
        }
        // `.{ .none = {} }` (std.fmt.Parser.specifier): a void variant given the void value is the bare `.none`.
        if (payloadType is null && fi.Arg3.Content is Zig.VoidValue)
        {
            return BuildVoidVariant(info, variant);
        }
        if (payloadType is null)
        {
            throw new IrUnsupportedException(
                $"union '{info.Name}' variant '{variant}' is a void variant — construct it as `.{variant}`, not `.{{ .{variant} = … }}`");
        }
        // Nested: new U { __tag = U_Tag.variant, __payload = new U_Payload { variant = value } }.
        var payloadNamed = new CType.Named(info.PayloadTypeName!);
        var payloadInit = new StructInit(new List<FieldInit>
        {
            new FieldInit(variant, payloadType, LowerExprSink(fi.Arg3, payloadType)),
        }) { Type = payloadNamed };
        var members = new List<FieldInit>
        {
            new FieldInit(info.TagFieldName, info.TagType, ResolveEnumLit(variant, info.TagType)),
            new FieldInit(info.PayloadFieldName, payloadNamed, payloadInit),
        };
        return new StructInit(members) { Type = new CType.Named(info.Name) };
    }

    /// <summary>Build a tagged-union VOID variant — the bare <c>.variant</c> form at a union sink
    /// — as a <see cref="StructInit"/> that sets only the <see cref="TagFieldName"/> discriminant
    /// (the payload fields take their zero default). The variant must be a void / tag-only
    /// variant.</summary>
    private CExpr BuildVoidVariant(ZigUnionInfo info, string variant)
    {
        if (!info.Variants.TryGetValue(variant, out var payloadType))
        {
            throw new IrUnsupportedException($"union '{info.Name}' has no variant '{variant}'");
        }
        if (payloadType is not null)
        {
            throw new IrUnsupportedException(
                $"union '{info.Name}' variant '{variant}' carries a payload — construct it as `.{{ .{variant} = … }}`");
        }
        var members = new List<FieldInit> { new FieldInit(info.TagFieldName, info.TagType, ResolveEnumLit(variant, info.TagType)) };
        return new StructInit(members) { Type = new CType.Named(info.Name) };
    }

    /// <summary>The container a DECL LITERAL at <paramref name="sink"/> looks its member up in — the sink's
    /// struct/union name — or null when the sink is not a zig container (an enum sink resolves a bare
    /// <c>.member</c> as a tag instead, and a curated std type models its own literals).</summary>
    private static string? DeclLiteralContainer(CType? sink)
        => sink?.Unqualified is CType.Named { Name: var name } && name is not (FbaTypeName or ArenaTypeName) ? name : null;

    /// <summary>Lower a decl-literal CALL <c>.name(args)</c> at a sink of container type
    /// <paramref name="container"/>: zig resolves <c>name</c> as a declaration of the RESULT type, so this
    /// is exactly the associated call <c>Container.name(args)</c>. The function is found through the shared
    /// method registry (<see cref="EnsureMethodDeclared"/>), so a container another module declares —
    /// <c>std.Io.Writer</c>'s <c>fixed</c> — declares and lowers in its own module. Its return type is not
    /// checked against the sink here; the ordinary store coercion does that.</summary>
    private CExpr LowerDeclLiteralCall(string container, string name, IReadOnlyList<Item> argItems)
    {
        // The curated `std.mem.Alignment` (the runtime carrier) models `.fromByteUnits(n)` directly.
        // `.of(T)` (std.Io.Writer.Allocating's `.of(u8)`): T's own alignment.
        if (container == AlignmentTypeName && name == "of" && argItems.Count == 1)
        {
            var ofAlign = _ir.AlignOfConst(LowerType(argItems[0]));
            return new Call("Alignment.fromByteUnits",
                new List<CExpr> { new LitInt(ofAlign.ToString(System.Globalization.CultureInfo.InvariantCulture), ofAlign) { Type = CType.ULong } },
                new List<CType> { CType.ULong }, null) { Type = new CType.Named(AlignmentTypeName) };
        }
        if (container == AlignmentTypeName && name == "fromByteUnits" && argItems.Count == 1)
        {
            return new Call("Alignment.fromByteUnits", new List<CExpr> { LowerExprSink(argItems[0], CType.ULong) },
                new List<CType> { CType.ULong }, null) { Type = new CType.Named(AlignmentTypeName) };
        }
        var fn = EnsureMethodDeclared(container, name)
            ?? throw new IrUnsupportedException(
                $"decl literal `.{name}(…)`: '{container}' has no function '{name}'");
        return BuildCall(fn, argItems, receiver: null);
    }

    /// <summary>The container const <paramref name="name"/> of <paramref name="container"/>, lowered by the module that
    /// declares it (see <see cref="LowerDeclLiteralValue"/>), or null when there is none: std.fmt.parse_float's
    /// <c>Decimal(T).min_exponent</c>, a const of a struct another module reified.</summary>
    private CExpr? TryLowerContainerConstAnywhere(string container, string name)
    {
        if (_containerConsts.TryGetValue(container, out var consts) && consts.TryGetValue(name, out var entry))
        {
            return LowerContainerConst(container, name, entry.typeItem, entry.rhs);
        }
        return _shared.ContainerConstOwners.TryGetValue(container, out var owner) && owner != this
            ? owner.TryLowerContainerConstAnywhere(container, name)
            : null;
    }

    /// <summary>Lower a decl-literal VALUE <c>.name</c> at a sink of container type
    /// <paramref name="container"/> — the container's <c>const name</c>, re-lowered like a
    /// <c>Container.name</c> read (<see cref="LowerContainerConst"/>). A container another module declares
    /// (array_list's reified <c>Aligned(u8, null)</c> with its <c>pub const empty</c>) has its const lowered
    /// by that module, where its RHS and the instance's comptime seeds resolve, as the call form
    /// (<see cref="LowerDeclLiteralCall"/>) already routes through the shared method registry.</summary>
    private CExpr LowerDeclLiteralValue(string container, string name)
    {
        if (_containerConsts.TryGetValue(container, out var consts) && consts.TryGetValue(name, out var entry))
        {
            return LowerContainerConst(container, name, entry.typeItem, entry.rhs);
        }
        if (_shared.ContainerConstOwners.TryGetValue(container, out var owner) && owner != this)
        {
            return owner.LowerDeclLiteralValue(container, name);
        }
        throw new IrUnsupportedException($"decl literal `.{name}`: '{container}' has no `const {name}`");
    }

    /// <summary>A pointer-to-array value (<c>*[N]T</c>: <c>&amp;arr</c>, or a parameter so typed) seen as the array it
    /// points at, with that array type, or (null, null). The two share one C# representation (the element
    /// pointer), so the array is the same expression retyped; an explicit <c>&amp;</c> strips back to its operand.</summary>
    private static (CExpr? Array, CType.Array? Type) PointedArray(CExpr value)
    {
        if (value.Type.Unqualified is not CType.Pointer { Pointee.Unqualified: CType.Array pa }) { return (null, null); }
        return value is Unary { Op: UnOp.AddrOf, Operand: var addressed }
            ? (addressed, pa)
            : (value with { Type = pa }, pa);
    }

    /// <summary>True for an empty aggregate literal that spells a zero-length array: <c>.{}</c>, or a typed
    /// <c>[_]T{}</c> / <c>[0]T{}</c>.</summary>
    private bool IsEmptyArrayLiteral(Item literal) => literal.Content switch
    {
        Zig.AnonStructInitEmpty => true,
        Zig.TypedStructInitEmpty { Arg0.Content: Zig.TyArray ta } => ta.Arg1.Content switch
        {
            Zig.Ident id => Tok(id.Arg0) == "_",                       // `[_]T{}`: the extent is inferred, 0
            _ => _ir.ConstEval(LowerExpr(ta.Arg1)) is 0,               // `[0]T{}`
        },
        _ => false,
    };

    /// <summary>Lower an expression that has a known result type (a "sink"): the two
    /// result-located Zig forms — a bare enum literal <c>.member</c> and an anonymous struct
    /// literal <c>.{…}</c> — need that type to resolve, so they're dispatched here; every
    /// other expression ignores the sink and lowers via <see cref="LowerExpr"/> (the backend
    /// still coerces at the store). Used at each typed sink: a typed decl init, a
    /// <c>return</c>, an assignment target, a switch case value, a struct-literal field.</summary>
    private CExpr LowerExprSink(Item expr, CType? sink)
    {
        switch (expr.Content)
        {
            // A lazy module's UNTYPED top-level const read at a sink (`alignment: Alignment = default_alignment`
            // with `const default_alignment = .right;`): the reader's type is the const's.
            case Zig.Ident lid when sink is not null && _lazy && _symbols.Resolve(Tok(lid.Arg0)) is null
                                    && LowerLazyValueConst(Tok(lid.Arg0), sink) is { } lazyAtSink:
                return lazyAtSink;
            // `.left` at a `?Alignment` sink (std.fmt.Placeholder.parse): the literal is the payload's, which
            // the optional then wraps.
            case Zig.EnumLit when sink?.Unqualified is CType.Optional { Inner: var optPayload }:
                return LowerExprSink(expr, optPayload);
            // A bare `.variant` at a tagged-union sink constructs its VOID variant (set the tag).
            case Zig.EnumLit el when sink?.Unqualified is CType.Named n && _unions.TryGetValue(n.Name, out var uinfo):
                return BuildVoidVariant(uinfo, Tok(el.Arg1));
            case Zig.EnumLit el when sink?.Unqualified is CType.Enum en:  // '.' IDENT
                return ResolveEnumLit(Tok(el.Arg1), en);
            // `.empty` at a curated `std.ArrayList(T)` sink (wall-plan W0) — zig's decl literal
            // for the empty unmanaged list is exactly `default`: a null pointer with zero
            // length/capacity (the first growing call allocates). Any other decl literal on a
            // list sink is an unmodeled member — clear error, never a silent zero.
            case Zig.EnumLit el when sink?.Unqualified is CType.ZigList:
                return Tok(el.Arg1) == "empty"
                    ? new DefaultLit { Type = sink }
                    : throw new IrUnsupportedException(
                        $"zig std.ArrayList has no modeled decl literal `.{Tok(el.Arg1)}` (only `.empty`)");
            // A DECL LITERAL (zig 0.14+) at a struct/union sink: `.fixed(buf)` IS `T.fixed(buf)`, and a
            // bare `.origin` IS `T.origin` — the member is looked up on the RESULT type, like an enum
            // literal's tag (road-to-zig-std G3 — `var w: Writer = .fixed(buf);` in `std.fmt.bufPrint`).
            case Zig.CallArgs { Arg0.Content: Zig.EnumLit dcl } dca when DeclLiteralContainer(sink) is { } dcc:
                return LowerDeclLiteralCall(dcc, Tok(dcl.Arg1), Flatten(dca.Arg2));
            case Zig.CallNoArgs { Arg0.Content: Zig.EnumLit dcl } when DeclLiteralContainer(sink) is { } dcc:
                return LowerDeclLiteralCall(dcc, Tok(dcl.Arg1), []);
            case Zig.EnumLit dvl when DeclLiteralContainer(sink) is { } dvc:
                return LowerDeclLiteralValue(dvc, Tok(dvl.Arg1));
            // `.{ 1, 5, 9, 5 }` at a SIMD-vector sink: one lane per element (T5).
            case Zig.AnonStructInit vecInit when sink?.Unqualified is CType.Vector vecSink:
                return LowerVectorLiteral(Flatten(vecInit.Arg2), vecSink);
            case Zig.AnonStructInit:
            case Zig.AnonStructInitEmpty:
                return LowerStructInit(expr, sink);
            // A `@builtin(...)` at a typed sink — the result-location cast builtins
            // (`@intCast`/`@ptrCast`/…) infer their target from `sink`. Routed through the
            // shared lowering WITH the sink (vs LowerExpr's sink-free call).
            case Zig.BuiltinCall b:
            {
                var builtinValue = LowerBuiltinCall(b, sink);
                // `@as(*const [1]u8, &c)` at a `[]const u8` parameter (std.Io.Writer.printAsciiChar): a pointer to an
                // array coerces to a slice of it, as the ordinary path below does. So does an array value
                // (`@field(args, "0")` of a tuple holding a string literal, whose element type drops the NUL).
                if (sink?.Unqualified is CType.Slice builtinSlice
                    && (builtinValue.Type?.Unqualified is CType.Array
                        || builtinValue.Type?.Unqualified is CType.Pointer && PointedArray(builtinValue) is ({ }, _)))
                {
                    return CoerceToSlice(builtinValue, builtinSlice);
                }
                return WidenToOptionalPayload(builtinValue, sink) ?? builtinValue;
            }
            // A switch EXPRESSION at a typed sink (`const x: T = switch (y) { … }`) — each arm's
            // value lowers at `sink`, so a result-located arm (`.member` / `.{…}` / a cast) resolves.
            // An if-EXPRESSION carries the sink into both arms, so `if (c) .a else .b` resolves each enum
            // literal against the result type; a comptime condition selects one arm, as LowerExpr's does.
            case Zig.IfExpr ie when sink is not null:
            {
                if (TryFoldComptimeCondition(ie.Arg2) is { } taken) { return LowerExprSink(taken ? ie.Arg4 : ie.Arg6, sink); }
                var then = LowerExprSink(ie.Arg4, sink);
                return new CondExpr(LowerExpr(ie.Arg2), then, LowerExprSink(ie.Arg6, sink)) { Type = then.Type };
            }
            case Zig.SwitchExpr s:         return LowerSwitchExpr(s.Arg2, s.Arg5, sink);
            case Zig.SwitchExprTrailing s: return LowerSwitchExpr(s.Arg2, s.Arg5, sink);
            case Zig.IfExprReturnThen ir when sink is not null:
                return LowerIfReturnThen(ir.Arg2, ir.Arg5, ir.Arg7, sink);
            // `comptime switch` / `comptime if` in value position: the inner form, whose comptime-known
            // subject already selects one arm at lowering time.
            case Zig.ComptimeSwitchExpr c: return LowerExprSink(c.Arg1, sink);
            case Zig.ComptimeIfExpr c:     return LowerExprSink(c.Arg1, sink);
            // A value-position capture `if` with a result type (`const a: ?Alignment = if (x) |b| … else null;`).
            case Zig.IfExprCapture ec when sink is not null:
                return LowerIfCaptureExpr(ec.Arg2, Tok(ec.Arg5), ec.Arg7, ec.Arg9, sink);
            case Zig.IfExprCaptureErr ee when sink is not null:
                return LowerIfCaptureExpr(ee.Arg2, Tok(ee.Arg5), ee.Arg7, ee.Arg12, sink, Tok(ee.Arg10));
            // `comptime e` keeps its result location (hash_map's `const max_align: Alignment = comptime
            // .fromByteUnits(…);`, a decl literal that needs the sink to resolve), then folds as LowerExpr's does.
            case Zig.PreComptime pc when sink is not null:
            {
                var inner = LowerExprSink(pc.Arg1, sink);
                // Only a SCALAR folds through the interpreter; an aggregate or curated value (the runtime
                // `Alignment` carrier) is a pure expression that stays as it is.
                if (inner.Type.Unqualified is not CType.Prim) { return inner; }
                var fold = new ComptimeFold(inner) { Type = inner.Type };
                _pendingComptimeFolds.Add(fold);
                return fold;
            }
            // `&.{}` / `&[_]T{}` at a slice sink is the EMPTY slice (array_list's `pub const empty: Self =
            // .{ .items = &.{}, .capacity = 0 }`, AlignedManaged's `.items = &[_]T{}`): the address of a
            // zero-length array, a null pointer with length 0.
            case Zig.PreAddrOf emptyAddr when sink?.Unqualified is CType.Slice emptySlice && IsEmptyArrayLiteral(emptyAddr.Arg1):
                return new SliceNew(new NullPtr { Type = new CType.Pointer(emptySlice.Element.Unqualified) },
                    new LitInt("0", 0) { Type = CType.ULong }, emptySlice.Element.Unqualified, emptySlice.Element.IsConst)
                { Type = emptySlice };
            // `&.{ .avx2, .sse2 }` at a `[]const Feature` sink (std.Target.x86.featureSet's argument): an array
            // literal of the slice's element type, so each element is result-located, viewed as the slice.
            case Zig.PreAddrOf listAddr when sink?.Unqualified is CType.Slice listSlice
                                          && listAddr.Arg1.Content is Zig.AnonStructInit:
                return CoerceToSlice(LowerExprSink(listAddr.Arg1, new CType.Array(listSlice.Element, null)), listSlice);
            // `&.{ … }` at a `*const S` sink (std.Io.Writer.fixed's `.vtable = &.{ .drain = fixedDrain, … }`):
            // the literal is result-located at `S`, and a comptime-known one lives in static storage.
            case Zig.PreAddrOf pa when pa.Arg1.Content is Zig.AnonStructInit
                                    && sink?.Unqualified is CType.Pointer { Pointee: var pointee }
                                    && pointee.Unqualified is CType.Named:
                return LowerAddressOfStructLiteral(pa.Arg1, pointee);
            // `var x: T = undefined;` (scalar) → `default(T)` (Zig's uninitialized; a zeroed
            // over-approximation). An array sink is handled earlier in DeclOf (stackalloc).
            case Zig.UndefinedLit:
                return new DefaultLit { Type = sink ?? CType.Int };
            default:
            {
                var lowered = LowerExpr(expr);
                // An array, a pointer to one, or a slice at a SIMD-vector sink is loaded (T5).
                if (sink?.Unqualified is CType.Vector vectorSink && TryCoerceToVector(lowered, vectorSink) is { } loaded)
                {
                    return loaded;
                }
                // A slice at a `*[N]T` / `*const [N]T` sink (std.hash.Wyhash's `self.round(input[i..][0..48])`, std.mem.readInt's
                // `data[0..4]`): zig coerces a comptime-length slice to a pointer to its array, which is its data pointer.
                if (sink?.Unqualified is CType.Pointer { Pointee: var arrayPointee } arrayPtrSink
                    && arrayPointee.Unqualified is CType.Array && lowered.Type?.Unqualified is CType.Slice)
                {
                    return new Member(lowered, "Ptr", false) { Type = arrayPtrSink };
                }
                // Array / string-literal → slice coercion at a `[]T` / `[]const T` sink (Zig's
                // implicit `*[N]T` → `[]T` and string-literal `*const [N:0]u8` → `[]const u8`).
                // A value already of slice type passes through (e.g. forwarding a `[]const u8`).
                if (sink?.Unqualified is CType.Slice slc && lowered.Type?.Unqualified is not CType.Slice)
                {
                    return CoerceToSlice(lowered, slc);
                }
                if (WidenToOptionalPayload(lowered, sink) is { } widened) { return widened; }
                // An untyped float literal (zig's comptime_float) takes the float type it lands at, so an `f32` one renders
                // as a C# float literal, even where no store coercion follows (a stackalloc'd `[_]f32{ 1e0, … }`).
                if (sink?.Unqualified == CType.Float && lowered is LitFloat { } floatLit && floatLit.Type?.Unqualified != CType.Float)
                {
                    return floatLit with { Type = CType.Float };
                }
                // A plain value at an ERROR-UNION sink (`fn unwrap(v: anyerror!u8)` called as `unwrap(5)`) is its
                // success variant, as zig coerces it; an error union or an error code passes as it is.
                if (sink?.Unqualified is CType.ErrorUnion okSink && lowered.Type?.Unqualified is not (CType.ErrorUnion or CType.ErrorSetType))
                {
                    // An array at a `![]T` sink is a slice first (std.mem.join's `… else &[0]u8{}`).
                    var okValue = okSink.Payload.Unqualified is CType.Slice okSlice
                                  && (lowered.Type?.Unqualified is CType.Array || PointedArray(lowered) is ({ }, _))
                        ? CoerceToSlice(lowered, okSlice)
                        : lowered;
                    return new ErrUnionOk(okValue) { Type = okSink };
                }
                // `*[N]T` → `[*]T` at a many-pointer sink (`.marks = &self.marks` in hash_map's
                // FieldIterator): the address of an array is its first element's, which is what the array
                // itself already renders as (a local's element pointer, a field's fixed buffer or inline-array
                // element pointer), C's array decay. `&arr` would instead be a pointer to that pointer.
                if (sink?.Unqualified is CType.Pointer { Pointee: var sinkElem }
                    && PointedArray(lowered) is ({ } arrOperand, { Element: var arrElem })
                    && arrElem.Unqualified.Equals(sinkElem.Unqualified))
                {
                    return arrOperand;
                }
                return lowered;
            }
        }
    }

    /// <summary>An integer of another width at an optional integer sink (std.bit_set's <c>return @ctz(mask);</c> in a
    /// <c>?usize</c> method), cast to the payload type, since C# has no implicit <c>int</c> → <c>ulong?</c>; null for
    /// anything else.</summary>
    private static CExpr? WidenToOptionalPayload(CExpr value, CType? sink)
        => sink?.Unqualified is CType.Optional { Inner: var optInner }
           && optInner.Unqualified is CType.Prim { Integer: true } optPrim
           && value.Type?.Unqualified is CType.Prim { Integer: true } valuePrim && !valuePrim.Equals(optPrim)
            ? new Cast(optInner, value) { Type = optInner }
            : null;

    /// <summary>Locals and globals bound, without an annotation, to a string literal (<c>const s = "abc";</c>)
    /// — zig types such a binding <c>*const [3:0]u8</c>, whose <c>.len</c> is 3, but the lowered symbol
    /// carries the literal's C array type <c>char[4]</c> (the NUL counted). Recorded so
    /// <see cref="IsStringLiteralValue"/> answers for a reference to one exactly as for the literal itself.
    /// Keyed by symbol identity.</summary>
    private readonly HashSet<Symbol> _stringLiteralSyms = new();

    /// <summary>True when <paramref name="value"/> is a zig string literal (<c>*const [N:0]u8</c>) — the
    /// literal itself, a comptime string substituted for a name, or a reference to a binding of one —
    /// whose lowered array type counts the NUL sentinel that zig's <c>.len</c> excludes.</summary>
    private bool IsStringLiteralValue(CExpr value)
        => value is LitStr || value is VarRef v && _stringLiteralSyms.Contains(v.Sym);

    /// <summary>Coerce an array or string-literal value into a slice fat pointer at a
    /// <c>[]T</c> / <c>[]const T</c> sink (Zig's array→slice coercion). A string literal is
    /// <c>*const [N:0]u8</c> — its <c>.len</c> excludes the sentinel NUL, so the count is the
    /// <see cref="LitStr"/>'s byte length (which INCLUDES the NUL) minus one; a plain array
    /// <c>[N]T</c> keeps its full element count.</summary>
    private CExpr CoerceToSlice(CExpr value, CType.Slice sliceType)
    {
        // Zig's `*[N]T` → `[]T`: `&arr` (address-of an array) coerces to a slice. Strip the
        // address-of to recover the array lvalue — a Zig array already lowers to its element pointer
        // (a `T*` in emitted C#), which is exactly the pointer `SliceNew` wants, and its element
        // count comes from the array type. (A bare `*[N]T` pointer VALUE that isn't a literal `&arr`
        // is rarer; it falls through to the array check below and reports a clear coercion error.)
        if (PointedArray(value) is ({ } arr, _)) { value = arr; }
        // `&copy` of a slice's array copy (see the `.*` lowering): the slice itself.
        if (value is Unary { Op: UnOp.AddrOf, Operand: { Type: var copyType } copy }
            && copyType.Unqualified is CType.Slice copySlice
            && copySlice.Element.Unqualified.Equals(sliceType.Element.Unqualified))
        {
            return copy;
        }
        if (value.Type.Unqualified is not CType.Array { Count: { } n })
        {
            throw new IrUnsupportedException(
                $"cannot coerce {value.Type.Describe()} to slice {sliceType.Describe()} (need an array or string literal)");
        }
        long count = IsStringLiteralValue(value) ? n - 1 : n;   // string literal drops the trailing NUL
        var lenLit = new LitInt(count.ToString(CultureInfo.InvariantCulture), count) { Type = CType.ULong };
        var elem = sliceType.Element;
        return new SliceNew(value, lenLit, elem.Unqualified, elem.IsConst) { Type = sliceType };
    }

    /// <summary>Lower a curated <c>std.mem.&lt;name&gt;(…)</c> call (the byte-blit / compare cluster).
    /// <c>eql(T, a, b)</c> and <c>copyForwards(T, dest, source)</c> take an explicit element type as
    /// the first argument; the slice arguments coerce at a <c>[]T</c> / <c>[]const T</c> sink (so a
    /// <c>&amp;array</c> promotes to a slice via <see cref="CoerceToSlice"/>). Both lower to a
    /// <see cref="ZigMemCall"/> rendered as <c>ZigMem.{Method}&lt;T&gt;(…)</c>. An unmodeled member is
    /// a clear error — dotcc models no general <c>std</c>.</summary>
    private CExpr LowerStdMemCall(string methodName, IReadOnlyList<Item> argItems)
    {
        switch (methodName)
        {
            case "eql":
                if (argItems.Count != 3)
                {
                    throw new IrUnsupportedException($"zig `std.mem.eql` expects (type, a, b); got {argItems.Count} argument(s)");
                }
                var eqElem = LowerType(argItems[0]).Unqualified;
                var eqSink = new CType.Slice(eqElem.WithQuals(TypeQual.Const));
                var eqA = LowerExprSink(argItems[1], eqSink);
                var eqB = LowerExprSink(argItems[2], eqSink);
                return new ZigMemCall("Eql", eqElem, new List<CExpr> { eqA, eqB }) { Type = CType.Bool };
            case "copyForwards":
                if (argItems.Count != 3)
                {
                    throw new IrUnsupportedException($"zig `std.mem.copyForwards` expects (type, dest, source); got {argItems.Count} argument(s)");
                }
                var cpElem = LowerType(argItems[0]).Unqualified;
                var cpDest = LowerExprSink(argItems[1], new CType.Slice(cpElem));
                var cpSrc = LowerExprSink(argItems[2], new CType.Slice(cpElem.WithQuals(TypeQual.Const)));
                return new ZigMemCall("CopyForwards", cpElem, new List<CExpr> { cpDest, cpSrc }) { Type = CType.Void };
            case "span":
                // std.mem.span(ptr) — a NUL-sentinel pointer `[*:0]T` → the `[]const T` slice before
                // the sentinel (dotcc's V1 sentinel = 0, erased in the type; the common `[*:0]const u8`
                // C-string case). The element comes from the pointer's pointee.
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `std.mem.span` expects (pointer); got {argItems.Count} argument(s)");
                }
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
            case "zeroes":
                // std.mem.zeroes(T) — an all-zero value of T → C#'s `default(T)` (zero-fills a scalar
                // or a struct uniformly). An ARRAY/slice type is a documented cut (arrays lower to a
                // pointer, so `default` would be a null pointer, not a zeroed array).
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `std.mem.zeroes` expects (type); got {argItems.Count} argument(s)");
                }
                var zt = LowerType(argItems[0]);
                if (zt.Unqualified is CType.Array or CType.Slice)
                {
                    throw new IrUnsupportedException(
                        "zig `std.mem.zeroes` of an array/slice type is not modeled yet (scalar and struct types are supported)");
                }
                return new DefaultLit { Type = zt };
            case "asBytes":
            {
                // std.mem.asBytes(ptr) — the bytes of the single item `ptr` points at (Wyhash's input in
                // std.hash_map's auto-hash). zig types it `*[@sizeOf(T)]u8`; dotcc gives the byte SLICE over
                // the same memory, which is what every consumer coerces it to (`[]const u8`), since a pointer
                // to an array would render as a pointer to the element pointer. Curated because the source
                // (`AsBytesReturnType` / `CopyPtrAttrs`) builds a pointer type from comptime attribute
                // structs and the pointer size class, which dotcc does not model.
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `std.mem.asBytes` expects (ptr); got {argItems.Count} argument(s)");
                }
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
            case "sliceAsBytes":
            {
                // std.mem.sliceAsBytes(slice) — the same memory as a byte slice, `len * @sizeOf(T)` long (std.mem.indexOf /
                // eql's haystack and needle). Curated for the same reason as asBytes: its source return type is built
                // by `CopyPtrAttrs` through `@Pointer(size, attrs, child, null)`, which dotcc does not model.
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `std.mem.sliceAsBytes` expects (slice); got {argItems.Count} argument(s)");
                }
                var sbSlice = LowerExpr(argItems[0]);
                if (sbSlice.Type.Unqualified is not CType.Slice { Element: var sbElemType })
                {
                    throw new IrUnsupportedException($"zig `std.mem.sliceAsBytes` expects a slice, got {sbSlice.Type.Describe()}");
                }
                var sbByte = sbElemType.IsConst ? CType.UChar.WithQuals(TypeQual.Const) : CType.UChar;
                var sbPtr = new Cast(new CType.Pointer(sbByte), new Member(sbSlice, "Ptr", false) { Type = new CType.Pointer(sbElemType) })
                {
                    Type = new CType.Pointer(sbByte),
                };
                CExpr sbLen = new Member(sbSlice, "Len", false) { Type = CType.ULong };
                if (sbElemType.Unqualified.SizeOf != 1)
                {
                    sbLen = new Binary(BinOp.Mul, sbLen, new SizeOfExpr(sbElemType.Unqualified) { Type = CType.ULong }) { Type = CType.ULong };
                }
                return new SliceNew(sbPtr, sbLen, CType.UChar, sbByte.IsConst) { Type = new CType.Slice(sbByte) };
            }
            default:
                throw new IrUnsupportedException(
                    $"zig `std.mem.{methodName}` is not modeled yet (supported: eql, copyForwards, span, zeroes, asBytes, sliceAsBytes)");
        }
    }

    /// <summary>Lower a <c>@memcpy</c>/<c>@memset</c> slice operand, inferring the element type from
    /// the operand itself (a <c>[]T</c> slice, a <c>[N]T</c> array, or <c>&amp;array</c> = <c>*[N]T</c>)
    /// rather than an explicit type argument. Returns the coerced slice expression and reports the
    /// (unqualified) element type via <paramref name="element"/>. When <paramref name="wantConst"/>
    /// the target slice is <c>[]const T</c> (a read source); otherwise <c>[]T</c> (a write dest).</summary>
    private CExpr LowerMemSlice(Item item, bool wantConst, out CType element)
    {
        var lowered = LowerExpr(item);
        element = SliceElementOf(lowered).Unqualified;
        if (lowered.Type.Unqualified is CType.Slice) { return lowered; }
        var elemQ = wantConst ? element.WithQuals(TypeQual.Const) : element;
        return CoerceToSlice(lowered, new CType.Slice(elemQ));
    }

    /// <summary>The element type of a lowered slice-like operand — a <c>[]T</c> slice, a <c>[N]T</c>
    /// array, or a pointer-to-array (<c>&amp;array</c> = <c>*[N]T</c>) — used to infer <c>@memcpy</c>/
    /// <c>@memset</c>'s element type from its dest without an explicit type argument.</summary>
    private static CType SliceElementOf(CExpr e) => e.Type.Unqualified switch
    {
        CType.Slice s => s.Element,
        CType.Array a => a.Element,
        CType.Pointer { Pointee.Unqualified: CType.Array pa } => pa.Element,
        _ => throw new IrUnsupportedException(
            $"expected a slice, array, or `&array` operand, got {e.Type.Describe()}"),
    };

    /// <summary>A length literal (<c>usize</c>).</summary>
    private static LitInt ZigLen(int n) => new(n.ToString(CultureInfo.InvariantCulture), n) { Type = CType.ULong };

    /// <summary>Lower a slice expression <c>base[lo..hi]</c> to a fat-pointer
    /// <see cref="SliceNew"/> <c>{ base.ptr + lo, hi - lo }</c>. When <paramref name="hi"/> is
    /// null the slice is open-ended (<c>base[lo..]</c>) and the high bound is the source length:
    /// a slice's <c>.Len</c> or an array's element count. The base may be a slice (re-slice
    /// through <c>.Ptr</c>), a bare pointer (no length — open-ended is rejected, as Zig does),
    /// or an array (decays to its element pointer); the element type + const-ness ride into the
    /// resulting <c>[]T</c> / <c>[]const T</c>.</summary>
    private CExpr BuildSlice(CExpr baseExpr, CExpr lo, CExpr? hi)
    {
        CExpr basePtr;
        CType element;
        CExpr? sourceLen;   // the known source length, used for an open-ended high bound
        // A pointer to an ARRAY (`*[N]T`, what `&arr` is and what zig's `ptr[0..48]` produces) slices like the
        // array: its length is N. `&arr` strips back to the array itself; any other such pointer is dereferenced.
        if (PointedArray(baseExpr) is ({ } pointedArray, _)) { baseExpr = pointedArray; }
        switch (baseExpr.Type.Unqualified)
        {
            case CType.Slice s:
                basePtr = new Member(baseExpr, "Ptr", false) { Type = new CType.Pointer(s.Element) };
                element = s.Element;
                sourceLen = new Member(baseExpr, "Len", false) { Type = CType.ULong };
                break;
            case CType.Pointer p:
                basePtr = baseExpr;
                element = p.Pointee;
                sourceLen = null;   // a bare pointer carries no length
                break;
            case CType.Array a:
                basePtr = baseExpr;   // decays to its element pointer
                element = a.Element;
                // A string literal's C array counts its NUL; zig's `"abc".len` (and so `s[1..]`'s end) does not.
                sourceLen = a.Count is int n
                    ? ZigLen(IsStringLiteralValue(baseExpr) ? n - 1 : n)
                    : null;
                break;
            default:
                throw new IrUnsupportedException($"cannot slice a {baseExpr.Type.Describe()} (need a slice, pointer, or array)");
        }
        var ptr = new Binary(BinOp.Add, basePtr, lo) { Type = new CType.Pointer(element) };
        CExpr len;
        if (hi is not null)
        {
            // len = (ulong)(hi - lo). The explicit cast covers non-constant bounds, where
            // a signed `int` difference has no implicit conversion to the ctor's `ulong`.
            var diff = new Binary(BinOp.Sub, hi, lo) { Type = hi.Type };
            len = new Cast(CType.ULong, diff) { Type = CType.ULong };
        }
        else
        {
            if (sourceLen is null)
            {
                throw new IrUnsupportedException(
                    "open-ended slice `[lo..]` needs a known length (slice or array); a bare pointer has none");
            }
            // len = sourceLen - (ulong)lo. sourceLen is already ulong; cast lo to match (a
            // signed `int` index has no implicit conversion to ulong).
            var loU = new Cast(CType.ULong, lo) { Type = CType.ULong };
            len = new Binary(BinOp.Sub, sourceLen, loU) { Type = CType.ULong };
        }
        return new SliceNew(ptr, len, element.Unqualified, element.IsConst) { Type = new CType.Slice(element) };
    }

    /// <summary>Lower a <c>@builtin(...)</c> call. Several builtins are RESULT-LOCATION-typed —
    /// Zig infers their target from the sink, not an explicit type argument: <c>@intCast</c>,
    /// <c>@truncate</c>, <c>@ptrCast</c>, <c>@bitCast</c>, <c>@floatFromInt</c>,
    /// <c>@intFromFloat</c>, <c>@floatCast</c>, <c>@enumFromInt</c>. Those are valid only at a
    /// typed sink (a typed binding, <c>return</c>, assignment, call argument, or nested inside
    /// <c>@as(T, …)</c>), so without one (<paramref name="sink"/> null) they're a clear error.
    /// <c>@as</c>/<c>@intFromEnum</c>/<c>@sizeOf</c>/<c>@alignCast</c> carry or need no sink and
    /// lower the same way from either path. Called from <see cref="LowerExpr"/> (sink null) and
    /// <see cref="LowerExprSink"/> (sink set).</summary>
    private CExpr LowerBuiltinCall(Zig.BuiltinCall b, CType? sink)
    {
        var bname = Tok(b.Arg0);
        var bargs = Flatten(b.Arg2);
        switch (bname)
        {
            case "@as":
                // `@as(T, expr)` — the explicit-type cast → the C Cast IR. The type arg becomes
                // the value's sink, so a nested result-location builtin (`@as(u8, @intCast(x))`)
                // and a bare enum/struct literal both resolve.
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@as` expects (type, value); got {bargs.Count} argument(s)");
                }
                // `@as(comptime_int, fmt.len)` (std.Io.Writer.print's eval-quota upcast): an UNTYPED comptime
                // number has no C# type to cast to, and the value itself is the answer.
                if (IsComptimeNumberTypeName(bargs[0])) { return LowerExpr(bargs[1]); }
                var asTarget = LowerType(bargs[0]);
                return new Cast(asTarget, LowerExprSink(bargs[1], asTarget)) { Type = asTarget };
            case "@intFromEnum":
                // `@intFromEnum(e)` — the enum's integer value → decay to the underlying type
                // (the same Cast the backend uses for C's enum→int).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@intFromEnum` expects (enum); got {bargs.Count} argument(s)");
                }
                var enumOperand = LowerExpr(bargs[0]);
                if (enumOperand.Type.Unqualified is not CType.Enum en)
                {
                    throw new IrUnsupportedException("zig `@intFromEnum` expects an enum operand");
                }
                return new Cast(en.Underlying, enumOperand) { Type = en.Underlying };
            case "@intFromBool":
                // `@intFromBool(b)` — a bool → its integer value (0 or 1). Zig types the result `u1`;
                // dotcc yields an `int`-typed 0/1 (exactly like a bool comparison result), which then
                // narrows to any integer sink the same way. The operand's lowered `_Bool` (CBool)
                // carries a defined conversion to int, so the cast is valid C#. Used by `std.ascii`'s
                // `toUpper`/`toLower` (`@as(u8, @intFromBool(isLower(c))) << 5`).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@intFromBool` expects (bool); got {bargs.Count} argument(s)");
                }
                return new Cast(CType.Int, LowerExpr(bargs[0])) { Type = CType.Int };
            case "@sizeOf":
                // `@sizeOf(T)` — the byte size as `usize`. Reuses the C `sizeof` IR (folded for a
                // user aggregate via the layout model, else C#'s `sizeof(T)`).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@sizeOf` expects (type); got {bargs.Count} argument(s)");
                }
                return new SizeOfExpr(LowerType(bargs[0])) { Type = CType.ULong };
            case "@bitSizeOf":
                // `@bitSizeOf(T)` — the width in BITS (road-to-zig-std S7), answered from the declared
                // spelling so an arbitrary-width `u21` reports 21 and not the 32 it widened to.
                return BitSizeOfBuiltin(bargs);
            case "@compileError":
                // Reaching here means the branch survived comptime folding — which is exactly when zig
                // raises it ("when semantically analyzed"). See ZigLowering.Reify.cs.
                return CompileErrorBuiltin(bargs);
            case "@compileLog":
                return CompileLogBuiltin(bargs);
            case "@setEvalBranchQuota":
                // Honored as a STATEMENT (see LowerStmt), where zig's `void` result puts it. In a value
                // position there is nothing to yield, so say so rather than invent one.
                throw new IrUnsupportedException(
                    "zig `@setEvalBranchQuota(n)` yields `void` — use it as a statement, not as a value");
            // SIMD vectors (the target-identity segment T5, ZigLowering.Vector.cs).
            case "@splat" when sink?.Unqualified is CType.Vector splatVector:
                return LowerSplat(bargs, splatVector);
            case "@reduce":
                return LowerReduce(bargs);
            case "@select":
                return LowerSelect(bargs, sink);
            case "@Int" or "@Struct" or "@Union" or "@Enum" or "@Pointer" or "@Fn" or "@Tuple" or "@Vector":
                // The reification family builds a TYPE. In a value position the useful thing to say is
                // which position it belongs in; the family's own cuts live in TryLowerReifyBuiltin.
                throw new IrUnsupportedException(
                    $"zig `{bname}(…)` constructs a TYPE — use it in a type position (`const T = {bname}(…);`, a "
                    + $"parameter / return annotation, or `@as({bname}(…), x)`), not as a value");
            case "@alignOf":
            {
                // `@alignOf(T)` — the ABI alignment as `usize` (Milestone T, part 4). Always a
                // compile-time constant on this LP64 target (the layout model computes it), so it
                // folds straight to a literal — no IR node, and it participates in comptime arithmetic
                // (a literal already folds) and renders directly at a runtime use site.
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@alignOf` expects (type); got {bargs.Count} argument(s)");
                }
                var align = _ir.AlignOfConst(LowerType(bargs[0]));
                return new LitInt(align.ToString(System.Globalization.CultureInfo.InvariantCulture), align) { Type = CType.ULong };
            }
            case "@fieldParentPtr":
            {
                // `const a: *Allocating = @fieldParentPtr("writer", w);` (std.Io.Writer.Allocating's vtable callbacks): the
                // parent's address is the field pointer minus the field's offset in the parent type the result names.
                if (bargs.Count != 2 || bargs[0].Content is not Zig.StrLit parentFieldLit)
                {
                    throw new IrUnsupportedException("zig `@fieldParentPtr` expects (\"field\", field_ptr)");
                }
                if (sink?.Unqualified is not CType.Pointer { Pointee: var parentType } parentPtr
                    || parentType.Unqualified is not CType.Named parentNamed)
                {
                    throw new IrUnsupportedException(
                        "zig `@fieldParentPtr` needs its result type (`const p: *Parent = @fieldParentPtr(\"field\", ptr);`)");
                }
                var parentField = UnquoteStringLiteral(Tok(parentFieldLit.Arg0));
                var fieldOffset = new OffsetOf(parentType, new[] { parentField }, _ir.StructFieldType(parentNamed, parentField)) { Type = CType.ULong };
                var fieldAddress = new Cast(CType.ULong, LowerExpr(bargs[1])) { Type = CType.ULong };
                return new Cast(parentPtr, new Binary(BinOp.Sub, fieldAddress, fieldOffset) { Type = CType.ULong }) { Type = parentPtr };
            }
            case "@offsetOf":
            {
                // `@offsetOf(T, "field")` — the byte offset of a field as `usize` (Milestone T,
                // part 4). Reuses the C `offsetof` IR (`OffsetOf`): the comptime engine folds it via
                // the layout model (`OffsetOfConstPath`), and a runtime use renders the .NET
                // blittable-layout computation. The field name is a comptime string literal.
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@offsetOf` expects (type, field-name); got {bargs.Count} argument(s)");
                }
                var offStruct = LowerType(bargs[0]);
                if (offStruct.Unqualified is not CType.Named offNamed)
                {
                    throw new IrUnsupportedException("zig `@offsetOf` expects a struct/union type as the first argument");
                }
                if (bargs[1].Content is not Zig.StrLit offFieldLit)
                {
                    throw new IrUnsupportedException("zig `@offsetOf` field name must be a string literal");
                }
                var offField = UnquoteStringLiteral(Tok(offFieldLit.Arg0));
                var offMemberType = _ir.StructFieldType(offNamed, offField);
                return new OffsetOf(offStruct, new[] { offField }, offMemberType) { Type = CType.ULong };
            }
            case "@typeName":
            {
                // `@typeName(T)` — the type's name as a comptime `[:0]const u8` string (road-to-zig-std
                // S5, the reflection brick's first slice). Read the SOURCE spelling off the type AST so
                // it matches zig byte-for-byte (`@typeName([]const u8)` == "[]const u8"); V1 covers
                // primitives + slice/pointer/optional compositions of them. A user type resolves to
                // zig's FILE-QUALIFIED name (`main.Foo`), which dotcc can't reproduce, so it (and an
                // alias, which zig resolves to the underlying name) is a loud cut.
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@typeName` expects (type); got {bargs.Count} argument(s)");
                }
                if (ZigTypeSpelling(bargs[0]) is not { } spelling)
                {
                    throw new IrUnsupportedException(
                        "zig `@typeName` V1 supports a primitive type or a slice/pointer/optional of one; a user type's "
                        + "fully-qualified name (zig's `file.Name`) and an alias are not modeled yet (road-to-zig-std S5 reflection)");
                }
                return ZigStringLiteral(spelling);
            }
            // Math builtins (road-to-zig-std B3) → `ZigMath.<helper><T>` over the peer-resolved operand
            // type. @min/@max/@rem/@divTrunc are ordinary; @mod/@divFloor follow the divisor's sign /
            // round toward -inf (unlike C#'s truncating %//). Zig's @min/@max are variadic — V1 binary.
            case "@min":      return MathBin2("Min", bname, bargs);
            case "@max":      return MathBin2("Max", bname, bargs);
            case "@rem":      return MathBin2("Rem", bname, bargs);
            case "@divTrunc": return MathBin2("DivTrunc", bname, bargs);
            // `@divExact(a, b)` asserts the division is exact (safety-checked in debug builds only), so its
            // value is the truncating quotient; dotcc reports ReleaseFast and does not trap.
            case "@divExact": return MathBin2("DivTrunc", bname, bargs);
            case "@mod":      return MathBin2("Mod", bname, bargs);
            case "@divFloor": return MathBin2("DivFloor", bname, bargs);
            // Overflow-detecting arithmetic (road-to-zig-std B3) → `ZigMath.<helper><T>` returning
            // Zig's `struct { T, u1 }` as a C# `(T, byte)` ValueTuple — a `CType.Tuple([T, u8])`, so the
            // result destructures (`const r, const o = @addWithOverflow(a, b);`) or indexes (`r[0]`/`r[1]`)
            // through the existing tuple machinery. add/sub/mul are peer-typed; shl's 2nd arg is a shift count.
            case "@addWithOverflow": return OverflowBin("AddWithOverflow", bname, bargs);
            case "@subWithOverflow": return OverflowBin("SubWithOverflow", bname, bargs);
            case "@mulWithOverflow": return OverflowBin("MulWithOverflow", bname, bargs);
            case "@shlWithOverflow": return OverflowShl(bname, bargs);
            case "@popCount":
                // `@popCount(x)` — set-bit count (width-agnostic; leading zeros add nothing). → int.
                return FoldBitCount("PopCount", BitCountArg("@popCount", bargs));
            case "@clz":
                // `@clz(x)` — leading-zero count within x's bit width (exact for the standard widths
                // dotcc maps 1:1; an arbitrary `uN` counts in its containing width — a documented edge).
                return FoldBitCount("Clz", BitCountArg("@clz", bargs));
            case "@ctz":
                // `@ctz(x)` — trailing-zero count within x's bit width.
                return FoldBitCount("Ctz", BitCountArg("@ctz", bargs));
            case "@intFromPtr":
                // `@intFromPtr(p)` — the pointer's address as `usize` → an unchecked cast to `ulong`
                // (the LP64 pointer-width). The VALUE is a runtime address (nondeterministic), so a
                // program observing it must derive something stable (a pointer difference, an alignment
                // remainder, a null check) — exactly as in real zig.
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@intFromPtr` expects (pointer); got {bargs.Count} argument(s)");
                }
                return new Cast(CType.ULong, LowerExpr(bargs[0])) { Type = CType.ULong };
            case "@byteSwap":
                // `@byteSwap(x)` — reverse byte order → ZigMath.ByteSwap<T> (same type). Exact for the
                // whole-byte standard widths dotcc maps 1:1 (an odd `u24`-style width is a documented edge).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@byteSwap` expects (integer); got {bargs.Count} argument(s)");
                }
                var bswArg = LowerExpr(bargs[0]);
                return new Call("ZigMath.ByteSwap", new List<CExpr> { bswArg }) { Type = bswArg.Type };
            case "@abs":
            {
                // `@abs(x)` — magnitude. Zig's `@abs(iN)` returns the UNSIGNED peer `uN` (so
                // `@abs(i8 -128)` = `u8 128`, which a same-width signed abs would overflow): compute the
                // magnitude in 128-bit (ZigMath.Abs128) and cast to the operand's unsigned peer. An
                // already-unsigned operand is the identity; a float operand is a loud cut (V1).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@abs` expects (number); got {bargs.Count} argument(s)");
                }
                var absArg = LowerExpr(bargs[0]);
                if (absArg.Type.Unqualified is not CType.Prim { Integer: true } absPrim)
                {
                    throw new IrUnsupportedException("zig `@abs` V1 supports an integer operand (float `@abs` is not lowered yet)");
                }
                if (!absPrim.Signed) { return absArg; }   // @abs of an unsigned int is the identity
                var absU = UnsignedPeerInt(absArg.Type);
                return new Cast(absU, new Call("ZigMath.Abs128", new List<CExpr> { absArg }) { Type = CType.UInt128 }) { Type = absU };
            }
            case "@errorName":
                // `@errorName(e)` → the error's name as `[]const u8` (real zig: `[:0]const u8`).
                // The operand is a flat `ushort` error code; the name comes from the runtime
                // `__zigErrorName(code)` code→name table the backend emits from `ir.ZigErrorCodes`
                // (Milestone X, part 1). Returns a `ConstSlice<byte>` over the RVA-pinned name bytes.
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@errorName` expects (error); got {bargs.Count} argument(s)");
                }
                return new Call("__zigErrorName", new List<CExpr> { LowerExpr(bargs[0]) },
                                new List<CType> { CType.ErrorSet }, null)
                {
                    Type = new CType.Slice(CType.UChar.WithQuals(TypeQual.Const)),
                };
            case "@alignCast":
                // `@alignCast(p)` only raises the pointee's alignment requirement — unobservable
                // in dotcc's managed model — so it's the IDENTITY (the enclosing `@ptrCast` / sink
                // does the real conversion). Needs no sink, and works nested in its idiomatic
                // `@ptrCast(@alignCast(p))` (where it's reached via the sink-free LowerExpr).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@alignCast` expects (value); got {bargs.Count} argument(s)");
                }
                return LowerExpr(bargs[0]);
            // `const a_bytes: []u8 = @ptrCast(a);` with `a: *T` (std.mem.swap): a single item viewed as a slice of
            // the sink's element type, `@sizeOf(T) / @sizeOf(elem)` elements long.
            case "@ptrCast" when sink?.Unqualified is CType.Slice castSlice && bargs.Count == 1
                                 && LowerExpr(bargs[0]) is { Type: CType.Pointer { Pointee: var castPointee } } castPtr
                                 && castPointee.Unqualified is not CType.VoidType:
            {
                var elem = castSlice.Element.Unqualified;
                var elemPtr = new CType.Pointer(castSlice.Element);
                // `sizeof(T) / sizeof(elem)` in the emitted C#: a struct's size is only known to the layout (CType.SizeOf
                // is 0 for a named aggregate, which made std.mem.swap swap nothing and std.hash_map crash).
                // A primitive's size is known here, so it stays a literal.
                CExpr len;
                if (castPointee.Unqualified is CType.Prim pointeePrim && elem.SizeOf > 0)
                {
                    var n = pointeePrim.SizeOf / elem.SizeOf;
                    len = new LitInt(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n) { Type = CType.ULong };
                }
                else
                {
                    len = new SizeOfExpr(castPointee.Unqualified) { Type = CType.ULong };
                    if (elem.SizeOf != 1)
                    {
                        len = new Binary(BinOp.Div, len, new SizeOfExpr(elem) { Type = CType.ULong }) { Type = CType.ULong };
                    }
                }
                return new SliceNew(new Cast(elemPtr, castPtr) { Type = elemPtr }, len, elem, castSlice.Element.IsConst) { Type = castSlice };
            }
            case "@intCast" or "@truncate" or "@ptrCast" or "@bitCast"
                or "@floatFromInt" or "@intFromFloat" or "@floatCast" or "@enumFromInt":
                return LowerResultLocationBuiltin(bname, bargs, sink);
            case "@memcpy":
                // `@memcpy(dest, source)` — copy `source.len` elements into `dest` (equal lengths in
                // Zig; a forward element copy). The element type is inferred from the dest operand.
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@memcpy` expects (dest, source); got {bargs.Count} argument(s)");
                }
                var mcDest = LowerMemSlice(bargs[0], wantConst: false, out var mcElem);
                var mcSrc = LowerMemSlice(bargs[1], wantConst: true, out _);
                return new ZigMemCall("CopyForwards", mcElem, new List<CExpr> { mcDest, mcSrc }) { Type = CType.Void };
            case "@call":
            {
                // `@call(.always_inline, Hasher.update, .{ hasher, bytes })` (std.hash.autoHash): the
                // modifier only steers zig's inliner, so this is the ordinary call `callee(args…)`.
                if (bargs.Count != 3)
                {
                    throw new IrUnsupportedException($"zig `@call` expects (modifier, function, .{{ args }}); got {bargs.Count} argument(s)");
                }
                var callArgs = bargs[2].Content switch
                {
                    Zig.AnonStructInitEmpty => new List<Item>(),
                    Zig.AnonStructInit tuple when Flatten(tuple.Arg2).All(f => f.Content is Zig.FieldInitPositional)
                        => Flatten(tuple.Arg2).Select(f => f.Content is Zig.FieldInitPositional pos ? pos.Arg0 : f).ToList(),
                    _ => throw new IrUnsupportedException("zig `@call`: the arguments must be a positional tuple literal `.{ a, b }`"),
                };
                return LowerCallItems(bargs[1], callArgs);
            }
            case "@memmove":
                // `@memmove(dest, source)` — `@memcpy` for OVERLAPPING operands (a backward copy when dest is
                // past source), array_list's in-place shift.
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@memmove` expects (dest, source); got {bargs.Count} argument(s)");
                }
                var mmDest = LowerMemSlice(bargs[0], wantConst: false, out var mmElem);
                var mmSrc = LowerMemSlice(bargs[1], wantConst: true, out _);
                return new ZigMemCall("Move", mmElem, new List<CExpr> { mmDest, mmSrc }) { Type = CType.Void };
            case "@memset":
                // `@memset(dest, value)` — set every element of `dest` to `value` (lowered at the
                // element-type sink, so a `comptime_int` like `7` becomes `(byte)7`).
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@memset` expects (dest, value); got {bargs.Count} argument(s)");
                }
                var msDest = LowerMemSlice(bargs[0], wantConst: false, out var msElem);
                var msVal = LowerExprSink(bargs[1], msElem);
                return new ZigMemCall("Set", msElem, new List<CExpr> { msDest, msVal }) { Type = CType.Void };
            case "@hasField":
            case "@hasDecl":
                // Comptime MEMBERSHIP (road-to-zig-std S5c) — folds to a boolean literal from the same
                // registries the member lists read; unlike `decl_names` it needs no declaration order.
                return TryEvalMembershipBuiltin(b)
                    ?? throw new IrUnsupportedException($"zig `{bname}`: not a membership query");
            case "@field":
                // Comptime-NAMED field access — re-enters the ordinary member path once the name folds.
                return LowerFieldBuiltin(bargs);
            case "@typeInfo":
                // Reaching here means a `@typeInfo(T)` was used where a RUNTIME value is wanted — every
                // comptime position (a field access, a `switch` subject, a `const` binding) folds it
                // before this point (see ZigLowering.TypeInfo.cs). A `std.builtin.Type` has no runtime
                // representation in dotcc, so this is the honest error rather than a synthesized value.
                throw new IrUnsupportedException(
                    "zig `@typeInfo(T)` is a comptime value with no runtime representation — use it in a comptime "
                    + "position (`switch (@typeInfo(T))`, `@typeInfo(T).int.signedness`, `const info = @typeInfo(T);`), "
                    + "not as a runtime value");
            default:
                throw new IrUnsupportedException(
                    $"zig builtin '{bname}' not lowered yet (supported: @as, @intCast, @truncate, @ptrCast, @bitCast, " +
                    "@floatFromInt, @intFromFloat, @floatCast, @enumFromInt, @alignCast, @intFromEnum, @sizeOf, @alignOf, " +
                    "@bitSizeOf, @offsetOf, @typeName, @typeInfo, @hasField, @hasDecl, @field, @Int, @compileError, " +
                    "@compileLog, @setEvalBranchQuota, @min, @max, @rem, @divTrunc, @mod, @divFloor, " +
                    "@popCount, @clz, @ctz, " +
                    "@byteSwap, @abs, @intFromPtr, @errorName, @memcpy, @memmove, @memset, @divExact, @call, @branchHint)");
        }
    }

    /// <summary>The zig SOURCE spelling of a type AST, for <c>@typeName</c> (road-to-zig-std S5) — read
    /// off the AST so it is byte-identical to real zig: a primitive keyword verbatim (<c>u8</c>,
    /// <c>usize</c>, …), or a slice / pointer / optional composed of a spellable element
    /// (<c>[]const u8</c>, <c>*u8</c>, <c>?i32</c>). Returns null for anything else — a bare identifier
    /// that is NOT a primitive (a user type or an alias, whose <c>@typeName</c> is zig's file-qualified
    /// / resolved name, not the source token), or a type former V1 does not spell — so
    /// <see cref="LowerBuiltinCall"/> makes it a precise loud cut rather than emit a wrong name.</summary>
    private string? ZigTypeSpelling(Item typeAst) => typeAst.Content switch
    {
        Zig.Ident id when TryLowerPrim(Tok(id.Arg0), out _) => Tok(id.Arg0),
        Zig.TySlice s      => ZigTypeSpelling(s.Arg2) is { } e ? "[]" + e : null,
        Zig.TySliceConst s => ZigTypeSpelling(s.Arg3) is { } e ? "[]const " + e : null,
        Zig.TyPointer p    => ZigTypeSpelling(p.Arg1) is { } e ? "*" + e : null,
        Zig.TyPtrConst p   => ZigTypeSpelling(p.Arg2) is { } e ? "*const " + e : null,
        Zig.TyOptional o   => ZigTypeSpelling(o.Arg1) is { } e ? "?" + e : null,
        _ => null,
    };

    /// <summary>Build a <see cref="LitStr"/> from a plain (unquoted, escape-free) string — the shared
    /// shape a Zig string literal lowers to (a quoted segment through the C string encoder, typed
    /// <c>char[N]</c> incl. the NUL). Used by <c>@typeName</c>; the spelling is ASCII with no quote /
    /// backslash, so it needs no escaping.</summary>
    private static LitStr ZigStringLiteral(string text)
    {
        var segs = new List<string> { "\"" + text + "\"" };
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
    }

    /// <summary>Lower a two-operand Zig math builtin (<c>@min</c>/<c>@max</c>/<c>@rem</c>/<c>@divTrunc</c>/
    /// <c>@mod</c>/<c>@divFloor</c>) to a <c>ZigMath.&lt;helper&gt;&lt;T&gt;</c> call
    /// (<see cref="DotCC.Libc.ZigMath"/>). Zig has no integer promotion, so the result type is the
    /// peer-resolved operand type (<see cref="PeerIntType"/>); both operands coerce to it so C# infers
    /// the generic <c>T</c> and the op runs at the right width (evaluated once, as call arguments — no
    /// double-eval). <c>@min</c>/<c>@max</c> are variadic in Zig; V1 handles the binary form and makes a
    /// wider call a clear arity error.</summary>
    private CExpr MathBin2(string helper, string zigName, IReadOnlyList<Item> bargs)
    {
        // `@min` / `@max` are variadic in zig, and a comptime-known result is a comptime value: `@Int(s,
        // @max(8, info.int.bits))` (std.fmt.parseIntWithSign) needs its width during lowering.
        if (zigName is "@min" or "@max" && bargs.Count >= 2)
        {
            var operands = bargs.Select(LowerExpr).ToList();
            var values = new List<long>(operands.Count);
            foreach (var o in operands) { if (_ir.ConstEval(o) is { } v) { values.Add(v); } }
            var peer = operands.Skip(1).Aggregate(operands[0].Type, (acc, o) => PeerIntType(new DefaultLit { Type = acc }, o));
            if (values.Count == operands.Count)
            {
                var r = zigName == "@max" ? values.Max() : values.Min();
                return new LitInt(r.ToString(System.Globalization.CultureInfo.InvariantCulture), r) { Type = peer };
            }
            var acc = CoerceToPeer(operands[0], peer);
            for (var i = 1; i < operands.Count; i++)
            {
                acc = new Call($"ZigMath.{helper}", new List<CExpr> { acc, CoerceToPeer(operands[i], peer) }) { Type = peer };
            }
            return acc;
        }
        if (bargs.Count != 2)
        {
            throw new IrUnsupportedException(
                $"zig `{zigName}` expects (a, b); got {bargs.Count} argument(s)"
                + (zigName is "@min" or "@max" ? " (variadic `@min`/`@max` beyond two operands is not lowered yet)" : ""));
        }
        var a = LowerExpr(bargs[0]);
        var b = LowerExpr(bargs[1]);
        var t = PeerIntType(a, b);
        // Two compile-time constants fold, so a quotient can size an array (`*const [@divExact(@typeInfo(T).int
        // .bits, 8)]u8` in std.mem.readInt): the same arithmetic ZigMath performs at runtime.
        if (_ir.ConstEval(a) is { } av && _ir.ConstEval(b) is { } bv && bv != 0)
        {
            long? folded = helper switch
            {
                "DivTrunc" => av / bv,
                "DivFloor" => (av / bv) - ((av % bv != 0) && ((av < 0) != (bv < 0)) ? 1 : 0),
                "Rem" => av % bv,
                "Mod" => ((av % bv) + bv) % bv,
                _ => null,
            };
            if (folded is { } f) { return new LitInt(f.ToString(System.Globalization.CultureInfo.InvariantCulture), f) { Type = t }; }
        }
        return new Call($"ZigMath.{helper}", new List<CExpr> { CoerceToPeer(a, t), CoerceToPeer(b, t) }) { Type = t };
    }

    /// <summary>Lower a two-operand overflow-detecting builtin (<c>@addWithOverflow</c>/
    /// <c>@subWithOverflow</c>/<c>@mulWithOverflow</c>) to a <c>ZigMath.&lt;helper&gt;&lt;T&gt;</c> call
    /// (<see cref="DotCC.Libc.ZigMath"/>) returning Zig's <c>struct { T, u1 }</c> — modeled as a
    /// <c>CType.Tuple([T, u8])</c> so it destructures / indexes through the existing tuple path. Both
    /// operands coerce to the peer-resolved type (evaluated once, as call arguments). A 128-bit operand
    /// is a loud cut (the overflow flag is computed in a 128-bit accumulator, which can't be widened
    /// further to detect a 128-bit overflow).</summary>
    private CExpr OverflowBin(string helper, string zigName, IReadOnlyList<Item> bargs)
    {
        if (bargs.Count != 2)
        {
            throw new IrUnsupportedException($"zig `{zigName}` expects (a, b); got {bargs.Count} argument(s)");
        }
        var a = LowerExpr(bargs[0]);
        var b = LowerExpr(bargs[1]);
        var t = PeerIntType(a, b);
        RejectWideOverflowOperand(zigName, t);
        return new Call($"ZigMath.{helper}", new List<CExpr> { CoerceToPeer(a, t), CoerceToPeer(b, t) })
        {
            Type = new CType.Tuple(new List<CType> { t, CType.UChar }),
        };
    }

    /// <summary>Lower <c>@shlWithOverflow(value, shift)</c> — like <see cref="OverflowBin"/> but the
    /// second operand is a shift COUNT (Zig's small <c>Log2(T)</c>), coerced to <c>int</c>, not
    /// peer-resolved with the value. The result type is <c>struct { T, u1 }</c> over the value's type.</summary>
    private CExpr OverflowShl(string zigName, IReadOnlyList<Item> bargs)
    {
        if (bargs.Count != 2)
        {
            throw new IrUnsupportedException($"zig `{zigName}` expects (value, shift); got {bargs.Count} argument(s)");
        }
        var value = LowerExpr(bargs[0]);
        var t = value.Type.Unqualified;
        if (t is not CType.Prim { Integer: true })
        {
            throw new IrUnsupportedException($"zig `{zigName}` expects an integer value operand, got {value.Type.Describe()}");
        }
        RejectWideOverflowOperand(zigName, t);
        var shift = new Cast(CType.Int, LowerExpr(bargs[1])) { Type = CType.Int };
        return new Call("ZigMath.ShlWithOverflow", new List<CExpr> { value, shift })
        {
            Type = new CType.Tuple(new List<CType> { t, CType.UChar }),
        };
    }

    /// <summary>Reject a 128-bit operand of an overflow-detecting builtin: the flag is derived in a
    /// 128-bit accumulator, so a 128-bit operation can't have its overflow detected (there is no wider
    /// primitive accumulator). A clear cut rather than a silently-wrong flag.</summary>
    private static void RejectWideOverflowOperand(string zigName, CType t)
    {
        if (t.Unqualified is CType.Prim { Bytes: 16, Integer: true })
        {
            throw new IrUnsupportedException(
                $"zig `{zigName}` on a 128-bit operand is not lowered yet (overflow is computed in a 128-bit "
                + "accumulator, which can't detect a 128-bit overflow); use a <= 64-bit integer");
        }
    }

    /// <summary>A bit-count builtin (<c>@popCount</c>/<c>@clz</c>/<c>@ctz</c>) → <c>ZigMath.&lt;helper&gt;</c>,
    /// or its literal when the operand is compile-time-known and its type is a standard width. That fold is
    /// what lets a comptime computation use one — <c>std.math.Log2Int</c>'s
    /// <c>const log2_bits = 16 - @clz(bits - 1);</c> sizes the type it returns — and it counts in the same
    /// width the runtime helper would, so the two can never disagree.
    /// <para>That width is the operand's ZIG type (<see cref="ZigIntOperandType"/>), not its lowered one:
    /// the IR types <c>bits - 1</c> on a <c>u16</c> with C's promotion to <c>int</c>, but zig has no
    /// promotion — it is a <c>u16</c>, and <c>@clz</c> counts in 16 bits. The operand is narrowed back to
    /// that type (a truncating cast, zig's own wrap-on-overflow in ReleaseFast) before counting, at comptime
    /// and at runtime alike.</para></summary>
    private CExpr FoldBitCount(string helper, Item argItem)
    {
        var arg = LowerExpr(argItem);
        if (ZigIntOperandType(argItem) is CType.Prim { Integer: true } zt
            && arg.Type?.Unqualified is CType.Prim { Integer: true } lt
            && zt.SizeOf < lt.SizeOf)
        {
            arg = new Cast(zt, arg) { Type = zt };
        }
        if (_ir.ConstEval(arg) is { } v
            && arg.Type?.Unqualified is CType.Prim { Integer: true, Name: not "_Bool", Bytes: 1 or 2 or 4 or 8 } p)
        {
            var width = p.Bytes * 8;
            var bits = unchecked((ulong)v) & (width == 64 ? ulong.MaxValue : (1UL << width) - 1);
            var n = helper switch
            {
                "PopCount" => System.Numerics.BitOperations.PopCount(bits),
                "Clz"      => bits == 0 ? width : System.Numerics.BitOperations.LeadingZeroCount(bits) - (64 - width),
                _          => bits == 0 ? width : System.Numerics.BitOperations.TrailingZeroCount(bits),
            };
            return new LitInt(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n) { Type = CType.Int };
        }
        return new Call("ZigMath." + helper, new List<CExpr> { arg }) { Type = CType.Int };
    }

    /// <summary>The ZIG type of an integer operand expression, or null when it is an untyped
    /// <c>comptime_int</c> (a literal, or arithmetic over literals) or a shape this does not follow. Zig has
    /// no integer promotion: arithmetic takes its operands' peer type, with an untyped literal yielding to
    /// the typed side — so <c>bits - 1</c> on a <c>u16</c> is a <c>u16</c>. A leaf is a name (its declared
    /// symbol type, which for a comptime const is its annotation) or an <c>@as(T, …)</c>.</summary>
    private CType? ZigIntOperandType(Item it)
    {
        switch (it.Content)
        {
            case Zig.Grouped g: return ZigIntOperandType(g.Arg1);
            case Zig.Add a:     return PeerZigType(a.Arg0, a.Arg2);
            case Zig.Sub a:     return PeerZigType(a.Arg0, a.Arg2);
            case Zig.Mul a:     return PeerZigType(a.Arg0, a.Arg2);
            case Zig.AddWrap a: return PeerZigType(a.Arg0, a.Arg2);
            case Zig.SubWrap a: return PeerZigType(a.Arg0, a.Arg2);
            case Zig.MulWrap a: return PeerZigType(a.Arg0, a.Arg2);
            case Zig.DivOp a:   return PeerZigType(a.Arg0, a.Arg2);
            case Zig.ModOp a:   return PeerZigType(a.Arg0, a.Arg2);
            case Zig.BitAnd a:  return PeerZigType(a.Arg0, a.Arg2);
            case Zig.BitOr a:   return PeerZigType(a.Arg0, a.Arg2);
            case Zig.BitXor a:  return PeerZigType(a.Arg0, a.Arg2);
            case Zig.Shl a:     return ZigIntOperandType(a.Arg0);   // a shift keeps its LEFT operand's type
            case Zig.Shr a:     return ZigIntOperandType(a.Arg0);
            case Zig.Ident id:  return _symbols.Resolve(Tok(id.Arg0))?.Type?.Unqualified;
            case Zig.BuiltinCall b when Tok(b.Arg0) == "@as" && Flatten(b.Arg2) is [var asType, _]:
                return LowerType(asType).Unqualified;
            default: return null;
        }
    }

    /// <summary>The peer type of two zig operands (<see cref="ZigIntOperandType"/>): an untyped side yields
    /// to the typed one; two typed sides take the wider (they are equal in valid zig).</summary>
    private CType? PeerZigType(Item l, Item r)
    {
        var lt = ZigIntOperandType(l);
        var rt = ZigIntOperandType(r);
        if (lt is null) { return rt; }
        if (rt is null) { return lt; }
        return lt.SizeOf >= rt.SizeOf ? lt : rt;
    }

    /// <summary>Validate + return the single integer argument of a bit-count builtin
    /// (<c>@popCount</c>/<c>@clz</c>/<c>@ctz</c>) — a clear arity error otherwise.</summary>
    private static Item BitCountArg(string zigName, IReadOnlyList<Item> bargs)
        => bargs.Count == 1
            ? bargs[0]
            : throw new IrUnsupportedException($"zig `{zigName}` expects (integer); got {bargs.Count} argument(s)");

    /// <summary>The unsigned peer of a signed integer <see cref="CType.Prim"/> (same byte width,
    /// <c>Signed = false</c>) — for <c>@abs</c>, whose result type is <c>uN</c> for an <c>iN</c> operand.
    /// An already-unsigned or non-integer type is returned unchanged.</summary>
    private static CType UnsignedPeerInt(CType t) => t.Unqualified switch
    {
        CType.Prim { Integer: true, Signed: true, Bytes: 1 } => CType.UChar,
        CType.Prim { Integer: true, Signed: true, Bytes: 2 } => CType.UShort,
        CType.Prim { Integer: true, Signed: true, Bytes: 4 } => CType.UInt,
        CType.Prim { Integer: true, Signed: true, Bytes: 8 } => CType.ULong,
        CType.Prim { Integer: true, Signed: true, Bytes: 16 } => CType.UInt128,
        _ => t,
    };

    /// <summary>Lower a result-location cast builtin at its sink. Each is single-arg; the cast
    /// TARGET is the <paramref name="sink"/> (Zig infers it from the result location, unlike
    /// <c>@as(T, x)</c> which carries the type). Most map to the C <see cref="Cast"/> IR — the
    /// backend's unchecked cast truncates/converts, matching Zig's NON-safe-mode semantics (dotcc
    /// models no overflow trap, the same stance taken for plain <c>+</c>); <c>@bitCast</c>
    /// reinterprets the bit pattern via <see cref="BitCast"/>. Without a sink it's a clear error
    /// (Zig requires a result location to infer the type).</summary>
    private CExpr LowerResultLocationBuiltin(string name, List<Item> bargs, CType? sink)
    {
        if (bargs.Count != 1)
        {
            throw new IrUnsupportedException($"zig builtin '{name}' expects (value); got {bargs.Count} argument(s)");
        }
        if (sink is null)
        {
            throw new IrUnsupportedException(
                $"zig builtin '{name}' needs a result location to infer its target type — use it at a typed binding, " +
                "return, assignment, call argument, or nested inside `@as(T, …)`");
        }
        var operand = LowerExpr(bargs[0]);
        return name == "@bitCast"
            ? new BitCast(sink, operand) { Type = sink }
            : new Cast(sink, operand) { Type = sink };
    }

}
