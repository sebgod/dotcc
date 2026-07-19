#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Generic functions — call-site monomorphization (wall-plan W3a/W3b/W5). A <c>comptime</c>
/// or <c>anytype</c> parameter turns a function into a TEMPLATE: it is NOT lowered once; a call
/// instantiates a SPECIALIZED body per resolved-argument tuple (C++-template-style monomorphization over
/// the retained AST), emitted under a deterministic mangled name and memoized by key so a repeat call
/// reuses it. Three kinds of monomorphization-key parameter:
/// <list type="bullet">
/// <item><b>VALUE</b> (<c>comptime N: i32</c>, wall-plan W3a) — the resolved integer is baked into the
/// body as a literal; the signature does NOT depend on it, so it is lowered once at template time.</item>
/// <item><b>TYPE</b> (<c>comptime T: type</c>, wall-plan W3b) — the later parameter / return types
/// reference <c>T</c>, so the signature DEPENDS on the resolved type and CANNOT be lowered at template
/// time. Such a generic is lowered PER INSTANTIATION: at the call site the type argument resolves to a
/// concrete <see cref="CType"/>, seeded into <see cref="_typeAliases"/> so the signature (and later the
/// body) resolve <c>T</c> through <see cref="LowerTypeName"/>; the instance is mangled by the resolved
/// TYPE (<c>max__i32</c> / <c>max__f64</c>) — an alias for the same type keys the same instance.</item>
/// <item><b>ANYTYPE</b> (<c>a: anytype</c>, wall-plan W5) — a HYBRID of a TYPE key and a runtime slot.
/// Unlike a comptime TYPE param (an explicit type argument, consumed at compile time), an anytype
/// param's type is INFERRED from the actual argument (<c>T := @TypeOf(arg)</c>, <see cref="InferArgType"/>)
/// AND the argument is still passed at runtime. The inferred type keys / mangles the instance and is
/// seeded into <see cref="_anytypeSeeds"/> so a signature spelled <c>@TypeOf(param)</c> resolves; the
/// instance body binds the param as an ordinary runtime symbol of the inferred type, so duck-typed use
/// (member access, arithmetic) lowers against the concrete type, a mismatch failing PER INSTANTIATION.
/// The signature depends on the inferred type, so — like a TYPE param — it is lowered per instance.</item>
/// </list>
///
/// <para>RE-ENTRANCY (the audit the plan mandated FIRST): the body lowering carries a lot of per-fn
/// mutable state (temp counters, the ANF hoist buffer, the label/loop-value stacks, the
/// <c>_currentFn*</c> fields, the symbol-table scope) — re-entering <see cref="LowerFnBody"/>
/// synchronously mid-call would clobber it. So instantiation is DEFERRED: a call records the request
/// in <see cref="_pendingInstantiations"/> and emits a call to the mangled instance symbol; the
/// worklist is DRAINED after pass 2 (like the pass-3 comptime-fold drain), so every instance body
/// lowers at TOP LEVEL, in a clean between-functions state, never nested. Draining may enqueue more
/// instantiations (transitive / recursive generics) — the cursor loop in <see cref="ZigLowering.Lower"/>
/// picks those up, bounded by <see cref="MaxInstantiations"/>.</para>
///
/// <para>SCOPING (the plan's queued obligation): a comptime VALUE param reuses the <c>comptime var</c>
/// machinery — recorded in <see cref="_comptimeVars"/>, keyed by a FRESH per-instance symbol, so
/// distinct instances' values are inherently isolated. A comptime TYPE param seeds
/// <see cref="_typeAliases"/> — a function-flat map (the W1 leniency) — so a naïve set WOULD leak the
/// binding across instances / functions and, worse, a nested instantiation whose type param shares the
/// name <c>T</c> would clobber the outer one. The fix is the proven W2 shadow pattern: the seed is
/// SHADOW-SAVED and restored, both around the per-instance signature lowering here
/// (<see cref="InstantiateGeneric"/>) and around the instance body lowering
/// (<see cref="LowerFnBodyCore"/>, via <see cref="_typeAliasShadows"/>). Because draining is SEQUENTIAL
/// at top level, save/restore is sufficient — no global frame stack is needed. A body-local
/// <c>const U = T;</c> keeps the W1 alias leniency (each instance re-declares it, so it is overwritten,
/// never stale-read across instances).</para>
///
/// <para>V1 SCOPE (loud cuts): a generic METHOD is rejected (free functions only); a <c>type</c>-RETURNING
/// function (<c>fn Pair(comptime T: type) type</c>) is wall-plan W4; a value-dependent type
/// (<c>fn f(comptime n: usize, a: [n]u8)</c>) needs a comptime VALUE woven into a type extent (a W3a
/// signature is lowered at template time — but a mixed <c>comptime T: type</c> generic DOES lower its
/// signature per instance, so a later param typed <c>T</c> is fully supported); a comptime <c>if
/// (T == i32)</c> type-comparison inside a body needs interpreter type values (a later brick); and a
/// comptime VALUE argument must be <see cref="IrBuilder.ConstEval"/>-able, a comptime TYPE argument must
/// name a type (a bare primitive / container / alias — a type-FORMER argument like <c>[]u8</c> does not
/// parse in argument position yet).</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>How a function parameter is bound (wall-plan W3a/W3b).</summary>
    private enum ParamKind
    {
        /// <summary>An ordinary runtime parameter — a signature slot with storage.</summary>
        Runtime,
        /// <summary>A <c>comptime N: &lt;integer type&gt;</c> VALUE parameter — a monomorphization key
        /// baked into the body as a literal (W3a).</summary>
        ComptimeValue,
        /// <summary>A <c>comptime T: type</c> TYPE parameter — a monomorphization key that makes the
        /// signature depend on the resolved type (W3b).</summary>
        ComptimeType,
        /// <summary>An <c>a: anytype</c> INFERRED-type parameter (wall-plan W5) — a monomorphization key
        /// like a comptime TYPE param, but the type is inferred from the ACTUAL ARGUMENT
        /// (<c>@TypeOf(arg)</c>) rather than passed explicitly, AND the argument is still passed at
        /// runtime (unlike a <see cref="ComptimeType"/> arg, which is a type spelling consumed at compile
        /// time). So an anytype param is a HYBRID: it seeds the instance's type environment (keying the
        /// specialization) and ALSO occupies a runtime signature slot.</summary>
        AnyType,
    }

    /// <summary>One parameter of a function signature, carrying its RAW type AST (lowered lazily — a
    /// type-param generic's runtime-parameter and return types depend on <c>T</c> and can only resolve
    /// per-instantiation, once <c>T</c> is bound) and its <see cref="ParamKind"/>. The variadic marker
    /// <c>...</c> is tracked separately (it has no name/type). For a <see cref="ParamKind.ComptimeType"/>
    /// param the <see cref="TypeAst"/> is the <c>type</c> keyword and is never lowered; for a
    /// <see cref="ParamKind.AnyType"/> param it is the <c>anytype</c> keyword and is likewise never
    /// lowered (the type is inferred from the argument).</summary>
    private readonly record struct ParamInfo(string Name, Item TypeAst, ParamKind Kind)
    {
        /// <summary>True for either comptime kind — a monomorphization key with NO runtime slot. An
        /// <see cref="ParamKind.AnyType"/> param is deliberately EXCLUDED (it is a key AND a runtime
        /// slot); callers that mean "any kind that makes the function generic" test the kinds directly.</summary>
        public bool IsComptime => Kind is ParamKind.ComptimeValue or ParamKind.ComptimeType;
    }

    /// <summary>A generic function's retained template — everything an instantiation needs to re-lower a
    /// specialized signature + body: the template symbol, the FULL ordered parameter list (comptime +
    /// runtime, with raw type ASTs, so a call splits args positionally and lowers each runtime type once
    /// <c>T</c> is bound), and the raw return-type + body ASTs (re-lowered per instance).</summary>
    private sealed record GenericFnInfo(
        Symbol Template,
        IReadOnlyList<ParamInfo> Params,
        Item RetType,
        bool ErrUnion,
        Item Body);

    /// <summary>A queued instantiation body to lower after pass 2. Drained at top level
    /// (re-entrancy-safe — see the class doc), so its <see cref="LowerFnBodyCore"/> runs in a clean
    /// between-functions state. Carries the per-instance runtime parameter list + the comptime VALUE and
    /// TYPE seeds resolved at the call site, so the body lowers against the concrete signature.</summary>
    private sealed record PendingInstantiation(
        Symbol Instance,
        GenericFnInfo Generic,
        IReadOnlyList<(string name, long value, CType type)> ValueSeeds,
        IReadOnlyList<(string name, CType type)> TypeSeeds,
        IReadOnlyList<(string name, CType type)> RuntimeParams,
        IReadOnlyList<(string name, bool hasValue, long value, CType inner)> OptionalSeeds);

    /// <summary>Generic (comptime-param) function symbols → their retained template. Populated in
    /// pass 1 (<see cref="DeclareFn"/>), consulted at every call site (<c>LowerCallInner</c>) so a
    /// call to a generic routes to <see cref="InstantiateGeneric"/> instead of a direct
    /// <see cref="BuildCall"/>.</summary>
    private readonly Dictionary<Symbol, GenericFnInfo> _genericFns = new();

    /// <summary>Memoized instantiations: the mangled instance name (which IS the instantiation key —
    /// deterministic from the template name + the resolved comptime-argument tuple) → the instance
    /// <see cref="Symbol"/>. A repeat call with the same values/types reuses the one emitted body.</summary>
    private readonly Dictionary<string, Symbol> _instantiations = new(System.StringComparer.Ordinal);

    /// <summary>The monomorphization worklist — instance bodies awaiting lowering, drained after pass
    /// 2 by a cursor loop (so transitive appends are picked up). Enqueued by
    /// <see cref="InstantiateGeneric"/>.</summary>
    private readonly List<PendingInstantiation> _pendingInstantiations = new();

    /// <summary>Distinct instantiations emitted this build — capped to bound emitted-code size (a
    /// runaway recursive generic, e.g. one keyed by an ever-growing value, would otherwise instantiate
    /// forever). Enforced at ENQUEUE time in <see cref="InstantiateGeneric"/>.</summary>
    private int _instantiationCount;

    /// <summary>The instantiation budget — a friendly upper bound on distinct specializations of all
    /// generics in one build (mirrors the <c>inline for</c> unroll cap's spirit).</summary>
    private const int MaxInstantiations = 1024;

    /// <summary>True while lowering a generic INSTANCE body (wall-plan W3a) — set in
    /// <see cref="LowerFnBodyCore"/> when comptime value or type seeds are present. It unlocks Zig's
    /// comptime control-flow inside the specialized body: an <c>if</c> whose condition is comptime-known
    /// (<see cref="IrBuilder.ConstEval"/> folds it, because a comptime value param substitutes a literal)
    /// is folded to just its taken branch (<see cref="LowerIfStmt"/>), and code after a comptime-taken
    /// terminator is dropped as comptime-dead (<see cref="LowerStmtsWithDefers"/>). Together these let a
    /// recursive comptime generic (<c>fib</c>: <c>if (n &lt; 2) return n; return fib(n-1)+fib(n-2);</c>)
    /// prune its base case and terminate, instead of instantiating <c>fib(n-1)</c> forever. Gated to
    /// instance bodies so no ordinary (non-generic) lowering changes. A comptime-if in EXPRESSION
    /// position (a ternary <c>if</c>) is not folded yet — a recursive generic written that way would
    /// hit <see cref="MaxInstantiations"/> (a loud cut, not a miscompile).</summary>
    private bool _inGenericInstance;

    /// <summary>Instantiate (or reuse) a generic function at a call site (wall-plan W3a/W3b). Splits the
    /// arguments positionally over the template's parameters:
    /// <list type="number">
    /// <item>Each comptime TYPE argument (<c>i32</c>) resolves to a concrete <see cref="CType"/> in the
    /// CALLER's type environment (so a type-arg spelled as an alias resolves to the aliased type — the
    /// "key by resolved type" rule).</item>
    /// <item>The resolved type args are seeded into <see cref="_typeAliases"/> (shadow-saved), so the
    /// per-instance SIGNATURE — runtime parameter types + the return type, which may reference <c>T</c> —
    /// lowers; a comptime VALUE argument is <see cref="IrBuilder.ConstEval"/>-folded (a non-constant is a
    /// loud error).</item>
    /// <item>The resolved value + type tuple forms the mangled name (<c>max__i32</c>; <c>fn__10</c>; a
    /// negative value spells <c>n10</c>), the memoization key. On first sight the instance symbol is
    /// declared with the lowered concrete signature and its body enqueued for the post-pass-2 drain.</item>
    /// </list>
    /// The seeded type env is restored before returning. The call itself lowers to a direct
    /// <see cref="BuildCall"/> of the instance passing only the RUNTIME arguments (evaluated in the
    /// caller's context) — the comptime ones are baked into the specialized body / signature.</summary>
    private CExpr InstantiateGeneric(Symbol templateSym, GenericFnInfo g, IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != g.Params.Count)
        {
            throw new IrUnsupportedException(
                $"call to generic '{templateSym.Name}': expected {g.Params.Count} argument(s), got {argItems.Count}");
        }

        var inv = CultureInfo.InvariantCulture;
        var mangleTokens = new List<string>();
        var typeSeeds = new List<(string name, CType type)>();
        var valueSeeds = new List<(string name, long value, CType type)>();
        var optionalSeeds = new List<(string name, bool hasValue, long value, CType inner)>();
        var anytypeSeeds = new List<(string name, CType type)>();
        var runtimeArgItems = new List<Item>();

        // Phase 1 — resolve each comptime TYPE arg in the CALLER's environment (a type-arg spelled as an
        // alias resolves to its aliased type, so it keys the same instance as the underlying type), and
        // INFER each `anytype` arg's type from the actual argument (`T := @TypeOf(arg)`, wall-plan W5).
        for (var i = 0; i < g.Params.Count; i++)
        {
            switch (g.Params[i].Kind)
            {
                case ParamKind.ComptimeType:
                    typeSeeds.Add((g.Params[i].Name, LowerType(argItems[i]).Unqualified));
                    break;
                case ParamKind.AnyType:
                    anytypeSeeds.Add((g.Params[i].Name, InferArgType(argItems[i])));
                    break;
            }
        }

        // Phase 2 — seed the resolved type args (shadow-saved), so a later parameter / return type that
        // references `T`, and a comptime VALUE param whose type is `T`, resolve while we lower them.
        var typeShadows = new List<(string name, CType? prev)>();
        foreach (var (name, type) in typeSeeds)
        {
            typeShadows.Add((name, _typeAliases.TryGetValue(name, out var pv) ? pv : (CType?)null));
            _typeAliases[name] = type;
        }
        // Seed each inferred `anytype` type (shadow-saved) so a signature spelled `@TypeOf(param)` (a
        // return type or a later parameter) resolves through TypeOfBuiltin — the param is not yet an
        // in-scope symbol at signature-lowering time (it becomes one only in the instance body).
        var anytypeShadows = new List<(string name, CType? prev)>();
        foreach (var (name, type) in anytypeSeeds)
        {
            anytypeShadows.Add((name, _anytypeSeeds.TryGetValue(name, out var pv) ? pv : (CType?)null));
            _anytypeSeeds[name] = type;
        }
        Symbol instanceSym;
        try
        {
            // Mangle in PARAMETER order (types then values interleave by position) so the key is
            // deterministic; collect comptime VALUE seeds and the runtime argument items.
            for (var i = 0; i < g.Params.Count; i++)
            {
                switch (g.Params[i].Kind)
                {
                    case ParamKind.ComptimeType:
                        mangleTokens.Add(MangleType(typeSeeds.First(s => s.name == g.Params[i].Name).type));
                        break;
                    case ParamKind.ComptimeValue:
                        // A comptime OPTIONAL value param `comptime x: ?T` (road-to-zig-std S4b): the arg
                        // is a comptime `null` (no runtime rep) or a comptime-known payload. Seed it into
                        // _comptimeOptionalVars so a captured `if (x) |y| … else …` folds at lowering time.
                        if (LowerType(g.Params[i].TypeAst).Unqualified is CType.Optional optParam)
                        {
                            if (IsComptimeNull(argItems[i]))
                            {
                                mangleTokens.Add("optnull");
                                optionalSeeds.Add((g.Params[i].Name, false, 0, optParam.Inner));
                            }
                            else
                            {
                                var optArgExpr = LowerExpr(argItems[i]);
                                if (_ir.ConstEval(optArgExpr) is not { } ov)
                                {
                                    throw new IrUnsupportedException(
                                        $"call to generic '{templateSym.Name}': the `comptime {g.Params[i].Name}: ?T` argument must be "
                                        + "a comptime `null` or a compile-time-known payload");
                                }
                                mangleTokens.Add("opt" + (ov >= 0 ? ov.ToString(inv) : "n" + (-(System.Int128)ov).ToString(inv)));
                                optionalSeeds.Add((g.Params[i].Name, true, ov, optParam.Inner));
                            }
                            break;
                        }
                        var argExpr = LowerExpr(argItems[i]);
                        if (_ir.ConstEval(argExpr) is not { } v)
                        {
                            throw new IrUnsupportedException(
                                $"call to generic '{templateSym.Name}': the `comptime {g.Params[i].Name}` argument must be a "
                                + "compile-time-known integer constant (a literal / arithmetic / comptime value; wrap a call as `comptime f()`)");
                        }
                        // A negative value can't spell a C# identifier segment, so encode the sign;
                        // long.MinValue has no positive `long`, so widen through Int128 for the magnitude.
                        mangleTokens.Add(v >= 0 ? v.ToString(inv) : "n" + (-(System.Int128)v).ToString(inv));
                        valueSeeds.Add((g.Params[i].Name, v, LowerType(g.Params[i].TypeAst)));
                        break;
                    case ParamKind.AnyType:
                        // A hybrid (wall-plan W5): its inferred type keys the specialization AND the
                        // argument is passed at runtime — so it contributes BOTH a mangle token and a
                        // runtime argument (unlike a comptime TYPE arg, which is compile-time-only).
                        mangleTokens.Add(MangleType(_anytypeSeeds[g.Params[i].Name]));
                        runtimeArgItems.Add(argItems[i]);
                        break;
                    default:
                        runtimeArgItems.Add(argItems[i]);
                        break;
                }
            }

            var mangled = templateSym.Name + "__" + string.Join("_", mangleTokens);
            if (!_instantiations.TryGetValue(mangled, out instanceSym!))
            {
                if (++_instantiationCount > MaxInstantiations)
                {
                    throw new IrUnsupportedException(
                        $"zig: generic instantiation budget ({MaxInstantiations}) exceeded while instantiating "
                        + $"'{mangled}' — a runaway recursive generic (an ever-changing comptime value)?");
                }
                // Lower the concrete signature against the seeded type env — runtime parameter types +
                // the return type may reference a type param or `@TypeOf(anytypeParam)`. An `anytype`
                // param (W5) is a runtime slot whose type is the inferred one (not lowered from an AST).
                // (For a value-only generic no type is seeded, so this is exactly the W3a template-time
                // signature.) Preserves parameter order, so it aligns with `runtimeArgItems`.
                var runtimeParams = g.Params
                    .Where(p => p.Kind is ParamKind.Runtime or ParamKind.AnyType)
                    .Select(p => (p.Name, p.Kind == ParamKind.AnyType ? _anytypeSeeds[p.Name] : LowerType(p.TypeAst)))
                    .ToList();
                var ret = LowerType(g.RetType);
                if (g.ErrUnion) { ret = new CType.ErrorUnion(ret); }
                instanceSym = _symbols.Declare(new Symbol
                {
                    Name = mangled,
                    Kind = SymKind.Func,
                    Type = new CType.Func(ret, runtimeParams.Select(p => p.Item2).ToList(), false),
                    IsGlobal = true,
                });
                // An error-union generic: register the instance's raw return-type AST so its body resolves
                // its declared error set in LowerFnBodyCore (the same lazy resolution a plain fn gets).
                if (ret is CType.ErrorUnion) { _fnErrorReturnTypes[instanceSym] = (g.RetType, g.ErrUnion); }
                _instantiations[mangled] = instanceSym;
                _pendingInstantiations.Add(new PendingInstantiation(instanceSym, g, valueSeeds, typeSeeds, runtimeParams, optionalSeeds));
            }
        }
        finally
        {
            // Restore the caller's type env — the seeds are re-applied per instance at drain time.
            for (var i = typeShadows.Count - 1; i >= 0; i--)
            {
                var (name, prev) = typeShadows[i];
                if (prev is { } p) { _typeAliases[name] = p; } else { _typeAliases.Remove(name); }
            }
            // Restore the `anytype` seeds (W5) — the instance BODY resolves each such param through its
            // in-scope symbol (declared with the inferred type in `runtimeParams`), so the seed is only
            // needed for the signature lowering here.
            for (var i = anytypeShadows.Count - 1; i >= 0; i--)
            {
                var (name, prev) = anytypeShadows[i];
                if (prev is { } p) { _anytypeSeeds[name] = p; } else { _anytypeSeeds.Remove(name); }
            }
        }
        // The runtime arguments are the CALLER's expressions — lower them (in BuildCall) in the restored
        // caller type env, coercing to the instance's now-concrete parameter types. An `anytype`
        // argument is among them (a runtime slot), coerced to its inferred parameter type.
        return BuildCall(instanceSym, runtimeArgItems, receiver: null);
    }

    /// <summary>Infer an <c>anytype</c> argument's type at a call site (wall-plan W5) — Zig's
    /// <c>@TypeOf(actual arg)</c>. Lowers the argument expression into a THROWAWAY hoist buffer (like
    /// <see cref="TypeOfBuiltin"/>) purely to read its synthesized <see cref="CType"/>; the real
    /// argument is lowered AGAIN in <see cref="BuildCall"/> for the runtime call, so any side effect is
    /// emitted exactly once (the inference lowering here is discarded).</summary>
    private CType InferArgType(Item argItem)
    {
        var savedBuf = _hoist;
        var savedImpure = _hoistImpureSeen;
        _hoist = new List<CStmt>();   // throwaway — the inference lowering is discarded
        try
        {
            return (LowerExpr(argItem).Type
                ?? throw new IrUnsupportedException("zig `anytype` argument has no statically known type")).Unqualified;
        }
        finally
        {
            _hoist = savedBuf;
            _hoistImpureSeen = savedImpure;
        }
    }

    /// <summary>Lower one queued instantiation body (drained after pass 2). Hands the pre-resolved
    /// runtime parameters + the comptime VALUE seeds (each name paired with its resolved value) + the
    /// comptime TYPE seeds (each name paired with its resolved <see cref="CType"/>) to
    /// <see cref="LowerFnBodyCore"/>, which declares the value seeds as in-scope comptime symbols and
    /// seeds the type aliases (shadow-saved) so the body substitutes literals / resolves <c>T</c>. Runs
    /// at top level (never nested), so the per-fn lowering state starts clean.</summary>
    private void LowerInstantiationBody(PendingInstantiation p)
        => LowerFnBodyCore(p.Instance, p.RuntimeParams, p.Generic.Body, p.ValueSeeds, p.TypeSeeds, p.OptionalSeeds);

    /// <summary>True when a generic argument is a comptime <c>null</c> — a bare <c>null</c> literal
    /// (optionally parenthesized). The comptime-optional seed for such an argument has no payload.</summary>
    private static bool IsComptimeNull(Item arg)
    {
        var cur = arg;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        return cur.Content is Zig.NullLit;
    }

    /// <summary>Mangle a resolved comptime TYPE argument into an identifier-safe, structurally-unique
    /// token for the instance name (wall-plan W3b) — <c>i32</c>/<c>u32</c>/<c>f64</c>/<c>bool</c> for
    /// primitives, the type name for a container/enum, <c>p_&lt;pointee&gt;</c> for a pointer, and a
    /// sanitized <see cref="CType.Describe"/> as a catch-all. Keyed by the RESOLVED type (not the source
    /// spelling), so an alias for <c>i32</c> and <c>i32</c> itself key the same instance.</summary>
    private static string MangleType(CType t)
    {
        t = t.Unqualified;
        return t switch
        {
            CType.Prim { Name: "_Bool" } => "bool",
            CType.Prim p when p.IsInteger => (p.Signed ? "i" : "u") + (p.Bytes * 8).ToString(CultureInfo.InvariantCulture),
            CType.Prim p => "f" + (p.Bytes * 8).ToString(CultureInfo.InvariantCulture),
            CType.VoidType => "void",
            CType.Named n => n.Name,
            CType.Enum e => e.Name,
            CType.Pointer ptr => "p_" + MangleType(ptr.Pointee),
            _ => SanitizeIdent(t.Describe()),
        };
    }

    /// <summary>Replace every non-alphanumeric character with <c>_</c>, so an arbitrary type spelling
    /// becomes a legal identifier segment for a mangled instance name.</summary>
    private static string SanitizeIdent(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) { sb.Append(char.IsLetterOrDigit(ch) ? ch : '_'); }
        return sb.ToString();
    }

    // ---- type-returning functions (wall-plan W4) -------------------------

    /// <summary>A type-RETURNING generic function's retained template (wall-plan W4):
    /// <c>fn Pair(comptime T: type) type { return struct { a: T, b: T }; }</c>. Unlike an ordinary
    /// generic (which emits a specialized runtime BODY), a type-returning function is a COMPTIME type
    /// constructor — it emits no runtime code; a call in a type position REIFIES a fresh struct per
    /// resolved type argument (<c>Pair__i32</c>, memoized). Carries the template symbol, its
    /// (all-comptime-TYPE) params, and the raw body AST (a single <c>return struct {…}</c>).</summary>
    private readonly record struct TypeReturningGenericInfo(Symbol Template, IReadOnlyList<ParamInfo> Params, Item Body);

    /// <summary>Type-returning generic function symbols → their retained template (wall-plan W4).
    /// Populated in pass 1 (<see cref="DeclareFn"/>); a call to one in a type position (or a type-alias
    /// RHS) routes to <see cref="EvalTypeReturningCall"/> via <see cref="TryEvalTypeReturningCall"/>.
    /// Never lowered as a runtime function (skipped in the pass-1 body list, like a generic).</summary>
    private readonly Dictionary<Symbol, TypeReturningGenericInfo> _typeReturningGenerics = new();

    /// <summary>Recognize a call to a type-returning generic (wall-plan W4) — <c>Pair(i32)</c> in a
    /// type / type-alias position — and evaluate it to the reified <see cref="CType"/>. Handles both the
    /// with-args (<see cref="Zig.CallArgs"/>) and no-args (<see cref="Zig.CallNoArgs"/>) call shapes; the
    /// callee must be a bare identifier bound to a symbol in <see cref="_typeReturningGenerics"/>.
    /// Returns false for any other node (a curated std generic, a runtime call, a non-call), so the
    /// caller falls through to its normal handling.</summary>
    private bool TryEvalTypeReturningCall(Item maybeCall, out CType type)
    {
        type = CType.Void;
        Item calleeItem;
        IReadOnlyList<Item> args;
        switch (maybeCall.Content)
        {
            case Zig.CallArgs ca:   calleeItem = ca.Arg0; args = Flatten(ca.Arg2); break;
            case Zig.CallNoArgs cn: calleeItem = cn.Arg0; args = System.Array.Empty<Item>(); break;
            default: return false;
        }
        if (calleeItem.Content is Zig.Ident id
            && _symbols.Resolve(Tok(id.Arg0)) is { } sym
            && _typeReturningGenerics.TryGetValue(sym, out var info))
        {
            type = EvalTypeReturningCall(sym, info, args);
            return true;
        }
        return false;
    }

    /// <summary>Evaluate (or reuse) a type-returning generic at a use site (wall-plan W4): resolve each
    /// comptime TYPE argument to a concrete type in the CALLER's env (an alias → its aliased type, so the
    /// instance is keyed by the RESOLVED type — <c>Pair(i32)</c> ≡ <c>Pair(I)</c> for <c>const I=i32</c>),
    /// mangle by the resolved types (<c>Pair__i32</c>), and REIFY the returned <c>struct {…}</c> under
    /// that name — its fields lowered with the type params seeded into <see cref="_typeAliases"/>
    /// (shadow-saved), so <c>a: T</c> becomes the concrete field type and <c>next: ?*@This()</c> a
    /// self-pointer. Memoized: the mangled type is registered in <see cref="_containerTypes"/> BEFORE the
    /// fields lower, so a self-referential field / a recursive <c>Pair(T)</c> inside the body resolves to
    /// the in-progress type, and a repeat call reuses it. Returns the reified <see cref="CType.Named"/>.
    /// V1: fields-only (a method / <c>const</c> member in the returned struct is a loud cut), a single
    /// <c>return struct {…}</c> body.</summary>
    private CType EvalTypeReturningCall(Symbol templateSym, TypeReturningGenericInfo info, IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != info.Params.Count)
        {
            throw new IrUnsupportedException(
                $"call to type-returning generic '{templateSym.Name}': expected {info.Params.Count} type argument(s), got {argItems.Count}");
        }
        var inv = CultureInfo.InvariantCulture;
        // Resolve each comptime argument (road-to-zig-std S4b widens W4's TYPE-only params): a TYPE arg →
        // its resolved type; a VALUE arg → a comptime value; an OPTIONAL value arg → a comptime null /
        // payload. Each contributes a mangle token, so the reified struct is keyed by the resolved args.
        var typeSeeds = new List<(string name, CType type)>();
        var valueSeeds = new List<(string name, long value, CType type)>();
        var optionalSeeds = new List<(string name, bool hasValue, long value, CType inner)>();
        var mangleTokens = new List<string>(argItems.Count);
        for (var i = 0; i < info.Params.Count; i++)
        {
            var p = info.Params[i];
            if (p.Kind == ParamKind.ComptimeType)
            {
                var at = LowerType(argItems[i]).Unqualified;
                typeSeeds.Add((p.Name, at));
                mangleTokens.Add(MangleType(at));
            }
            else if (LowerType(p.TypeAst).Unqualified is CType.Optional optP)
            {
                // A comptime OPTIONAL value param `comptime x: ?T` — a comptime null or known payload.
                if (IsComptimeNull(argItems[i]))
                {
                    mangleTokens.Add("optnull");
                    optionalSeeds.Add((p.Name, false, 0, optP.Inner));
                }
                else
                {
                    if (_ir.ConstEval(LowerExpr(argItems[i])) is not { } ov)
                    {
                        throw new IrUnsupportedException(
                            $"call to type-returning generic '{templateSym.Name}': the `comptime {p.Name}: ?T` argument "
                            + "must be a comptime null or a compile-time-known payload");
                    }
                    mangleTokens.Add("opt" + (ov >= 0 ? ov.ToString(inv) : "n" + (-(System.Int128)ov).ToString(inv)));
                    optionalSeeds.Add((p.Name, true, ov, optP.Inner));
                }
            }
            else
            {
                if (_ir.ConstEval(LowerExpr(argItems[i])) is not { } vv)
                {
                    throw new IrUnsupportedException(
                        $"call to type-returning generic '{templateSym.Name}': the `comptime {p.Name}` argument "
                        + "must be a compile-time-known value");
                }
                mangleTokens.Add(vv >= 0 ? vv.ToString(inv) : "n" + (-(System.Int128)vv).ToString(inv));
                valueSeeds.Add((p.Name, vv, LowerType(p.TypeAst)));
            }
        }
        var mangled = mangleTokens.Count == 0 ? templateSym.Name : templateSym.Name + "__" + string.Join("_", mangleTokens);

        // Memoized — also short-circuits a self-referential field / recursive use, since the mapping is
        // installed BELOW before the fields are lowered.
        if (_containerTypes.TryGetValue(mangled, out var existing)) { return existing; }

        var mangledType = new CType.Named(mangled);
        var savedContainer = _currentContainer;
        var typeShadows = new List<(string name, CType? prev)>();
        // A scope for the value/optional comptime seeds (so the body's captured-if conditions + array
        // extents resolve); the type-param seeds ride _typeAliases (shadow-saved), like the W4 path.
        _symbols.EnterScope();
        try
        {
            foreach (var (name, type) in typeSeeds)
            {
                typeShadows.Add((name, _typeAliases.TryGetValue(name, out var pv) ? pv : (CType?)null));
                _typeAliases[name] = type;
            }
            foreach (var (name, value, type) in valueSeeds)
            {
                var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type });
                _comptimeVars[sym] = (value, type);
            }
            foreach (var (name, hasValue, value, inner) in optionalSeeds)
            {
                var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = new CType.Optional(inner) });
                _comptimeOptionalVars[sym] = (hasValue, value, inner);
            }
            // Process the body: leading `const NAME = <type>;` locals become scoped type aliases (the RHS
            // may be a captured-if that folds to a type — S4b pt2 / S4c), then the final `return struct {…}`.
            var fieldsItem = ProcessTypeReturningBody(templateSym.Name, info.Body, typeShadows);
            var (fields, methods, consts, containers) = fieldsItem is { } m
                ? SplitMembers(m)
                : (new List<Item>(), new List<Item>(), new List<Item>(), new List<Item>());
            if (methods.Count > 0 || consts.Count > 0 || containers.Count > 0)
            {
                throw new IrUnsupportedException(
                    $"type-returning generic '{templateSym.Name}': the returned `struct` is fields-only in V1 (wall-plan W4) — "
                    + "a method, `const`, or nested-container member in the returned type is not supported yet");
            }
            _containerTypes[mangled] = mangledType;   // memo + @This() target; BEFORE reify for self-ref
            _currentContainer = mangled;
            RegisterStruct(mangled, fields);
        }
        finally
        {
            _currentContainer = savedContainer;
            for (var i = typeShadows.Count - 1; i >= 0; i--)
            {
                var (name, prev) = typeShadows[i];
                if (prev is { } p) { _typeAliases[name] = p; } else { _typeAliases.Remove(name); }
            }
            _symbols.ExitScope();
        }
        return mangledType;
    }

    /// <summary>Process a type-returning generic's body (wall-plan W4, extended by road-to-zig-std S4c):
    /// zero or more leading <c>const NAME = &lt;type&gt;;</c> type-alias locals followed by the final
    /// <c>return struct {…};</c>. Each leading alias is resolved to a <see cref="CType"/> (its RHS may be a
    /// captured-<c>if</c> that folds to a type — <see cref="ResolveTypeReturningAliasRhs"/>) and registered
    /// into <see cref="_typeAliases"/> (shadow-saved via <paramref name="typeShadows"/>, restored by the
    /// caller), so a later alias / a field type can reference it. Returns the returned struct's
    /// <c>FieldDecls</c> item (or null for <c>return struct {};</c>). A non-const leading statement, a
    /// non-<c>return struct</c> tail, or an empty body is a loud cut.</summary>
    private Item? ProcessTypeReturningBody(string fnName, Item body, List<(string name, CType? prev)> typeShadows)
    {
        IReadOnlyList<Item> stmts = body.Content switch
        {
            Zig.Block b => Flatten(b.Arg1),
            _ => System.Array.Empty<Item>(),
        };
        if (stmts.Count == 0)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': an empty body — expected `[const NAME = <type>;]* return struct {{…}};`");
        }
        for (var i = 0; i < stmts.Count - 1; i++)
        {
            if (stmts[i].Content is not Zig.ConstDecl cd)
            {
                throw new IrUnsupportedException(
                    $"type-returning generic '{fnName}': a leading body statement must be a `const NAME = <type>;` "
                    + $"type alias (road-to-zig-std S4c) — got {stmts[i].Content?.GetType().Name ?? "null"}");
            }
            var aliasName = Tok(cd.Arg1);
            var aliasType = ResolveTypeReturningAliasRhs(fnName, aliasName, cd.Arg3);
            typeShadows.Add((aliasName, _typeAliases.TryGetValue(aliasName, out var pv) ? pv : (CType?)null));
            _typeAliases[aliasName] = aliasType;
        }
        return stmts[^1].Content switch
        {
            Zig.ReturnStructType rst => rst.Arg3,   // FieldDecls
            Zig.ReturnStructTypeEmpty => null,       // `return struct {};` — zero fields
            _ => throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': the body's final statement must be `return struct {{ … }};` "
                + "(wall-plan W4) — returning a non-struct type (a bare `T`, an enum / union) is not supported yet"),
        };
    }

    /// <summary>Resolve a type-returning body's <c>const NAME = &lt;rhs&gt;;</c> alias RHS to a
    /// <see cref="CType"/> (road-to-zig-std S4b pt2). A captured-<c>if</c> on a comptime-known optional
    /// (<c>if (opt) |x| TypeA else TypeB</c>) FOLDS to the taken branch's type — <c>null</c> → the else,
    /// a payload → the then with <c>x</c> bound to the literal; the branch is lowered as a TYPE. Any other
    /// RHS is lowered as an ordinary type expression. This is how <c>std.ArrayList</c>'s
    /// <c>Aligned(T, alignment)</c> selects <c>const Slice = if (alignment) |a| …align(a)… else []T;</c>.</summary>
    private CType ResolveTypeReturningAliasRhs(string fnName, string aliasName, Item rhs)
    {
        var cur = rhs;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is Zig.IfExprCapture ic && TryComptimeOptionalCond(ic.Arg2, out var copt))
        {
            if (!copt.HasValue) { return LowerType(ic.Arg9); }   // comptime null → the else-branch type
            _symbols.EnterScope();
            BindFoldedCapture(Tok(ic.Arg5), copt.Value, copt.Inner);
            var t = LowerType(ic.Arg7);                          // payload → the then-branch type, `x` bound
            _symbols.ExitScope();
            return t;
        }
        return LowerType(rhs);
    }
}
