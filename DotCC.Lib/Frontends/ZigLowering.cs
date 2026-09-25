#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>
/// Lowers a parsed Zig translation unit (the raw <c>Zig.*</c> parse tree yielded by
/// the generated identity visitor) onto the neutral typed IR — the Zig-side analogue
/// of <see cref="IrBuilder.AddUnit"/>'s top-down walk over <c>C.*</c>. Standalone by
/// design: it reuses the neutral IR types (<see cref="FuncDef"/>/<see cref="CExpr"/>/
/// <see cref="CType"/>/<see cref="Symbol"/>/<see cref="SymbolTable"/>) and the target's
/// <see cref="INameLegalizer"/>, but leaves the C <see cref="IrBuilder"/> untouched, so
/// the two frontends stay decoupled. Shared IR-construction helpers get extracted from
/// <see cref="IrBuilder"/> only once this second implementer shows what's actually common.
///
/// SURFACE (grows deliberately; the grammar parses more than this lowers): functions
/// with parameters; typed/untyped <c>const</c>/<c>var</c>; <c>return</c>; <c>if</c>/
/// <c>while</c> statements and assignment; the <c>if</c>-expression (→ ternary); the
/// full arithmetic / comparison / boolean / bitwise / shift / prefix operator set; and
/// the fixed-width integer types mapped to their faithful C# signedness. Everything else
/// throws <see cref="IrUnsupportedException"/> — fail loudly, grow deliberately.
///
/// SEMANTICS NOTE: Zig has no C-style implicit promotions — a valid Zig binary op has
/// same-typed operands — but we reuse C's <see cref="CType.UsualArithmetic"/> for the
/// result type anyway, because the C# BACKEND promotes identically (<c>u8 + u8</c> is
/// <c>int</c> in C# too) and inserts the narrowing cast back at the typed sink. A
/// comparison / boolean op is typed <see cref="CType.Int"/>: the backend renders it as
/// an integer-valued <c>(CBool)(…)</c> and wraps every condition in <c>Cond.B(…)</c>,
/// so an <c>int</c>-typed relational feeds <c>if</c>/<c>while</c>/ternary cleanly.
/// </summary>
internal sealed partial class ZigLowering
{
    private readonly IrModule _ir;
    private readonly SymbolTable _symbols;

    /// <summary>The name legalizer, kept so an <c>@import</c>ed module can be lowered with its own
    /// <see cref="ZigLowering"/> against the same legalizer (consistent emitted names across modules).</summary>
    private readonly INameLegalizer _names;

    /// <summary>The Zig module graph (road-to-zig-std S1), or null for a standalone unit with no
    /// cross-module imports (the pre-graph behavior — only <c>@import("std")</c> is modeled). When set,
    /// a relative <c>@import("./x.zig")</c> resolves + lowers the sibling module through it.</summary>
    private readonly ZigModuleGraph? _moduleGraph;

    /// <summary>Directory of the file being lowered — the base a relative <c>@import</c> resolves
    /// against (null for an in-memory/standalone unit).</summary>
    private readonly string? _importerDir;

    /// <summary>Bound name of a <c>const X = @import("…zig");</c> → its raw import spec (a relative
    /// path, or the absolute <c>std.zig</c> path for <c>@import("std")</c>), recorded WITHOUT resolving —
    /// so std.zig's 66 re-exports don't fan out at prepare time. The target module is resolved + prepared
    /// LAZILY on first navigation/use (<see cref="ResolveImport"/>). Road-to-zig-std S1/S2.</summary>
    private readonly Dictionary<string, string> _importSpecs = new(System.StringComparer.Ordinal);

    /// <summary>Memo of a bound import name → the resolved+prepared <see cref="ZigModule"/> (populated by
    /// <see cref="ResolveImport"/> on first use), so <c>X.func(…)</c> / a <c>X.sub</c> navigation resolves
    /// the module without re-loading.</summary>
    private readonly Dictionary<string, ZigModule> _importModules = new(System.StringComparer.Ordinal);

    /// <summary>Resolve an import bound in THIS unit by <paramref name="name"/> to its module, loading +
    /// preparing it (lazily) on first use and memoizing. Null when <paramref name="name"/> isn't a
    /// bound file import (e.g. an unconfigured <c>std</c>, or a plain value const).</summary>
    internal ZigModule? ResolveImport(string name)
    {
        if (_importModules.TryGetValue(name, out var existing)) { return existing; }
        if (!_importSpecs.TryGetValue(name, out var spec) || _moduleGraph is null) { return null; }
        // A SYNTHETIC module (`builtin` / `root` — road-to-zig-std S3): generated as Zig source and
        // prepared exactly like a file module, so navigation into it needs no second rule. Checked
        // BEFORE the importer-directory requirement below — a generated module has no directory to
        // resolve against, so an in-memory unit can ask about the target just as a file can.
        if (ZigSyntheticModules.IsSyntheticSpec(spec) && _moduleGraph.LoadSynthetic(spec) is { } synth)
        {
            EnsureModulePrepared(synth);
            _importModules[name] = synth;
            return synth;
        }
        if (_importerDir is not null)
        {
            // Only a resolvable `.zig` file becomes a navigable module; a missing file isn't navigable,
            // so return null and let the caller error if it was actually used (rather than throw a raw
            // file-not-found here).
            var full = System.IO.Path.IsPathRooted(spec) ? spec : System.IO.Path.Combine(_importerDir, spec);
            if (!spec.EndsWith(".zig", System.StringComparison.Ordinal) || !System.IO.File.Exists(full))
            {
                return null;
            }
            var mod = _moduleGraph.Load(spec, _importerDir);
            EnsureModulePrepared(mod);
            _importModules[name] = mod;
            return mod;
        }
        return null;
    }

    /// <summary>Resolve a module-navigation expression to its <see cref="ZigModule"/>: a bare import name
    /// (<c>util</c>, <c>std</c>) or a chained re-export (<c>std.ascii</c> → <c>ascii.zig</c> via std.zig's
    /// <c>pub const ascii = @import("ascii.zig")</c>). Navigates through each base module's own imports.
    /// Null when the expression isn't a module path (road-to-zig-std S1/S2).</summary>
    private ZigModule? ResolveModulePath(Item expr) => expr.Content switch
    {
        // A bare name is an import, or an ALIAS of a module path (`const math = std.math;`, recorded
        // unresolved in _moduleAliasPaths), which is how std spells nearly every cross-file reference.
        Zig.Ident id => ResolveNamedModule(Tok(id.Arg0)),
        Zig.Field f when ResolveModulePath(f.Arg0) is { Lowering: { } baseLowering } =>
            baseLowering.ResolveNamedModule(Tok(f.Arg2)),
        // A module bound as a const of a NESTED container another module declares (std.crypto's
        // `pub const hash = struct { pub const sha2 = @import("crypto/sha2.zig"); … }`, so `std.crypto.hash.sha2`).
        Zig.Field f when f.Arg0.Content is Zig.Field && TryResolveModuleNestedType(f.Arg0) is { Type: var nestedType, Owner: var nestedOwner }
                         && ContainerTypeName(nestedType) is { } nestedName =>
            nestedOwner.ContainerConstModule(nestedName, Tok(f.Arg2)),
        // An INLINE import (`pub const block = @import("sort/block.zig").block;` in sort.zig): the spec is
        // registered under a synthetic import name, so it resolves (and memoizes) exactly as `const x =
        // @import("…");` does.
        Zig.BuiltinCall b when Tok(b.Arg0) == "@import" && Flatten(b.Arg2) is { Count: 1 } ia
                               && ia[0].Content is Zig.StrLit sl =>
            ResolveInlineImport(Tok(sl.Arg0).Trim('"')),
        // `@field(Target, @tagName(family))` (std.Target.Cpu.has's parameter type): a module member named by a
        // comptime string, on a module or on this file's own `@This()`.
        Zig.BuiltinCall fb when Tok(fb.Arg0) == "@field" && Flatten(fb.Arg2) is { Count: 2 } fa
                               && ComptimeName(fa[1]) is { } member
                               && FieldBaseModule(fa[0]) is { } baseModule =>
            baseModule.ResolveNamedModule(member),
        _ => null,
    };

    /// <summary>The module a container const names (<c>pub const sha2 = @import("crypto/sha2.zig");</c> inside a
    /// namespace struct), resolved in that container's scope; null when the const is not a module path.</summary>
    private ZigModule? ContainerConstModule(string container, string name)
    {
        if (!_containerConsts.TryGetValue(container, out var consts) || !consts.TryGetValue(name, out var entry)
            || entry.typeItem is not null)
        {
            return null;
        }
        using var scope = EnterContainer(container);
        return ResolveModulePath(entry.rhs);
    }

    /// <summary>The module an <c>@field</c> base denotes: a module path, or a name bound to this file's own
    /// file-as-struct type (<c>const Target = @This();</c> in Target.zig), which is this module.</summary>
    private ZigLowering? FieldBaseModule(Item baseItem)
    {
        if (ResolveModulePath(baseItem)?.Lowering is { } module) { return module; }
        return baseItem.Content is Zig.Ident id && _fileContainer is { } file
               && TryLookupContainerType(Tok(id.Arg0), out var t) && ContainerTypeName(t) == file
            ? this
            : null;
    }

    /// <summary>A comptime NAME: a comptime string (<see cref="EvalComptimeValue"/>), or <c>@tagName(x)</c>
    /// of a comptime enum value (a generic's <c>comptime family: Arch.Family</c> seed). Null otherwise.</summary>
    private string? ComptimeName(Item item)
    {
        if (item.Content is Zig.BuiltinCall { Arg0: var tn } tb && Tok(tn) == "@tagName"
            && Flatten(tb.Arg2) is [{ Content: Zig.Ident { Arg0: var argTok } }]
            && _symbols.Resolve(Tok(argTok)) is { } seed && _comptimeVars.TryGetValue(seed, out var seedValue)
            && seedValue.Type.Unqualified is CType.Enum seedEnum
            && _enumMembers.TryGetValue(seedEnum.Name, out var members))
        {
            return members.FirstOrDefault(m => m.Value.ConstValue == seedValue.Value).Key;
        }
        return EvalComptimeValue(item) is LitStr s
            ? new string(DotCC.EmitHelpers.StringByteValues(s.Segments).Select(b => (char)b).ToArray())
            : null;
    }

    /// <summary>The module a top-level NAME of this module denotes: an import, an alias of a module path
    /// (<c>const math = std.math;</c>), or a re-export of another module name (std.zig's
    /// <c>pub const builtin = lang;</c>, so <c>std.builtin.Endian</c> is lang.zig's). Null when it names none.</summary>
    private ZigModule? ResolveNamedModule(string name)
        => ResolveImport(name)
        ?? (_moduleAliasPaths.TryGetValue(name, out var aliased) ? ResolveModulePath(aliased)
            : _declAliases.TryGetValue(name, out var reexport) && reexport.Content is Zig.Ident or Zig.Field
                ? ResolveModulePath(reexport)
                : IsSelfModuleAlias(name) ? _module
                : IsRootSelfAlias(name) ? _rootSelfModule : null);

    /// <summary>A top-level <c>const NAME = @This();</c> (std's <c>const mem = @This();</c>): inside a file, <c>@This()</c> is
    /// the file's own struct, so the name aliases this module (<c>mem.eql(…)</c> calls its own <c>eql</c>).</summary>
    private bool IsSelfModuleAlias(string name) =>
        _lazyValueConsts.TryGetValue(name, out var vc) && vc.typeItem is null
        && vc.rhs.Content is Zig.BuiltinCallNoArgs { Arg0: var thisTok } && IsThisBuiltin(thisTok)
        && _symbols.Resolve(name) is null or { IsGlobal: true };

    /// <summary>The ROOT file's analogue of <see cref="IsSelfModuleAlias"/>: a top-level <c>const root = @This();</c>
    /// in a root file with no top-level fields (a namespace, not a file-as-struct type), not shadowed here.</summary>
    private bool IsRootSelfAlias(string name) =>
        _rootSelfAliases.Contains(name) && _symbols.Resolve(name) is null or { IsGlobal: true };

    /// <summary>Whether a <c>BuiltinCallNoArgs</c> name token is <c>@This</c>.</summary>
    private static bool IsThisBuiltin(Item tok) => Tok(tok) == "@This";

    /// <summary>The root file's top-level <c>const NAME = @This();</c> names (see <see cref="IsRootSelfAlias"/>).</summary>
    private readonly HashSet<string> _rootSelfAliases = new(System.StringComparer.Ordinal);

    /// <summary>A root unit has no <see cref="ZigModule"/> of its own (<see cref="_module"/> is null), so one that names
    /// itself through <c>@This()</c> gets this synthetic module, whose <see cref="ZigModule.Lowering"/> is the root's
    /// own lowering: <c>root.helper()</c>, <c>root.Point</c> and <c>root.limit</c> then resolve by the same
    /// module-qualified paths as <c>util.helper()</c> through an import.</summary>
    private ZigModule? _rootSelfModule;

    /// <summary>Resolve an inline <c>@import("spec")</c> (see <see cref="ResolveModulePath"/>) through the
    /// ordinary import table, under the synthetic name <c>@import:spec</c>.</summary>
    private ZigModule? ResolveInlineImport(string spec)
    {
        var name = "@import:" + spec;
        _importSpecs.TryAdd(name, spec);
        return ResolveImport(name);
    }

    /// <summary>This unit's top-level function declarations (name → the declared <see cref="Symbol"/>),
    /// captured in pass 1 so an importing module can build a call against them (<see cref="ExportedFns"/>).</summary>
    private readonly Dictionary<string, Symbol> _exportedFns = new(System.StringComparer.Ordinal);

    /// <summary>The top-level functions this unit declares, for an importer to call — see
    /// <see cref="_exportedFns"/>. Meaningful after <see cref="Lower"/> has run.</summary>
    internal IReadOnlyDictionary<string, Symbol> ExportedFns => _exportedFns;

    // ---- lazy (decl-driven) lowering for an imported module (road-to-zig-std S2) ----------------

    /// <summary>True for a module lowered LAZILY (an <c>@import</c>ed module, prepared via
    /// <see cref="Lower"/> with <c>prepareOnly</c>): its container types are registered up front, but a
    /// function's signature + body are lowered only when referenced (<see cref="EnsureDeclLowered"/>).
    /// A leaf like <c>std.ascii</c> thus compiles only the classifiers a program touches — its
    /// std-heavy tails (<c>allocLowerString</c>, <c>HexEscape.format</c>) never lower, matching upstream
    /// "analyze only what's referenced". False for a root unit (eager, unchanged).</summary>
    private bool _lazy;

    /// <summary>In a lazy module, each top-level function name → its raw decl AST (unlowered), so a
    /// reference can lower exactly that function on demand. Built in <see cref="Lower"/>'s prepare pass.</summary>
    private readonly Dictionary<string, Item> _moduleFnDecls = new(System.StringComparer.Ordinal);

    /// <summary>Names in a lazy module whose signature has already been declared (the demand memo, so a
    /// second reference — or a self/mutual call — resolves the existing symbol instead of re-declaring).</summary>
    private readonly HashSet<string> _lazyDeclared = new(System.StringComparer.Ordinal);

    /// <summary>Lazy-module function bodies awaiting lowering, enqueued by <see cref="EnsureDeclLowered"/>
    /// (signature now, body later) and drained at TOP LEVEL by <see cref="DrainPendingBodies"/> — never
    /// nested inside another body's lowering, the re-entrancy discipline the monomorphization worklist
    /// already follows. A body drained here may reference more decls (a sibling call), which enqueue and
    /// are picked up by the same cursor loop.</summary>
    /// <para><c>container</c> is non-null for a method body (declared on demand by
    /// <see cref="EnsureMethodDeclared"/>), so the drain can set <see cref="_currentContainer"/> around it
    /// the way pass 2 does — a <c>@This()</c> in the body has to resolve to its container.</para>
    private readonly List<(Symbol sym, List<(string name, CType type)> ps, Item body, string? container)> _pendingModuleBodies = new();

    /// <summary>Cursor into <see cref="_pendingModuleBodies"/> — so a repeated drain (the graph loops
    /// until no module has pending work) resumes rather than re-lowering.</summary>
    private int _pendingBodyCursor;

    /// <summary>Cursors into the two worklists a lazy module shares with pass 2.5 — see
    /// <see cref="DrainPendingBodies"/>.</summary>
    private int _pendingInstCursor;
    private int _pendingReifiedCursor;

    /// <summary>True while this (lazy) module still has work enqueued but not yet lowered — a referenced
    /// function body, a monomorphized generic instance, or a reified type-returning generic's method.</summary>
    internal bool HasPendingBodies =>
        _pendingBodyCursor < _pendingModuleBodies.Count
        || _pendingInstCursor < _pendingInstantiations.Count
        || _pendingReifiedCursor < _pendingReifiedMethods.Count;

    /// <summary>Ensure a lazy module's function <paramref name="name"/> is DECLARED (signature lowered so
    /// a call can bind to it) and its body ENQUEUED for the top-level drain; returns the function symbol,
    /// or null when the module has no such top-level function (the caller then errors, or treats it as a
    /// namespace member). A referenced function whose signature can't lower (an unmodeled std type) throws
    /// LOUDLY here — deliberately, since the program actually reached it; an UNreferenced one is simply
    /// never touched. Idempotent via <see cref="_lazyDeclared"/>.</summary>
    internal Symbol? EnsureDeclLowered(string name)
    {
        // The module's own table, not a scope lookup: a function declared lazily lands in whatever scope
        // was current at its FIRST reference (a function body's), so once that body is done a later
        // reference could no longer resolve it by scope (std.fmt.charToDigit, first named in one instance
        // body, then called from another).
        if (_lazyDeclared.Contains(name)) { return _exportedFns.GetValueOrDefault(name); }
        if (!_moduleFnDecls.TryGetValue(name, out var d)) { return null; }
        var e = d.Content switch
        {
            Zig.FnDef f          => DeclareFn(f.Arg1, f.Arg3, f.Arg6, f.Arg7),
            Zig.FnDefNoArgs f    => DeclareFn(f.Arg1, null, f.Arg5, f.Arg6),
            Zig.FnDefErr f       => DeclareFn(f.Arg1, f.Arg3, f.Arg7, f.Arg8, errUnion: true),
            Zig.FnDefNoArgsErr f => DeclareFn(f.Arg1, null, f.Arg6, f.Arg7, errUnion: true),
            _ => throw new IrUnsupportedException("lazy module decl is not a function: " + (d.Content?.GetType().Name ?? "null")),
        };
        // Memoized only once declared, so a signature that failed to lower fails the same way at the next
        // reference instead of resolving to nothing.
        _lazyDeclared.Add(name);
        _exportedFns[name] = e.sym;
        // A generic / type-returning template has no base body to lower (an instantiation body is drained
        // via the monomorphization worklist); everything else enqueues its body for the top-level drain.
        if (!_genericFns.ContainsKey(e.sym) && !_typeReturningGenerics.ContainsKey(e.sym))
        {
            _pendingModuleBodies.Add((e.sym, e.ps, e.body, null));
        }
        return e.sym;
    }

    /// <summary>Ensure the method <paramref name="method"/> of container <paramref name="container"/> is
    /// declared, wherever it was written, and return its symbol — the method analogue of
    /// <see cref="EnsureDeclLowered"/> (road-to-zig-std S4d). A method declared eagerly (a root unit's) is
    /// already in <see cref="_methods"/> and this is a plain read; one belonging to a LAZILY-prepared
    /// module is declared here, on first call, and its body enqueued for that module's drain. Null when no
    /// such method exists anywhere, so the caller reports it by name.</summary>
    private Symbol? EnsureMethodDeclared(string container, string method)
    {
        if (_methods.TryGetValue(container, out var known) && known.TryGetValue(method, out var sym))
        {
            return sym;
        }
        if (!_lazyMethodDecls.TryGetValue((container, method), out var pending))
        {
            // A file-as-struct type's methods are its module's top-level functions (road-to-zig-std G3).
            return _shared.FileStructOwners.TryGetValue(container, out var fileOwner)
                ? fileOwner.FileStructFn(method)
                : null;
        }
        // Declared BY ITS OWNER: the signature's types (and the body's) are spelled in that module's
        // source, so they must resolve in that module's environment, not the caller's.
        var owner = pending.owner;
        var e = owner.DeclareMethod(container, pending.decl);
        if (!owner.IsFnTemplate(e.sym)) { owner._pendingModuleBodies.Add((e.sym, e.ps, e.body, container)); }
        return e.sym;
    }

    /// <summary>A type this module DECLARES, by its source name — the cross-module read behind
    /// type-position navigation (road-to-zig-std S4d), the type analogue of
    /// <see cref="EnsureDeclLowered"/>. Needs no on-demand work: a prepared module registers all of its
    /// container types (structs / unions / enums, with their consts and nested containers) in pass 0,
    /// before any laziness kicks in, so the lookup is a plain read. Null when this module declares no
    /// such type — the caller reports that loudly, naming the module.
    /// <para>The lookup key is the container's plain SOURCE name; the returned <see cref="CType"/> carries
    /// its module-qualified emitted name (<c>&lt;module&gt;__&lt;Name&gt;</c>, <see cref="QualifyTypeName"/>),
    /// so two modules may each declare a same-named aggregate.</para></summary>
    internal CType? ResolveExportedType(string name) => ResolveExportedType(name, 0);

    /// <summary>A lazy module's top-level consts whose RHS is a CALL, not evaluated at prepare (see pass 0):
    /// name → the call. The first TYPE-position use evaluates one (<see cref="TryDeferredTypeAlias"/>).</summary>
    private readonly Dictionary<string, Item> _deferredTypeCalls = new(System.StringComparer.Ordinal);

    /// <summary>Evaluate the deferred top-level call <paramref name="name"/> as a type alias, on its first use
    /// as a type: true (and the alias registered, with its declared width) when it denotes a type. Consumed
    /// either way, so a call that turns out to be a VALUE stays the lazy value const it already is.</summary>
    private bool TryDeferredTypeAlias(string name, out CType type)
    {
        type = CType.Int;
        if (!_deferredTypeCalls.Remove(name, out var rhs) || !TryTypeAliasRhs(rhs, out type)) { return false; }
        _typeAliases[name] = type;
        SetDeclaredIntBits(name, DeclaredBitsOfTypeArg(rhs));
        return true;
    }

    private CType? ResolveExportedType(string name, int hops)
    {
        RaiseIfPoisoned(name);   // a container whose lazy registration failed raises at this reference
        if (_containerTypes.TryGetValue(name, out var t)) { return t; }
        // A top-level type ALIAS (`pub const ArgSetType = u32;` in fmt.zig), recorded in pass 0.
        if (_typeAliases.TryGetValue(name, out var alias)) { return alias; }
        if (TryDeferredTypeAlias(name, out var deferred)) { return deferred; }
        // A re-export (`pub const Pair = inner.Pair;`) names a type declared elsewhere.
        return ResolveAliasedType(name, hops);
    }

    /// <summary>Lower everything this lazy module has enqueued (from each cursor onward). Runs at TOP
    /// LEVEL only. A body may reference a sibling (declaring + enqueuing it) — the cursor loops pick
    /// those up. Since road-to-zig-std S4d this covers all three worklists a lazy module can gather
    /// (referenced bodies, generic instances, reified type-returning generics' methods), not bodies
    /// alone: a navigated type may reify in this module, and its methods would otherwise be declared
    /// and never lowered. A deferred <c>comptime</c> fold in any of these bodies resolves with every
    /// other module's, after the graph drains (<see cref="ZigModuleGraph.ResolveComptimeFolds"/>).</summary>
    internal void DrainPendingBodies()
    {
        // The lazy-module analogue of pass 2.5, over three mutually-feeding worklists: a referenced
        // function's body may call a generic (enqueueing an instance) or name a reified type-returning
        // generic (enqueueing its methods, road-to-zig-std G4), and either of those bodies may reference
        // another sibling decl. So alternate the cursors until all three are exhausted rather than
        // draining each once. Every list only grows on a fresh memo miss (a new mangled instance, capped
        // by MaxInstantiations; a new mangled reified container, memoized before its members bind; a
        // not-yet-declared decl name), so this terminates. Reached through the graph's own fixpoint loop
        // (ZigModuleGraph.DrainAll), which re-visits a module that gained work while another drained.
        while (HasPendingBodies)
        {
            while (_pendingBodyCursor < _pendingModuleBodies.Count)
            {
                var e = _pendingModuleBodies[_pendingBodyCursor++];
                if (!BeginBody(e.sym)) { continue; }   // lowered on demand already (E2)
                _currentContainer = e.container;   // a method body's `@This()`, as in pass 2
                LowerFnBody(e.sym, e.ps, e.body);
                _currentContainer = null;
            }
            for (; _pendingInstCursor < _pendingInstantiations.Count; _pendingInstCursor++)
            {
                if (BeginBody(_pendingInstantiations[_pendingInstCursor].Instance))
                {
                    LowerInstantiationBody(_pendingInstantiations[_pendingInstCursor]);
                }
            }
            for (; _pendingReifiedCursor < _pendingReifiedMethods.Count; _pendingReifiedCursor++)
            {
                if (BeginBody(_pendingReifiedMethods[_pendingReifiedCursor].Method))
                {
                    LowerReifiedMethodBody(_pendingReifiedMethods[_pendingReifiedCursor]);
                }
            }
        }
    }

    /// <summary>The flat global error set: each distinct <c>error.Foo</c> name → a stable
    /// non-zero code (0 is the success sentinel). Shared across the units of one build (the
    /// caller passes one dictionary) so a given error name gets one code program-wide — V1
    /// erases the error SET, so a single space suffices. See <see cref="ErrorCode"/>.</summary>
    private readonly Dictionary<string, int> _errorCodes;

    /// <summary>The return type of the function whose body is currently being lowered (null
    /// outside a body / for a void-less return). When it is a <see cref="CType.ErrorUnion"/>,
    /// <c>return</c> wraps its value as an error union (<see cref="LowerReturn"/>).</summary>
    private CType? _currentFnRet;

    /// <summary>True while lowering a function body that contains at least one <c>errdefer</c>
    /// (pre-scanned in <see cref="LowerFnBody"/>). When set, a <c>return error.X;</c> is routed
    /// through a thrown <see cref="ZigErrorThrow"/> rather than a direct <see cref="ErrUnionErr"/>
    /// return, so the error propagates through the <c>errdefer</c> <c>catch</c>(es) on the stack
    /// (Milestone H). A function with no <c>errdefer</c> keeps the direct-return form untouched.</summary>
    private bool _currentFnHasErrdefer;

    /// <summary>The function whose body is lowering now (null outside one): the caller of every runtime call edge
    /// recorded for the comptime-return check (task #92).</summary>
    private Symbol? _currentFnSym;

    /// <summary>Above zero while lowering code zig evaluates at COMPILE time (a <c>comptime</c> expression or block, a
    /// type body, an array extent, a global initializer): a call there is not a runtime call (task #92).</summary>
    private int _comptimeDepth;

    /// <summary>Functions declared <c>inline fn</c> (and their generic instances), by symbol (task #92).</summary>
    private readonly HashSet<Symbol> _zigInlineFns = new();

    /// <summary>Runtime call edges, caller to callees, of the whole build (task #92): shared through the module graph.</summary>
    private Dictionary<Symbol, HashSet<Symbol>> _runtimeCalls => _moduleGraph?.RuntimeCalls ?? _ownRuntimeCalls;

    /// <summary>The runtime call edges of a lowering built without a module graph.</summary>
    private readonly Dictionary<Symbol, HashSet<Symbol>> _ownRuntimeCalls = new();

    /// <summary>Functions whose body only compiles at comptime, build-wide, with the error a runtime call reports: a
    /// non-inline one returning from a <c>comptime { }</c> block (task #92), one iterating a tuple (task #100).</summary>
    private Dictionary<Symbol, string> _comptimeReturnFns => _moduleGraph?.ComptimeReturnFns ?? _ownComptimeReturnFns;

    /// <summary>The comptime-only functions of a lowering built without a module graph.</summary>
    private readonly Dictionary<Symbol, string> _ownComptimeReturnFns = new();

    /// <summary>Record that the function lowering now calls <paramref name="callee"/> at runtime (task #92).</summary>
    private void RecordRuntimeCall(Symbol callee)
    {
        // A call lowered only for the comptime interpreter (a function's `comptime { }` result block) never runs.
        if (_comptimeDepth > 0 || _loweringForComptimeEval > 0 || _currentFnSym is not { } caller) { return; }
        if (!_runtimeCalls.TryGetValue(caller, out var callees))
        {
            callees = new HashSet<Symbol>();
            _runtimeCalls[caller] = callees;
        }
        callees.Add(callee);
    }

    /// <summary>zig's "function called at runtime cannot return value at comptime" (task #92): a non-inline function
    /// returning from a <c>comptime { }</c> block may only be called at compile time. The runtime call graph is walked
    /// from <paramref name="roots"/> (<c>main</c>, or every top-level function of a program without one), so a function
    /// reached only through <c>comptime f()</c>, a global initializer or a function nothing calls is not an error, as in
    /// zig, which analyzes a function only once it is referenced. A comptime-only instance is never entered.</summary>
    internal static void CheckComptimeReturnsAtRuntime(IEnumerable<Symbol> roots, Dictionary<Symbol, HashSet<Symbol>> calls,
        Dictionary<Symbol, string> comptimeReturnFns, HashSet<Symbol> comptimeOnlyFns)
    {
        if (comptimeReturnFns.Count == 0) { return; }
        var seen = new HashSet<Symbol>();
        var work = new Stack<Symbol>();
        foreach (var root in roots)
        {
            if (seen.Add(root)) { work.Push(root); }
        }
        while (work.Count > 0)
        {
            var fn = work.Pop();
            if (comptimeReturnFns.TryGetValue(fn, out var error)) { throw new CompileException(error); }
            if (!calls.TryGetValue(fn, out var callees)) { continue; }
            foreach (var callee in callees)
            {
                if (!comptimeOnlyFns.Contains(callee) && seen.Add(callee)) { work.Push(callee); }
            }
        }
    }

    /// <summary>The roots of the runtime call graph for <see cref="CheckComptimeReturnsAtRuntime"/>: this root module's
    /// <c>main</c>, or every top-level function when it has none (a library).</summary>
    internal IEnumerable<Symbol> RuntimeRoots()
        => _exportedFns.TryGetValue("main", out var main)
            ? new[] { main }
            : _exportedFns.Values.Where(s => s.Kind == SymKind.Func);

    /// <summary>Monotonic counter for destructure temporaries (<c>__tupN</c>): a destructure
    /// <c>const a, const b = e;</c> evaluates <c>e</c> ONCE into <c>__tupN</c>, then binds each
    /// name to its positional element. The temp lives in the enclosing (brace-less
    /// <see cref="Seq"/>) scope alongside the binders, so repeated destructures in one block need
    /// distinct temp names (Milestone G).</summary>
    private int _tupleTempCounter;

    /// <summary>The ANF statement-hoist buffer (the "sub-expression positions" milestone). When
    /// non-null, a value-producing construct that lowers to STATEMENTS — a side-effecting/capturing
    /// <c>catch</c>, a <c>catch return</c>/<c>orelse return</c> — appearing in a SUB-expression
    /// position appends its pre-statements + a result temp here and evaluates to a bare
    /// <see cref="VarRef"/>; the enclosing statement then runs the buffer first (a brace-less
    /// <see cref="Seq"/>, so the temps stay in scope). Installed by <see cref="Hoisted"/> only at
    /// eval-safe statement points (return / expr-stmt / assignment / decl init) — NOT a loop
    /// condition (a per-iteration re-eval), where it stays null and the construct is rejected.</summary>
    private List<CStmt>? _hoist;

    /// <summary>True once a SIDE-EFFECTING evaluation (a call) has occurred in the current
    /// <see cref="_hoist"/> scope BEFORE the point being lowered. A construct that wants to hoist
    /// checks this FIRST: hoisting past a prior side effect would reorder it (the hoisted pre runs
    /// before the statement, hence before the earlier side effect), so that case is rejected. A
    /// hoisted construct's OWN internals are sequenced into the buffer, so they restore the flag
    /// (they don't count against a later hoist). See <see cref="Hoisted"/>.</summary>
    private bool _hoistImpureSeen;

    /// <summary>Monotonic counter for ANF result temporaries (<c>__anfN</c>).</summary>
    private int _anfTempCounter;

    /// <summary>An enclosing labeled value-block (<c>blk: { … break :blk v; }</c>) being lowered
    /// (Milestone L, part 2). Each <c>break :blk v</c> assigns <c>v</c> to the block's result
    /// <see cref="Temp"/> and jumps to <see cref="EndLabel"/>; the surrounding statement then reads
    /// the temp. <see cref="Sink"/> is the result-location hint (the annotated type / function
    /// return / lvalue type) and <see cref="ResultType"/> is the resolved type — the sink if known,
    /// else the first <c>break</c> value's type. A stack so nested labeled blocks resolve a
    /// <c>break :label</c> innermost-first.</summary>
    private sealed class LabeledBlockTarget
    {
        public required string Label { get; init; }
        public required Symbol Temp { get; init; }
        public required string EndLabel { get; init; }
        public CType? Sink { get; init; }
        public CType? ResultType { get; set; }
    }

    /// <summary>Active labeled value-blocks, innermost on top — see <see cref="LabeledBlockTarget"/>.</summary>
    private readonly Stack<LabeledBlockTarget> _labeledBlocks = new();

    /// <summary>Monotonic counter for labeled-value-block temporaries / end labels
    /// (<c>__blkN</c> / <c>__blkN_end</c>), one per <c>blk: { … }</c> (Milestone L, part 2).</summary>
    private int _blockLabelCounter;

    /// <summary>An enclosing labeled loop (<c>lbl: while (…) { … }</c>) being lowered (Milestone L,
    /// part 3). C# has no labeled break/continue, so a <c>break :lbl</c> / <c>continue :lbl</c> — which
    /// may target an OUTER loop — lowers to a <c>goto</c> to <see cref="BreakLabel"/> (emitted just
    /// after the loop) / <see cref="ContLabel"/> (emitted at the END of the loop body, so the loop's
    /// natural iteration step still runs after the jump). The labels are emitted only when actually
    /// referenced (<see cref="BreakUsed"/> / <see cref="ContUsed"/>), so an unused one draws no C#
    /// "unreferenced label" warning. A stack so a <c>break :lbl</c> resolves the label innermost-first.</summary>
    private sealed class LabeledLoopTarget
    {
        public required string Label { get; init; }
        public required string BreakLabel { get; init; }
        public required string ContLabel { get; init; }
        public bool BreakUsed { get; set; }
        public bool ContUsed { get; set; }
    }

    /// <summary>Active labeled loops, innermost on top — see <see cref="LabeledLoopTarget"/>.</summary>
    private readonly Stack<LabeledLoopTarget> _labeledLoops = new();

    /// <summary>Where an UNLABELED <c>break</c> goes when a <c>switch</c> statement sits between it and its
    /// loop. zig's <c>break</c> in a prong exits the enclosing LOOP, but the prong lowers into a C#
    /// <c>switch</c>, where a bare <c>break</c> exits only the switch (a silent miscompile: the loop kept
    /// iterating). Such a break is a <c>goto</c> to <see cref="BreakLabel"/>, placed just after the loop
    /// when used. <see cref="SwitchDepth"/> counts the statement switches entered since this loop.</summary>
    private sealed class LoopBreakTarget
    {
        public required string BreakLabel { get; init; }
        public bool Used { get; set; }
        public int SwitchDepth { get; set; }
    }

    /// <summary>Every runtime loop being lowered, innermost on top (see <see cref="LoopBreakTarget"/>).</summary>
    private readonly Stack<LoopBreakTarget> _loopBreakTargets = new();

    /// <summary>The loop statement <see cref="LowerLoopWithBreakTarget"/> is lowering right now, so the
    /// re-entry into <see cref="LowerStmt"/> lowers it instead of wrapping it again.</summary>
    private Item? _loopBeingWrapped;

    /// <summary>Monotonic counter for labeled-loop break / continue labels (<c>__loopN_brk</c> /
    /// <c>__loopN_cont</c>), one per labeled loop (Milestone L, part 3).</summary>
    private int _loopLabelCounter;

    /// <summary>A value-position loop (<c>while/for … else …</c>) being lowered as a statement
    /// (Milestone Y, part 2): a <c>break v</c> (unlabeled, innermost) or <c>break :lbl v</c> (matching
    /// <see cref="Label"/>) inside the body assigns <see cref="Temp"/> and jumps to
    /// <see cref="EndLabel"/> (skipping the <c>else</c> value, which supplies the result on normal
    /// completion). <see cref="ResultType"/> is the sink when known, else fixed by the first
    /// <c>break</c> value (or the <c>else</c> value). <see cref="BreakUsed"/> gates emitting the end
    /// label (an else-only loop never jumps there — avoids a C# unreferenced-label warning).</summary>
    private sealed class LoopValueTarget
    {
        public required Symbol Temp { get; init; }
        public required string EndLabel { get; init; }
        public string? Label { get; init; }
        public CType? Sink { get; init; }
        public CType? ResultType { get; set; }
        public bool BreakUsed { get; set; }
    }

    /// <summary>Active value-position loops, innermost on top — see <see cref="LoopValueTarget"/>.</summary>
    private readonly Stack<LoopValueTarget> _loopValues = new();

    /// <summary>Monotonic counter for value-loop result temps / end labels (<c>__lvN</c> /
    /// <c>__lvN_end</c>), one per value-position loop (Milestone Y, part 2).</summary>
    private int _loopValueCounter;

    /// <summary>Container type names declared in this unit (<c>const P = struct {…}</c> /
    /// <c>const C = enum {…}</c>) → the <see cref="CType"/> the name resolves to: a
    /// <see cref="CType.Named"/> for a struct, a <see cref="CType.Enum"/> for an enum.
    /// Populated in pass 0 (before signatures/bodies) so a struct used before its decl —
    /// or a self/forward reference like <c>next: *Node</c> — resolves. Consulted by
    /// <see cref="LowerTypeName"/> ahead of the primitive table.</summary>
    private readonly Dictionary<string, CType> _containerTypes = new(System.StringComparer.Ordinal);

    /// <summary>Per enum name, each member name → its <see cref="SymKind.EnumConst"/> symbol
    /// (carrying the enum <see cref="CType"/> + the constant value). Drives both
    /// <c>EnumName.member</c> (a <see cref="Zig.Field"/> whose base names an enum) and the
    /// bare <c>.member</c> literal at a typed sink (<see cref="ResolveEnumLit"/>) — each
    /// lowering to an <see cref="EnumConstRef"/>, rendered by the shared backend as
    /// <c>EnumName.member</c>.
    /// <para>SHARED down the <c>@import</c> chain for the same reason as <see cref="_methods"/>: an
    /// enum's members belong to the enum, and its name is unique across the emitted program, so an
    /// imported enum's <c>.member</c> resolves at a call site in another module (road-to-zig-std
    /// S4d — before it, no imported type could be named at all).</para></summary>
    private readonly Dictionary<string, Dictionary<string, Symbol>> _enumMembers;

    /// <summary>Per container (struct) name, each method name → the mangled free-function
    /// <see cref="Symbol"/> it lowers to (<c>TypeName_method</c>). Populated in pass 1 (so a
    /// method body can forward-reference a sibling method) and consulted by
    /// <see cref="LowerMethodCall"/> to rewrite a UFCS instance call (<c>p.method(…)</c>) or a
    /// static/associated call (<c>Type.func(…)</c>) to that free function.
    /// <para>SHARED down the <c>@import</c> chain (a prepared module gets its parent's table, like
    /// <see cref="_errorCodes"/>): a method belongs to its container, and a container's name is unique
    /// across the emitted program — <see cref="IrModule.RegisterStructType"/> now enforces that — so one
    /// table lets a call site reach a method declared by ANOTHER module, which is what a navigated type
    /// (road-to-zig-std S4d) or a cross-module reified generic needs. Two independent ROOT units keep
    /// separate tables, exactly as before: they see each other only through <c>@import</c>.</para></summary>
    private readonly Dictionary<string, Dictionary<string, Symbol>> _methods;

    /// <summary>Container methods a LAZILY-prepared module declares but has not lowered — keyed by
    /// (container, method) → the module that owns them plus the raw method AST. A prepared module stops
    /// after registering types (road-to-zig-std S2), so declaring its methods' signatures then would
    /// defeat the point: an unreferenced method whose signature names an unlowerable type must stay
    /// invisible. They are declared on FIRST CALL instead (<see cref="EnsureMethodDeclared"/>) — the
    /// method analogue of <see cref="EnsureDeclLowered"/>. Shared down the <c>@import</c> chain, so a
    /// call site in another module finds both the AST and the module that must lower it.</summary>
    private readonly Dictionary<(string container, string method), (ZigLowering owner, Item decl)> _lazyMethodDecls;

    /// <summary>The tables this unit shares with every module it imports (transitively) — see
    /// <see cref="ZigImportScope"/>. Held so a prepared child can be handed the same scope.</summary>
    private readonly ZigImportScope _shared;

    /// <summary>The container (struct) whose method signature / body is currently being lowered,
    /// so a <c>@This()</c> type resolves to it (null outside a method — a <c>@This()</c> there is
    /// an error). Set around method declaration (<see cref="DeclareMethod"/>) and pass-2 body
    /// lowering, mirroring how <see cref="_currentFnRet"/> tracks the active return type.</summary>
    private string? _currentContainer;

    /// <summary>The name of the function whose body is currently being lowered (pass 2) — the prefix
    /// for an in-function container's mangled IR type name (wall-plan W2, <c>&lt;fn&gt;__&lt;P&gt;</c>).
    /// Set/cleared around each body in <see cref="LowerFnBody"/>.</summary>
    private string _currentFnName = "";

    /// <summary>Mangled IR type names of every in-function container registered so far (wall-plan W2)
    /// — the dup guard, since <see cref="IrModule.RegisterStructType"/> silently no-ops a repeat name
    /// (so a redeclared local would otherwise miscompile). Global for the build: a mangled name is
    /// unique per (function, container), and the IR type it names is emitted once program-wide.</summary>
    private readonly HashSet<string> _localContainers = new(System.StringComparer.Ordinal);

    /// <summary>Plain-name → previous <see cref="_containerTypes"/> binding shadowed by an in-function
    /// container in the CURRENT body (wall-plan W2), restored at body exit so a local <c>const Point =
    /// struct{…}</c> does NOT leak its type into a sibling function (the mangled IR type stays
    /// registered globally — this only scopes the plain-name alias). Reset per function.</summary>
    private readonly List<(string Name, CType? Prev)> _localContainerShadows = new();

    /// <summary>Each inline named-field struct TYPE occurrence (<c>fn f() struct { a: u8 }</c>, a
    /// field/param/var annotation — road-to-zig-std S9, grammar #90) → its synthesized IR type name.
    /// Keyed by the AST <see cref="Item"/> reference, because a Zig struct type is nominal by its
    /// declaration SITE: two textually-identical inline <c>struct {…}</c> forms are DISTINCT types, and
    /// the same occurrence lowered in more than one pass must reify ONE registered type. The synthesized
    /// name (<c>__AnonStruct&lt;n&gt;</c>, module-qualified in an imported module since the counter is per
    /// module) is unique program-wide, so it never collides with a user type
    /// or an in-function W2 container. Keyed by REFERENCE (<see cref="ReferenceEqualityComparer"/>) —
    /// <see cref="Item.Equals"/> compares the grammar symbol ID, so a value-equality dictionary would
    /// collapse every inline <c>struct {…}</c> (all share that ID) into one type; the parse tree is
    /// built once, so a given occurrence is the same object across passes and memoizes correctly.</summary>
    private readonly Dictionary<Item, string> _inlineStructNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>This module's IR-name prefix when it is an IMPORTED module (module-qualified container
    /// naming), null for a root unit — whose names stay exactly as spelled, so a single-file program's
    /// emitted C# is unchanged. Applied by <see cref="QualifyTypeName"/> to every container IR name the
    /// module mints: top-level containers (and so their nested ones, which derive from the parent), W4
    /// reified generic instances and inline anonymous structs. Assigned by the importer
    /// (<see cref="ZigImportScope.ModulePrefixFor"/>).</summary>
    private readonly string? _modulePrefix;

    /// <summary>The file name (without <c>.zig</c>) of the unit being lowered, or null for an in-memory
    /// unit: the source name of the file-as-struct container (<see cref="_fileContainer"/>).</summary>
    private readonly string? _fileStem;

    /// <summary>The IR name of this module's FILE-AS-STRUCT container (road-to-zig-std G3), or null when
    /// the file declares no top-level fields. A zig file is itself a struct, so one with fields at file
    /// scope (<c>Io/Writer.zig</c>: <c>vtable</c>, <c>buffer</c>, <c>end</c>) is an instantiable type
    /// whose methods are the file's own top-level functions. The fields register as an ordinary struct
    /// under this name; <c>@This()</c> at file scope resolves to it; and a method or static call on it
    /// routes to the module's top-level function of that name (<see cref="FileStructFn"/>), so a
    /// function is lowered once whether it is reached as <c>fixed(buf)</c> or <c>Writer.fixed(buf)</c>.</summary>
    private string? _fileContainer;

    /// <summary>The file-as-struct TYPE of this module (see <see cref="_fileContainer"/>), for an importer
    /// that names the module in a type position (<c>const Writer = std.Io.Writer; var w: Writer</c>).</summary>
    internal CType? FileStructType
    {
        get
        {
            if (_fileContainer is not { } fc) { return null; }
            RaiseIfPoisoned(fc);   // its fields failed to lower in a lazy module: raise at this reference
            return _containerTypes[fc];
        }
    }

    /// <summary>This module's top-level function <paramref name="name"/>, as a method of its file-as-struct
    /// container: declared on demand in a lazy module, read from pass 1 in a root unit. Null when there is
    /// no such function; one the resilient parse SKIPPED raises its parse error instead, since "no such
    /// method" would hide the real wall (<c>Writer.print</c>). A GENERIC one never reaches here from a
    /// call: on an instance (<c>w.print(fmt, args)</c>) the method call instantiates it
    /// (<see cref="TryResolveFileStructGenericMethod"/>), and through the type (<c>Writer.print(w, …)</c>)
    /// it is an ordinary exported generic call (<see cref="FileStructGenericTemplate"/>). Any other
    /// generic use is a loud cut, as its template symbol carries only a placeholder signature.</summary>
    private Symbol? FileStructFn(string name)
    {
        var sym = FileStructFnSymbol(name);
        if (sym is null) { RaiseIfSkippedDecl(name); }
        if (sym is not null && (_genericFns.ContainsKey(sym) || _typeReturningGenerics.ContainsKey(sym)))
        {
            throw new IrUnsupportedException(
                $"'{_fileStem}.{name}' is a generic (a `comptime` / `anytype` parameter, or a `type` return) "
                + "used through its file-as-struct type other than as a call, which is not supported yet (road-to-zig-std G3)");
        }
        return sym;
    }

    /// <summary>This module's top-level function <paramref name="name"/> when it is a GENERIC template
    /// (a <c>comptime</c> / <c>anytype</c> parameter), for a call through its file-as-struct type; null
    /// for any other function or none.</summary>
    internal Symbol? FileStructGenericTemplate(string name)
        => _fileContainer is not null && FileStructFnSymbol(name) is { } sym && _genericFns.ContainsKey(sym) ? sym : null;

    /// <summary>The symbol of this module's top-level function <paramref name="name"/>, declaring it on
    /// demand in a lazy module (see <see cref="FileStructFn"/>), or null.</summary>
    private Symbol? FileStructFnSymbol(string name)
        => _lazy ? EnsureDeclLowered(name) : _exportedFns.GetValueOrDefault(name);

    /// <summary>A LAZY module's top-level value <c>const</c>s (name → optional annotation + RHS), recorded
    /// raw by the prepare pass (road-to-zig-std G3/G5). zig evaluates a top-level <c>const</c>'s
    /// initializer at comptime, so a reference lowers the RHS where it is named, with the annotation as
    /// its sink, exactly as a container const is inlined (<see cref="LowerLazyValueConst"/>).</summary>
    private readonly Dictionary<string, (Item? typeItem, Item rhs)> _lazyValueConsts = new(System.StringComparer.Ordinal);

    /// <summary>The lazy value consts being lowered right now, the guard that turns a const depending on
    /// itself into a loud error (zig reports a dependency loop) instead of a stack overflow.</summary>
    private readonly HashSet<string> _lazyValueConstsInProgress = new(System.StringComparer.Ordinal);

    /// <summary>Lower a reference to this lazy module's top-level value const <paramref name="name"/>, or
    /// null when it declares none (see <see cref="_lazyValueConsts"/>).</summary>
    /// <summary>A top-level VALUE const this (lazily prepared) module declares, lowered here for a read
    /// from another module (<c>std.atomic.cache_line</c>); null when it declares no such const.</summary>
    internal CExpr? LowerExportedValueConst(string name) => LowerLazyValueConst(name);

    /// <summary>Whether this module declares <paramref name="name"/> at top level (what <c>@hasDecl(module, name)</c>
    /// asks): a function or variable, a value const, a container, an alias, or a declaration that failed or is a
    /// <c>@compileError</c> tombstone (still declared, as zig's lazy analysis has it).</summary>
    internal bool DeclaresTopLevel(string name) =>
        _lazyValueConsts.ContainsKey(name) || _containerTypes.ContainsKey(name) || _declAliases.ContainsKey(name)
        || _moduleAliasPaths.ContainsKey(name) || _failedContainers.ContainsKey(name) || _poisonedConsts.ContainsKey(name)
        || ResolveExportedDecl(name) is not null;

    /// <summary>One field of a top-level value const that is its struct type's DEFAULT value:
    /// <c>pub const options: Options = if (@hasDecl(root, "std_options")) root.std_options else .{};</c> in std.zig,
    /// read as <c>std.options.fmt_max_depth</c>. The field's default lowers alone, in this module, so the rest of
    /// the struct (std.Options holds a generic fn type and <c>@EnumLiteral()</c> fields) never has to. Null when the
    /// const is not such a value or the field has no recorded default.</summary>
    internal CExpr? LowerDefaultedConstField(string constName, string fieldName)
    {
        if (!_lazyValueConsts.TryGetValue(constName, out var vc) || vc.typeItem?.Content is not Zig.Ident { Arg0: var typeTok })
        {
            return null;
        }
        var init = vc.rhs;
        while (init.Content is Zig.IfExpr ie && TryFoldComptimeCondition(ie.Arg2) is { } taken) { init = taken ? ie.Arg4 : ie.Arg6; }
        if (init.Content is not Zig.AnonStructInitEmpty) { return null; }
        var structName = QualifyTypeName(Tok(typeTok));
        if (!_structFieldDecls.TryGetValue((structName, fieldName), out var decl)
            && !_structFieldDecls.TryGetValue((Tok(typeTok), fieldName), out decl))
        {
            return null;
        }
        return LowerExprSink(decl.Default, LowerType(decl.Type));
    }

    /// <summary>Lower a lazy module's top-level value const <paramref name="name"/> where it is read. An
    /// UNTYPED one takes <paramref name="useSink"/>, the reader's result type: `const default_alignment =
    /// .right;` in std.fmt is an enum literal that only its use can type.</summary>
    /// <summary>The value of a <c>comptime_int</c> initializer. Comptime by definition, so a call in it runs
    /// (<c>cacheLineForCpu(builtin.cpu)</c> in std.atomic), not only the call-free constant folding.</summary>
    private long? ComptimeIntValue(CExpr init) =>
        _ir.ConstEval(init) ?? (_ir.ResolveComptimeFold(init) is { } folded ? _ir.ConstEval(folded) : null);

    private CExpr? LowerLazyValueConst(string name, CType? useSink = null)
    {
        if (!_lazy || !_lazyValueConsts.TryGetValue(name, out var vc)) { return null; }
        if (!_lazyValueConstsInProgress.Add(name))
        {
            throw new IrUnsupportedException($"zig: top-level `const {name}` depends on itself (a dependency loop)");
        }
        _comptimeDepth++;   // a top-level const's initializer is evaluated at compile time (task #92)
        try
        {
            // `pub const cache_line: comptime_int = switch (builtin.cpu.arch) { … };` (std.atomic): a
            // comptime-only integer folds to its literal; it has no runtime type to lower.
            if (vc.typeItem?.Content is Zig.Ident { Arg0: var ctTok } && Tok(ctTok) == "comptime_int")
            {
                return ComptimeIntValue(LowerExprSink(vc.rhs, CType.Long)) is { } ct
                    ? new LitInt(ct.ToString(System.Globalization.CultureInfo.InvariantCulture), ct) { Type = CType.Long }
                    : throw new IrUnsupportedException($"zig `const {name}: comptime_int` must be compile-time-known");
            }
            if (_lazyConstStatics.TryGetValue(name, out var memo)) { return new VarRef(memo) { Type = memo.Type, IsLValue = true }; }
            var sink = vc.typeItem is { } t ? LowerType(t) : useSink;
            // Lowered under a fresh hoist of its own: this module's lowering has no statement in progress at a use site
            // in another module. A value that needed statements first (std.base64's `standard = Codecs{ … }`, whose array
            // fields are copied in after the literal, task #78) becomes a static global with a synthesized initializer.
            CExpr value;
            List<CStmt> pre;
            using (EnterFreshHoist())
            {
                value = LowerExprSink(vc.rhs, sink);
                pre = _hoist ?? new List<CStmt>();
            }
            // An array LITERAL (std.fmt.float's `FLOAT64_POW5_INV_SPLIT: [326][2]u64 = .{ … }`) is one program-lifetime table,
            // as a root global is: it had been rebuilt as a `stackalloc` on every call of every function that read it.
            if (pre.Count == 0 && value is StackArray { Type: CType.Array arrayType } table)
            {
                var (tableElement, tableElems) = FlattenArrayLiteral(name, table);
                var tableSym = AddArrayGlobal(QualifyTypeName(name), arrayType,
                    new PinnedArray(tableElement, tableElems, null) { Type = new CType.Pointer(tableElement) });
                _ir.ConstGlobalInits[tableSym] = table;
                _lazyConstStatics[name] = tableSym;
                return new VarRef(tableSym) { Type = tableSym.Type, IsLValue = true };
            }
            if (pre.Count == 0) { return value; }
            var qualified = QualifyTypeName(name);
            var global = _symbols.Declare(new Symbol
            {
                Name = qualified, Kind = SymKind.Var, Type = value.Type, Storage = Storage.Static, IsGlobal = true,
            });
            _ir.Globals.Add(new GlobalVar(global, InitFunctionCall(qualified, pre, value)));
            _lazyConstStatics[name] = global;
            return new VarRef(global) { Type = global.Type, IsLValue = true };
        }
        finally
        {
            _comptimeDepth--;
            _lazyValueConstsInProgress.Remove(name);
        }
    }

    /// <summary>Each top-level <c>const</c> whose RHS is a dotted path rooted at an import
    /// (<c>const Writer = std.Io.Writer;</c>) → that RHS, recorded WITHOUT resolving (so preparing a
    /// module does not fan out into every module it aliases). A type position that names one resolves it
    /// on demand (<see cref="TryResolveModuleTypeAlias"/>): when the path lands on a file-as-struct module,
    /// the name is that module's type.</summary>
    private readonly Dictionary<string, Item> _moduleAliasPaths = new(System.StringComparer.Ordinal);

    /// <summary>Each TOP-LEVEL <c>const NAME = &lt;name or dotted path&gt;;</c> → its RHS, unresolved: a
    /// candidate DECLARATION alias (road-to-zig-std G3/G5). std re-exports constantly, 633 times in the
    /// pin: <c>pub const indexOfScalar = findScalar;</c> in <c>mem.zig</c>,
    /// <c>pub const AutoHashMap = hash_map.AutoHashMap;</c> in <c>std.zig</c>. A lookup of NAME that finds no
    /// declaration of its own follows the RHS (<see cref="ResolveExportedDecl"/>); recording resolves
    /// nothing, so preparing a module never fans out into what it re-exports. Top-level only, unlike the
    /// function-flat comptime maps, so a body's <c>const y = x;</c> can never pose as a re-export.</summary>
    private readonly Dictionary<string, Item> _declAliases = new(System.StringComparer.Ordinal);

    /// <summary>How many re-export hops a lookup follows before giving up: std's deepest chain is two
    /// or three, so this only bounds a cycle (<c>const a = b; const b = a;</c>).</summary>
    private const int MaxAliasHops = 16;

    /// <summary>The FUNCTION declaration that <paramref name="name"/> names in this module, following
    /// re-export aliases (<see cref="_declAliases"/>) across modules: the module that OWNS it and its
    /// symbol, declared on demand in its owner. A generic template comes back as its template symbol, for
    /// the owner to instantiate (the owner's environment spells its signature). Null when no function is
    /// reachable under that name, so a caller can report it or try another reading.
    /// <para><paramref name="raiseIfSkipped"/> is for a site that will fail when this returns null (a call
    /// being lowered): it raises the parse error of a skipped declaration instead. A PROBE (a type-position
    /// reading, a comptime-only check) leaves it false, since a probe that misses falls through to another
    /// reading, e.g. the curated `std.mem.zeroes` behind a skipped `mem.zig` declaration of that name.</para></summary>
    internal (ZigLowering Owner, Symbol Sym)? ResolveExportedDecl(string name, bool raiseIfSkipped = false)
        => ResolveExportedDecl(name, 0, raiseIfSkipped);

    private (ZigLowering Owner, Symbol Sym)? ResolveExportedDecl(string name, int hops, bool raiseIfSkipped)
    {
        if ((_lazy ? EnsureDeclLowered(name) : _exportedFns.GetValueOrDefault(name)) is { } sym)
        {
            return (this, sym);
        }
        if (hops >= MaxAliasHops || !_declAliases.TryGetValue(name, out var rhs))
        {
            // The name IS declared here, but that declaration did not parse.
            if (raiseIfSkipped) { RaiseIfSkippedDecl(name); }
            return null;
        }
        return rhs.Content switch
        {
            Zig.Ident id => ResolveExportedDecl(Tok(id.Arg0), hops + 1, raiseIfSkipped),
            Zig.Field f => ResolveModulePath(f.Arg0)?.Lowering?.ResolveExportedDecl(Tok(f.Arg2), hops + 1, raiseIfSkipped),
            _ => null,
        };
    }

    /// <summary>The imported module this lowering prepared (null for a root unit, which is parsed
    /// strictly, so it never has a skipped declaration).</summary>
    private ZigModule? _module;

    /// <summary>Whether this module is the std file <paramref name="fileName"/> (directly under the std root), so a
    /// curated std function called BARE inside it lowers like the qualified call from outside.</summary>
    private bool IsStdModule(string fileName) =>
        _module?.Path is { } path && _moduleGraph?.StdRootPath is { } stdRoot
        && string.Equals(System.IO.Path.GetFullPath(path),
                         System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(stdRoot) ?? "", fileName)),
                         System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Raise the real wall when <paramref name="name"/> is a top-level declaration of this
    /// module that the resilient parse skipped: "did not parse", with the parse error, instead of the
    /// "unresolved name" a lookup would otherwise report. A no-op for any other name.</summary>
    /// <summary><see cref="RaiseIfSkippedDecl"/> for <paramref name="name"/> and each same-module alias it
    /// names in turn (<c>pub const HashMapUnmanaged = Custom;</c>), up to <see cref="MaxAliasHops"/>.</summary>
    internal void RaiseIfSkippedAlongAliases(string name)
    {
        for (var hops = 0; hops < MaxAliasHops; hops++)
        {
            RaiseIfSkippedDecl(name);
            if (!_declAliases.TryGetValue(name, out var rhs) || rhs.Content is not Zig.Ident next) { return; }
            name = Tok(next.Arg0);
        }
    }

    internal void RaiseIfSkippedDecl(string name)
    {
        if (_module?.SkippedDecls.TryGetValue(name, out var error) is not true) { return; }
        // The parser's message ends with the full expected-symbol list; the head is what locates it.
        var message = error.Message;
        var cut = message.IndexOf("; expected one of", System.StringComparison.Ordinal);
        if (cut > 0) { message = message[..cut]; }
        throw new IrUnsupportedException(
            $"zig `{name}` in {System.IO.Path.GetFileName(_module.Path)} did not parse, so it cannot be used: {message}");
    }

    /// <summary>The TYPE that <paramref name="name"/> names through a re-export alias
    /// (<c>pub const Pair = inner.Pair;</c>), following the chain like <see cref="ResolveExportedDecl"/>:
    /// a container another module declares, or a file-as-struct module. Null when the alias leads to no
    /// type.</summary>
    private CType? ResolveAliasedType(string name, int hops)
    {
        if (hops >= MaxAliasHops || !_declAliases.TryGetValue(name, out var rhs)) { return null; }
        return rhs.Content switch
        {
            Zig.Ident id => ResolveExportedType(Tok(id.Arg0), hops + 1),   // std.hash.crc's `Crc32 = Crc32IsoHdlc`, a type-call alias
            Zig.Field f => ResolveModulePath(rhs)?.Lowering?.FileStructType
                ?? ResolveModulePath(f.Arg0)?.Lowering?.ResolveExportedType(Tok(f.Arg2), hops + 1),
            _ => null,
        };
    }

    /// <summary>True when a top-level <c>const</c> is comptime-only because it aliases something with no
    /// runtime value: a module (a namespace or a file-as-struct type), a type, or a function. The root
    /// unit's global pass skips such a binding rather than lowering <c>util.f</c> as a value.</summary>
    private bool IsComptimeOnlyAlias(string name)
        => IsModuleAlias(name) || _rootSelfAliases.Contains(name)
        || (_declAliases.ContainsKey(name)
            // Only a function OWNED elsewhere: a same-file `const f2 = f;` keeps its fn-pointer global,
            // so `&f2` and passing `f2` as a value still work.
            && (ResolveExportedDecl(name) is { Owner: var owner } && owner != this
                || ResolveAliasedType(name, 0) is not null));

    /// <summary>True when <paramref name="name"/> is a recorded <see cref="_moduleAliasPaths"/> chain that
    /// resolves to a module: a namespace (or a file-as-struct type), never a runtime value.</summary>
    private bool IsModuleAlias(string name)
        => _moduleAliasPaths.TryGetValue(name, out var path) && ResolveModulePath(path) is not null;

    /// <summary>Resolve a bare name bound to a MODULE (an import, or a recorded
    /// <see cref="_moduleAliasPaths"/> chain) whose file is a struct, to that struct type, memoizing it as
    /// an ordinary type alias. False for any other name, and for a module with no top-level fields (a
    /// namespace, not a type). Called only from type-shaped positions, so a module is prepared only when
    /// a program actually asks for it as a type.</summary>
    private bool TryResolveModuleTypeAlias(string name, out CType type)
    {
        type = CType.Int;
        var module = _moduleAliasPaths.TryGetValue(name, out var path) ? ResolveModulePath(path)
            : _importSpecs.ContainsKey(name) ? ResolveImport(name)
            : null;
        // The path may also end at a TYPE a module declares (`const Pair = lib.Pair;`), rather than at a
        // file-as-struct module.
        var fileType = CuratedStdFileType(module)
            ?? module?.Lowering?.FileStructType
            ?? (module is null && path?.Content is Zig.Field pf
                ? ResolveModulePath(pf.Arg0)?.Lowering?.ResolveExportedType(Tok(pf.Arg2))
                : null)
            // Or a NESTED type further down (`const CpuModel = std.Target.Cpu.Model;` in std/Target/x86.zig).
            ?? (module is null && path is { Content: Zig.Field } ? TryResolveModuleNestedType(path)?.Type : null);
        if (fileType is null) { return false; }
        _typeAliases[name] = fileType;
        type = fileType;
        return true;
    }

    /// <summary>The std FILES a curated type stands for, by path under the std root: std's own files name them by
    /// import (mem.zig's <c>pub const Allocator = @import("mem/Allocator.zig");</c>), not by the <c>std.mem.Allocator</c>
    /// path the curated set is keyed on, so the file itself has to map back to the curated type.</summary>
    private static readonly Dictionary<string, string> CuratedStdFiles = new(System.StringComparer.Ordinal)
    {
        ["mem/Allocator.zig"] = "std.mem.Allocator",
    };

    /// <summary>The curated type <paramref name="module"/> stands for (<see cref="CuratedStdFiles"/>), or null when it
    /// is not one of those std files: std.mem.join's <c>allocator: Allocator</c> is then the runtime allocator, whose
    /// <c>alloc</c> / <c>free</c> the curated model owns, exactly as <c>std.mem.Allocator</c> from user code.</summary>
    private CType? CuratedStdFileType(ZigModule? module)
    {
        if (module is null || _moduleGraph?.StdRootPath is not { } stdRoot
            || System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(stdRoot)) is not { } stdDir)
        {
            return null;
        }
        var relative = System.IO.Path.GetRelativePath(stdDir, System.IO.Path.GetFullPath(module.Path)).Replace('\\', '/');
        return CuratedStdFiles.TryGetValue(relative, out var curated) && StdTypes.TryGetValue(curated, out var make)
            ? make()
            : null;
    }

    /// <summary>The IR name for a container this module declares under source name
    /// <paramref name="name"/>: <c>prefix__name</c> in an imported module, the name itself in a root unit.
    /// The plain source name still resolves inside the module (it maps to the qualified type); only the
    /// EMITTED name is qualified, which is what has to be unique program-wide.
    /// <para>A ROOT unit's name is qualified too when it is one the runtime declares
    /// (<see cref="RuntimeTypeNames"/> — <c>const Allocator = struct {…}</c> would otherwise emit a second
    /// C# <c>Allocator</c> and fail to build): <c>root__Allocator</c>.</para></summary>
    /// <summary>True when an import spec, read relative to this module's directory, names the configured
    /// std ROOT (<c>std.zig</c> itself, as std's own files import it).</summary>
    private bool IsStdRootSpec(string spec)
    {
        if (_moduleGraph?.StdRootPath is not { } stdRoot || _importerDir is null) { return false; }
        var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(spec) ? spec : System.IO.Path.Combine(_importerDir, spec));
        return string.Equals(full, System.IO.Path.GetFullPath(stdRoot), System.StringComparison.OrdinalIgnoreCase);
    }

    private string QualifyTypeName(string name) => _modulePrefix is { } p
        ? $"{p}__{name}"
        : RuntimeTypeNames.IsReserved(name) ? $"root__{name}" : name;

    /// <summary>The comptime VALUE of each <c>const</c> that binds a comptime-known literal (a string or
    /// an integer today) — the seed of the comptime-value engine (road-to-zig-std S5). Populated as a
    /// side effect of <see cref="TryComptimeConstBinding"/> (the runtime decl still emits, so a runtime
    /// use of the name is unaffected); consulted by <see cref="EvalComptimeValue"/> so a comptime context
    /// — today only a <c>++</c>/<c>**</c> operand or repeat count — resolves the name to its literal and
    /// folds (<c>const p = "a"; const m = p ++ "b";</c>, <c>const n = 3; const r = s ** n;</c>). Keyed by
    /// NAME (function-flat, like <see cref="_typeAliases"/> — the W1 leniency); a later re-binding
    /// overwrites, which matches the sequential monomorphization drain. Scalar/string only in V1 — an
    /// aggregate (array/struct) comptime value is the next S5 sub-brick.</summary>
    private readonly Dictionary<string, CExpr> _comptimeValues = new(System.StringComparer.Ordinal);

    /// <summary>The comptime ARRAY value of each <c>const</c> bound to a typed array literal
    /// (<c>const a = [_]u8{1,2};</c> — road-to-zig-std S5) — stored as its RAW element-type item + raw
    /// positional element items, NOT a lowered value, so recording is pure (no lowering, safe in any
    /// pass incl. top-level pass 0) and a <c>++</c>/<c>**</c> use re-feeds the shared
    /// <see cref="BuildArrayInit"/> at the merged extent exactly like a direct <c>[_]T{…}</c> operand —
    /// preserving every element form (char/enum-lit/nested) that <see cref="LowerArrayElems"/> handles.
    /// Separate from <see cref="_comptimeValues"/> (scalar/string) because the array fold works on raw
    /// AST items, not a lowered <see cref="StackArray"/>. Name-keyed, same leniency.</summary>
    private readonly Dictionary<string, (Item ElemTypeItem, IReadOnlyList<Item> Items)> _comptimeArrayConsts = new(System.StringComparer.Ordinal);

    /// <summary>Alias-name → previous <see cref="_typeAliases"/> binding shadowed by a comptime-TYPE
    /// parameter seed while lowering a generic instance's signature / body (wall-plan W3b), restored so
    /// a type param <c>T ↦ i32</c> does not leak into the next drained instance / a sibling function,
    /// and a nested instantiation whose type param shares the name <c>T</c> does not clobber the outer
    /// one. The proven W2 shadow pattern (<see cref="_localContainerShadows"/>), applied to the
    /// function-flat type-alias map; reset per body / per instantiation signature. Carries the
    /// previous <see cref="_declaredIntBits"/> entry too, so the declared width that rides alongside
    /// a type binding is saved and restored in lockstep with the binding itself.</summary>
    private readonly List<(string Name, CType? Prev, int? PrevBits)> _typeAliasShadows = new();

    /// <summary>Per container name, each <c>const Self = @This();</c> alias → the container's own
    /// type. A container-scoped self alias is the ubiquitous Zig idiom for naming the receiver type
    /// inside its methods without repeating the container name (any alias name works, not just
    /// <c>Self</c>). Populated in pass 0b (so a method signature can spell its receiver as the
    /// alias); consulted by <see cref="ResolveSelfAlias"/> only while a method of that container is
    /// being lowered (<see cref="_currentContainer"/> set), so the alias is genuinely scoped — two
    /// containers may each declare <c>const Self = @This();</c> without colliding. (A non-<c>@This()</c>
    /// value const — a namespaced constant — is not lowered yet; it needs top-level globals.)</summary>
    private readonly Dictionary<string, Dictionary<string, CType>> _selfAliases = new(System.StringComparer.Ordinal);

    /// <summary>Per parent container name, each NESTED container decl (<c>const Inner = struct {…};</c>
    /// as a struct-body member — road-to-zig-std S9, grammar #89) → the synthesized type of the nested
    /// struct, registered under a parent-mangled IR name (<c>Parent__Inner</c>). Scoped exactly like
    /// <see cref="_selfAliases"/>: consulted by <see cref="ResolveNestedType"/> only while a method of
    /// the parent (or a container nested inside it) is in scope — its fields, consts and methods, i.e.
    /// while <see cref="_currentContainer"/> is it or a descendant — so the plain name <c>Inner</c>
    /// resolves without leaking globally, and two parents may each nest a same-named <c>Inner</c>
    /// (pervasive in std) without colliding. Any container kind nests (struct / enum / union, with
    /// methods and consts, to any depth — pass 0 registers each as an ordinary container), and
    /// <c>Parent.Inner</c> resolves qualified through this map too.</summary>
    private readonly Dictionary<string, Dictionary<string, CType>> _nestedContainerTypes = new(System.StringComparer.Ordinal);

    /// <summary>A nested container's (mangled) name → its enclosing container's name — the lexical
    /// scope chain <see cref="ResolveNestedType"/> walks, so a nested container's own members (and a
    /// grandchild's) can name a sibling or an outer nested type by its plain name, as zig allows.</summary>
    private readonly Dictionary<string, string> _containerParents = new(System.StringComparer.Ordinal);

    /// <summary>Per <c>(struct name, field name)</c>, the RAW default-value AST of a
    /// <c>field: T = default</c> declaration (std S9). Stored unlowered and materialized lazily — a
    /// <c>.{…}</c> literal that OMITS the field appends <c>LowerExprSink(default, fieldType)</c>
    /// (see <see cref="BuildStructInit"/>), so a NON-ZERO default is honored (a zero default already
    /// matched C#'s zero-init). Keyed by the registered struct name, so a mangled in-function
    /// container (<c>&lt;fn&gt;__&lt;P&gt;</c>) and a top-level struct never collide.</summary>
    private readonly Dictionary<(string Struct, string Field), Item> _structFieldDefaults = new();

    /// <summary>Each defaulted struct field's declared type and default ASTs, by (struct, field), recorded before
    /// the struct's field types lower (see <see cref="RegisterStruct"/>), so they survive a failed registration.</summary>
    private readonly Dictionary<(string Struct, string Field), (Item Type, Item Default)> _structFieldDecls = new();

    /// <summary>Each top-level <c>const</c> of this module → its raw RHS, recorded before pass 0 in both
    /// lowering modes. What lets a comptime CONDITION fold a module-level bool while containers are still
    /// registering (debug.zig's <c>runtime_safety = switch (builtin.mode) {…}</c>, read by
    /// <c>SafetyLock</c>'s field default and type const), before any global is declared.</summary>
    private readonly Dictionary<string, Item> _topLevelConstRhs = new(System.StringComparer.Ordinal);

    /// <summary>The top-level consts <see cref="TryFoldComptimeCondition"/> is folding, so a const that
    /// names itself does not recurse.</summary>
    private readonly HashSet<string> _foldingTopLevelConsts = new(System.StringComparer.Ordinal);

    /// <summary>Per container name, each namespaced VALUE <c>const</c> member → its (optional type
    /// annotation + ) right-hand-side expression, stored unlowered. A container-level <c>const</c>
    /// is a comptime constant in Zig, so a <c>Type.NAME</c> use inlines the expression — lowered
    /// fresh at each use site (with the annotation as its sink), which needs no global storage. So a
    /// <c>const max = 100;</c> / <c>const default = Color.red;</c> member reads as <c>Type.max</c> /
    /// <c>Type.default</c>. A const RHS may reference a SIBLING const by bare name (Milestone R, part
    /// 6) — resolved against this table during the re-lower (<see cref="_currentConstContainer"/>).</summary>
    private readonly Dictionary<string, Dictionary<string, (Item? typeItem, Item rhs)>> _containerConsts = new(System.StringComparer.Ordinal);

    /// <summary>Per container name, each namespaced mutable <c>var</c> member → its lowered global
    /// symbol (Milestone R, part 6). A container-level <c>var</c> is a namespaced mutable global; it
    /// lowers to a real <see cref="GlobalVar"/> under a mangled <c>Container_name</c> symbol (pass 1.5,
    /// <see cref="LowerContainerVar"/>), and a <c>Type.name</c> read/write resolves to a
    /// <see cref="VarRef"/> of that symbol (an lvalue). Populated before pass 2 so bodies resolve it.
    /// V1: scalar only (an array/aggregate container var is rejected).</summary>
    private readonly Dictionary<string, Dictionary<string, Symbol>> _containerVars = new(System.StringComparer.Ordinal);

    /// <summary>Container <c>var</c>s collected in pass 0b (container, name, optional type, RHS),
    /// lowered to globals in pass 1.5 — deferred so the initializer can reference functions (declared
    /// in pass 1) and an untyped var can infer from a fully-resolvable RHS.</summary>
    private readonly List<(string container, string name, Item? typeItem, Item rhs)> _pendingContainerVars = new();

    /// <summary>Deferred <c>comptime EXPR</c> folds (Milestone T), collected as they are lowered and
    /// resolved in a post-pass once every function body is lowered, so a <c>comptime fib(10)</c> can
    /// interpret its callee regardless of declaration order. Each node is shared by reference in the IR;
    /// resolving it patches its <see cref="ComptimeFold.Resolved"/> in place. The queue is the module
    /// graph's (<see cref="ZigModuleGraph.PendingComptimeFolds"/>), resolved after every module drains,
    /// because a fold may call a function a lazy module owns (<c>std.math.maxInt(u8)</c>); a lowering
    /// with no graph resolves its own at the end of <see cref="Lower"/>.</summary>
    private List<ComptimeFold> _pendingComptimeFolds => _moduleGraph?.PendingComptimeFolds ?? _ownComptimeFolds;

    /// <summary>The fold queue of a lowering built without a module graph (see
    /// <see cref="_pendingComptimeFolds"/>).</summary>
    private readonly List<ComptimeFold> _ownComptimeFolds = new();

    /// <summary>Lowering-time values of <c>comptime var</c> / <c>comptime const</c> locals (Milestone T,
    /// part 3 — the loop counter of an <c>inline while</c>). Keyed by Symbol IDENTITY (the same instance
    /// the symbol table hands every reference). A reference to one of these substitutes its CURRENT value
    /// as a literal during lowering (the <c>Zig.Ident</c> case), so the condition / continue-expression /
    /// body of an <c>inline while</c> fold and unroll. Updated as comptime mutations are processed in
    /// source order, matching Zig's sequential comptime semantics. No runtime decl is ever emitted.</summary>
    private readonly Dictionary<Symbol, (long Value, CType Type)> _comptimeVars = new();

    /// <summary>Comptime-known OPTIONAL value parameters (road-to-zig-std S4b) — a generic instance's
    /// <c>comptime x: ?T</c> seed, keyed by the seed Symbol's identity. <c>HasValue</c> distinguishes a
    /// comptime <c>null</c> (which has no runtime representation) from a comptime-known payload
    /// <c>Value</c> (of type <c>Inner</c>). A captured <c>if (x) |y| … else …</c> whose condition is one
    /// of these FOLDS at lowering time to the taken branch (binding <c>y</c> to the literal payload) —
    /// this is how <c>std.ArrayList</c>'s <c>Aligned(T, alignment)</c> selects its <c>Slice</c> type when
    /// <c>alignment</c> is a comptime <c>?mem.Alignment</c>. Lowering-tier only (the interpreter's
    /// value/type firewall stays closed — types are computed here, as in W1/W4).</summary>
    private readonly Dictionary<Symbol, (bool HasValue, long Value, CType Inner)> _comptimeOptionalVars = new();

    /// <summary>The container whose <c>const</c> RHS is currently being re-lowered (Milestone R, part
    /// 6) — lets a bare (unqualified) identifier in that RHS resolve to a SIBLING container const. Null
    /// outside a container-const re-lower (so an unresolved bare ident still errors as before).</summary>
    private string? _currentConstContainer;

    /// <summary>Container consts currently mid-resolution (keyed <c>container.name</c>) — a cycle
    /// guard for sibling-const-by-bare-name, so <c>const a = b; const b = a;</c> errors cleanly rather
    /// than recursing forever.</summary>
    private readonly HashSet<string> _constResolving = new(System.StringComparer.Ordinal);

    /// <summary>A tagged union (<c>union(enum)</c>) lowered to the FAITHFUL C tagged-union shape:
    /// an outer struct <c>{ U_Tag __tag; U_Payload __payload; }</c> whose <c>__payload</c> is a
    /// nested <c>[StructLayout(Explicit)]</c> union (every payload variant overlaid at offset 0,
    /// via the shared C union machinery — <c>IsUnion=true</c>). Overlapping payloads match Zig's
    /// memory model (correct size). A union with only void variants has no <c>__payload</c> (it is
    /// just a tag). Holds what construction (<see cref="BuildUnionInit"/>) and a union
    /// <c>switch</c> (<see cref="LowerUnionSwitch"/>) need.</summary>
    internal sealed record ZigUnionInfo(
        string Name,                    // the outer discriminated-union struct name (`U`)
        CType.Enum TagType,             // the tag enum — auto-synthesized `U_Tag`, or a named `union(SomeEnum)` enum
        string TagFieldName,
        string? PayloadTypeName,        // the nested overlapping-payload union type (null if every variant is void)
        string PayloadFieldName,
        IReadOnlyDictionary<string, CType?> Variants);  // variant name → payload type (null = void)

    /// <summary>Registered tagged unions: the union struct name → its <see cref="ZigUnionInfo"/>.</summary>
    private Dictionary<string, ZigUnionInfo> _unions => _shared.Unions;   // shared: a switch in one module over another's union

    /// <summary>Module-import aliases (Milestone F): the bound name of a <c>const X =
    /// @import("std");</c> → the module string (<c>"std"</c>). Comptime — no runtime decl is
    /// emitted; the alias roots a dotted-path resolution (<see cref="TryResolveStdPath"/>) so
    /// <c>X.heap.page_allocator</c> / <c>X.mem.Allocator</c> resolve. Only <c>"std"</c> is
    /// modeled; any other module errors. Function-flat (no nested-scope shadowing), like the
    /// self-alias / container-const tracking.</summary>
    private readonly Dictionary<string, string> _imports = new(System.StringComparer.Ordinal);

    /// <summary>Type-as-value aliases (wall-plan W1 — the comptime-type foundation): the bound name
    /// of a <c>const T = &lt;type&gt;;</c> → the resolved <see cref="CType"/>. Zig's "types are values"
    /// core (see <c>zig.lalr.yaml</c> header): a type expression is reachable in value position via
    /// <c>CurlySuffix → Type</c>, so <c>const T = i32;</c> / <c>const P = *T;</c> / <c>const O = ?T;</c>
    /// / <c>const T = @TypeOf(x);</c> already PARSE — this map is the lowering-side recognition
    /// (<see cref="TryComptimeConstBinding"/>). Comptime — no runtime decl (<see cref="IsComptimeBound"/>);
    /// a later use of the alias in a type position resolves here through <see cref="LowerTypeName"/>, so
    /// <c>var x: T = 5;</c> / <c>*T</c> / <c>[]T</c> compose over the aliased element for free. Function-flat
    /// (no nested-scope shadowing), like the import / self-alias tracking; a comptime-type-valued
    /// interpreter <c>TypeVal</c> arrives with W3 (a comptime FUNCTION returning a type).</summary>
    private readonly Dictionary<string, CType> _typeAliases = new(System.StringComparer.Ordinal);

    /// <summary><c>anytype</c>-parameter name → its inferred concrete type (wall-plan W5), seeded
    /// (shadow-saved) only while lowering a generic instance's SIGNATURE at the call site
    /// (<see cref="InstantiateGeneric"/>) — so a return / parameter type spelled <c>@TypeOf(param)</c>
    /// resolves to the inferred type even though the param is not yet an in-scope symbol (it becomes one
    /// in the instance body). Consulted by <see cref="TypeOfBuiltin"/> ahead of the throwaway-hoist
    /// operand lowering. Empty outside an anytype instantiation.</summary>
    private readonly Dictionary<string, CType> _anytypeSeeds = new(System.StringComparer.Ordinal);

    /// <summary>Bindings to a PROVABLE allocator (Milestone F/U): a <c>const a =
    /// std.heap.page_allocator;</c> (or <c>c_allocator</c>) records <c>a → CHeap</c>; a <c>const a =
    /// fba.allocator();</c> over a known <c>FixedBufferAllocator</c> local records <c>a → Fba</c>
    /// (Milestone U, with the FBA symbol in <see cref="_fbaAllocatorSites"/>). Either way a later
    /// <c>a.alloc(…)</c> DEVIRTUALIZES (a direct <c>Libc.malloc</c> / a direct FBA bump, no vtable).
    /// Comptime — no runtime decl; a use of <c>a</c> as a VALUE materializes the matching fat pointer
    /// (<c>ZigAlloc.CHeap()</c> / <c>ZigAlloc.FbaAllocator(&amp;fba)</c>). An opaque
    /// <c>std.mem.Allocator</c> parameter is never recorded here (→ indirect dispatch).</summary>
    private readonly Dictionary<string, AllocKind> _defaultAllocatorBindings = new(System.StringComparer.Ordinal);

    /// <summary>For each <c>Fba</c>-kind binding in <see cref="_defaultAllocatorBindings"/> (a
    /// devirtualized <c>const a = fba.allocator();</c>, Milestone U), the backing
    /// <c>FixedBufferAllocator</c> symbol — so a devirtualized <c>a.alloc(…)</c> can build the
    /// <c>&amp;fba</c> context and a value use can materialize <c>ZigAlloc.FbaAllocator(&amp;fba)</c>.</summary>
    private readonly Dictionary<string, Symbol> _fbaAllocatorSites = new(System.StringComparer.Ordinal);

    /// <summary>Names bound to an explicit error-set declaration (Milestone N, part 5): a
    /// <c>const E = error{A, B};</c> records <c>E</c> here. dotcc erases the set, so <c>E</c> carries
    /// no runtime value (it is used only as the ignored set in an <c>E!T</c> return type) — recording
    /// it makes the top-level global pass skip its (non-existent) decl, exactly like the comptime
    /// allocator/import bindings.</summary>
    private readonly HashSet<string> _errorSets = new(System.StringComparer.Ordinal);

    /// <summary>Each declared error set's member names (Milestone X, part 3) — <c>const E = error{A, B}</c>
    /// records <c>E → {A, B}</c>. The runtime code stays flat (membership is NOT a runtime concept),
    /// but the table lets dotcc be a good compiler and REJECT illegal programs: an <c>E.member</c>
    /// where <c>member ∉ E</c>, and a <c>return</c> of an error outside a function's declared set.</summary>
    private readonly Dictionary<string, HashSet<string>> _errorSetMembers = new(System.StringComparer.Ordinal);

    /// <summary>An error-union function's RAW return-type AST, recorded in <see cref="DeclareFn"/>
    /// (pass 1) and resolved to its declared set LAZILY in <see cref="LowerFnBody"/> (pass 2) for the
    /// foreign-error return check (Milestone X, part 3). Deferred because the <c>const E = error{…}</c>
    /// set declarations are processed in pass 1.5 (after pass 1's <c>DeclareFn</c>), so the member
    /// table isn't ready when a signature is declared — but it always is by the time a body is lowered.</summary>
    private readonly Dictionary<Symbol, (Item retType, bool errUnion)> _fnErrorReturnTypes = new();

    /// <summary>The active function's declared error set (<see cref="_fnErrorSets"/> entry), or null
    /// when unconstrained — set per body in <see cref="LowerFnBody"/>, mirroring <see cref="_currentFnRet"/>.
    /// A <c>return error.X</c> / <c>return E.X</c> whose name is outside this set is rejected.</summary>
    private (string? name, HashSet<string> members)? _currentFnErrorSet;

    /// <summary>The provable kind of an allocator operand. <c>CHeap</c> = the statically-known
    /// <c>page_allocator</c>/<c>c_allocator</c> default (→ direct <c>Libc.malloc</c>/<c>free</c>);
    /// <c>Fba</c> = a provable <c>fba.allocator()</c> result (Milestone U → a direct FBA bump over
    /// the <c>&amp;fba</c> in <see cref="_fbaAllocatorSites"/>). Both devirtualize (no vtable).</summary>
    private enum AllocKind { CHeap, Fba }

    /// <summary>The runtime <c>FixedBufferAllocator</c> type name (a <see cref="CType.Named"/>), as
    /// spelled by the spliced <c>ZigAlloc.cs</c> — the second concrete allocator.</summary>
    private const string FbaTypeName = "FixedBufferAllocator";

    /// <summary>The runtime <c>ArenaAllocator</c> type name (a <see cref="CType.Named"/>), as
    /// spelled by the spliced <c>ZigAlloc.cs</c> — the third concrete allocator (Milestone U).</summary>
    private const string ArenaTypeName = "ArenaAllocator";

    /// <summary>The runtime <c>AllocatorVTable</c> type name (Milestone W, part 1b) — the 4-fn
    /// <c>{ alloc, resize, remap, free }</c> table a user-constructed <c>std.mem.Allocator</c>
    /// carries by value, as spelled by the spliced <c>ZigAlloc.cs</c>.</summary>
    private const string VTableTypeName = "AllocatorVTable";

    /// <summary>The runtime <c>Alignment</c> type name (Milestone W, part 1b) — Zig's
    /// <c>std.mem.Alignment</c> threaded through the vtable functions.</summary>
    private const string AlignmentTypeName = "Alignment";

    /// <summary>The discriminant field name on a lowered tagged-union struct — a leading
    /// double-underscore so it can't collide with a user variant (a Zig field name).</summary>
    private const string TagFieldName = "__tag";

    /// <summary>The nested overlapping-payload union field on a lowered tagged-union struct.</summary>
    private const string PayloadFieldName = "__payload";

    /// <summary>Suffix for a synthesized tag enum's name (<c>U</c> → <c>U_Tag</c>).</summary>
    private const string TagSuffix = "_Tag";

    /// <summary>Suffix for the synthesized nested payload-union type (<c>U</c> → <c>U_Payload</c>).</summary>
    private const string PayloadSuffix = "_Payload";

    // Test mode (`dotcc zig test`): `test "…" {}` blocks are lowered to runnable functions
    // and registered in the IR test manifest, rather than dropped. Set at construction.
    private readonly bool _testMode;

    public ZigLowering(IrModule ir, INameLegalizer names, Dictionary<string, int>? errorCodes = null,
        bool testMode = false, ZigModuleGraph? moduleGraph = null, string? importerDir = null,
        ZigImportScope? shared = null, string? modulePrefix = null, string? fileStem = null)
    {
        _modulePrefix = modulePrefix;
        _fileStem = fileStem;
        _shared = shared ?? new ZigImportScope();
        _methods = _shared.Methods;
        _enumMembers = _shared.EnumMembers;
        _lazyMethodDecls = _shared.LazyMethodDecls;
        _ir = ir;
        _names = names;
        _symbols = new SymbolTable(names);
        _errorCodes = errorCodes ?? new Dictionary<string, int>(System.StringComparer.Ordinal);
        _testMode = testMode;
        _moduleGraph = moduleGraph;
        _importerDir = importerDir;
    }

    /// <summary>Prepare an <c>@import</c>ed module for LAZY lowering (road-to-zig-std S2): give it its own
    /// child <see cref="ZigLowering"/> (sharing this unit's IR, legalizer, and error-code space, carrying
    /// the module's own directory so ITS relative imports resolve), registered so the top-level drain
    /// reaches its bodies, and run only the prepare pass (register container types + build the function
    /// decl table). A function is lowered ON DEMAND when referenced (<see cref="EnsureDeclLowered"/>), so
    /// a leaf like <c>std.ascii</c> compiles only the classifiers a program touches. Prepared once per
    /// module (the <see cref="ZigModule.Lowering"/> memo, set BEFORE preparing so an import cycle
    /// terminates).</summary>
    private void EnsureModulePrepared(ZigModule module)
    {
        if (module.Lowering is not null) { return; }
        var dir = System.IO.Path.GetDirectoryName(module.Path);
        var stdDir = _moduleGraph?.StdRootPath is { } stdRoot ? System.IO.Path.GetDirectoryName(stdRoot) : null;
        var child = new ZigLowering(_ir, _names, _errorCodes, _testMode, _moduleGraph, dir, _shared,
            modulePrefix: _shared.ModulePrefixFor(module.Path, stdDir),
            fileStem: System.IO.Path.GetFileNameWithoutExtension(module.Path));
        module.Lowering = child;
        child._module = module;
        _moduleGraph?.RegisterLowering(child);
        child.Lower(module.Parse.Tree, prepareOnly: true, lazy: true);
    }

    /// <summary>Resolve an error name to its stable code in the flat global error set,
    /// assigning the next 1-based code on first sight (0 is reserved for success).</summary>
    private int ErrorCode(string name)
    {
        if (!_errorCodes.TryGetValue(name, out var code))
        {
            code = _errorCodes.Count + 1;
            _errorCodes[name] = code;
        }
        return code;
    }

    private static string Tok(Item it) => NormalizeIdent(it.Content as string
        ?? throw new IrUnsupportedException("expected a token lexeme"));

    /// <summary>
    /// Normalizes a raw token lexeme into the name the rest of lowering uses. A Zig quoted
    /// identifier <c>@"…"</c> (road-to-zig-std S9) is folded to its inner text with any character
    /// that isn't C#-identifier-legal mangled to <c>_</c>, so it can key maps and be emitted
    /// directly. Every other lexeme (operators, keywords, plain identifiers, <c>@name</c> builtins)
    /// is returned unchanged — none of them start with <c>@"</c>.
    /// </summary>
    private static string NormalizeIdent(string lexeme)
    {
        if (lexeme.Length < 3 || lexeme[0] != '@' || lexeme[1] != '"' || lexeme[^1] != '"')
        {
            return lexeme;
        }
        var inner = lexeme[2..^1];
        var sb = new System.Text.StringBuilder(inner.Length + 1);
        // A leading digit can't start a C# identifier — prefix `_` so `@"0"` becomes `_0`.
        if (inner.Length > 0 && char.IsAsciiDigit(inner[0])) { sb.Append('_'); }
        foreach (var c in inner)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    public void Lower(Item root, bool prepareOnly = false, bool lazy = false)
    {
        // Three passes. Pass 0 registers container TYPES (struct/enum) so a signature,
        // body, or another container's field can reference one declared later (a forward /
        // self reference). Pass 1 declares every function signature in the (global) scope —
        // Zig has no prototypes, so a call may forward-reference a function defined later.
        // Pass 2 lowers each body against the now-complete type + signature environment. An
        // `extern fn` prototype is declared in pass 1 too but has no body to lower.
        var decls = Flatten(root);
        // Set the lazy flag BEFORE pass 0 so `@import` binding (TryComptimeConstBinding, pass 0a) knows
        // to record a special/unmodeled import (`builtin`/`root`) as a deferred spec rather than throw.
        _lazy = lazy;

        // Container methods (struct/enum/union), collected across pass 0 as mangled
        // `TypeName_method` free functions and declared in pass 1 (so a method body can
        // forward-reference a sibling). A method is container-kind-agnostic at this point — it is
        // keyed only by the container NAME (see DeclareMethod / LowerMethodCall).
        var containerMethods = new List<(string container, Item fnDef)>();

        // The pass-0 work list: every top-level decl in source order, with each struct's NESTED
        // container members (`pub const Mode = enum {…};` inside `const Number = struct {…}` — std.fmt's
        // shape) spliced in right after their parent, under a parent-mangled name (`Number__Mode`, unique
        // so two parents' like-named nested types never collide in the IR). A nested container is then
        // an ordinary container in every respect — enum or struct or union, with methods, consts and its
        // own nested members — and only its NAME differs; the plain name resolves through the parent
        // chain (ResolveNestedType), and qualified (`Number.Mode`) through the parent's nested map.
        var pass0 = CollectPass0Decls(decls, QualifyTypeName);
        foreach (var decl in decls)
        {
            switch (Unwrap(decl).Content)
            {
                case Zig.ConstDecl c:      _topLevelConstRhs[Tok(c.Arg1)] = c.Arg3; break;
                case Zig.ConstDeclTyped c: _topLevelConstRhs[Tok(c.Arg1)] = c.Arg5; break;
            }
        }

        // A file with top-level FIELDS is itself a struct type (road-to-zig-std G3). Its NAME registers
        // before pass 0a so a `const Writer = @This();` binding, and any container whose field points
        // back at the file type, resolves; the field layout registers in pass 0b like any struct's.
        var topFields = decls.Select(d => Unwrap(d).Content).OfType<Zig.TopField>().Select(t => t.Arg0).ToList();

        // A ROOT namespace file naming itself (`const root = @This();`, then `root.helper()`): a lazy module
        // records the binding as a value const and resolves it through its own ZigModule (IsSelfModuleAlias);
        // the root has none, so it gets a synthetic one over its own tree and lowering.
        if (!lazy && _module is null && topFields.Count == 0)
        {
            foreach (var d in decls.Select(Unwrap))
            {
                if (d.Content is Zig.ConstDecl { Arg3.Content: Zig.BuiltinCallNoArgs self } c && IsThisBuiltin(self.Arg0))
                {
                    _rootSelfAliases.Add(Tok(c.Arg1));
                }
            }
            if (_rootSelfAliases.Count > 0)
            {
                var path = System.IO.Path.Combine(_importerDir ?? "", (_fileStem ?? "root") + ".zig");
                _rootSelfModule = new ZigModule(path, new LALR.CC.ResilientParseResult(root, [])) { Lowering = this };
            }
        }

        // Record every top-level `const NAME = name;` / `= a.b.c;` as a candidate re-export, unresolved.
        foreach (var d in decls.Select(Unwrap))
        {
            if (d.Content is Zig.ConstDecl { Arg3.Content: Zig.Ident or Zig.Field } alias)
            {
                _declAliases[Tok(alias.Arg1)] = alias.Arg3;
            }
        }
        if (topFields.Count > 0)
        {
            // The stem is a file name, so it may hold any character (`my-file.zig`): keep it identifier-safe.
            var stem = new string((_fileStem ?? "root").Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
            var fileContainer = _modulePrefix is null ? "root__" + stem : QualifyTypeName(stem);
            _fileContainer = fileContainer;
            _containerTypes[fileContainer] = new CType.Named(fileContainer);
            _shared.FileStructOwners[fileContainer] = this;
        }

        // Pass 0a: register every container NAME first (a struct/union as a `CType.Named`
        // placeholder, an enum fully — enums are self-contained int constants), so pass 0b
        // can resolve a struct field that points to a struct/enum declared further down. An enum
        // is fully registered here (incl. its consts), so its methods are collected here too.
        foreach (var (containerName, content, parent) in pass0)
        {
            if (containerName is not { } name)
            {
                switch (content)
                {
                    // A top-level comptime allocator/namespace binding (`const std = @import("std");`)
                    // — recorded HERE in pass 0 so a pass-1 signature (`fn f(a: std.mem.Allocator)`)
                    // resolves the import alias. Emits no decl (Milestone F). A non-comptime const
                    // falls through (rejected in pass 1 as an unsupported top-level global).
                    // In a LAZY module a CALL on the right (`pub const Crc3Gsm = Crc(u3, .{ … });` in hash/crc.zig)
                    // is not evaluated at prepare: zig analyses a declaration only when it is referenced, and
                    // preparing std.hash must not run every CRC instantiation. It is a deferred type-alias
                    // candidate (TryDeferredTypeAlias) and, like any unclaimed const, a lazy value const.
                    case Zig.ConstDecl d when _lazy && d.Arg3.Content is Zig.CallArgs or Zig.CallNoArgs:
                        _deferredTypeCalls[Tok(d.Arg1)] = d.Arg3;
                        break;
                    case Zig.ConstDecl d:      TryComptimeConstBinding(Tok(d.Arg1), d.Arg3); break;  // const IDENT = RhsExpr ;
                    case Zig.ConstDeclTyped d: TryComptimeConstBinding(Tok(d.Arg1), d.Arg5); break;  // const IDENT : Type = RhsExpr ;
                }
                continue;
            }
            var registered = RegisterContainerIsolated(name, ContainerDeclName(content),
                () => RegisterContainerName(name, content, containerMethods));
            if (!registered) { continue; }
            // A top-level container of an IMPORTED module registered under its qualified IR name
            // (`fmt__Alignment`); the module's own code — and an importer navigating `fmt.Alignment` —
            // still spells it plainly, so map the source name to the same type.
            if (parent is null && ContainerDeclName(content) is { } plainName && plainName != name
                && _containerTypes.TryGetValue(name, out var qualifiedType))
            {
                _containerTypes[plainName] = qualifiedType;
            }
            if (parent is { } parentName) { ScopeNestedContainer(name, content, parentName); }
        }
        // Pass 0b: build struct field layouts (field types now resolve through 0a), register each
        // struct's/union's consts, and collect their methods. Each runs with the container as the
        // current scope, so a field typed by a NESTED container (`mode: Mode`) resolves — as does
        // `@This()` in a field.
        foreach (var (containerName, content, _) in pass0)
        {
            if (containerName is not { } name || _failedContainers.ContainsKey(name)) { continue; }
            RegisterContainerIsolated(name, ContainerDeclName(content),
                () => RegisterContainerBody(name, content, containerMethods));
        }

        if (_fileContainer is { } fileStruct)
        {
            RegisterContainerIsolated(fileStruct, null, () =>
            {
                using (EnterContainer(fileStruct)) { RegisterStruct(fileStruct, topFields); }
            });
        }
        if (_lazy)
        {
            var moduleContainers = pass0.Select(p => p.Name).OfType<string>();
            FailDependentContainers(_fileContainer is { } fs ? moduleContainers.Append(fs) : moduleContainers);
        }

        // For a lazily-lowered imported module (road-to-zig-std S2), stop after registration: record
        // each top-level function's raw decl so a reference can lower exactly that function on demand
        // (EnsureDeclLowered), and skip the eager signature / global / body passes entirely. Container
        // types + consts registered above are enough for a referenced function to resolve its types.
        if (prepareOnly)
        {
            foreach (var decl in decls)
            {
                var d = Unwrap(decl);
                Item? fnName = d.Content switch
                {
                    Zig.FnDef f          => f.Arg1,
                    Zig.FnDefNoArgs f    => f.Arg1,
                    Zig.FnDefErr f       => f.Arg1,
                    Zig.FnDefNoArgsErr f => f.Arg1,
                    _ => null,
                };
                if (fnName is not null) { _moduleFnDecls[Tok(fnName)] = d; continue; }
                // A top-level VALUE const (`const use_vectors_for_comparison = use_vectors and !builtin.fuzz;`
                // in mem.zig): recorded raw, lowered where it is named (LowerLazyValueConst). A comptime
                // binding pass 0 already claimed (an import, a type alias, a re-export) is not a value.
                var (constName, constType, constRhs) = d.Content switch
                {
                    Zig.ConstDecl c      => (Tok(c.Arg1), (Item?)null, c.Arg3),
                    Zig.ConstDeclTyped c => (Tok(c.Arg1), c.Arg3, c.Arg5),
                    _ => (null, null, null),
                };
                if (constName is not null && constRhs is not null
                    && !_importSpecs.ContainsKey(constName) && !_typeAliases.ContainsKey(constName)
                    && !_declAliases.ContainsKey(constName) && !_moduleAliasPaths.ContainsKey(constName)
                    && !_containerTypes.ContainsKey(constName))
                {
                    _lazyValueConsts[constName] = (constType, constRhs);
                }
            }
            // The container methods collected above are NOT declared (that would lower their signatures,
            // defeating laziness — an unreferenced method naming an unlowerable type must stay
            // invisible). Their ASTs are recorded instead, and the first CALL declares one
            // (EnsureMethodDeclared, road-to-zig-std S4d): now that a navigated type can be named, its
            // methods have to be callable too.
            foreach (var (container, fnDef) in containerMethods)
            {
                _lazyMethodDecls[(container, MethodNameOf(fnDef))] = (this, fnDef);
            }
            return;
        }

        // Pass 1: function signatures. Free functions first (a top-level decl), then struct
        // methods (mangled `TypeName_method` free functions, recorded in `_methods` for call
        // rewriting). Declaring every signature up front lets a call forward-reference any
        // function — including a sibling method.
        var entries = new List<(Symbol sym, List<(string name, CType type)> ps, Item body, string? container)>();
        // Add a free function's pass-1 result to the pass-2 body list — UNLESS it's a generic
        // (a comptime-param template, wall-plan W3a): a generic has no base body to lower; a call
        // instantiates a specialized body per resolved value, drained after pass 2.
        void AddFnEntry((Symbol sym, List<(string name, CType type)> ps, Item body) e)
        {
            // A generic (W3a/W3b) has no base body to lower; a type-returning generic (W4) emits no
            // runtime code at all (each use reifies a type) — skip both from the pass-2 body list.
            if (!_genericFns.ContainsKey(e.sym) && !_typeReturningGenerics.ContainsKey(e.sym))
            {
                entries.Add(AsEntry(e, null));
            }
        }
        // Record a top-level function under its source name so an importing module can build a call
        // against its symbol (road-to-zig-std S1 cross-module resolution), then pass the entry through
        // unchanged. (V1 exports every top-level fn, not only `pub` ones — the importer is trusted; a
        // pub-visibility check is a later refinement.)
        (Symbol sym, List<(string name, CType type)> ps, Item body) Export(Item nameTok,
            (Symbol sym, List<(string name, CType type)> ps, Item body) e)
        {
            _exportedFns[Tok(nameTok)] = e.sym;
            return e;
        }
        foreach (var decl in decls)
        {
            var d = Unwrap(decl);   // unwrap `pub`
            switch (d.Content)
            {
                case Zig.ExternFnProto f:       DeclareExternFn(f.Arg2, f.Arg4, f.Arg6); break;  // extern fn IDENT ( Params ) Type ;
                case Zig.ExternFnProtoNoArgs f: DeclareExternFn(f.Arg2, null, f.Arg5); break;     // extern fn IDENT ( ) Type ;
                case Zig.ExternCFnProto f:       DeclareExternFn(f.Arg3, f.Arg5, f.Arg7); break;  // extern "c" fn IDENT ( Params ) Type ;
                case Zig.ExternCFnProtoNoArgs f: DeclareExternFn(f.Arg3, null, f.Arg6); break;     // extern "c" fn IDENT ( ) Type ;
                // The optional CallConv (Milestone R, part 5) sits between `)` and the return, so the
                // return type + body are one slot further right than the pre-CallConv layout.
                case Zig.FnDef f:          AddFnEntry(Export(f.Arg1, DeclareFn(f.Arg1, f.Arg3, f.Arg6, f.Arg7))); break;
                case Zig.FnDefNoArgs f:    AddFnEntry(Export(f.Arg1, DeclareFn(f.Arg1, null, f.Arg5, f.Arg6))); break;
                case Zig.FnDefErr f:       AddFnEntry(Export(f.Arg1, DeclareFn(f.Arg1, f.Arg3, f.Arg7, f.Arg8, errUnion: true))); break;   // `!T` return → ErrorUnion(T)
                case Zig.FnDefNoArgsErr f: AddFnEntry(Export(f.Arg1, DeclareFn(f.Arg1, null, f.Arg6, f.Arg7, errUnion: true))); break;
                // Container decls were handled in pass 0 — skip here.
                case Zig.StructDecl or Zig.StructDeclEmpty or Zig.ExternStructDecl or Zig.PackedStructDecl or Zig.PackedStructDeclBacked or Zig.EnumDecl or Zig.EnumDeclTyped or Zig.UnionDeclEnum or Zig.UnionDeclTagged or Zig.UnionDeclUntagged: break;
                // A top-level `const`/`var` is either a comptime binding (an `@import`/allocator
                // alias recorded in pass 0, which emits no decl) or a runtime global — both are
                // resolved by the global pass below (LowerTopLevelGlobals), so skip them here.
                case Zig.ConstDecl or Zig.ConstDeclTyped or Zig.VarDecl or Zig.VarDeclTyped
                  or Zig.ConstDeclTypedMods or Zig.VarDeclTypedMods or Zig.VarDeclThreadLocal: break;
                // A container-level `comptime {}` is analysis-only — always DROPPED (its side effects
                // need the comptime engine, S4–S7). A `test` block is DROPPED in a normal build too
                // (road-to-zig-std S9); but in TEST MODE (`dotcc zig test`) each `test "…" {}` is
                // lowered to a runnable `anyerror!void` function and registered in the IR test manifest
                // (DeclareTest) so the emitted program's entry point runs it — the harness for running
                // real std tests from source.
                case Zig.TopComptime: break;
                case Zig.TestDeclNamed t when _testMode: AddFnEntry(DeclareTest(UnquoteStringLiteral(Tok(t.Arg1)), t.Arg2)); break;
                case Zig.TestDeclIdent t  when _testMode: AddFnEntry(DeclareTest(Tok(t.Arg1), t.Arg2)); break;
                case Zig.TestDeclAnon t   when _testMode: AddFnEntry(DeclareTest(null, t.Arg1)); break;
                case Zig.TestDeclNamed or Zig.TestDeclIdent or Zig.TestDeclAnon: break;   // normal build: drop
                // A top-level container FIELD: registered with the file-as-struct type in pass 0b.
                case Zig.TopField: break;
                default: throw new IrUnsupportedException("zig top-level decl: " + (d.Content?.GetType().Name ?? "null"));
            }
        }
        foreach (var (container, fnDef) in containerMethods)
        {
            var me = DeclareMethod(container, fnDef);
            if (!IsFnTemplate(me.sym)) { entries.Add(me); }   // a generic method instantiates per call
        }

        // Pass 1.5: runtime top-level globals. Lowered AFTER every function/method signature
        // (so a global initializer may reference a function) and BEFORE the bodies (so a body
        // resolves a global), in SOURCE order (so a global may reference an earlier global — a
        // forward reference between globals is a documented V1 cut).
        LowerTopLevelGlobals(decls);
        // Container-level `var`s (Milestone R, part 6) — lowered to globals after top-level globals
        // (so a container var's init may reference one) and before pass 2 (so a body resolves it).
        foreach (var (container, name, typeItem, rhs) in _pendingContainerVars)
        {
            LowerContainerVar(container, name, typeItem, rhs);
        }

        // Pass 2: bodies. `_currentContainer` is set for a method body so its `@This()` resolves. The list
        // is kept, so a comptime call can lower a later body on demand (E2); the loop skips those.
        _rootBodies.AddRange(entries);
        foreach (var (sym, ps, body, container) in entries)
        {
            if (!BeginBody(sym)) { continue; }
            _currentContainer = container;
            LowerFnBody(sym, ps, body);
            _currentContainer = null;
        }

        // Pass 2.5 (wall-plan W3a): drain the monomorphization worklist. A generic call enqueued a
        // request during pass 2 (or during an earlier drained instance); each instance body lowers HERE,
        // at top level — never nested in another body's lowering — so the per-fn lowering state starts
        // clean (the re-entrancy-safe design the plan's audit demanded). The one exception is a body a
        // comptime value demanded earlier (the comptime engine's E2), which lowered under a FnStateScope
        // that saved and restored that state; the loop skips it. A cursor loop (not a fixed
        // count) picks up transitive / recursive instantiations an instance body enqueues; the total is
        // bounded by MaxInstantiations (enforced at enqueue). Runs BEFORE the comptime-fold pass so a
        // `comptime EXPR` inside an instance body is resolved alongside the base-body folds below.
        // Two mutually-feeding worklists share this drain (road-to-zig-std G4 added the second): a
        // generic instance body may name a reified type-returning generic (enqueueing its methods), and a
        // reified method body may call a generic (enqueueing an instance). So alternate cursors until BOTH
        // are exhausted rather than draining each once. Terminates because each list only grows on a fresh
        // memo miss — a new mangled instance (capped by MaxInstantiations) or a new mangled reified
        // container (memoized in _containerTypes before its members bind).
        var instCursor = 0;
        var reifiedCursor = 0;
        while (instCursor < _pendingInstantiations.Count || reifiedCursor < _pendingReifiedMethods.Count)
        {
            for (; instCursor < _pendingInstantiations.Count; instCursor++)
            {
                if (BeginBody(_pendingInstantiations[instCursor].Instance))
                {
                    LowerInstantiationBody(_pendingInstantiations[instCursor]);
                }
            }
            for (; reifiedCursor < _pendingReifiedMethods.Count; reifiedCursor++)
            {
                if (BeginBody(_pendingReifiedMethods[reifiedCursor].Method))
                {
                    LowerReifiedMethodBody(_pendingReifiedMethods[reifiedCursor]);
                }
            }
        }

        // Pass 3 (Milestone T): resolve every deferred `comptime EXPR` once all function bodies are
        // lowered, so a comptime call can interpret its callee. With a module graph that is after EVERY
        // module drains (ZigFrontend calls ZigModuleGraph.ResolveComptimeFolds): the callee may be a
        // lazy module's instance, which lowers only then. Without one, all bodies are lowered now.
        if (_moduleGraph is null)
        {
            foreach (var fold in _ownComptimeFolds)
            {
                fold.Resolved = _ir.ResolveComptimeFold(fold.Inner) ?? throw _ir.ComptimeFoldFailure(fold.Inner);
            }
            CheckComptimeReturnsAtRuntime(RuntimeRoots(), _ownRuntimeCalls, _ownComptimeReturnFns, _ownComptimeOnlyFns);
            _ir.Functions.RemoveAll(f => _ownComptimeOnlyFns.Contains(f.Sym) || HasComptimeOnlySignature(f.Sym));
        }
    }

    /// <summary>Pass 1.5: lower every runtime top-level <c>const</c>/<c>var</c> to a
    /// <see cref="GlobalVar"/> (the same IR node the C frontend's file-scope variables produce, so
    /// the C# backend renders each as a <c>public static</c> field of <c>DotCcGlobals</c>, surfaced
    /// by bare name via <c>using static</c>). A <c>const</c> bound to a comptime <c>@import</c>/
    /// allocator alias (recorded in pass 0) emits no decl and is skipped. A top-level <c>var</c> is
    /// always a runtime global (only <c>const</c> can be a comptime binding). The const-ness of a
    /// <c>const</c> global is NOT enforced — both lower to a mutable field, which is observably
    /// identical for a correct Zig program (real zig rejects a write to a const).</summary>
    private void LowerTopLevelGlobals(IReadOnlyList<Item> decls)
    {
        foreach (var decl in decls)
        {
            switch (Unwrap(decl).Content)
            {
                // Re-attempt the comptime-const binding now that pass 1 has declared every function: a
                // top-level `const P = Pair(i32);` whose RHS calls a type-returning generic (wall-plan
                // W4) couldn't resolve in pass 0 (the fn wasn't declared yet), so it records the type
                // alias HERE and emits no global. A plain runtime const still returns false → a global.
                // A name bound to a module path (`const math = std.math;`, or a file-as-struct TYPE) is
                // comptime-only, with no runtime value (G3).
                case Zig.ConstDecl d      when !IsComptimeBound(Tok(d.Arg1)) && !IsComptimeOnlyAlias(Tok(d.Arg1)):
                    if (!TryComptimeConstBinding(Tok(d.Arg1), d.Arg3)) { LowerGlobal(d.Arg1, null, d.Arg3, isConst: true); }
                    break;  // const IDENT = RhsExpr ;
                case Zig.ConstDeclTyped d when !IsComptimeBound(Tok(d.Arg1)):
                    if (!TryComptimeConstBinding(Tok(d.Arg1), d.Arg5)) { LowerGlobal(d.Arg1, d.Arg3, d.Arg5, isConst: true); }
                    break;  // const IDENT : Type = RhsExpr ;
                case Zig.VarDecl d:        LowerGlobal(d.Arg1, null,   d.Arg3); break;  // var IDENT = RhsExpr ;
                case Zig.VarDeclTyped d:   LowerGlobal(d.Arg1, d.Arg3, d.Arg5); break;  // var IDENT : Type = RhsExpr ;
                // A typed global with align/linksection modifiers (Milestone R, part 5) — modifiers
                // ignored (no-op on the managed target); RhsExpr is one slot right of the Type.
                case Zig.ConstDeclTypedMods d: LowerGlobal(d.Arg1, d.Arg3, d.Arg6, isConst: true); break;  // const IDENT : Type DeclMods = RhsExpr ;
                case Zig.VarDeclTypedMods d:   LowerGlobal(d.Arg1, d.Arg3, d.Arg6); break;  // var IDENT : Type DeclMods = RhsExpr ;
                // `threadlocal var x: T = 0;` — thread storage duration → [ThreadStatic] on the
                // emitted field (the C `_Thread_local` twofer; same marker, same constraint).
                case Zig.VarDeclThreadLocal d: LowerGlobal(d.Arg2, d.Arg4, d.Arg6, threadLocal: true); break;
            }
        }
    }

    /// <summary>Lower one top-level global: resolve its declared type (annotation, else inferred
    /// from the initializer like an untyped local in <see cref="DeclOf"/>), lower the initializer
    /// against that sink, declare the global symbol in the (module) scope so bodies resolve it, and
    /// record a <see cref="GlobalVar"/>. Scalar, aggregate (struct via <see cref="StructInit"/>),
    /// and <c>[N]T</c> array / <c>undefined</c> globals are supported (Milestone K). The initializer
    /// is lowered at module scope, so it must be a constant / module-resolvable value.
    /// <para>An integer <c>const</c> (<paramref name="isConst"/>) whose initializer folds also carries
    /// the folded value, the way a C23 <c>constexpr</c> does (<see cref="Symbol.IsConstexpr"/>): a zig
    /// container-level <c>const</c> IS comptime-known, so its name must fold wherever a constant is
    /// required — a comptime argument (<c>addN(N, 3)</c>), an array extent. The emitted field is
    /// unchanged.</para></summary>
    private void LowerGlobal(Item nameTok, Item? typeItem, Item rhsItem, bool threadLocal = false, bool isConst = false)
    {
        _comptimeDepth++;   // a container-level initializer is evaluated at compile time (task #92)
        try { LowerGlobalCore(nameTok, typeItem, rhsItem, threadLocal, isConst); }
        finally { _comptimeDepth--; }
        // A top-level `const` is immutable: a store to it is zig's "cannot assign to constant" (task #95).
        if (isConst && _symbols.Resolve(Tok(nameTok)) is { IsGlobal: true } declared) { _zigConstBindings.Add(declared); }
    }

    /// <summary>Lower a top-level <c>const</c> / <c>var</c> declaration (see <see cref="LowerGlobal"/>).</summary>
    private void LowerGlobalCore(Item nameTok, Item? typeItem, Item rhsItem, bool threadLocal, bool isConst)
    {
        // `threadlocal` V1: a zero-initialized SCALAR only. The array/aggregate
        // paths below don't carry the marker (their pinned backing store is
        // process-wide by construction), and a non-zero initializer breaks under
        // .NET [ThreadStatic] (the initializer runs on the first thread only) —
        // both are loud rejections, checked where they'd otherwise lower.
        if (threadLocal && (IsSentinelArrayType(typeItem) || rhsItem.Content is Zig.UndefinedLit or Zig.LabeledBlock))
        {
            throw new IrUnsupportedException(
                $"threadlocal '{Tok(nameTok)}': only a zero-initialized scalar threadlocal is supported");
        }
        // A labeled value-block initializer (`const table = blk: { … break :blk t; };`) runs at compile time, as in
        // zig: its value becomes the static initializer (task #79).
        CExpr? blockInit = null;
        if (rhsItem.Content is Zig.LabeledBlock)
        {
            var (blockType, blockValue) = ComptimeLabeledBlockInit(
                $"the global '{Tok(nameTok)}'", QualifyTypeName(Tok(nameTok)), rhsItem, typeItem is not null ? LowerType(typeItem) : null);
            if (blockType.Unqualified is CType.Array blockArray)
            {
                AddArrayGlobal(Tok(nameTok), blockArray, blockValue);
                return;
            }
            blockInit = blockValue;
        }
        // A `[N:s]T` sentinel array GLOBAL — reserve ONE extra trailing slot for the sentinel in the
        // pinned, program-lifetime backing store (the local-decl stackalloc does the same, part 4 /
        // Milestone Z). The symbol keeps the LOGICAL `CType.Array(element, N)` type (so `.len` /
        // slicing exclude the sentinel), while the store lays down N+1 slots. Mirrors the local path.
        if (typeItem is { Content: Zig.TySentArray } sentType)
        {
            var inv = CultureInfo.InvariantCulture;
            var sArr = (CType.Array)LowerType(sentType);       // non-null (pattern-bound)
            var sN = (int)(sArr.Count ?? 0);
            var sVal = SentinelArrayValue(sentType);
            var sentLit = new LitInt(sVal.ToString(inv), sVal) { Type = CType.Int };
            var ptrTy = new CType.Pointer(sArr.Element);
            if (rhsItem.Content is Zig.UndefinedLit)
            {
                // `undefined`: a ZERO sentinel rides C#'s zero-fill (reserve N+1). A NON-ZERO sentinel
                // needs the value written into the trailing slot — a pinned static store can't
                // post-write, so lay down an explicit `[0×N, s]` element list instead.
                if (sVal == 0)
                {
                    var np1 = sN + 1;
                    AddArrayGlobal(Tok(nameTok), sArr, new PinnedArray(sArr.Element, null,
                        new LitInt(np1.ToString(inv), np1) { Type = CType.Int }) { Type = ptrTy });
                }
                else
                {
                    var zeros = new List<CExpr>();
                    for (var k = 0; k < sN; k++) { zeros.Add(new LitInt("0", 0) { Type = CType.Int }); }
                    zeros.Add(sentLit);
                    AddArrayGlobal(Tok(nameTok), sArr, new PinnedArray(sArr.Element, zeros, null) { Type = ptrTy });
                }
                return;
            }
            // An array literal (`.{…}` / `[N]T{…}`) → a StackArray; append the sentinel to its elems so
            // the pinned store lays down N+1 slots (`g[N]` reads the sentinel back).
            if (LowerExprSink(rhsItem, sArr) is not StackArray sSa)
            {
                throw new IrUnsupportedException(
                    $"a `[N:s]T` sentinel array global '{Tok(nameTok)}' must be initialized with an array literal (`.{{…}}` / `[N]T{{…}}`) or `undefined`");
            }
            var sElems = new List<CExpr>(sSa.Elems) { sentLit };
            AddArrayGlobal(Tok(nameTok), sArr, new PinnedArray(sSa.Element, sElems, null) { Type = ptrTy });
            return;
        }
        var declared = typeItem is not null ? LowerType(typeItem) : null;
        // `[N]T = undefined` → a zeroed, pinned, program-lifetime backing store (the array-literal
        // forms fall through to the StackArray pinning below, which also catches an inferred
        // `const more = [_]T{…}` with no annotation).
        if (declared is CType.Array uarr && rhsItem.Content is Zig.UndefinedLit)
        {
            var n = uarr.Count ?? 0;
            AddArrayGlobal(Tok(nameTok), uarr, new PinnedArray(uarr.Element, null,
                new LitInt(n.ToString(CultureInfo.InvariantCulture), n) { Type = CType.Int }) { Type = new CType.Pointer(uarr.Element) });
            return;
        }
        var init = blockInit ?? LowerGlobalInit(Tok(nameTok), rhsItem, declared);
        // A comptime ARRAY at a global `const` (`const TBL = comptime buildTable();`) would resolve
        // (in pass 3) to a StackArray, but by then this global is already a scalar GlobalVar — the
        // StackArray would emit as an invalid `static T* TBL = stackalloc …` field initializer. The
        // form isn't round-trippable anyway (real zig rejects `comptime` on a container const, which
        // is already comptime), so reject it clearly rather than miscompile. A local
        // `const x = comptime f();` works, and a runtime-initialized global `const x = f();` (no
        // `comptime`) works via the sound array-by-value return. (A scalar/struct comptime global
        // splices fine — only the array shape needs the pinned re-home this path doesn't do.)
        if (init is ComptimeFold cf && (declared ?? cf.Type).Unqualified is CType.Array)
        {
            throw new IrUnsupportedException(
                $"a comptime array at a global `const` '{Tok(nameTok)}' is not supported — use a local "
                + "`const x = comptime f();`, or drop the `comptime` keyword for a runtime-initialized "
                + "global `const x = f();` (real zig rejects `comptime` on a container const anyway)");
        }
        // An array literal (`.{…}` / `[N]T{…}`) lowers to a StackArray — a `stackalloc`, invalid and
        // dangling as a static field initializer. Re-home it in a pinned, rooted, program-lifetime
        // backing store exposed as a stable `T*` (the same store a C file-scope array uses).
        if (init is StackArray sa)
        {
            if (threadLocal)
            {
                throw new IrUnsupportedException(
                    $"threadlocal '{Tok(nameTok)}': only a zero-initialized scalar threadlocal is supported");
            }
            // A NESTED array (`const TABLE: [N][2]u64 = .{ .{…}, … }`, std.fmt.float's power-of-5 tables) is one flat
            // block of scalars, row after row, as every use indexes it (`TABLE + i * 2`); its rows had been emitted as
            // `stackalloc` pointers inside the static initializer, which neither builds nor outlives the initializer.
            var (flatElement, flatElems) = FlattenArrayLiteral(Tok(nameTok), sa);
            var arraySym = AddArrayGlobal(Tok(nameTok), (CType.Array)sa.Type,
                new PinnedArray(flatElement, flatElems, null) { Type = new CType.Pointer(flatElement) });
            // A const array is comptime-known, so a comptime use may read it (`Mixer(seed_a, 5)` passing it as a
            // `comptime seed: [4]u32` argument): the interpreter evaluates its literal.
            if (isConst) { _ir.ConstGlobalInits[arraySym] = sa; }
            return;
        }
        // A .NET [ThreadStatic] initializer runs on the FIRST thread only, so C's/
        // Zig's "every thread starts at the initial value" holds only for the
        // zero/default value every thread's slot gets anyway.
        if (threadLocal && _ir.ConstEval(init) is not 0)
        {
            throw new IrUnsupportedException(
                $"threadlocal '{Tok(nameTok)}': a non-zero initializer is not supported (a .NET [ThreadStatic] initializer runs only on the first thread)");
        }
        var type = declared ?? init.Type ?? CType.Int;
        var folded = isConst && type.Unqualified is CType.Prim { Integer: true } ? _ir.ConstEval(init) : null;
        var sym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok), Kind = SymKind.Var, Type = type, Storage = Storage.Static, IsGlobal = true,
            IsThreadLocal = threadLocal,
            IsConstexpr = folded is not null,
            ConstValue = folded ?? 0,
        });
        if (declared is null && init is LitStr) { _stringLiteralSyms.Add(sym); }
        _ir.Globals.Add(new GlobalVar(sym, init));
        // A top-level CONST aggregate (`const cpu: std.Target.Cpu = .{…}`) is comptime-known, so a comptime
        // call may read it (`comptime std.atomic.cacheLineForCpu(cpu)`): the interpreter evaluates its init.
        if (isConst && type.Unqualified is CType.Named) { _ir.ConstGlobalInits[sym] = init; }
    }

    /// <summary>Lower a global's initializer. One that needs statements before its value (std.base64's
    /// <c>standard = Codecs{ .alphabet_chars = …, … }</c>: an array field is copied in after the literal, task #78) is
    /// wrapped in a synthesized static <c>__init_NAME()</c> function, and the global is initialized by a call to it. C#
    /// runs static initializers in declaration order, and a global the initializer reads is lowered, and so declared,
    /// before it.</summary>
    private CExpr LowerGlobalInit(string name, Item rhsItem, CType? declared)
    {
        CExpr value;
        List<CStmt> pre;
        using (EnterFreshHoist())
        {
            value = LowerExprSink(rhsItem, declared);
            pre = _hoist ?? new List<CStmt>();
        }
        return pre.Count == 0 ? value : InitFunctionCall(name, pre, value);
    }

    /// <summary>A synthesized static <c>__init_NAME()</c> that runs <paramref name="pre"/> and returns <paramref name="value"/>,
    /// and a call to it: the initializer of a global whose value needs statements first.</summary>
    private Call InitFunctionCall(string name, List<CStmt> pre, CExpr value)
    {
        var fn = DeclareFnSymbol(new Symbol
        {
            Name = "__init_" + name,
            Kind = SymKind.Func,
            Type = new CType.Func(value.Type, new List<CType>(), false),
            IsGlobal = true,
        });
        _ir.Functions.Add(new FuncDef(fn, new List<Symbol>(), new Block(new List<CStmt>(pre) { new Return(value) }), false));
        return new Call(fn.Name, new List<CExpr>(), new List<CType>(), fn) { Type = value.Type };
    }

    /// <summary>A lazy module's top-level consts whose value needed statements (see <see cref="LowerLazyValueConst"/>),
    /// memoized as static globals: each is evaluated once, as zig evaluates a top-level const once.</summary>
    private readonly Dictionary<string, Symbol> _lazyConstStatics = new(System.StringComparer.Ordinal);

    /// <summary>An array literal's elements as one flat block of scalars under its innermost element type: a row of a nested
    /// array (<c>[N][2]u64</c>) contributes its own elements in order, which is the layout dotcc indexes a nested array by.
    /// A row that is not itself a literal cannot be flattened here, and is a loud cut rather than a bad emit.</summary>
    private static (CType Element, List<CExpr> Elems) FlattenArrayLiteral(string name, StackArray literal)
    {
        if (literal.Element.Unqualified is not CType.Array) { return (literal.Element, literal.Elems.ToList()); }
        var flat = new List<CExpr>();
        CType? scalar = null;
        foreach (var row in literal.Elems)
        {
            if (row is not StackArray rowLiteral)
            {
                throw new IrUnsupportedException(
                    $"global `{name}`: a nested array row must be an array literal to be laid out flat (got {row.GetType().Name})");
            }
            var (rowElement, rowElems) = FlattenArrayLiteral(name, rowLiteral);
            scalar ??= rowElement;
            flat.AddRange(rowElems);
        }
        return (scalar ?? ((CType.Array)literal.Element.Unqualified).Element, flat);
    }

    /// <summary>Record a <c>[N]T</c> array global: an array-typed static symbol (so references
    /// resolve + <c>sizeof</c> is exact) backed by the pinned <paramref name="pinned"/> store
    /// (rendered as a stable <c>T*</c>). The symbol is declared after the initializer is lowered, so
    /// a literal element can reference an earlier global but never the array itself.</summary>
    /// <returns>The declared array symbol.</returns>
    private Symbol AddArrayGlobal(string name, CType.Array arr, CExpr pinned)
    {
        var sym = _symbols.Declare(new Symbol
        {
            Name = name, Kind = SymKind.Var, Type = arr, Storage = Storage.Static, IsGlobal = true,
        });
        _ir.Globals.Add(new GlobalVar(sym, pinned));
        return sym;
    }

    /// <summary>Pass 1.5: lower a container-level <c>var</c> (a namespaced mutable global, Milestone R
    /// part 6) to a <see cref="GlobalVar"/> under a mangled <c>Container_name</c> symbol — the same
    /// shape a top-level global takes, so the backend renders it as a <c>DotCcGlobals</c> field. The
    /// initializer is lowered at module scope (with <see cref="_currentConstContainer"/> set so it may
    /// reference a sibling const by bare name). The symbol is recorded in <see cref="_containerVars"/>
    /// so a <c>Type.name</c> read/write resolves to its <see cref="VarRef"/>. V1: scalar only — an
    /// array/aggregate container var is rejected (the pinned-store mangling isn't wired).</summary>
    private void LowerContainerVar(string container, string name, Item? typeItem, Item rhsItem)
    {
        var declared = typeItem is not null ? LowerType(typeItem) : null;
        var prev = _currentConstContainer;
        _currentConstContainer = container;   // a container var's init may name a sibling const
        CExpr init;
        try { init = LowerExprSink(rhsItem, declared); }
        finally { _currentConstContainer = prev; }
        if (init is StackArray)
        {
            throw new IrUnsupportedException(
                $"container '{container}' var '{name}': an array/aggregate container `var` is not supported yet (use a scalar)");
        }
        var type = declared ?? init.Type ?? CType.Int;
        var sym = _symbols.Declare(new Symbol
        {
            Name = container + "_" + name, Kind = SymKind.Var, Type = type, Storage = Storage.Static, IsGlobal = true,
        });
        _ir.Globals.Add(new GlobalVar(sym, init));
        if (!_containerVars.TryGetValue(container, out var vars))
        {
            vars = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
            _containerVars[container] = vars;
        }
        vars[name] = sym;
    }

    /// <summary>Re-lower a container <c>const</c>'s RHS at a <c>Type.NAME</c> (or sibling-bare-name)
    /// use site — container consts are comptime, so the expression is inlined fresh each time (typed
    /// by its annotation). <see cref="_currentConstContainer"/> is set so a bare identifier in the RHS
    /// resolves to a SIBLING const (Milestone R, part 6); a re-entry on the same const is a dependency
    /// cycle and errors cleanly (<see cref="_constResolving"/>).</summary>
    private CExpr LowerContainerConst(string container, string name, Item? typeItem, Item rhs)
    {
        var key = container + "." + name;
        if (!_constResolving.Add(key))
        {
            throw new IrUnsupportedException($"container '{container}' const '{name}' has a dependency cycle");
        }
        var prev = _currentConstContainer;
        _currentConstContainer = container;
        // A reified struct's const may read its comptime params (`pub const max = if (cap) |n| n else 0;`),
        // and any const is evaluated in its container's scope (`pub const empty: Self = .{ … };`, read as a
        // decl literal from another module).
        using var seeds = EnterReifiedSeeds(container);
        using var scope = EnterContainer(container);
        try
        {
            var sink = typeItem is not null ? LowerType(typeItem) : null;
            // A const computed by a labeled block (std.hash.crc's `lookup_table`) is evaluated ONCE, at compile time,
            // into a static (task #79): re-lowering the block at each use would put its loop in every reader.
            if (rhs.Content is Zig.LabeledBlock)
            {
                if (!_staticContainerConsts.TryGetValue((container, name), out var blockSym))
                {
                    var (blockType, blockInit) = ComptimeLabeledBlockInit($"'{container}.{name}'", $"{container}__{name}", rhs, sink);
                    blockSym = _symbols.Declare(new Symbol
                    {
                        Name = $"{container}__{name}__static", Kind = SymKind.Var, Type = blockType, Storage = Storage.Static, IsGlobal = true,
                    });
                    _ir.Globals.Add(new GlobalVar(blockSym, blockInit));
                    _staticContainerConsts[(container, name)] = blockSym;
                }
                return new VarRef(blockSym) { Type = blockSym.Type, IsLValue = true };
            }
            return LowerExprSink(rhs, sink);
        }
        finally
        {
            _currentConstContainer = prev;
            _constResolving.Remove(key);
        }
    }

    /// <summary><c>comptime label: { … }</c> in value position (std.unicode's <c>const first = comptime first: { … break :first
    /// a ++ b ++ c; };</c>, task #82): run by the comptime interpreter as a const's labeled block is. An array value becomes a
    /// static global, one per site and function instance (a generic's instances may compute different tables); anything
    /// else is its literal.</summary>
    private CExpr ComptimeLabeledBlockValue(Item labeled, CType? sink)
    {
        var key = (labeled, _currentFnName);
        if (_comptimeBlockStatics.TryGetValue(key, out var memo)) { return new VarRef(memo) { Type = memo.Type, IsLValue = true }; }
        var (type, init) = ComptimeLabeledBlockInit("a `comptime` block", "__ctblk" + _comptimeBlockStatics.Count, labeled, sink);
        if (init is not PinnedArray) { return init; }
        var sym = _symbols.Declare(new Symbol
        {
            Name = "__ctblk" + _comptimeBlockStatics.Count, Kind = SymKind.Var, Type = type, Storage = Storage.Static, IsGlobal = true,
        });
        _ir.Globals.Add(new GlobalVar(sym, init));
        _comptimeBlockStatics[key] = sym;
        return new VarRef(sym) { Type = type, IsLValue = true };
    }

    /// <summary>The statics <see cref="ComptimeLabeledBlockValue"/> made, by site and function instance.</summary>
    private readonly Dictionary<(Item Site, string Fn), Symbol> _comptimeBlockStatics = new();

    /// <summary>The static initializer of a global or container const computed by a labeled block (std.hash.crc's
    /// <c>const lookup_table = blk: { var table: [256]I = undefined; for (&amp;table, 0..) |*e, i| { … } break :blk table; };</c>).
    /// zig runs the block at compile time, so it is lowered into a throwaway scope, run by the comptime interpreter,
    /// and its value spliced back: a pinned array for a <c>[N]T</c> result, a literal otherwise. None of the block's
    /// statements reach an emitted body. A block the interpreter cannot run is a loud cut that says why. A struct
    /// with array fields (std.bit_set's <c>full</c>) is built by a synthesized <c>__init_</c><paramref name="initName"/>
    /// that copies them in after the literal.</summary>
    private (CType Type, CExpr Init) ComptimeLabeledBlockInit(string what, string initName, Item labeled, CType? declared)
    {
        Symbol? result = null;
        CStmt lowered;
        List<CStmt> hoisted;
        _symbols.EnterScope();
        try
        {
            using var hoist = EnterFreshHoist();
            lowered = LowerLabeledValue(labeled, declared, temp =>
            {
                result = temp;
                return new Block(new List<CStmt>());
            });
            hoisted = _hoist ?? new List<CStmt>();
        }
        finally
        {
            _symbols.ExitScope();
        }
        if (result is not { } resultSym
            || _ir.EvalComptimeBlock(new Block([.. hoisted, lowered]), resultSym) is not { } value)
        {
            throw new IrUnsupportedException(
                $"the labeled block initializing {what} did not evaluate at compile time"
                + (_ir.ComptimeMiss is { } why ? $" (the interpreter stopped at {why})" : ""));
        }
        var type = declared ?? resultSym.Type;
        IrUnsupportedException Unspliceable() => new(
            $"the labeled block initializing {what} evaluated to a value with no static form");
        // An array breaks out of the block decayed to a pointer (the temp is `T*`), so without an annotation the
        // array type is the evaluated value's own.
        if (value is IrModule.CtArray array
            && (type.Unqualified as CType.Array ?? array.Type.Unqualified as CType.Array) is { } arrayType)
        {
            var elems = array.Elems.Select(e => _ir.SpliceComptimeValue(e) ?? throw Unspliceable()).ToList();
            return (arrayType, new PinnedArray(arrayType.Element, elems, null) { Type = new CType.Pointer(arrayType.Element) });
        }
        if (_ir.SpliceComptimeValue(value) is { } spliced) { return (type, spliced); }
        if (_ir.SpliceStructDeferringArrays(value) is not var (structInit, arrays) || type.Unqualified is not CType.Named)
        {
            throw Unspliceable();
        }
        Symbol temp;
        _symbols.EnterScope();
        try { temp = _symbols.Declare(new Symbol { Name = "__ctv", Kind = SymKind.Var, Type = type }); }
        finally { _symbols.ExitScope(); }
        var pre = new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(temp, structInit) }) };
        foreach (var (field, fieldArray, arrayValue) in arrays)
        {
            var elems = arrayValue.Elems.Select(e => _ir.SpliceComptimeValue(e) ?? throw Unspliceable()).ToList();
            var bytes = (long)elems.Count * fieldArray.Element.SizeOf;
            pre.Add(new ExprStmt(new Call("memcpy", new List<CExpr>
            {
                new Member(new VarRef(temp) { Type = type, IsLValue = true }, field, false) { Type = fieldArray, IsLValue = true },
                new PinnedArray(fieldArray.Element, elems, null) { Type = new CType.Pointer(fieldArray.Element) },
                new LitInt(bytes.ToString(CultureInfo.InvariantCulture), bytes) { Type = CType.Int },
            }) { Type = new CType.Pointer(CType.Void) }));
        }
        return (type, InitFunctionCall(initName, pre, new VarRef(temp) { Type = type }));
    }

    /// <summary>Tag a pass-1 function entry with the container it belongs to (null for a free
    /// function), so pass 2 can set <see cref="_currentContainer"/> while lowering its body.</summary>
    private static (Symbol sym, List<(string name, CType type)> ps, Item body, string? container) AsEntry(
        (Symbol sym, List<(string name, CType type)> ps, Item body) e, string? container)
        => (e.sym, e.ps, e.body, container);

    /// <summary>Unwrap a top-level decl's optional visibility/linkage modifier — <c>pub</c>
    /// (<see cref="Zig.PubFn"/>/<see cref="Zig.PubVar"/>), <c>export</c> (<see cref="Zig.ExportFn"/>/
    /// <see cref="Zig.ExportVar"/>), or <c>pub export</c> (<see cref="Zig.PubExportFn"/>/
    /// <see cref="Zig.PubExportVar"/>) — to its inner declaration; an unmodified decl is returned
    /// unchanged. Both modifiers are a no-op in a single-file console program (every non-static
    /// function is already export-eligible under <c>-shared</c>; a data export under <c>-shared</c>
    /// is a documented V1 cut), so peeling lets all the existing FnDef / global / container handling
    /// apply. A `pub` container (<see cref="Zig.PubContainer"/>) peels to the inner struct/enum/union
    /// decl (an in-FUNCTION container decl is still a cut — it'd need on-the-fly type registration).</summary>
    /// <summary>Pass 0's work list (see <see cref="Lower"/>): each top-level decl's unwrapped content in
    /// source order, a container one carrying its registration NAME (qualified by <paramref name="qualify"/>
    /// — the module prefix of an imported module, see <see cref="QualifyTypeName"/>), with every struct's nested
    /// container members spliced in directly after it (recursively) under the parent-mangled name
    /// <c>Parent__Inner</c> and their parent's name. A non-container decl carries a null name.</summary>
    /// <summary>Pass 0a for one container: register its NAME (a struct / union as a <see cref="CType.Named"/>
    /// placeholder; an enum fully, with its consts and methods collected into <paramref name="methods"/>),
    /// in its own scope. Shared by a module's pass 0 and a reified struct's nested containers.</summary>
    private void RegisterContainerName(string name, object? content, List<(string container, Item fnDef)> methods)
    {
        using (EnterContainer(name))   // an enum's member values / consts resolve in its own scope
        {
            switch (content)
            {
                case Zig.StructDecl:        _containerTypes[name] = new CType.Named(name); break;
                case Zig.StructDeclEmpty:   _containerTypes[name] = new CType.Named(name); break;
                case Zig.ExternStructDecl:  _containerTypes[name] = new CType.Named(name); break;  // const IDENT = extern struct { … } ;
                case Zig.PackedStructDecl:  _containerTypes[name] = new CType.Named(name); break;  // const IDENT = packed struct { … } ;
                case Zig.PackedStructDeclBacked: _containerTypes[name] = new CType.Named(name); break;  // const IDENT = packed struct(T) { … } ;
                case Zig.EnumDecl e:        foreach (var m in RegisterEnumZig(name, null, e.Arg5)) { methods.Add((name, m)); } break;       // const IDENT = enum { EnumFields } ;
                case Zig.EnumDeclTyped e:   foreach (var m in RegisterEnumZig(name, e.Arg5, e.Arg8)) { methods.Add((name, m)); } break;     // const IDENT = enum ( Type ) { EnumFields } ;
                case Zig.UnionDeclEnum:     _containerTypes[name] = new CType.Named(name); break;  // const IDENT = union(enum) { … } ;
                case Zig.UnionDeclTagged:   _containerTypes[name] = new CType.Named(name); break;  // const IDENT = union(SomeEnum) { … } ;
                case Zig.UnionDeclUntagged: _containerTypes[name] = new CType.Named(name); break;  // const IDENT = union { … } ;
            }
        }
    }

    /// <summary>Scope a NESTED container's plain name to its parent, after it is registered (an enum's
    /// <see cref="CType.Enum"/> only exists once RegisterEnumZig has run).</summary>
    private void ScopeNestedContainer(string name, object? content, string parentName)
    {
        if (!_containerTypes.TryGetValue(name, out var nestedType)) { return; }
        _containerParents[name] = parentName;
        if (!_nestedContainerTypes.TryGetValue(parentName, out var nestedMap))
        {
            nestedMap = new Dictionary<string, CType>(System.StringComparer.Ordinal);
            _nestedContainerTypes[parentName] = nestedMap;
        }
        nestedMap[ContainerDeclName(content) ?? name] = nestedType;
    }

    /// <summary>Pass 0b for one container: its field layout (field types resolve through pass 0a), its
    /// consts, and its methods (collected into <paramref name="methods"/>), in its own scope so a field typed
    /// by a nested container or by <c>@This()</c> resolves.</summary>
    private void RegisterContainerBody(string name, object? content, List<(string container, Item fnDef)> methods)
    {
        using (EnterContainer(name))
        {
            switch (content)
            {
                case Zig.StructDecl s:      // const IDENT = struct { Members } ;
                {
                    var (fields, allFns, consts, _) = SplitMembers(s.Arg5);
                    var fnDefs = DeclareTypeReturningMembers(name, allFns);
                    RegisterContainerConsts(name, consts);   // first: a field may be typed by a type const
                    RegisterStruct(name, fields);
                    foreach (var m in fnDefs) { methods.Add((name, m)); }
                    break;
                }
                case Zig.StructDeclEmpty: RegisterStruct(name, System.Array.Empty<Item>()); break;  // const IDENT = struct { } ;
                case Zig.ExternStructDecl s:  // const IDENT = extern struct { Members } ;
                {
                    var (fields, allFns, consts, _) = SplitMembers(s.Arg6);
                    var fnDefs = DeclareTypeReturningMembers(name, allFns);
                    RegisterContainerConsts(name, consts);   // first: a field may be typed by a type const
                    RegisterStruct(name, fields, AggregateLayout.Sequential);
                    foreach (var m in fnDefs) { methods.Add((name, m)); }
                    break;
                }
                case Zig.PackedStructDecl or Zig.PackedStructDeclBacked:  // const IDENT = packed struct [( T )] { Members } ;
                {
                    var (fields, allFns, consts, _) = SplitMembers(content is Zig.PackedStructDecl ps ? ps.Arg6 : ((Zig.PackedStructDeclBacked)content).Arg9);
                    var fnDefs = DeclareTypeReturningMembers(name, allFns);
                    RegisterContainerConsts(name, consts);   // first: a field may be typed by a type const
                    RegisterStruct(name, fields, AggregateLayout.Packed);
                    foreach (var m in fnDefs) { methods.Add((name, m)); }
                    break;
                }
                case Zig.UnionDeclEnum u:   foreach (var m in RegisterUnion(name, u.Arg8)) { methods.Add((name, m)); } break;  // const IDENT = union(enum) { UnionMembers } ;
                case Zig.UnionDeclTagged u: foreach (var m in RegisterUnionTagged(name, Tok(u.Arg5), u.Arg8)) { methods.Add((name, m)); } break;  // const IDENT = union(SomeEnum) { UnionMembers } ;
                case Zig.UnionDeclUntagged u: foreach (var m in RegisterUnionUntagged(name, u.Arg5)) { methods.Add((name, m)); } break;  // const IDENT = union { UnionMembers } ;
            }
        }
    }

    /// <summary>The nested containers of a container, depth-first (<c>&lt;parent&gt;__Inner</c>, then its own), each
    /// with its content and parent: pass 0's flattening, for a container that is not top-level.</summary>
    private static List<(string Name, object? Content, string Parent)> CollectNestedContainers(string parent, IReadOnlyList<Item> containers)
    {
        var list = new List<(string Name, object? Content, string Parent)>();
        void Add(string p, IReadOnlyList<Item> items)
        {
            foreach (var nested in items)
            {
                if (ContainerDeclName(nested.Content) is not { } inner) { continue; }
                var name = $"{p}__{inner}";
                list.Add((name, nested.Content, p));
                Add(name, NestedContainerItems(nested.Content));
            }
        }
        Add(parent, containers);
        return list;
    }

    private static List<(string? Name, object? Content, string? Parent)> CollectPass0Decls(IReadOnlyList<Item> decls,
        Func<string, string> qualify)
    {
        var list = new List<(string? Name, object? Content, string? Parent)>();
        foreach (var decl in decls)
        {
            var content = Unwrap(decl).Content;
            if (ContainerDeclName(content) is { } name)
            {
                AddContainer(qualify(name), content, null);
            }
            else
            {
                list.Add((null, content, null));
            }
        }
        return list;

        void AddContainer(string name, object? content, string? parent)
        {
            list.Add((name, content, parent));
            foreach (var nested in NestedContainerItems(content))
            {
                var nestedContent = nested.Content;
                if (ContainerDeclName(nestedContent) is { } inner)
                {
                    AddContainer($"{name}__{inner}", nestedContent, name);
                }
            }
        }
    }

    /// <summary>The declared (source) name of a container decl — <c>Mode</c> for
    /// <c>const Mode = enum {…};</c> — or null when the node is not a container decl.</summary>
    private static string? ContainerDeclName(object? content) => content switch
    {
        Zig.StructDecl s        => Tok(s.Arg1),
        Zig.StructDeclEmpty s   => Tok(s.Arg1),
        Zig.ExternStructDecl s  => Tok(s.Arg1),
        Zig.PackedStructDecl s  => Tok(s.Arg1),
        Zig.PackedStructDeclBacked s => Tok(s.Arg1),
        Zig.EnumDecl e          => Tok(e.Arg1),
        Zig.EnumDeclTyped e     => Tok(e.Arg1),
        Zig.UnionDeclEnum u     => Tok(u.Arg1),
        Zig.UnionDeclTagged u   => Tok(u.Arg1),
        Zig.UnionDeclUntagged u => Tok(u.Arg1),
        _ => null,
    };

    /// <summary>The name tokens of the <c>inline fn</c> declarations seen (task #92). The <c>inline</c> keyword is
    /// otherwise erased where a declaration is unwrapped, and zig lets only an inline function <c>return</c> from a
    /// <c>comptime { }</c> block when it is called at runtime. Keyed by the AST token, which is unique per declaration
    /// and shared by every lowering of it; weak, so a finished compilation's AST is not kept alive.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item, object> InlineFnNameToks = new();

    /// <summary>Record <paramref name="fnDef"/> as an <c>inline fn</c> (see <see cref="InlineFnNameToks"/>) and return it.</summary>
    private static Item MarkInline(Item fnDef)
    {
        var name = fnDef.Content switch
        {
            Zig.FnDef f => f.Arg1,
            Zig.FnDefNoArgs f => f.Arg1,
            Zig.FnDefErr f => f.Arg1,
            Zig.FnDefNoArgsErr f => f.Arg1,
            _ => null,
        };
        if (name is not null) { InlineFnNameToks.AddOrUpdate(name, InlineMark); }
        return fnDef;
    }

    /// <summary>The value <see cref="InlineFnNameToks"/> maps to (only presence matters).</summary>
    private static readonly object InlineMark = new();

    /// <summary>True when the declaration named by <paramref name="nameTok"/> was an <c>inline fn</c>.</summary>
    private static bool IsInlineFnName(Item nameTok) => InlineFnNameToks.TryGetValue(nameTok, out _);

    private static Item Unwrap(Item decl) => decl.Content switch
    {
        Zig.PubFn p         => p.Arg1,   // `pub FnDef`
        Zig.InlineFn i      => MarkInline(i.Arg1),   // `inline FnDef` (an optimizer hint; lowers as a plain fn)
        Zig.PubInlineFn pi  => MarkInline(pi.Arg2),  // `pub inline FnDef`
        Zig.ExportFn e      => e.Arg1,   // `export FnDef` (Milestone R)
        Zig.PubExportFn pe  => pe.Arg2,  // `pub export FnDef` (Milestone R)
        Zig.PubVar p        => p.Arg1,   // `pub VarDecl` (exported/public data)
        Zig.ExportVar e     => e.Arg1,   // `export VarDecl`
        Zig.PubExportVar pe => pe.Arg2,  // `pub export VarDecl`
        Zig.PubContainer p  => p.Arg1,   // `pub const P = struct/enum/union {…}` (public container)
        _ => decl,
    };

    // ---- helpers ---------------------------------------------------------

    /// <summary>Flatten a left-recursive list spine (Decls / Stmts / Params / ArgList)
    /// into source order with an explicit stack — same anti-stack-overflow walk as
    /// <see cref="IrBuilder"/>'s <c>FlattenFns</c>. The cons/one node types are disjoint
    /// across the four list kinds, and the walk stops at the first non-list node (so a
    /// nested Block's own Stmts aren't pulled into the parent), so one method serves all
    /// four with no cross-contamination.</summary>
    internal static List<Item> Flatten(Item it)
    {
        var stack = new Stack<Item>();
        stack.Push(it);
        var ordered = new List<Item>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n.Content)
            {
                case Zig.DeclsCons c:  stack.Push(c.Arg1); stack.Push(c.Arg0); break;  // [Decl, Decls]
                case Zig.DeclsOne o:   stack.Push(o.Arg0); break;
                case Zig.StmtsCons c:  stack.Push(c.Arg1); stack.Push(c.Arg0); break;  // [Stmt, Stmts]
                case Zig.StmtsOne o:   stack.Push(o.Arg0); break;
                case Zig.ParamsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Param, ',', Params]
                case Zig.ParamsOne o:  stack.Push(o.Arg0); break;
                case Zig.ParamsTrail t: stack.Push(t.Arg0); break;  // [Param, ','] trailing comma
                case Zig.ArgsCons c:   stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Expr, ',', ArgList]
                case Zig.ArgsOne o:    stack.Push(o.Arg0); break;
                case Zig.ArgsTrail t:  stack.Push(t.Arg0); break;  // [Arg, ','] trailing comma
                case Zig.ProngsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Prongs, ',', Prong] (left-recursive)
                case Zig.ProngsOne o:  stack.Push(o.Arg0); break;
                case Zig.CaseValsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Expr, ',', CaseVals]
                case Zig.CaseValsOne o:  stack.Push(o.Arg0); break;
                case Zig.CaseValsTrail t: stack.Push(t.Arg0); break;  // [Expr, ','] trailing comma
                case Zig.FieldDeclsCons c: stack.Push(c.Arg1); stack.Push(c.Arg0); break;  // [Member, FieldDecls] (right-recursive)
                case Zig.FieldDeclsOne o:  stack.Push(o.Arg0); break;
                case Zig.EnumFieldsCons c: stack.Push(c.Arg1); stack.Push(c.Arg0); break;  // [EnumMember, EnumFields] (right-recursive)
                case Zig.EnumFieldsOne o:  stack.Push(o.Arg0); break;
                case Zig.UnionVariantsCons c: stack.Push(c.Arg1); stack.Push(c.Arg0); break;  // [UnionMember, UnionVariants] (right-recursive)
                case Zig.UnionVariantsOne o:  stack.Push(o.Arg0); break;
                case Zig.FieldInitsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [FieldInits, ',', FieldInit]
                case Zig.FieldInitsOne o:  stack.Push(o.Arg0); break;
                case Zig.FieldInitsTrail t: stack.Push(t.Arg0); break;
                case Zig.TupleTypesCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [TupleTypes, ',', Type]
                case Zig.TupleTypesOne o:  stack.Push(o.Arg0); break;
                case Zig.TupleTypesTrail t: stack.Push(t.Arg0); break;
                case Zig.FnTypeParamsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [FnTypeParam, ',', FnTypeParams] (right-recursive)
                case Zig.FnTypeParamsOne o:  stack.Push(o.Arg0); break;
                case Zig.FnTypeParamsTrail t: stack.Push(t.Arg0); break;  // [FnTypeParam, ','] trailing comma
                case Zig.DestructBindsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [DestructBinds, ',', DestructBind]
                case Zig.DestructBindsOne o:  stack.Push(o.Arg0); break;
                case Zig.ForObjsTwo t:  stack.Push(t.Arg2); stack.Push(t.Arg0); break;  // [ForObj, ',', ForObj]
                case Zig.ForObjsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [ForObjs, ',', ForObj]
                case Zig.ForCapsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [ForCaps, ',', ForCap]
                default: ordered.Add(n); break;
            }
        }
        return ordered;
    }
}
