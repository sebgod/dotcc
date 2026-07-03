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
public sealed class IrUnsupportedException : DotCC.CompileException
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
    private readonly SymbolTable _symbols;
    // Typedef name → its underlying type. Unlike the legacy emitter (which emits
    // `using` aliases and resolves names textually), the IR resolves a typedef
    // name straight to its CType — so `size_t x` becomes `ulong x` with the right
    // SizeOf, no alias directive needed. Populated in declaration order, so a
    // chained typedef (`typedef size_t mysize;`) resolves through the table.
    private readonly Dictionary<string, CType> _typedefs = new(StringComparer.Ordinal);
    // Struct/union name → its fields, so member access can resolve a field's
    // type. Keyed by the canonical (tag, or anonymous-typedef alias) name.
    private readonly Dictionary<string, List<StructField>> _structFields = new(StringComparer.Ordinal);
    // Whether each registered aggregate is a union — drives the compile-time layout
    // model (offsetof folding) and is set alongside every _structFields entry.
    private readonly Dictionary<string, bool> _structIsUnion = new(StringComparer.Ordinal);
    // Byte-packed structs (Zig `packed struct`): the compile-time layout model drops
    // inter-field padding + aligns to 1 for these, so `@sizeOf`/`offsetof` match the
    // emitted [StructLayout(Sequential, Pack=1)] runtime layout. (Zig front-end only.)
    private readonly HashSet<string> _packedStructs = new(StringComparer.Ordinal);
    // Enum tag → its resolved CType.Enum, so `enum Tag` as a type resolves to the
    // real enum (not plain int). Anonymous-but-typedef'd enums are reached through
    // _typedefs instead (the alias maps to the same CType.Enum).
    private readonly Dictionary<string, CType.Enum> _enumTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emittedTypes = new(StringComparer.Ordinal);
    private string _file = "";
    // The name of the function currently being built — the value of the C99
    // predefined identifier `__func__` inside its body.
    private string _currentFnName = "";
    // The declared return type of the function currently being built — lets a
    // `return <const T*>` from a `T*`-returning function trip the const-discard check.
    private CType? _currentRet;

    public List<FuncDef> Functions { get; } = new();
    public List<GlobalVar> Globals { get; } = new();
    public List<StructTypeDef> Types { get; } = new();
    public List<EnumTypeDef> Enums { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();

    /// <summary>The Zig front-end's flat error set: each <c>error.Foo</c> name → its stable
    /// <c>ushort</c> code (1-based). Populated by <c>ZigFrontend.AddUnits</c> after lowering all
    /// units; consumed by the backend to emit the <c>__zigErrorName</c> code→name table that
    /// backs <c>@errorName</c> (Milestone X, part 1). Null/empty for a C-only program or a Zig
    /// program that never names an error.</summary>
    public IReadOnlyDictionary<string, int>? ZigErrorCodes { get; set; }

    /// <summary>
    /// Functions a native import (`-l`) library must resolve: declared by prototype,
    /// defined in NO translation unit, actually called, and NOT from a synthetic
    /// system header (those are runtime-provided via <c>using static Libc</c>, flagged
    /// by the reserved line band). Variadic candidates ARE included — the emit pass
    /// (<c>Compiler.ComputeImportCandidates</c>) warns and skips them, since a varargs
    /// signature can't become a function pointer. Empty unless the program calls an
    /// undefined non-system prototype; computed on demand (cheap, post-build).
    /// </summary>
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
    public void AddUnit(Item root, string file)
    {
        _file = file;
        FlattenFns(root, BuildTopLevel);
    }

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
            case C.FuncDef d: BuildFuncDef(d.Arg0, d.Arg1); break;
            case C.ExternFnDef d: BuildFuncDef(d.Arg1, d.Arg2); break;
            case C.FuncProto p: RegisterProto(p.Arg0); break;
            case C.ExternFnProto p: RegisterProto(p.Arg1); break;
            // A header-defined file-scope variable (chibi sexp.h's `static const
            // unsigned char sexp_uvector_sizes[] = {…};`) re-arrives once per TU
            // that includes the header. An identical re-definition is the same
            // object — the first build's field + file-scope binding serve every
            // TU, so skip it (mirrors BuildFuncDef's static-inline dedup; see
            // AlreadySeenTopLevel for the per-TU-state caveat).
            case C.GlobalDeclList or C.GlobalStaticDeclList
                or C.GlobalArr or C.GlobalStaticArr
                or C.GlobalArrInit or C.GlobalStaticArrInit
                or C.GlobalArrInitImplicit or C.GlobalStaticArrInitImplicit
                or C.GlobalCharArrStr or C.GlobalCharArrStrSized
                or C.GlobalStaticCharArrStr or C.GlobalStaticCharArrStrSized
                or C.GlobalU16CharArrStr or C.GlobalU16CharArrStrSized
                or C.GlobalStaticU16CharArrStr or C.GlobalStaticU16CharArrStrSized
                or C.GlobalWCharArrStr or C.GlobalWCharArrStrSized
                or C.GlobalStaticWCharArrStr or C.GlobalStaticWCharArrStrSized
                or C.GlobalU32CharArrStr or C.GlobalU32CharArrStrSized
                or C.GlobalStaticU32CharArrStr or C.GlobalStaticU32CharArrStrSized
                or C.GlobalU8CharArrStr or C.GlobalU8CharArrStrSized
                or C.GlobalStaticU8CharArrStr or C.GlobalStaticU8CharArrStrSized
                or C.GlobalStaticStructInit when AlreadySeenTopLevel(fn):
                break;
            case C.GlobalDeclList g: BuildGlobalDecls(g.Arg0, g.Arg1, Storage.Static); break;
            case C.GlobalStaticDeclList g: BuildGlobalDecls(g.Arg1, g.Arg2, Storage.Static); break;
            // File-scope arrays — pinned global backing store (plain and `static`
            // lower identically; internal linkage is a no-op for a never-exported
            // variable). Sized, brace-initialized, and implicit-`[]` forms.
            case C.GlobalArr g: BuildGlobalArr(g.Arg0, g.Arg1, g.Arg2, null, null); break;
            case C.GlobalStaticArr g: BuildGlobalArr(g.Arg1, g.Arg2, g.Arg3, null, null); break;
            case C.GlobalArrInit g: BuildGlobalArr(g.Arg0, g.Arg1, g.Arg2, g.Arg5, null); break;
            case C.GlobalStaticArrInit g: BuildGlobalArr(g.Arg1, g.Arg2, g.Arg3, g.Arg6, null); break;
            case C.GlobalArrInitImplicit g: BuildGlobalArr(g.Arg0, g.Arg1, null, g.Arg6, null); break;
            case C.GlobalStaticArrInitImplicit g: BuildGlobalArr(g.Arg1, g.Arg2, null, g.Arg7, null); break;
            // File-scope char arrays from a string literal.
            case C.GlobalCharArrStr g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg5, null, null); break;
            case C.GlobalCharArrStrSized g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg4, g.Arg2, null); break;
            case C.GlobalStaticCharArrStr g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg6, null, null); break;
            case C.GlobalStaticCharArrStrSized g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg5, g.Arg3, null); break;
            // char16_t file-scope arrays from a u"…" literal (same shapes, u16 decode).
            case C.GlobalU16CharArrStr g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg5, null, null, wide: true); break;
            case C.GlobalU16CharArrStrSized g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg4, g.Arg2, null, wide: true); break;
            case C.GlobalStaticU16CharArrStr g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg6, null, null, wide: true); break;
            case C.GlobalStaticU16CharArrStrSized g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg5, g.Arg3, null, wide: true); break;
            // wchar_t file-scope arrays from an L"…" literal (same shapes, 16-bit decode).
            case C.GlobalWCharArrStr g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg5, null, null, wide: true); break;
            case C.GlobalWCharArrStrSized g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg4, g.Arg2, null, wide: true); break;
            case C.GlobalStaticWCharArrStr g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg6, null, null, wide: true); break;
            case C.GlobalStaticWCharArrStrSized g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg5, g.Arg3, null, wide: true); break;
            // char32_t file-scope arrays from a U"…" literal (same shapes, 32-bit decode).
            case C.GlobalU32CharArrStr g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg5, null, null, wide: true); break;
            case C.GlobalU32CharArrStrSized g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg4, g.Arg2, null, wide: true); break;
            case C.GlobalStaticU32CharArrStr g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg6, null, null, wide: true); break;
            case C.GlobalStaticU32CharArrStrSized g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg5, g.Arg3, null, wide: true); break;
            // char8_t file-scope arrays from a u8"…" literal (same shapes, UTF-8 byte decode).
            case C.GlobalU8CharArrStr g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg5, null, null, wide: true); break;
            case C.GlobalU8CharArrStrSized g: BuildGlobalCharArr(g.Arg0, g.Arg1, g.Arg4, g.Arg2, null, wide: true); break;
            case C.GlobalStaticU8CharArrStr g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg6, null, null, wide: true); break;
            case C.GlobalStaticU8CharArrStrSized g: BuildGlobalCharArr(g.Arg1, g.Arg2, g.Arg5, g.Arg3, null, wide: true); break;
            // `extern T a[N];` / `extern T a[];` — declaration only (storage elsewhere).
            case C.ExternArr g: BuildExternArr(g.Arg1, g.Arg2, g.Arg3); break;
            case C.ExternArrIncomplete g: BuildExternArr(g.Arg1, g.Arg2, null); break;
            // `static T x = { … };` at file scope — a once-initialised struct/union field.
            case C.GlobalStaticStructInit g: BuildGlobalStructInit(g.Arg1, g.Arg2, g.Arg5); break;
            // `extern T x;` declares the name + type for resolution but emits no
            // field — the definition lives in another TU (dotcc whole-program model).
            case C.ExternVarDecl g: BuildGlobalDecls(g.Arg1, g.Arg2, Storage.Extern); break;
            // `typedef <type> <name>;` — record name → underlying type. Resolution
            // (ResolveType's TypeName case) then sees through it everywhere.
            case C.TypedefAlias t: _typedefs[Tok(t.Arg2)] = ResolveType(t.Arg1); break;
            // `typedef T Name[N];` — the alias IS an array type. Bounds must be
            // constant (same rule as struct array members); the registered
            // CType.Array drives every use site: member → fixed buffer, param →
            // pointer decay, sizeof → N*sizeof(T), local decl → stackalloc.
            case C.TypedefArr t:
                _typedefs[Tok(t.Arg2)] = MakeArrayType(
                    ResolveType(t.Arg1),
                    TryConstDims(t.Arg3) ?? throw new IrUnsupportedException("non-constant array typedef bound"));
                break;
            // enum definitions — register a real C# enum (tagged/typedef'd) or, for
            // an anonymous un-typedef'd enum, plain int constants.
            case C.EnumDef e: RegisterEnum(Tok(e.Arg1), null, e.Arg3); break;
            case C.EnumDefTyped e: RegisterEnum(Tok(e.Arg1), e.Arg3, e.Arg5); break;
            case C.TypedefEnum e: _typedefs[Tok(e.Arg6)] = RegisterEnum(Tok(e.Arg2), null, e.Arg4, Tok(e.Arg6)); break;
            case C.TypedefEnumAnon e: _typedefs[Tok(e.Arg5)] = RegisterEnum(null, null, e.Arg3, Tok(e.Arg5)); break;
            // struct/union definitions.
            case C.StructDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: false); break;
            case C.UnionDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: true); break;
            case C.TypedefStruct s: BuildStructDef(Tok(s.Arg2), s.Arg4, Tok(s.Arg6), isUnion: false); break;
            case C.TypedefUnion s: BuildStructDef(Tok(s.Arg2), s.Arg4, Tok(s.Arg6), isUnion: true); break;
            case C.TypedefStructAnon s: BuildStructDef(null, s.Arg3, Tok(s.Arg5), isUnion: false); break;
            case C.TypedefUnionAnon s: BuildStructDef(null, s.Arg3, Tok(s.Arg5), isUnion: true); break;
            // `struct Tag;` forward declaration — C# resolves order-independently.
            case C.StructFwd: break;
            // `_Static_assert(expr[, "msg"]);` at file scope — a compile-time-only
            // assertion, EVALUATED here (C11 §6.7.10) via the unified comptime
            // interpreter. A holding assertion emits nothing; a zero or non-constant
            // controlling expression is a collected compile error. The message-less
            // arity gates C23 (it postdates the two-arg C11 form).
            case C.StaticAssert sa: Gate(2011, "_Static_assert", fn); CheckStaticAssert(sa.Arg2, sa.Arg4, SrcPos.From(fn)); break;
            case C.StaticAssertNoMsg sa: Gate(2023, "_Static_assert with no message", fn); CheckStaticAssert(sa.Arg2, null, SrcPos.From(fn)); break;
            // `typedef Ret (*Name)(params);` — record Name → fn-ptr type.
            case C.TypedefFnPtr t: _typedefs[Tok(t.Arg4)] = FnPtrType(t.Arg1, t.Arg7); break;
            case C.TypedefFnPtrNoArgs t: _typedefs[Tok(t.Arg4)] = FnPtrType(t.Arg1, null); break;
            default: throw new IrUnsupportedException(TypeName(fn.Content));
        }
    }

    // Structural fingerprints of every file-scope variable definition built so
    // far — the global-side twin of _fnDefSites (which needs per-site symbols;
    // globals don't, because their file-scope binding persists across TUs).
    private readonly HashSet<string> _seenTopLevelDefs = new(StringComparer.Ordinal);

    /// <summary>True when an identical file-scope variable definition was already
    /// built (same position-free structural dump = same post-expansion tokens,
    /// i.e. the same header re-included by another TU). Caveat: C would give each
    /// TU its OWN copy of a header-defined MUTABLE <c>static</c> variable; dotcc
    /// merges them into one field. For the idiom that actually occurs (header
    /// <c>static const</c> tables) the two are indistinguishable.</summary>
    private bool AlreadySeenTopLevel(Item fn) => !_seenTopLevelDefs.Add(fn.ToString());

    /// <summary>File-scope variable declaration. Each declarator becomes a
    /// <c>DotCcGlobals</c> field (codegen emits <c>public static unsafe T name</c>);
    /// an <c>extern</c> one is registered for resolution only (no field).</summary>
    private void BuildGlobalDecls(Item typeItem, Item listItem, Storage storage)
    {
        _sawThreadLocalSpec = false; // consumed below: set by THIS declaration's spec resolution
        _sawConstexprSpec = false;   // same discipline
        WalkDeclList(typeItem, listItem, (name, initItem, type) =>
        {
            // A file-scope array has its own productions (pinned GlobalArray
            // lowering); an array TAIL here would silently become a plain field.
            if (type.Unqualified is CType.Array)
            {
                throw new IrUnsupportedException("array declarator in a file-scope multi-declarator list (split it into its own declaration)");
            }
            var sym = _symbols.Declare(new Symbol
            {
                Name = name, Kind = SymKind.Var, Storage = storage, IsGlobal = true,
                // A constexpr object is const-qualified (C23 §6.7.1p5 implies it),
                // so the standard write-to-const error covers assignments.
                Type = _sawConstexprSpec ? type.WithQuals(TypeQual.Const) : type,
                IsThreadLocal = _sawThreadLocalSpec,
                IsConstexpr = _sawConstexprSpec,
            });
            if (storage != Storage.Extern)
            {
                _definedGlobalNames.Add(name); // a real definition — satisfies any extern decl
                CExpr? gInit = null;
                if (initItem is { } ii) { gInit = BuildExpr(ii); EnsureNotEmbed(gInit); CheckQualifierDiscard(gInit, sym.Type, SrcPos.From(ii), "initialization"); }
                // A .NET [ThreadStatic] initializer runs on the FIRST thread only,
                // so C's "every thread starts at the initial value" holds only for
                // the zero/default value .NET gives every thread's slot anyway.
                if (sym.IsThreadLocal && gInit is not null && ConstEval(gInit) is not 0)
                {
                    Diagnostics.Add(new Diagnostic(Severity.Error,
                        $"'{name}': a non-zero-initialized _Thread_local is not supported (a .NET [ThreadStatic] initializer runs only on the first thread)",
                        SrcPos.From(typeItem), _file));
                }
                if (sym.IsConstexpr) { BindConstexpr(sym, gInit, SrcPos.From(typeItem)); }
                Globals.Add(new GlobalVar(sym, gInit));
            }
        });
    }

    /// <summary>A prototype declares the function (so calls resolve + we know its
    /// signature) but emits no body.</summary>
    // ---- function markers (inline / noreturn / deprecated) ----------------
    // _sawNoreturnSpec/_sawInlineSpec: the signature's spec resolution saw the
    // `_Noreturn` (C11; the C23 lowercase `noreturn` arrives pre-promoted onto the
    // same terminal) / `inline` (C99) function specifier — reset by
    // RegisterProto/BuildFuncDef immediately before ExtractFnSig so only THIS
    // declaration's specifiers count. _pendingAttrNoreturn/_pendingAttrDeprecated:
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

    /// <summary>Record the function/storage specifiers a resolved spec run may
    /// carry — `_Noreturn` (gated C11) and `inline` for the enclosing function
    /// symbol, `_Thread_local` (gated C11) for the enclosing file-scope variable
    /// declaration. Shared by <see cref="ResolveSpecs"/> and
    /// <see cref="SpecsThenName"/> so both the spec-multiset and the
    /// typedef-name routes behave identically. A block-scope `_Thread_local`
    /// (even `static _Thread_local`, which C allows) is a loud V1 rejection —
    /// dotcc lowers thread-locals as file-scope [ThreadStatic] fields only.</summary>
    private void RecordDeclSpecs(List<string> specs, SrcPos pos)
    {
        if (specs.Contains("_Noreturn")) { Gate(2011, "_Noreturn", pos); _sawNoreturnSpec = true; }
        if (specs.Contains("inline")) { _sawInlineSpec = true; } // no gate — pre-C99 rejection is structural (rule 2)
        if (specs.Contains("_Thread_local"))
        {
            Gate(2011, "_Thread_local", pos);
            if (_symbols.AtFileScope) { _sawThreadLocalSpec = true; }
            else
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    "'_Thread_local' at block scope is not supported (dotcc lowers thread-locals as file-scope [ThreadStatic] fields only)",
                    pos, _file));
            }
        }
        if (specs.Contains("constexpr"))
        {
            // No Gate: the spelling only becomes a keyword via rule-2 promotion
            // under -std=c23, so a pre-C23 dialect rejects structurally (the ID
            // never reaches here). C23 §6.7.1p5 forbids combining with
            // _Thread_local.
            if (specs.Contains("_Thread_local"))
            {
                Diagnostics.Add(new Diagnostic(Severity.Error,
                    "'constexpr' may not be used with '_Thread_local'", pos, _file));
            }
            _sawConstexprSpec = true;
        }
    }

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

    /// <summary>Walk an <c>AttrList</c> collecting the attributes dotcc lowers:
    /// `noreturn` (a bare ID pre-C23; the promoted `_Noreturn` keyword under c23)
    /// and `deprecated` (bare or with a string-literal message). Everything else —
    /// `nodiscard`, `maybe_unused`, `fallthrough`, vendor-namespaced attrs — is
    /// accepted and ignored (no .NET counterpart with teeth: C# doesn't warn on
    /// unused locals/params, and the BCL has no must-use-result attribute).</summary>
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

    private void RegisterProto(Item fnSig)
    {
        _sawNoreturnSpec = false;
        _sawInlineSpec = false;
        var sig = ExtractFnSig(fnSig);
        // The reduction's position is its leftmost leaf token (LALR.CC propagates
        // children[0].Position up), and the whole declaration lives in one file —
        // so the band check is reliable without inspecting the name token.
        var sym = DeclareFunc(sig, fromSystemHeader: fnSig.Position.Line >= SrcPos.SyntheticLineBase);
        ApplyFnMarkers(sym);
        // Import-mode candidate tracking: a prototype not (yet) defined in any TU
        // is a potential native `-l` import. A definition seen later retracts it
        // (BuildFuncDef). System-header protos are flagged above and excluded at
        // ProtoOnlyReferenced — they're runtime-provided via `using static Libc`.
        if (!_fnDefSites.ContainsKey(sig.Name)) { _protoOnlyFuncs[sig.Name] = sym; }
    }

    /// <summary>One already-built function DEFINITION: its parse subtrees (retained
    /// so the structural fingerprint is computed lazily — only names that actually
    /// collide across TUs pay for the <c>ToString</c>) and the symbol it bound.</summary>
    private sealed class FnDefSite
    {
        public required Item Sig;
        public required Item Block;
        public required Symbol Sym;
        public string? PrintCache;
        /// <summary>Position-free structural dump of the definition — identical
        /// text ⇔ the same post-expansion tokens, i.e. the same header re-included.</summary>
        public string Print => PrintCache ??= Fingerprint(Sig, Block);
    }

    private static string Fingerprint(Item sig, Item block) => sig.ToString() + "" + block.ToString();

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
    private readonly HashSet<string> _referencedFuncs = new(StringComparer.Ordinal);
    // Extern DATA (`extern int x;`) read/written through its extern symbol, and the
    // names some TU actually DEFINES. An extern referenced but defined nowhere would
    // need a native data import — unsupported in V1 (the emit pass warns). See
    // ExternDataReferenced.
    private readonly HashSet<string> _referencedExternData = new(StringComparer.Ordinal);
    private readonly HashSet<string> _definedGlobalNames = new(StringComparer.Ordinal);

    private void BuildFuncDef(Item fnSig, Item block)
    {
        _sawNoreturnSpec = false;
        _sawInlineSpec = false;
        var sig = ExtractFnSig(fnSig);
        // A definition means this name is no longer a pure prototype → not an import.
        _protoOnlyFuncs.Remove(sig.Name);
        Symbol funcSym;
        if (_fnDefSites.TryGetValue(sig.Name, out var sites))
        {
            var print = Fingerprint(fnSig, block);
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
            sites.Add(new FnDefSite { Sig = fnSig, Block = block, Sym = funcSym, PrintCache = print });
        }
        else
        {
            funcSym = DeclareFunc(sig, fromSystemHeader: fnSig.Position.Line >= SrcPos.SyntheticLineBase);
            _fnDefSites[sig.Name] = new List<FnDefSite> { new() { Sig = fnSig, Block = block, Sym = funcSym } };
        }

        ApplyFnMarkers(funcSym);
        _symbols.BeginFunction();
        _currentFnName = sig.Name; // drives the `__func__` predefined identifier
        _currentRet = (funcSym.Type as CType.Func)?.Return; // drives return const-discard
        _symbols.EnterScope(); // parameter scope
        var paramSyms = new List<Symbol>(sig.Params.Count);
        foreach (var (pType, pName) in sig.Params)
        {
            paramSyms.Add(_symbols.Declare(new Symbol { Name = pName, Kind = SymKind.Param, Type = pType }));
        }
        var body = PromoteMallocs(BuildBlock(block));
        _symbols.ExitScope();

        Functions.Add(new FuncDef(funcSym, paramSyms, body, sig.Variadic));
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
            FromSystemHeader = fromSystemHeader,
        });
    }

    // ---- function signatures --------------------------------------------

    private readonly record struct FnSig(CType Return, string Name, List<ParamInfo> Params, bool Variadic, bool IsStatic);

    private FnSig ExtractFnSig(Item it) => it.Content switch
    {
        C.FnSig n => new(ResolveType(n.Arg0), Tok(n.Arg1), BuildParams(n.Arg3, out var v0), v0, false),
        C.FnSigNoArgs n => new(ResolveType(n.Arg0), Tok(n.Arg1), new(), false, false),
        C.FnSigVoidArgs n => new(ResolveType(n.Arg0), Tok(n.Arg1), new(), false, false),
        C.FnSigStatic n => new(ResolveType(n.Arg1), Tok(n.Arg2), BuildParams(n.Arg4, out var v1), v1, true),
        C.FnSigStaticNoArgs n => new(ResolveType(n.Arg1), Tok(n.Arg2), new(), false, true),
        C.FnSigStaticVoidArgs n => new(ResolveType(n.Arg1), Tok(n.Arg2), new(), false, true),
        // Parenthesized declarator name `T (name)(args)` — identical to
        // `T name(args)`; the parens are pure grouping around the name (public
        // headers wrap API names so a same-named function-like macro can't expand
        // at the declaration). Name is Arg2, the param list Arg5.
        C.FnSigParen n => new(ResolveType(n.Arg0), Tok(n.Arg2), BuildParams(n.Arg5, out var vp), vp, false),
        C.FnSigParenNoArgs n => new(ResolveType(n.Arg0), Tok(n.Arg2), new(), false, false),
        C.FnSigParenVoidArgs n => new(ResolveType(n.Arg0), Tok(n.Arg2), new(), false, false),
        // Function returning a function pointer: `Ret (*name(params))(fnPtrParams)`
        // (e.g. <signal.h>'s `void (*signal(int, void(*)(int)))(int)`). The result
        // type is the function-pointer `Ret (*)(fnPtrParams)`; name + params are the
        // outer declarator's.
        C.FnSigRetFnPtr n => new(FnPtrType(n.Arg0, n.Arg9), Tok(n.Arg3), BuildParams(n.Arg5, out var vr), vr, false),
        C.FnSigRetFnPtrNoArgs n => new(FnPtrType(n.Arg0, null), Tok(n.Arg3), BuildParams(n.Arg5, out var vrn), vrn, false),
        C.FnSigRetFnPtrVoid n => new(FnPtrType(n.Arg0, null), Tok(n.Arg3), BuildParams(n.Arg5, out var vrv), vrv, false),
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
                case C.Param p: acc.Add(new(DecayParam(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                case C.ParamUnnamed p: acc.Add(new(DecayParam(ResolveType(p.Arg0)), "_p" + unnamed++)); break;
                case C.ParamArrayUnsized p: acc.Add(new(new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                case C.ParamArraySized p: acc.Add(new(new CType.Pointer(ResolveType(p.Arg0)), Tok(p.Arg1))); break;
                // Function-pointer parameter: `Ret (*name)(paramTypes)`.
                case C.ParamFnPtr p: acc.Add(new(FnPtrType(p.Arg0, p.Arg6), Tok(p.Arg3))); break;
                case C.ParamFnPtrNoArgs p: acc.Add(new(FnPtrType(p.Arg0, null), Tok(p.Arg3))); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        Walk(paramList);
        variadic = vararg;
        return acc;
    }

    /// <summary>Array-to-pointer decay for a parameter type (C99 §6.7.5.3p7).
    /// Only relevant when the type ARRIVES as an array — i.e. through an
    /// array-typedef alias; the explicit <c>T name[]</c> productions decay at
    /// their own case arms.</summary>
    private static CType DecayParam(CType t)
        => t is CType.Array a ? new CType.Pointer(a.Element) : t;

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
            if (Enums.All(e => e.Name != enumName)) { Enums.Add(new EnumTypeDef(enumName!, underlying, members)); }
        }
        return (CType?)enumType ?? CType.Int;
    }

    /// <summary>The constant byte size of a type — the layout model for a user
    /// aggregate (so the size is exact for an array bound), else the type's own
    /// <see cref="CType.SizeOf"/>.</summary>
    private long? SizeOfConst(CType t) =>
        t.Unqualified is CType.Named n && _structFields.ContainsKey(n.Name) ? Layout(t).Size : t.SizeOf;

    // ---- compile-time C-ABI layout (for offsetof / sizeof folding) --------
    // The .NET blittable layout dotcc emits (sequential structs, explicit unions,
    // natural alignment on this LP64 target) matches the C ABI for the types it
    // models, so size/offset can be computed at compile time — which an array bound
    // like `char padding[offsetof(T, m)]` requires (Lua's alignment-union trick).

    /// <summary>The ABI alignment (in bytes) of a type — the comptime value of Zig's
    /// <c>@alignOf(T)</c> (Milestone T, part 4). A pure compile-time constant on this LP64 target, so
    /// the Zig front-end folds it straight to a literal; surfaced here because <see cref="Layout"/> is
    /// private and the layout model (natural alignment, struct = max field alignment) lives in this
    /// type.</summary>
    internal int AlignOfConst(CType t) => Layout(t).Align;

    /// <summary>The (size, alignment) in bytes of a type under the C ABI / .NET
    /// blittable layout.</summary>
    private (int Size, int Align) Layout(CType t)
    {
        switch (t.Unqualified)
        {
            case CType.Prim p: return (p.Bytes, p.Bytes);
            case CType.Pointer or CType.Func: return (8, 8);
            case CType.Array a:
            {
                var (es, ea) = Layout(a.FlatElement);
                var count = 1;
                for (CType c = a; c is CType.Array ca; c = ca.Element) { count *= ca.Count ?? 0; }
                return (es * count, ea);
            }
            case CType.Named n: return LayoutAggregate(n.Name);
            default: return (0, 1);
        }
    }

    /// <summary>The (size, alignment) of a registered struct/union: sequential
    /// fields each aligned up to their own alignment for a struct; all overlaid at 0
    /// for a union. The total rounds up to the aggregate's alignment.</summary>
    private (int Size, int Align) LayoutAggregate(string name)
    {
        if (!_structFields.TryGetValue(name, out var fields)) { return (0, 1); } // opaque/unknown
        var isUnion = _structIsUnion.GetValueOrDefault(name);
        var packed = _packedStructs.Contains(name);   // byte-packed: no inter-field padding, align 1
        int align = 1, size = 0, off = 0;
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
            if (packed) { fa = 1; }
            if (fa > align) { align = fa; }
            if (isUnion) { if (fs > size) { size = fs; } }
            else { off = RoundUp(off, fa) + fs; }
        }
        return (RoundUp(isUnion ? size : off, align), align);
    }

    /// <summary>The byte offset of a (possibly nested) member-designator within
    /// struct <paramref name="structName"/> — per-level offsets summed, each
    /// intermediate level resolved through its member's type. Null if any level
    /// isn't modelled.</summary>
    private int? OffsetOfConstPath(string structName, IReadOnlyList<string> path)
    {
        var total = 0;
        var current = structName;
        for (var i = 0; i < path.Count; i++)
        {
            var seg = path[i];
            if (OffsetOfConst(current, seg) is not { } off
                || !_structFields.TryGetValue(current, out var fields)) { return null; }
            total += off;
            if (i == path.Count - 1) { break; }   // final segment — no deeper level to name
            CType? segType = null;
            foreach (var f in fields)
            {
                if (f.Name == seg) { segType = f.Type; break; }
            }
            if ((segType?.Unqualified as CType.Named)?.Name is not { } next) { return null; }
            current = next;
        }
        return total;
    }

    /// <summary>The byte offset of <paramref name="member"/> within struct
    /// <paramref name="structName"/> (0 for any union member), or null if unknown.</summary>
    private int? OffsetOfConst(string structName, string member)
    {
        if (!_structFields.TryGetValue(structName, out var fields)) { return null; }
        if (_structIsUnion.GetValueOrDefault(structName)) { return 0; }
        var packed = _packedStructs.Contains(structName);   // byte-packed: no inter-field padding
        var off = 0;
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
            if (!packed) { off = RoundUp(off, fa); }
            if (f.Name == member) { return off; }
            off += fs;
        }
        return null;
    }

    private static int RoundUp(int v, int align) => align <= 1 ? v : (v + align - 1) / align * align;

    // ---- structs / unions ------------------------------------------------

    /// <summary>Define a struct/union. <paramref name="tag"/> is the C tag (null
    /// for an anonymous <c>typedef struct {…} Alias</c>); <paramref name="alias"/>
    /// the typedef name if any. Emits a C# struct under a canonical name, records
    /// its fields for member-type resolution, and maps the typedef alias to it.</summary>
    private void BuildStructDef(string? tag, Item memberList, string? alias, bool isUnion)
    {
        var canonical = tag ?? alias ?? throw new IrUnsupportedException("struct with neither tag nor typedef name");
        var fields = BuildStructFields(memberList, canonical);
        if (_emittedTypes.Add(canonical))
        {
            _structFields[canonical] = fields;
            _structIsUnion[canonical] = isUnion;
            Types.Add(new StructTypeDef(canonical, fields, isUnion));
        }
        // `struct Tag` and the typedef alias both resolve to the canonical type.
        if (alias is not null) { _typedefs[alias] = new CType.Named(canonical); }
    }

    // ---- shared aggregate API for a second frontend (Zig) -----------------
    // The struct/enum field tables (_structFields / _structIsUnion / _enumTypes) are
    // private to the C build, but the registries they feed (Types / Enums) and the
    // layout/field-type model are frontend-neutral. These `internal` shims let the Zig
    // frontend register the SAME def records and resolve field types through the SAME
    // tables — no duplication, no behavior change for C, AOT-clean. The first shared-code
    // addition since the IFrontend seam (Zig Milestone D); see ZigLowering.

    /// <summary>Register a Zig struct/union under <paramref name="name"/>: add it to the
    /// emitted <see cref="Types"/> list AND the field/union layout tables so member access
    /// and <c>sizeof</c>/<c>offsetof</c> resolve through the same compile-time model the C
    /// frontend uses. Idempotent on the name (a second registration is ignored).</summary>
    internal void RegisterStructType(string name, List<StructField> fields, bool isUnion, AggregateLayout layout = AggregateLayout.Default)
    {
        if (_emittedTypes.Add(name))
        {
            _structFields[name] = fields;
            _structIsUnion[name] = isUnion;
            if (layout == AggregateLayout.Packed) { _packedStructs.Add(name); }
            Types.Add(new StructTypeDef(name, fields, isUnion, layout));
        }
    }

    /// <summary>Register a Zig enum under <paramref name="name"/> with the given underlying
    /// integer type and members, mapping the name to its <see cref="CType.Enum"/> (so the
    /// name resolves as a real enum type) and emitting an <see cref="EnumTypeDef"/>. Returns
    /// the <see cref="CType.Enum"/>. Idempotent on the name.</summary>
    internal CType.Enum RegisterEnumType(string name, CType underlying, List<EnumMember> members)
    {
        var enumType = new CType.Enum(name, underlying);
        _enumTypes[name] = enumType;
        if (Enums.All(e => e.Name != name)) { Enums.Add(new EnumTypeDef(name, underlying, members)); }
        return enumType;
    }

    /// <summary>The declared type of <paramref name="field"/> on the struct/union that
    /// <paramref name="structType"/> names (pointer levels peeled, mirroring
    /// <see cref="MemberType"/>), or <c>null</c> when the type isn't a registered aggregate
    /// or has no such field — so the Zig frontend can raise a precise diagnostic rather than
    /// silently defaulting to <see cref="CType.Int"/> as the C member access does.</summary>
    internal CType? StructFieldType(CType structType, string field)
    {
        var t = structType.Unqualified;
        while (t is CType.Pointer p) { t = p.Pointee.Unqualified; }
        if (t is CType.Named n && _structFields.TryGetValue(n.Name, out var fields))
        {
            foreach (var f in fields) { if (f.Name == field) { return f.Type; } }
        }
        return null;
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
                case C.StructMemberList sm:
                    WalkDeclList(sm.Arg0, sm.Arg1, (name, _, type) => fields.Add(new StructField(name, type)));
                    break;
                // C11 anonymous struct/union member — its fields are promoted into
                // the parent. Held in a generated nested aggregate + a hidden field;
                // each inner name is recorded so `parent.inner` routes through it.
                case C.AnonStructMember am: Gate(2011, "anonymous struct/union member", m); AddAnonMember(am.Arg3, owner, fields, isUnion: false); break;
                case C.AnonUnionMember am: Gate(2011, "anonymous struct/union member", m); AddAnonMember(am.Arg3, owner, fields, isUnion: true); break;
                // A NAMED member of a nested aggregate type — `struct {…} m;` /
                // `struct Tag {…} m;` (and union forms). Define the (tagged or
                // synthesized) type, then add `m` of that type — no promotion.
                case C.NamedNestedStruct nm: AddNamedNested(null, nm.Arg3, Tok(nm.Arg5), fields, isUnion: false); break;
                case C.NamedNestedUnion nm: AddNamedNested(null, nm.Arg3, Tok(nm.Arg5), fields, isUnion: true); break;
                case C.NamedNestedTaggedStruct nm: AddNamedNested(Tok(nm.Arg1), nm.Arg4, Tok(nm.Arg6), fields, isUnion: false); break;
                case C.NamedNestedTaggedUnion nm: AddNamedNested(Tok(nm.Arg1), nm.Arg4, Tok(nm.Arg6), fields, isUnion: true); break;
                // `T name[N]…;` — a fixed-size array member (codegen: a `fixed`
                // buffer for a primitive element, an [InlineArray] wrapper for a
                // non-primitive one). Multi-dimensional bounds give a nested array
                // type so `s.m[i][j]` strides; bounds must be constant expressions.
                case C.StructArrMember sm:
                {
                    var dims = TryConstDims(sm.Arg2) ?? throw new IrUnsupportedException("non-constant struct array bound");
                    fields.Add(new StructField(Tok(sm.Arg1), MakeArrayType(ResolveType(sm.Arg0), dims)));
                    break;
                }
                // C99 flexible array member `T name[];` — over-allocated at malloc
                // time. Model as a 1-element array (the struct-hack [1] convention),
                // so the member exists and access over-indexes into the tail.
                case C.StructFlexArrMember sm:
                    Gate(1999, "flexible array member", m);
                    fields.Add(new StructField(Tok(sm.Arg1), new CType.Array(ResolveType(sm.Arg0), 1)));
                    break;
                // `Ret (*name)(params);` — a function-pointer member. Same
                // FnPtrType lowering as the typedef/param fn-ptr forms (codegen
                // emits a `delegate*` field).
                case C.StructFnPtrMember sm:
                    fields.Add(new StructField(Tok(sm.Arg3), FnPtrType(sm.Arg0, sm.Arg6)));
                    break;
                case C.StructFnPtrMemberNoArgs sm:
                    fields.Add(new StructField(Tok(sm.Arg3), FnPtrType(sm.Arg0, null)));
                    break;
                // `T name : W;` — a bit-field. Codegen packs consecutive same-size
                // bit-fields into one shared backing field (MSVC storage-unit layout)
                // + a masked/sign-extended accessor property, so sizeof + offsets
                // match C while reads/writes keep C's value semantics.
                case C.StructBitField sm:
                {
                    var w = ConstEval(BuildExpr(sm.Arg3)) ?? throw new IrUnsupportedException("non-constant bit-field width");
                    fields.Add(new StructField(Tok(sm.Arg1), ResolveType(sm.Arg0), (int)w));
                    break;
                }
                // `T : W;` — an anonymous bit-field (padding). Kept in the field list
                // with an empty name and its width so the backend's packing reserves
                // its bits (and a zero width forces the next field onto a fresh
                // storage unit); it produces no accessible member and is skipped by
                // positional initializers.
                case C.StructAnonBitField sm:
                {
                    var w = ConstEval(BuildExpr(sm.Arg2)) ?? throw new IrUnsupportedException("non-constant anonymous bit-field width");
                    fields.Add(new StructField("", ResolveType(sm.Arg0), (int)w));
                    break;
                }
                default: throw new IrUnsupportedException(TypeName(m.Content));
            }
        }
        Member(memberList);
        return fields;
    }

    /// <summary>Add a C11 anonymous struct/union member: build it as a generated
    /// nested aggregate type, add a hidden container field to the parent, and record
    /// each inner field as promoted (so <c>parent.inner</c> rewrites to
    /// <c>parent.hidden.inner</c> at access time, keeping the union's overlap /
    /// the struct's sequential layout).</summary>
    private void AddAnonMember(Item innerMemberList, string owner, List<StructField> parentFields, bool isUnion)
    {
        var nested = $"__Anon{_anonAggrSeq++}";
        var innerFields = BuildStructFields(innerMemberList, nested);
        _structFields[nested] = innerFields;
        _structIsUnion[nested] = isUnion;
        Types.Add(new StructTypeDef(nested, innerFields, isUnion));

        var hidden = "__anon_" + nested;
        parentFields.Add(new StructField(hidden, new CType.Named(nested)));

        if (!_promoted.TryGetValue(owner, out var pm)) { _promoted[owner] = pm = new(StringComparer.Ordinal); }
        foreach (var f in innerFields) { pm[f.Name] = (hidden, nested); }
    }

    /// <summary>Add a NAMED member of a nested aggregate type (<c>struct {…} m;</c>
    /// or a tagged <c>struct Tag {…} m;</c>, and union forms). Defines the nested
    /// type (under its tag, or a synthesized name) and adds <paramref name="member"/>
    /// of that type — unlike an anonymous member, the fields are NOT promoted.</summary>
    private void AddNamedNested(string? tag, Item innerMemberList, string member, List<StructField> parentFields, bool isUnion)
    {
        var typeName = tag ?? $"__Anon{_anonAggrSeq++}";
        if (_emittedTypes.Add(typeName))
        {
            var inner = BuildStructFields(innerMemberList, typeName);
            _structFields[typeName] = inner;
            _structIsUnion[typeName] = isUnion;
            Types.Add(new StructTypeDef(typeName, inner, isUnion));
        }
        parentFields.Add(new StructField(member, new CType.Named(typeName)));
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
    private CType.Func FnPtrType(Item retItem, Item? paramListItem)
    {
        var ret = ResolveType(retItem);
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
        C.TypePtr t => new CType.Pointer(ResolveType(t.Arg0)),
        // `int * const p` — the POINTER is const (can't repoint); the pointee is
        // unchanged. Flag the Pointer so `p = q` trips the const check while
        // `*p = v` (pointee write) does not. `* volatile` / `* restrict` have no
        // C# model, so they stay plain pointers (dropped).
        C.TypePtrQualConst t => new CType.Pointer(ResolveType(t.Arg0)).WithQuals(TypeQual.Const),
        C.TypePtrQualVolatile t => new CType.Pointer(ResolveType(t.Arg0)),
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
        C.TypeAtomicParen t => AtomicType(ResolveType(t.Arg2), it),
        // `_Alignas(Type) T` / `_Alignas(constexpr) T` (C11 §6.7.5) — the
        // alignment specifier is ACCEPTED + IGNORED (a C# field/local has no
        // controllable alignment; same no-op treatment as Zig's `align(N)`).
        // Gated C11; the operand is validated but never read.
        C.TypeAlignasType t => AlignasType(t.Arg2, t.Arg4, it),
        C.TypeAlignasExpr t => AlignasExpr(t.Arg2, t.Arg4, it),
        C.TypePtrQualRestrict t => new CType.Pointer(ResolveType(t.Arg0)),
        C.TypeName t => ResolveTypeName(Tok(t.Arg0)),
        // `enum Tag` as a type — the registered real C# enum, or plain int if the
        // tag is unknown (forward/opaque) or names an anonymous int-constant enum.
        C.TypeEnum te => _enumTypes.TryGetValue(Tok(te.Arg1), out var et) ? et : CType.Int,
        // `struct Tag` / `union Tag` as a type — the canonical C# struct name.
        C.TypeStruct t => new CType.Named(Tok(t.Arg1)),
        C.TypeUnion t => new CType.Named(Tok(t.Arg1)),
        // Inline anonymous aggregate used as a type — `union { int i; float f; } u;`
        // (a NAMED member/var of an unnamed aggregate). Synthesize a struct name.
        C.TypeAnonStruct t => ResolveAnonAggregate(it, t.Arg3, isUnion: false),
        C.TypeAnonUnion t => ResolveAnonAggregate(it, t.Arg3, isUnion: true),
        // `TypeSpecList TYPE_NAME` — a function-specifier run (inline / _Noreturn)
        // immediately preceding a typedef-name: `static inline Cell *bump(…)`,
        // Lua's `l_sinline Table *gettable(…)`. The run contributes only its
        // function-specifier facts, recorded for the enclosing function symbol:
        // `inline` (→ [MethodImpl(AggressiveInlining)]) and `_Noreturn` (gated C11,
        // → [DoesNotReturn]). The TYPE_NAME is the whole base type.
        C.TypeSpecThenName t => SpecsThenName(t, it),
        // C23 `typeof(expr)` / `typeof(type)` — the expr form reads the operand's
        // synthesized CType (qualifiers dropped, as `typeof_unqual` does); the type
        // form unwraps to that type. The expr isn't evaluated (only its type taken).
        C.TypeofExpr t => BuildExpr(t.Arg2).Type.Unqualified,
        C.TypeofType t => ResolveType(t.Arg2),
        // A function-pointer type `Ret (*)(params)` — abstract declarator (a cast
        // target, a typedef target, a sizeof operand): lowers to CType.Func.
        C.TypeFnPtr t => FnPtrType(t.Arg0, t.Arg5),
        C.TypeFnPtrNoArgs t => FnPtrType(t.Arg0, null),
        C.TypeFnPtrVoid t => FnPtrType(t.Arg0, null),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary>Per-occurrence synthetic name for an inline anonymous aggregate
    /// type, cached by source position so the same type item always resolves to
    /// the same <see cref="CType.Named"/> (the field that holds it and every
    /// member access agree on one synthesized struct).</summary>
    private readonly Dictionary<SrcPos, CType> _anonAggregates = new();
    private int _anonAggrSeq;

    private CType ResolveAnonAggregate(Item typeItem, Item memberListItem, bool isUnion)
    {
        var pos = SrcPos.From(typeItem);
        if (_anonAggregates.TryGetValue(pos, out var cached)) { return cached; }
        var name = $"__Anon{_anonAggrSeq++}";
        var named = new CType.Named(name);
        _anonAggregates[pos] = named;
        var fields = BuildStructFields(memberListItem, name);
        _structFields[name] = fields;
        _structIsUnion[name] = isUnion;
        Types.Add(new StructTypeDef(name, fields, isUnion));
        return named;
    }

    /// <summary>Resolve a type-name token: a user/library typedef resolves to its
    /// underlying type; an unknown name (a predefined opaque type like
    /// <c>FILE</c> / <c>jmp_buf</c>'s target) stays a <see cref="CType.Named"/>
    /// whose spelling the backend emits verbatim.</summary>
    private CType ResolveTypeName(string name) =>
        _typedefs.TryGetValue(name, out var t) ? t : new CType.Named(name);

    /// <summary>Resolve a `TypeSpecList TYPE_NAME` type: the run's surviving facts
    /// are the function/storage specifiers (`_Noreturn`, `inline`,
    /// `_Thread_local` — see <see cref="RecordDeclSpecs"/>); the typedef-name is
    /// the whole base type.</summary>
    private CType SpecsThenName(C.TypeSpecThenName t, Item it)
    {
        RecordDeclSpecs(CollectSpecs(t.Arg0), SrcPos.From(it));
        return ResolveTypeName(Tok(t.Arg1));
    }

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
        C.TsInline => "inline",
        C.TsNoreturn => "_Noreturn",
        C.TsThreadLocal => "_Thread_local",
        C.TsConstexpr => "constexpr",
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
        var align = AlignOfConst(ResolveType(a.Arg2));
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
                    typed.Add((ResolveType(a.Arg0).Unqualified, a.Arg2, assoc));
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
        CheckAlignasStrictEnough(AlignOfConst(ResolveType(operandType)), t, it);
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
        RecordDeclSpecs(specs, pos);
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
            case C.StmtStaticDecl s: return BuildStmtStaticDecl(s) with { Pos = pos };
            case C.StmtStaticStructInit s: return BuildStmtStaticStructInit(s) with { Pos = pos };
            // Block-scope `static` arrays — pinned global storage under a mangled
            // name (same static storage duration as a file-scope array).
            case C.StmtStaticArr s: return BuildStaticLocalArr(s.Arg1, s.Arg2, s.Arg3, null) with { Pos = pos };
            case C.StmtStaticArrInit s: return BuildStaticLocalArr(s.Arg1, s.Arg2, s.Arg3, s.Arg6) with { Pos = pos };
            case C.StmtStaticArrInitImplicit s: return BuildStaticLocalArr(s.Arg1, s.Arg2, null, s.Arg7) with { Pos = pos };
            // Block-scope `static char a[] = "…"` / sized — pinned global char array.
            case C.StmtStaticCharArrStr s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg6, null) with { Pos = pos };
            case C.StmtStaticCharArrStrSized s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg5, s.Arg3) with { Pos = pos };
            case C.StmtStaticU16CharArrStr s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg6, null, wide: true) with { Pos = pos };
            case C.StmtStaticU16CharArrStrSized s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg5, s.Arg3, wide: true) with { Pos = pos };
            case C.StmtStaticWCharArrStr s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg6, null, wide: true) with { Pos = pos };
            case C.StmtStaticWCharArrStrSized s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg5, s.Arg3, wide: true) with { Pos = pos };
            case C.StmtStaticU32CharArrStr s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg6, null, wide: true) with { Pos = pos };
            case C.StmtStaticU32CharArrStrSized s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg5, s.Arg3, wide: true) with { Pos = pos };
            case C.StmtStaticU8CharArrStr s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg6, null, wide: true) with { Pos = pos };
            case C.StmtStaticU8CharArrStrSized s: return BuildStaticLocalCharArr(s.Arg1, s.Arg2, s.Arg5, s.Arg3, wide: true) with { Pos = pos };
            // Block-scope aggregate TYPE definitions (`struct cD { … };` inside a
            // function body — the block-scope enum forms are handled below). A
            // type has no storage, so C allows this; dotcc hoists the definition
            // into the top-level type section (deduped by tag, exactly as a
            // file-scope definition) and the statement emits nothing.
            case C.StmtStructDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: false); return EmptyStmt(pos);
            case C.StmtUnionDef s: BuildStructDef(Tok(s.Arg1), s.Arg3, null, isUnion: true); return EmptyStmt(pos);
            // Block-scope `_Static_assert(expr[, "msg"]);` — compile-time only,
            // evaluated exactly like the file-scope forms; a holding assertion
            // emits nothing. The message-less arity gates C23.
            case C.StaticAssertStmt s: Gate(2011, "_Static_assert", it); CheckStaticAssert(s.Arg2, s.Arg4, pos); return EmptyStmt(pos);
            case C.StaticAssertStmtNoMsg s: Gate(2023, "_Static_assert with no message", it); CheckStaticAssert(s.Arg2, null, pos); return EmptyStmt(pos);
            // C23 `[[attr]]` prepending a statement / block-scope declaration —
            // ACCEPTED + IGNORED; gate C23, unwrap to the inner statement. The
            // bare attribute-declaration `[[fallthrough]];` arrives here as a
            // wrapped EMPTY statement — it alone leaves a trace (a FallthroughMarker)
            // so BuildSwitch's -Wimplicit-fallthrough check can see it was intended.
            case C.AttrStmt s:
                Gate(2023, "[[attributes]]", it);
                if (AttrListHasFallthrough(s.Arg1)) { return new FallthroughMarker { Pos = pos }; }
                return BuildStmt(s.Arg4);
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
            // A block-scope enum definition has no storage — register its
            // constants and emit nothing (an empty block).
            case C.StmtEnumDef s: RegisterEnum(Tok(s.Arg1), null, s.Arg3); return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
            case C.StmtEnumDefTyped s: RegisterEnum(Tok(s.Arg1), s.Arg3, s.Arg5); return new Block(System.Array.Empty<CStmt>()) { Pos = pos };
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
    private static CStmt? SetjmpGuardOf(CExpr cond, CStmt then, CStmt? els, SrcPos pos)
    {
        // Bare `if (setjmp(env)) …` — truthy only on the longjmp re-entry, so the
        // then-branch is the recovery (catch) and the absent/else side is the
        // normal (try) path.
        if (IsSetjmpCall(cond, out var bareEnv))
        {
            return new SetjmpGuard(bareEnv, TryBody: els, CatchBody: then) { Pos = pos };
        }
        // `setjmp(env) == 0` / `!= 0`, either operand order.
        if (cond is Binary { Op: BinOp.Eq or BinOp.Ne } b)
        {
            CExpr? env = null;
            if (IsSetjmpCall(b.Left, out var e1) && IsZeroLit(b.Right)) { env = e1; }
            else if (IsSetjmpCall(b.Right, out var e2) && IsZeroLit(b.Left)) { env = e2; }
            if (env is not null)
            {
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

    /// <summary>True when <paramref name="e"/> is a direct call to <c>setjmp</c>
    /// (parens peeled); yields the env-token argument expression.</summary>
    private static bool IsSetjmpCall(CExpr e, out CExpr env)
    {
        while (e is Paren p) { e = p.Inner; }
        if (e is Call { Callee: "setjmp", Args: { Count: 1 } a })
        {
            env = a[0];
            return true;
        }
        env = null!;
        return false;
    }

    /// <summary>True for the integer literal <c>0</c> (parens peeled) — the RHS of
    /// a recognised <c>setjmp(env) == 0</c> guard.</summary>
    private static bool IsZeroLit(CExpr e)
    {
        while (e is Paren p) { e = p.Inner; }
        return e is LitInt { Value: 0 };
    }

    /// <summary>A declaration in statement position. The grammar wraps the
    /// concrete declaration kind (plain, array, fn-ptr, …) inside
    /// <c>StmtDecl.Arg0</c>; dispatch on it.</summary>
    private CStmt BuildDeclStmt(Item it) => it.Content switch
    {
        C.Decl => BuildDecl(it),
        // `auto x = E;` (C23 type inference) and `auto Type x …` (redundant
        // pre-C23 storage class). See BuildDeclAutoInfer / BuildDeclList.
        C.DeclAutoInfer d => BuildDeclAutoInfer(d),
        C.DeclAutoStorage d => BuildDeclList(d.Arg1, d.Arg2),
        // `char s[] = "hi";` (implicit size) / `char buf[N] = "hi";` (explicit, zero-padded).
        C.DeclCharArrStr d => BuildDeclCharArrStr(d.Arg0, d.Arg1, null, d.Arg5),
        C.DeclCharArrStrSized d => BuildDeclCharArrStr(d.Arg0, d.Arg1, CharArrSize(d.Arg2), d.Arg4),
        C.DeclU16CharArrStr d => BuildDeclCharArrStr(d.Arg0, d.Arg1, null, d.Arg5, wide: true),
        C.DeclU16CharArrStrSized d => BuildDeclCharArrStr(d.Arg0, d.Arg1, CharArrSize(d.Arg2), d.Arg4, wide: true),
        C.DeclWCharArrStr d => BuildDeclCharArrStr(d.Arg0, d.Arg1, null, d.Arg5, wide: true),
        C.DeclWCharArrStrSized d => BuildDeclCharArrStr(d.Arg0, d.Arg1, CharArrSize(d.Arg2), d.Arg4, wide: true),
        C.DeclU32CharArrStr d => BuildDeclCharArrStr(d.Arg0, d.Arg1, null, d.Arg5, wide: true),
        C.DeclU32CharArrStrSized d => BuildDeclCharArrStr(d.Arg0, d.Arg1, CharArrSize(d.Arg2), d.Arg4, wide: true),
        C.DeclU8CharArrStr d => BuildDeclCharArrStr(d.Arg0, d.Arg1, null, d.Arg5, wide: true),
        C.DeclU8CharArrStrSized d => BuildDeclCharArrStr(d.Arg0, d.Arg1, CharArrSize(d.Arg2), d.Arg4, wide: true),
        C.DeclStructInit d => BuildDeclStructInit(d),
        // `T x = { .field = … };` — C99 designated struct/union initializer.
        C.DeclStructDesignated d => BuildLocalInit(d.Arg1, ResolveType(d.Arg0), BuildStructDesignated(ResolveType(d.Arg0), d.Arg4)),
        // `T x = {};` — C23 empty initializer (zero value).
        C.DeclEmptyInit d => Gated(2023, "empty initializer", d.Arg1, BuildLocalInit(d.Arg1, ResolveType(d.Arg0), new DefaultLit { Type = ResolveType(d.Arg0) })),
        C.DeclArr d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, null, implicitSize: false),
        C.DeclArrEmptyInit d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, null, implicitSize: false),
        C.DeclArrInit d => BuildArrDecl(d.Arg0, d.Arg1, d.Arg2, d.Arg5, implicitSize: false),
        C.DeclArrInitImplicit d => BuildArrDecl(d.Arg0, d.Arg1, null, d.Arg6, implicitSize: true),
        // Pointer-to-array `T (*p)[N]` [= init] — a row pointer (multi-dim machinery).
        C.DeclPtrToArr d => BuildPtrToArr(d.Arg0, d.Arg3, d.Arg5, null),
        C.DeclPtrToArrInit d => BuildPtrToArr(d.Arg0, d.Arg3, d.Arg5, d.Arg7),
        // Local function-pointer variable: `Ret (*name)(params)` [= init].
        C.DeclFnPtr d => BuildFnPtrLocal(d.Arg0, d.Arg3, d.Arg6, null),
        C.DeclFnPtrNoArgs d => BuildFnPtrLocal(d.Arg0, d.Arg3, null, null),
        C.DeclFnPtrInit d => BuildFnPtrLocal(d.Arg0, d.Arg3, d.Arg6, d.Arg9),
        C.DeclFnPtrNoArgsInit d => BuildFnPtrLocal(d.Arg0, d.Arg3, null, d.Arg8),
        _ => throw new IrUnsupportedException(TypeName(it.Content)),
    };

    /// <summary>Per-translation-unit counter giving each function-scope
    /// <c>static</c> local a program-unique backing-field name. The CS0136
    /// per-function rename can't serve here — two functions' same-named statics
    /// become two fields of the SAME <c>DotCcGlobals</c> class.</summary>
    private int _staticLocalSeq;

    /// <summary>A function-scope <c>static</c> local (<c>static int counter = 0;</c>).
    /// Its storage is program-lifetime, so it lowers to a once-initialized static
    /// field of <c>DotCcGlobals</c> (a <see cref="GlobalVar"/> with a mangled,
    /// program-unique name); references resolve to that field via an alias symbol
    /// in the function's scope. The statement itself emits nothing.</summary>
    private CStmt BuildStmtStaticDecl(C.StmtStaticDecl n)
    {
        WalkDeclList(n.Arg1, n.Arg2, (name, initItem, type) =>
        {
            // A static-local ARRAY has its own production (pinned GlobalArray
            // lowering under a mangled name); an array tail here would silently
            // become a plain static field.
            if (type.Unqualified is CType.Array)
            {
                throw new IrUnsupportedException("array declarator in a static-local multi-declarator list (split it into its own declaration)");
            }
            var sym = new Symbol
            {
                Name = name, Kind = SymKind.Var, Type = type,
                Storage = Storage.Static, IsGlobal = true,
                TargetName = $"{_symbols.Escape(name)}__s{_staticLocalSeq++}",
            };
            CExpr? slInit = null;
            if (initItem is { } ii) { slInit = BuildExpr(ii); EnsureNotEmbed(slInit); CheckQualifierDiscard(slInit, sym.Type, SrcPos.From(ii), "initialization"); }
            Globals.Add(new GlobalVar(sym, slInit));
            _symbols.DeclareAlias(sym);
        });
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }

    /// <summary>File-scope <c>static T x = { … };</c> — a once-initialised
    /// <c>DotCcGlobals</c> field with a positional aggregate initializer.</summary>
    private void BuildGlobalStructInit(Item typeItem, Item nameItem, Item initListItem)
    {
        var type = ResolveType(typeItem);
        var init = BuildAggregateInit(type, initListItem);
        var sym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Static, IsGlobal = true,
        });
        Globals.Add(new GlobalVar(sym, init));
    }

    /// <summary>Block-scope <c>static T x = { … };</c> — like a global aggregate
    /// init, but with a program-unique mangled name and an alias symbol so the
    /// function body's references resolve to the hoisted field.</summary>
    private CStmt BuildStmtStaticStructInit(C.StmtStaticStructInit n)
    {
        var type = ResolveType(n.Arg1);
        var init = BuildAggregateInit(type, n.Arg5);
        var sym = new Symbol
        {
            Name = Tok(n.Arg2), Kind = SymKind.Var, Type = type,
            Storage = Storage.Static, IsGlobal = true,
            TargetName = $"{_symbols.Escape(Tok(n.Arg2))}__s{_staticLocalSeq++}",
        };
        Globals.Add(new GlobalVar(sym, init));
        _symbols.DeclareAlias(sym);
        return new DeclStmt(System.Array.Empty<LocalDecl>());
    }

    private DeclStmt BuildFnPtrLocal(Item retItem, Item nameItem, Item? paramsItem, Item? initItem)
    {
        CType.Func type = FnPtrType(retItem, paramsItem);
        var init = initItem is { } ii ? BuildExpr(ii) : null;
        // Propagate a dlsym-sourced native calling-convention marker from the
        // initializer onto the declared fn-ptr type, so the canonical idiom
        //     int (*fn)(int) = (int(*)(int))dlsym(h, "add");  fn(5);
        // calls `fn` through delegate* unmanaged[Cdecl]. Calls render unchanged —
        // C# picks the calli convention from the variable's (now-native) type.
        if (init?.Type is CType.Func { IsNativeCallConv: true } && !type.IsNativeCallConv)
        {
            type = type with { IsNativeCallConv = true };
        }
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, init) });
    }

    private CStmt BuildDecl(Item declItem)
    {
        if (declItem.Content is not C.Decl decl) { throw new IrUnsupportedException(TypeName(declItem.Content)); }
        return BuildDeclList(decl.Arg0, decl.Arg1);
    }

    /// <summary>Build a <c>Type DeclItemList</c> declaration into a
    /// <see cref="DeclStmt"/>. Shared by the plain form (<c>int x, y;</c>) and the
    /// redundant pre-C23 storage-class form (<c>auto int z;</c>), which differ
    /// only in the dropped <c>auto</c> keyword.</summary>
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
        WalkDeclList(typeItem, listItem, (name, initItem, type) =>
        {
            if (type.Unqualified is CType.Array)
            {
                if (_sawConstexprSpec)
                {
                    throw new IrUnsupportedException(
                        $"'{name}': only integer constexpr objects are supported (float/pointer/struct/array constexpr is not built yet)");
                }
                // Same lowering as a standalone fixed-size array decl: the symbol
                // keeps the (possibly nested) array type so sizeof/the length idiom
                // resolve; the stackalloc extent is the flattened product.
                var total = 1;
                var elem = type.Unqualified;
                while (elem is CType.Array a)
                {
                    total *= a.Count ?? throw new IrUnsupportedException("unsized array in a multi-declarator tail");
                    elem = a.Element;
                }
                var asym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
                Flush();
                stmts.Add(new ArrayDecl(asym, elem,
                    new LitInt(total.ToString(System.Globalization.CultureInfo.InvariantCulture), total) { Type = CType.Int },
                    null));
                return;
            }
            var sym = _symbols.Declare(new Symbol
            {
                Name = name, Kind = SymKind.Var, Storage = Storage.Auto,
                // const-qualified for the same write-to-const coverage as the
                // file-scope form.
                Type = _sawConstexprSpec ? type.WithQuals(TypeQual.Const) : type,
                IsConstexpr = _sawConstexprSpec,
            });
            CExpr? sInit = null;
            if (initItem is { } ii) { sInit = BuildExpr(ii); EnsureNotEmbed(sInit); CheckQualifierDiscard(sInit, sym.Type, SrcPos.From(ii), "initialization"); }
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

    /// <summary>Walk a comma-separated init-declarator list, invoking
    /// <paramref name="add"/> with each declarator's (name, initializer?, type).
    /// The FIRST declarator's <c>*</c>s were greedily folded into
    /// <paramref name="baseType"/> by the grammar's <c>Type → Type *</c> rule;
    /// each subsequent declarator rebuilds its type from the pointer-stripped
    /// element plus its own <c>*</c>s (so <c>int *a, b;</c> ⇒ a:int*, b:int).
    /// Shared by local (<see cref="BuildDecl"/>) and file-scope declarations.</summary>
    private void WalkDeclList(Item typeItem, Item listItem, Action<string, Item?, CType> add)
    {
        var baseType = ResolveType(typeItem);
        // Peel only the LITERAL trailing `*`s (the `Type → Type *` rule greedily
        // folded them into baseType, but they bind to the FIRST declarator alone) —
        // subsequent declarators rebuild from the stripped element plus their own
        // `*`s (`int *a, b` ⇒ a:int*, b:int). Pointer-ness that came from a typedef
        // base (`BoxPtr p, q` ⇒ both Box*) is NOT a literal star and stays in
        // `element`, so every declarator keeps it.
        var litStars = CountLiteralStars(typeItem);
        var element = baseType;
        for (var i = 0; i < litStars && element is CType.Pointer p; i++) { element = p.Pointee; }
        void WalkTail(Item it, int stars)
        {
            switch (it.Content)
            {
                // `DeclItemTail → * DeclItemTail` (declItemTailPtr) — its child is
                // itself a DeclItemTail, so it can be a further `*` level OR the
                // terminal `DeclItemTailPlain` wrapping the DeclItem. Both recurse,
                // accumulating the star count (`int *a, *b;` → b:int*).
                case C.DeclItemTailPtr p: WalkTail(p.Arg1, stars + 1); break;
                case C.DeclItemTailPlain t: WalkTail(t.Arg0, stars); break;
                case C.DeclItem di: add(Tok(di.Arg0), null, WrapPtr(element, stars)); break;
                case C.DeclItemInit di: add(Tok(di.Arg0), di.Arg2, WrapPtr(element, stars)); break;
                // `…, name[N]` — an array declarator in tail position. The type is
                // an array OF the star-wrapped element (`int *a, *c[5];` → c is an
                // array of int*); the consumer decides the lowering (local →
                // ArrayDecl/stackalloc, struct member → fixed buffer).
                case C.DeclItemTailArr a:
                    add(Tok(a.Arg0), null, MakeArrayType(WrapPtr(element, stars),
                        TryConstDims(a.Arg1) ?? throw new IrUnsupportedException("non-constant array bound in a multi-declarator tail")));
                    break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        void Walk(Item it)
        {
            switch (it.Content)
            {
                case C.DeclItemListCons c: Walk(c.Arg0); Walk(c.Arg2); break;
                case C.DeclItemListOne o: Walk(o.Arg0); break;
                case C.DeclItem di: add(Tok(di.Arg0), null, baseType); break;
                case C.DeclItemInit di: add(Tok(di.Arg0), di.Arg2, baseType); break;
                case C.DeclItemTailPlain t: WalkTail(t.Arg0, 0); break;
                case C.DeclItemTailPtr t: WalkTail(t.Arg1, 1); break;
                case C.DeclItemTailArr: WalkTail(it, 0); break;
                default: throw new IrUnsupportedException(TypeName(it.Content));
            }
        }
        Walk(listItem);
    }

    private static CType WrapPtr(CType t, int stars)
    {
        for (var i = 0; i < stars; i++) { t = new CType.Pointer(t); }
        return t;
    }

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

    /// <summary>A local array declaration. Lowers to a C# <c>stackalloc</c>. A
    /// constant dimension types the symbol as <see cref="CType.Array"/> (so
    /// <c>sizeof(arr)</c> and the array-length idiom resolve); a runtime extent
    /// (VLA-ish) decays to a pointer. Multi-dimensional arrays are deferred.</summary>
    private ArrayDecl BuildArrDecl(Item typeItem, Item nameItem, Item? dimsItem, Item? initItem, bool implicitSize)
    {
        var elem = ResolveType(typeItem);
        var name = Tok(nameItem);
        var dims = dimsItem is { } di ? TryConstDims(di) : null;

        CType arrType;
        CExpr? countExpr = null;
        List<CExpr>? inits = null;
        if (initItem is { } ii)
        {
            // Brace-initialized — the array interpreter resolves designators,
            // struct elements, and per-dimension zero-fill into a dense list; a
            // multi-dim array flattens to one stackalloc of product(dims). The
            // symbol keeps the NESTED array type so a[i][j] strides correctly.
            inits = BuildArrayElems(elem, dims, ParseInitList(ii));
            arrType = dims is { Count: >= 1 } ? MakeArrayType(elem, dims) : new CType.Array(elem, inits.Count);
        }
        else if (dims is { Count: >= 1 })
        {
            // Fixed-size, no initializer (C# zero-fills a stackalloc). A multi-dim
            // array flattens to one stackalloc of the product; sizeof(arr) and the
            // array-length idiom read the dimensions off the nested array type.
            var total = 1;
            foreach (var d in dims) { total *= d; }
            arrType = MakeArrayType(elem, dims);
            countExpr = new LitInt(total.ToString(System.Globalization.CultureInfo.InvariantCulture), total) { Type = CType.Int };
        }
        else if (dimsItem is { } di2 && BuildArrDims(di2) is { Count: 1 } runtimeDims)
        {
            // Runtime extent (VLA) — C arrays decay to a pointer in value context.
            arrType = new CType.Pointer(elem);
            countExpr = runtimeDims[0];
        }
        else if (dimsItem is { })
        {
            throw new IrUnsupportedException("multi-dimensional array with a non-constant dimension");
        }
        else
        {
            arrType = new CType.Pointer(elem);
        }

        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = arrType, Storage = Storage.Auto });
        return new ArrayDecl(sym, elem, countExpr, inits);
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

    /// <summary><c>struct Point p = {3, 4};</c> — a local declaration with a
    /// positional aggregate initializer. Types the symbol as the struct and
    /// builds a <see cref="StructInit"/> from the brace list.</summary>
    private CStmt BuildDeclStructInit(C.DeclStructInit n)
    {
        var type = ResolveType(n.Arg0);
        return BuildLocalInit(n.Arg1, type, BuildAggregateInit(type, n.Arg4));
    }

    /// <summary>Declare a single block local of <paramref name="type"/> with an
    /// already-built initializer, as a one-declarator <see cref="DeclStmt"/>.</summary>
    private DeclStmt BuildLocalInit(Item nameItem, CType type, CExpr init)
    {
        EnsureNotEmbed(init);
        var sym = _symbols.Declare(new Symbol { Name = Tok(nameItem), Kind = SymKind.Var, Type = type, Storage = Storage.Auto });
        return new DeclStmt(new[] { new LocalDecl(sym, init) });
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
        var init = BuildDecl(s.Arg3);
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
            C.SizeofType s => new SizeOfExpr(ResolveType(s.Arg2)) { Type = CType.SizeT },
            // `sizeof expr` — the operand isn't evaluated, only its type measured.
            C.SizeofExpr s => new SizeOfExpr(BuildExpr(s.Arg1).Type) { Type = CType.SizeT },
            // `_Alignof(Type)` (C11 §6.5.3.4) — folds immediately to the layout
            // model's alignment (an integer constant expression, `size_t`-typed
            // like sizeof), so it composes with _Static_assert / array bounds /
            // case labels with no IR node of its own.
            C.AlignofType a => FoldAlignof(a, it),
            C.GenericSelect g => BuildGenericSelect(g, it),
            C.OffsetofExpr o => BuildOffsetof(o),
            // `va_arg(ap, T)` — special syntax (its 2nd operand is a type).
            C.VaArgExpr v => new VaArgGet(BuildExpr(v.Arg2), ResolveType(v.Arg4)) { Type = ResolveType(v.Arg4) },
            C.Call c => BuildCall(c.Arg0, c.Arg2),
            C.CallNoArgs c => BuildCall(c.Arg0, null),
            // C99/C23 compound literals — (T){…} struct/scalar, (T[]){…} array,
            // designated, and the C23 empty form.
            C.CompoundLit c => Gated(1999, "compound literals", c.Arg1, BuildCompoundLit(c.Arg1, c.Arg4)),
            C.CompoundLitDesignated c => Gated(1999, "compound literals", c.Arg1, BuildCompoundLitDesignated(c.Arg1, c.Arg4)),
            C.CompoundLitEmpty c => Gated(2023, "empty initializer", c.Arg1, BuildCompoundLitEmpty(c.Arg1)),
            C.CompoundLitArr c => Gated(1999, "compound literals", c.Arg1, BuildArrayCompoundLit(c.Arg1, c.Arg2, c.Arg5)),
            C.CompoundLitArrImplicit c => Gated(1999, "compound literals", c.Arg1, BuildArrayCompoundLit(c.Arg1, null, c.Arg6)),
            C.CommaOp => BuildCommaOp(it),
            _ => throw new IrUnsupportedException(TypeName(it.Content)),
        };
        return e with { Pos = pos };
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
            var hidden = new Member(base_, p.Hidden, arrow) { Type = new CType.Named(p.Nested), IsLValue = true };
            return BuildMemberAccess(hidden, field, arrow: false);
        }
        return new Member(base_, field, arrow) { Type = MemberType(base_, field), IsLValue = true };
    }

    /// <summary>The canonical struct/union name an expression's type names (pointer
    /// levels peeled), or null if it isn't an aggregate.</summary>
    private static string? StructCanonical(CType t)
    {
        while (t is CType.Pointer p) { t = p.Pointee; }
        return (t.Unqualified as CType.Named)?.Name;
    }

    private CExpr BuildCast(C.Cast c)
    {
        var target = ResolveType(c.Arg1);
        var operand = BuildExpr(c.Arg3);
        // `(void)X` — C# has no void cast and the value is discarded; carry the
        // operand through typed void so a statement position emits `X;`.
        if (target is CType.VoidType) { return operand with { Type = CType.Void }; }
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
            // silent.
            if (DlfcnInScope && operand.Type.Unqualified is CType.Pointer { Pointee: CType.VoidType })
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

    private CExpr Rel(BinOp op, Item l, Item r) =>
        new Binary(op, BuildExpr(l), BuildExpr(r)) { Type = CType.Int };

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
        switch (ConstEval(BuildExpr(exprItem)))
        {
            case null:
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
            UnOp.AddrOf => new CType.Pointer(oe.Type),
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
        // code never hits this; it's a safety net during incremental growth.
        return new NameRef(name) { Type = CType.Int };
    }

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
            var fn = sym?.Type as CType.Func;
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
            var calleeSym = sym is { Kind: SymKind.Func } ? sym : null;
            return new Call(name, args, fn?.Params, calleeSym) { Type = fn?.Return ?? CType.Int };
        }

        // Indirect call through a computed fn-ptr expression: `(*fp)(x)`,
        // `tbl[i](x)`, `s.fn(x)`. Dereferencing a function pointer is a C no-op
        // that decays straight back to the pointer; C# calls fn-ptrs directly
        // and rejects `*fp` (CS0193). Peel redundant parens, then any leading
        // deref whose operand is itself a function pointer — repeatedly, so
        // `(*(e.op))(x)` and the parenthesised `(*f)(x)` forms both reduce. A
        // deref of a pointer-TO-fn-pointer is preserved: C# needs it to reach
        // the callable value.
        var callee = BuildExpr(calleeItem);
        while (true)
        {
            if (callee is Paren pc) { callee = pc.Inner; continue; }
            if (callee is Unary { Op: UnOp.Deref } u && IsFuncPtr(u.Operand.Type)) { callee = u.Operand; continue; }
            break;
        }
        var rty = callee.Type switch
        {
            CType.Func f2 => f2.Return,
            CType.Pointer { Pointee: CType.Func f3 } => f3.Return,
            _ => CType.Int,
        };
        return new IndirectCall(callee, args) { Type = rty };
    }

    /// <summary>True when <paramref name="t"/> is a function pointer (or a bare
    /// function type) — the operand of a no-op call-site deref. Typedefs already
    /// resolve to their underlying type at <c>ResolveType</c> time, so a plain
    /// structural check on the unqualified type suffices.</summary>
    private static bool IsFuncPtr(CType t) =>
        t.Unqualified is CType.Pointer { Pointee: CType.Func } or CType.Func;

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
        var structType = ResolveType(n.Arg2);
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

    /// <summary>The constant array bound of a char-array string declarator's
    /// <c>ArrDims</c> (single dimension, constant-folded).</summary>
    private int CharArrSize(Item arrDims)
    {
        var dims = BuildArrDims(arrDims);
        if (dims.Count != 1 || ConstEval(dims[0]) is not { } n)
        {
            throw new IrUnsupportedException("char-array string init with a non-constant or multi-dimensional bound");
        }
        return (int)n;
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

    /// <summary>Decode a char-array string initializer's literal to its element
    /// values (excluding the NUL the caller appends), picking the width from how the
    /// literal was spelled and the array's element type: a narrow <c>"…"</c> decodes
    /// to UTF-8 bytes; a wide literal decodes to UTF-16 code units, except a
    /// <c>char32_t</c> element decodes the <c>U"…"</c> to UTF-32 code units (one per
    /// Unicode scalar). Shared by the block-scope / file-scope / static-local char
    /// array builders so all three route char32_t identically.</summary>
    private List<int> WideArrValues(CType elem, Item strSeqItem, bool wide)
    {
        if (!wide) { return DotCC.EmitHelpers.StringByteValues(CollectStrSegments(strSeqItem)); }
        // A wide-prefixed literal (u/U/L/u8): strip the prefix, then decode by the
        // element width — char32_t = 32-bit code units, char8_t = UTF-8 bytes (like
        // a narrow string), else 16-bit (char16_t / wchar_t).
        var segs = CollectWideStrSegments(strSeqItem);
        return elem switch
        {
            CType.Prim { Name: "char32_t" } => DotCC.EmitHelpers.StringU32Values(segs),
            CType.Prim { Name: "char8_t" } => DotCC.EmitHelpers.StringByteValues(segs),
            _ => DotCC.EmitHelpers.StringU16Values(segs),
        };
    }

    /// <summary><c>char s[] = "hi";</c> — a mutable char array initialised from a
    /// string. Lowers to a byte stackalloc of the decoded bytes plus the NUL;
    /// an explicit size zero-pads (or, exact-fit, may drop the NUL — C's rule).</summary>
    private CStmt BuildDeclCharArrStr(Item typeItem, Item nameItem, int? explicitSize, Item strSeqItem, bool wide = false)
    {
        var elem = ResolveType(typeItem);
        var bytes = WideArrValues(elem, strSeqItem, wide);
        bytes.Add(0);                                   // NUL
        var count = explicitSize ?? bytes.Count;
        var inits = new List<CExpr>(count);
        for (var i = 0; i < count; i++)
        {
            var v = i < bytes.Count ? bytes[i] : 0;     // zero-pad beyond the string
            inits.Add(new LitInt(v.ToString(System.Globalization.CultureInfo.InvariantCulture), v) { Type = CType.Int });
        }
        var sym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameItem), Kind = SymKind.Var, Type = new CType.Array(elem, count), Storage = Storage.Auto,
        });
        return new ArrayDecl(sym, elem, null, inits);
    }

    private CExpr BuildChr(C.Chr c)
    {
        // A C character constant has type int; emit its integer value (the simplest
        // C-faithful lowering — `'A'` → `65`, `'\n'` → `10`). The value carries into
        // byte/int sinks via C#'s constant conversions.
        var raw = Tok(c.Arg0);
        if (raw is null || raw.Length < 3) { return new LitInt("0", 0) { Type = CType.Int }; }
        var value = DecodeCharConstant(raw[1..^1]);
        return new LitInt(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) { Type = CType.Int };
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
