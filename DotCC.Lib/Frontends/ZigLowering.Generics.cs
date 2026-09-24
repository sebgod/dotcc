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
/// comptime VALUE argument must be <see cref="IrModule.ConstEval"/>-able, a comptime TYPE argument must
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
        Item Body,
        string? Owner = null);

    /// <summary>A queued instantiation body to lower after pass 2. Drained at top level
    /// (re-entrancy-safe — see the class doc), so its <see cref="LowerFnBodyCore"/> runs in a clean
    /// between-functions state. Carries the per-instance runtime parameter list + the comptime VALUE and
    /// TYPE seeds resolved at the call site, so the body lowers against the concrete signature.</summary>
    /// <summary>One resolved <c>comptime T: type</c> argument: the parameter name, the type it
    /// resolved to, and — when the source SPELLED an integer width — that declared width. The width
    /// travels with the seed because the lowered type cannot carry it: dotcc widens `uN`/`iN` to the
    /// smallest standard width, so `u21` and `u32` are the same `CType`. See
    /// <see cref="_declaredIntBits"/> for why this rides alongside the type rather than on it.</summary>
    private readonly record struct TypeSeed(string Name, CType Type, int? DeclaredBits);

    private sealed record PendingInstantiation(
        Symbol Instance,
        GenericFnInfo Generic,
        IReadOnlyList<(string name, long value, CType type)> ValueSeeds,
        IReadOnlyList<TypeSeed> TypeSeeds,
        IReadOnlyList<(string name, CType type)> RuntimeParams,
        IReadOnlyList<(string name, bool hasValue, long value, CType inner)> OptionalSeeds,
        IReadOnlyList<(string name, LitStr value)> StringSeeds,
        IReadOnlyList<(string name, ZigLowering owner, Symbol fn)>? FnSeeds = null);

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
    /// (<see cref="IrModule.ConstEval"/> folds it, because a comptime value param substitutes a literal)
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
    /// lowers; a comptime VALUE argument is <see cref="IrModule.ConstEval"/>-folded (a non-constant is a
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
        var (instanceSym, runtimeArgItems) = ResolveGenericInstance(templateSym, g, argItems, argScope: this);
        return FoldIfComptimeOnly(this, instanceSym, BuildCall(instanceSym, runtimeArgItems, receiver: null));
    }

    /// <summary>Instances whose declared return type is <c>comptime_int</c> (<c>std.math.maxInt</c>,
    /// <c>minInt</c>): zig evaluates every call at compile time, since the result has no runtime type.
    /// dotcc lowers the instance with an <see cref="CType.Int128"/> carrier (every <c>maxInt</c> /
    /// <c>minInt</c> of a width up to 127 bits fits) and folds each call (<see cref="FoldIfComptimeOnly"/>).</summary>
    private HashSet<Symbol> _comptimeOnlyFns => _moduleGraph?.ComptimeOnlyFns ?? _ownComptimeOnlyFns;

    /// <summary>The comptime-only set of a lowering built without a module graph (see
    /// <see cref="_comptimeOnlyFns"/>).</summary>
    private readonly HashSet<Symbol> _ownComptimeOnlyFns = new();

    /// <summary>Each comptime-only instance whose value was evaluated when it was instantiated
    /// (<see cref="TryEvalComptimeIntBody"/>) → that value, a spliced literal.</summary>
    private readonly Dictionary<Symbol, CExpr> _comptimeIntValues = new();

    /// <summary>Evaluate a <c>comptime_int</c> function's body NOW, with the instance's seeds live (the
    /// comptime-call engine's immediate path): <c>std.math.maxInt(usize)</c> as an enum member value
    /// (std.Io.Limit's <c>unlimited</c>) is needed during registration, before any deferred fold runs.
    /// The body may bind comptime <c>const</c>s (a <c>@typeInfo</c> value, a type alias, a folded
    /// scalar) and must then <c>return</c> a value the interpreter folds, as <c>maxInt</c> / <c>minInt</c>
    /// do. Anything else returns null and the call stays a deferred fold (V1), which fails loudly if it
    /// cannot fold either. Every binding made here is undone, so the caller's scope is untouched.</summary>
    private CExpr? TryEvalComptimeIntBody(GenericFnInfo g,
        IReadOnlyList<(string name, long value, CType type)> valueSeeds,
        IReadOnlyList<(string name, bool hasValue, long value, CType inner)> optionalSeeds)
    {
        var bound = new List<(string Name, ZigTypeInfo? Info, CType? Alias, int? Bits, CExpr? Value)>();
        _symbols.EnterScope();
        try
        {
            foreach (var (name, value, type) in valueSeeds)
            {
                _comptimeVars[_symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type })] = (value, type);
            }
            foreach (var (name, hasValue, value, inner) in optionalSeeds)
            {
                var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = new CType.Optional(inner) });
                _comptimeOptionalVars[sym] = (hasValue, value, inner);
            }
            using var hoist = EnterThrowawayHoist();
            foreach (var stmt in BodyStatements(g.Body))
            {
                switch (stmt.Content)
                {
                    case Zig.ConstDecl cd:
                    {
                        var name = Tok(cd.Arg1);
                        bound.Add((name, _typeInfoBindings.GetValueOrDefault(name), _typeAliases.GetValueOrDefault(name),
                                   _declaredIntBits.TryGetValue(name, out var pb) ? pb : null, _comptimeValues.GetValueOrDefault(name)));
                        if (TryComptimeConstBinding(name, cd.Arg3)) { break; }
                        if (_ir.ConstEval(LowerExpr(cd.Arg3)) is not { } v) { return null; }
                        var vt = CType.Long;
                        _comptimeVars[_symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = vt })] = (v, vt);
                        break;
                    }
                    case Zig.StmtReturn r:
                        return _ir.ResolveComptimeFold(LowerExprSink(r.Arg1, CType.Int128));
                    default:
                        return null;
                }
            }
            return null;
        }
        catch (IrUnsupportedException)
        {
            return null;   // not evaluable now: the call stays a deferred fold, which reports its own failure
        }
        finally
        {
            for (var i = bound.Count - 1; i >= 0; i--)
            {
                var (name, info, alias, bits, value) = bound[i];
                if (info is { } ti) { _typeInfoBindings[name] = ti; } else { _typeInfoBindings.Remove(name); }
                if (alias is { } a) { _typeAliases[name] = a; } else { _typeAliases.Remove(name); }
                SetDeclaredIntBits(name, bits);
                if (value is { } cv) { _comptimeValues[name] = cv; } else { _comptimeValues.Remove(name); }
            }
            _symbols.ExitScope();
        }
    }

    /// <summary>True when <paramref name="fn"/> is one of this module's GENERIC templates (a
    /// <c>comptime</c> / <c>anytype</c> parameter), which a call instantiates rather than calls.</summary>
    internal bool IsGenericTemplate(Symbol fn) => _genericFns.ContainsKey(fn);

    /// <summary>True when <paramref name="fn"/> is one of this module's comptime-only instances
    /// (<see cref="_comptimeOnlyFns"/>).</summary>
    internal bool IsComptimeOnlyFn(Symbol fn) => _comptimeOnlyFns.Contains(fn);

    /// <summary>Wrap a call to a comptime-only instance that <paramref name="owner"/> declares in a
    /// deferred <see cref="ComptimeFold"/>, queued for the graph's pass 3 (the comptime-call engine V1,
    /// road-to-zig-std G3): the shared interpreter runs the instance body once every module has drained
    /// and splices the literal, so <c>const m: u8 = std.math.maxInt(u8);</c> is <c>255</c>. Any other call
    /// is returned as is.</summary>
    private CExpr FoldIfComptimeOnly(ZigLowering owner, Symbol instance, CExpr call)
    {
        if (!owner.IsComptimeOnlyFn(instance)) { return call; }
        // Evaluated when it was instantiated (TryEvalComptimeIntBody): the value is known now, so a
        // position that needs it during lowering (an enum member, an array extent) can use it.
        if (owner._comptimeIntValues.TryGetValue(instance, out var known)) { return FitComptimeIntLiteral(known); }
        var fold = new ComptimeFold(call) { Type = call.Type };
        _pendingComptimeFolds.Add(fold);
        return fold;
    }

    /// <summary>A comptime_int literal typed wide enough for its value: <c>std.math.maxInt(u64)</c> folds to
    /// 18446744073709551615, which a <c>const max = …;</c> would otherwise declare as an <c>int</c>
    /// (std.math.sqrt_int). A value that fits its own type is returned unchanged.</summary>
    private static CExpr FitComptimeIntLiteral(CExpr literal)
    {
        if (literal is not LitInt { Digits: var digits } lit
            || !System.Int128.TryParse(digits, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            || v <= long.MaxValue)
        {
            return literal;
        }
        return lit with { Type = v <= ulong.MaxValue ? CType.ULong : CType.Int128 };
    }

    /// <summary>Instantiate a generic function THIS module exports, called from <paramref name="caller"/>
    /// through the module graph (<c>std.fmt.bufPrint(&amp;buf, "{d}", .{42})</c> — road-to-zig-std G3).
    /// The template, its signature and its instance body belong HERE (they resolve in this module's
    /// environment and drain with this module's pending bodies); the ARGUMENTS belong to the caller — a
    /// comptime type argument, a comptime value, an <c>anytype</c> argument's inferred type are all read
    /// in the caller's scope, exactly as <see cref="TryEvalExportedTypeReturningCall"/> reads a
    /// type-returning generic's. Returns the instance and the runtime argument items for the CALLER to
    /// lower in its own <see cref="BuildCall"/>, or null when <paramref name="sym"/> is not a generic
    /// template here (an ordinary function — the caller calls it directly).</summary>
    internal (Symbol Instance, IReadOnlyList<Item> RuntimeArgs)? TryResolveExportedGenericInstance(
        Symbol sym, IReadOnlyList<Item> argItems, ZigLowering caller)
        => _genericFns.TryGetValue(sym, out var g) ? ResolveGenericInstance(sym, g, argItems, caller) : null;

    /// <summary>Instantiate this module's GENERIC top-level function <paramref name="name"/> called as a
    /// method of its file-as-struct type on an instance (road-to-zig-std G3): <c>w.print(fmt, args)</c> is
    /// <c>print(w, fmt, args)</c> with <c>fn print(w: *Writer, comptime fmt: []const u8, args: anytype)</c>.
    /// The receiver item fills parameter 0, which must be a runtime (or <c>anytype</c>) parameter, and the
    /// rest are read in <paramref name="caller"/> as for any exported generic. Returns the instance and the
    /// runtime argument items AFTER the receiver (the caller passes the receiver itself, adjusted to the
    /// instance's first parameter), or null when <paramref name="name"/> is not a generic function here.</summary>
    internal (Symbol Instance, IReadOnlyList<Item> RuntimeArgs)? TryResolveFileStructGenericMethod(
        string name, Item receiverItem, IReadOnlyList<Item> argItems, ZigLowering caller)
    {
        if (_fileContainer is null || FileStructFnSymbol(name) is not { } sym || !_genericFns.TryGetValue(sym, out var g))
        {
            return null;
        }
        if (g.Params.Count == 0 || g.Params[0].Kind is not (ParamKind.Runtime or ParamKind.AnyType))
        {
            throw new IrUnsupportedException(
                $"'{_fileStem}.{name}' called on an instance needs a runtime first parameter to take the receiver");
        }
        var all = new List<Item>(argItems.Count + 1) { receiverItem };
        all.AddRange(argItems);
        var (instance, runtimeArgs) = ResolveGenericInstance(sym, g, all, caller);
        return (instance, runtimeArgs.Skip(1).ToList());
    }

    /// <summary>The body of <see cref="InstantiateGeneric"/>: resolve (or reuse) the instance a call
    /// selects, and return it with the runtime argument items still to be lowered. Every argument
    /// expression is read in <paramref name="argScope"/> — this module for a local call, the calling
    /// module for one reached through the module graph — while everything the template itself spells
    /// (parameter types, the return type) is read here.</summary>
    private (Symbol Instance, IReadOnlyList<Item> RuntimeArgs) ResolveGenericInstance(
        Symbol templateSym, GenericFnInfo g, IReadOnlyList<Item> argItems, ZigLowering argScope)
    {
        if (argItems.Count != g.Params.Count)
        {
            throw new IrUnsupportedException(
                $"call to generic '{templateSym.Name}': expected {g.Params.Count} argument(s), got {argItems.Count}");
        }
        // A generic METHOD's signature is spelled in its owner's scope, with the owner's comptime seeds live
        // (`key: K` in a HashMap instance); its body is drained there too (LowerInstantiationBody).
        var ownerSeedsKey = g.Owner is { } ownerName ? ReifiedAncestor(ownerName) : null;
        using var ownerSeedScope = EnterReifiedSeeds(ownerSeedsKey ?? "");
        using var ownerScope = EnterContainer(g.Owner ?? _currentContainer);

        var inv = CultureInfo.InvariantCulture;
        var mangleTokens = new List<string>();
        var typeSeeds = new List<TypeSeed>();
        var valueSeeds = new List<(string name, long value, CType type)>();
        var optionalSeeds = new List<(string name, bool hasValue, long value, CType inner)>();
        var stringSeeds = new List<(string name, LitStr value)>();
        var anytypeSeeds = new List<(string name, CType type)>();
        var anytypeBits = new Dictionary<string, int>(System.StringComparer.Ordinal);
        var anytypeTupleBits = new Dictionary<string, int?[]>(System.StringComparer.Ordinal);
        var fnSeeds = new List<(string name, ZigLowering owner, Symbol fn)>();
        var aggregateSeeds = new List<(string name, IrModule.ComptimeValue value, CType type)>();
        var comptimeIntArgs = new Dictionary<string, long>(System.StringComparer.Ordinal);
        var runtimeArgItems = new List<Item>();

        // Phase 1 — resolve each comptime TYPE arg in the CALLER's environment (a type-arg spelled as an
        // alias resolves to its aliased type, so it keys the same instance as the underlying type), and
        // INFER each `anytype` arg's type from the actual argument (`T := @TypeOf(arg)`, wall-plan W5).
        for (var i = 0; i < g.Params.Count; i++)
        {
            switch (g.Params[i].Kind)
            {
                case ParamKind.ComptimeType:
                    // The declared width rides the seed: `f(u21)` and `f(u32)` resolve to the SAME
                    // CType, so without it they would key one instance and share one `bits` answer.
                    typeSeeds.Add(new TypeSeed(g.Params[i].Name, argScope.LowerType(argItems[i]).Unqualified,
                                               argScope.DeclaredBitsOfTypeArg(argItems[i])));
                    break;
                // An `anytype` bound to a `comptime_int` (`log2(pos_max)` in std.math.IntFittingRange) is comptime:
                // zig instantiates per VALUE, and `@TypeOf(x)` is `comptime_int`, so it is a value seed here.
                case ParamKind.AnyType when argScope.ComptimeIntArgValue(argItems[i]) is { } ctIntArg:
                    comptimeIntArgs[g.Params[i].Name] = ctIntArg;
                    anytypeSeeds.Add((g.Params[i].Name, CType.ComptimeInt));
                    break;
                case ParamKind.AnyType:
                    anytypeSeeds.Add((g.Params[i].Name, argScope.InferArgType(argItems[i])));
                    // The argument's declared width, where its value carries one (road-to-zig-std G3).
                    if (argScope.DeclaredBitsOfArgument(argItems[i]) is { } argBits) { anytypeBits[g.Params[i].Name] = argBits; }
                    // A tuple literal's per-element widths (std.fmt's `.{42}`: `@field(args, "0")` is an `int`, 32 bits).
                    if (argScope.TupleLiteralElemBits(argItems[i]) is { } tupleBits) { anytypeTupleBits[g.Params[i].Name] = tupleBits; }
                    break;
            }
        }

        // Phase 2 — seed the resolved type args (shadow-saved), so a later parameter / return type that
        // references `T`, and a comptime VALUE param whose type is `T`, resolve while we lower them.
        var typeShadows = new List<(string name, CType? prev, int? prevBits)>();
        foreach (var (name, type, bits) in typeSeeds)
        {
            typeShadows.Add((name,
                             _typeAliases.TryGetValue(name, out var pv) ? pv : (CType?)null,
                             _declaredIntBits.TryGetValue(name, out var pb) ? pb : (int?)null));
            _typeAliases[name] = type;
            SetDeclaredIntBits(name, bits);
        }
        // Seed each inferred `anytype` type (shadow-saved) so a signature spelled `@TypeOf(param)` (a
        // return type or a later parameter) resolves through TypeOfBuiltin — the param is not yet an
        // in-scope symbol at signature-lowering time (it becomes one only in the instance body).
        var anytypeShadows = new List<(string name, CType? prev)>();
        var anytypeBitShadows = new List<(string name, int? prev)>();
        foreach (var (name, type) in anytypeSeeds)
        {
            anytypeShadows.Add((name, _anytypeSeeds.TryGetValue(name, out var pv) ? pv : (CType?)null));
            _anytypeSeeds[name] = type;
            anytypeBitShadows.Add((name, _anytypeSeedBits.TryGetValue(name, out var pb) ? pb : null));
            if (anytypeBits.TryGetValue(name, out var ab)) { _anytypeSeedBits[name] = ab; } else { _anytypeSeedBits.Remove(name); }
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
                        mangleTokens.Add(MangleTypeSeed(typeSeeds.First(s => s.Name == g.Params[i].Name)));
                        break;
                    case ParamKind.ComptimeValue:
                        // A comptime OPTIONAL value param `comptime x: ?T` (road-to-zig-std S4b): the arg
                        // is a comptime `null` (no runtime rep) or a comptime-known payload. Seed it into
                        // _comptimeOptionalVars so a captured `if (x) |y| … else …` folds at lowering time.
                        var valueParamType = LowerType(g.Params[i].TypeAst).Unqualified;
                        // A comptime FUNCTION value (`comptime lessThanFn: fn (…) bool`, std.mem.sort): the
                        // function it names keys the instance, and calls in the body go straight to it.
                        if (valueParamType is CType.Func)
                        {
                            if (TryResolveComptimeFnValue(argItems[i], argScope) is not { } fnValue)
                            {
                                throw new IrUnsupportedException(
                                    $"call to generic '{templateSym.Name}': the `comptime {g.Params[i].Name}` function argument must name "
                                    + "a function at compile time (a function, a comptime function parameter, or a closure-idiom call)");
                            }
                            mangleTokens.Add(fnValue.Fn.Name);
                            fnSeeds.Add((g.Params[i].Name, fnValue.Owner, fnValue.Fn));
                            break;
                        }
                        if (valueParamType is CType.Optional optParam)
                        {
                            if (!argScope.TryComptimeOptionalArg(argItems[i], out var hasOpt, out var ov))
                            {
                                throw new IrUnsupportedException(
                                    $"call to generic '{templateSym.Name}': the `comptime {g.Params[i].Name}: ?T` argument must be "
                                    + "a comptime `null` or a compile-time-known payload");
                            }
                            mangleTokens.Add(OptionalMangleToken(hasOpt, ov));
                            optionalSeeds.Add((g.Params[i].Name, hasOpt, ov, optParam.Inner));
                            break;
                        }
                        // A comptime STRING param `comptime fmt: []const u8` (road-to-zig-std G3 — the
                        // shape of every std formatting entry point): the argument must be a comptime
                        // string (a literal, a comptime const, a `++` fold). It has no integer value for
                        // ConstEval, so it keys the instance by a digest of its bytes and seeds the body
                        // as a comptime string, the way an `inline for` capture over field names is.
                        if (IsByteSliceOrArray(valueParamType))
                        {
                            if (argScope.EvalComptimeStringArg(argItems[i]) is not { } str)
                            {
                                throw new IrUnsupportedException(
                                    $"call to generic '{templateSym.Name}': the `comptime {g.Params[i].Name}` argument must be a "
                                    + "compile-time-known string (a literal, a comptime const, or a `++` / `**` fold)");
                            }
                            mangleTokens.Add(MangleComptimeString(str));
                            stringSeeds.Add((g.Params[i].Name, str));
                            break;
                        }
                        // A comptime STRUCT param (`comptime cpu: std.Target.Cpu` in std.simd.suggestVectorLengthForCpu,
                        // the target-identity segment T4): the interpreter's value of the argument keys the instance
                        // by a digest of its contents, and the body reads it as a comptime aggregate.
                        if (valueParamType is CType.Named && !_unions.ContainsKey(((CType.Named)valueParamType).Name))
                        {
                            var aggArg = argScope.LowerExprSink(argItems[i], valueParamType);
                            if (_ir.EvalComptimeValue(aggArg) is not { } aggValue)
                            {
                                throw new IrUnsupportedException(
                                    $"call to generic '{templateSym.Name}': the `comptime {g.Params[i].Name}` argument must be a "
                                    + "compile-time-known struct value" + (_ir.ComptimeMiss is { } why ? $" (the interpreter stopped at {why})" : ""));
                            }
                            mangleTokens.Add("c" + IrModule.ComptimeDigest(aggValue));
                            aggregateSeeds.Add((g.Params[i].Name, aggValue, valueParamType));
                            break;
                        }
                        // An ENUM-typed param (`comptime sign: enum { pos, neg }`) is the result location
                        // its bare `.pos` argument resolves against; zig result-locates it the same way.
                        var argExpr = valueParamType is CType.Enum
                            ? argScope.LowerExprSink(argItems[i], valueParamType)
                            : argScope.LowerExpr(argItems[i]);
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
                    case ParamKind.AnyType when comptimeIntArgs.TryGetValue(g.Params[i].Name, out var ctInt):
                        mangleTokens.Add("ci" + (ctInt >= 0 ? ctInt.ToString(inv) : "n" + (-(System.Int128)ctInt).ToString(inv)));
                        valueSeeds.Add((g.Params[i].Name, ctInt, CType.ComptimeInt));
                        break;
                    case ParamKind.AnyType:
                        // A hybrid (wall-plan W5): its inferred type keys the specialization AND the
                        // argument is passed at runtime — so it contributes BOTH a mangle token and a
                        // runtime argument (unlike a comptime TYPE arg, which is compile-time-only).
                        // A declared width other than the lowered type's own (`u21` in a `uint`) keys its own
                        // instance, since `@typeInfo(@TypeOf(x)).int.bits` differs between them.
                        var anyType = _anytypeSeeds[g.Params[i].Name];
                        mangleTokens.Add(MangleType(anyType)
                            + (anytypeBits.TryGetValue(g.Params[i].Name, out var mb) && anyType.Unqualified is CType.Prim { Integer: true } mp
                               && mb != mp.Bytes * 8 ? "w" + mb.ToString(inv) : ""));
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
                // The comptime VALUE / OPTIONAL seeds are bound while the signature lowers, so a type spelled
                // with one resolves (array_list's `… !SentinelSlice(sentinel)` for `comptime sentinel: T`).
                List<(string, CType)> runtimeParams;
                // `comptime_int` and `?comptime_int` (std.simd.suggestVectorLength) results exist only at compile time:
                // every call folds, and the instance is dropped from the program (see _comptimeOnlyFns).
                var comptimeOnly = !g.ErrUnion && (IsComptimeIntType(g.RetType)
                    || g.RetType.Content is Zig.TyOptional { Arg1: var optRet } && IsComptimeIntType(optRet));
                CType ret;
                _symbols.EnterScope();
                try
                {
                    foreach (var (name, value, type) in valueSeeds)
                    {
                        _comptimeVars[_symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type })] = (value, type);
                    }
                    foreach (var (name, hasValue, value, inner) in optionalSeeds)
                    {
                        var optSym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = new CType.Optional(inner) });
                        _comptimeOptionalVars[optSym] = (hasValue, value, inner);
                    }
                    runtimeParams = g.Params
                        .Where(p => p.Kind is ParamKind.Runtime or ParamKind.AnyType && !comptimeIntArgs.ContainsKey(p.Name))
                        .Select(p => (p.Name, p.Kind == ParamKind.AnyType ? _anytypeSeeds[p.Name] : LowerType(p.TypeAst)))
                        .ToList();
                    ret = !comptimeOnly ? LowerType(g.RetType)
                        : g.RetType.Content is Zig.TyOptional ? new CType.Optional(CType.Int128) : CType.Int128;
                    // `@TypeOf(x)` of a `comptime_int` argument (std.math.log2): the result is comptime-only too.
                    if (ret.Unqualified is CType.Prim { IsComptimeInt: true } && !g.ErrUnion)
                    {
                        comptimeOnly = true;
                        ret = CType.Int128;
                    }
                }
                finally
                {
                    _symbols.ExitScope();
                }
                if (g.ErrUnion) { ret = new CType.ErrorUnion(ret); }
                instanceSym = DeclareFnSymbol(new Symbol
                {
                    Name = mangled,
                    Kind = SymKind.Func,
                    Type = new CType.Func(ret, runtimeParams.Select(p => p.Item2).ToList(), false),
                    IsGlobal = true,
                }, qualify: g.Owner is null);   // a method's mangled name carries its (qualified) owner
                // An error-union generic: register the instance's raw return-type AST so its body resolves
                // its declared error set in LowerFnBodyCore (the same lazy resolution a plain fn gets).
                if (ret is CType.ErrorUnion) { _fnErrorReturnTypes[instanceSym] = (g.RetType, g.ErrUnion); }
                // The closure idiom (`fn asc(comptime T: type) fn (…) bool { return struct { … }.inner; }`):
                // reify the anonymous struct NOW, with this instance's seeds live, so a comptime function
                // argument can name the method before the instance body is drained.
                if (ret.Unqualified is CType.Func && ClosureIdiomReturn(g.Body) is { } closure)
                {
                    _fnValueOfInstance[instanceSym] = ReifyClosureStruct(mangled, closure.Arg3, Tok(closure.Arg6),
                        typeSeeds, valueSeeds, optionalSeeds);
                }
                if (comptimeOnly)
                {
                    _comptimeOnlyFns.Add(instanceSym);
                    if (TryEvalComptimeIntBody(g, valueSeeds, optionalSeeds) is { } value) { _comptimeIntValues[instanceSym] = value; }
                }
                _instantiations[mangled] = instanceSym;
                _fnParamInfos[instanceSym] = g.Params;
                if (DeclaredBitsOfTypeArg(g.RetType) is { } instRetBits) { _fnReturnBits[instanceSym] = instRetBits; }
                if (anytypeBits.Count > 0) { _instanceAnytypeBits[instanceSym] = anytypeBits; }
                if (anytypeTupleBits.Count > 0) { _instanceAnytypeTupleBits[instanceSym] = anytypeTupleBits; }
                // A method instance's body re-enters its owner's seeds too, its own LAST so they win a clash.
                if (ownerSeedsKey is { } osk && _reifiedSeeds.TryGetValue(osk, out var os))
                {
                    typeSeeds = [.. os.Types, .. typeSeeds];
                    valueSeeds = [.. os.Values, .. valueSeeds];
                    optionalSeeds = [.. os.Optionals, .. optionalSeeds];
                }
                _pendingInstantiations.Add(new PendingInstantiation(instanceSym, g, valueSeeds, typeSeeds, runtimeParams, optionalSeeds, stringSeeds, fnSeeds));
                if (aggregateSeeds.Count > 0) { _instanceAggregateSeeds[instanceSym] = aggregateSeeds; }
            }
        }
        finally
        {
            // Restore the caller's type env — the seeds are re-applied per instance at drain time.
            for (var i = typeShadows.Count - 1; i >= 0; i--)
            {
                var (name, prev, prevBits) = typeShadows[i];
                if (prev is { } p) { _typeAliases[name] = p; } else { _typeAliases.Remove(name); }
                SetDeclaredIntBits(name, prevBits);
            }
            // Restore the `anytype` seeds (W5) — the instance BODY resolves each such param through its
            // in-scope symbol (declared with the inferred type in `runtimeParams`), so the seed is only
            // needed for the signature lowering here.
            for (var i = anytypeShadows.Count - 1; i >= 0; i--)
            {
                var (name, prev) = anytypeShadows[i];
                if (prev is { } p) { _anytypeSeeds[name] = p; } else { _anytypeSeeds.Remove(name); }
            }
            for (var i = anytypeBitShadows.Count - 1; i >= 0; i--)
            {
                var (name, prev) = anytypeBitShadows[i];
                if (prev is { } pb) { _anytypeSeedBits[name] = pb; } else { _anytypeSeedBits.Remove(name); }
            }
        }
        // The runtime arguments are the CALLER's expressions — lowered (in BuildCall) in the restored
        // caller type env, coercing to the instance's now-concrete parameter types. An `anytype`
        // argument is among them (a runtime slot), coerced to its inferred parameter type.
        return (instanceSym, runtimeArgItems);
    }

    /// <summary>True for a byte slice or byte array type (<c>[]const u8</c>, <c>[N]u8</c>) — the declared
    /// type of a comptime STRING parameter.</summary>
    private static bool IsByteSliceOrArray(CType t) => t switch
    {
        CType.Slice s => IsByte(s.Element),
        CType.Array a => IsByte(a.Element),
        _ => false,
    };

    /// <summary>True for an 8-bit integer element type (<c>u8</c>/<c>i8</c>, qualifiers ignored).</summary>
    private static bool IsByte(CType t) => t.Unqualified is CType.Prim { Bytes: 1, Integer: true };

    /// <summary>Mangle a comptime STRING argument into an identifier-safe instance-name token: <c>s</c>
    /// plus a 32-bit FNV-1a digest of the literal's source segments. A digest rather than the text itself,
    /// since a format string is arbitrary bytes and can be long; deterministic, so the same string keys
    /// the same instance in every build. A collision would silently share one instance between two
    /// strings, so a second string hashing to a taken token is caught by <see cref="_stringInstanceKeys"/>.</summary>
    private string MangleComptimeString(LitStr str)
    {
        var text = string.Join("\0", str.Segments);
        var h = 2166136261u;
        foreach (var ch in text) { h = unchecked((h ^ ch) * 16777619u); }
        var token = "s" + h.ToString("x8", CultureInfo.InvariantCulture);
        if (_stringInstanceKeys.TryGetValue(token, out var seen) && seen != text)
        {
            throw new IrUnsupportedException(
                $"zig: two comptime string arguments hash to the same instance token '{token}' — rename one of them");
        }
        _stringInstanceKeys[token] = text;
        return token;
    }

    /// <summary>Each comptime-string mangle token → the string it was minted for, so a digest collision
    /// fails loudly instead of sharing an instance (see <see cref="MangleComptimeString"/>).</summary>
    private readonly Dictionary<string, string> _stringInstanceKeys = new(System.StringComparer.Ordinal);

    /// <summary>Infer an <c>anytype</c> argument's type at a call site (wall-plan W5) — Zig's
    /// <c>@TypeOf(actual arg)</c>. Lowers the argument expression into a THROWAWAY hoist buffer (like
    /// <see cref="TypeOfBuiltin"/>) purely to read its synthesized <see cref="CType"/>; the real
    /// argument is lowered AGAIN in <see cref="BuildCall"/> for the runtime call, so any side effect is
    /// emitted exactly once (the inference lowering here is discarded).</summary>
    private CType InferArgType(Item argItem)
    {
        using var _ = EnterThrowawayHoist();   // the inference lowering is discarded
        return (LowerExpr(argItem).Type
            ?? throw new IrUnsupportedException("zig `anytype` argument has no statically known type")).Unqualified;
    }

    /// <summary>Each local <c>const</c> initialized by an untyped integer literal: a <c>comptime_int</c> in zig, lowered
    /// on an <c>int</c> carrier (see <see cref="ComptimeIntArgValue"/>).</summary>
    private readonly HashSet<Symbol> _comptimeIntLocals = new();

    /// <summary>The value of an argument whose zig type is <c>comptime_int</c>: an untyped integer literal, or
    /// an expression of the <see cref="CType.ComptimeInt"/> type (a <c>comptime_int</c> parameter, a capture of
    /// one, arithmetic over those), when it evaluates at compile time. Null for anything else, including a
    /// value outside the 64-bit range comptime seeds carry today.</summary>
    private long? ComptimeIntArgValue(Item argItem)
    {
        if (argItem.Content is Zig.Grouped g) { return ComptimeIntArgValue(g.Arg1); }
        using var _ = EnterThrowawayHoist();
        CExpr lowered;
        try { lowered = LowerExpr(argItem); }
        catch (IrUnsupportedException) { return null; }
        var untypedConst = argItem.Content is Zig.Ident { Arg0: var constTok } && _symbols.Resolve(Tok(constTok)) is { } constSym
                           && _comptimeIntLocals.Contains(constSym);
        if (argItem.Content is not Zig.IntLit && !untypedConst && lowered.Type?.Unqualified is not CType.Prim { IsComptimeInt: true })
        {
            return null;
        }
        return _ir.ConstEval(lowered)
            ?? (_ir.EvalComptimeValue(lowered) is IrModule.CtInt { Value: var big } && big >= long.MinValue && big <= long.MaxValue
                ? (long)big : null);
    }

    /// <summary>Lower one queued instantiation body (drained after pass 2). Hands the pre-resolved
    /// runtime parameters + the comptime VALUE seeds (each name paired with its resolved value) + the
    /// comptime TYPE seeds (each name paired with its resolved <see cref="CType"/>) to
    /// <see cref="LowerFnBodyCore"/>, which declares the value seeds as in-scope comptime symbols and
    /// seeds the type aliases (shadow-saved) so the body substitutes literals / resolves <c>T</c>. Runs
    /// at top level (never nested), so the per-fn lowering state starts clean.</summary>
    /// <summary>Each instance's comptime STRUCT parameters (<c>comptime cpu: std.Target.Cpu</c>) with their values,
    /// bound as comptime aggregates while its body lowers (<see cref="LowerInstantiationBody"/>).</summary>
    private readonly Dictionary<Symbol, List<(string name, IrModule.ComptimeValue value, CType type)>> _instanceAggregateSeeds = new();

    /// <summary>True for the bare type name <c>comptime_int</c>.</summary>
    private static bool IsComptimeIntType(Item type) => type.Content is Zig.Ident id && Tok(id.Arg0) == "comptime_int";

    private void LowerInstantiationBody(PendingInstantiation p)
    {
        // A comptime struct parameter is a comptime aggregate the body reads (`cpu.arch`, `cpu.has(…)`): bound in a
        // scope around the body, like `const x = comptime f();` of one.
        var aggregateScope = _instanceAggregateSeeds.TryGetValue(p.Instance, out var aggregates);
        if (aggregateScope)
        {
            _symbols.EnterScope();
            foreach (var (name, value, type) in aggregates ?? [])
            {
                var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type });
                _ir.ComptimeGlobals[sym] = value;
            }
        }
        try
        {
            LowerInstantiationBodyCore(p);
        }
        finally
        {
            if (aggregateScope) { _symbols.ExitScope(); }
        }
    }

    private void LowerInstantiationBodyCore(PendingInstantiation p)
    {
        // A `comptime f: fn (…) R` parameter is bound to the function it was given for this instance's body
        // (a call `f(…)` resolves through `_fnAliases`); the caller's own aliases are put back afterwards.
        var shadows = new List<(string name, (ZigLowering, Symbol)? prev)>();
        foreach (var (name, owner, fn) in p.FnSeeds ?? System.Array.Empty<(string, ZigLowering, Symbol)>())
        {
            shadows.Add((name, _fnAliases.TryGetValue(name, out var prev) ? prev : null));
            _fnAliases[name] = (owner, fn);
        }
        var outerInstantiation = _currentInstantiation;
        _currentInstantiation = p;
        try
        {
            // A generic METHOD's instance lowers inside its owner (`Self`, sibling methods, nested types).
            using var container = EnterContainer(p.Generic.Owner ?? _currentContainer);
            LowerFnBodyCore(p.Instance, p.RuntimeParams, p.Generic.Body, p.ValueSeeds, p.TypeSeeds, p.OptionalSeeds, p.StringSeeds);
        }
        finally
        {
            _currentInstantiation = outerInstantiation;
            foreach (var (name, prev) in shadows)
            {
                if (prev is { } pv) { _fnAliases[name] = pv; } else { _fnAliases.Remove(name); }
            }
        }
    }

    /// <summary>The generic instance whose body is lowering right now, if any: a LOCAL struct declared in it
    /// (<see cref="LowerLocalStruct"/>) hands its comptime seeds to the struct's methods.</summary>
    private PendingInstantiation? _currentInstantiation;

    /// <summary>Each instance whose body returns a method of an anonymous struct (the closure idiom,
    /// <c>return struct { pub fn inner … }.inner;</c>) → that method: the comptime FUNCTION value the
    /// instance stands for (<c>std.sort.asc(u8)</c>).</summary>
    private readonly Dictionary<Symbol, Symbol> _fnValueOfInstance = new();

    /// <summary>The <c>return struct { … }.member;</c> of a closure-idiom body, or null. Leading <c>comptime { … }</c>
    /// blocks (hash_map's getAutoHashFn asserts and raises its `@compileError` there) are analysis-only and
    /// may precede it.</summary>
    private static Zig.ReturnStructMember? ClosureIdiomReturn(Item body)
    {
        var stmts = BodyStatements(body);
        if (stmts.Count == 0 || stmts[^1].Content is not Zig.ReturnStructMember r) { return null; }
        for (var i = 0; i < stmts.Count - 1; i++)
        {
            if (stmts[i].Content is not Zig.ComptimeBlock) { return null; }
        }
        return r;
    }

    /// <summary>Reify the anonymous struct of a closure-idiom <c>return struct { … }.member;</c> in the scope
    /// of function (or instance) <paramref name="owner"/>, and return the method <paramref name="member"/>.
    /// Memoized per owner (<c>&lt;owner&gt;__Anon</c>). Methods are declared now, while any comptime seeds
    /// are live, and their bodies deferred with those seeds, as a reified generic's methods are. Fields are a
    /// loud cut: the idiom's struct is a namespace for its functions.</summary>
    private Symbol ReifyClosureStruct(string owner, Item fieldDecls, string member,
        IReadOnlyList<TypeSeed> typeSeeds,
        IReadOnlyList<(string name, long value, CType type)> valueSeeds,
        IReadOnlyList<(string name, bool hasValue, long value, CType inner)> optionalSeeds)
    {
        var anon = owner + "__Anon";
        if (!_containerTypes.ContainsKey(anon))
        {
            var (fields, methods, consts, containers) = SplitMembers(fieldDecls);
            if (fields.Count > 0 || containers.Count > 0)
            {
                throw new IrUnsupportedException(
                    $"zig `return struct {{ … }}.{member};` in '{owner}': the anonymous struct may declare only functions and "
                    + "consts (the closure idiom), not fields or nested containers");
            }
            _containerTypes[anon] = new CType.Named(anon);
            using var container = EnterContainer(anon);
            RegisterStruct(anon, fields);
            RegisterContainerConsts(anon, consts);
            foreach (var methodDef in methods)
            {
                var me = DeclareMethod(anon, methodDef);
                _currentContainer = anon;
                if (IsFnTemplate(me.sym)) { continue; }
                _pendingReifiedMethods.Add(new PendingReifiedMethod(me.sym, anon, me.ps, me.body, typeSeeds, valueSeeds, optionalSeeds));
            }
        }
        return _methods.TryGetValue(anon, out var ms) && ms.TryGetValue(member, out var sym)
            ? sym
            : throw new IrUnsupportedException($"zig `return struct {{ … }}.{member};` in '{owner}': the struct declares no function '{member}'");
    }

    /// <summary>The function a COMPTIME function-typed argument names (<c>comptime lessThanFn: fn (…) bool</c>
    /// given <c>std.sort.asc(u8)</c>, a function name, or a function parameter passed along), read in
    /// <paramref name="argScope"/>: the owning module and the function's symbol, or null.</summary>
    private (ZigLowering Owner, Symbol Fn)? TryResolveComptimeFnValue(Item arg, ZigLowering argScope)
    {
        switch (arg.Content)
        {
            case Zig.Grouped g:
                return TryResolveComptimeFnValue(g.Arg1, argScope);
            case Zig.Ident id:
            {
                var name = Tok(id.Arg0);
                if (argScope._fnAliases.TryGetValue(name, out var alias)) { return alias; }
                var sym = argScope._symbols.Resolve(name) ?? (argScope._lazy ? argScope.EnsureDeclLowered(name) : null);
                return sym is { Kind: SymKind.Func } && !argScope._genericFns.ContainsKey(sym) ? (argScope, sym) : null;
            }
            case Zig.CallArgs or Zig.CallNoArgs:
            {
                var (callee, args) = arg.Content switch
                {
                    Zig.CallArgs ca => (ca.Arg0, (IReadOnlyList<Item>)Flatten(ca.Arg2)),
                    Zig.CallNoArgs cn => (cn.Arg0, (IReadOnlyList<Item>)System.Array.Empty<Item>()),
                    _ => throw new System.InvalidOperationException(),
                };
                (ZigLowering owner, Symbol template)? target = callee.Content switch
                {
                    Zig.Ident cid when argScope._symbols.Resolve(Tok(cid.Arg0)) is { } s && argScope._genericFns.ContainsKey(s) => (argScope, s),
                    Zig.Ident cid when argScope._symbols.Resolve(Tok(cid.Arg0)) is null && argScope._lazy
                                    && argScope.EnsureDeclLowered(Tok(cid.Arg0)) is { } ls && argScope._genericFns.ContainsKey(ls) => (argScope, ls),
                    Zig.Field cf when argScope.ResolveModulePath(cf.Arg0)?.Lowering is { } mod
                                   && mod.ResolveExportedDecl(Tok(cf.Arg2)) is { } d && d.Owner.IsGenericTemplate(d.Sym) => (d.Owner, d.Sym),
                    _ => null,
                };
                if (target is not { } t || t.owner.TryResolveExportedGenericInstance(t.template, args, argScope) is not { } inst)
                {
                    return null;
                }
                return t.owner._fnValueOfInstance.TryGetValue(inst.Instance, out var fnValue) ? (t.owner, fnValue) : null;
            }
            // `ByMod.less` (a comparator passed to std.mem.sort): a method named through its container.
            case Zig.Field { Arg0.Content: Zig.Ident { Arg0: var typeTok }, Arg2: var memberTok }
                when argScope._containerTypes.TryGetValue(Tok(typeTok), out var containerType)
                     && ContainerTypeName(containerType) is { } containerName
                     && argScope._methods.TryGetValue(containerName, out var containerMethods)
                     && containerMethods.TryGetValue(Tok(memberTok), out var method)
                     && !argScope._genericFns.ContainsKey(method):
                return (argScope, method);
            default:
                return null;
        }
    }

    /// <summary>The body of <see cref="InstantiateGeneric"/>: resolve (or reuse) the instance a call

    /// <summary>True when a generic argument is a comptime <c>null</c> — a bare <c>null</c> literal
    /// (optionally parenthesized). The comptime-optional seed for such an argument has no payload.</summary>
    /// <summary>Read a comptime OPTIONAL argument in this (the caller's) scope: a <c>null</c> literal, the
    /// caller's own comptime optional seed passed on by name (array_list's <c>Aligned(T, alignment)</c>
    /// forwarding <c>alignment</c> to <c>AlignedManaged</c>), or a compile-time-known payload. False when it
    /// is none of those.</summary>
    private bool TryComptimeOptionalArg(Item arg, out bool hasValue, out long value)
    {
        hasValue = false;
        value = 0;
        if (IsComptimeNull(arg)) { return true; }
        var cur = arg;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is Zig.Ident id && _symbols.Resolve(Tok(id.Arg0)) is { } sym
            && _comptimeOptionalVars.TryGetValue(sym, out var seeded))
        {
            hasValue = seeded.HasValue;
            value = seeded.Value;
            return true;
        }
        if (_ir.ConstEval(LowerExpr(arg)) is not { } v) { return false; }
        hasValue = true;
        value = v;
        return true;
    }

    /// <summary>The instance-name token of a comptime optional argument: <c>optnull</c>, or <c>opt</c> and
    /// the payload (<c>n</c> marks a negative one).</summary>
    private static string OptionalMangleToken(bool hasValue, long value)
    {
        var inv = CultureInfo.InvariantCulture;
        return !hasValue ? "optnull" : "opt" + (value >= 0 ? value.ToString(inv) : "n" + (-(System.Int128)value).ToString(inv));
    }

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
            CType.Prim { IsComptimeInt: true } => "comptime_int",   // not `i128`, which is another type
            CType.Prim p when p.IsInteger => (p.Signed ? "i" : "u") + (p.Bytes * 8).ToString(CultureInfo.InvariantCulture),
            CType.Prim p => "f" + (p.Bytes * 8).ToString(CultureInfo.InvariantCulture),
            CType.VoidType => "void",
            CType.Named n => n.Name,
            CType.Enum e => e.Name,
            CType.Pointer ptr => "p_" + MangleType(ptr.Pointee),
            CType.Vector v => "v" + v.Count.ToString(CultureInfo.InvariantCulture) + "_" + MangleType(v.Element),
            _ => SanitizeIdent(t.Describe()),
        };
    }

    /// <summary>Mangle a resolved comptime-TYPE argument for the instance key, honouring the DECLARED
    /// integer width when the source spelled one that the lowered type cannot represent. <see
    /// cref="MangleType"/> keys an integer by its LOWERED width (<c>Bytes * 8</c>), so <c>u21</c> and
    /// <c>u32</c> — both lowered to <c>uint</c> — would mangle identically and share one memoized
    /// instance; whichever instantiated first would then dictate the other's
    /// <c>@typeInfo(T).int.bits</c>. Keying by the declared width instead makes them distinct
    /// instances, which is also what zig means (they ARE different types). Every STANDARD spelling
    /// declares exactly its lowered width, so this is byte-identical to <see cref="MangleType"/>
    /// there — no existing instance name changes.</summary>
    private static string MangleTypeSeed(TypeSeed seed)
    {
        if (seed.DeclaredBits is { } bits
            && seed.Type.Unqualified is CType.Prim { Integer: true, Name: not "_Bool" } p
            && bits != p.Bytes * 8)
        {
            return (p.Signed ? "i" : "u") + bits.ToString(CultureInfo.InvariantCulture);
        }
        return MangleType(seed.Type);
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
    /// (all-comptime-TYPE) params, and the raw body AST (a single <c>return struct {…}</c>). A container
    /// MEMBER carries its <c>Owner</c> container, whose scope and comptime seeds its body is evaluated in.</summary>
    private readonly record struct TypeReturningGenericInfo(Symbol Template, IReadOnlyList<ParamInfo> Params, Item Body,
        string? Owner = null);

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
            type = EvalTypeReturningCall(sym, info, args, out var localBits);
            RecordTypeCallBits(maybeCall, localBits);
            return true;
        }
        // A type-returning METHOD: named bare inside its container or one it encloses (hash_map's
        // `FieldIterator(K)`), or through a container type (`Self.SentinelSlice(s)`).
        if (TypeReturningMethodCallee(calleeItem) is { } methodSym
            && _typeReturningGenerics.TryGetValue(methodSym, out var methodInfo))
        {
            type = EvalTypeReturningCall(methodSym, methodInfo, args, out var methodBits);
            RecordTypeCallBits(maybeCall, methodBits);
            return true;
        }
        // A SIBLING in a lazy module that is not declared yet (hash_map.zig's `AutoHashMap` body calls
        // `HashMap(…)`), or a re-export of a type-returning generic: declare it on demand in whichever
        // module owns it, and evaluate it there (a skipped declaration raises its parse error instead).
        if (calleeItem.Content is Zig.Ident sid
            && _symbols.Resolve(Tok(sid.Arg0)) is null
            && ResolveExportedDecl(Tok(sid.Arg0)) is { Owner: var owner, Sym: var siblingSym }
            && owner._typeReturningGenerics.TryGetValue(siblingSym, out var siblingInfo))
        {
            type = owner.EvalTypeReturningCall(siblingSym, siblingInfo, args, out var siblingBits, typeArgScope: this);
            RecordTypeCallBits(maybeCall, siblingBits);
            return true;
        }
        // A type-returning method of a container ANOTHER module declares, reached through a type alias or a
        // module path (`CpuFeature.FeatureSetFns(Feature)` with `const CpuFeature = std.Target.Cpu.Feature;`
        // in std/Target/x86.zig): reified in the owning module, its type arguments read here.
        if (calleeItem.Content is Zig.Field foreignField
            && ForeignContainerOwner(foreignField.Arg0) is ({ } foreignOwner, { } foreignContainer)
            // Through a FILE-as-struct type (`FloatInfo.from(T)` in std.fmt.parse_float, FloatInfo.zig): the symbol is
            // read without declaring it, since a value-returning generic reached this way is not a type (and the
            // declaring path rejects any non-call use of one), so the probe must just answer no.
            && (foreignContainer == foreignOwner._fileContainer
                    ? foreignOwner.FileStructFnSymbol(Tok(foreignField.Arg2))
                    : foreignOwner.EnsureMethodDeclared(foreignContainer, Tok(foreignField.Arg2))) is { } foreignSym
            && foreignOwner._typeReturningGenerics.TryGetValue(foreignSym, out var foreignInfo))
        {
            type = foreignOwner.EvalTypeReturningCall(foreignSym, foreignInfo, args, out var foreignBits, typeArgScope: this);
            RecordTypeCallBits(maybeCall, foreignBits);
            return true;
        }
        // A MODULE-QUALIFIED callee (`array_list.Aligned(u8)`, `std.array_list.Aligned(u8)`) — the
        // type-position half of module-graph navigation (road-to-zig-std S4d). The template lives in the
        // imported module, so the reification runs THERE (its body's types resolve in its own
        // environment); only the comptime TYPE arguments resolve here, in the caller's, since the
        // argument expressions are the caller's. A curated path is never navigated (S1's rule).
        if (calleeItem.Content is Zig.Field fld
            && !IsCuratedStdPath(calleeItem)
            && ResolveModulePath(fld.Arg0) is { Lowering: { } nav }
            && nav.TryEvalExportedTypeReturningCall(Tok(fld.Arg2), args, caller: this) is { } navResult)
        {
            type = navResult.Type;
            RecordTypeCallBits(maybeCall, navResult.Bits);
            return true;
        }
        return false;
    }

    /// <summary>The owning module and IR name of a container ANOTHER module declares, named by a type alias
    /// (<c>CpuFeature</c>) or a module path (<c>std.Target.Cpu.Feature</c>); null for this module's own.</summary>
    private (ZigLowering Owner, string Container)? ForeignContainerOwner(Item baseItem)
    {
        CType? type = baseItem.Content switch
        {
            Zig.Ident id when _symbols.Resolve(Tok(id.Arg0)) is null
                => TryLookupContainerType(Tok(id.Arg0), out var known) ? known
                   : TryResolveModuleTypeAlias(Tok(id.Arg0), out var aliased) ? aliased : null,
            Zig.Field when !IsCuratedStdPath(baseItem) => TryResolveModuleNestedType(baseItem)?.Type,
            _ => null,
        };
        if (type is null || ContainerTypeName(type) is not { } name) { return null; }
        return _moduleGraph?.OwnerOfContainer(name) is { } owner && owner != this ? (owner, name) : null;
    }

    /// <summary>True when this module registered a container under IR name <paramref name="name"/>.</summary>
    internal bool DeclaresContainer(string name) =>
        // An imported module's containers (nested ones included) carry its unique prefix (`fmt__Placeholder`,
        // `Target__Cpu__Feature`); a root's are its own registrations.
        _modulePrefix is { } prefix
            ? name.StartsWith(prefix + "__", System.StringComparison.Ordinal)
            : _containerTypes.ContainsKey(name);

    /// <summary>The type-returning METHOD a call's callee names, or null: a bare name found in
    /// <see cref="_methods"/> of the current container or any lexically enclosing one, or
    /// <c>Container.name</c> through a container type (a self alias included).</summary>
    private Symbol? TypeReturningMethodCallee(Item callee)
    {
        switch (callee.Content)
        {
            case Zig.Ident id when _symbols.Resolve(Tok(id.Arg0)) is null:
                for (var c = _currentContainer; c is not null; c = _containerParents.GetValueOrDefault(c))
                {
                    if (_methods.TryGetValue(c, out var ms) && ms.TryGetValue(Tok(id.Arg0), out var s)
                        && _typeReturningGenerics.ContainsKey(s))
                    {
                        return s;
                    }
                }
                return null;
            case Zig.Field f when MemberBaseType(f.Arg0)?.Unqualified is CType.Named n:
                return _methods.TryGetValue(n.Name, out var owned) && owned.TryGetValue(Tok(f.Arg2), out var m)
                    && _typeReturningGenerics.ContainsKey(m)
                    ? m
                    : null;
            default:
                return null;
        }
    }

    /// <summary>The container type a member access is made through, when its base spells one: a name
    /// (<c>Self</c>, <c>Shapes</c>), a type call (<c>Box(u16).Elem()</c>) or a qualified nested type; else null.</summary>
    private CType? MemberBaseType(Item baseItem) => baseItem.Content switch
    {
        Zig.Ident id => TryLookupContainerType(Tok(id.Arg0), out var ct) ? ct : null,
        Zig.CallArgs or Zig.CallNoArgs => TryEvalTypeReturningCall(baseItem, out var called) ? called : null,
        Zig.Field => TryResolveQualifiedNestedType(baseItem),
        _ => null,
    };

    /// <summary>The container whose recorded comptime seeds a member of <paramref name="container"/> sees:
    /// the nearest reified instance on its lexical parent chain, or null.</summary>
    private string? ReifiedAncestor(string container)
    {
        for (string? c = container; c is not null; c = _containerParents.GetValueOrDefault(c))
        {
            if (_reifiedSeeds.ContainsKey(c)) { return c; }
        }
        return null;
    }

    /// <summary>Reify a type-returning generic THIS module exports, called from
    /// <paramref name="caller"/> (road-to-zig-std S4d). Declares the decl on demand
    /// (<see cref="EnsureDeclLowered"/> — in a lazy module a template is only a retained AST until
    /// something references it), then evaluates it exactly as a local call would, except that the
    /// comptime TYPE arguments are resolved in the CALLER's type environment: the argument expressions
    /// (<c>Aligned(MyAlias)</c>) belong to the caller, while the template's body and parameter types
    /// belong here. Null when this module exports no such type-returning generic, so the caller can
    /// fall through to its own handling.
    /// <para>A non-type comptime argument (a <c>comptime n: usize</c> value) is read in the caller's
    /// scope too, so a caller-side named <c>const</c> folds like a literal.</para></summary>
    internal (CType Type, int? Bits)? TryEvalExportedTypeReturningCall(string name, IReadOnlyList<Item> argItems, ZigLowering caller)
    {
        // Through any re-export (`pub const AutoHashMap = hash_map.AutoHashMap;`): the template is
        // evaluated by the module that declares it.
        if (ResolveExportedDecl(name) is not { Owner: var owner, Sym: var sym }
            || !owner._typeReturningGenerics.TryGetValue(sym, out var info))
        {
            return null;
        }
        var type = owner.EvalTypeReturningCall(sym, info, argItems, out var bits, typeArgScope: caller);
        return (type, bits);
    }

    /// <summary>Record (or clear) the declared integer width a type-returning call site just resolved to
    /// — see <see cref="_typeCallBits"/>. A struct result carries none; a delegating one carries its
    /// returned type's (<c>fn U(comptime n: u16) type { return @Int(.unsigned, n); }</c>).</summary>
    private void RecordTypeCallBits(Item callSite, int? bits)
    {
        if (bits is { } b) { _typeCallBits[callSite] = b; } else { _typeCallBits.Remove(callSite); }
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
    /// Members: fields, <c>const</c>s (including <c>const Self = @This();</c>) and METHODS are all
    /// reified (road-to-zig-std G4) — each method is declared immediately under the mangled container (so
    /// a call site resolves it through <see cref="_methods"/>) and its BODY is deferred to
    /// <see cref="_pendingReifiedMethods"/>, drained at top level like a monomorphized instance. A nested
    /// container member is still a loud cut.
    /// <para>Arguments resolve in TWO phases, exactly like <see cref="InstantiateGeneric"/>: every TYPE
    /// argument first, in the CALLER's environment (so a callee parameter sharing a name with a caller
    /// alias can't shadow it mid-read), then the seeds are installed and the remaining parameters read
    /// under them — which is what lets a later parameter's declared type spell an earlier type parameter
    /// (<c>fn Counter(comptime T: type, comptime start: T) type</c>; parameters bind LEFT TO RIGHT, as in
    /// zig).</para></summary>
    private CType EvalTypeReturningCall(Symbol templateSym, TypeReturningGenericInfo info,
        IReadOnlyList<Item> argItems, out int? declaredBits, ZigLowering? typeArgScope = null)
    {
        declaredBits = null;
        // Whose type environment the comptime TYPE arguments are read in: this module's for an ordinary
        // local call, the CALLER's when the template was reached through the module graph (S4d) — the
        // arguments are spelled at the call site, so they resolve there.
        var argScope = typeArgScope ?? this;
        // A type-returning METHOD evaluates in its owner's scope, with the owner's comptime seeds live: the
        // body of hash_map's `FieldIterator` names the instance's `Metadata`, and its arguments name `K`.
        var ownerSeedsKey = info.Owner is { } ownerName ? ReifiedAncestor(ownerName) : null;
        using var ownerSeedScope = EnterReifiedSeeds(ownerSeedsKey ?? "");
        using var ownerScope = EnterContainer(info.Owner ?? _currentContainer);
        if (argItems.Count != info.Params.Count)
        {
            throw new IrUnsupportedException(
                $"call to type-returning generic '{templateSym.Name}': expected {info.Params.Count} type argument(s), got {argItems.Count}");
        }
        var inv = CultureInfo.InvariantCulture;
        // Resolve each comptime argument (road-to-zig-std S4b widens W4's TYPE-only params): a TYPE arg →
        // its resolved type; a VALUE arg → a comptime value; an OPTIONAL value arg → a comptime null /
        // payload. Each contributes a mangle token, so the reified struct is keyed by the resolved args.
        var typeSeeds = new List<TypeSeed>();
        var valueSeeds = new List<(string name, long value, CType type)>();
        var optionalSeeds = new List<(string name, bool hasValue, long value, CType inner)>();
        var mangleTokens = new List<string>(argItems.Count);

        // Phase 1 — resolve every comptime TYPE argument in the CALLER's type environment (an alias
        // resolves to its aliased type, so the reification is keyed by the RESOLVED type). Deliberately
        // BEFORE any seed is installed: an argument expression belongs to the caller, so a callee
        // parameter that happens to share a name with a caller alias must not shadow it while the
        // caller's own arguments are still being read. Same two-phase shape as InstantiateGeneric.
        for (var i = 0; i < info.Params.Count; i++)
        {
            if (info.Params[i].Kind == ParamKind.ComptimeType)
            {
                // The declared integer width is read in the CALLER's scope too — the argument is
                // spelled there, so a caller-side alias for `u21` resolves to 21 the same way.
                // A type ARGUMENT is a pure type computation (`Log2Int(@Int(.unsigned, 384))`): a wide integer
                // may appear in it (see IntBuiltinType).
                argScope._typeArgDepth++;
                try
                {
                    typeSeeds.Add(new TypeSeed(info.Params[i].Name,
                                               argScope.LowerType(argItems[i]).Unqualified,
                                               argScope.DeclaredBitsOfTypeArg(argItems[i])));
                }
                finally
                {
                    argScope._typeArgDepth--;
                }
            }
        }

        // Phase 2 — seed the resolved type args (shadow-saved) BEFORE the remaining parameters are
        // read, so a later parameter's declared type can spell an earlier type parameter
        // (`fn Counter(comptime T: type, comptime start: T) type` — parameters bind LEFT TO RIGHT, as
        // in zig). The same seeds stay installed through the body reification below; the leading
        // type-alias locals `ProcessTypeReturningBody` binds append to the same shadow list, and the
        // outer `finally` restores the caller's environment however this returns (including the
        // memo hit).
        var typeShadows = new List<(string name, CType? prev, int? prevBits)>();
        foreach (var (name, type, bits) in typeSeeds)
        {
            typeShadows.Add((name,
                             _typeAliases.TryGetValue(name, out var pv) ? pv : (CType?)null,
                             _declaredIntBits.TryGetValue(name, out var pb) ? pb : (int?)null));
            _typeAliases[name] = type;
            SetDeclaredIntBits(name, bits);
        }
        var paramTypeSeedCount = typeShadows.Count;   // the body's own type aliases append after these
        try
        {
            // Mangle in PARAMETER order (types and values interleave by position) so the key is
            // deterministic, and resolve each comptime VALUE / OPTIONAL argument against the seeded types.
            for (var i = 0; i < info.Params.Count; i++)
            {
                var p = info.Params[i];
                if (p.Kind == ParamKind.ComptimeType)
                {
                    mangleTokens.Add(MangleTypeSeed(typeSeeds.First(s => s.Name == p.Name)));
                }
                else if (LowerType(p.TypeAst).Unqualified is CType.Optional optP)
                {
                    // A comptime OPTIONAL value param `comptime x: ?T` — a comptime null or known payload,
                    // or the caller's own comptime optional passed on (`AlignedManaged(T, alignment)`).
                    if (!argScope.TryComptimeOptionalArg(argItems[i], out var hasOpt, out var ov))
                    {
                        throw new IrUnsupportedException(
                            $"call to type-returning generic '{templateSym.Name}': the `comptime {p.Name}: ?T` argument "
                            + "must be a comptime null or a compile-time-known payload");
                    }
                    mangleTokens.Add(OptionalMangleToken(hasOpt, ov));
                    optionalSeeds.Add((p.Name, hasOpt, ov, optP.Inner));
                }
                else
                {
                    // An ENUM-typed param (std.mem's `SplitIterator(T, .scalar)` with `comptime delimiter_type:
                    // DelimiterType`) is the result location its bare `.scalar` argument resolves against.
                    var valueParamType = LowerType(p.TypeAst);
                    var valueArg = valueParamType.Unqualified is CType.Enum
                        ? argScope.LowerExprSink(argItems[i], valueParamType)
                        : argScope.LowerExpr(argItems[i]);
                    if (_ir.ConstEval(valueArg) is not { } vv)
                    {
                        throw new IrUnsupportedException(
                            $"call to type-returning generic '{templateSym.Name}': the `comptime {p.Name}` argument "
                            + "must be a compile-time-known value");
                    }
                    mangleTokens.Add(vv >= 0 ? vv.ToString(inv) : "n" + (-(System.Int128)vv).ToString(inv));
                    valueSeeds.Add((p.Name, vv, LowerType(p.TypeAst)));
                }
            }
            // Module-qualified in an imported module: two modules may each declare a `fn Box(comptime T)`.
            // (A member's template name already carries its owner's, which is qualified.)
            var baseName = info.Owner is not null ? templateSym.Name : QualifyTypeName(templateSym.Name);
            var mangled = mangleTokens.Count == 0 ? baseName : baseName + "__" + string.Join("_", mangleTokens);

            // Memoized — also short-circuits a self-referential field / recursive use, since the mapping is
            // installed BELOW before the fields are lowered. A DELEGATING instance (the W4 lift) memoizes
            // the type its body returned instead, under its own name.
            if (_delegatedTypes.TryGetValue(mangled, out var delegated))
            {
                declaredBits = delegated.Bits;
                return delegated.Type;
            }
            if (_containerTypes.TryGetValue(mangled, out var existing)) { return existing; }
            if (!_typeBodiesInProgress.Add(mangled))
            {
                throw new IrUnsupportedException(
                    $"type-returning generic '{templateSym.Name}': the instance `{mangled}` depends on itself "
                    + "(its body returns a type that needs the instance being computed — zig reports a dependency loop)");
            }

            var mangledType = new CType.Named(mangled);
            // The body below re-targets _currentContainer more than once (DeclareMethod clears it); the
            // guard restores the caller's on every exit path, including the delegated early return.
            using var containerRestore = EnterContainer(_currentContainer);
            // A scope for the value/optional comptime seeds (so the body's captured-if conditions + array
            // extents resolve); the type-param seeds already ride _typeAliases, installed by phase 2.
            _symbols.EnterScope();
            try
            {
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
                TypeBodyResult bodyResult;
                try
                {
                    bodyResult = ProcessTypeReturningBody(templateSym.Name, info.Body, typeShadows);
                }
                finally
                {
                    _typeBodiesInProgress.Remove(mangled);
                }
                // `return <type expression>;` (the W4 lift): the body DELEGATED — its result is a type that
                // already exists (another instance, a primitive, `@Int(…)`), so nothing is reified here.
                if (!bodyResult.IsStruct && bodyResult.Delegated is { } delegatedType)
                {
                    _delegatedTypes[mangled] = (delegatedType, bodyResult.DelegatedBits);
                    declaredBits = bodyResult.DelegatedBits;
                    return delegatedType;
                }
                var (fields, methods, consts, containers) = bodyResult.Fields is { } m
                    ? SplitMembers(m)
                    : (new List<Item>(), new List<Item>(), new List<Item>(), new List<Item>());
                _containerTypes[mangled] = mangledType;   // memo + @This() target; BEFORE reify for self-ref
                if (info.Owner is { } lexicalOwner)
                {
                    // A method-made instance is lexically inside its owner (it sees the owner's nested types),
                    // and its deferred bodies re-enter the owner's seeds too, its own LAST so they win a clash.
                    _containerParents[mangled] = lexicalOwner;
                    if (ownerSeedsKey is { } osk && _reifiedSeeds.TryGetValue(osk, out var os))
                    {
                        typeSeeds = [.. os.Types, .. typeSeeds];
                        valueSeeds = [.. os.Values, .. valueSeeds];
                        optionalSeeds = [.. os.Optionals, .. optionalSeeds];
                    }
                }
                // The body's own `const NAME = <type>;` locals (std.fmt.parse_float's `const MantissaT = mantissaType(T);` in
                // BiasedFp) are live while the fields reify, and a METHOD names them too (`@as(MantissaT, …)` in
                // toFloat): its body lowers later (a deferred method, or a generic method's instance through
                // _reifiedSeeds), so they ride along as extra type seeds.
                var methodTypeSeeds = new List<TypeSeed>(typeSeeds);
                foreach (var (localName, _, _) in typeShadows.Skip(paramTypeSeedCount))
                {
                    if (_typeAliases.TryGetValue(localName, out var localType) && methodTypeSeeds.All(t => t.Name != localName))
                    {
                        methodTypeSeeds.Add(new TypeSeed(localName, localType,
                            _declaredIntBits.TryGetValue(localName, out var localBits) ? localBits : null));
                    }
                }
                _reifiedSeeds[mangled] = (methodTypeSeeds, valueSeeds, optionalSeeds);
                _currentContainer = mangled;
                // TYPE const members (`pub const Slice = if (alignment) |a| … else []T;` in Aligned,
                // `pub const Unmanaged = HashMapUnmanaged(K, V, …);` in HashMap) are evaluated NOW, while
                // this instantiation's comptime seeds are live, into aliases scoped to the mangled container
                // (the `const Self = @This();` map), so a FIELD typed by one (`items: Slice`) resolves.
                // Every other const stays a lazily-lowered value const.
                // NESTED containers (hash_map's `pub const Entry = struct {…};`, `Iterator`, … inside Custom's
                // returned struct): flattened to `<mangled>__Name` and scoped to the instance, exactly as pass 0
                // flattens a module's, with this instance's seeds live (their fields may be typed `K` / `V`); their
                // methods are deferred with the seeds like the instance's own. NAMED first, before any type const
                // that uses one (`FieldIterator(K)` reifies a struct whose field points at `Mark`); laid out after
                // the type consts, and before the instance's own fields.
                var nestedDecls = CollectNestedContainers(mangled, containers);
                var nestedMethods = new List<(string container, Item fnDef)>();
                foreach (var (nName, nContent, nParent) in nestedDecls)
                {
                    RegisterContainerName(nName, nContent, nestedMethods);
                    ScopeNestedContainer(nName, nContent, nParent);
                }
                _currentContainer = mangled;
                // TYPE-returning member functions first: a type const may call one (`KeyIterator = FieldIterator(K)`).
                methods = DeclareTypeReturningMembers(mangled, methods);
                var valueConsts = new List<Item>();
                foreach (var c in consts)
                {
                    if (c.Content is Zig.ConstDecl typeConst && IsTypeConstMember(typeConst.Arg3))
                    {
                        var (memberType, memberBits) = LowerComptimeTypeExpr(templateSym.Name, typeConst.Arg3);
                        if (!_selfAliases.TryGetValue(mangled, out var scoped))
                        {
                            scoped = new Dictionary<string, CType>(System.StringComparer.Ordinal);
                            _selfAliases[mangled] = scoped;
                        }
                        scoped[Tok(typeConst.Arg1)] = memberType;
                        if (memberBits is { } mb) { _typeConstBits[(mangled, Tok(typeConst.Arg1))] = mb; }
                        continue;
                    }
                    valueConsts.Add(c);
                }
                consts = valueConsts;
                // Their bodies: after the type consts, which a nested field may name (`index: Size`).
                foreach (var (nName, nContent, _) in nestedDecls)
                {
                    RegisterContainerBody(nName, nContent, nestedMethods);
                }
                foreach (var (nContainer, nDef) in nestedMethods)
                {
                    var nm = DeclareMethod(nContainer, nDef);
                    if (IsFnTemplate(nm.sym)) { continue; }   // a generic method instantiates per call
                    _pendingReifiedMethods.Add(new PendingReifiedMethod(
                        nm.sym, nContainer, nm.ps, nm.body, methodTypeSeeds, valueSeeds, optionalSeeds));
                }
                _currentContainer = mangled;
                RegisterStruct(mangled, fields, bodyResult.Layout);
                // `const Self = @This();` → a self alias scoped to the MANGLED container, plus any value
                // const — both keyed by the mangled name, so a method's `self: *Self` and a `S.NAME` use
                // resolve exactly like an ordinary container's. Runs after _containerTypes[mangled] is set
                // (the self alias reads it) and before the methods (their signatures may spell `Self`).
                RegisterContainerConsts(mangled, consts);
                // Each method: declare the signature NOW — while the comptime type/value seeds are live, so a
                // `v: T` parameter lowers to the concrete type — and defer the BODY. The signature is reached
                // by call sites through `_methods[mangled]` (not by name lookup), so declaring it inside this
                // reification's scope is fine; the body must NOT lower here, because a reification is
                // triggered mid-signature/mid-body from an arbitrary type position and LowerFnBodyCore would
                // clobber the in-flight per-function state (the same re-entrancy rule as W3a's worklist).
                foreach (var methodDef in methods)
                {
                    var me = DeclareMethod(mangled, methodDef);
                    _currentContainer = mangled;   // DeclareMethod clears it; the next signature needs it back
                    if (IsFnTemplate(me.sym)) { continue; }   // a generic method instantiates per call
                    _pendingReifiedMethods.Add(new PendingReifiedMethod(
                        me.sym, mangled, me.ps, me.body, methodTypeSeeds, valueSeeds, optionalSeeds));
                }
            }
            finally
            {
                _symbols.ExitScope();
            }
            return mangledType;
        }
        finally
        {
            // Restore the caller's type environment. Covers the phase-2 seeds AND any leading body-local
            // alias `ProcessTypeReturningBody` appended, on every exit path — including the memo hit.
            for (var i = typeShadows.Count - 1; i >= 0; i--)
            {
                var (name, prev, prevBits) = typeShadows[i];
                if (prev is { } p) { _typeAliases[name] = p; } else { _typeAliases.Remove(name); }
                SetDeclaredIntBits(name, prevBits);
            }
        }
    }

    /// <summary>A METHOD of a reified type-returning generic's struct, awaiting body lowering
    /// (road-to-zig-std G4). Same re-entrancy rule as <see cref="PendingInstantiation"/>: the reification
    /// that produced it runs from an arbitrary type position (a signature, a global initializer, another
    /// body), so lowering the method body inline would clobber the in-flight per-function state. The body
    /// is therefore deferred and drained at top level. Carries the mangled container (so <c>@This()</c>,
    /// <c>Self</c> and sibling-method calls resolve while the body lowers) and the reification's comptime
    /// seeds, re-applied per body so the method's own references to <c>T</c> / a comptime value param
    /// resolve to the same concrete types the signature was built from.</summary>
    private sealed record PendingReifiedMethod(
        Symbol Method,
        string Container,
        IReadOnlyList<(string name, CType type)> RuntimeParams,
        Item Body,
        IReadOnlyList<TypeSeed> TypeSeeds,
        IReadOnlyList<(string name, long value, CType type)> ValueSeeds,
        IReadOnlyList<(string name, bool hasValue, long value, CType inner)> OptionalSeeds,
        IReadOnlyList<(string name, ZigLowering owner, Symbol fn)>? FnSeeds = null);

    /// <summary>Reified-generic method bodies awaiting lowering, drained at top level alongside
    /// <see cref="_pendingInstantiations"/> (each drain can enqueue into the other: a method body may call
    /// a generic, and a generic instance may name a reified type). Enqueued by
    /// <see cref="EvalTypeReturningCall"/>.</summary>
    private readonly List<PendingReifiedMethod> _pendingReifiedMethods = new();

    /// <summary>Each reified container's comptime seeds, by its mangled name, so a member lowered lazily
    /// later (a field default, a <c>Type.NAME</c> const) can see them again (<see cref="EnterReifiedSeeds"/>).</summary>
    private readonly Dictionary<string, (IReadOnlyList<TypeSeed> Types,
        IReadOnlyList<(string name, long value, CType type)> Values,
        IReadOnlyList<(string name, bool hasValue, long value, CType inner)> Optionals)> _reifiedSeeds
        = new(System.StringComparer.Ordinal);

    /// <summary>Lower one deferred reified-generic method body (road-to-zig-std G4) at top level. Sets
    /// <see cref="_currentContainer"/> to the mangled container for the duration — exactly what pass 2
    /// does for an ordinary container's method — so <c>@This()</c>, a <c>Self</c> alias, a sibling method
    /// call and field access all resolve; then re-applies the reification's comptime seeds through the
    /// shared <see cref="LowerFnBodyCore"/>, so the body's <c>T</c> matches its signature's.</summary>
    private void LowerReifiedMethodBody(PendingReifiedMethod p)
    {
        // A method of a LOCAL struct inside a generic instance (std.sort's `Context.lessThan` calling the
        // instance's `comptime lessThanFn`) sees the instance's comptime function seeds too.
        var shadows = new List<(string name, (ZigLowering, Symbol)? prev)>();
        foreach (var (name, owner, fn) in p.FnSeeds ?? System.Array.Empty<(string, ZigLowering, Symbol)>())
        {
            shadows.Add((name, _fnAliases.TryGetValue(name, out var prev) ? prev : null));
            _fnAliases[name] = (owner, fn);
        }
        try
        {
            using var _ = EnterContainer(p.Container);
            LowerFnBodyCore(p.Method, p.RuntimeParams, p.Body, p.ValueSeeds, p.TypeSeeds, p.OptionalSeeds);
        }
        finally
        {
            foreach (var (name, prev) in shadows)
            {
                if (prev is { } pv) { _fnAliases[name] = pv; } else { _fnAliases.Remove(name); }
            }
        }
    }

    // The body EVALUATOR (ProcessTypeReturningBody and the comptime type-expression folds it uses)
    // lives in ZigLowering.TypeBody.cs — the W4 lift (road-to-zig-std G4 blocker 2).
}
