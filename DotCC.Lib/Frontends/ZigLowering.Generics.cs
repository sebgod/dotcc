#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Generic functions — call-site monomorphization (wall-plan W3a). A <c>comptime</c>-value
/// parameter (<c>fn addN(comptime N: i32, x: i32) i32</c>) turns a function into a TEMPLATE: it is
/// NOT lowered once, because <c>N</c> has no runtime storage. Instead a call instantiates a
/// SPECIALIZED body per resolved comptime-argument tuple (C++-template-style monomorphization over
/// the retained AST), emitted under a deterministic mangled name (<c>addN__10</c>) and memoized by
/// key so a repeat call reuses it.
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
/// <para>SCOPING (the plan's queued obligation): a comptime value param reuses the existing
/// <c>comptime var</c> machinery — it is declared as an in-scope symbol with NO runtime decl and its
/// resolved value is recorded in <see cref="_comptimeVars"/>, so references substitute the literal
/// (see the <c>Zig.Ident</c> case + <see cref="ComptimeVarLit"/>). Because that map is keyed by SYMBOL
/// IDENTITY and each instantiation declares a FRESH param symbol, <c>N ↦ 10</c> and <c>N ↦ 100</c>
/// are inherently isolated with no leak — no per-instantiation frame stack needed for the value case.
/// (The fuller flat-maps→frames refactor becomes load-bearing for W3b type params, where a
/// <c>const U = T;</c> body alias would collide across instances.)</para>
///
/// <para>V1 SCOPE (loud cuts): comptime VALUE params only — a <c>comptime T: type</c> TYPE param is
/// W3b; a generic METHOD is rejected (W3a is free functions only); the param/return signature is
/// lowered at TEMPLATE time, so a value-dependent type (<c>fn f(comptime n: usize, a: [n]u8)</c>)
/// where a later param/return type references the comptime value is a cut (that needs
/// instantiation-time signature lowering — W3b); and the comptime argument must be
/// <see cref="IrBuilder.ConstEval"/>-able (a literal / arithmetic / <c>comptime var</c> / <c>sizeof</c>
/// / enum constant), NOT an arbitrary call — a call-valued comptime arg needs an explicit
/// <c>comptime f()</c>.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>One parameter of a function signature, carrying whether it is <c>comptime</c>-qualified
    /// (wall-plan W3a). The variadic marker <c>...</c> is tracked separately (it has no name/type).</summary>
    private readonly record struct ParamInfo(string Name, CType Type, bool IsComptime);

    /// <summary>A generic function's retained template — everything an instantiation needs to re-lower
    /// a specialized body: the template symbol (its runtime-param <see cref="CType.Func"/> signature,
    /// shared by every instance since W3a signatures don't depend on the comptime value), the FULL
    /// ordered parameter list (comptime + runtime, so a call splits args positionally), and the raw
    /// return-type + body ASTs (re-lowered per instance).</summary>
    private sealed record GenericFnInfo(
        Symbol Template,
        IReadOnlyList<ParamInfo> Params,
        Item RetType,
        bool ErrUnion,
        Item Body);

    /// <summary>A queued instantiation body to lower after pass 2. Drained at top level
    /// (re-entrancy-safe — see the class doc), so its <see cref="LowerFnBodyCore"/> runs in a clean
    /// between-functions state.</summary>
    private sealed record PendingInstantiation(
        Symbol Instance,
        GenericFnInfo Generic,
        IReadOnlyList<long> ComptimeValues);

    /// <summary>Generic (comptime-param) function symbols → their retained template. Populated in
    /// pass 1 (<see cref="DeclareFn"/>), consulted at every call site (<c>LowerCallInner</c>) so a
    /// call to a generic routes to <see cref="InstantiateGeneric"/> instead of a direct
    /// <see cref="BuildCall"/>.</summary>
    private readonly Dictionary<Symbol, GenericFnInfo> _genericFns = new();

    /// <summary>Memoized instantiations: the mangled instance name (which IS the instantiation key —
    /// deterministic from the template name + the resolved comptime-value tuple) → the instance
    /// <see cref="Symbol"/>. A repeat call with the same values reuses the one emitted body.</summary>
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
    /// <see cref="LowerFnBodyCore"/> when comptime seeds are present. It unlocks Zig's comptime
    /// control-flow inside the specialized body: an <c>if</c> whose condition is comptime-known
    /// (<see cref="IrBuilder.ConstEval"/> folds it, because a comptime param substitutes a literal) is
    /// folded to just its taken branch (<see cref="LowerIfStmt"/>), and code after a comptime-taken
    /// terminator is dropped as comptime-dead (<see cref="LowerStmtsWithDefers"/>). Together these let a
    /// recursive comptime generic (<c>fib</c>: <c>if (n &lt; 2) return n; return fib(n-1)+fib(n-2);</c>)
    /// prune its base case and terminate, instead of instantiating <c>fib(n-1)</c> forever. Gated to
    /// instance bodies so no ordinary (non-generic) lowering changes. A comptime-if in EXPRESSION
    /// position (a ternary <c>if</c>) is not folded yet — a recursive generic written that way would
    /// hit <see cref="MaxInstantiations"/> (a loud cut, not a miscompile).</summary>
    private bool _inGenericInstance;

    /// <summary>Lower the type of a <c>comptime</c> parameter, rejecting a <c>comptime T: type</c> TYPE
    /// parameter with a clear wall-plan W3b message (rather than W1's generic runtime-<c>type</c>
    /// rejection). A comptime VALUE param's type lowers like any other.</summary>
    private CType LowerComptimeParamType(Item typeItem, string paramName)
    {
        if (typeItem.Content is Zig.Ident id && Tok(id.Arg0) == "type")
        {
            throw new IrUnsupportedException(
                $"zig: a `comptime {paramName}: type` TYPE parameter is not supported yet (wall-plan W3b) — "
                + "W3a supports comptime VALUE parameters (`comptime n: <integer type>`) for now");
        }
        return LowerType(typeItem);
    }

    /// <summary>Instantiate (or reuse) a generic function at a call site (wall-plan W3a). Splits the
    /// arguments positionally over the template's parameters: each <c>comptime</c>-position argument is
    /// <see cref="IrBuilder.ConstEval"/>-folded to its value (a non-constant is a loud error); the
    /// resolved value tuple forms the mangled name (<c>fn__10</c>; a negative value spells <c>n10</c>),
    /// which is the memoization key. On first sight the instance symbol is declared (sharing the
    /// template's runtime-param signature) and its body enqueued for the post-pass-2 drain. The call
    /// itself lowers to a direct <see cref="BuildCall"/> of the instance passing only the RUNTIME
    /// arguments — the comptime ones are baked into the specialized body.</summary>
    private CExpr InstantiateGeneric(Symbol templateSym, GenericFnInfo g, IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != g.Params.Count)
        {
            throw new IrUnsupportedException(
                $"call to generic '{templateSym.Name}': expected {g.Params.Count} argument(s), got {argItems.Count}");
        }

        var inv = CultureInfo.InvariantCulture;
        var comptimeValues = new List<long>();
        var mangleTokens = new List<string>();
        var runtimeArgItems = new List<Item>();
        for (var i = 0; i < g.Params.Count; i++)
        {
            if (!g.Params[i].IsComptime)
            {
                runtimeArgItems.Add(argItems[i]);
                continue;
            }
            var argExpr = LowerExpr(argItems[i]);
            if (_ir.ConstEval(argExpr) is not { } v)
            {
                throw new IrUnsupportedException(
                    $"call to generic '{templateSym.Name}': the `comptime {g.Params[i].Name}` argument must be a "
                    + "compile-time-known integer constant (a literal / arithmetic / comptime value; wrap a call as `comptime f()`)");
            }
            comptimeValues.Add(v);
            // A negative value can't spell a C# identifier segment, so encode the sign; long.MinValue
            // has no positive `long`, so widen through Int128 for the magnitude.
            mangleTokens.Add(v >= 0 ? v.ToString(inv) : "n" + (-(System.Int128)v).ToString(inv));
        }

        var mangled = templateSym.Name + "__" + string.Join("_", mangleTokens);
        if (!_instantiations.TryGetValue(mangled, out var instanceSym))
        {
            if (++_instantiationCount > MaxInstantiations)
            {
                throw new IrUnsupportedException(
                    $"zig: generic instantiation budget ({MaxInstantiations}) exceeded while instantiating "
                    + $"'{mangled}' — a runaway recursive generic (an ever-changing comptime value)?");
            }
            instanceSym = _symbols.Declare(new Symbol
            {
                Name = mangled,
                Kind = SymKind.Func,
                Type = g.Template.Type,   // runtime-param signature — shared by every instance in W3a
                IsGlobal = true,
            });
            // An error-union generic: register the instance's raw return-type AST so its body resolves
            // its declared error set in LowerFnBodyCore (the same lazy resolution a plain fn gets).
            if (g.ErrUnion) { _fnErrorReturnTypes[instanceSym] = (g.RetType, g.ErrUnion); }
            _instantiations[mangled] = instanceSym;
            _pendingInstantiations.Add(new PendingInstantiation(instanceSym, g, comptimeValues));
        }
        return BuildCall(instanceSym, runtimeArgItems, receiver: null);
    }

    /// <summary>Lower one queued instantiation body (drained after pass 2). Derives the instance's
    /// runtime parameters + the comptime seeds (each comptime param name paired with its resolved
    /// value, positional) and hands them to <see cref="LowerFnBodyCore"/>, which declares each seed as
    /// an in-scope symbol recorded in <see cref="_comptimeVars"/> so the body substitutes the literal.
    /// Runs at top level (never nested), so the per-fn lowering state starts clean.</summary>
    private void LowerInstantiationBody(PendingInstantiation p)
    {
        var g = p.Generic;
        var runtimeParams = g.Params.Where(x => !x.IsComptime).Select(x => (x.Name, x.Type)).ToList();
        var comptimeParams = g.Params.Where(x => x.IsComptime).ToList();
        var seeds = new List<(string name, long value, CType type)>(comptimeParams.Count);
        for (var i = 0; i < comptimeParams.Count; i++)
        {
            seeds.Add((comptimeParams[i].Name, p.ComptimeValues[i], comptimeParams[i].Type));
        }
        LowerFnBodyCore(p.Instance, runtimeParams, g.Body, seeds);
    }
}
