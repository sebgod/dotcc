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

    /// <summary>Restores what <see cref="EnterReifiedSeeds"/> installed: the type aliases its type seeds
    /// shadowed, and the symbol scope its value / optional seeds were declared in.</summary>
    private readonly ref struct ReifiedSeedScope
    {
        private readonly ZigLowering? _owner;
        private readonly List<(string Name, CType? Prev, int? PrevBits)>? _shadows;

        internal ReifiedSeedScope(ZigLowering? owner, List<(string Name, CType? Prev, int? PrevBits)>? shadows)
        {
            _owner = owner;
            _shadows = shadows;
        }

        public void Dispose()
        {
            if (_owner is not { } o || _shadows is not { } sh) { return; }
            for (var i = sh.Count - 1; i >= 0; i--)
            {
                var (name, prev, prevBits) = sh[i];
                if (prev is { } p) { o._typeAliases[name] = p; } else { o._typeAliases.Remove(name); }
                o.SetDeclaredIntBits(name, prevBits);
            }
            o._symbols.ExitScope();
        }
    }

    /// <summary>Re-install the comptime seeds a REIFIED container was instantiated with (road-to-zig-std
    /// G4/G5), around lowering one of its members lazily: a field default materialized in a struct
    /// literal, or a <c>Type.NAME</c> const. Both are stored raw and lowered at the USE site, where the
    /// instantiation's <c>T</c> / <c>cap</c> / <c>n</c> are otherwise out of scope (the method-body drain
    /// re-applies them the same way). A no-op for any other container.</summary>
    private ReifiedSeedScope EnterReifiedSeeds(string container)
    {
        if (!_reifiedSeeds.TryGetValue(container, out var seeds)) { return new ReifiedSeedScope(null, null); }
        _symbols.EnterScope();
        var shadows = new List<(string Name, CType? Prev, int? PrevBits)>();
        foreach (var (name, type, bits) in seeds.Types)
        {
            shadows.Add((name,
                         _typeAliases.TryGetValue(name, out var prev) ? prev : (CType?)null,
                         _declaredIntBits.TryGetValue(name, out var pb) ? pb : (int?)null));
            _typeAliases[name] = type;
            SetDeclaredIntBits(name, bits);
        }
        foreach (var (name, value, type) in seeds.Values)
        {
            var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type });
            _comptimeVars[sym] = (value, type);
        }
        foreach (var (name, hasValue, value, inner) in seeds.Optionals)
        {
            var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = new CType.Optional(inner) });
            _comptimeOptionalVars[sym] = (hasValue, value, inner);
        }
        return new ReifiedSeedScope(this, shadows);
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
