#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Generic functions — call-site monomorphization (wall-plan W3a/W3b). A <c>comptime</c>
/// parameter turns a function into a TEMPLATE: it is NOT lowered once; a call instantiates a
/// SPECIALIZED body per resolved comptime-argument tuple (C++-template-style monomorphization over
/// the retained AST), emitted under a deterministic mangled name and memoized by key so a repeat call
/// reuses it. Two kinds of comptime parameter:
/// <list type="bullet">
/// <item><b>VALUE</b> (<c>comptime N: i32</c>, wall-plan W3a) — the resolved integer is baked into the
/// body as a literal; the signature does NOT depend on it, so it is lowered once at template time.</item>
/// <item><b>TYPE</b> (<c>comptime T: type</c>, wall-plan W3b) — the later parameter / return types
/// reference <c>T</c>, so the signature DEPENDS on the resolved type and CANNOT be lowered at template
/// time. Such a generic is lowered PER INSTANTIATION: at the call site the type argument resolves to a
/// concrete <see cref="CType"/>, seeded into <see cref="_typeAliases"/> so the signature (and later the
/// body) resolve <c>T</c> through <see cref="LowerTypeName"/>; the instance is mangled by the resolved
/// TYPE (<c>max__i32</c> / <c>max__f64</c>) — an alias for the same type keys the same instance.</item>
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
    }

    /// <summary>One parameter of a function signature, carrying its RAW type AST (lowered lazily — a
    /// type-param generic's runtime-parameter and return types depend on <c>T</c> and can only resolve
    /// per-instantiation, once <c>T</c> is bound) and its <see cref="ParamKind"/>. The variadic marker
    /// <c>...</c> is tracked separately (it has no name/type). For a <see cref="ParamKind.ComptimeType"/>
    /// param the <see cref="TypeAst"/> is the <c>type</c> keyword itself and is never lowered.</summary>
    private readonly record struct ParamInfo(string Name, Item TypeAst, ParamKind Kind)
    {
        /// <summary>True for either comptime kind — a monomorphization key, not a runtime slot.</summary>
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
        IReadOnlyList<(string name, CType type)> RuntimeParams);

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
        var runtimeArgItems = new List<Item>();

        // Phase 1 — resolve comptime TYPE args in the CALLER's environment (a type-arg spelled as an
        // alias resolves to its aliased type, so it keys the same instance as the underlying type).
        for (var i = 0; i < g.Params.Count; i++)
        {
            if (g.Params[i].Kind != ParamKind.ComptimeType) { continue; }
            var argType = LowerType(argItems[i]).Unqualified;
            typeSeeds.Add((g.Params[i].Name, argType));
        }

        // Phase 2 — seed the resolved type args (shadow-saved), so a later parameter / return type that
        // references `T`, and a comptime VALUE param whose type is `T`, resolve while we lower them.
        var typeShadows = new List<(string name, CType? prev)>();
        foreach (var (name, type) in typeSeeds)
        {
            typeShadows.Add((name, _typeAliases.TryGetValue(name, out var pv) ? pv : (CType?)null));
            _typeAliases[name] = type;
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
                // the return type may reference a type param. (For a value-only generic no type is seeded,
                // so this is exactly the W3a template-time signature.)
                var runtimeParams = g.Params
                    .Where(p => p.Kind == ParamKind.Runtime)
                    .Select(p => (p.Name, LowerType(p.TypeAst)))
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
                _pendingInstantiations.Add(new PendingInstantiation(instanceSym, g, valueSeeds, typeSeeds, runtimeParams));
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
        }
        // The runtime arguments are the CALLER's expressions — lower them (in BuildCall) in the restored
        // caller type env, coercing to the instance's now-concrete parameter types.
        return BuildCall(instanceSym, runtimeArgItems, receiver: null);
    }

    /// <summary>Lower one queued instantiation body (drained after pass 2). Hands the pre-resolved
    /// runtime parameters + the comptime VALUE seeds (each name paired with its resolved value) + the
    /// comptime TYPE seeds (each name paired with its resolved <see cref="CType"/>) to
    /// <see cref="LowerFnBodyCore"/>, which declares the value seeds as in-scope comptime symbols and
    /// seeds the type aliases (shadow-saved) so the body substitutes literals / resolves <c>T</c>. Runs
    /// at top level (never nested), so the per-fn lowering state starts clean.</summary>
    private void LowerInstantiationBody(PendingInstantiation p)
        => LowerFnBodyCore(p.Instance, p.RuntimeParams, p.Generic.Body, p.ValueSeeds, p.TypeSeeds);

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
}
