#nullable enable

using System.Collections.Generic;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>The comptime engine's E2 (road-to-zig-std, the comptime-engine segment): a function BODY lowered
/// on demand, in the middle of lowering another one, because the interpreter needs it NOW. A comptime
/// value in a lowering-time position (an array extent <c>[lenFor(u8)]u8</c>, a <c>comptime</c> call whose
/// value a type or a folded branch needs) cannot wait for the post-drain fold queue, and its callee is
/// usually not lowered yet: a generic instance drains after pass 2, and a plain function may come later in
/// the file or live in a lazy module.
/// <para>Bodies used to lower only at top level (the W3a re-entrancy rule), because the per-function lowering
/// state is a set of fields. An on-demand body therefore runs inside a <see cref="FnStateScope"/>, which
/// saves every per-function field and the symbol table's function scopes, starts the callee on a clean
/// slate, and puts the caller's state back afterwards. Each body lowers once: every drain skips a body
/// that has already started (<see cref="BeginBody"/>), and a body that is in progress (a recursive
/// demand) is not demanded again, so its comptime call falls back to the deferred fold.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>Every function body whose lowering has started, by a drain or on demand.</summary>
    private readonly HashSet<Symbol> _bodiesStarted = new();

    /// <summary>The root module's pass-2 work list, kept so a body can be demanded before pass 2 reaches
    /// it (a comptime call to a function declared later in the file).</summary>
    private readonly List<(Symbol sym, List<(string name, CType type)> ps, Item body, string? container)> _rootBodies = new();

    /// <summary>Mark <paramref name="sym"/>'s body as started. False when it already was (lowered on demand
    /// earlier, so the drain skips it).</summary>
    private bool BeginBody(Symbol sym) => _bodiesStarted.Add(sym);

    /// <summary>Lower <paramref name="sym"/>'s body now, if this module owns a pending one: a pass-2 body, a
    /// lazily referenced body, a generic instance or a reified method. False when this module has no such
    /// body, or it has already started.</summary>
    internal bool TryLowerBodyOnDemand(Symbol sym)
    {
        if (_bodiesStarted.Contains(sym)) { return false; }
        // A body whose on-demand lowering fails is un-marked, so the top-level drain lowers it again and its
        // error surfaces there: a speculative evaluation (TryEvalComptimeIntBody) that demanded it may swallow
        // the exception, and a body left "started" would otherwise never be lowered at all.
        try { return TryLowerBodyOnDemandCore(sym); }
        catch (IrUnsupportedException)
        {
            _bodiesStarted.Remove(sym);
            throw;
        }
    }

    /// <summary>The lookup and lowering behind <see cref="TryLowerBodyOnDemand"/>.</summary>
    private bool TryLowerBodyOnDemandCore(Symbol sym)
    {
        if (_rootBodies.FirstOrDefault(e => ReferenceEquals(e.sym, sym)) is { body: not null } root)
        {
            using var _ = new FnStateScope(this);
            using var container = EnterContainer(root.container);
            BeginBody(sym);
            LowerFnBody(root.sym, root.ps, root.body);
            return true;
        }
        if (_pendingModuleBodies.FirstOrDefault(e => ReferenceEquals(e.sym, sym)) is { body: not null } module)
        {
            using var _ = new FnStateScope(this);
            using var container = EnterContainer(module.container);
            BeginBody(sym);
            LowerFnBody(module.sym, module.ps, module.body);
            return true;
        }
        if (_pendingInstantiations.FirstOrDefault(p => ReferenceEquals(p.Instance, sym)) is { } inst)
        {
            using var _ = new FnStateScope(this);
            BeginBody(sym);
            LowerInstantiationBody(inst);
            return true;
        }
        if (_pendingReifiedMethods.FirstOrDefault(p => ReferenceEquals(p.Method, sym)) is { } method)
        {
            using var _ = new FnStateScope(this);
            BeginBody(sym);
            LowerReifiedMethodBody(method);
            return true;
        }
        return false;
    }

    /// <summary>Saves the per-function lowering state on construction, clears it for a nested body, and
    /// restores it on dispose (<see cref="TryLowerBodyOnDemand"/>). Covers the current function's
    /// identity and return / error set, the container, the ANF hoist buffer, the labeled-block and loop
    /// target stacks, the body-scoped shadow lists, and the symbol table's function scopes and used
    /// names. The function-flat maps (<c>_typeAliases</c>, <c>_comptimeValues</c>) are not swapped: every
    /// body shadow-saves what it binds in them, as it already did for sequential drains.</summary>
    private sealed class FnStateScope : System.IDisposable
    {
        private readonly ZigLowering _o;
        private readonly bool _inGenericInstance;
        private readonly CType? _currentFnRet;
        private readonly string _currentFnName;
        private readonly bool _currentFnHasErrdefer;
        private readonly (string? name, HashSet<string> members)? _currentFnErrorSet;
        private readonly string? _currentContainer;
        private readonly string? _currentConstContainer;
        private readonly List<CStmt>? _hoist;
        private readonly bool _hoistImpureSeen;
        private readonly Item? _loopBeingWrapped;
        private readonly int _comptimeBoolCallDepth;
        private readonly int _inlineUnrollDepth;
        private readonly (string Name, CType? Prev)[] _localContainerShadows;
        private readonly (string Name, CType? Prev, int? PrevBits)[] _typeAliasShadows;
        private readonly LabeledBlockTarget[] _labeledBlocks;
        private readonly LabeledLoopTarget[] _labeledLoops;
        private readonly LoopBreakTarget[] _loopBreakTargets;
        private readonly LoopValueTarget[] _loopValues;
        private readonly SymbolTable.SuspendedFunction _symbols;

        internal FnStateScope(ZigLowering o)
        {
            _o = o;
            _inGenericInstance = o._inGenericInstance;
            _currentFnRet = o._currentFnRet;
            _currentFnName = o._currentFnName;
            _currentFnHasErrdefer = o._currentFnHasErrdefer;
            _currentFnErrorSet = o._currentFnErrorSet;
            _currentContainer = o._currentContainer;
            _currentConstContainer = o._currentConstContainer;
            _hoist = o._hoist;
            _hoistImpureSeen = o._hoistImpureSeen;
            _loopBeingWrapped = o._loopBeingWrapped;
            _comptimeBoolCallDepth = o._comptimeBoolCallDepth;
            _inlineUnrollDepth = o._inlineUnrollDepth;
            _localContainerShadows = o._localContainerShadows.ToArray();
            _typeAliasShadows = o._typeAliasShadows.ToArray();
            _labeledBlocks = o._labeledBlocks.ToArray();
            _labeledLoops = o._labeledLoops.ToArray();
            _loopBreakTargets = o._loopBreakTargets.ToArray();
            _loopValues = o._loopValues.ToArray();
            _symbols = o._symbols.SuspendFunction();

            o._currentContainer = null;
            o._currentConstContainer = null;
            o._hoist = null;
            o._hoistImpureSeen = false;
            o._loopBeingWrapped = null;
            o._comptimeBoolCallDepth = 0;
            o._inlineUnrollDepth = 0;
            o._localContainerShadows.Clear();
            o._typeAliasShadows.Clear();
            o._labeledBlocks.Clear();
            o._labeledLoops.Clear();
            o._loopBreakTargets.Clear();
            o._loopValues.Clear();
        }

        public void Dispose()
        {
            var o = _o;
            o._inGenericInstance = _inGenericInstance;
            o._currentFnRet = _currentFnRet;
            o._currentFnName = _currentFnName;
            o._currentFnHasErrdefer = _currentFnHasErrdefer;
            o._currentFnErrorSet = _currentFnErrorSet;
            o._currentContainer = _currentContainer;
            o._currentConstContainer = _currentConstContainer;
            o._hoist = _hoist;
            o._hoistImpureSeen = _hoistImpureSeen;
            o._loopBeingWrapped = _loopBeingWrapped;
            o._comptimeBoolCallDepth = _comptimeBoolCallDepth;
            o._inlineUnrollDepth = _inlineUnrollDepth;
            o._localContainerShadows.Clear();
            o._localContainerShadows.AddRange(_localContainerShadows);
            o._typeAliasShadows.Clear();
            o._typeAliasShadows.AddRange(_typeAliasShadows);
            Restore(o._labeledBlocks, _labeledBlocks);
            Restore(o._labeledLoops, _labeledLoops);
            Restore(o._loopBreakTargets, _loopBreakTargets);
            Restore(o._loopValues, _loopValues);
            o._symbols.ResumeFunction(_symbols);
        }

        /// <summary>Refill <paramref name="stack"/> from a <see cref="Stack{T}.ToArray"/> snapshot (top
        /// first), so the order is what it was.</summary>
        private static void Restore<T>(Stack<T> stack, T[] snapshot)
        {
            stack.Clear();
            for (var i = snapshot.Length - 1; i >= 0; i--) { stack.Push(snapshot[i]); }
        }
    }
}
