#nullable enable

using System.Collections.Generic;
using DotCC.Ir;

namespace DotCC.Frontends;

/// <summary>Scope guards for the lowering context that is saved, replaced and restored around a nested
/// lowering — the current container (<see cref="_currentContainer"/>) and the ANF hoist buffer
/// (<see cref="_hoist"/> + <see cref="_hoistImpureSeen"/>). Each used to be a hand-written
/// save / <c>try</c> / <c>finally</c> restore at every site; a site that forgot the <c>finally</c> would
/// have leaked a nested context into the rest of the compile on the first exception, silently. A guard
/// makes the restore part of the construct: <c>using var _ = EnterContainer(name);</c>.
/// <para>The guards are <c>ref struct</c>s so one can never be boxed, stored in a field, or captured by a
/// lambda — it lives exactly as long as the lexical scope that declared it, which is the whole contract.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>Restores <see cref="_currentContainer"/> on dispose. See <see cref="EnterContainer"/>.</summary>
    private readonly ref struct ContainerScope
    {
        private readonly ZigLowering _owner;
        private readonly string? _saved;

        internal ContainerScope(ZigLowering owner, string? saved)
        {
            _owner = owner;
            _saved = saved;
        }

        public void Dispose() => _owner._currentContainer = _saved;
    }

    /// <summary>Make <paramref name="container"/> the current container until the returned guard is
    /// disposed — the scope <c>@This()</c>, a <c>Self</c> alias, a nested type name and a sibling method
    /// resolve in.</summary>
    private ContainerScope EnterContainer(string? container)
    {
        var scope = new ContainerScope(this, _currentContainer);
        _currentContainer = container;
        return scope;
    }

    /// <summary>Restores the ANF hoist buffer and its impurity watermark on dispose. See
    /// <see cref="EnterThrowawayHoist"/> / <see cref="EnterFreshHoist"/>.</summary>
    private readonly ref struct HoistScope
    {
        private readonly ZigLowering _owner;
        private readonly List<CStmt>? _savedBuffer;
        private readonly bool _savedImpure;

        internal HoistScope(ZigLowering owner, List<CStmt>? savedBuffer, bool savedImpure)
        {
            _owner = owner;
            _savedBuffer = savedBuffer;
            _savedImpure = savedImpure;
        }

        public void Dispose()
        {
            _owner._hoist = _savedBuffer;
            _owner._hoistImpureSeen = _savedImpure;
        }
    }

    /// <summary>Lower into a THROWAWAY hoist buffer until the guard is disposed — for a lowering whose
    /// statements must never reach an emitted body: an operand read only for its TYPE (<c>@TypeOf</c>,
    /// <c>anytype</c> inference) or only for its comptime VALUE (a type body's condition or const). The
    /// impurity watermark is left as it is, and restored with the buffer.</summary>
    private HoistScope EnterThrowawayHoist()
    {
        var scope = new HoistScope(this, _hoist, _hoistImpureSeen);
        _hoist = new List<CStmt>();
        return scope;
    }

    /// <summary>Install a FRESH hoist buffer at an eval-safe statement point until the guard is disposed —
    /// a new, empty buffer and a cleared impurity watermark (nothing has been evaluated yet in this
    /// statement). The caller reads <see cref="_hoist"/> before disposing to prepend what was hoisted.</summary>
    private HoistScope EnterFreshHoist()
    {
        var scope = new HoistScope(this, _hoist, _hoistImpureSeen);
        _hoist = new List<CStmt>();
        _hoistImpureSeen = false;
        return scope;
    }
}
