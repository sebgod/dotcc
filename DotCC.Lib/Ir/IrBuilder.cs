#nullable enable

using System;
using System.Collections.Generic;
using Item = global::LALR.CC.LexicalGrammar.Item;

namespace DotCC.Ir;

/// <summary>Thrown when the IR builder meets a parse-tree node it doesn't yet
/// lower. Carries the node type name so the backend fails loudly on an
/// unsupported construct rather than silently miscompiling. A subclass of
/// <see cref="DotCC.CompileException"/> so callers catch the one public
/// compile-error type regardless of whether the cause was a parse error, an
/// invalid type, or an unsupported construct.</summary>
public class IrUnsupportedException : DotCC.CompileException
{
    public IrUnsupportedException(string node) : base($"dotcc does not yet support: {node}") { }
}

/// <summary>
/// Builds the typed IR from the raw LALR parse tree (driven by the
/// LALR.CC-generated <see cref="C.IdentityVisitor"/>, which leaves each reduced
/// <c>Item.Content</c> holding its raw grammar record). A TOP-DOWN recursive walk with full
/// scope/type context — the opposite of the legacy bottom-up string emitter.
/// This is the sole backend: it covers the whole C surface dotcc supports.
/// A parse-tree node it doesn't yet lower raises
/// <see cref="IrUnsupportedException"/> (fail loudly, never silently miscompile).
/// </summary>
internal sealed partial class IrBuilder
{
    /// <summary>The neutral IR module this C binder builds into — the output lists, the aggregate
    /// registries and their layout model, and the comptime interpreter. The backends and the Zig
    /// front-end see only this (<see cref="IrModule"/>); the members below are this binder's own
    /// shorthand for it, so the C binding code reads as it always has.</summary>
    internal IrModule Module { get; } = new();

    private List<FuncDef> Functions => Module.Functions;
    private List<GlobalVar> Globals => Module.Globals;
    private List<StructTypeDef> Types => Module.Types;
    private List<EnumTypeDef> Enums => Module.Enums;
    internal List<Diagnostic> Diagnostics => Module.Diagnostics;
    private Dictionary<string, List<StructField>> _structFields => Module.StructFields;
    private Dictionary<string, bool> _structIsUnion => Module.StructIsUnion;
    private HashSet<string> _packedStructs => Module.PackedStructs;
    private Dictionary<string, CType.Enum> _enumTypes => Module.EnumTypes;
    private HashSet<string> _emittedTypes => Module.EmittedTypes;
    private long? SizeOfConst(CType t) => Module.SizeOfConst(t);
    private int AlignOfConst(CType t) => Module.AlignOfConst(t);
    private int? OffsetOfConstPath(string structName, IReadOnlyList<string> path) => Module.OffsetOfConstPath(structName, path);
    private static string? StructCanonical(CType t) => IrModule.StructCanonical(t);
    private static void RejectReservedTypeName(string name, string kind) => IrModule.RejectReservedTypeName(name, kind);
    private bool AddEnumDef(string name, CType underlying, List<EnumMember> members) => Module.AddEnumDef(name, underlying, members);
    private IReadOnlyList<StructField>? StructFieldsOf(string name) => Module.StructFieldsOf(name);
    internal long? ConstEval(CExpr e) => Module.ConstEval(e);

    private readonly SymbolTable _symbols;
    // Typedef name → its underlying type. Unlike the legacy emitter (which emits
    // `using` aliases and resolves names textually), the IR resolves a typedef
    // name straight to its CType — so `size_t x` becomes `ulong x` with the right
    // SizeOf, no alias directive needed. Populated in declaration order, so a
    // chained typedef (`typedef size_t mysize;`) resolves through the table.
    private readonly Dictionary<string, CType> _typedefs = new(StringComparer.Ordinal);
    private string _file = "";
    // Every `setjmp(env)` call built in the CURRENT function, by reference identity
    // (records are value-equal, so a reference comparer is required to tell two
    // textually-identical calls apart — and the tracked object is the post-clone one
    // BuildExpr actually puts in the tree), mapped to its source position. A recogniser
    // (the `if`-guard SetjmpGuardOf, the value-capture rewrite, the switch-subject
    // wrap) REMOVES the call it consumes; anything left once the body is built is a
    // setjmp in a shape dotcc can't model (a loop/ternary condition, a nested
    // sub-expression, a bare discarded call) — which would otherwise silently lower to
    // the always-0 runtime stub and turn a `longjmp` into an uncaught exception. So a
    // survivor is a loud CompileException (RejectStraySetjmp), never a silent
    // miscompile.
    private readonly Dictionary<Call, SrcPos> _setjmpCalls =
        new(ReferenceEqualityComparer.Instance);
    // Monotonic id for the goto-restart label + synthetic capture var of a
    // value-capturing setjmp (SetjmpCapture) — program-unique across all functions.
    private int _setjmpSeq;
    // The name of the function currently being built — the value of the C99
    // predefined identifier `__func__` inside its body.
    private string _currentFnName = "";
    // The declared return type of the function currently being built — lets a
    // `return <const T*>` from a `T*`-returning function trip the const-discard check.
    private CType? _currentRet;


    /// <summary>
    /// Functions a native import (`-l`) library must resolve: declared by prototype,
    /// defined in NO translation unit, actually called, and NOT from a synthetic
    /// system header (those are runtime-provided via <c>using static Libc</c>, flagged
    /// by the reserved line band). Variadic candidates ARE included — the emit pass
    /// (<c>Compiler.ComputeImportCandidates</c>) warns and skips them, since a varargs
    /// signature can't become a function pointer. Empty unless the program calls an
    /// undefined non-system prototype; computed on demand (cheap, post-build).
    /// </summary>
    /// <summary>Publish the import-mode analysis onto <see cref="Module"/> (call after every unit is
    /// added), where the emit pass reads it.</summary>
    internal void PublishImportAnalysis()
    {
        Module.ProtoOnlyReferenced = ProtoOnlyReferenced;
        Module.ExternDataReferenced = ExternDataReferenced;
    }

    public IReadOnlyDictionary<string, Symbol> ProtoOnlyReferenced
    {
        get
        {
            var result = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            foreach (var (name, sym) in _protoOnlyFuncs)
            {
                if (!_referencedFuncs.Contains(name)) { continue; }
                if (sym.FromSystemHeader) { continue; }
                result[name] = sym;
            }
            return result;
        }
    }

    /// <summary>
    /// Extern DATA objects (<c>extern int verbosity;</c>) referenced but defined in
    /// NO translation unit — these would need a native data import, which V1 import
    /// mode does not support, so the emit pass warns and skips them. Distinct from a
    /// normal whole-program extern that another TU's definition satisfies. Sorted for
    /// deterministic diagnostics.
    /// </summary>
    public IReadOnlyList<string> ExternDataReferenced
    {
        get
        {
            var result = new List<string>();
            foreach (var name in _referencedExternData)
            {
                if (!_definedGlobalNames.Contains(name)) { result.Add(name); }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }

    // Dialect-gating sink (the emit-pass half of -pedantic). Non-null only under
    // -pedantic / -pedantic-errors; the builder calls RequireMin at each construct
    // that postdates the selected -std=, mirroring the legacy emit-pass gate. A C
    // feature is structurally accepted by the one union grammar regardless of
    // dialect, so this is the rejection layer — and a pure no-op on the default path.
    private readonly DotCC.DialectGate? _gate;

    /// <param name="names">The target's identifier policy, threaded into the
    /// symbol table so <see cref="Symbol.TargetName"/> is escaped/uniquified for the
    /// backend that will consume this IR (Compiler.BuildIr supplies the C# policy
    /// unless the wat backend injects its own, so names are target-legal and
    /// flat-local shadowing is resolved). Neutral mechanism stays in
    /// <see cref="SymbolTable"/>; only the policy varies — and which policy applies
    /// is the compiler's decision, keeping this namespace backend-free.</param>
    /// <summary>C23 <c>#embed</c> payloads, keyed by the content-hash the
    /// preprocessor's <c>OnEmbed</c> stamped onto each synthetic EMBED token.
    /// <see cref="BuildEmbed"/> resolves the carrier back to its raw file bytes
    /// here. Shared with the <see cref="CPreprocessor"/> that populated it
    /// (Compiler.BuildIr threads one dictionary through both), so identical
    /// embeds across TUs dedup by hash.</summary>
    private readonly IReadOnlyDictionary<string, byte[]> _embeds;

    // Whether to emit the const-discarding-pointer-conversion warning (gcc
    // -Wdiscarded-qualifiers). On by default; `-Wno-discarded-qualifiers` clears it.
    // Does NOT affect the write-to-const ERROR — that's a constraint violation, not
    // a suppressible warning.
    private readonly WarningFlags _warnings;

    internal IrBuilder(DotCC.DialectGate? gate, INameLegalizer names,
        IReadOnlyDictionary<string, byte[]>? embeds = null, WarningFlags warnings = WarningFlags.Default)
    {
        _gate = gate;
        _symbols = new SymbolTable(names);
        _embeds = embeds ?? new Dictionary<string, byte[]>();
        _warnings = warnings;
        // <uchar.h> char16_t is a pre-registered type name (Compiler.PredefinedTypeNames)
        // rather than a real typedef, so seed its resolution here — straight to the
        // Char16 Prim (→ C# char) instead of the verbatim CType.Named fallback the
        // other seeded library names take.
        _typedefs["char16_t"] = CType.Char16;
        // <wchar.h> wchar_t is likewise a pre-registered type name (not a real
        // typedef) — seed it to the WChar Prim (→ C# char; dotcc's MSVC-shaped
        // 16-bit wchar_t). See CType.WChar.
        _typedefs["wchar_t"] = CType.WChar;
        // <uchar.h> char32_t is likewise a pre-registered type name — seed it to the
        // Char32 Prim (→ C# uint; a 32-bit UTF-32 code unit). See CType.Char32.
        _typedefs["char32_t"] = CType.Char32;
        // <uchar.h> char8_t (C23) — seed it to the Char8 Prim (→ C# byte; an 8-bit
        // UTF-8 code unit, like dotcc's char). See CType.Char8.
        _typedefs["char8_t"] = CType.Char8;
    }

    /// <summary>Flag a feature introduced in <paramref name="era"/> (ISO year) when
    /// the active dialect predates it. No-op when the gate is off or new enough.</summary>
    private void Gate(int era, string feature, Item it) => _gate?.RequireMin(era, feature, it.Position.Line);
    private void Gate(int era, string feature, SrcPos pos) => _gate?.RequireMin(era, feature, pos.Line);
    /// <summary>Gate a feature, then return an already-built value — for gating in
    /// expression-bodied switch arms (the value is built eagerly; gating only
    /// records, so evaluation order is irrelevant).</summary>
    private T Gated<T>(int era, string feature, Item it, T value) { Gate(era, feature, it); return value; }

    /// <summary>Walk one translation unit's parse tree, appending its functions
    /// / globals to the accumulated lists (file-scope symbols persist across
    /// units so a whole-program call resolves).</summary>
    public void AddUnit(Item root, string file) => AddUnit(root, file, library: false);

    /// <summary>Bind one translation unit. A <paramref name="library"/> unit (the wat target's
    /// libc, compiled from C with the program) defines its functions weakly: one the program
    /// already defines keeps the program's, and what it does define is marked
    /// <see cref="Symbol.IsLibrary"/>, so it is left out unless something reaches it.</summary>
    public void AddUnit(Item root, string file, bool library)
    {
        _libraryUnit = library;
        _file = file;
        _unitObjects.Clear();
        Module.IsObject = ObjectKey is not null;
        if (root.Content is C.TuEmpty)
        {
            _gate?.Report("ISO C forbids an empty translation unit", 0);
            return;
        }
        var globalsBefore = Module.Globals.Count;
        FlattenFns(root, BuildTopLevel);
        if (library)
        {
            for (var i = globalsBefore; i < Module.Globals.Count; i++) { Module.LibraryGlobals.Add(Module.Globals[i].Sym); }
        }
        if (ObjectKey is { } key) { QualifyTuLocals(key); }
    }

    /// <summary>The file-scope objects this unit has defined so far, by name. A tentative
    /// definition (no initializer) and a later definition of the same name in one unit are
    /// one object (C11 6.9.2p2), as CPython's forward <c>static PyModuleDef m;</c> before
    /// <c>static PyModuleDef m = {…};</c> relies on.</summary>
    private readonly Dictionary<string, GlobalVar> _unitObjects = new(StringComparer.Ordinal);

    /// <summary>
    /// Set when this builder compiles one translation unit to an object (<c>--emit=obj</c>): a
    /// key naming the unit, which qualifies its <see cref="Symbol.IsTuLocal"/> names, since
    /// every unit linked into the program has its own. It also names anonymous aggregates by
    /// their content (<see cref="AnonAggregateName"/>).
    /// </summary>
    internal string? ObjectKey { get; init; }

    /// <summary>Give every name only this unit can reach its <paramref name="key"/>: the
    /// functions and objects it defines with internal linkage and its static locals. Every
    /// reference holds the symbol, so they print the qualified name too.</summary>
    private void QualifyTuLocals(string key)
    {
        foreach (var fn in Functions)
        {
            if (fn.Sym.IsTuLocal) { fn.Sym.TargetName += "__" + key; }
        }
        foreach (var g in Globals)
        {
            if (g.Sym.IsTuLocal) { g.Sym.TargetName += "__" + key; }
        }
    }

    /// <summary>
    /// The name of a synthesized anonymous aggregate over <paramref name="memberList"/>. A
    /// whole program numbers them. An object names one by its members' parse fingerprint: a
    /// header struct's anonymous member is then the same type, behind the same hidden field,
    /// in every unit that includes it, as the linker's one copy of the struct requires.
    /// </summary>
    private string AnonAggregateName(Item memberList, bool isUnion) => ObjectKey is null
        ? $"__Anon{_anonAggrSeq++}"
        : $"__Anon{(isUnion ? 'U' : 'S')}_{Fingerprints.Of(memberList).A:x16}";

    // ---- top level -------------------------------------------------------

    private void FlattenFns(Item it, Action<Item> onFn)
    {
        // A translation unit with thousands of top-level declarations (Lua, with all
        // its #included prototypes) nests FnsCons thousands deep — recursing would
        // overflow dotcc's own stack. Flatten with an explicit stack (pushing the
        // right child first so the left is processed first), preserving source order.
        var stack = new Stack<Item>();
        stack.Push(it);
        var ordered = new List<Item>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n.Content)
            {
                case C.FnsCons c: stack.Push(c.Arg1); stack.Push(c.Arg0); break;
                case C.FnsOne o: stack.Push(o.Arg0); break;
                default: ordered.Add(n); break;
            }
        }
        foreach (var f in ordered) { onFn(f); }
    }

    private void BuildTopLevel(Item fn)
    {
        switch (fn.Content)
        {
            // C23 `[[attr]]` prepending a file-scope declaration — gate C23,
            // collect the attrs dotcc lowers (`noreturn` → [DoesNotReturn],
            // `deprecated` → [Obsolete]; every other attr is ACCEPTED + IGNORED),
            // then unwrap to the inner declaration, which applies them if it
            // declares a function (ApplyFnMarkers). Chained specs recurse and
            // accumulate; a non-function declaration ignores them (the clear).
            case C.AttrFn a:
                Gate(2023, "[[attributes]]", fn);
                CollectDeclAttrs(a.Arg1);
                BuildTopLevel(a.Arg4);
                _pendingAttrNoreturn = false;
                _pendingAttrDeprecated = null;
                _pendingAttrNodiscard = null;
                break;
            case C.FuncDef d: BuildFuncDef(d.Arg0, d.Arg1, Fingerprints.Of(fn)); break;
            // A header-defined file-scope variable (chibi sexp.h's `static const
            // unsigned char sexp_uvector_sizes[] = {…};`) re-arrives once per TU
            // that includes the header. An identical re-definition is the same
            // object — the first build's field + file-scope binding serve every
            // TU, so skip it (mirrors BuildFuncDef's static-inline dedup; see
            // AlreadySeenTopLevel for the per-TU-state caveat).
            case C.GlobalDeclList g when StorageClassOf(g.Arg0) is null or SpecKw.Static
                    && !DeclaresFunction(g.Arg1) && AlreadySeenTopLevel(fn):
                break;
            // File-scope declarations, any declarator in any position (scalar, struct,
            // array, fn-ptr, fn-ptr table), dispatched on the storage class.
            case C.GlobalDeclList g: BuildFileScopeDecl(g.Arg0, g.Arg1); break;
            // A declaration with no declarators: `struct Node { … };`, `struct
            // Node;`, `enum { A, B };`. Resolving the type defines what it declares.
            case C.TagDecl t: BuildTagDecl(t.Arg0); break;
            case C.FnEmpty: _gate?.Report("ISO C does not allow extra ';' outside of a function", fn.Position.Line); break;
            // `_Static_assert(expr[, "msg"]);` at file scope — a compile-time-only
            // assertion, EVALUATED here (C11 §6.7.10) via the unified comptime
            // interpreter. A holding assertion emits nothing; a zero or non-constant
            // controlling expression is a collected compile error. The message-less
            // arity gates C23 (it postdates the two-arg C11 form).
            case C.StaticAssert sa: Gate(2011, "_Static_assert", fn); CheckStaticAssert(sa.Arg2, sa.Arg4, SrcPos.From(fn)); break;
            case C.StaticAssertNoMsg sa: Gate(2023, "_Static_assert with no message", fn); CheckStaticAssert(sa.Arg2, null, SrcPos.From(fn)); break;
            default: throw new IrUnsupportedException(TypeName(fn.Content));
        }
    }

    // Structural fingerprints of every file-scope variable definition built so
    // far — the global-side twin of _fnDefSites (which needs per-site symbols;
    // globals don't, because their file-scope binding persists across TUs).
    private readonly HashSet<Frontends.ParseFingerprints.Fp> _seenTopLevelDefs = new();

    /// <summary>True when an identical file-scope variable definition was already
    /// built (same position-free structural dump = same post-expansion tokens,
    /// i.e. the same header re-included by another TU). Caveat: C would give each
    /// TU its OWN copy of a header-defined MUTABLE <c>static</c> variable; dotcc
    /// merges them into one field. For the idiom that actually occurs (header
    /// <c>static const</c> tables) the two are indistinguishable.</summary>
    private bool AlreadySeenTopLevel(Item fn) => !_seenTopLevelDefs.Add(Fingerprints.Of(fn));

    /// <summary>The structural fingerprints of the file-scope definitions this builder binds, computed by the parser
    /// that built them (<see cref="Frontends.ParseFingerprints.Wrap"/>).</summary>
    internal required Frontends.ParseFingerprints Fingerprints { get; init; }

    /// <summary>A file-scope declaration with declarators, dispatched on the storage
    /// class its Type carries (<see cref="CheckDeclSpecs(Item)"/>): <c>typedef</c> names
    /// an alias per declarator, which ResolveType's TypeName case then sees through
    /// everywhere; <c>extern</c> declares the names and types for resolution but emits
    /// no storage (the definition lives in another TU, dotcc's whole-program model);
    /// a plain or <c>static</c> declaration defines (internal linkage is a no-op for a
    /// never-exported variable, so both lower to a <c>DotCcGlobals</c> field).</summary>
    private void BuildFileScopeDecl(Item typeItem, Item listItem)
    {
        switch (CheckDeclSpecs(typeItem))
        {
            case SpecKw.Typedef: BuildTypedefs(typeItem, listItem); break;
            case SpecKw.Extern: BuildGlobalDecls(typeItem, listItem, Storage.Extern, internalLinkage: false); break;
            case SpecKw.Static: BuildGlobalDecls(typeItem, listItem, Storage.Static, internalLinkage: true); break;
            case SpecKw.Auto: BuildGlobalDecls(typeItem, listItem, Storage.Auto, internalLinkage: false); break;
            default: BuildGlobalDecls(typeItem, listItem, Storage.Static, internalLinkage: false); break;
        }
    }

    /// <summary>File-scope declaration. A function declarator declares a function (a
    /// prototype; <paramref name="internalLinkage"/> for <c>static</c>); every other
    /// declarator becomes a <c>DotCcGlobals</c> field (codegen emits <c>public static
    /// unsafe T name</c>; an array, a pinned array behind a <c>T*</c>). An
    /// <c>extern</c> declaration is registered for resolution only (no field), unless
    /// it has an initializer, which makes it a definition (C11 6.9.2p1; gcc warns).</summary>
    private void BuildGlobalDecls(Item typeItem, Item listItem, Storage storage, bool internalLinkage)
    {
        _sawThreadLocalSpec = false; // consumed below: set by THIS declaration's spec resolution
        _sawConstexprSpec = false;   // same discipline
        _sawNoreturnSpec = false;    // a function declarator's `_Noreturn` / `inline`
        _sawInlineSpec = false;
        var isRegister = DeclaresRegister(typeItem);
        WalkDeclList(typeItem, listItem, d =>
        {
            if (storage == Storage.Auto)
            {
                // `auto` gives automatic storage duration, which nothing at file scope
                // has (C11 6.9p2); gcc's error. It lowers as a plain declaration.
                Diagnostics.Add(new Diagnostic(Severity.Error, $"file-scope declaration of '{d.Name}' specifies 'auto'", SrcPos.From(d.At), _file));
            }
            if (d.Type is CType.Func { IsFunctionType: true } fnType)
            {
                if (isRegister) { InvalidFunctionStorage(d); }
                DeclareFunctionDeclarator(d, fnType, internalLinkage);
                return;
            }
            var name = d.Name;
            if (isRegister)
            {
                // A file-scope `register` object is a GNU global register variable,
                // which needs an `asm("reg")` name dotcc does not model; gcc's errors.
                _gate?.Report($"file-scope declaration of '{name}' specifies 'register'", d.At.Position.Line);
                Diagnostics.Add(new Diagnostic(Severity.Error, $"register name not specified for '{name}'", SrcPos.From(d.At), _file));
            }
            var declStorage = storage == Storage.Auto ? Storage.Static : storage;
            if (storage == Storage.Extern && d.Init is not null)
            {
                Diagnostics.Add(new Diagnostic(Severity.Warning, $"'{name}' initialized and declared 'extern'", SrcPos.From(d.At), _file));
                declStorage = Storage.Static;
            }
            if (d.Type.Unqualified is CType.Array arr)
            {
                if (_sawConstexprSpec)
                {
                    throw new IrUnsupportedException(
                        $"'{name}': only integer constexpr objects are supported (float/pointer/struct/array constexpr is not built yet)");
                }
                if (_sawThreadLocalSpec)
                {
                    throw new IrUnsupportedException($"'{name}': a _Thread_local array is not supported");
                }
                if (declStorage == Storage.Extern)
                {
                    // Storage lives elsewhere: register the name so same-TU references
                    // resolve. A sized extent keeps the array type for `sizeof`; an open
                    // one (`extern const char lua_ident[];`) decays to a pointer.
                    _symbols.Declare(new Symbol
                    {
                        Name = name, Kind = SymKind.Var, Storage = Storage.Extern, IsGlobal = true,
                        Type = arr.Count is null ? new CType.Pointer(arr.Element) : arr,
                    });
                    return;
                }
                BuildStaticArray(d, arr, csName: null, tuLocal: internalLinkage);
                _definedGlobalNames.Add(name); // a real definition — satisfies any extern decl
                return;
            }
            // A second definition of an object this unit already defines completes it: a
            // tentative one adds nothing, an initialized one replaces the tentative one under
            // the same symbol (every earlier reference holds it), and two initializers are
            // gcc's redefinition error.
            if (declStorage != Storage.Extern && _unitObjects.TryGetValue(name, out var prior))
            {
                if (d.Init is null) { return; }
                if (prior.Init is not null)
                {
                    Diagnostics.Add(new Diagnostic(Severity.Error, $"redefinition of '{name}'", SrcPos.From(d.At), _file));
                    return;
                }
                Globals.Remove(prior);
                prior.Sym.Type = d.Type;
                DefineFileScopeObject(prior.Sym, d, typeItem);
                return;
            }
            var sym = _symbols.Declare(new Symbol
            {
                Name = name, Kind = SymKind.Var, Storage = declStorage, IsGlobal = true,
                IsTuLocal = internalLinkage,
                // A constexpr object is const-qualified (C23 §6.7.1p5 implies it),
                // so the standard write-to-const error covers assignments.
                Type = _sawConstexprSpec ? d.Type.WithQuals(TypeQual.Const) : d.Type,
                IsThreadLocal = _sawThreadLocalSpec,
                IsConstexpr = _sawConstexprSpec,
            });
            if (declStorage != Storage.Extern) { DefineFileScopeObject(sym, d, typeItem); }
        });
    }

    /// <summary>Define the file-scope object <paramref name="sym"/> declared by
    /// <paramref name="d"/>: its initializer, if any, and its <see cref="GlobalVar"/>, which
    /// this unit's later declarations of the name find in <see cref="_unitObjects"/>.</summary>
    private void DefineFileScopeObject(Symbol sym, Declarator d, Item typeItem)
    {
        _definedGlobalNames.Add(sym.Name); // a real definition — satisfies any extern decl
        CExpr? gInit = null;
        FlexibleTail? flexible = null;
        if (d.Init is { } ii)
        {
            gInit = BuildInitValue(sym.Type, ii);
            CheckQualifierDiscard(gInit, sym.Type, SrcPos.From(ii), "initialization");
            (gInit, flexible) = SplitFlexibleInit(gInit, SrcPos.From(ii));
            if (flexible is not null && sym.IsThreadLocal)
            {
                throw new IrUnsupportedException($"'{sym.Name}': a _Thread_local object with an initialized flexible array member is not supported");
            }
        }
        PropagateNativeCallConv(sym, gInit);
        if (sym.IsConstexpr) { BindConstexpr(sym, gInit, SrcPos.From(typeItem)); }
        var global = new GlobalVar(sym, gInit)
        {
            PerThreadInit = sym.IsThreadLocal && gInit is { } ti && !Module.IsZeroInitializer(ti),
            Flexible = flexible,
        };
        Globals.Add(global);
        _unitObjects[sym.Name] = global;
    }

    // ---- function markers (inline / noreturn / deprecated) ----------------
    // _sawNoreturnSpec/_sawInlineSpec: the signature's spec resolution saw the
    // `_Noreturn` (C11; the C23 lowercase `noreturn` arrives pre-promoted onto the
    // same terminal) / `inline` (C99) function specifier — reset by BuildFuncDef
    // immediately before ExtractFnSig, and at the start of every declaration that
    // may hold a function declarator, so only THIS declaration's specifiers count. _pendingAttrNoreturn/_pendingAttrDeprecated:
    // the recognized attrs of an enclosing C23 `[[…]]` specifier (BuildTopLevel's
    // AttrFn case), consumed by the wrapped function declaration and cleared on unwind.
    private bool _sawNoreturnSpec;
    private bool _sawInlineSpec;
    // `_Thread_local` (C11) seen by a FILE-SCOPE declaration's spec resolution —
    // reset + consumed by BuildGlobalDecls (block scope rejects immediately in
    // RecordDeclSpecs instead, so the flag never carries block-scope state).
    private bool _sawThreadLocalSpec;
    // `constexpr` (C23) seen by a declaration's spec resolution — reset + consumed
    // by BuildGlobalDecls (file scope) and BuildDeclList (block scope; C23 allows
    // both). The declared symbol binds its ConstEval'd value.
    private bool _sawConstexprSpec;
    private bool _pendingAttrNoreturn;
    private string? _pendingAttrDeprecated;
    // The recognized C23 `[[nodiscard]]` / `[[nodiscard("reason")]]` of an enclosing
    // `[[…]]` specifier (null = absent, "" = message-less), consumed by the wrapped
    // function declaration (ApplyFnMarkers → Symbol.Nodiscard) and cleared on unwind.
    private string? _pendingAttrNodiscard;

    /// <summary>Record one function/storage specifier prefix of the declaration
    /// being built — `_Noreturn` (gated C11) and `inline` for the enclosing function
    /// symbol, `_Thread_local` (gated C11) for the enclosing file-scope variable
    /// declaration, `constexpr` for the enclosing object. The flags are reset at
    /// the start of each declaration, so a pair of prefixes sees the other's flag.
    /// A block-scope `_Thread_local` (even `static _Thread_local`, which C allows)
    /// is a loud V1 rejection — dotcc lowers thread-locals as file-scope
    /// [ThreadStatic] fields only.</summary>
    private void RecordDeclSpec(string spec, SrcPos pos)
    {
        switch (spec)
        {
            case "_Noreturn": Gate(2011, "_Noreturn", pos); _sawNoreturnSpec = true; break;
            case "inline": _sawInlineSpec = true; break; // no gate — pre-C99 rejection is structural (rule 2)
            case "_Thread_local":
                Gate(2011, "_Thread_local", pos);
                if (_sawConstexprSpec) { ConstexprWithThreadLocal(pos); }
                if (_symbols.AtFileScope) { _sawThreadLocalSpec = true; }
                else
                {
                    Diagnostics.Add(new Diagnostic(Severity.Error,
                        "'_Thread_local' at block scope is not supported (dotcc lowers thread-locals as file-scope [ThreadStatic] fields only)",
                        pos, _file));
                }
                break;
            case "constexpr":
                // No Gate: the spelling only becomes a keyword via rule-2 promotion
                // under -std=c23, so a pre-C23 dialect rejects structurally (the ID
                // never reaches here). C23 §6.7.1p5 forbids combining with
                // _Thread_local.
                if (_sawThreadLocalSpec) { ConstexprWithThreadLocal(pos); }
                _sawConstexprSpec = true;
                break;
        }
    }

    /// <summary>C23 6.7.1p5: <c>constexpr</c> does not combine with
    /// <c>_Thread_local</c>.</summary>
    private void ConstexprWithThreadLocal(SrcPos pos)
        => Diagnostics.Add(new Diagnostic(Severity.Error,
            "'constexpr' may not be used with '_Thread_local'", pos, _file));

    /// <summary>Bind a C23 <c>constexpr</c> object's compile-time value onto its
    /// symbol: the initializer must exist, fold via <see cref="ConstEval"/>, and be
    /// representable in the declared type (all three are C23 constraints; the first
    /// and last use gcc's exact wording). The value lands in
    /// <see cref="Symbol.ConstValue"/>, which the comptime interpreter substitutes
    /// at every use in a constant expression. V1 is the integer family — a float/
    /// pointer/struct/array constexpr (legal C23) is a loud unsupported cut.</summary>
    private void BindConstexpr(Symbol sym, CExpr? init, SrcPos pos)
    {
        if (!sym.Type.IsInteger)
        {
            throw new IrUnsupportedException(
                $"'{sym.Name}': only integer constexpr objects are supported (float/pointer/struct/array constexpr is not built yet)");
        }
        if (init is null)
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                "'constexpr' requires an initialized data declaration", pos, _file));
            return;
        }
        if (ConstEval(init) is not { } v)
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                "initializer element is not constant", pos, _file));
            return;
        }
        if (!ConstexprRepresentable(v, sym.Type))
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                "'constexpr' initializer not representable in type of object", pos, _file));
            return;
        }
        sym.ConstValue = v;
    }

    /// <summary>C23 §6.7.1p6: a constexpr initializer must be exactly representable
    /// in the object's type. Derived from <see cref="CType.Prim"/>'s width +
    /// signedness; 8-byte types accept any <c>long</c> bit pattern, and non-Prim
    /// integer types (enum) skip the check.</summary>
    private static bool ConstexprRepresentable(long v, CType t) => t.Unqualified switch
    {
        CType.Prim { Bytes: 8 } => true,
        CType.Prim { Signed: true, Bytes: var b } => v >= -(1L << (b * 8 - 1)) && v < 1L << (b * 8 - 1),
        CType.Prim { Signed: false, Bytes: var b } => v >= 0 && v < 1L << (b * 8),
        _ => true,
    };

    /// <summary>Walk a DECLARATION's <c>AttrList</c> collecting the attributes that
    /// stick to the declared function symbol: `noreturn` (a bare ID pre-C23; the
    /// promoted `_Noreturn` keyword under c23) → [DoesNotReturn], `deprecated` (bare
    /// or with a string-literal message) → [Obsolete], and `nodiscard` (bare or with
    /// a reason) → the -Wunused-result discard warning. The remaining C23 standard
    /// attributes carry nothing HERE: `maybe_unused` has teeth only on a block-scope
    /// declaration (handled on the built DeclStmt — see the `C.AttrStmt` case, not
    /// this decl path; on a function/global C# never warns, so it's a true no-op),
    /// `fallthrough` attaches to a statement (<see cref="AttrListHasFallthrough"/>),
    /// and `unsequenced` / `reproducible` are purity hints with no teeth-bearing .NET
    /// counterpart — mapping them to the vestigial [Pure] (the JIT ignores it) would
    /// be cosmetic, so they are recognized-standard-but-DELIBERATELY-inert. Vendor
    /// namespaced attrs (`gnu::aligned(N)`, …) are likewise accepted + ignored.</summary>
    private void CollectDeclAttrs(Item it)
    {
        switch (it.Content)
        {
            case C.AttrListCons c: CollectDeclAttrs(c.Arg0); CollectDeclAttrs(c.Arg2); break;
            case C.AttrNoreturn: _pendingAttrNoreturn = true; break;
            case C.AttrIdent a when Tok(a.Arg0) == "noreturn": _pendingAttrNoreturn = true; break;
            case C.AttrIdent a when Tok(a.Arg0) == "deprecated": _pendingAttrDeprecated ??= ""; break;
            case C.AttrCall a when Tok(a.Arg0) == "deprecated":
                _pendingAttrDeprecated ??= TryStringLiteral(a.Arg2) ?? "";
                break;
            case C.AttrIdent a when Tok(a.Arg0) == "nodiscard": _pendingAttrNodiscard ??= ""; break;
            case C.AttrCall a when Tok(a.Arg0) == "nodiscard":
                _pendingAttrNodiscard ??= TryStringLiteral(a.Arg2) ?? "";
                break;
            default: break; // all other attribute shapes: accepted + ignored
        }
    }

    /// <summary>True when an <c>AttrList</c> contains the C23 <c>[[fallthrough]]</c>
    /// attribute (a bare identifier). Recognized structurally — like the attributes in
    /// <see cref="CollectDeclAttrs"/> — but kept separate: fallthrough attaches to a
    /// statement (a switch fall-through point), not to a declared symbol, so it drives
    /// <see cref="CheckImplicitFallthrough"/> rather than a <c>_pendingAttr*</c> flag.</summary>
    private bool AttrListHasFallthrough(Item it) => it.Content switch
    {
        C.AttrListCons c => AttrListHasFallthrough(c.Arg0) || AttrListHasFallthrough(c.Arg2),
        C.AttrIdent a => Tok(a.Arg0) == "fallthrough",
        _ => false,
    };

    /// <summary>True when an <c>AttrList</c> contains the C23 <c>[[maybe_unused]]</c>
    /// attribute (a bare identifier). Kept separate from <see cref="CollectDeclAttrs"/>
    /// for the same reason as fallthrough: it attaches to the STATEMENT (a block-scope
    /// declaration whose local(s) might go unread), not to a declared function symbol,
    /// so it rides the built <see cref="DeclStmt"/> — see the <c>C.AttrStmt</c> case.</summary>
    private bool AttrListHasMaybeUnused(Item it) => it.Content switch
    {
        C.AttrListCons c => AttrListHasMaybeUnused(c.Arg0) || AttrListHasMaybeUnused(c.Arg2),
        C.AttrIdent a => Tok(a.Arg0) == "maybe_unused",
        _ => false,
    };

    /// <summary>The decoded UTF-8 text of a string-literal expression item, or null
    /// when the expression isn't a plain string literal. Structural (AST) recognition;
    /// adjacent segments concatenate and escapes decode through the same
    /// single-source-of-truth decoder as string lowering.</summary>
    private string? TryStringLiteral(Item e)
    {
        if (e.Content is not C.Str s) { return null; }
        var bytes = DotCC.EmitHelpers.StringByteValues(CollectStrSegments(s.Arg0));
        var arr = new byte[bytes.Count];
        for (var i = 0; i < bytes.Count; i++) { arr[i] = (byte)bytes[i]; }
        return System.Text.Encoding.UTF8.GetString(arr);
    }

    /// <summary>Apply the collected function markers to a just-declared function
    /// symbol: the `_Noreturn` specifier seen by this signature's spec resolution
    /// and any recognized attribute from an enclosing `[[…]]` specifier. Additive —
    /// a marker spelled on either the prototype or the definition sticks to the
    /// shared symbol, so the emitted method carries it either way.</summary>
    private void ApplyFnMarkers(Symbol sym)
    {
        if (_sawNoreturnSpec || _pendingAttrNoreturn) { sym.IsNoReturn = true; }
        if (_sawInlineSpec) { sym.IsInline = true; }
        if (_pendingAttrDeprecated is { } dep && sym.Deprecated is null) { sym.Deprecated = dep; }
        if (_pendingAttrNodiscard is { } nd && sym.Nodiscard is null) { sym.Nodiscard = nd; }
    }

    /// <summary>Warn (gcc <c>-Wunused-result</c>, on by default — the attribute's
    /// whole purpose is to diagnose the discard) when a call whose callee is declared
    /// C23 <c>[[nodiscard]]</c> has its non-void result thrown away in statement
    /// position. A <c>(void)f()</c> cast suppresses it for free: <see cref="BuildCast"/>
    /// lowers <c>(void)expr</c> to the operand with its <see cref="CType"/> set to
    /// <see cref="CType.Void"/>, so the discarded value is void-typed here and the
    /// check skips — exactly C's suppression idiom. gcc-verbatim wording.</summary>
    private void CheckNodiscardDiscarded(CExpr discarded, SrcPos pos)
    {
        if (discarded is Call { CalleeSym: { Nodiscard: { } reason } sym } call
            && call.Type.Unqualified is not CType.VoidType)
        {
            var suffix = reason.Length > 0 ? $": \"{reason}\"" : "";
            Diagnostics.Add(new Diagnostic(Severity.Warning,
                $"ignoring return value of '{sym.Name}', declared with attribute 'nodiscard'{suffix}",
                pos, _file));
        }
    }

    /// <summary>One already-built function DEFINITION: its structural fingerprint and
    /// the symbol it bound.</summary>
    private sealed class FnDefSite
    {
        /// <summary>Position-free structural fingerprint of the definition: identical
        /// ⇔ the same post-expansion tokens, i.e. the same header re-included.</summary>
        public required Frontends.ParseFingerprints.Fp Print;
        public required Symbol Sym;
    }

    // Definitions seen so far, by C name — the whole-program merge's handling of
    // C internal linkage. A `static` name may be defined in several TUs: an
    // identical re-definition (a header-defined `static inline` re-included by
    // the next TU) re-binds to the one emitted copy; a different body is a fresh
    // per-TU function under a uniquified TargetName.
    private readonly Dictionary<string, List<FnDefSite>> _fnDefSites = new(StringComparer.Ordinal);

    // ---- import-mode candidate tracking (native `-l` linking) ----------------
    // _protoOnlyFuncs: functions DECLARED (prototype) but defined in NO translation
    // unit — potential native imports. _referencedFuncs: every name actually called
    // (a conservative superset; filtered by the proto-only set). A function that is
    // proto-only AND referenced AND not from a synthetic header AND non-variadic is
    // what an `-l` library must resolve. See ProtoOnlyReferenced.
    private readonly Dictionary<string, Symbol> _protoOnlyFuncs = new(StringComparer.Ordinal);

    /// <summary>True while a library unit is bound (see <see cref="AddUnit(Item, string, bool)"/>).</summary>
    private bool _libraryUnit;

    /// <summary>The functions called by name that no unit bound so far defines: what a library
    /// unit is looked up for.</summary>
    internal IEnumerable<string> UndefinedCalledFunctions() =>
        _referencedFuncs.Where(name => !_fnDefSites.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
    private readonly HashSet<string> _referencedFuncs = new(StringComparer.Ordinal);
    // Extern DATA (`extern int x;`) read/written through its extern symbol, and the
    // names some TU actually DEFINES. An extern referenced but defined nowhere would
    // need a native data import — unsupported in V1 (the emit pass warns). See
    // ExternDataReferenced.
    private readonly HashSet<string> _referencedExternData = new(StringComparer.Ordinal);
    private readonly HashSet<string> _definedGlobalNames = new(StringComparer.Ordinal);

    private void BuildFuncDef(Item fnSig, Item block, Frontends.ParseFingerprints.Fp print)
    {
        _sawNoreturnSpec = false;
        _sawInlineSpec = false;
        // A function definition is `extern` or `static` (C11 6.9.1p4); gcc's wording,
        // an error for `register` and `typedef` and a warning for `auto`.
        if (CheckDeclSpecs(FnSigType(fnSig)) is { } storage and (SpecKw.Register or SpecKw.Typedef or SpecKw.Auto))
        {
            Diagnostics.Add(new Diagnostic(storage == SpecKw.Auto ? Severity.Warning : Severity.Error,
                $"function definition declared '{Spelling(storage)}'", SrcPos.From(fnSig), _file));
        }
        var sig = ExtractFnSig(fnSig);
        // A library unit's external definition is weak: the program's own definition of the
        // name wins. A static one is the unit's own, whatever other units define.
        if (_libraryUnit && !sig.IsStatic && _fnDefSites.ContainsKey(sig.Name)) { return; }
        // A definition means this name is no longer a pure prototype → not an import.
        _protoOnlyFuncs.Remove(sig.Name);
        Symbol funcSym;
        if (_fnDefSites.TryGetValue(sig.Name, out var sites))
        {
            foreach (var site in sites)
            {
                if (site.Print == print)
                {
                    // Identical re-definition: one emitted copy serves all TUs —
                    // re-bind this TU's references to it and build nothing.
                    _symbols.DeclareAlias(site.Sym);
                    return;
                }
            }
            var paramTypes = new List<CType>(sig.Params.Count);
            foreach (var (t, _) in sig.Params) { paramTypes.Add(t); }
            if (sig.IsStatic)
            {
                // New definition has INTERNAL linkage: a fresh per-TU function
                // under a uniquified TargetName.
                funcSym = _symbols.DeclareAlias(new Symbol
                {
                    Name = sig.Name,
                    Kind = SymKind.Func,
                    Type = new CType.Func(sig.Return, paramTypes, sig.Variadic),
                    Storage = Storage.Static,
                    IsGlobal = true,
                    IsTuLocal = true,
                    // Program-unique: a TU defines a static name at most once, so the
                    // per-name site count suffices (same scheme as static locals' __s{n}).
                    TargetName = $"{_symbols.Escape(sig.Name)}__{sites.Count + 1}",
                });
            }
            else
            {
                // New definition has EXTERNAL linkage — it owns the canonical global
                // name. Legal only if every prior definition of this name was
                // `static` (internal linkage, TU-local); two external definitions
                // are a genuine multiple-definition link error. The motivating case
                // is chibi's core `static sexp_string_hash` (sexp.c) coexisting with
                // srfi/69's exported `sexp_string_hash` (hash.c, pulled into eval.c
                // by the static-clibs include) — legal C the whole-program merge
                // must not reject.
                foreach (var site in sites)
                {
                    if (site.Sym.Storage != Storage.Static)
                    {
                        throw new IrUnsupportedException(
                            $"duplicate definition of external function '{sig.Name}' (a real linker would reject this too)");
                    }
                }
                // A prior `static` definition holds the canonical name (the first
                // occurrence keeps it). It is TU-local, so move it aside to `__1`
                // (never assigned by the static-uniquify scheme above, which starts
                // at __2); its callers hold the Symbol, so the rename rides through.
                // The external then claims the canonical name.
                var canonical = _symbols.Escape(sig.Name);
                foreach (var site in sites)
                {
                    if (site.Sym.TargetName == canonical) { site.Sym.TargetName = $"{canonical}__1"; }
                }
                funcSym = _symbols.Declare(new Symbol
                {
                    Name = sig.Name,
                    Kind = SymKind.Func,
                    Type = new CType.Func(sig.Return, paramTypes, sig.Variadic),
                    Storage = Storage.None,
                    IsGlobal = true,
                });
            }
            sites.Add(new FnDefSite { Print = print, Sym = funcSym });
        }
        else
        {
            funcSym = DeclareFunc(sig, fromSystemHeader: fnSig.Position.Line >= SrcPos.SyntheticLineBase);
            _fnDefSites[sig.Name] = new List<FnDefSite> { new() { Print = print, Sym = funcSym } };
        }

        ApplyFnMarkers(funcSym);
        if (_libraryUnit && !sig.IsStatic) { Module.LibraryFunctions.Add(sig.Name); }
        _symbols.BeginFunction();
        _setjmpCalls.Clear(); // per-function stray-setjmp tracking (see the field)
        _currentFnName = sig.Name; // drives the `__func__` predefined identifier
        _currentRet = (funcSym.Type as CType.Func)?.Return; // drives return const-discard
        _symbols.EnterScope(); // parameter scope
        var paramSyms = new List<Symbol>(sig.Params.Count);
        foreach (var p in sig.Params)
        {
            paramSyms.Add(_symbols.Declare(new Symbol
            {
                Name = p.Name, Kind = SymKind.Param, Type = p.Type,
                Storage = p.IsRegister ? Storage.Register : Storage.None,
            }));
        }
        var built = BuildBlock(block);
        // Reject a setjmp in an unmodeled position BEFORE malloc-promote — it may re-clone
        // a stray call's containing expression and orphan the tracked reference.
        RejectStraySetjmp();
        var body = PromoteMallocs(built);
        _symbols.ExitScope();

        Functions.Add(new FuncDef(funcSym, paramSyms, body, sig.Variadic));
    }

    /// <summary>After a function body is built, any <c>setjmp</c> call NOT claimed by a
    /// recogniser (<see cref="SetjmpGuardOf"/>, <see cref="RewriteSetjmpCaptures"/>, the
    /// switch-subject wrap) is in a shape dotcc can't faithfully model — it would lower to
    /// the always-0 runtime stub and a `longjmp` at it would become an uncaught exception.
    /// Reject it loudly (constraint: no silent miscompiles), naming the supported shapes.</summary>
    private void RejectStraySetjmp()
    {
        if (_setjmpCalls.Count == 0) { return; }
        // Report the earliest stray for a stable message; positions are line/column.
        SrcPos where = default;
        var first = true;
        foreach (var pos in _setjmpCalls.Values)
        {
            if (first || pos.Line < where.Line || (pos.Line == where.Line && pos.Column < where.Column))
            { where = pos; first = false; }
        }
        _setjmpCalls.Clear();
        throw new DotCC.CompileException(
            $"setjmp at {where}: this use of setjmp is not supported. dotcc recognises setjmp only as "
            + "an `if` guard — `if (setjmp(env))` / `if (setjmp(env) == 0)` — as a value capture "
            + "— `T r = setjmp(env);` (or `r = setjmp(env);`) followed by a `switch`/`if` on `r` — "
            + "or a `switch (setjmp(env)) { … }`. Rewrite the call into one of these shapes "
            + "(setjmp in a loop/ternary condition, a nested sub-expression, or a discarded call "
            + "cannot be modeled as structured control flow).");
    }

    private Symbol DeclareFunc(FnSig sig, bool fromSystemHeader = false)
    {
        var existing = _symbols.Resolve(sig.Name);
        if (existing is { Kind: SymKind.Func }) { return existing; } // re-declaration (proto then def)
        var paramTypes = new List<CType>(sig.Params.Count);
        foreach (var (t, _) in sig.Params) { paramTypes.Add(t); }
        return _symbols.Declare(new Symbol
        {
            Name = sig.Name,
            Kind = SymKind.Func,
            Type = new CType.Func(sig.Return, paramTypes, sig.Variadic),
            Storage = sig.IsStatic ? Storage.Static : Storage.None,
            IsGlobal = true,
            IsTuLocal = sig.IsStatic,
            FromSystemHeader = fromSystemHeader,
        });
    }

    // ---- function signatures --------------------------------------------

    private readonly record struct FnSig(CType Return, string Name, List<ParamInfo> Params, bool Variadic, bool IsStatic);

    private FnSig ExtractFnSig(Item it) => it.Content switch
    {
        C.FnSig n => new(ResolveType(n.Arg0), Tok(n.Arg1), BuildParams(n.Arg3, out var v0), v0, DeclaresStatic(n.Arg0)),
        C.FnSigNoArgs n => new(ResolveType(n.Arg0), Tok(n.Arg1), new(), false, DeclaresStatic(n.Arg0)),
        // Parenthesized declarator name `T (name)(args)` — identical to
        // `T name(args)`; the parens are pure grouping around the name (public
        // headers wrap API names so a same-named function-like macro can't expand
        // at the declaration). Name is Arg2, the param list Arg5.
        C.FnSigParen n => new(ResolveType(n.Arg0), Tok(n.Arg2), BuildParams(n.Arg5, out var vp), vp, DeclaresStatic(n.Arg0)),
        C.FnSigParenNoArgs n => new(ResolveType(n.Arg0), Tok(n.Arg2), new(), false, DeclaresStatic(n.Arg0)),
        // Function returning a function pointer: `Ret (*name(params))(fnPtrParams)`
        // (e.g. <signal.h>'s `void (*signal(int, void(*)(int)))(int)`). The result
        // type is the function-pointer `Ret (*)(fnPtrParams)`; name + params are the
        // outer declarator's.
        C.FnSigRetFnPtr n => new(FnPtrType(n.Arg0, n.Arg9), Tok(n.Arg3), BuildParams(n.Arg5, out var vr), vr, DeclaresStatic(n.Arg0)),
        C.FnSigRetFnPtrNoArgs n => new(FnPtrType(n.Arg0, null), Tok(n.Arg3), BuildParams(n.Arg5, out var vrn), vrn, DeclaresStatic(n.Arg0)),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    private List<ParamInfo> BuildParams(Item paramList, out bool variadic)
    {
        var acc = new List<ParamInfo>();
        var vararg = false;
        var unnamed = 0;
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.ParamsCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.ParamsOne o: Walk(o.Arg0); break;
                case C.ParamsVararg v: Walk(v.Arg0); vararg = true; break;
                // A param whose TYPE resolves to an array (an array-typedef
                // alias like chibi's `sexp_abi_identifier_t`) decays to a
                // pointer exactly like the explicit `T name[]` forms below
                // (C99 §6.7.5.3p7 applies through a typedef too).
                case C.Param p:
                    CheckParamSpecs(p.Arg0, Tok(p.Arg1));
                    acc.Add(new(DecayParam(ResolveType(p.Arg0)), Tok(p.Arg1)) { IsRegister = DeclaresRegister(p.Arg0) });
                    break;
                case C.ParamUnnamed p: CheckParamSpecs(p.Arg0, null); acc.Add(new(DecayParam(ResolveType(p.Arg0)), "_p" + unnamed++)); break;
                case C.ParamArrayUnsized p: CheckParamSpecs(p.Arg0, Tok(p.Arg1)); acc.Add(new(new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                case C.ParamArraySized p: CheckParamSpecs(p.Arg0, Tok(p.Arg1)); acc.Add(new(new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                // Function-pointer parameter: `Ret (*name)(paramTypes)` and its
                // `(*const name)` / `(**name)` forms.
                case C.ParamFnPtrDecl p:
                {
                    var (name, type) = FnPtrDeclarator(ResolveType(p.Arg0), p.Arg1, p.Arg2);
                    CheckParamSpecs(p.Arg0, name);
                    acc.Add(new(type, name));
                    break;
                }
                // A parameter of function type is a pointer to that function
                // (C11 6.7.6.3p8): `Ret name(paramTypes)`, `Ret (name)(paramTypes)`.
                case C.ParamFnType p: CheckParamSpecs(p.Arg0, Tok(p.Arg1)); acc.Add(new(FnPtrTailType(ResolveType(p.Arg0), p.Arg2), Tok(p.Arg1))); break;
                case C.ParamParenFnType p: CheckParamSpecs(p.Arg0, Tok(p.Arg2)); acc.Add(new(FnPtrTailType(ResolveType(p.Arg0), p.Arg4), Tok(p.Arg2))); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        Walk(paramList);
        variadic = vararg;
        // `(void)`: an unnamed parameter of type void as the only item means the
        // function has no parameters (C11 6.7.6.3p10).
        if (acc.Count == 1 && acc[0].Type.Unqualified is CType.VoidType) { acc.Clear(); }
        return acc;
    }

    /// <summary>Array-to-pointer decay for a parameter type (C99 §6.7.5.3p7).
    /// Only relevant when the type ARRIVES as an array — i.e. through an
    /// array-typedef alias; the explicit <c>T name[]</c> productions decay at
    /// their own case arms.</summary>
    private static CType DecayParam(CType t) => t switch
    {
        CType.Array a => new CType.Pointer(a.Element),
        CType.Func { IsFunctionType: true } f => f with { IsFunctionType = false },
        _ => t,
    };

    // ---- enums -----------------------------------------------------------

    /// <summary>Register an enum definition. Enumerator values auto-increment from
    /// the previous (starting at 0), with an explicit <c>= expr</c> overriding (a
    /// constant integer expression). A TAGGED or TYPEDEF-named enum becomes a real
    /// C# enum: each enumerator is a <see cref="SymKind.EnumConst"/> typed AS the
    /// enum (so it renders <c>EnumName.Member</c> and decays to its underlying int
    /// only at C's plain-int contexts), and an <see cref="EnumTypeDef"/> is emitted.
    /// An ANONYMOUS, un-typedef'd enum has no C# name, so its enumerators stay plain
    /// int constants (named constants) rather than synthesizing a type. Returns the
    /// enum's <see cref="CType"/> — the <see cref="CType.Enum"/>, or
    /// <see cref="CType.Int"/> for the anonymous form — so a typedef caller can alias
    /// the name to it.</summary>
    private CType RegisterEnum(string? tag, Item? baseType, Item enumList, string? typedefName = null)
    {
        var underlying = baseType is { } bt ? ResolveType(bt) : CType.Int;
        if (baseType is { } baseItem) { Gate(2023, "enum with a fixed underlying type", baseItem); }
        // The C# enum name: the tag, else the typedef alias; anonymous + un-typedef'd
        // ⇒ null ⇒ plain int constants.
        var enumName = tag ?? typedefName;
        CType.Enum? enumType = enumName is null ? null : new CType.Enum(enumName, underlying);
        var members = new List<EnumMember>();
        long next = 0;
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.EnumListCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.EnumListOne o: Walk(o.Arg0); break;
                case C.EnumListTrail t: Walk(t.Arg0); break;  // trailing `,` — no member
                case C.EnumItem e: Add(Tok(e.Arg0), null); break;
                case C.EnumItemInit e: Add(Tok(e.Arg0), e.Arg2); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        void Add(string name, Item? valueItem)
        {
            if (valueItem is { } vi)
            {
                next = ConstEval(BuildExpr(vi))
                    ?? throw new IrUnsupportedException("non-constant enum initializer for " + name);
            }
            _symbols.Declare(new Symbol
            {
                Name = name, Kind = SymKind.EnumConst,
                Type = (CType?)enumType ?? CType.Int, ConstValue = next, IsGlobal = true,
            });
            if (enumType is not null) { members.Add(new EnumMember(name, next)); }
            next++;
        }
        Walk(enumList);
        if (enumType is not null)
        {
            if (tag is not null) { _enumTypes[tag] = enumType; }
            AddEnumDef(enumType.Name, underlying, members);
        }
        return (CType?)enumType ?? CType.Int;
    }


    // ---- structs / unions ------------------------------------------------

    /// <summary>Define a struct/union. <paramref name="tag"/> is the C tag (null
    /// for an anonymous <c>typedef struct {…} Alias</c>); <paramref name="alias"/>
    /// the typedef name if any. Emits a C# struct under a canonical name, records
    /// its fields for member-type resolution, and maps the typedef alias to it.
    /// A body a synthetic header gives a runtime-owned aggregate (<paramref name="synthetic"/>, and
    /// <see cref="RuntimeTypeNames.IsRuntimeOwnedAggregate"/>) is registered the same way, marked
    /// <see cref="StructTypeDef.IsRuntimeOwned"/> so the backend emits no second type for it.</summary>
    private void BuildStructDef(string? tag, Item memberList, string? alias, bool isUnion, bool synthetic)
    {
        var canonical = tag ?? alias ?? throw new IrUnsupportedException("struct with neither tag nor typedef name");
        RejectReservedTypeName(canonical, isUnion ? "union" : "struct");
        var fields = BuildStructFields(memberList, canonical);
        if (_emittedTypes.Add(canonical))
        {
            _structFields[canonical] = fields;
            _structIsUnion[canonical] = isUnion;
            var runtimeOwned = synthetic && RuntimeTypeNames.IsRuntimeOwnedAggregate(canonical);
            Types.Add(new StructTypeDef(canonical, fields, isUnion, IsRuntimeOwned: runtimeOwned));
        }
        // `struct Tag` and the typedef alias both resolve to the canonical type.
        if (alias is not null) { _typedefs[alias] = new CType.Named(canonical); }
    }


    /// <summary>Promoted (anonymous-aggregate) field map: per owning struct/union,
    /// each promoted inner field name → (the hidden container field, the nested
    /// aggregate's type name). A C11 anonymous <c>struct {…};</c> / <c>union {…};</c>
    /// member lifts its fields into the parent — dotcc keeps them in a generated
    /// nested type and rewrites <c>v.i</c> to <c>v.hidden.i</c> at access time.</summary>
    private readonly Dictionary<string, Dictionary<string, (string Hidden, string Nested)>> _promoted = new(StringComparer.Ordinal);

    /// <summary>Parse a struct/union member list into <see cref="StructField"/>s for
    /// the <paramref name="owner"/> aggregate. Scalar/pointer, array (incl.
    /// multi-dim / flexible / non-primitive), bit-field, and C11 anonymous
    /// struct/union members are supported.</summary>
    private List<StructField> BuildStructFields(Item memberList, string owner)
    {
        var fields = new List<StructField>();
        void Member(Item m)
        {
            switch (m.Content)
            {
                case C.MembersCons c: Member(c.Arg0); Member(c.Arg1); break;
                case C.MembersOne o: Member(o.Arg0); break;
                // A member declarator list, bit-fields included. A bit-field
                // (`T name : W`) is packed with its consecutive same-size
                // neighbours into one backing field (MSVC storage-unit layout) and
                // read through a masked / sign-extended accessor property, so
                // sizeof, offsets and value semantics match C. An unnamed one
                // (`T : W`) keeps an empty name and its width so the packing
                // reserves its bits (a zero width starts a fresh storage unit); it
                // is no accessible member and positional initializers skip it.
                case C.StructMemberList sm:
                    CheckMemberSpecs(sm.Arg0);
                    WalkDeclList(sm.Arg0, sm.Arg1,
                        d => fields.Add(new StructField(d.Name, MemberDeclaratorType(d, m))),
                        (name, type, width) => fields.Add(new StructField(name, type, BitFieldWidth(width))));
                    break;
                // A member declaration with no declarators: a C11 anonymous
                // struct/union member (its fields promoted into the parent, held in a
                // generated nested aggregate + a hidden field, each inner name
                // recorded so `parent.inner` routes through it), or a nested tag
                // definition. A NAMED nested aggregate (`struct {…} m;`) is an
                // ordinary member list whose Type is the tag definition.
                case C.MemberTagDecl mt: CheckMemberSpecs(mt.Arg0); BuildMemberTagDecl(mt.Arg0, owner, fields, m); break;
                default: throw new IrUnsupportedException(TypeName(m.Content));
            }
        }
        Member(memberList);
        return fields;
    }

    /// <summary>A bit-field's width: an integer constant expression.</summary>
    private int BitFieldWidth(Item width)
        => (int)(ConstEval(BuildExpr(width)) ?? throw new IrUnsupportedException("non-constant bit-field width"));

    /// <summary>Add a C11 anonymous struct/union member: build it as a generated
    /// nested aggregate type, add a hidden container field to the parent, and record
    /// each inner field as promoted (so <c>parent.inner</c> rewrites to
    /// <c>parent.hidden.inner</c> at access time, keeping the union's overlap /
    /// the struct's sequential layout).</summary>
    private void AddAnonMember(Item innerMemberList, string owner, List<StructField> parentFields, bool isUnion)
    {
        var nested = AnonAggregateName(innerMemberList, isUnion);
        if (!_structFields.TryGetValue(nested, out var innerFields))
        {
            innerFields = BuildStructFields(innerMemberList, nested);
            _structFields[nested] = innerFields;
            _structIsUnion[nested] = isUnion;
            Types.Add(new StructTypeDef(nested, innerFields, isUnion));
        }

        var hidden = "__anon_" + nested;
        parentFields.Add(new StructField(hidden, new CType.Named(nested)));

        if (!_promoted.TryGetValue(owner, out var pm)) { _promoted[owner] = pm = new(StringComparer.Ordinal); }
        foreach (var f in innerFields) { pm[f.Name] = (hidden, nested); }
        // The members an anonymous aggregate inside this one promotes are this one's too
        // (C11 6.7.2.1p13 applies at every level): the first hop is through this hidden
        // field, and `nested`'s own table takes the access the rest of the way.
        if (_promoted.TryGetValue(nested, out var deeper))
        {
            foreach (var name in deeper.Keys) { pm[name] = (hidden, nested); }
        }
    }

    /// <summary>The CType of <paramref name="field"/> read off the struct/union
    /// that <paramref name="baseExpr"/>'s type names (pointer levels peeled), or
    /// <see cref="CType.Int"/> when unknown (e.g. an as-yet-unregistered struct).</summary>
    private CType MemberType(CExpr baseExpr, string field)
    {
        var t = baseExpr.Type;
        while (t is CType.Pointer p) { t = p.Pointee; }
        if (t is CType.Named n && _structFields.TryGetValue(n.Name, out var fields))
        {
            foreach (var f in fields) { if (f.Name == field) { return f.Type; } }
        }
        return CType.Int;
    }

    /// <summary>Build a function-pointer type <c>Ret (*)(params)</c> →
    /// <see cref="CType.Func"/> (codegen lowers it to <c>delegate*&lt;params, Ret&gt;</c>).
    /// A lone <c>void</c> parameter list means no parameters.</summary>
    /// <summary>The fn-ptr type of a raw declarator <c>Ret (*name…)FnPtrTail</c>.</summary>
    private CType.Func FnPtrTailType(Item retItem, Item tailItem) => FnPtrTailType(ResolveType(retItem), tailItem);

    /// <summary>The fn-ptr type of a raw declarator over an already-resolved return
    /// type (a DeclItem's return type is the list's base / star-wrapped element).</summary>
    private CType.Func FnPtrTailType(CType ret, Item tailItem) => tailItem.Content switch
    {
        C.FnPtrTail t => FnPtrType(ret, t.Arg1),
        C.FnPtrTailNoArgs => FnPtrType(ret, null),
        _ => throw new IrUnsupportedException(TypeName(tailItem.Content)),
    };

    /// <summary>Whether a <c>FnPtrQuals</c> run (the qualifiers after a declarator's
    /// <c>*</c>) includes <c>const</c> (the pointer itself is then read-only).
    /// <c>volatile</c> / <c>restrict</c> have no C# model and are accepted.</summary>
    private static bool QualsHaveConst(Item quals) => quals.Content switch
    {
        C.FnPtrQualConst or C.FnPtrQualsConst => true,
        C.FnPtrQualVolatile or C.FnPtrQualRestrict => false,
        C.FnPtrQualsVolatile q => QualsHaveConst(q.Arg0),
        C.FnPtrQualsRestrict q => QualsHaveConst(q.Arg0),
        _ => throw new IrUnsupportedException(TypeName(quals.Content)),
    };

    /// <summary>A <c>FnPtrName</c> (<c>(*name)</c> / <c>(*quals name)</c> /
    /// <c>(**name)</c>) as the fn-ptr type over <paramref name="ret"/>
    /// (const-qualified for <c>*const</c>, a pointer to it for <c>**</c>).</summary>
    private (string Name, CType Type) FnPtrDeclarator(CType ret, Item nameItem, Item tailItem)
    {
        CType type = FnPtrTailType(ret, tailItem);
        return nameItem.Content switch
        {
            C.FnPtrName n => (Tok(n.Arg2), type),
            C.FnPtrNameQual n => (Tok(n.Arg3), QualsHaveConst(n.Arg2) ? type.WithQuals(TypeQual.Const) : type),
            // `(**name)`: a pointer to the function pointer.
            C.FnPtrNamePtr n => (Tok(n.Arg3), new CType.Pointer(type)),
            _ => throw new IrUnsupportedException(TypeName(nameItem.Content)),
        };
    }

    /// <summary>A fn-ptr array declarator (<c>FnPtrArr</c> / <c>FnPtrArrOpen</c>) over the
    /// return type and tail: its name, its dimensions (null for <c>[]</c>), and the
    /// element type (const-qualified for <c>*const</c>).</summary>
    private (Item Name, Item? Dims, CType Elem) FnPtrArrParts(CType ret, Item arrItem, Item tailItem)
    {
        CType elem = FnPtrTailType(ret, tailItem);
        CType Q(Item quals) => QualsHaveConst(quals) ? elem.WithQuals(TypeQual.Const) : elem;
        return arrItem.Content switch
        {
            C.FnPtrArr a => (a.Arg2, a.Arg3, elem),
            C.FnPtrArrQual a => (a.Arg3, a.Arg4, Q(a.Arg2)),
            C.FnPtrArrOpen a => (a.Arg2, null, elem),
            C.FnPtrArrOpenQual a => (a.Arg3, null, Q(a.Arg2)),
            _ => throw new IrUnsupportedException(TypeName(arrItem.Content)),
        };
    }

    /// <summary>C's dlsym idiom, <c>int (*fn)(int) = (int(*)(int))dlsym(h, "add");</c>:
    /// a native-convention initializer (<c>CType.Func.IsNativeCallConv</c>, set at the
    /// cast of a dlsym call) makes the declared fn-ptr native too, so calls through it
    /// use <c>delegate* unmanaged[Cdecl]</c>. Calls render unchanged; C# picks the calli
    /// convention from the variable's type.</summary>
    private static void PropagateNativeCallConv(Symbol sym, CExpr? init)
    {
        if (init?.Type is CType.Func { IsNativeCallConv: true } && sym.Type is CType.Func { IsNativeCallConv: false } f)
        {
            sym.Type = f with { IsNativeCallConv = true };
        }
    }

    private CType.Func FnPtrType(Item retItem, Item? paramListItem) => FnPtrType(ResolveType(retItem), paramListItem);

    private CType.Func FnPtrType(CType ret, Item? paramListItem)
    {
        var ptypes = new List<CType>();
        var variadic = false;
        if (paramListItem is { } pl)
        {
            var ps = BuildParams(pl, out variadic);
            if (!(ps.Count == 1 && ps[0].Type is CType.VoidType))
            {
                foreach (var p in ps) { ptypes.Add(p.Type); }
            }
        }
        return new CType.Func(ret, ptypes, variadic);
    }

    // ---- types -----------------------------------------------------------

    private CType ResolveType(Item it) => it.Content switch
    {
        C.TypeFromSpec t => ResolveSpecs(CollectSpecs(t.Arg0), SrcPos.From(it)),
        C.TypePtr t => PointerTo(ResolveType(t.Arg0)),
        // `int * const p` — the POINTER is const (can't repoint); the pointee is
        // unchanged. Flag the Pointer so `p = q` trips the const check while
        // `*p = v` (pointee write) does not. `* volatile` / `* restrict` have no
        // C# model, so they stay plain pointers (dropped).
        C.TypePtrQualConst t => PointerTo(ResolveType(t.Arg0)).WithQuals(TypeQual.Const),
        C.TypePtrQualVolatile t => PointerTo(ResolveType(t.Arg0)),
        // `const T` / `T const` — leading or trailing const qualifier. Carries the
        // flag on the type; drives the const-correctness check + read-only-array RVA.
        C.TypeConstPre t => ResolveType(t.Arg1).WithQuals(TypeQual.Const),
        C.TypeConstPost t => ResolveType(t.Arg0).WithQuals(TypeQual.Const),
        // `volatile T` / `T volatile` — leading or trailing qualifier prefix. Carry
        // the flag; codegen fences reads/writes of a volatile scalar lvalue
        // (Volatile.Read/Write).
        C.TypeVolatile t => ResolveType(t.Arg1).WithQuals(TypeQual.Volatile),
        C.TypeVolatilePost t => ResolveType(t.Arg0).WithQuals(TypeQual.Volatile),
        // `_Atomic T` / `_Atomic(T)` (C11). Codegen lowers reads/writes of an atomic
        // scalar lvalue to seq-cst Atomic.Load/Store/*Fetch (Interlocked-backed).
        C.TypeAtomic t => AtomicType(ResolveType(t.Arg1), it),
        C.TypeAtomicParen t => AtomicType(TypeNameType(t.Arg2, TypeNameSite.SpecifierQualifierList), it),
        // `_Alignas(Type) T` / `_Alignas(constexpr) T` (C11 §6.7.5) — the
        // alignment specifier is ACCEPTED + IGNORED (a C# field/local has no
        // controllable alignment; same no-op treatment as Zig's `align(N)`).
        // Gated C11; the operand is validated but never read.
        C.TypeAlignasType t => AlignasType(t.Arg2, t.Arg4, it),
        C.TypeAlignasExpr t => AlignasExpr(t.Arg2, t.Arg4, it),
        C.TypePtrQualRestrict t => PointerTo(ResolveType(t.Arg0)),
        C.TypeName t => ResolveTypeName(Tok(t.Arg0)),
        // `enum Tag` as a type — the registered real C# enum, or plain int if the
        // tag is unknown (forward/opaque) or names an anonymous int-constant enum.
        C.TypeEnum te => _enumTypes.TryGetValue(Tok(te.Arg1), out var et) ? et : CType.Int,
        // `struct Tag` / `union Tag` as a type — the canonical C# struct name.
        C.TypeStruct t => TagReference(t.Arg1),
        C.TypeUnion t => TagReference(t.Arg1),
        // Tag definitions (C11 6.7.2.1 / 6.7.2.2): the type is defined once per
        // occurrence and the declaration goes on with it (`static const struct X
        // { … } t[] = …;`, `union { int i; float f; } u;`, `typedef enum { … } Mode;`).
        C.TypeStructDef t => DefineAggregate(it, Tok(t.Arg1), t.Arg3, isUnion: false),
        C.TypeStructAnonDef t => DefineAggregate(it, null, t.Arg2, isUnion: false),
        C.TypeUnionDef t => DefineAggregate(it, Tok(t.Arg1), t.Arg3, isUnion: true),
        C.TypeUnionAnonDef t => DefineAggregate(it, null, t.Arg2, isUnion: true),
        C.TypeEnumDef t => DefineEnum(it, Tok(t.Arg1), null, t.Arg3),
        C.TypeEnumAnonDef t => DefineEnum(it, null, null, t.Arg2),
        C.TypeEnumDefTyped t => DefineEnum(it, Tok(t.Arg1), t.Arg3, t.Arg5),
        // Storage classes and function specifiers, a Type prefix or suffix (C11
        // 6.7.1, 6.7.4): dropped from the type. The declaration reads its storage
        // class structurally (DeclSpecsOf); the ones with a lowering of their own
        // are recorded for the enclosing declaration (RecordDeclSpec).
        C.TypeDeclSpec t => DeclSpec(t.Arg0, t.Arg1),
        C.TypeDeclSpecPost t => DeclSpec(t.Arg1, t.Arg0),
        // C23 `typeof(expr)` / `typeof(type)` — the expr form reads the operand's
        // synthesized CType (qualifiers dropped, as `typeof_unqual` does); the type
        // form unwraps to that type. The expr isn't evaluated (only its type taken).
        C.TypeofExpr t => BuildExpr(t.Arg2).Type.Unqualified,
        C.TypeofType t => TypeNameType(t.Arg2, TypeNameSite.Typeof),
        // A function-pointer type `Ret (*)(params)` — abstract declarator (a cast
        // target, a typedef target, a sizeof operand): lowers to CType.Func.
        C.TypeFnPtr t => FnPtrType(t.Arg0, t.Arg5),
        C.TypeFnPtrNoArgs t => FnPtrType(t.Arg0, null),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    private int _anonAggrSeq;

    /// <summary>A synthesized <c>__AnonN</c> struct or union for an anonymous
    /// aggregate definition. Each definition is a distinct type, and
    /// <see cref="DefineAggregate"/> calls this once per definition (its cache is
    /// keyed by the Type item), so the field that holds it and every member access
    /// agree on one struct. A source position is no key: two headers can hold
    /// anonymous definitions at the same line and column.</summary>
    private CType ResolveAnonAggregate(Item memberListItem, bool isUnion)
    {
        var name = AnonAggregateName(memberListItem, isUnion);
        if (!_structFields.ContainsKey(name))
        {
            var fields = BuildStructFields(memberListItem, name);
            _structFields[name] = fields;
            _structIsUnion[name] = isUnion;
            Types.Add(new StructTypeDef(name, fields, isUnion));
        }
        return new CType.Named(name);
    }

    /// <summary>`struct Tag` / `union Tag` as a type: the canonical C# struct name. A tag the
    /// program's own sources name is recorded (<see cref="IrModule.DeclaredTags"/>), so one it
    /// never completes still gets a type; one a synthetic header names is the runtime's
    /// (<see cref="IrModule.RuntimeTags"/>).</summary>
    private CType TagReference(Item tag)
    {
        var name = Tok(tag);
        (tag.Position.Line < SrcPos.SyntheticLineBase ? Module.DeclaredTags : Module.RuntimeTags).Add(name);
        return new CType.Named(name);
    }

    /// <summary>Resolve a type-name token: a user/library typedef resolves to its
    /// underlying type; an unknown name (a predefined opaque type like
    /// <c>FILE</c> / <c>jmp_buf</c>'s target) stays a <see cref="CType.Named"/>
    /// whose spelling the backend emits verbatim.</summary>
    private CType ResolveTypeName(string name) =>
        _symbols.Resolve(name) is { Kind: SymKind.Typedef } local ? local.Type
        : _typedefs.TryGetValue(name, out var t) ? t : new CType.Named(name);

    /// <summary>Resolve a specifier-prefixed or -suffixed type (<c>static T</c>,
    /// <c>T inline</c>, …): the type is <paramref name="inner"/>'s. A specifier with a
    /// lowering of its own is recorded for the enclosing declaration; the storage
    /// classes are read structurally from the declaration's Type instead.</summary>
    private CType DeclSpec(Item spec, Item inner)
    {
        var kw = SpecOf(spec);
        if (kw is SpecKw.Inline or SpecKw.Noreturn or SpecKw.ThreadLocal or SpecKw.Constexpr)
        {
            RecordDeclSpec(Spelling(kw), SrcPos.From(spec));
        }
        return ResolveType(inner);
    }

    /// <summary>A storage-class specifier (C11 6.7.1, C23 <c>constexpr</c>) or function
    /// specifier (6.7.4).</summary>
    private enum SpecKw { Typedef, Extern, Static, Auto, Register, ThreadLocal, Constexpr, Inline, Noreturn }

    /// <summary>The specifier a <c>DeclSpec</c> item spells.</summary>
    private static SpecKw SpecOf(Item spec) => spec.Content switch
    {
        C.SpecTypedef => SpecKw.Typedef,
        C.SpecExtern => SpecKw.Extern,
        C.SpecStatic => SpecKw.Static,
        C.SpecAuto => SpecKw.Auto,
        C.SpecRegister => SpecKw.Register,
        C.SpecThreadLocal => SpecKw.ThreadLocal,
        C.SpecConstexpr => SpecKw.Constexpr,
        C.SpecInline => SpecKw.Inline,
        C.SpecNoreturn => SpecKw.Noreturn,
        _ => throw new IrUnsupportedException(TypeName(spec.Content)),
    };

    /// <summary>A specifier's keyword, as diagnostics name it.</summary>
    private static string Spelling(SpecKw kw) => kw switch
    {
        SpecKw.Typedef => "typedef",
        SpecKw.Extern => "extern",
        SpecKw.Static => "static",
        SpecKw.Auto => "auto",
        SpecKw.Register => "register",
        SpecKw.ThreadLocal => "_Thread_local",
        SpecKw.Constexpr => "constexpr",
        SpecKw.Inline => "inline",
        _ => "_Noreturn",
    };

    /// <summary>Whether a specifier is one of the storage classes of which a
    /// declaration takes at most one (C11 6.7.1p2; <c>_Thread_local</c> may join
    /// <c>static</c> or <c>extern</c>, and C23's <c>constexpr</c> has its own rules).</summary>
    private static bool IsStorageClass(SpecKw kw) =>
        kw is SpecKw.Typedef or SpecKw.Extern or SpecKw.Static or SpecKw.Auto or SpecKw.Register;

    /// <summary>One specifier on a declaration's Type spine. <c>AfterPointer</c>: a
    /// <c>*</c> precedes it (<c>int *static p</c>), which C's grammar does not allow
    /// (the qualifiers of a pointer are its only specifiers).</summary>
    private readonly record struct SpecSite(SpecKw Kw, Item At, bool AfterPointer);

    /// <summary>The storage-class and function specifiers of a declaration's Type, in
    /// source order, read structurally: each is a DeclSpec prefix or suffix anywhere in
    /// the qualifier chain, and since the Type group reduces a prefix before a
    /// following <c>*</c>, a declaration's specifiers sit below its pointers
    /// (<c>static int *p</c> is <c>(static int) *</c>). An abstract function-pointer
    /// type carries its return type's (<c>static int (*)(void)</c>).</summary>
    private static List<SpecSite> DeclSpecsOf(Item typeItem)
    {
        var acc = new List<SpecSite>();
        void MarkAfterPointer()
        {
            for (var i = 0; i < acc.Count; i++) { acc[i] = acc[i] with { AfterPointer = true }; }
        }
        var it = typeItem;
        while (true)
        {
            switch (it.Content)
            {
                case C.TypeDeclSpec t: acc.Add(new(SpecOf(t.Arg0), t.Arg0, false)); it = t.Arg1; break;
                case C.TypeDeclSpecPost t: acc.Add(new(SpecOf(t.Arg1), t.Arg1, false)); it = t.Arg0; break;
                case C.TypePtr t: MarkAfterPointer(); it = t.Arg0; break;
                case C.TypePtrQualConst t: MarkAfterPointer(); it = t.Arg0; break;
                case C.TypePtrQualVolatile t: MarkAfterPointer(); it = t.Arg0; break;
                case C.TypePtrQualRestrict t: MarkAfterPointer(); it = t.Arg0; break;
                case C.TypeConstPost t: it = t.Arg0; break;
                case C.TypeVolatilePost t: it = t.Arg0; break;
                case C.TypeConstPre t: it = t.Arg1; break;
                case C.TypeVolatile t: it = t.Arg1; break;
                case C.TypeAtomic t: it = t.Arg1; break;
                case C.TypeAlignasType t: it = t.Arg4; break;
                case C.TypeAlignasExpr t: it = t.Arg4; break;
                case C.TypeFnPtr t: it = t.Arg0; break;
                case C.TypeFnPtrNoArgs t: it = t.Arg0; break;
                default:
                    acc.Sort((a, b) => SrcPos.From(a.At).Line != SrcPos.From(b.At).Line
                        ? SrcPos.From(a.At).Line.CompareTo(SrcPos.From(b.At).Line)
                        : SrcPos.From(a.At).Column.CompareTo(SrcPos.From(b.At).Column));
                    return acc;
            }
        }
    }

    /// <summary>The storage class a declaration's Type carries, or null for none (the
    /// first, when there are several, which <see cref="CheckDeclSpecs(Item)"/> rejects).</summary>
    private static SpecKw? StorageClassOf(Item typeItem)
    {
        foreach (var s in DeclSpecsOf(typeItem))
        {
            if (IsStorageClass(s.Kw)) { return s.Kw; }
        }
        return null;
    }

    /// <summary>Whether a declaration's Type carries the <c>register</c> storage class.</summary>
    private static bool DeclaresRegister(Item typeItem) => DeclSpecsOf(typeItem).Exists(s => s.Kw == SpecKw.Register);

    /// <summary>Whether a declaration's Type carries the <c>static</c> storage class
    /// (internal linkage for a function).</summary>
    private static bool DeclaresStatic(Item typeItem) => DeclSpecsOf(typeItem).Exists(s => s.Kw == SpecKw.Static);

    /// <summary>The first storage-class or function specifier of a Type in source
    /// order, if any.</summary>
    private static SpecSite? FirstDeclSpec(Item typeItem) =>
        DeclSpecsOf(typeItem) is [var first, ..] ? first : null;

    /// <summary><see cref="CheckDeclSpecs(Item, out bool)"/> when the caller needs
    /// only the storage class.</summary>
    private SpecKw? CheckDeclSpecs(Item typeItem) => CheckDeclSpecs(typeItem, out _);

    /// <summary>Check a declaration's specifiers the way gcc does and return its storage
    /// class (null for none, or when <paramref name="multiple"/>): no specifier after a
    /// <c>*</c>, no storage class twice (a function specifier may repeat, C11 6.7.4),
    /// and at most one storage class, <c>_Thread_local</c> aside, which may join
    /// <c>static</c> or <c>extern</c> (C11 6.7.1p2; gcc names that pairing).</summary>
    private SpecKw? CheckDeclSpecs(Item typeItem, out bool multiple)
    {
        SpecKw? storage = null;
        var seen = new HashSet<SpecKw>();
        var threadLocal = false;
        multiple = false;
        foreach (var s in DeclSpecsOf(typeItem))
        {
            if (s.AfterPointer)
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    $"expected identifier or '(' before '{Spelling(s.Kw)}'", SrcPos.From(s.At), _file));
                continue;
            }
            if (!seen.Add(s.Kw))
            {
                if (s.Kw is not (SpecKw.Inline or SpecKw.Noreturn))
                {
                    Diagnostics.Add(new Diagnostic(Severity.Error, $"duplicate '{Spelling(s.Kw)}'", SrcPos.From(s.At), _file));
                }
                continue;
            }
            if (s.Kw == SpecKw.ThreadLocal) { threadLocal = true; }
            else if (IsStorageClass(s.Kw))
            {
                if (storage is null) { storage = s.Kw; }
                else { multiple = true; }
            }
        }
        if (threadLocal && storage is { } other and (SpecKw.Typedef or SpecKw.Auto or SpecKw.Register))
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                $"'_Thread_local' used with '{Spelling(other)}'", SrcPos.From(typeItem), _file));
            return null;
        }
        if (multiple)
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                "multiple storage classes in declaration specifiers", SrcPos.From(typeItem), _file));
            return null;
        }
        return storage;
    }

    /// <summary>A parameter's specifiers (C11 6.7.6.3p2): <c>register</c> is its only
    /// storage class, and a function specifier is meaningless there; gcc's error and
    /// warning.</summary>
    private void CheckParamSpecs(Item typeItem, string? name)
    {
        CheckDeclSpecs(typeItem, out var multiple);
        var reported = multiple;
        foreach (var s in DeclSpecsOf(typeItem))
        {
            if (s.Kw is SpecKw.Inline or SpecKw.Noreturn)
            {
                Diagnostics.Add(new Diagnostic(Severity.Warning,
                    $"{(name is null ? "unnamed parameter" : $"parameter '{name}'")} declared '{Spelling(s.Kw)}'", SrcPos.From(s.At), _file));
            }
            else if (s.Kw != SpecKw.Register && !reported)
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    name is null ? "storage class specified for unnamed parameter" : $"storage class specified for parameter '{name}'",
                    SrcPos.From(s.At), _file));
                reported = true;
            }
        }
    }

    /// <summary>A member declaration takes a specifier-qualifier list (C11 6.7.2.1p1):
    /// a storage class or function specifier there is gcc's parse error.</summary>
    private void CheckMemberSpecs(Item typeItem)
    {
        if (FirstDeclSpec(typeItem) is { } s)
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                $"expected specifier-qualifier-list before '{Spelling(s.Kw)}'", SrcPos.From(s.At), _file));
        }
    }

    /// <summary>Where a type name (C11 6.7.7) appears, for gcc's wording when it
    /// carries a storage class or function specifier.</summary>
    private enum TypeNameSite { Cast, Sizeof, Alignof, Typeof, SpecifierQualifierList }

    /// <summary>Resolve a type name (C11 6.7.7): it takes a specifier-qualifier list, so
    /// a storage class or function specifier in it is gcc's error for the
    /// <paramref name="site"/> (a storage class in a cast, <c>sizeof</c> or
    /// <c>_Alignof</c> is named as such; anything else is a parse error there).</summary>
    private CType TypeNameType(Item typeItem, TypeNameSite site)
    {
        if (FirstDeclSpec(typeItem) is { } s)
        {
            var kw = Spelling(s.Kw);
            var fnSpec = s.Kw is SpecKw.Inline or SpecKw.Noreturn;
            var msg = site switch
            {
                TypeNameSite.Cast when !fnSpec => "storage class specifier in cast",
                TypeNameSite.Sizeof when !fnSpec => "storage class specifier in 'sizeof'",
                TypeNameSite.Alignof when !fnSpec => "storage class specifier in '_Alignof'",
                TypeNameSite.SpecifierQualifierList => $"expected specifier-qualifier-list before '{kw}'",
                _ => $"expected expression before '{kw}'",
            };
            Diagnostics.Add(new Diagnostic(Severity.Error, msg, SrcPos.From(s.At), _file));
        }
        return ResolveType(typeItem);
    }

    /// <summary>A function declared with a storage class it cannot have
    /// (<c>register</c> anywhere, <c>static</c> at block scope), gcc's wording.</summary>
    private void InvalidFunctionStorage(Declarator d)
        => Diagnostics.Add(new Diagnostic(Severity.Error,
            $"invalid storage class for function '{d.Name}'", SrcPos.From(d.At), _file));

    /// <summary>The Type item of a function definition's signature.</summary>
    private static Item FnSigType(Item fnSig) => fnSig.Content switch
    {
        C.FnSig n => n.Arg0,
        C.FnSigNoArgs n => n.Arg0,
        C.FnSigParen n => n.Arg0,
        C.FnSigParenNoArgs n => n.Arg0,
        C.FnSigRetFnPtr n => n.Arg0,
        C.FnSigRetFnPtrNoArgs n => n.Arg0,
        _ => throw new IrUnsupportedException(TypeName(fnSig.Content)),
    };

    private List<string> CollectSpecs(Item it)
    {
        var acc = new List<string>();
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.TypeSpecListCons c: Walk(c.Arg0); Walk(c.Arg1); break;
                case C.TypeSpecListOne o: Walk(o.Arg0); break;
                default: acc.Add(SpecKeyword(node.Content)); break;
            }
        }
        Walk(it);
        return acc;
    }

    private static string SpecKeyword(object spec) => spec switch
    {
        C.TsInt => "int",
        C.TsChar => "char",
        C.TsFloat => "float",
        C.TsDouble => "double",
        C.TsVoid => "void",
        C.TsShort => "short",
        C.TsLong => "long",
        C.TsUnsigned => "unsigned",
        C.TsSigned => "signed",
        C.TsBool => "_Bool",
        C.TsFloat128 => "Float128",
        C.TsInt128 => "__int128",
        C.TsComplex => "_Complex",
        _ => throw new IrUnsupportedException(TypeName(spec)),
    };

    /// <summary>Resolve a declaration-specifier multiset to a <see cref="CType"/>.
    /// Order-insensitive; covers the slice (int/char/short/long/long long with
    /// signedness, float/double/long double, void, _Bool). Qualifiers
    /// (<c>const</c>/<c>volatile</c>) are NOT specifiers — they're Type prefix/
    /// postfix productions that flag the resolved <see cref="CType"/>.</summary>
    /// <summary>Apply the <c>_Atomic</c> qualifier (C11), flagging it under an
    /// older -std=.</summary>
    private CType AtomicType(CType inner, Item it)
    {
        Gate(2011, "_Atomic", it);
        return inner.WithQuals(TypeQual.Atomic);
    }

    /// <summary>`_Alignof(Type)` (C11 §6.5.3.4) — fold to the layout model's ABI
    /// alignment as a <c>size_t</c>-typed literal (an integer constant expression,
    /// so it flows into <c>_Static_assert</c> / array bounds / case labels like an
    /// enum constant). Gated C11.</summary>
    private CExpr FoldAlignof(C.AlignofType a, Item it)
    {
        Gate(2011, "_Alignof", it);
        var align = AlignOfConst(TypeNameType(a.Arg2, TypeNameSite.Alignof));
        return new LitInt(align.ToString(System.Globalization.CultureInfo.InvariantCulture), align) { Type = CType.SizeT };
    }

    /// <summary>C11 §6.5.1.1 `_Generic(ctrl, T1: e1, …, default: eD)` — type-generic
    /// selection, resolved entirely at lowering time (exactly as a native C compiler
    /// does): the controlling expression is built ONLY for its synthesized type —
    /// C says it is NOT evaluated, so its IR is discarded — the type undergoes
    /// lvalue conversion (<see cref="LvalueConvert"/>), and the single compatible
    /// association's expression is lowered in its place. There is no _Generic IR
    /// node; the selection IS the selected arm (so an ICE arm composes with
    /// `_Static_assert`/ConstEval for free). Constraint violations — no compatible
    /// association without a `default`, duplicate compatible association types,
    /// duplicate `default` — are collected diagnostics (gcc-shaped wording).
    /// Compatibility is structural CType equality on unqualified association types;
    /// an enum-typed controlling expression falls back to its integer backing when
    /// no association names the enum itself (C leaves the compatible integer type
    /// implementation-defined; dotcc's enums are int-backed).</summary>
    private CExpr BuildGenericSelect(C.GenericSelect g, Item it)
    {
        Gate(2011, "_Generic", it);
        // Building the controlling expr may touch builder side-state (AddressTaken,
        // referenced-function tracking) even though the node is discarded —
        // semantically harmless (worst case a pessimized nint global).
        var ctrl = LvalueConvert(BuildExpr(g.Arg2).Type);

        // Collect associations in source order.
        var typed = new List<(CType Type, Item Expr, Item Assoc)>();
        Item? defaultArm = null;
        void Add(Item assoc)
        {
            switch (assoc.Content)
            {
                case C.GenericAssocType a:
                    typed.Add((TypeNameType(a.Arg0, TypeNameSite.SpecifierQualifierList).Unqualified, a.Arg2, assoc));
                    break;
                case C.GenericAssocDefault a:
                    if (defaultArm is not null)
                    {
                        Diagnostics.Add(new Diagnostic(Severity.Error,
                            "duplicate 'default' case in '_Generic'", SrcPos.From(assoc), _file));
                    }
                    defaultArm = a.Arg2;
                    break;
            }
        }
        void Walk(Item node)
        {
            if (node.Content is C.GenericAssocCons c) { Walk(c.Arg0); Add(c.Arg2); }
            else { Add(node); }
        }
        Walk(g.Arg4);

        // §6.5.1.1p2: no two associations may specify compatible types. With
        // structural compatibility that's plain CType equality.
        for (var i = 0; i < typed.Count; i++)
        {
            for (var j = i + 1; j < typed.Count; j++)
            {
                if (typed[i].Type.Equals(typed[j].Type))
                {
                    // gcc-verbatim (it names no type here either).
                    Diagnostics.Add(new Diagnostic(Severity.Error,
                        "'_Generic' specifies two compatible types",
                        SrcPos.From(typed[j].Assoc), _file));
                }
            }
        }

        foreach (var (t, expr, _) in typed)
        {
            if (ctrl.Equals(t)) { return BuildExpr(expr); }
        }
        // Enum fallback: `_Generic(color, int: …)` — the enum's backing integer.
        if (ctrl is CType.Enum en)
        {
            foreach (var (t, expr, _) in typed)
            {
                if (en.Underlying.Unqualified.Equals(t)) { return BuildExpr(expr); }
            }
        }
        if (defaultArm is not null) { return BuildExpr(defaultArm); }
        Diagnostics.Add(new Diagnostic(Severity.Error,
            $"'_Generic' selector of type '{ctrl.Describe()}' is not compatible with any association",
            SrcPos.From(it), _file));
        return new LitInt("0", 0) { Type = CType.Int }; // error recovery — compile still fails on the diagnostic
    }

    /// <summary>C11 §6.3.2.1 lvalue conversion, as `_Generic` applies it to the
    /// controlling expression's type (C17 semantics, matching gcc/clang): the
    /// top-level qualifiers drop, an array of T decays to pointer-to-T (element
    /// qualifiers survive — they are part of the pointee type). A function
    /// designator needs no wrapping: dotcc's <see cref="CType.Func"/> already IS
    /// the function-pointer type.</summary>
    private static CType LvalueConvert(CType t) => t.Unqualified switch
    {
        CType.Array a => new CType.Pointer(a.Element),
        var u => u,
    };

    /// <summary>`_Alignas(Type) T` — the align-as-type form (C11 §6.7.5). The
    /// specifier is a NO-OP on the managed target (a C# field/local has no
    /// controllable alignment), but the constraint still holds: the requested
    /// alignment (the operand type's natural alignment) must not be less strict
    /// than the declared type's own (§6.7.5p4). Gated C11.</summary>
    private CType AlignasType(Item operandType, Item inner, Item it)
    {
        Gate(2011, "_Alignas", it);
        var t = ResolveType(inner);
        CheckAlignasStrictEnough(AlignOfConst(TypeNameType(operandType, TypeNameSite.SpecifierQualifierList)), t, it);
        return t;
    }

    /// <summary>`_Alignas(constexpr) T` — the integer form (C11 §6.7.5). Accepted
    /// and ignored when valid; a non-constant, non-power-of-2, or weaker-than-
    /// natural alignment is a constraint violation (gcc rejects all three).
    /// <c>_Alignas(0)</c> is C11's explicit no-effect case. Gated C11.</summary>
    private CType AlignasExpr(Item alignExpr, Item inner, Item it)
    {
        Gate(2011, "_Alignas", it);
        var t = ResolveType(inner);
        if (ConstEval(BuildExpr(alignExpr)) is not { } align)
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                "requested alignment is not an integer constant", SrcPos.From(it), _file));
        }
        else if (align != 0)   // `_Alignas(0)` has no effect (§6.7.5p6)
        {
            if (align < 0 || (align & (align - 1)) != 0)
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    "requested alignment is not a positive power of 2", SrcPos.From(it), _file));
            }
            else
            {
                CheckAlignasStrictEnough(align, t, it);
            }
        }
        return t;
    }

    /// <summary>C11 §6.7.5p4: an alignment specifier shall not request an alignment
    /// LESS strict than the declared type's natural one (dotcc can't honor a
    /// stricter one either, but over-alignment is at worst a missed optimization —
    /// under-alignment would change the program's meaning, so it stays an error).</summary>
    private void CheckAlignasStrictEnough(long requested, CType declared, Item it)
    {
        if (requested < AlignOfConst(declared))
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                $"_Alignas alignment {requested} is less strict than the type's natural alignment", SrcPos.From(it), _file));
        }
    }

    private CType ResolveSpecs(List<string> specs, SrcPos pos)
    {
        int u = 0, s = 0, sh = 0, lng = 0, baseCount = 0;
        var quals = TypeQual.None;
        var isComplex = false;
        string? base_ = null;
        foreach (var k in specs)
        {
            switch (k)
            {
                case "unsigned": u++; break;
                case "signed": s++; break;
                case "short": sh++; break;
                case "long": lng++; break;
                // C99 _Complex — every width widens to the double-backed Complex.
                case "_Complex": isComplex = true; break;
                case "inline" or "_Noreturn" or "_Thread_local" or "constexpr": break; // ignored for type purposes here
                case "void": base_ = "void"; baseCount++; break;
                case "char": base_ = "char"; baseCount++; break;
                case "int": base_ = "int"; baseCount++; break;
                case "float": base_ = "float"; baseCount++; break;
                case "double": base_ = "double"; baseCount++; break;
                case "_Bool": base_ = "_Bool"; baseCount++; break;
                case "Float128": base_ = "Float128"; baseCount++; break;
                case "__int128": base_ = "__int128"; baseCount++; break;
            }
        }

        // Reject the ill-formed specifier multisets the C standard forbids (the
        // legacy emitter diagnosed these; the messages match its/clang's intent).
        // A purely permissive resolver would silently mis-type `long long long` or
        // `short double`. Order matters only for which message a multi-error combo
        // reports first.
        if (u >= 1 && s >= 1) { throw new DotCC.CompileException("cannot combine `signed` and `unsigned`"); }
        if (u >= 2) { throw new DotCC.CompileException("duplicate `unsigned`"); }
        if (s >= 2) { throw new DotCC.CompileException("duplicate `signed`"); }
        if (sh >= 1 && lng >= 1) { throw new DotCC.CompileException("cannot combine `short` and `long`"); }
        if (sh >= 2) { throw new DotCC.CompileException("duplicate `short`"); }
        if (lng >= 3) { throw new DotCC.CompileException("more than two `long` in a type"); }
        if (baseCount >= 2) { throw new DotCC.CompileException("multiple base types in a declaration"); }
        if (base_ == "_Bool" && (u > 0 || s > 0 || sh > 0 || lng > 0 || isComplex))
        { throw new DotCC.CompileException("`_Bool` cannot be combined with other type specifiers"); }
        if (base_ == "Float128" && (u > 0 || s > 0 || sh > 0 || lng > 0 || isComplex))
        { throw new DotCC.CompileException("`_Float128` cannot be combined with other type specifiers"); }
        // `__int128` takes signed/unsigned (like `int`) but no size or `_Complex` modifier.
        if (base_ == "__int128" && (sh > 0 || lng > 0 || isComplex))
        { throw new DotCC.CompileException("`__int128` cannot be combined with `short`, `long`, or `_Complex`"); }
        if (base_ == "float" && (u > 0 || s > 0 || sh > 0 || lng > 0))
        { throw new DotCC.CompileException("`float` cannot take size or sign modifiers"); }
        if (base_ == "double" && lng >= 2)
        { throw new DotCC.CompileException("`long long double` is not a valid type"); }
        if (base_ == "double" && (u > 0 || s > 0 || sh > 0))
        { throw new DotCC.CompileException("`double` cannot take sign or `short` modifiers"); }
        if (isComplex && base_ is not ("float" or "double"))
        { throw new DotCC.CompileException("`_Complex` requires a `float`, `double`, or `long double` base"); }

        // Dialect gates: type-spec features newer than the selected -std=.
        if (base_ == "_Bool") { Gate(1999, "_Bool", pos); }
        if (lng >= 2) { Gate(1999, "long long", pos); }
        if (isComplex) { Gate(1999, "_Complex", pos); }

        CType t = base_ switch
        {
            "void" => CType.Void,
            "_Bool" => CType.Bool,
            "Float128" => CType.Float128,
            "__int128" => u > 0 ? CType.UInt128 : CType.Int128,  // signed is the default
            "float" => CType.Float,
            "double" => lng >= 1 ? CType.LongDouble : CType.Double,
            "char" => u > 0 ? CType.UChar : s > 0 ? CType.SChar : CType.Char,
            _ => sh > 0 ? (u > 0 ? CType.UShort : CType.Short)
                 : lng >= 2 ? (u > 0 ? CType.ULongLong : CType.LongLong)
                 : lng == 1 ? (u > 0 ? CType.ULong : CType.Long)
                 : (u > 0 ? CType.UInt : CType.Int),
        };
        if (isComplex) { return CType.Complex.WithQuals(quals); }
        return t.WithQuals(quals);
    }

    // ---- statements ------------------------------------------------------

    private Block BuildBlock(Item it)
    {
        var stmts = new List<CStmt>();
        switch (it.Content)
        {
            case C.Block b:
                _symbols.EnterScope();
                // C90 requires all declarations to precede statements in a block; a
                // declaration after a statement ("mixed declarations") is C99+.
                var sawNonDecl = false;
                FlattenStmts(b.Arg2, x =>
                {
                    if (x.Content is C.StmtDecl) { if (sawNonDecl) { Gate(1999, "mixed declarations and code", x); } }
                    else { sawNonDecl = true; }
                    stmts.Add(BuildStmt(x));
                });
                // Desugar a value-capturing `T r = setjmp(env); …rest…` — the rest of THIS
                // block is the goto-restart body, so recognition is necessarily block-level.
                stmts = RewriteSetjmpCaptures(stmts);
                _symbols.ExitScope();
                break;
            case C.BlockEmpty:
                break;
            default:
                throw new IrUnsupportedException(TypeName(it.Content));
        }
        return new Block(stmts);
    }

    /// <summary>A statement that emits nothing (a hoisted local type definition,
    /// a bare <c>;</c>). An empty block is dropped wholesale by codegen.</summary>
    private static CStmt EmptyStmt(SrcPos pos) => new Block(System.Array.Empty<CStmt>()) { Pos = pos };

    private void FlattenStmts(Item it, Action<Item> onStmt)
    {
        // Iterative walk of the statement list (a large function body — Lua's
        // luaV_execute — would otherwise recurse one frame per statement).
        while (true)
        {
            switch (it.Content)
            {
                case C.StmtsCons c: onStmt(c.Arg0); it = c.Arg1; continue;
                case C.StmtsOne o: onStmt(o.Arg0); break;
                default: onStmt(it); break;
            }
            break;
        }
    }

    private CStmt BuildStmt(Item it)
    {
        var pos = SrcPos.From(it);
        switch (it.Content)
        {
            case C.Block: return BuildBlock(it);
            case C.BlockEmpty: return BuildBlock(it);
            case C.StmtDecl d: return BuildDeclStmt(d.Arg0) with { Pos = pos };
            // Block-scope declarations with no declarators (`struct cD { … };`
            // inside a function body). A type has no storage: dotcc hoists a tag
            // definition into the top-level type section (deduped by tag, exactly as
            // at file scope) and the statement emits nothing.
            case C.StmtTagDecl s: BuildTagDecl(s.Arg0); return EmptyStmt(pos);
            // Block-scope `_Static_assert(expr[, "msg"]);` — compile-time only,
            // evaluated exactly like the file-scope forms; a holding assertion
            // emits nothing. The message-less arity gates C23.
            case C.StaticAssertStmt s: Gate(2011, "_Static_assert", it); CheckStaticAssert(s.Arg2, s.Arg4, pos); return EmptyStmt(pos);
            case C.StaticAssertStmtNoMsg s: Gate(2023, "_Static_assert with no message", it); CheckStaticAssert(s.Arg2, null, pos); return EmptyStmt(pos);
            // C23 `[[attr]]` prepending a statement / block-scope declaration —
            // gate C23, then unwrap to the inner statement. Two recognized attrs
            // carry meaning here; every other shape is accepted + ignored:
            //   `[[fallthrough]];` arrives as a wrapped EMPTY statement and leaves a
            //     FallthroughMarker so BuildSwitch's -Wimplicit-fallthrough check
            //     sees the fall-through was intended.
            //   `[[maybe_unused]]` on a block-scope declaration rides the built
            //     DeclStmt (MaybeUnused=true) so the backend suppresses C#'s
            //     unused-local warning — the faithful lowering of "don't warn if
            //     unused". Only a sole DeclStmt is flagged (the common `int x;` /
            //     `int x = e;` case); a multi-kind split (Seq / ArrayDecl / Block)
            //     leaves the attribute inert (a documented V1 limit).
            case C.AttrStmt s:
                Gate(2023, "[[attributes]]", it);
                if (AttrListHasFallthrough(s.Arg1)) { return new FallthroughMarker { Pos = pos }; }
                var inner = BuildStmt(s.Arg4);
                if (inner is DeclStmt decl && AttrListHasMaybeUnused(s.Arg1)) { return decl with { MaybeUnused = true }; }
                return inner;
            case C.StmtExpr e:
            {
                var stmtExpr = BuildExpr(e.Arg0);
                CheckNodiscardDiscarded(stmtExpr, pos);
                return new ExprStmt(stmtExpr) { Pos = pos };
            }
            case C.StmtEmpty: return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
            case C.StmtIf s:
            {
                var cond = BuildExpr(s.Arg2);
                var then = BuildStmt(s.Arg4);
                return SetjmpGuardOf(cond, then, null, pos) ?? new If(cond, then, null) { Pos = pos };
            }
            case C.StmtIfElse s:
            {
                var cond = BuildExpr(s.Arg2);
                var then = BuildStmt(s.Arg4);
                var els = BuildStmt(s.Arg6);
                return SetjmpGuardOf(cond, then, els, pos) ?? new If(cond, then, els) { Pos = pos };
            }
            case C.StmtWhile s: return new While(BuildExpr(s.Arg2), BuildStmt(s.Arg4)) { Pos = pos };
            case C.StmtDoWhile s: return new DoWhile(BuildStmt(s.Arg1), BuildExpr(s.Arg4)) { Pos = pos };
            case C.StmtReturn s:
            {
                var rv = BuildExpr(s.Arg1);
                if (_currentRet is { } rt) { CheckQualifierDiscard(rv, rt, pos, "return"); }
                return new Return(rv) { Pos = pos };
            }
            case C.StmtReturnVoid: return new Return(null) { Pos = pos };
            case C.StmtBreak: return new Break { Pos = pos };
            case C.StmtContinue: return new Continue { Pos = pos };
            case C.StmtForDecl s: return BuildForDecl(s) with { Pos = pos };
            case C.StmtForExpr s:
                return new For(new ExprStmt(BuildCommaExpr(s.Arg2)), BuildForCond(s.Arg4), BuildForPost(s.Arg6), BuildStmt(s.Arg8)) { Pos = pos };
            case C.StmtForNoInit s:
                return new For(null, BuildForCond(s.Arg3), BuildForPost(s.Arg5), BuildStmt(s.Arg7)) { Pos = pos };
            case C.StmtSwitch s: return BuildSwitch(s, pos);
            // A case/default label NESTED in a switch body statement (Duff's device).
            // BuildSwitch handles the top-level ones; a nested one reaches here.
            case C.CaseLabel cl: return new CaseLabelStmt(BuildExpr(cl.Arg1), BuildStmt(cl.Arg3)) { Pos = pos };
            case C.DefaultLabel dl: return new CaseLabelStmt(null, BuildStmt(dl.Arg2)) { Pos = pos };
            case C.StmtGoto s: return new Goto(Tok(s.Arg1)) { Pos = pos };
            case C.StmtLabel s: return new Labeled(Tok(s.Arg0), BuildStmt(s.Arg2)) { Pos = pos };
            default: throw new IrUnsupportedException(TypeName(it.Content));
        }
    }

    /// <summary>Build a <see cref="Switch"/>. The grammar parses <c>case E:</c> /
    /// <c>default:</c> as statement-level labels whose body is the following
    /// statement (stacked labels nest: <c>case 0: case 1: stmt</c> is
    /// <c>CaseLabel(0, CaseLabel(1, stmt))</c>), so the body is a flat statement
    /// list with labels sprinkled in. Walk it, opening a new section whenever a
    /// label follows body statements and stacking consecutive labels into one
    /// section.</summary>
    private CStmt BuildSwitch(C.StmtSwitch s, SrcPos pos)
    {
        var subject = BuildExpr(s.Arg2);
        var raw = new List<Item>();
        if (s.Arg4.Content is C.Block b) { FlattenStmts(b.Arg2, raw.Add); }

        var sections = new List<SwitchSection>();
        var labels = new List<SwitchLabel>();
        var body = new List<CStmt>();
        var open = false;   // a section has been started (≥1 label seen)
        // goto labels fused onto a case label (`lbl: case 'Q': stmt` — legal C,
        // both name the same statement; chibi main.c). C# requires the label
        // AFTER the case labels, so they're deferred here and re-attached to the
        // section's first statement (the same program point).
        var pendingLabels = new List<string>();

        void Flush()
        {
            if (open) { sections.Add(new SwitchSection(labels, body)); }
            labels = new List<SwitchLabel>();
            body = new List<CStmt>();
            open = false;
        }

        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.CaseLabel cl:
                    if (body.Count > 0) { Flush(); }   // label after body → new section
                    open = true;
                    labels.Add(new SwitchLabel(BuildExpr(cl.Arg1)));
                    Walk(cl.Arg3);
                    break;
                case C.DefaultLabel dl:
                    if (body.Count > 0) { Flush(); }
                    open = true;
                    labels.Add(new SwitchLabel(null));
                    Walk(dl.Arg2);
                    break;
                case C.StmtLabel ls when LeadsToCase(ls.Arg2):
                    // Defer the goto label past the case labels it shares a
                    // statement with; the next plain statement picks it up.
                    pendingLabels.Add(Tok(ls.Arg0));
                    Walk(ls.Arg2);
                    break;
                default:
                    // A statement before the first case label is unreachable in C;
                    // drop it (C# would reject it anyway).
                    if (open)
                    {
                        var st = BuildStmt(it);
                        for (var i = pendingLabels.Count - 1; i >= 0; i--)
                        {
                            st = new Labeled(pendingLabels[i], st) { Pos = st.Pos };
                        }
                        pendingLabels.Clear();
                        body.Add(st);
                    }
                    break;
            }
        }

        _symbols.EnterScope();
        foreach (var it in raw) { Walk(it); }
        Flush();
        _symbols.ExitScope();
        if ((_warnings & WarningFlags.ImplicitFallthrough) != 0) { CheckImplicitFallthrough(sections); }
        // `switch (setjmp(env)) { … }` — a value-capturing setjmp whose region is exactly
        // this switch. Rewrite the subject to a synthetic capture var reset to 0 and wrap
        // the switch in a goto-restart SetjmpCapture (real C's "returns twice"), so a
        // `longjmp(env, v)` re-runs the switch with the matching case reached.
        if (IsSetjmpCall(subject, out var env, out var call))
        {
            _setjmpCalls.Remove(call);
            var sym = _symbols.Declare(new Symbol { Name = "__sjval" + _setjmpSeq, Kind = SymKind.Var, Type = CType.Int, Storage = Storage.Auto });
            var vref = new VarRef(sym) { Type = CType.Int, IsLValue = true };
            var decl = new DeclStmt(new[] { new LocalDecl(sym, ZeroLit()) }) { Pos = pos };
            var sw = new Switch(vref, sections) { Pos = pos };
            var cap = new SetjmpCapture(env, vref, sw, _setjmpSeq++) { Pos = pos };
            return new Block(new CStmt[] { decl, cap }) { Pos = pos };
        }
        return new Switch(subject, sections) { Pos = pos };
    }

    /// <summary>gcc/clang <c>-Wimplicit-fallthrough</c> (opt-in): warn on a NON-EMPTY
    /// switch section that falls through into the next label without a C23
    /// <c>[[fallthrough]];</c> marker. The last section is exempt (nothing follows to
    /// fall INTO), and a genuinely empty section — stacked labels like
    /// <c>case 0: case 1:</c> — is merged into the next by <see cref="BuildSwitch"/>, so
    /// neither of those fires. A section that ends control flow (break/return/goto/
    /// <c>unreachable()</c>) doesn't fall through. The warning fires EXACTLY when the
    /// backend would synthesize an implicit <c>goto case</c> and no marker excused it —
    /// so it can't disagree with the emitted code. gcc-verbatim wording.</summary>
    private void CheckImplicitFallthrough(IReadOnlyList<SwitchSection> sections)
    {
        for (var i = 0; i < sections.Count - 1; i++)   // last section: nothing to fall INTO
        {
            var body = sections[i].Body;
            if (body.Count == 0) { continue; }                  // empty (stacked labels) — fine
            var last = body[^1];
            if (EndsWithFallthrough(last)) { continue; }        // explicit [[fallthrough]]; — fine
            if (StmtTerminates(last)) { continue; }             // break/return/goto/unreachable — fine
            Diagnostics.Add(new Diagnostic(Severity.Warning, "this statement may fall through", last.Pos, _file));
        }
    }

    /// <summary>True when a statement's effective last statement is a
    /// <see cref="FallthroughMarker"/> — recursing into a trailing lone <c>{ … }</c>
    /// block so <c>case X: { …; [[fallthrough]]; }</c> is recognized as well as the
    /// brace-less <c>case X: …; [[fallthrough]];</c>. Mirrors the block-recursion in
    /// <see cref="StmtTerminates"/>.</summary>
    private static bool EndsWithFallthrough(CStmt s) => s switch
    {
        FallthroughMarker => true,
        Block b => b.Stmts.Count > 0 && EndsWithFallthrough(b.Stmts[^1]),
        _ => false,
    };

    /// <summary>Does control flow leave this statement without falling out the bottom?
    /// The IR-side mirror of <c>CSharpBackend.Terminates</c> (kept in step with it), used
    /// by the <c>-Wimplicit-fallthrough</c> check so the warning matches the code the
    /// backend actually synthesizes. <c>unreachable()</c> lowers to a <c>throw</c>, so a
    /// case ending in it terminates too.</summary>
    private static bool StmtTerminates(CStmt s) => s switch
    {
        Break or Continue or Return or Goto or ZigErrorThrow => true,
        ExprStmt es => IsUnreachableExpr(es.Expr),
        Block b => b.Stmts.Count > 0 && StmtTerminates(b.Stmts[^1]),
        If f => f.Else is { } e && StmtTerminates(f.Then) && StmtTerminates(e),
        Labeled l => StmtTerminates(l.Body),
        DeferGuard g => StmtTerminates(g.Body),
        _ => false,
    };

    /// <summary>True when an expression is (a paren-chain around) the C23
    /// <c>unreachable()</c> call — which both backends lower to a <c>throw</c>. Mirrors
    /// <c>CSharpBackend.IsUnreachableCall</c>.</summary>
    private static bool IsUnreachableExpr(CExpr e)
    {
        while (e is Paren p) { e = p.Inner; }
        return e is Call { Callee: "__dotcc_unreachable" };
    }

    /// <summary>True when a statement is a <c>case</c>/<c>default</c> label,
    /// possibly under a stack of goto labels (<c>a: b: case 1: stmt</c>) — the
    /// shape <see cref="BuildSwitch"/> must split so the goto labels land AFTER
    /// the case labels in the emitted section (C# label placement).</summary>
    private static bool LeadsToCase(Item it) => it.Content switch
    {
        C.CaseLabel or C.DefaultLabel => true,
        C.StmtLabel ls => LeadsToCase(ls.Arg2),
        _ => false,
    };

    /// <summary>Recognise the <c>setjmp</c>/<c>longjmp</c> guard idiom in an
    /// <c>if</c> and desugar it to a <see cref="SetjmpGuard"/>. Returns null when
    /// the condition is not a setjmp guard (the caller then builds a plain <see
    /// cref="If"/>). Recognition runs on the already-built IR — never on emitted
    /// text:
    /// <list type="bullet">
    ///   <item><c>if (setjmp(env)) recovery [else normal]</c> — setjmp is truthy
    ///     ONLY on the longjmp re-entry, so the then-branch is the recovery.</item>
    ///   <item><c>if (setjmp(env) == 0) normal [else recovery]</c> — the compare
    ///     is true on the direct (zero) return, so the then-branch is normal.</item>
    ///   <item><c>if (setjmp(env) != 0) recovery [else normal]</c> — the inverse.</item>
    /// </list></summary>
    private CStmt? SetjmpGuardOf(CExpr cond, CStmt then, CStmt? els, SrcPos pos)
    {
        // Bare `if (setjmp(env)) …` — truthy only on the longjmp re-entry, so the
        // then-branch is the recovery (catch) and the absent/else side is the
        // normal (try) path.
        if (IsSetjmpCall(cond, out var bareEnv, out var bareCall))
        {
            _setjmpCalls.Remove(bareCall);
            return new SetjmpGuard(bareEnv, TryBody: els, CatchBody: then) { Pos = pos };
        }
        // `setjmp(env) == 0` / `!= 0`, either operand order.
        if (cond is Binary { Op: BinOp.Eq or BinOp.Ne } b)
        {
            CExpr? env = null;
            Call? call = null;
            if (IsSetjmpCall(b.Left, out var e1, out var c1) && IsZeroLit(b.Right)) { env = e1; call = c1; }
            else if (IsSetjmpCall(b.Right, out var e2, out var c2) && IsZeroLit(b.Left)) { env = e2; call = c2; }
            if (env is not null && call is not null)
            {
                _setjmpCalls.Remove(call);
                // `== 0` is true on the direct (zero) return; `!= 0` is true on the
                // re-entry. The "true on direct return" branch is the try (normal)
                // body; the other is the catch (recovery).
                var trueOnDirect = b.Op == BinOp.Eq;
                var tryBody = trueOnDirect ? then : els;
                var catchBody = trueOnDirect ? els : then;
                return new SetjmpGuard(env, tryBody, catchBody) { Pos = pos };
            }
        }
        return null;
    }

    /// <summary>True when the name <c>setjmp</c> is shadowed by a local variable/parameter in
    /// the current scope — then a <c>setjmp(...)</c> call is an ordinary indirect call through
    /// that variable (e.g. a function pointer named <c>setjmp</c>, chibi/Lua-style), NOT the libc
    /// <c>setjmp</c>, so none of the setjmp recognition/rejection applies to it.</summary>
    private bool SetjmpShadowed() => _symbols.Resolve("setjmp") is { Kind: SymKind.Var or SymKind.Param };

    /// <summary>True when <paramref name="e"/> is a direct call to the libc <c>setjmp</c>
    /// (parens peeled, and not shadowed by a local of that name); yields the env-token argument
    /// expression and the <see cref="Call"/> node itself (so the recogniser can claim it out of
    /// <see cref="_setjmpCalls"/>).</summary>
    private bool IsSetjmpCall(CExpr e, out CExpr env, out Call call)
    {
        while (e is Paren p) { e = p.Inner; }
        if (e is Call { Callee: "setjmp", Args: { Count: 1 } a } c && !SetjmpShadowed())
        {
            env = a[0];
            call = c;
            return true;
        }
        env = null!;
        call = null!;
        return false;
    }

    /// <summary>True for the integer literal <c>0</c> (parens peeled) — the RHS of
    /// a recognised <c>setjmp(env) == 0</c> guard.</summary>
    private static bool IsZeroLit(CExpr e)
    {
        while (e is Paren p) { e = p.Inner; }
        return e is LitInt { Value: 0 };
    }

    /// <summary>The <c>int</c>-typed literal <c>0</c> — the value a captured setjmp
    /// starts at (its direct return) before the goto-restart body re-runs it.</summary>
    private static CExpr ZeroLit() => new LitInt("0", 0) { Type = CType.Int };

    /// <summary>Recognise a VALUE-CAPTURING <c>setjmp</c> at block level and desugar the
    /// capture PLUS the rest of the block into a goto-restart <see cref="SetjmpCapture"/>:
    /// <list type="bullet">
    ///   <item><c>T r = setjmp(env); …rest…</c> — the decl becomes <c>T r = 0;</c>, and the
    ///     rest of the block is the re-runnable body.</item>
    ///   <item><c>r = setjmp(env); …rest…</c> — same, for a pre-declared simple <c>r</c>
    ///     (a VarRef target only; a side-effecting lvalue is left as a stray → rejected).</item>
    /// </list>
    /// The rest re-runs on each matching <c>longjmp</c> with <c>r</c> holding the jump value, so
    /// the <c>switch</c>/<c>if</c> on <c>r</c> in the rest reaches the recovery path — real C's
    /// "returns twice". Recurses so a SECOND value-capture in the same block nests cleanly.
    /// The bare <c>switch (setjmp(env))</c> form is handled where the switch is built
    /// (<see cref="BuildSwitch"/>), since its region is self-contained.</summary>
    private List<CStmt> RewriteSetjmpCaptures(List<CStmt> stmts)
    {
        for (var i = 0; i < stmts.Count; i++)
        {
            // Shape #1: `T r = setjmp(env);` (a sole-declarator decl).
            if (stmts[i] is DeclStmt { Decls: { Count: 1 } ds } declStmt
                && ds[0].Init is { } init && IsSetjmpCall(init, out var env1, out var call1))
            {
                _setjmpCalls.Remove(call1);
                var target = new VarRef(ds[0].Sym) { Type = ds[0].Sym.Type, IsLValue = true };
                var reset = declStmt with { Decls = new[] { ds[0] with { Init = ZeroLit() } } };
                return BuildCaptureTail(stmts, i, env1, target, reset);
            }
            // Shape #2: `r = setjmp(env);` into a pre-declared SIMPLE var (VarRef only —
            // a Member/Index/Deref target could double-evaluate a side effect across the
            // reset + the catch write, so it's left unrecognised and rejected loudly).
            if (stmts[i] is ExprStmt { Expr: Assign { CompoundOp: null, Target: VarRef tgt, Value: { } rhs } } es
                && IsSetjmpCall(rhs, out var env2, out var call2))
            {
                _setjmpCalls.Remove(call2);
                var reset = es with { Expr = new Assign(null, tgt, ZeroLit()) { Type = tgt.Type, Pos = es.Pos } };
                return BuildCaptureTail(stmts, i, env2, tgt, reset);
            }
        }
        return stmts;
    }

    /// <summary>Build the <c>pre… ; reset ; SetjmpCapture(rest)</c> statement list for a
    /// value-capturing setjmp found at index <paramref name="i"/>. The rest of the block
    /// (statements after the capture) is recursively rewritten — so nested captures nest —
    /// and becomes the re-runnable body.</summary>
    private List<CStmt> BuildCaptureTail(List<CStmt> stmts, int i, CExpr env, CExpr target, CStmt reset)
    {
        var pos = stmts[i].Pos;
        var tail = RewriteSetjmpCaptures(stmts.GetRange(i + 1, stmts.Count - i - 1));
        var result = stmts.GetRange(0, i);
        result.Add(reset);
        result.Add(new SetjmpCapture(env, target, new Block(tail) { Pos = pos }, _setjmpSeq++) { Pos = pos });
        return result;
    }

    /// <summary>A declaration in statement position. The grammar wraps the
    /// concrete declaration kind (plain, array, fn-ptr, …) inside
    /// <c>StmtDecl.Arg0</c>; dispatch on it.</summary>
    private CStmt BuildDeclStmt(Item it) => it.Content switch
    {
        C.Decl d => BuildBlockDecl(d.Arg0, d.Arg1, forInit: false),
        // `auto x = E;` (C23 type inference). See BuildDeclAutoInfer.
        C.DeclAutoInfer d => BuildDeclAutoInfer(d),
        // Pointer-to-array `T (*p)[N]` [= init] — a row pointer (multi-dim machinery).
        C.DeclPtrToArr d => BuildPtrToArr(d.Arg0, d.Arg3, d.Arg5, null),
        C.DeclPtrToArrInit d => BuildPtrToArr(d.Arg0, d.Arg3, d.Arg5, d.Arg7),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary>Per-translation-unit counter giving each function-scope
    /// <c>static</c> local a program-unique backing-field name. The CS0136
    /// per-function rename can't serve here — two functions' same-named statics
    /// become two fields of the SAME <c>DotCcGlobals</c> class.</summary>
    private int _staticLocalSeq;

    /// <summary>A block-scope declaration with declarators, dispatched on the storage
    /// class its Type carries (<see cref="CheckDeclSpecs(Item)"/>): <c>static</c> locals,
    /// <c>extern</c> references to file-scope names, block-scope typedefs, and plain /
    /// <c>auto</c> / <c>register</c> locals. A <c>for</c> loop's initial declaration
    /// (<paramref name="forInit"/>) may declare only auto and register objects (C11
    /// 6.8.5p3); gcc accepts the others with a pedantic warning, as dotcc does.</summary>
    private CStmt BuildBlockDecl(Item typeItem, Item listItem, bool forInit)
    {
        switch (CheckDeclSpecs(typeItem))
        {
            case SpecKw.Static: return BuildStaticLocals(typeItem, listItem, forInit);
            case SpecKw.Extern:
                BuildBlockExternDecls(typeItem, listItem, forInit);
                return new DeclStmt(System.Array.Empty<LocalDecl>());
            case SpecKw.Typedef:
                BuildTypedefs(typeItem, listItem, forInit);
                return new DeclStmt(System.Array.Empty<LocalDecl>());
            default: return BuildDeclList(typeItem, listItem);
        }
    }

    /// <summary>A function-scope <c>static</c> local (<c>static int counter = 0;</c>).
    /// Its storage is program-lifetime, so it lowers to a once-initialized static
    /// field of <c>DotCcGlobals</c> (a <see cref="GlobalVar"/> with a mangled,
    /// program-unique name); references resolve to that field via an alias symbol
    /// in the function's scope. The statement itself emits nothing.</summary>
    private CStmt BuildStaticLocals(Item typeItem, Item listItem, bool forInit)
    {
        WalkDeclList(typeItem, listItem, d =>
        {
            if (d.Type is CType.Func { IsFunctionType: true })
            {
                InvalidFunctionStorage(d);
                return;
            }
            if (forInit)
            {
                _gate?.Report($"declaration of static variable '{d.Name}' in 'for' loop initial declaration", d.At.Position.Line);
            }
            var csName = $"{_symbols.Escape(d.Name)}__s{_staticLocalSeq++}";
            if (d.Type.Unqualified is CType.Array arr)
            {
                BuildStaticArray(d, arr, csName, tuLocal: true);
                return;
            }
            var sym = new Symbol
            {
                Name = d.Name, Kind = SymKind.Var, Type = d.Type,
                Storage = Storage.Static, IsGlobal = true, IsTuLocal = true, TargetName = csName,
            };
            CExpr? slInit = null;
            FlexibleTail? flexible = null;
            if (d.Init is { } ii)
            {
                slInit = BuildInitValue(sym.Type, ii);
                CheckQualifierDiscard(slInit, sym.Type, SrcPos.From(ii), "initialization");
                (slInit, flexible) = SplitFlexibleInit(slInit, SrcPos.From(ii));
            }
            PropagateNativeCallConv(sym, slInit);
            Globals.Add(new GlobalVar(sym, slInit) { Flexible = flexible });
            _symbols.DeclareAlias(sym);
        });
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }

    /// <summary>A <c>for</c> loop's initial declaration (C99 6.8.5p3).</summary>
    private CStmt BuildForInitDecl(Item declItem)
    {
        if (declItem.Content is not C.Decl decl) { throw new IrUnsupportedException(TypeName(declItem.Content)); }
        return BuildBlockDecl(decl.Arg0, decl.Arg1, forInit: true);
    }

    /// <summary>Build a <c>Type DeclItemList</c> block-scope declaration of automatic
    /// objects (<c>int x, y;</c>, <c>auto int z;</c>, <c>register int r;</c>) into a
    /// <see cref="DeclStmt"/>.</summary>
    private CStmt BuildDeclList(Item typeItem, Item listItem)
    {
        // Most declarations are all-scalar → one DeclStmt (the historical shape).
        // An ARRAY declarator in the list (`char *str=NULL, numbuf[LEN];`) lowers
        // to its own ArrayDecl (stackalloc) interleaved in source order; the whole
        // statement then becomes a brace-less Seq so the names share the enclosing
        // scope.
        var stmts = new List<CStmt>();
        var scalars = new List<LocalDecl>();
        void Flush()
        {
            if (scalars.Count > 0) { stmts.Add(new DeclStmt(scalars.ToArray())); scalars.Clear(); }
        }
        _sawConstexprSpec = false; // consumed below (C23 allows block-scope constexpr)
        _sawNoreturnSpec = false;
        _sawInlineSpec = false;
        var isRegister = DeclaresRegister(typeItem);
        WalkDeclList(typeItem, listItem, d =>
        {
            // A block-scope function declaration (C11 6.2.2p5: external linkage)
            // declares the function for the rest of the block and emits nothing.
            if (d.Type is CType.Func { IsFunctionType: true } fnType)
            {
                if (isRegister) { InvalidFunctionStorage(d); }
                DeclareFunctionDeclarator(d, fnType, internalLinkage: false);
                return;
            }
            if (d.Type.Unqualified is CType.Array arr)
            {
                if (_sawConstexprSpec)
                {
                    throw new IrUnsupportedException(
                        $"'{d.Name}': only integer constexpr objects are supported (float/pointer/struct/array constexpr is not built yet)");
                }
                Flush();
                stmts.Add(BuildLocalArray(d, arr));
                return;
            }
            var sym = _symbols.Declare(new Symbol
            {
                Name = d.Name, Kind = SymKind.Var, Storage = isRegister ? Storage.Register : Storage.Auto,
                // const-qualified for the same write-to-const coverage as the
                // file-scope form.
                Type = _sawConstexprSpec ? d.Type.WithQuals(TypeQual.Const) : d.Type,
                IsConstexpr = _sawConstexprSpec,
            });
            CExpr? sInit = null;
            if (d.Init is { } ii)
            {
                sInit = BuildInitValue(sym.Type, ii);
                CheckQualifierDiscard(sInit, sym.Type, SrcPos.From(ii), "initialization");
                CheckNoFlexibleInit(sInit, SrcPos.From(ii));
            }
            PropagateNativeCallConv(sym, sInit);
            if (sym.IsConstexpr) { BindConstexpr(sym, sInit, SrcPos.From(typeItem)); }
            scalars.Add(new LocalDecl(sym, sInit));
        });
        Flush();
        return stmts.Count == 1 ? stmts[0] : new Seq(stmts);
    }

    /// <summary>C23 type inference — <c>auto x = E;</c> deduces <c>x</c>'s type
    /// from its initializer. The IR already synthesizes a <see cref="CType"/> on
    /// every expression, so the declared symbol simply takes the initializer's
    /// type and lowers like any scalar local (codegen prints the inferred C#
    /// type). gcc <c>__auto_type</c> / C++ <c>auto</c> / C# <c>var</c> shape.</summary>
    private DeclStmt BuildDeclAutoInfer(C.DeclAutoInfer n)
    {
        Gate(2023, "auto` type inference", n.Arg1);
        var init = BuildExpr(n.Arg3);
        EnsureNotEmbed(init);
        var sym = _symbols.Declare(new Symbol { Name = Tok(n.Arg1), Kind = SymKind.Var, Type = init.Type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, init) });
    }

    private static CType WrapPtr(CType t, int stars)
    {
        for (var i = 0; i < stars; i++) { t = PointerTo(t); }
        return t;
    }

    /// <summary>The type a pointer declarator gives over <paramref name="t"/>: a
    /// pointer, except over a function type, where it is the function-pointer type
    /// (dotcc represents a fn-ptr as the bare <see cref="CType.Func"/>).</summary>
    private static CType PointerTo(CType t)
        => t is CType.Func { IsFunctionType: true } f ? f with { IsFunctionType = false } : new CType.Pointer(t);

    /// <summary>Count the literal trailing <c>*</c>s on a type node (the
    /// <c>Type → Type *</c> pointer-qualifier chain). Distinguishes a pointer
    /// written with <c>*</c> in the source (binds to the first declarator only)
    /// from one a typedef base contributes (binds to every declarator).</summary>
    private static int CountLiteralStars(Item typeItem)
    {
        var n = 0;
        var it = typeItem;
        while (true)
        {
            switch (it.Content)
            {
                case C.TypePtr t: n++; it = t.Arg0; break;
                case C.TypePtrQualConst t: n++; it = t.Arg0; break;
                case C.TypePtrQualVolatile t: n++; it = t.Arg0; break;
                case C.TypePtrQualRestrict t: n++; it = t.Arg0; break;
                default: return n;
            }
        }
    }

    /// <summary>Collect the dimension expressions of an <c>ArrDims</c> node,
    /// outer→inner.</summary>
    private List<CExpr> BuildArrDims(Item it)
    {
        var dims = new List<CExpr>();
        void Walk(Item n)
        {
            switch (n.Content)
            {
                case C.ArrDimsCons c: Walk(c.Arg0); dims.Add(BuildExpr(c.Arg2)); break;
                case C.ArrDimsOne o: dims.Add(BuildExpr(o.Arg1)); break;
                default: throw new IrUnsupportedException(TypeName(n.Content));
            }
        }
        Walk(it);
        return dims;
    }

    /// <summary>Reject a <c>#embed</c> that reached a scalar / non-brace-array
    /// position. The carrier is only consumed (expanded to bytes) inside an
    /// initializer list (<see cref="ParseInitList"/>); a bare <c>int x = #embed …</c>
    /// or an arbitrary-expression use is a V1 cut, surfaced loudly here rather
    /// than miscompiled.</summary>
    private static void EnsureNotEmbed(CExpr e)
    {
        if (e is EmbedData)
        {
            throw new IrUnsupportedException(
                "#embed is only supported as a brace initializer of an array, "
                + "e.g. `unsigned char d[] = { #embed \"file\" };` "
                + "(scalar, braceless, or arbitrary-expression #embed is not supported)");
        }
    }

    /// <summary>A positional struct/union aggregate initializer from an
    /// <c>InitList</c> — shared by local, file-scope, and static-local struct
    /// inits (see <see cref="BuildStructPositional"/>).</summary>
    private StructInit BuildAggregateInit(CType type, Item initListItem) =>
        BuildStructPositional(type, ParseInitList(initListItem));

    private For BuildForDecl(C.StmtForDecl s)
    {
        // for ( <decl> ; <cond> ; <post> ) <body> — Arg2 is the ScopeEnter epsilon;
        // Decl=Arg3, ForCond=Arg5, ForPost=Arg7, body=Arg9 (see legacy StmtForDecl).
        _symbols.EnterScope();
        Gate(1999, "for-loop initializer declaration", s.Arg3);
        var init = BuildForInitDecl(s.Arg3);
        var cond = BuildForCond(s.Arg5);
        var post = BuildForPost(s.Arg7);
        var body = BuildStmt(s.Arg9);
        _symbols.ExitScope();
        return new For(init, cond, post, body);
    }

    private CExpr? BuildForCond(Item it) => it.Content switch
    {
        C.ForCondExpr e => BuildExpr(e.Arg0),
        C.ForCondEmpty => null,
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    private CExpr? BuildForPost(Item it) => it.Content switch
    {
        C.ForPostEmpty => null,
        C.ForPostExprs e => BuildCommaExpr(e.Arg0),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary>A comma-separated expression list (for-init / for-update). One
    /// expression passes through; several become a <see cref="CommaSeq"/> that
    /// codegen renders as C#'s native <c>a, b</c> list in those positions.</summary>
    private CExpr BuildCommaExpr(Item it)
    {
        var items = new List<CExpr>();
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.CommaExprCons c: Walk(c.Arg0); items.Add(BuildExpr(c.Arg2)); break;
                case C.CommaExprOne o: items.Add(BuildExpr(o.Arg0)); break;
                default: items.Add(BuildExpr(node)); break;
            }
        }
        Walk(it);
        return items.Count == 1 ? items[0] : new CommaSeq(items) { Type = items[^1].Type };
    }

    // ---- expressions -----------------------------------------------------

    private CExpr BuildExpr(Item it)
    {
        var pos = SrcPos.From(it);
        CExpr e = it.Content switch
        {
            C.Num n => BuildNum(n),
            C.Flt f => BuildFloat(f),
            C.Str => BuildStr(it),
            C.U16str => BuildU16Str(it),
            C.Wstr => BuildWStr(it),
            C.U32str => BuildU32Str(it),
            C.U8str => BuildU8Str(it),
            C.Embed em => BuildEmbed(em),
            C.Chr c => BuildChr(c),
            C.U16chr c => BuildU16Chr(c),
            C.Wchr c => BuildWChr(c),
            C.U32chr c => BuildU32Chr(c),
            C.U8chr c => BuildU8Chr(c),
            C.Var v => BuildVar(v),
            C.Paren p => BuildExpr(p.Arg1) is var inner ? new Paren(inner) { Type = inner.Type, IsLValue = inner.IsLValue } : throw new InvalidOperationException(),
            C.Add b => Bin(BinOp.Add, b.Arg0, b.Arg2),
            C.Sub b => Bin(BinOp.Sub, b.Arg0, b.Arg2),
            C.Mul b => Bin(BinOp.Mul, b.Arg0, b.Arg2),
            C.Div b => Bin(BinOp.Div, b.Arg0, b.Arg2),
            C.Mod b => Bin(BinOp.Mod, b.Arg0, b.Arg2),
            C.Shl b => Bin(BinOp.Shl, b.Arg0, b.Arg2),
            C.Shr b => Bin(BinOp.Shr, b.Arg0, b.Arg2),
            C.BAnd b => Bin(BinOp.BitAnd, b.Arg0, b.Arg2),
            C.BOr b => Bin(BinOp.BitOr, b.Arg0, b.Arg2),
            C.BXor b => Bin(BinOp.BitXor, b.Arg0, b.Arg2),
            C.Lt b => Rel(BinOp.Lt, b.Arg0, b.Arg2),
            C.Gt b => Rel(BinOp.Gt, b.Arg0, b.Arg2),
            C.Le b => Rel(BinOp.Le, b.Arg0, b.Arg2),
            C.Ge b => Rel(BinOp.Ge, b.Arg0, b.Arg2),
            C.Eq b => Rel(BinOp.Eq, b.Arg0, b.Arg2),
            C.Neq b => Rel(BinOp.Ne, b.Arg0, b.Arg2),
            C.Land b => Rel(BinOp.LogAnd, b.Arg0, b.Arg2),
            C.Lor b => Rel(BinOp.LogOr, b.Arg0, b.Arg2),
            C.Assign a => Asn(null, a.Arg0, a.Arg2),
            C.AddAssign a => Asn(BinOp.Add, a.Arg0, a.Arg2),
            C.SubAssign a => Asn(BinOp.Sub, a.Arg0, a.Arg2),
            C.MulAssign a => Asn(BinOp.Mul, a.Arg0, a.Arg2),
            C.DivAssign a => Asn(BinOp.Div, a.Arg0, a.Arg2),
            C.ModAssign a => Asn(BinOp.Mod, a.Arg0, a.Arg2),
            C.AndAssign a => Asn(BinOp.BitAnd, a.Arg0, a.Arg2),
            C.OrAssign a => Asn(BinOp.BitOr, a.Arg0, a.Arg2),
            C.XorAssign a => Asn(BinOp.BitXor, a.Arg0, a.Arg2),
            C.ShlAssign a => Asn(BinOp.Shl, a.Arg0, a.Arg2),
            C.ShrAssign a => Asn(BinOp.Shr, a.Arg0, a.Arg2),
            C.PreInc u => Un(UnOp.PreInc, u.Arg1),
            C.PreDec u => Un(UnOp.PreDec, u.Arg1),
            C.PostInc u => Un(UnOp.PostInc, u.Arg0),
            C.PostDec u => Un(UnOp.PostDec, u.Arg0),
            C.UPlus u => Un(UnOp.Plus, u.Arg1),
            C.Neg u => Un(UnOp.Neg, u.Arg1),
            C.BNot u => Un(UnOp.BitNot, u.Arg1),
            C.LNot u => Un(UnOp.LogNot, u.Arg1),
            C.Deref u => Un(UnOp.Deref, u.Arg1),
            C.AddrOf u => Un(UnOp.AddrOf, u.Arg1),
            C.Ternary t => BuildTernary(t),
            C.Subscript s => BuildIndex(s),
            C.MemberDot m => BuildMember(m.Arg0, m.Arg2, arrow: false),
            C.MemberArrow m => BuildMember(m.Arg0, m.Arg2, arrow: true),
            C.Cast c => BuildCast(c),
            C.LitTrue => new LitInt("1", 1) { Type = CType.Int },
            C.LitFalse => new LitInt("0", 0) { Type = CType.Int },
            C.LitNullptr => new NullPtr { Type = new CType.Pointer(CType.Void) },
            C.SizeofType s => new SizeOfExpr(TypeNameType(s.Arg2, TypeNameSite.Sizeof)) { Type = CType.SizeT },
            // `sizeof expr` — the operand isn't evaluated, only its type measured.
            C.SizeofExpr s => BuildSizeofExpr(s),
            // `_Alignof(Type)` (C11 §6.5.3.4) — folds immediately to the layout
            // model's alignment (an integer constant expression, `size_t`-typed
            // like sizeof), so it composes with _Static_assert / array bounds /
            // case labels with no IR node of its own.
            C.AlignofType a => FoldAlignof(a, it),
            C.GenericSelect g => BuildGenericSelect(g, it),
            C.OffsetofExpr o => BuildOffsetof(o),
            // `va_arg(ap, T)` — special syntax (its 2nd operand is a type).
            C.VaArgExpr v => BuildVaArg(v),
            C.Call c => BuildCall(c.Arg0, c.Arg2),
            C.CallNoArgs c => BuildCall(c.Arg0, null),
            // C99/C23 compound literals — (T){…} struct/scalar, (T[]){…} array,
            // designated, and the C23 empty form.
            C.CompoundLit c => Gated(1999, "compound literals", c.Arg1, BuildCompoundLit(CompoundLitType(c.Arg1), c.Arg4)),
            C.CompoundLitEmpty c => Gated(2023, "empty initializer", c.Arg1, BuildCompoundLitEmpty(CompoundLitType(c.Arg1))),
            C.CompoundLitArr c => Gated(1999, "compound literals", c.Arg1, BuildArrayCompoundLit(CompoundLitType(c.Arg1), c.Arg2, c.Arg5)),
            C.CompoundLitArrImplicit c => Gated(1999, "compound literals", c.Arg1, BuildArrayCompoundLit(CompoundLitType(c.Arg1), null, c.Arg6)),
            C.CommaOp => BuildCommaOp(it),
            _ => throw new IrUnsupportedException(TypeName(it.Content)),
        };
        var result = e with { Pos = pos };
        // Track every libc setjmp call by the FINAL (post-Pos-clone) object — the reference that
        // actually lands in the IR tree — so a recogniser can claim it by identity through
        // the shallow statement clones. `e with {…}` above would orphan a reference taken in
        // BuildCall, so this must be here, after the clone. An unclaimed survivor is a stray
        // (unsupported shape) rejected by RejectStraySetjmp once the body is built. A `setjmp`
        // SHADOWED by a local (a fn-ptr named `setjmp`) is an ordinary call, not the libc one —
        // excluded here so it neither triggers recognition nor gets rejected.
        if (result is Call { Callee: "setjmp" } sjCall && !SetjmpShadowed()) { _setjmpCalls[sjCall] = pos; }
        return result;
    }

    private CExpr BuildTernary(C.Ternary t)
    {
        var cond = BuildExpr(t.Arg0);
        var then = BuildExpr(t.Arg2);
        var els = BuildExpr(t.Arg4);
        // Result type: arithmetic arms reconcile per the usual conversions; if either
        // arm is a pointer/array/function the result is THAT (C: `cond ? ptr : NULL`
        // is the pointer type, not the null constant's int) — else the then-arm's type.
        static bool IsPtrish(CType t) => t.Unqualified is CType.Pointer or CType.Array or CType.Func;
        var ty = then.Type.IsArithmetic && els.Type.IsArithmetic ? CType.UsualArithmetic(then.Type, els.Type)
               : IsPtrish(then.Type) ? then.Type
               : IsPtrish(els.Type) ? els.Type
               : then.Type;
        return new CondExpr(cond, then, els) { Type = ty };
    }

    private CExpr BuildIndex(C.Subscript s)
    {
        var base_ = BuildExpr(s.Arg0);
        var idx = BuildExpr(s.Arg2);
        var elem = base_.Type switch
        {
            CType.Pointer p => p.Pointee,
            CType.Array a => a.Element,
            _ => CType.Int,
        };
        return new Index(base_, idx) { Type = elem, IsLValue = true };
    }

    private CExpr BuildMember(Item baseItem, Item fieldItem, bool arrow) =>
        BuildMemberAccess(BuildExpr(baseItem), Tok(fieldItem), arrow);

    /// <summary>Build a member access, routing a promoted (anonymous-aggregate)
    /// field through its hidden container: <c>v.i</c> on an aggregate whose <c>i</c>
    /// came from an anonymous <c>struct/union {…};</c> becomes <c>v.hidden.i</c>.
    /// Recurses so a field promoted through several nesting levels still resolves.</summary>
    private CExpr BuildMemberAccess(CExpr base_, string field, bool arrow)
    {
        if (StructCanonical(base_.Type) is { } canonical
            && _promoted.TryGetValue(canonical, out var pm)
            && pm.TryGetValue(field, out var p))
        {
            var hidden = new Member(base_, p.Hidden, arrow) { Type = new CType.Named(p.Nested).WithQuals(MemberQuals(base_, arrow)), IsLValue = true };
            return BuildMemberAccess(hidden, field, arrow: false);
        }
        return new Member(base_, field, arrow) { Type = MemberType(base_, field).WithQuals(MemberQuals(base_, arrow)), IsLValue = true };
    }

    /// <summary>The qualifiers a member access inherits from its object (C11 6.5.2.3p3, p4): a member
    /// of a <c>const</c> or <c>volatile</c> struct or union is itself so qualified, through <c>.</c> or
    /// through a pointer to one.</summary>
    private static TypeQual MemberQuals(CExpr base_, bool arrow)
    {
        var obj = arrow ? (base_.Type.Unqualified as CType.Pointer)?.Pointee : base_.Type;
        return obj is null ? TypeQual.None : obj.Quals & (TypeQual.Const | TypeQual.Volatile);
    }


    /// <summary><c>sizeof expr</c>: the operand is not evaluated, only its type measured,
    /// which must be complete (a flexible array member's is not; gcc's error).</summary>
    private CExpr BuildSizeofExpr(C.SizeofExpr s)
    {
        var operand = BuildExpr(s.Arg1);
        if (operand.Type.Unqualified is CType.Array { Count: null } incomplete)
        {
            Diagnostics.Add(new Diagnostic(Severity.Error,
                $"invalid application of 'sizeof' to incomplete type '{incomplete.Describe()}'", SrcPos.From(s.Arg1), _file));
        }
        return new SizeOfExpr(operand.Type) { Type = CType.SizeT };
    }

    /// <summary><c>va_arg(ap, T)</c>: its second operand is a type name.</summary>
    private CExpr BuildVaArg(C.VaArgExpr v)
    {
        var type = TypeNameType(v.Arg4, TypeNameSite.SpecifierQualifierList);
        return new VaArgGet(BuildExpr(v.Arg2), type) { Type = type };
    }

    private CExpr BuildCast(C.Cast c)
    {
        var target = TypeNameType(c.Arg1, TypeNameSite.Cast);
        var operand = BuildExpr(c.Arg3);
        // `(void)X`: X is evaluated and its value discarded. A void cast node keeps X's own
        // type on X, so each backend sees what X produces (the wat backend drops it; C#,
        // which has no void cast, renders X in statement position).
        if (target is CType.VoidType) { return new Cast(CType.Void, operand) { Type = CType.Void, Pos = operand.Pos }; }
        // `(void *)0`, the headers' NULL: the null pointer constant, typed void* (C11
        // 6.3.2.3p3), the same node C23 `nullptr` builds.
        if (target.Unqualified is CType.Pointer { Pointee: CType.VoidType } && IsNullPointerConstant(operand))
        {
            return new NullPtr { Type = target };
        }
        if (target is CType.Func ft)
        {
            // A cast of a dlsym() result DIRECTLY to a function-pointer type is
            // POSIX's own idiom for invoking a loaded symbol. dlsym is the ONLY
            // producer of native code addresses in a dotcc program, so mark the
            // function type native-calling-convention here (→ delegate*
            // unmanaged[Cdecl]). The marker rides the cast's Type, so the inline
            // form `((T(*)(a))dlsym(h,"x"))(a)` already calls through the C
            // convention; it also propagates onto a fn-ptr local's declared type in
            // BuildFnPtrLocal. See include/dlfcn.h for the contract.
            if (IsDlsymCall(c.Arg3))
            {
                var native = ft with { IsNativeCallConv = true };
                return new Cast(native, operand) { Type = native };
            }
            // No-silent-miscompile guard: a cast to a fn-ptr type whose operand is a
            // void*-typed value that is NOT a direct dlsym() call cannot be verified
            // native — if that void* actually came from dlsym, the emitted (managed)
            // call would use the wrong calling convention. Warn, but only once
            // <dlfcn.h> is in scope (its `dlsym` prototype registered), so the
            // legitimate managed void*-context fn-ptr idiom in non-dlfcn code stays
            // silent. A null pointer (`(destructor)NULL`) holds no code address, so
            // there is nothing to verify.
            if (DlfcnInScope && Unparen(operand) is not NullPtr && operand.Type.Unqualified is CType.Pointer { Pointee: CType.VoidType })
            {
                Diagnostics.Add(new Diagnostic(
                    Severity.Warning,
                    "cast of void* to a function-pointer type cannot be verified as native code; "
                    + "if this pointer came from dlsym(), cast the dlsym() call directly so the call "
                    + "uses the C calling convention",
                    SrcPos.From(c.Arg3),
                    _file));
            }
        }
        return new Cast(target, operand) { Type = target };
    }

    /// <summary>True when <paramref name="it"/> (peeling redundant parens) is a
    /// direct call to <c>dlsym</c> — the one native-code-address producer dotcc
    /// recognises, so casting it to a function-pointer type marks that type
    /// native (a <c>delegate* unmanaged[Cdecl]</c> call). See include/dlfcn.h.</summary>
    private bool IsDlsymCall(Item it) => it.Content switch
    {
        C.Paren p => IsDlsymCall(p.Arg1),
        C.Call call => TryCalleeName(call.Arg0, out var n) && n == "dlsym",
        C.CallNoArgs call => TryCalleeName(call.Arg0, out var n) && n == "dlsym",
        _ => false,
    };

    /// <summary>True once <c>&lt;dlfcn.h&gt;</c> is in scope — detected by its
    /// <c>dlsym</c> prototype being registered (the synthetic header declares it).
    /// Gates the void*→fn-ptr "cannot verify native" warning so the legitimate
    /// managed void*-context fn-ptr idiom in non-dlfcn code stays silent.</summary>
    private bool DlfcnInScope => _symbols.Resolve("dlsym") is { Kind: SymKind.Func };

    /// <summary>The value-context comma operator (<c>Expr → Expr ',' E</c>,
    /// left-associative). Flatten nested commas into a single ordered operand
    /// list — the leading operands are evaluated for side effects, the last is
    /// the value (and type).</summary>
    private CExpr BuildCommaOp(Item it)
    {
        var items = new List<CExpr>();
        void Walk(Item node)
        {
            if (node.Content is C.CommaOp c) { Walk(c.Arg0); items.Add(BuildExpr(c.Arg2)); }
            else { items.Add(BuildExpr(node)); }
        }
        Walk(it);
        return new CommaOp(items) { Type = items[^1].Type, IsLValue = items[^1].IsLValue };
    }

    private CExpr Bin(BinOp op, Item l, Item r)
    {
        var le = BuildExpr(l);
        var re = BuildExpr(r);
        return new Binary(op, le, re) { Type = BinaryType(op, le.Type, re.Type) };
    }

    private CExpr Rel(BinOp op, Item l, Item r)
    {
        var le = BuildExpr(l);
        var re = BuildExpr(r);
        // C11 6.5.9p5: comparing a pointer (or function pointer) with a null
        // pointer constant converts the constant to the pointer's type. C# will
        // not compare `int*` with `int` (CS0019), so the `0` becomes the typed
        // null pointer (GH #230).
        if (op is BinOp.Eq or BinOp.Ne)
        {
            if (IsPointerOperand(le.Type) && IsNullPointerConstant(re)) { re = new NullPtr { Type = le.Type }; }
            else if (IsPointerOperand(re.Type) && IsNullPointerConstant(le)) { le = new NullPtr { Type = re.Type }; }
        }
        return new Binary(op, le, re) { Type = CType.Int };
    }

    /// <summary>A pointer or function-pointer operand of <c>==</c> / <c>!=</c>.</summary>
    private static bool IsPointerOperand(CType t) => t.Unqualified is CType.Pointer or CType.Func;

    /// <summary>C's null pointer constant (C11 6.3.2.3p3): an integer constant
    /// expression with the value 0 (<c>0</c>, <c>0L</c>, <c>(0)</c>, <c>'\0'</c>, an
    /// enumerator equal to 0).</summary>
    private bool IsNullPointerConstant(CExpr e) =>
        e.Type.Unqualified is CType.Prim { Integer: true } or CType.Enum && ConstEval(e) is 0;

    /// <summary>The C type of a binary arithmetic/bitwise/shift expression's
    /// result. Pointer arithmetic (<c>p + i</c> / <c>p - i</c>) yields the
    /// (decayed) pointer type and <c>p - q</c> yields <c>ptrdiff_t</c> (a signed
    /// 64-bit, <c>long</c> here); a shift yields its promoted left operand's type
    /// (the right operand doesn't take part); everything else takes the usual
    /// arithmetic conversions (<see cref="CType.UsualArithmetic"/>).</summary>
    private static CType BinaryType(BinOp op, CType l, CType r)
    {
        // C99 _Complex (lowered to System.Numerics.Complex): arithmetic with a
        // complex operand yields complex — its C# operators handle complex×real.
        if (IsComplexType(l) || IsComplexType(r)) { return CType.Complex; }
        static CType Decay(CType t) => t.Unqualified switch
        {
            CType.Array a => new CType.Pointer(a.Element),
            var u => u,
        };
        var lPtr = l.Unqualified is CType.Pointer or CType.Array;
        var rPtr = r.Unqualified is CType.Pointer or CType.Array;
        if (op is BinOp.Add or BinOp.Sub && (lPtr || rPtr))
        {
            return lPtr && rPtr ? CType.Long : Decay(lPtr ? l : r);
        }
        if (op is BinOp.Shl or BinOp.Shr)
        {
            return l.Unqualified is CType.Prim { Integer: true, Bytes: < 4 } ? CType.Int : l.Unqualified;
        }
        return CType.UsualArithmetic(l, r);
    }

    private CExpr Asn(BinOp? op, Item l, Item r)
    {
        var le = BuildExpr(l);
        ReportConstWrite(le, SrcPos.From(l), "assignment of");
        var re = BuildExpr(r);
        // Plain `p = q` losing a pointee const is a qualifier discard (compound
        // assignment doesn't convert the RHS to the LHS pointer type).
        if (op is null) { CheckQualifierDiscard(re, le.Type, SrcPos.From(l), "assignment"); }
        return new Assign(op, le, re) { Type = le.Type, IsLValue = false };
    }

    /// <summary>Diagnose a write through a <c>const</c>-qualified lvalue (assignment
    /// or <c>++</c>/<c>--</c>) as a hard error — writing to a const object is a C
    /// constraint violation (gcc/clang reject it), and it is also what licenses the
    /// read-only-array RVA lowering. The lvalue's own type carries the qualifier, so
    /// this fires for a const variable, a write through a pointer-to-const
    /// (<c>*p</c> where <c>p</c> is <c>const T*</c>), and a const array element.
    /// Skipped when the write's position is in a system header (the user isn't to
    /// blame for the runtime's own decls).</summary>
    private void ReportConstWrite(CExpr target, SrcPos pos, string verb)
    {
        if (!target.Type.IsConst || pos.IsSystemHeader) { return; }
        var what = Unparen(target) is VarRef v ? $"variable '{v.Sym.Name}'" : "location";
        Diagnostics.Add(new Diagnostic(Severity.Error, $"{verb} read-only {what}", pos, _file));
    }

    /// <summary>Warn (gcc <c>-Wdiscarded-qualifiers</c>) when an IMPLICIT conversion
    /// drops a <c>const</c> from a pointer's pointee — passing a <c>const T*</c>
    /// where a <c>T*</c> is expected, or <c>p = q</c> with <c>p</c> a <c>T*</c> and
    /// <c>q</c> a <c>const T*</c>. An explicit cast is the programmer's deliberate
    /// override and is exempt, as is anything originating in a system header. Adding
    /// const (<c>T*</c> → <c>const T*</c>) is always allowed.</summary>
    private void CheckQualifierDiscard(CExpr value, CType target, SrcPos pos, string context)
    {
        if ((_warnings & WarningFlags.DiscardedQualifiers) == 0 || pos.IsSystemHeader || Unparen(value) is Cast) { return; }
        if (PointeeOf(value.Type) is { } sp && PointeeOf(target) is { } tp && sp.IsConst && !tp.IsConst)
        {
            Diagnostics.Add(new Diagnostic(Severity.Warning,
                $"{context} discards 'const' qualifier from pointer target type", pos, _file));
        }
    }

    /// <summary>Evaluate a <c>_Static_assert(expr[, "msg"]);</c> (C11 §6.7.10) at
    /// compile time via <see cref="ConstEval"/>. The controlling expression must be an
    /// integer constant expression: a non-constant one (e.g. a <c>const</c> variable
    /// read — not an ICE in C, matching gcc/clang) or a constant ZERO is a collected
    /// compile error; non-zero holds and the declaration emits nothing. The optional
    /// message rides as its raw quoted token text, giving gcc's exact wording
    /// (<c>static assertion failed: "msg"</c>).</summary>
    private void CheckStaticAssert(Item exprItem, Item? msgItem, SrcPos pos)
    {
        _firstUndeclared = null;
        var cond = BuildExpr(exprItem);
        switch (ConstEval(cond))
        {
            case null:
                // An undeclared identifier is the likely cause; report it first, as gcc
                // does (a header's missing macro reads as an unknown name here).
                if (_firstUndeclared is { } undeclared)
                {
                    Diagnostics.Add(new Diagnostic(Severity.Error,
                        _symbols.AtFileScope
                            ? $"'{undeclared}' undeclared here (not in a function)"
                            : $"'{undeclared}' undeclared (first use in this function)",
                        SrcPos.From(exprItem), _file));
                }
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    "expression in static assertion is not constant", pos, _file));
                break;
            case 0:
                var suffix = msgItem is { } m ? ": " + Tok(m) : "";
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    "static assertion failed" + suffix, pos, _file));
                break;
        }
    }

    /// <summary>The pointed-to / element type of a pointer or array, or null for a
    /// non-indirect type — the level at which a pointer const-discard is judged.</summary>
    private static CType? PointeeOf(CType t) => t.Unqualified switch
    {
        CType.Pointer p => p.Pointee,
        CType.Array a => a.Element,
        _ => null,
    };

    private CExpr Un(UnOp op, Item operand)
    {
        var oe = BuildExpr(operand);
        // ++/-- on a const lvalue is a write — same constraint violation as assignment.
        if (op is UnOp.PreInc or UnOp.PostInc or UnOp.PreDec or UnOp.PostDec)
        {
            ReportConstWrite(oe, SrcPos.From(operand),
                op is UnOp.PreInc or UnOp.PostInc ? "increment of" : "decrement of");
        }
        // Record the target-neutral C fact that an object's address is taken (&x), for
        // any var or param. Each backend decides what it implies: the C# backend stores
        // an address-taken pointer GLOBAL as `nint` (a pointer T can't be
        // Unsafe.AsPointer<T> — CS0306; see CSharpBackend.NintStorage), while the wat backend
        // gives any address-taken local/param a linear-memory frame slot. Set here, at
        // the one site every `&` node is built, so the fact is complete and no backend
        // has to re-derive it by walking the tree.
        if (op == UnOp.AddrOf && Unparen(oe) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } sym })
        {
            // C11 6.5.3.2p1: the operand of `&` may not be declared `register`.
            if (sym.Storage == Storage.Register)
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    $"address of register variable '{sym.Name}' requested", SrcPos.From(operand), _file));
            }
            sym.AddressTaken = true;
        }
        CType t = op switch
        {
            UnOp.LogNot => CType.Int,
            // Unary + - ~ apply C's integer promotions (§6.3.1.1): `-sbyteField`
            // is an INT — without this the store coercion can't see the C#-side
            // promotion and a narrowing store (chibi's `sign = -sign`) misses
            // its cast. inc/dec below keep the lvalue's own type.
            UnOp.Plus or UnOp.Neg or UnOp.BitNot => CType.IntegerPromote(oe.Type),
            // `&f` of a function designator is the pointer to the function,
            // which in dotcc's IR IS the fn-ptr type (a bare CType.Func), so
            // Pointer(Func) only ever means a pointer TO a function pointer
            // (`&fp`, a `(**name)` declarator).
            UnOp.AddrOf => Unparen(oe) is VarRef { Sym.Kind: SymKind.Func } ? oe.Type : new CType.Pointer(oe.Type),
            // *p → pointee; *arr (incl. a string literal, typed char[]) → its element
            // (the array decays to a pointer first). *ptr-to-array stays the array,
            // which codegen treats as a no-op decay back to the row pointer.
            UnOp.Deref => oe.Type.Unqualified switch
            {
                CType.Pointer p => p.Pointee,
                CType.Array a => a.Element,
                _ => oe.Type,
            },
            _ => oe.Type,
        };
        // `*fp` designates the function, which converts straight back to the pointer
        // (C11 6.5.3.2p4, 6.3.2.1p4): `iternext = *tp->tp_iternext;` stores `fp` itself.
        if (op == UnOp.Deref && IsFuncPtr(oe.Type)) { return oe; }
        return new Unary(op, oe) { Type = t, IsLValue = op == UnOp.Deref };
    }

    /// <summary>Peel redundant <see cref="Paren"/> wrappers to reach the inner expr.</summary>
    private static CExpr Unparen(CExpr e) => e is Paren p ? Unparen(p.Inner) : e;

    private CExpr BuildVar(C.Var v)
    {
        var name = Tok(v.Arg0);
        var sym = _symbols.Resolve(name);
        if (sym is { Kind: SymKind.EnumConst })
        {
            // An enumerator of a real (named) enum renders as EnumName.Member; one of
            // an anonymous int-constant enum lowers to its literal integer value.
            return sym.Type.Unqualified is CType.Enum
                ? new EnumConstRef(sym) { Type = sym.Type }
                : new LitInt(sym.ConstValue.ToString(System.Globalization.CultureInfo.InvariantCulture), sym.ConstValue) { Type = CType.Int };
        }
        if (sym is not null)
        {
            // Import-mode: a read/write through an extern DATA symbol is a candidate
            // native data import (resolved against another TU's definition, or — if
            // none — warned as unsupported by the emit pass).
            if (sym is { Kind: SymKind.Var, Storage: Storage.Extern }) { _referencedExternData.Add(sym.Name); }
            // A function named other than to call it (its address taken, or decayed into a
            // function pointer) is used as surely as a call uses it: a library unit or an
            // import must supply it all the same.
            if (sym is { Kind: SymKind.Func }) { _referencedFuncs.Add(sym.Name); }
            return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
        }
        // `__func__` (C99 §6.4.2.2) — a predefined identifier implicitly declared
        // at the start of every function body as `static const char __func__[] =
        // "<name>";`. It is not a macro (the preprocessor never sees it), so it
        // surfaces here as an unbound name; resolve it to a string literal of the
        // enclosing function's name. (`__FILE__`/`__LINE__` ARE macros and are
        // already expanded upstream by the preprocessor.)
        // The imaginary unit — <complex.h> expands `I` → `_Complex_I` →
        // `__dotcc_complex_I`, a Libc static of System.Numerics.Complex surfaced by
        // `using static Libc`. Type it complex so `a + b*I` propagates correctly.
        if (name is "__dotcc_complex_I")
        {
            return new NameRef("__dotcc_complex_I") { Type = CType.Complex };
        }
        if (name is "__func__" && _currentFnName.Length != 0)
        {
            var fnSegs = new[] { $"\"{_currentFnName}\"" };
            DotCC.EmitHelpers.EncodeStringLiteral(fnSegs, out var fnLen);
            return new LitStr(fnSegs) { Type = new CType.Array(CType.Char, fnLen) };
        }
        // Unresolved (a macro-substituted token, a builtin not in a header). Surface
        // the raw name; the backend escapes it and lets its compiler arbitrate. Slice
        // code never hits this; it's a safety net during incremental growth. A
        // constant-expression context reads the first such name to report it.
        _firstUndeclared ??= name;
        return new NameRef(name) { Type = CType.Int };
    }

    /// <summary>The first identifier <see cref="BuildVar"/> could not resolve since a
    /// constant-expression context cleared it, so that context can report the
    /// undeclared name (gcc's diagnostic) rather than only "not constant".</summary>
    private string? _firstUndeclared;

    private CExpr BuildCall(Item calleeItem, Item? argList)
    {
        var args = new List<CExpr>();
        if (argList is { } al) { FlattenArgs(al, x => args.Add(BuildExpr(x))); }

        // A simple named callee — a function, a fn-ptr variable, or a libc builtin.
        if (TryCalleeName(calleeItem, out var name))
        {
            // Import-mode: record every directly-called name (a conservative
            // superset — intersected with the proto-only set at ProtoOnlyReferenced).
            _referencedFuncs.Add(name);
            // The resolved signature's parameter types drive call-argument
            // coercion (C's implicit conversion at a call, e.g. `size_t` sizeof
            // arg → `int` malloc param, or the int-0 null-pointer constant). The
            // resolved symbol rides along so the backend emits its TargetName —
            // matters only when a same-named static was renamed (see BuildFuncDef).
            var sym = _symbols.Resolve(name);
            var fn = sym?.Type.Unqualified as CType.Func;
            if (sym is { Kind: SymKind.Var or SymKind.Param } && fn is null)
            {
                // C11 6.5.2.2p1: an object called by name must be a function
                // pointer (a pointer to one needs its `*` first).
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    "called object is not a function or function pointer", SrcPos.From(calleeItem), _file));
            }
            // Passing a `const T*` where the parameter is a plain `T*` discards the
            // pointee const (gcc -Wdiscarded-qualifiers). Only the fixed params are
            // checked; a variadic tail has no declared type to compare against.
            if (fn?.Params is { } ps)
            {
                var n = Math.Min(args.Count, ps.Count);
                for (var i = 0; i < n; i++)
                {
                    CheckQualifierDiscard(args[i], ps[i], args[i].Pos, $"passing argument {i + 1} to '{name}'");
                }
            }
            // A fn-ptr VARIABLE / parameter callee rides along too: its field or local
            // may be spelled differently from the C name (a block-scope static's
            // mangled `name__sN` field, a CS0136 rename), and the call must use that.
            var calleeSym = sym is { Kind: SymKind.Func }
                || sym is { Kind: SymKind.Var or SymKind.Param } && fn is not null ? sym : null;
            // No header declares C11's atomic generic functions or <stdarg.h>'s macros (their
            // operand types vary): an undeclared one has its standard result type, not an
            // implicit int.
            var type = fn?.Return ?? (sym is null ? GenericBuiltinResult(name, args) : null) ?? CType.Int;
            return new Call(name, args, fn?.Params, calleeSym) { Type = type };
        }

        // Indirect call through a computed fn-ptr expression: `(*fp)(x)`,
        // `tbl[i](x)`, `s.fn(x)`. Dereferencing a function pointer is a C no-op
        // that decays straight back to the pointer; C# calls fn-ptrs directly
        // and rejects `*fp` (CS0193). Peel redundant parens, then any leading
        // deref whose operand is itself a function pointer — repeatedly, so
        // `(*(e.op))(x)` and the parenthesised `(*f)(x)` forms both reduce. A
        // deref of a pointer-TO-fn-pointer (`(*pp)(x)`) is preserved: it is what
        // reaches the callable value.
        var callee = BuildExpr(calleeItem);
        while (true)
        {
            if (callee is Paren pc) { callee = pc.Inner; continue; }
            if (callee is Unary { Op: UnOp.Deref } u && IsFuncPtr(u.Operand.Type)) { callee = u.Operand; continue; }
            break;
        }
        var calleeFn = callee.Type.Unqualified as CType.Func;
        if (calleeFn is null)
        {
            // C11 6.5.2.2p1: the callee must be a function pointer (a pointer to
            // a function pointer, say, needs its `*` first).
            Diagnostics.Add(new Diagnostic(Severity.Error,
                "called object is not a function or function pointer", SrcPos.From(calleeItem), _file));
        }
        // The function pointer's parameter types drive the same call-argument
        // coercion as a direct call's (GH #230: `s.fn(0)` passes `null`).
        return new IndirectCall(callee, args, calleeFn?.Params) { Type = calleeFn?.Return ?? CType.Int };
    }

    /// <summary>The result type of a C11 <c>&lt;stdatomic.h&gt;</c> generic function (7.17):
    /// the atomic object's non-atomic type for a load, an exchange or a fetch-and-modify,
    /// <c>_Bool</c> for a compare-exchange, a flag test or the lock-free query, <c>void</c> for a
    /// store, an init, a flag clear or a fence. And <c>void</c> for <c>&lt;stdarg.h&gt;</c>'s
    /// <c>va_start</c>, <c>va_end</c> and <c>va_copy</c> (7.16.1). Null for any other name.</summary>
    private static CType? GenericBuiltinResult(string name, IReadOnlyList<CExpr> args)
    {
        if (name is "va_start" or "va_end" or "va_copy") { return CType.Void; }
        var generic = name.EndsWith("_explicit", StringComparison.Ordinal) ? name[..^"_explicit".Length] : name;
        CType? Object() => args.Count > 0 && args[0].Type.Unqualified is CType.Pointer p ? p.Pointee.Unqualified : null;
        return generic switch
        {
            "atomic_load" or "atomic_exchange" or "atomic_fetch_add" or "atomic_fetch_sub"
                or "atomic_fetch_or" or "atomic_fetch_and" or "atomic_fetch_xor" => Object(),
            "atomic_compare_exchange_strong" or "atomic_compare_exchange_weak"
                or "atomic_flag_test_and_set" or "atomic_is_lock_free" => CType.Bool,
            "atomic_store" or "atomic_init" or "atomic_flag_clear"
                or "atomic_thread_fence" or "atomic_signal_fence" => CType.Void,
            _ => null,
        };
    }

    /// <summary>True when <paramref name="t"/> is a function pointer (or a bare
    /// function type) — the operand of a no-op call-site deref. Typedefs already
    /// resolve to their underlying type at <c>ResolveType</c> time, so a plain
    /// structural check on the unqualified type suffices.</summary>
    private static bool IsFuncPtr(CType t) => t.Unqualified is CType.Func;

    /// <summary>True when <paramref name="t"/> is the C99 <c>_Complex</c> type —
    /// recognised structurally, independent of any target spelling.</summary>
    private static bool IsComplexType(CType t) =>
        t.Unqualified is CType.ComplexType;

    private bool TryCalleeName(Item it, out string name)
    {
        switch (it.Content)
        {
            case C.Var v: name = Tok(v.Arg0); return true;
            case C.Paren p: return TryCalleeName(p.Arg1, out name);
            default: name = ""; return false;
        }
    }

    private void FlattenArgs(Item it, Action<Item> onArg)
    {
        switch (it.Content)
        {
            case C.ArgsCons c: onArg(c.Arg0); FlattenArgs(c.Arg2, onArg); break;
            case C.ArgsOne o: onArg(o.Arg0); break;
            default: onArg(it); break;
        }
    }

    // ---- literals --------------------------------------------------------

    /// <summary><c>offsetof(T, member)</c> — resolve the aggregate type and look
    /// up the member's field type so codegen knows whether it is a primitive
    /// <c>fixed</c>-buffer (whose access already yields its address, so no
    /// <c>&amp;</c>). The actual offset is computed at runtime by the
    /// null-pointer idiom in codegen, matching the .NET blittable layout.</summary>
    private CExpr BuildOffsetof(C.OffsetofExpr n)
    {
        var structType = TypeNameType(n.Arg2, TypeNameSite.SpecifierQualifierList);
        var path = CollectOffsetofPath(n.Arg4);
        // Record the FINAL member's declared type (a neutral fact); the backend
        // decides whether its layout makes the member's access self-addressing.
        // Walk the path segment by segment: each intermediate segment must be a
        // modelled struct/union member whose type names the next level.
        CType? memberType = null;
        var canon = (structType.Unqualified as CType.Named)?.Name;
        foreach (var seg in path)
        {
            memberType = null;
            if (canon is null || !_structFields.TryGetValue(canon, out var fields)) { break; }
            foreach (var f in fields)
            {
                if (f.Name != seg) { continue; }
                memberType = f.Type;
                break;
            }
            canon = (memberType?.Unqualified as CType.Named)?.Name;
        }
        return new OffsetOf(structType, path, memberType) { Type = CType.SizeT };
    }

    /// <summary>Flatten an <c>OffsetofPath</c> parse tree (<c>ID ('.' ID)*</c>)
    /// into its segment names, in source order.</summary>
    private List<string> CollectOffsetofPath(Item it)
    {
        var segs = new List<string>();
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.OffsetofPathCons c: Walk(c.Arg0); segs.Add(Tok(c.Arg2)); break;
                case C.OffsetofPathOne o: segs.Add(Tok(o.Arg0)); break;
                default: throw new IrUnsupportedException(TypeName(node.Content));
            }
        }
        Walk(it);
        return segs;
    }

    /// <summary>Transient carrier for a C23 <c>#embed</c> payload — the named
    /// file's raw bytes, resolved from the preprocessor side-table. It NEVER
    /// reaches the backend: <see cref="ParseInitList"/> expands it to byte
    /// constants in initializer position, and any other position is rejected
    /// loudly (scalar / braceless / arbitrary-expression <c>#embed</c> is a V1
    /// cut). Typed <c>char[N]</c> like a string literal so a sole-embed char
    /// array sizes correctly before expansion.</summary>
    internal sealed record EmbedData(IReadOnlyList<int> Bytes) : CExpr;

    /// <summary>Lower a C23 <c>#embed</c> carrier (one synthetic EMBED token whose
    /// content is the content-hash key) to an <see cref="EmbedData"/> over the
    /// file bytes the preprocessor stashed. Gated C23.</summary>
    private CExpr BuildEmbed(C.Embed em)
    {
        Gate(2023, "#embed", em.Arg0);
        var key = Tok(em.Arg0);
        if (key is null || !_embeds.TryGetValue(key, out var bytes))
        {
            // The carrier and the side-table are produced together in OnEmbed;
            // a miss means an internal plumbing bug, not user error.
            throw new IrUnsupportedException("internal: #embed payload not found for carrier token");
        }
        var ints = new int[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) { ints[i] = bytes[i]; }
        return new EmbedData(ints) { Type = new CType.Array(CType.Char, bytes.Length) };
    }

    private CExpr BuildStr(Item it)
    {
        var segs = CollectStrSegments(((C.Str)it.Content).Arg0);
        // A string literal has type char[N] (N = decoded bytes incl. NUL). It
        // decays to char* in most contexts — but NOT under sizeof, which is why
        // the array type is carried rather than the decayed pointer. The byte
        // length is decoded here for the TYPE; the backend re-decodes the segments
        // to emit the literal text (the IR carries no target text).
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
    }

    private CExpr BuildU8Str(Item it)
    {
        // u8"…" — a C23 char8_t (UTF-8) string literal. dotcc's plain narrow strings
        // are ALREADY UTF-8, so this reuses the byte LitStr node and the exact same
        // Libc.L(…u8) lowering; only the element type differs (char8_t vs char — both
        // render to C# byte), carried for sizeof / _Generic fidelity.
        var segs = CollectWideStrSegments(((C.U8str)it.Content).Arg0);
        DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
        return new LitStr(segs) { Type = new CType.Array(CType.Char8, byteLen) };
    }

    private CExpr BuildU16Str(Item it)
    {
        // u"…" — a char16_t string literal: type char16_t[N] (N = code units incl.
        // NUL), decaying to char16_t* in use (like LitStr/char[N]). Element count is
        // the UTF-16 code-unit count + 1; the backend re-decodes for the literal text.
        var segs = CollectWideStrSegments(((C.U16str)it.Content).Arg0);
        var units = DotCC.EmitHelpers.StringU16Values(segs);
        return new LitU16Str(segs) { Type = new CType.Array(CType.Char16, units.Count + 1) };
    }

    private CExpr BuildWStr(Item it)
    {
        // L"…" — a wchar_t string literal. dotcc's wchar_t is the MSVC-shaped 16-bit
        // UTF-16 type, so this lowers IDENTICALLY to u"…" (UTF-16 code units, pooled
        // `Libc.L16`), reusing the LitU16Str node and the shared U16 decoder; only
        // the element type differs (wchar_t vs char16_t — both render to C# char).
        var segs = CollectWideStrSegments(((C.Wstr)it.Content).Arg0);
        var units = DotCC.EmitHelpers.StringU16Values(segs);
        return new LitU16Str(segs) { Type = new CType.Array(CType.WChar, units.Count + 1) };
    }

    private CExpr BuildU32Str(Item it)
    {
        // U"…" — a char32_t string literal: type char32_t[N] (N = UTF-32 code units
        // incl. NUL), decaying to char32_t* in use. One code unit per Unicode scalar
        // (StringU32Values folds UTF-16 surrogate pairs), so an astral char counts as
        // ONE element — unlike the u"…" path's two. The backend re-encodes via L32.
        var segs = CollectWideStrSegments(((C.U32str)it.Content).Arg0);
        var units = DotCC.EmitHelpers.StringU32Values(segs);
        return new LitU32Str(segs) { Type = new CType.Array(CType.Char32, units.Count + 1) };
    }

    /// <summary>Collect adjacent string-literal segments (raw quoted lexemes) of a
    /// <c>StringSeq</c>, in source order.</summary>
    private List<string> CollectStrSegments(Item strSeq)
    {
        var segs = new List<string>();
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.StrSeqCons sc: Walk(sc.Arg0); segs.Add(Tok(sc.Arg1)); break;
                case C.StrSeqOne so: segs.Add(Tok(so.Arg0)); break;
                default: segs.Add(Tok(node)); break;
            }
        }
        Walk(strSeq);
        return segs;
    }

    /// <summary>Collect adjacent wide string-literal segments of a
    /// <c>U16StringSeq</c> (<c>u"…"</c>), a <c>WStringSeq</c> (<c>L"…"</c>), or a
    /// <c>U32StringSeq</c> (<c>U"…"</c>), in source order, with the encoding prefix
    /// (<c>u</c>/<c>U</c>/<c>L</c>) stripped so each is a plain quoted lexeme the
    /// shared decoders (<c>StringU16Values</c> / <c>StringU32Values</c>) accept
    /// unchanged. char16_t and dotcc's MSVC-shaped 16-bit wchar_t decode identically
    /// (16-bit); char32_t decodes 32-bit — the caller picks by element type.</summary>
    private List<string> CollectWideStrSegments(Item strSeq)
    {
        var segs = new List<string>();
        // Strip the encoding prefix so each segment is a plain quoted lexeme. `u8` is
        // TWO chars (C23 char8_t); u / U / L are one. Everything after is `"…"`.
        static string StripPrefix(string raw) =>
            raw.Length >= 2 && raw[0] == 'u' && raw[1] == '8' ? raw[2..]
            : raw.Length >= 1 && raw[0] is 'u' or 'U' or 'L' ? raw[1..]
            : raw;
        void Walk(Item node)
        {
            switch (node.Content)
            {
                case C.U16strSeqCons sc: Walk(sc.Arg0); segs.Add(StripPrefix(Tok(sc.Arg1))); break;
                case C.U16strSeqOne so: segs.Add(StripPrefix(Tok(so.Arg0))); break;
                case C.WstrSeqCons sc: Walk(sc.Arg0); segs.Add(StripPrefix(Tok(sc.Arg1))); break;
                case C.WstrSeqOne so: segs.Add(StripPrefix(Tok(so.Arg0))); break;
                case C.U32strSeqCons sc: Walk(sc.Arg0); segs.Add(StripPrefix(Tok(sc.Arg1))); break;
                case C.U32strSeqOne so: segs.Add(StripPrefix(Tok(so.Arg0))); break;
                case C.U8strSeqCons sc: Walk(sc.Arg0); segs.Add(StripPrefix(Tok(sc.Arg1))); break;
                case C.U8strSeqOne so: segs.Add(StripPrefix(Tok(so.Arg0))); break;
                default: segs.Add(StripPrefix(Tok(node))); break;
            }
        }
        Walk(strSeq);
        return segs;
    }

    private CExpr BuildChr(C.Chr c)
    {
        // A C character constant has type int; emit its integer value (the simplest
        // C-faithful lowering — `'A'` → `65`, `'\n'` → `10`). The value carries into
        // byte/int sinks via C#'s constant conversions.
        var raw = Tok(c.Arg0);
        if (raw is null || raw.Length < 3) { return new LitInt("0", 0) { Type = CType.Int }; }
        var units = CharConstantUnits(raw[1..^1]);
        var value = units.Count == 1 ? DecodeCharConstant(units[0]) : MultiCharValue(units, SrcPos.From(c.Arg0));
        return new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) { Type = CType.Int };
    }

    /// <summary>The characters of a character constant's body, each a plain char or
    /// an escape sequence (<c>\n</c>, <c>\x41</c>, <c>\101</c>).</summary>
    private static List<string> CharConstantUnits(string inner)
    {
        var units = new List<string>();
        for (var i = 0; i < inner.Length;)
        {
            var start = i;
            if (inner[i] != '\\' || i + 1 >= inner.Length) { i++; }
            else if (inner[i + 1] == 'x')
            {
                i += 2;
                while (i < inner.Length && char.IsAsciiHexDigit(inner[i])) { i++; }
            }
            else if (inner[i + 1] is >= '0' and <= '7')
            {
                i += 2;
                while (i < inner.Length && i - start < 4 && inner[i] is >= '0' and <= '7') { i++; }
            }
            else { i += 2; }
            units.Add(inner[start..i]);
        }
        return units;
    }

    /// <summary>A multi-character constant's value as gcc computes it (C11 6.4.4.4p10
    /// leaves it implementation-defined): each character's byte shifted in from the
    /// right, keeping the last four, with gcc's warnings.</summary>
    private int MultiCharValue(List<string> units, SrcPos pos)
    {
        Diagnostics.Add(new Diagnostic(Severity.Warning, "multi-character character constant [-Wmultichar]", pos, _file));
        if (units.Count > 4)
        {
            Diagnostics.Add(new Diagnostic(Severity.Warning, "character constant too long for its type", pos, _file));
        }
        var value = 0;
        foreach (var u in units) { value = unchecked((value << 8) | (DecodeCharConstant(u) & 0xFF)); }
        return value;
    }

    private CExpr BuildU16Chr(C.U16chr c)
    {
        // u'x' — a C11 char16_t character constant. Unlike a plain char constant
        // (type int), this has type char16_t (→ C# char). Decode the value the same
        // way (DecodeCharConstant keeps full 16-bit \x / octal), tag it char16_t.
        var raw = Tok(c.Arg0);   // u'x'
        var lit0 = new LitInt("0", 0) { Type = CType.Int };
        if (raw is null || raw.Length < 4) { return new Cast(CType.Char16, lit0) { Type = CType.Char16 }; }
        var value = DecodeCharConstant(raw[2..^1]);   // strip the `u'` prefix and `'`
        // Wrap in an explicit (char16_t)→C# (char) cast: a bare integer literal is a
        // C# int, and C# has no implicit int→char conversion (even for constants), so
        // the value must be cast — and an int inner literal avoids the `u` suffix a
        // char16_t-typed literal would otherwise pick up (→ uint, also not char-convertible).
        var inner = new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) { Type = CType.Int };
        return new Cast(CType.Char16, inner) { Type = CType.Char16 };
    }

    private CExpr BuildWChr(C.Wchr c)
    {
        // L'x' — a wchar_t character constant (type wchar_t, not int). Same 16-bit
        // decode + explicit-(char)-cast lowering as u'x' (see BuildU16Chr), just
        // tagged wchar_t instead of char16_t (both render to C# char).
        var raw = Tok(c.Arg0);   // L'x'
        var lit0 = new LitInt("0", 0) { Type = CType.Int };
        if (raw is null || raw.Length < 4) { return new Cast(CType.WChar, lit0) { Type = CType.WChar }; }
        var value = DecodeCharConstant(raw[2..^1]);   // strip the `L'` prefix and `'`
        var inner = new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) { Type = CType.Int };
        return new Cast(CType.WChar, inner) { Type = CType.WChar };
    }

    private CExpr BuildU32Chr(C.U32chr c)
    {
        // U'x' — a C11 char32_t character constant (type char32_t → C# uint, not int).
        // Same decode + explicit-cast lowering as u'x' (see BuildU16Chr): a bare int
        // inner literal wrapped in a (char32_t)→(uint) cast tags the value's type. C#
        // DOES allow a constant int→uint conversion, so the backend may elide the cast
        // when the value fits — the Cast's Type keeps char32_t fidelity either way.
        var raw = Tok(c.Arg0);   // U'x'
        var lit0 = new LitInt("0", 0) { Type = CType.Int };
        if (raw is null || raw.Length < 4) { return new Cast(CType.Char32, lit0) { Type = CType.Char32 }; }
        var value = DecodeCharConstant(raw[2..^1]);   // strip the `U'` prefix and `'`
        var inner = new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) { Type = CType.Int };
        return new Cast(CType.Char32, inner) { Type = CType.Char32 };
    }

    private CExpr BuildU8Chr(C.U8chr c)
    {
        // u8'x' — a C23 char8_t character constant (type char8_t → C# byte, a single
        // UTF-8 code unit). Same (char8_t)-cast-of-an-int-literal lowering as u'x' /
        // U'x' (see BuildU16Chr): the inner literal MUST stay int, because a byte-typed
        // literal would render with a `u` suffix (uint), and C# has NO implicit
        // uint-constant→byte conversion (only int/long constants convert) → CS0266.
        var raw = Tok(c.Arg0);   // u8'x'
        var lit0 = new LitInt("0", 0) { Type = CType.Int };
        if (raw is null || raw.Length < 5) { return new Cast(CType.Char8, lit0) { Type = CType.Char8 }; }
        var value = DecodeCharConstant(raw[3..^1]);   // strip the `u8'` prefix and `'`
        var inner = new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) { Type = CType.Int };
        return new Cast(CType.Char8, inner) { Type = CType.Char8 };
    }

    /// <summary>Decode the body of a C character constant (the chars between the
    /// quotes) to its integer value: a single char, a named escape, a
    /// <c>\xHH</c> hex escape, or a <c>\NNN</c> octal escape.</summary>
    private static int DecodeCharConstant(string inner)
    {
        if (inner.Length == 0) { return 0; }
        if (inner[0] != '\\') { return inner[0]; }
        var esc = inner[1];
        switch (esc)
        {
            case 'n': return 10;
            case 't': return 9;
            case 'r': return 13;
            case 'a': return 7;
            case 'b': return 8;
            case 'f': return 12;
            case 'v': return 11;
            case '0' when inner.Length == 2: return 0;
            case '\\': return 92;
            case '\'': return 39;
            case '"': return 34;
            case '?': return 63;
            case 'x':
                return Convert.ToInt32(inner[2..], 16);
            case >= '0' and <= '7':
                return Convert.ToInt32(inner[1..], 8);
            default:
                throw new IrUnsupportedException("char literal '" + inner + "'");
        }
    }

    private CExpr BuildNum(C.Num n)
    {
        var raw = Tok(n.Arg0);
        if (raw.Contains('\'')) { Gate(2023, "digit separator", n.Arg0); }
        if (raw.Length >= 2 && raw[0] == '0' && raw[1] is 'b' or 'B') { Gate(2023, "binary integer literal", n.Arg0); }
        // Strip C integer suffix (u/U/l/L).
        var end = raw.Length;
        int ls = 0; var hasU = false;
        while (end > 0 && raw[end - 1] is 'u' or 'U' or 'l' or 'L')
        {
            if (raw[end - 1] is 'l' or 'L') { ls++; } else { hasU = true; }
            end--;
        }
        var digits = raw[..end].Replace("'", "");
        // The literal's numeric CORE (suffix-free); the backend re-adds a target
        // suffix from the CType derived below. Octal normalises to decimal here.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // `mag` is the constant's unsigned 64-bit magnitude, used to pick its type per
        // C99 6.4.4.1 (the first candidate type that can represent it). `val` is the
        // signed-long view used by constant folding, left null when it doesn't fit.
        string text; long? val = null; ulong mag = 0; bool magOk = false; var isDecimal = false;
        if (digits.Length >= 2 && digits[0] == '0' && digits[1] is 'x' or 'X')
        {
            text = digits;
            magOk = ulong.TryParse(digits[2..], System.Globalization.NumberStyles.HexNumber, inv, out mag);
            if (long.TryParse(digits[2..], System.Globalization.NumberStyles.HexNumber, inv, out var hv)) { val = hv; }
        }
        else if (digits.Length >= 2 && digits[0] == '0' && digits[1] is 'b' or 'B')
        {
            text = digits;
            try { mag = Convert.ToUInt64(digits[2..], 2); magOk = true; } catch { }
            if (magOk && mag <= long.MaxValue) { val = (long)mag; }
        }
        else if (digits.Length >= 2 && digits[0] == '0')
        {
            // Octal. Validate each digit (C rejects `08`/`09`); Convert.ToUInt64
            // base 8 would otherwise throw a raw FormatException.
            foreach (var c in digits)
            {
                if (c is < '0' or > '7') { throw new DotCC.CompileException($"invalid digit '{c}' in octal constant '{raw}'"); }
            }
            mag = Convert.ToUInt64(digits, 8); magOk = true;
            text = mag.ToString(inv);
            if (mag <= long.MaxValue) { val = (long)mag; }
        }
        else
        {
            text = digits;
            isDecimal = true;
            magOk = ulong.TryParse(digits, System.Globalization.NumberStyles.None, inv, out mag);
            if (long.TryParse(digits, inv, out var dv)) { val = dv; }
        }
        var ct = SelectIntType(isDecimal, hasU, ls > 0, mag, magOk);
        return new LitInt(text, val) { Type = ct };
    }

    /// <summary>The type of an integer constant per C99 6.4.4.1: the first type in
    /// the constant's candidate list that can represent its <paramref name="mag"/>.
    /// dotcc's model has only <c>int</c>/<c>unsigned</c> (32-bit) and
    /// <c>long</c>/<c>unsigned long</c> (64-bit, also covering <c>long long</c>), so
    /// the standard's lists collapse to those four. The key subtlety the
    /// suffix-only choice missed: a value too big for <c>int</c> climbs to a wider
    /// type — and a <em>hex/octal</em> constant may pick an unsigned type even with
    /// no <c>u</c> suffix (a decimal one may not). If the magnitude couldn't be
    /// parsed (overflow past 64 bits), fall back to the suffix-only choice.</summary>
    private static CType SelectIntType(bool decimalBase, bool hasU, bool hasL, ulong mag, bool magOk)
    {
        if (!magOk)
        {
            return hasL ? (hasU ? CType.ULong : CType.Long) : (hasU ? CType.UInt : CType.Int);
        }
        var fitsInt = mag <= int.MaxValue;
        var fitsUInt = mag <= uint.MaxValue;
        var fitsLong = mag <= long.MaxValue;

        if (hasU)
        {
            return !hasL && fitsUInt ? CType.UInt : CType.ULong;
        }
        if (hasL)
        {
            // Candidates: long, [unsigned long for hex/octal]. A signed-long value
            // stays long; anything larger needs the unsigned 64-bit type.
            return fitsLong ? CType.Long : CType.ULong;
        }
        if (decimalBase)
        {
            // Decimal, unsuffixed: int → long → (no further signed type). Past
            // long.MaxValue C has no valid type; be lenient like gcc (unsigned long).
            return fitsInt ? CType.Int : fitsLong ? CType.Long : CType.ULong;
        }
        // Hex/octal/binary, unsuffixed: unsigned types are candidates too.
        return fitsInt ? CType.Int : fitsUInt ? CType.UInt : fitsLong ? CType.Long : CType.ULong;
    }

    /// <summary>Build a floating-point literal, gating the C99 hex-float form
    /// (<c>0x1.8p3</c>) under an older -std=.</summary>
    private CExpr BuildFloat(C.Flt f)
    {
        var raw = Tok(f.Arg0);
        if (raw.Length >= 2 && raw[0] == '0' && raw[1] is 'x' or 'X') { Gate(1999, "hex float literal", f.Arg0); }
        // §6.4.4.2: f/F suffix → float; otherwise double (long double IS double here).
        var type = raw.Length > 0 && raw[^1] is 'f' or 'F' ? CType.Float : CType.Double;
        return new LitFloat(LowerFloat(raw)) { Type = type };
    }

    private static string LowerFloat(string raw)
    {
        // C99 hex float literal (`0x1.8p3`, value = mantissa * 2^exp). C# has no
        // hex-float syntax, so parse the value and emit a round-trippable decimal
        // (shared with the Zig front-end via EmitHelpers — single source of truth).
        if (raw.Length > 2 && raw[0] == '0' && raw[1] is 'x' or 'X')
        {
            return DotCC.EmitHelpers.LowerHexFloat(raw);
        }
        var last = raw.Length > 0 ? raw[^1] : '\0';
        if (last is 'f' or 'F') { return raw; }          // C# accepts the f suffix
        if (last is 'l' or 'L') { return raw[..^1]; }    // long double → double, drop L
        return raw;
    }

    // ---- helpers ---------------------------------------------------------

    private static string Tok(Item it) => it.Content as string
        ?? throw new IrUnsupportedException("expected terminal, got " + TypeName(it.Content));

    private static string TypeName(object? content) =>
        content is null ? "<null>" : content.GetType().Name;
}
