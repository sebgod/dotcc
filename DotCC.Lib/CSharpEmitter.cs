#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

/// <summary>
/// Visitor: every AST node returns a C# source-code snippet. Container nodes
/// concatenate child snippets; leaves emit primitive C# (identifiers,
/// literals, operators). One <c>Visit(C.&lt;Record&gt;)</c> overload per
/// action declared in <c>c.lalr.yaml</c> — the generator enforces this at
/// compile time.
/// </summary>
/// <remarks>
/// The lowering today is the initial low-level malloc/byte* form so the same
/// source compiles under <c>clang -std=c99</c> with identical observable
/// output. Roadmap: as the grammar grows toward more of real C, individual
/// <c>Visit</c> methods will get rewritten to prefer idiomatic C# types
/// where the source's usage allows (e.g. <c>int*</c>+<c>malloc</c> → <c>int[]</c>
/// when only ever indexed; <c>char*</c> → <c>string</c> for printf-only consumers).
/// </remarks>
internal sealed partial class CSharpEmitter : C.IVisitor<EmitContent>
{
    // ---- Reading children -------------------------------------------------
    // Every visit method accesses its child results via `n.ArgX.Content`.
    // Content is `object` carrying an EmitContent variant; T() / S() / A()
    // / EI() / IM() are the typed accessors. T() is by far the most common —
    // most children are plain text.

    /// <summary>Read a child as code text — the common case. Handles
    /// both visit-produced <see cref="EmitContent.Text"/> wrappers AND
    /// raw lexer-token strings (terminals like <c>ID</c>/<c>NUM</c>
    /// arrive with the lexeme as a plain string).</summary>
    private static string T(Item it) => it.Content switch
    {
        EmitContent.Text t => t.Value,
        EmitContent.DeclStmtMarker d => d.Value,
        string s => s,
        // setjmp variants must be consumed by Visit(Eq)/Visit(Neq) or
        // Visit(StmtIfElse). Reaching T() means setjmp showed up in a
        // context dotcc can't rewrite to try/catch — fail loudly with
        // a clear hint rather than silently emitting a bogus call.
        EmitContent.SetjmpCall =>
            throw new CompileException(
                "setjmp(env) can only appear as the condition of an `if/else` or " +
                "as `setjmp(env) == 0` / `!= 0`. Other contexts (assignment to a " +
                "variable, switch condition, etc.) aren't supported — see <setjmp.h>."),
        EmitContent.SetjmpCheckZero =>
            throw new CompileException(
                "setjmp(env) == 0 / != 0 comparison can only appear as the condition " +
                "of an `if/else`. See <setjmp.h>."),
        // sizeof / malloc markers render to their ordinary low-level text in
        // every context except the one structural consumer that pattern-matches
        // the variant (malloc recognition in Call; promotion in DeclItemInit).
        EmitContent.SizeofType st => $"sizeof({st.TypeName})",
        EmitContent.MallocSizeof ms => ms.LowLevelText,
        _ => throw new InvalidCastException(
            $"expected EmitContent.Text or string, got {it.Content?.GetType().FullName ?? "null"}"),
    };
    /// <summary>Read a child as an accumulated specifier list.</summary>
    private static IReadOnlyList<string> S(Item it) => ((EmitContent.SpecList)it.Content).Specs;
    /// <summary>Read a child as an argument list (call/array-init).</summary>
    private static IReadOnlyList<string> A(Item it) => ((EmitContent.Args)it.Content).Values;
    /// <summary>Read a child as an enum-item list.</summary>
    private static IReadOnlyList<string> EI(Item it) => ((EmitContent.EnumItems)it.Content).Items;
    /// <summary>Read a child as a designated-initializer member list.</summary>
    private static IReadOnlyList<string> IM(Item it) => ((EmitContent.InitMembers)it.Content).Members;
    /// <summary>Read a child as a function-signature header.</summary>
    private static EmitContent.FnHeader FH(Item it) => (EmitContent.FnHeader)it.Content;
    /// <summary>Read a child as an init-declarator list (file-scope and
    /// block-scope declarations both flow through this).</summary>
    private static IReadOnlyList<EmitContent.DeclEntry> DE(Item it) => ((EmitContent.DeclEntries)it.Content).Entries;

    // ---- enum typing helpers --------------------------------------------
    // C enums are plain ints in every expression; C# enums are a distinct type
    // with asymmetric operator rules (enum±int → enum, but enum&int / enum*int /
    // enum==int are all errors). To keep C semantics with no surprises, an
    // enum-typed operand of any arithmetic/bitwise/relational/shift operator is
    // decayed to `(int)` here, so the operation is pure int arithmetic and its
    // result is int (never enum). Enum-ness therefore only flows from leaves
    // (enum var read, enumerator ref) through transparent wrappers (paren, cast,
    // ++/--, ternary, comma, assign); the casts below are inserted at the typed
    // sinks (decl/assign/return/arg/index/printf/Cond.B).

    /// <summary>The C# enum type an expression child produced, or null.</summary>
    private static string? EnumOf(Item it) => it.Content is EmitContent.Text { EnumType: { } e } ? e : null;

    /// <summary>The synthesized <see cref="CType"/> a child propagated, or null.</summary>
    private static CType? TyOf(Item it) => it.Content is EmitContent.Text { Ty: { } t } ? t : null;

    /// <summary>Build a Text result carrying a synthesized type (for sizeof).</summary>
    private static EmitContent Typed(string text, CType? ty) => new EmitContent.Text(text, null, ty);

    /// <summary>Read an operand's text, decaying an enum value to its `int`
    /// underlying so it can take part in a C int operation.</summary>
    private static string IntDecay(Item it) => EnumOf(it) is null ? T(it) : $"(int){T(it)}";

    /// <summary>Wrap a child as a C-truthy condition (`Cond.B(...)`), decaying an
    /// enum first — `Cond.B` has int/double/pointer/bool overloads but not enum,
    /// so `if (color)` must become `Cond.B((int)color)`.</summary>
    private static string CondOf(Item it) => $"Cond.B({IntDecay(it)})";

    // Name of the function currently being reduced. Set by each fnSig*
    // action; cleared by funcDef/funcProto when the enclosing Fn finishes.
    // Read by Visit(Var) so `__func__` resolves directly to the enclosing
    // function's name — no placeholder-string substitution.
    private string? _currentFunctionName;

    // Emitted C# return type of the function being reduced (set in StartFn,
    // cleared at FuncDef/FuncProto). Read by Visit(StmtReturn) to reconcile a
    // returned value's enum-ness with the declared return type: `return c;` from
    // an int function decays the enum to int; `return 2;` from an enum function
    // casts the int to the enum.
    private string? _currentFunctionReturnType;

    // ---- malloc/free → stack-value peephole -----------------------------
    // Per-function usage of `S* p = (S*)malloc(sizeof(S))` candidates, keyed
    // by variable name; reset on each function entry (StartFn). A candidate is
    // *promotable* — rewritable to a stack value `S p = new S();` — iff every
    // reference to it is either the base of a `->` access or the argument of a
    // matching `free(p)` (so it never escapes: not returned, not passed to a
    // function, not address-taken, not pointer-arithmetic'd, not compared), and
    // a matching free exists. The decision needs the WHOLE function body, but
    // the pipeline is single-pass bottom-up SDT — the declaration reduces before
    // its later uses. So Compiler.EmitCSharp runs two passes: an analysis pass
    // populates `_promotableOut`, then the emit pass is seeded with that set via
    // `_promotableIn` and consults it locally at each node.
    private sealed class MallocVar
    {
        public string StructType = "";  // S, from sizeof(S)
        public bool TypeMatches;        // declared type was exactly S*
        public int TotalRefs;           // every Visit(Var) of this name
        public int ArrowRefs;           // uses as the base of `->`
        public int FreeRefs;            // free(p) calls
    }
    private readonly Dictionary<string, MallocVar> _fnMalloc = new(StringComparer.Ordinal);

    // ---- function-static locals -----------------------------------------
    // Maps a block-scope `static` variable's source name to the mangled
    // DotCcGlobals field name it lowered to (e.g. counter →
    // __static_tick_counter). Reset per function (StartFn), consulted by
    // Visit(Var) to rewrite in-function references to the global. A static's
    // single instance + persist-across-calls + once-init semantics fall out of
    // C#'s static field for free; the only work is name-mangling (so two
    // functions can each declare `static int x`) and this reference rewrite.
    private readonly Dictionary<string, string> _fnStatics = new(StringComparer.Ordinal);

    // ---- local/param names in the current function -----------------------
    // Raw C names declared as ordinary identifiers (locals, params) in the
    // function being emitted. Reset per function (StartFn), populated at each
    // declaration site. Consulted by Visit(Var) so a local that SHADOWS an
    // enumerator constant of the same name resolves to the local — not to the
    // `EnumName.Member` rewrite, which would emit a non-lvalue and not compile.
    // A flat per-function set (no block-scope stack), which is correct for all
    // realistic code; the only miss is an enumerator referenced in an outer
    // block AFTER a same-named inner-block local closed — a non-pattern. The
    // declaration always reduces before later uses (statements reduce in source
    // order), so the name is registered in time.
    private readonly HashSet<string> _localNames = new(StringComparer.Ordinal);

    // Declared C# type of each local/param in the current function (raw name →
    // type string, e.g. "Color"). Built live alongside _localNames; consulted by
    // Visit(Var) to tag an enum-typed variable read as enum so consumers cast.
    // Reset per function (StartFn). File-scope globals' types live in
    // _globalTypes (TU-lifetime).
    private readonly Dictionary<string, string> _localTypes = new(StringComparer.Ordinal);

    // File-scope (global) variable name → C# type, TU-lifetime. Populated by
    // EmitGlobalFields; consulted by Visit(Var) for globals referenced inside
    // functions (after the per-function _localTypes miss).
    private readonly Dictionary<string, string> _globalTypes = new(StringComparer.Ordinal);

    // Array variable name → its full array CType. Arrays lower to a C# pointer
    // (stackalloc / flattened for multi-dim), so this is the ONLY place the
    // element type + extent(s) survive — needed for `sizeof(arr)` and for
    // rewriting a multi-dim subscript `a[i][j]` to flat pointer arithmetic. A
    // 1-D array is `Arr(Sized(elem), N)`; `int a[2][3]` is the nested
    // `Arr(Arr(Sized(elem), 3), 2)`. _localArrayInfo is per-function (reset in
    // StartFn); _globalArrayInfo is TU-lifetime. A decayed-pointer param array
    // is NOT recorded (it's a genuine pointer for sizeof).
    private readonly Dictionary<string, CType> _localArrayInfo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CType> _globalArrayInfo = new(StringComparer.Ordinal);

    // Parameter names stage here. Params reduce as part of the FnSig BEFORE
    // StartFn establishes the function scope (and clears _localNames), so they
    // can't be registered directly — StartFn drains this into _localNames once
    // the scope exists, then the body's decls add to it live.
    private readonly List<(string Name, string Type)> _pendingParams = new();

    // Register a block-scope declared name for shadow resolution. No-op at file
    // scope (globals can't share an enumerator's name in C anyway).
    private void NoteLocal(string rawName)
    {
        if (_currentFunctionName is not null) { _localNames.Add(rawName); }
    }

    // ---- block-scope local renaming (CS0136 avoidance) -------------------
    // C lets two same-named locals live in nested-vs-enclosing block scopes —
    // e.g. a `v` inside an `if` block AND a separate `v` in the function body
    // (in EITHER textual order). C# rejects that (CS0136: a name in a nested
    // scope can't shadow one in an enclosing scope, regardless of order). Since
    // CS0136 is purely a name-collision rule, we alpha-rename on the fly: a
    // scope stack maps each raw C name to the unique C# identifier it was
    // emitted as, and a per-function used-name set drives a uniquifier so a
    // colliding declaration gets a fresh `name__k`. References (Visit(Var))
    // resolve through the stack innermost→outermost, so each use binds to the
    // declaration it does in C. Frames are pushed at block entry (the
    // `ScopeEnter` marker after `{`, and after a `for (` decl header) and popped
    // at the matching block / stmtForDecl action; params occupy the function's
    // outermost frame (StartFn) and always keep their raw name — a nested local
    // that collides with a param is the one renamed.
    //
    // The rename rides purely the emitted text: every side table (_localTypes,
    // _fnMalloc, _enumerators, array info, …) stays keyed by the RAW C name, so
    // type synthesis / malloc promotion / enum typing are unaffected.
    private readonly List<Dictionary<string, string>> _scopes = new();
    private readonly HashSet<string> _usedLocalNames = new(StringComparer.Ordinal);

    private void PushScope() => _scopes.Add(new Dictionary<string, string>(StringComparer.Ordinal));
    private void PopScope() { if (_scopes.Count > 0) { _scopes.RemoveAt(_scopes.Count - 1); } }

    /// <summary>
    /// Declare a local/param in the current (innermost) scope; returns the
    /// UN-escaped C# identifier to emit for it. The raw name is reused as-is
    /// the first time it appears in a function; a later collision (shadowing
    /// or reuse across scopes) gets a fresh <c>name__k</c> that dodges every
    /// identifier already used in the function. No-op (returns the raw name)
    /// at file scope, where there's no frame.
    /// </summary>
    private string DeclareLocal(string rawName)
    {
        if (_scopes.Count == 0) { return rawName; }
        var emitted = rawName;
        if (_usedLocalNames.Contains(rawName))
        {
            var k = 1;
            while (_usedLocalNames.Contains(emitted = $"{rawName}__{k}")) { k++; }
        }
        _usedLocalNames.Add(emitted);
        _scopes[^1][rawName] = emitted;
        return emitted;
    }

    /// <summary>
    /// Resolve a reference's raw name to the C# identifier its binding
    /// declaration was emitted as, searching the scope stack innermost →
    /// outermost. Null when the name isn't a local/param in scope (so the
    /// caller falls back to the raw name → global / function / builtin).
    /// </summary>
    private string? ResolveLocal(string rawName)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(rawName, out var emitted)) { return emitted; }
        }
        return null;
    }

    // Block-entry hook (`ScopeEnter → ε`, reduced just after `{` / `for (`).
    // Opens a fresh local-name frame; the matching block / stmtForDecl action
    // pops it.
    public EmitContent Visit(C.ScopeEnter n) { PushScope(); return string.Empty; }

    // Decisions consumed by the emit pass (function name, variable name).
    private readonly IReadOnlySet<(string Fn, string Var)> _promotableIn;
    // Results produced by the analysis pass (finalised per function at FuncDef).
    private readonly HashSet<(string Fn, string Var)> _promotableOut = new();
    public IReadOnlySet<(string Fn, string Var)> PromotableMallocVars => _promotableOut;

    /// <param name="promotable">The promotable (function, var) set from the
    /// analysis pass. Null/empty (the default, used by the analysis pass
    /// itself) means no promotion — every malloc decl emits its low-level
    /// form, which is exactly what the analysis pass discards.</param>
    // Dialect-gating sink. Non-null ONLY on the emit pass under -pedantic; null
    // on the analysis pass and on the default permissive path — so gates are a
    // no-op unless gating is requested, and each violation is collected once.
    private readonly DialectGate? _dialectGate;

    public CSharpEmitter(IReadOnlySet<(string Fn, string Var)>? promotable = null, DialectGate? dialectGate = null)
    {
        _promotableIn = promotable ?? new HashSet<(string, string)>();
        _dialectGate = dialectGate;
    }

    // Flag a construct introduced by a standard newer than the active dialect.
    // `introducedEra` is the ISO year (CDialect.Era: 1999 / 2011 / 2023). The
    // source line comes from the construct's first token. No-op when not gating.
    private void Gate(int introducedEra, string feature, Item at)
        => _dialectGate?.RequireMin(introducedEra, feature, at.Position.Line);

    // C90 mixed-declarations gate accumulator (only touched when gating). As a
    // block's StmtList reduces, `_blkContainsDecl` tracks whether the sub-list
    // built so far holds a declaration and `_blkOutOfOrder` whether a declaration
    // follows a non-declaration in it. A single pair suffices despite nesting: a
    // nested block's StmtList + Block reduce fully before any outer stmtsOne
    // resets these for the outer scope. Read once per block at Visit(Block).
    private bool _blkContainsDecl;
    private bool _blkOutOfOrder;

    public int MainArity { get; private set; } = -1;

    public void ResetMainArity() => MainArity = -1;

    // Side channel for struct declarations. C# requires top-level types
    // (struct, class, ref struct) to come AFTER top-level statements (which
    // includes our user functions emitted as static locals). So we can't
    // inline a struct decl into the function-emit stream — it'd land in the
    // statements section and get rejected (CS8803). Instead Visit(StructDef)
    // appends here and returns "" (empty contribution to the function emit);
    // Compiler.EmitCSharp reads StructDecls after the parse and threads it
    // into BuildShell, which inserts it in the type-decl section.
    private readonly StringBuilder _structs = new();
    public string StructDecls => _structs.ToString();
    public void ResetStructDecls() => _structs.Clear();

    // Side channel for file-scope variable declarations. Real C uses
    // these for globals visible across functions (`jmp_buf env;` for
    // setjmp, FILE* logs, opt parsers, etc.). C# top-level local
    // variables can't be captured by static local functions, so we
    // can't emit them as plain `T x;` at the top of the entry block.
    // Instead they collect into a `static unsafe class DotCcGlobals`
    // declared in the type-decls section; the shell adds
    // `using static DotCcGlobals;` so user code resolves the names
    // unqualified.
    private readonly StringBuilder _globals = new();
    public string Globals => _globals.ToString();
    public void ResetGlobals() => _globals.Clear();

    // Exports list: each non-static (external-linkage) C function definition.
    // Tuple is (cName, csharpReturnType, csharpParamList). Library mode reads
    // this list to emit a matching [UnmanagedCallersOnly(EntryPoint = "name")]
    // wrapper per entry; the wrappers delegate to the user-method body so
    // both internal C-to-C calls (direct method invocation) and external
    // C-to-native consumers work without each other knowing.
    public readonly record struct Export(string Name, string ReturnType, string Params);
    private readonly List<Export> _exports = new();
    public IReadOnlyList<Export> Exports => _exports;
    public void ResetExports() => _exports.Clear();

    // Side channel for `using` aliases produced by `typedef Type ID;`. C# 12+
    // permits `using unsafe Alias = Underlying;` at file scope (file-based
    // programs included). They must precede top-level statements, so the
    // Compiler.BuildShell injects them right after the regular `using` block.
    // The `unsafe` modifier makes pointer aliases (`typedef int* IntPtr` →
    // `using unsafe IntPtr = int*;`) legal alongside scalar ones.
    private readonly StringBuilder _aliases = new();
    private readonly HashSet<string> _aliasNames = new(StringComparer.Ordinal);
    public string UsingAliases => _aliases.ToString();
    public void ResetUsingAliases() { _aliases.Clear(); _aliasNames.Clear(); }

    // ---- Function signature visitors ------------------------------------
    // Each FnSig variant extracts (type, name, params) from its position,
    // sets `_currentFunctionName` (consumed by Visit(Var) for `__func__`),
    // and returns a structured EmitContent.FnHeader for the enclosing Fn
    // reduction to combine with the body.

    public EmitContent Visit(C.FnSig n)  // Type ID ( ParamList )
        => StartFn(type: T(n.Arg0), name: T(n.Arg1), pars: T(n.Arg3), isStatic: false);
    public EmitContent Visit(C.FnSigNoArgs n)  // Type ID ( )
        => StartFn(type: T(n.Arg0), name: T(n.Arg1), pars: "", isStatic: false);
    public EmitContent Visit(C.FnSigVoidArgs n)  // Type ID ( void )
        => StartFn(type: T(n.Arg0), name: T(n.Arg1), pars: "", isStatic: false);
    public EmitContent Visit(C.FnSigStatic n)  // static Type ID ( ParamList )
        => StartFn(type: T(n.Arg1), name: T(n.Arg2), pars: T(n.Arg4), isStatic: true);
    public EmitContent Visit(C.FnSigStaticNoArgs n)  // static Type ID ( )
        => StartFn(type: T(n.Arg1), name: T(n.Arg2), pars: "", isStatic: true);
    public EmitContent Visit(C.FnSigStaticVoidArgs n)  // static Type ID ( void )
        => StartFn(type: T(n.Arg1), name: T(n.Arg2), pars: "", isStatic: true);

    private EmitContent.FnHeader StartFn(string type, string name, string pars, bool isStatic)
    {
        // Set the active-function name BEFORE Block reduces — this is the
        // whole point of the FnSig split. Any __func__ inside the body
        // resolves to this directly at Var-visit time.
        _currentFunctionName = name;
        _currentFunctionReturnType = type;
        _fnReturnTypes[name] = type;
        // Fresh per-function scopes (no nested functions in C).
        _fnMalloc.Clear();
        _fnStatics.Clear();
        _localNames.Clear();
        _localTypes.Clear();
        _localArrayInfo.Clear();
        // Fresh local-renaming state, then open the function's outermost frame
        // (the parameter scope). The body's `{` opens a nested frame inside it.
        _scopes.Clear();
        _usedLocalNames.Clear();
        PushScope();
        // Adopt the params staged during this FnSig's ParamList reduction. Each
        // is registered in the param frame with its RAW name (params keep their
        // spelling — DeclareLocal returns the raw name while the used-set is
        // empty), so a nested local that collides is the one that gets renamed.
        foreach (var p in _pendingParams)
        {
            _localNames.Add(p.Name);
            _localTypes[p.Name] = p.Type;
            DeclareLocal(p.Name);
        }
        _pendingParams.Clear();
        return new EmitContent.FnHeader(type, name, pars, isStatic);
    }

    // ---- Function definition / prototype --------------------------------
    // `Fn → FnSig Block` and `Fn → FnSig ';'`. The FnSig has already run
    // and stashed (type/name/params/isStatic) into a typed FnHeader plus
    // set `_currentFunctionName` for the body's Var visits to consume.
    // Now we do the bookkeeping (MainArity, exports list) and emit/clear.

    public EmitContent Visit(C.FuncDef n) => EmitFuncDef(FH(n.Arg0), T(n.Arg1));

    // `extern T f(args) { … }` — an extern function DEFINITION. `extern` is the
    // default linkage for functions, so this is identical to a plain definition;
    // share the emit (bookkeeping, exports, malloc-promotion finalize, emit).
    public EmitContent Visit(C.ExternFnDef n) => EmitFuncDef(FH(n.Arg1), T(n.Arg2));

    // `extern T f(args);` — an extern function PROTOTYPE. Emits nothing (C#
    // methods hoist), but must unwind the FnSig state StartFn set, exactly like
    // a plain prototype.
    public EmitContent Visit(C.ExternFnProto n)
    {
        _currentFunctionName = null;
        _currentFunctionReturnType = null;
        _fnMalloc.Clear();
        _fnStatics.Clear();
        _scopes.Clear();
        _usedLocalNames.Clear();
        return string.Empty;
    }

    // `extern T x;` / `extern T a, b;` — extern VARIABLE declarations. These
    // declare without defining: the storage lives in another translation unit
    // (or another file in this program), so emit NO field — emitting one would
    // double-define against the real definition. Register each name's type so
    // same-file references still resolve as globals.
    public EmitContent Visit(C.ExternVarDecl n)
    {
        var type = T(n.Arg1);
        foreach (var e in DE(n.Arg2)) { _globalTypes[e.Name] = type; }
        return string.Empty;
    }

    private EmitContent EmitFuncDef(EmitContent.FnHeader sig, string body)
    {
        // Bookkeeping: `main` records arity (0 when params are empty,
        // CountCommas+1 otherwise — `int main()` is arity 0, not 1);
        // non-static non-main goes on the exports list for library-mode
        // [UnmanagedCallersOnly] wrappers.
        if (sig.Name == "main")
        {
            MainArity = string.IsNullOrEmpty(sig.Params) ? 0 : CountCommas(sig.Params) + 1;
        }
        else if (!sig.IsStatic) { _exports.Add(new Export(sig.Name, sig.Type, sig.Params)); }
        // Finalise stack-promotion decisions for this function: a candidate is
        // promotable iff its declared type matched `S*`, it's freed at least
        // once, and every reference is accounted for as either a `->` base or a
        // free arg (TotalRefs == ArrowRefs + FreeRefs — no escaping use).
        foreach (var (varName, mv) in _fnMalloc)
        {
            if (mv.TypeMatches && mv.FreeRefs >= 1
                && mv.TotalRefs == mv.ArrowRefs + mv.FreeRefs
                // Only promote genuine struct types — the plan is "stack struct
                // value". A scalar like `int*` can't reach here via `->` anyway,
                // but the gate keeps a degenerate malloc+free of a scalar from
                // lowering to a confusing `int p = new int();`.
                && _structFields.ContainsKey(mv.StructType))
            {
                _promotableOut.Add((sig.Name, varName));
            }
        }
        _fnMalloc.Clear();
        _fnStatics.Clear();
        _currentFunctionName = null;  // exit function scope
        _currentFunctionReturnType = null;
        // Drop the parameter frame (the body block already popped its own); a
        // clean stack between functions means file-scope references resolve as
        // globals, not against a stale frame.
        _scopes.Clear();
        _usedLocalNames.Clear();
        // Escape the method name for C# emission; sig.Name stays raw above for
        // the `main` check and the export list (the C-ABI EntryPoint keeps the
        // real name). A call to this function escapes identically via Visit(Var).
        return $"static unsafe {sig.Type} {Id(sig.Name)}({sig.Params})\n{body}";
    }

    public EmitContent Visit(C.FuncProto n)
    {
        // Prototypes emit nothing — C# methods hoist. We still need to
        // unwind the FnSig's _currentFunctionName since the body wasn't
        // visited but the name was set, and drop the param frame StartFn pushed
        // (no body Block ran to pop a nested one).
        _currentFunctionName = null;
        _currentFunctionReturnType = null;
        _scopes.Clear();
        _usedLocalNames.Clear();
        return string.Empty;
    }

    // `struct ID { fields } ;` — emit a C# struct declaration into the side
    // channel; contribute nothing to the function-emit stream. The struct
    // is marked `unsafe` so it can legally contain pointer fields; all our
    // C structs are by definition unmanaged (no GC refs in their fields)
    // so this is sound.
    public EmitContent Visit(C.StructDef n)
    {
        var name = T(n.Arg1);
        var members = T(n.Arg3);
        DrainPendingFields(name);
        _structs.Append("unsafe struct ").Append(name).Append("\n{\n");
        _structs.Append(IndentEach(members));
        _structs.Append("}\n\n");
        return string.Empty;
    }

    // `struct Node ;` — forward declaration. C# resolves type references
    // regardless of declaration order, so we emit nothing. The full
    // StructDef (if any) lands later in the same translation unit and
    // populates _structFields then.
    public EmitContent Visit(C.StructFwd n) => string.Empty;

    // File-scope variable declarations — `int x;`, `int x = 5;`, and the
    // multi-declarator forms `int a, b;` / `int a = 1, b = 2;` /
    // `int a, b = 5;`. Each declarator appends one `public static unsafe`
    // field to the _globals side channel; the shell wraps them in a
    // `static unsafe class DotCcGlobals` declared in the type-decls
    // section. Type runs through QualifyPredefinedTypeName so
    // `jmp_buf env;` (which after typedef lowering references
    // `LongJmpToken`) reaches `Libc.LongJmpToken` correctly — bare
    // nested-type names don't resolve at class-member-decl position
    // for the same reason the alias-emit path qualifies them.
    //
    // Reference types (currently just `LongJmpToken` + its `jmp_buf`
    // typedef alias) get an auto `= new T()` for no-init entries —
    // C# default-inits class-typed fields to null, and the longjmp
    // exception filter compares tokens by reference identity, so a
    // null env would silently break setjmp/longjmp dispatch.
    public EmitContent Visit(C.GlobalDeclList n)
    {
        EmitGlobalFields(T(n.Arg0), DE(n.Arg1));
        return string.Empty;
    }

    // File-scope `static T x;`. Internal linkage is a no-op for variables in
    // dotcc's single-program model (they're never exported), so it lowers
    // exactly like a plain global — the `static` keyword (Arg0) is consumed.
    public EmitContent Visit(C.GlobalStaticDeclList n)
    {
        EmitGlobalFields(T(n.Arg1), DE(n.Arg2));
        return string.Empty;
    }

    // Emit one `public static unsafe` field per init-declarator into the
    // DotCcGlobals side channel. `rawType` runs through QualifyPredefinedTypeName
    // so e.g. `jmp_buf`/`LongJmpToken` resolve at class-member position; ref
    // types get an auto `= new T()` for the no-init form (see GlobalDeclList's
    // original notes). Shared by file-scope globals AND function-static
    // locals (which pass already-mangled names).
    private void EmitGlobalFields(string rawType, IReadOnlyList<EmitContent.DeclEntry> entries)
    {
        var type = QualifyPredefinedTypeName(rawType);
        var isRefType = IsPredefinedRefTypeName(rawType) || _refTypeAliases.Contains(rawType);
        foreach (var entry in entries)
        {
            // Track global var → type, consulted by Visit(Var) for enum coercion
            // (gated on _enumTags there) AND by sizeof. Stored unconditionally so
            // `sizeof(globalScalar)` resolves; non-enum types simply never match
            // the _enumTags check, so this is safe.
            _globalTypes[entry.Name] = rawType;
            string init;
            if (entry.Init is not null)
            {
                // Reconcile enum-ness against the declared type (same as block scope).
                init = $" = {ReconcileEnumInit(rawType, entry)}";
            }
            else if (isRefType)
            {
                init = $" = new {type}()";
            }
            else
            {
                // Value-type field with no initializer — C# zero-inits
                // class fields, which matches C's static-storage default
                // (all zero-bits). No explicit initializer needed.
                init = string.Empty;
            }
            _globals.Append("    public static unsafe ").Append(type).Append(' ').Append(Id(entry.Name)).Append(init).Append(";\n");
        }
    }

    public EmitContent Visit(C.MembersCons n) => T(n.Arg0) + T(n.Arg1);
    public EmitContent Visit(C.MembersOne n)  => T(n.Arg0);
    // `Type ID ;` member — emit as public field. C convention is that all
    // struct fields are accessible to anyone with a pointer; matching that
    // requires `public` in C#. Field names also pushed onto _pendingFields
    // so the enclosing StructDef / TypedefStruct / UnionDef can index them
    // by struct name for the aggregate-init lookup later.
    public EmitContent Visit(C.StructMember n)
    {
        var fieldName = T(n.Arg1);
        _pendingFields.Add(fieldName);  // raw name — keyed lookups stay un-escaped
        return $"public {T(n.Arg0)} {Id(fieldName)};\n";
    }

    // Named bit-field `Type ID : width ;`. C# has no bit-fields, so dotcc emits
    // a FULL field of the declared type and DROPS the width (lossy lowering):
    // correct for values that fit the width — the common case — but it does NOT
    // truncate / wrap on overflow, and the struct's size & layout differ from C.
    // A faithful packed lowering (backing storage + masked accessors) is future
    // work. Documented in C-SUPPORT.md.
    public EmitContent Visit(C.StructBitField n)
    {
        var fieldName = T(n.Arg1);
        _pendingFields.Add(fieldName);
        return $"public {T(n.Arg0)} {Id(fieldName)}; // C bit-field :{StripOuterParens(T(n.Arg3))} (width dropped)\n";
    }

    // Anonymous bit-field `Type : width ;` — pure padding/alignment in C, with
    // no accessible member. Nothing to emit.
    public EmitContent Visit(C.StructAnonBitField n) => string.Empty;

    private void DrainPendingFields(string typeName)
    {
        _structFields[typeName] = new List<string>(_pendingFields);
        _pendingFields.Clear();
    }
    // `struct ID` as a type reference — emit just the ID. C# doesn't use the
    // `struct` keyword in usage position (only in declaration), and the
    // generated struct decl shares the same name.
    public EmitContent Visit(C.TypeStruct n) => T(n.Arg1);

    // `enum ID` as a type reference — emit the enum tag name (a real C# enum
    // type shares it). The enum-typing synthesis inserts int↔enum casts at use
    // sites; the `struct`/`union` keyword likewise drops in usage position.
    public EmitContent Visit(C.TypeEnum n) => T(n.Arg1);

    // `union ID` as a type reference — emit just the ID. The
    // [StructLayout(LayoutKind.Explicit)] struct declaration shares the name.
    public EmitContent Visit(C.TypeUnion n) => T(n.Arg1);

    // `union Name { Type f1; Type f2; … } ;` — emit a C# struct with
    // [StructLayout(LayoutKind.Explicit)] and [FieldOffset(0)] on each
    // member, giving C's overlapping-storage semantics. Reuses the
    // MemberList parsed for struct (one `Type ID ;` per member).
    public EmitContent Visit(C.UnionDef n)
    {
        var name = T(n.Arg1);
        var members = T(n.Arg3);
        DrainPendingFields(name);
        _structs.Append("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit)]\n");
        _structs.Append("unsafe struct ").Append(name).Append("\n{\n");
        // Members come in as `public T NAME;\n` lines from StructMember;
        // inject [FieldOffset(0)] before each `public` declaration.
        foreach (var line in members.Split('\n'))
        {
            if (line.Length == 0) { continue; }
            _structs.Append("    [global::System.Runtime.InteropServices.FieldOffset(0)] ");
            _structs.Append(line).Append('\n');
        }
        _structs.Append("}\n\n");
        return string.Empty;
    }

    // Map from enumerator name → containing enum name. Populated by
    // Visit(EnumDef); consulted by Visit(Var) so unqualified `Red` in user
    // code becomes `Color.Red` in the emitted C#. Keeps the C# namespace
    // clean (no top-level pollution by every enumerator) while preserving
    // the source-level convenience of writing the bare name.
    private readonly Dictionary<string, string> _enumerators = new(StringComparer.Ordinal);

    // Set of enum tag names (the `Color` in `enum Color { … }`). Populated by
    // Visit(EnumDef); consulted to decide whether a variable's declared type is
    // an enum (so a read of it is enum-typed) and whether a `(T)` cast targets
    // an enum. TU-lifetime (enums are file-scope).
    private readonly HashSet<string> _enumTags = new(StringComparer.Ordinal);

    // Function (raw C name) → emitted C# return type, TU-lifetime. Populated at
    // each FnSig (StartFn); consulted by Visit(Call) to tag a call's result with
    // its enum type when the callee returns an enum, so the result reconciles in
    // a consuming context (e.g. `int x = next(c)` decays, `Color d = next(c)`
    // needs no cast). Misses only a call placed before the callee's definition.
    private readonly Dictionary<string, string> _fnReturnTypes = new(StringComparer.Ordinal);

    // Struct/union/typedef-struct field-name tracker. Same precedent as
    // `_enumerators`: visitor-time symbol table. StructMember pushes each
    // field's name onto `_pendingFields` during child-visit; the enclosing
    // StructDef / TypedefStruct / UnionDef drains it into `_structFields`
    // keyed by the type name. The struct-aggregate-init visitor consults
    // this so `Point p = {1, 2};` lands as `Point p = new Point { x = 1, y = 2 };`
    // (C# has no positional-init form for structs — it needs named members).
    private readonly List<string> _pendingFields = new();
    private readonly Dictionary<string, List<string>> _structFields = new(StringComparer.Ordinal);

    // `enum Name { A, B = 5, C } ;` — emit a real C# `enum Name : int { … }`
    // into the type-decl side channel. A genuine enum (not the old `static class
    // { const int }`) preserves the C type name and makes `switch`/`case`,
    // type-safe params, and ToString work; the int↔enum casts C requires but C#
    // doesn't (`int x = c`, `c & MASK`, `Color c = 2`) are inserted by the
    // enum-typing synthesis at the consuming nodes. Enum members are compile-time
    // constants in C#, so `case Red:` and `int a[Blue]` still work. C# enum
    // auto-numbering matches C (start 0, prev+1), but we emit each resolved value
    // explicitly so a non-literal initializer (`1 << 2`, `A + 1`) round-trips.
    // Returns empty — type decls live in the side channel, after statements.
    public EmitContent Visit(C.EnumDef n) => EmitEnum(T(n.Arg1), EI(n.Arg3), "int");

    // C23 `enum Name : Type { … }` — the fixed underlying type. The Type
    // non-terminal already resolves an integer specifier to a C# integral type
    // (byte/sbyte/short/ushort/int/uint/long/ulong), each of which is a valid C#
    // enum base, so it passes straight through as the base. rhs indices:
    // enum(0) ID(1) :(2) Type(3) {(4) EnumList(5) }(6) ;(7).
    public EmitContent Visit(C.EnumDefTyped n)
    {
        Gate(2023, "enum with fixed underlying type (`enum : T`)", n.Arg0);  // C23
        return EmitEnum(T(n.Arg1), EI(n.Arg5), T(n.Arg3));
    }

    // Shared by plain `enum Name { … }` (base int) and the C23 fixed-underlying
    // form `enum Name : T { … }` (base = mapped C# integral type).
    private EmitContent EmitEnum(string enumName, IReadOnlyList<string> items, string baseType)
    {
        _enumTags.Add(enumName);
        _structs.Append("enum ").Append(enumName).Append(" : ").Append(baseType).Append("\n{\n");
        var next = 0L;
        foreach (var raw in items)
        {
            var eq = raw.IndexOf('=');
            string itemName;
            string valueText;
            if (eq < 0)
            {
                itemName = raw;
                valueText = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                next++;
            }
            else
            {
                itemName = raw[..eq];
                var expr = raw[(eq + 1)..];
                // When the explicit value is a literal int we use it as the
                // numeric basis for downstream auto-numbering. If the
                // expression isn't a plain literal (e.g. `1 << 2`), emit it
                // verbatim and best-effort advance `next` by 1.
                if (long.TryParse(expr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    valueText = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    next = parsed + 1;
                }
                else
                {
                    valueText = expr;
                    next++;
                }
            }
            _enumerators[itemName] = enumName;  // raw key — Visit(Var) looks up by raw name
            _structs.Append("    ").Append(Id(itemName)).Append(" = ").Append(valueText).Append(",\n");
        }
        _structs.Append("}\n\n");
        return string.Empty;
    }

    // EnumList accumulator — produces a typed EmitContent.EnumItems list.
    // Each element is either "name" (no explicit value) or "name=expr" so
    // EnumDef can split with one IndexOf('='). No sentinel chars.
    public EmitContent Visit(C.EnumListOne n) => new EmitContent.EnumItems(new[] { T(n.Arg0) });
    public EmitContent Visit(C.EnumListCons n)
    {
        var prev = EI(n.Arg0);
        var next = T(n.Arg2);
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(next);
        return new EmitContent.EnumItems(combined);
    }
    public EmitContent Visit(C.EnumItem n)     => T(n.Arg0);
    public EmitContent Visit(C.EnumItemInit n) => $"{T(n.Arg0)}={T(n.Arg2)}";

    // `Type -> TYPE_NAME` — the rewriter-synthesised terminal carrying a
    // typedef'd name. The Content is the raw identifier string; emit it
    // verbatim since the using-alias (or struct decl) we emitted for the
    // typedef already binds that name in C#'s namespace.
    public EmitContent Visit(C.TypeName n) => T(n.Arg0);

    // `typedef Type ID ;` — register an `using unsafe Alias = Type;` line in
    // the aliases side channel. Suppressed when Alias == Type (e.g.
    // `typedef struct Foo Foo;` where Type already lowers to `Foo`) since
    // C# rejects a self-alias and the struct named Foo already exists.
    // Suppressed too when the alias was already emitted earlier in the same
    // translation unit (deduplication — real C allows redeclaration to the
    // same type, real C# rejects duplicate aliases).
    public EmitContent Visit(C.TypedefAlias n)
    {
        var rawType = T(n.Arg1);
        var type = QualifyPredefinedTypeName(rawType);
        var alias = T(n.Arg2);
        // If the alias resolves (directly or transitively) to a predefined
        // reference type, record the alias so GlobalVar can auto-init
        // instances. `jmp_buf` → `LongJmpToken` is the canonical case.
        if (IsPredefinedRefTypeName(rawType) || _refTypeAliases.Contains(rawType))
        {
            _refTypeAliases.Add(alias);
        }
        if (alias != type && _aliasNames.Add(alias))
        {
            _aliases.Append("using unsafe ").Append(alias).Append(" = ").Append(type).Append(";\n");
        }
        return string.Empty;
    }

    /// <summary>
    /// Set of typedef'd alias names whose underlying type is a
    /// predefined C# reference type (currently just <c>LongJmpToken</c>
    /// from <c>&lt;setjmp.h&gt;</c>). Used by <see cref="Visit(C.GlobalVar)"/>
    /// to auto-instantiate the field — without an initializer the C#
    /// field would default to <c>null</c>, which breaks the longjmp
    /// exception filter that compares tokens by reference identity.
    /// </summary>
    private readonly HashSet<string> _refTypeAliases = new(StringComparer.Ordinal);

    private static bool IsPredefinedRefTypeName(string typeText)
    {
        foreach (var name in Compiler.PredefinedTypeNames)
        {
            if (typeText == name) { return true; }
        }
        return false;
    }

    /// <summary>
    /// In a <c>using unsafe Alias = X;</c> directive, C#'s name resolution
    /// for X does NOT consult <c>using static</c> directives in the same
    /// file — it only sees the enclosing namespace + type-alias usings.
    /// So a nested type like <c>Libc.LongJmpToken</c>, even with
    /// <c>using static Libc;</c> declared above, doesn't resolve as the
    /// bare <c>LongJmpToken</c> when used as the RHS of a type alias.
    /// Qualify it. The PredefinedTypeNames list (see Compiler) is small
    /// and known; we prefix those with <c>Libc.</c> when emitting alias
    /// directives. Inside method bodies the bare name still works via
    /// <c>using static</c>, so this only affects the alias-emit path.
    /// </summary>
    private static string QualifyPredefinedTypeName(string type)
    {
        foreach (var name in Compiler.PredefinedTypeNames)
        {
            if (type == name) { return "Libc." + name; }
        }
        return type;
    }

    // `typedef Ret (*Name)(args);` → `using unsafe Name = delegate*<args, Ret>;`.
    // C# function-pointer types put the return type LAST in the type arg
    // list (opposite of C's "return type first" syntax). The visitor strips
    // parameter names from the ParamList — C# function pointers are
    // by-type-only — by splitting on commas and dropping the trailing ID
    // from each "Type ID" chunk.
    public EmitContent Visit(C.TypedefFnPtr n)
    {
        var ret = T(n.Arg1);
        var name = T(n.Arg4);
        var pars = T(n.Arg7);
        var typesOnly = StripParamNames(pars);
        // A function-pointer TYPE's parameters aren't real parameters of any
        // function definition — but the Param visitors still staged them into
        // _pendingParams. Discard them: only an FnSig's StartFn should adopt
        // staged params, and leaking these would corrupt the next function's
        // parameter scope (its names + local-rename used-set).
        _pendingParams.Clear();
        _aliasNames.Add(name);
        _aliases.Append("using unsafe ").Append(name).Append(" = delegate*<")
            .Append(typesOnly).Append(", ").Append(ret).Append(">;\n");
        return string.Empty;
    }

    public EmitContent Visit(C.TypedefFnPtrNoArgs n)
    {
        var ret = T(n.Arg1);
        var name = T(n.Arg4);
        _aliasNames.Add(name);
        _aliases.Append("using unsafe ").Append(name).Append(" = delegate*<")
            .Append(ret).Append(">;\n");
        return string.Empty;
    }

    private static string StripParamNames(string paramList)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var p in paramList.Split(", "))
        {
            // Each "Type ID" — last space separates type from name. Type
            // emission has no internal spaces (`int**` not `int * *`), so
            // taking everything before the last space is safe.
            var sp = p.LastIndexOf(' ');
            var typeOnly = sp < 0 ? p : p[..sp];
            if (!first) { sb.Append(", "); }
            sb.Append(typeOnly);
            first = false;
        }
        return sb.ToString();
    }

    // `typedef struct ID { MemberList } ID ;` — emit the struct under the
    // alias name (the trailing ID). When tag != alias, also bind the tag as
    // a `using` alias so code using `struct Tag` typeref form also resolves.
    public EmitContent Visit(C.TypedefStruct n)
    {
        var tag = T(n.Arg2);
        var members = T(n.Arg4);
        var alias = T(n.Arg6);
        // Index fields under BOTH the alias and the tag — code may refer to
        // the type by either name (and `using unsafe Tag = Alias;` below
        // makes the tag a real type reference too).
        var fields = new List<string>(_pendingFields);
        _structFields[alias] = fields;
        if (tag != alias) { _structFields[tag] = fields; }
        _pendingFields.Clear();
        _structs.Append("unsafe struct ").Append(alias).Append("\n{\n");
        _structs.Append(IndentEach(members));
        _structs.Append("}\n\n");
        if (tag != alias && _aliasNames.Add(tag))
        {
            _aliases.Append("using unsafe ").Append(tag).Append(" = ").Append(alias).Append(";\n");
        }
        return string.Empty;
    }

    public EmitContent Visit(C.FnsCons n) =>
        T(n.Arg0) + ((T(n.Arg0)).Length > 0 ? "\n\n" : "") + T(n.Arg1);

    public EmitContent Visit(C.FnsOne n) => T(n.Arg0);

    // Params
    public EmitContent Visit(C.Param n) { _pendingParams.Add((T(n.Arg1), T(n.Arg0))); return $"{T(n.Arg0)} {Id(T(n.Arg1))}"; }
    // Unnamed (abstract) parameter — `int f(int, char*)` or a function-pointer
    // type's params. C# requires a parameter name, so synthesize a unique one
    // (`_p0`, `_p1`, …). The counter only needs to be unique within a list and
    // deterministic across the analysis/emit passes — a monotonic counter is
    // both. For a fn-ptr type the name is dropped by StripParamNames; for a real
    // prototype/definition the body can't reference it (matching C).
    public EmitContent Visit(C.ParamUnnamed n)
    {
        var type = T(n.Arg0);
        var name = "_p" + _unnamedParamSeq++;
        _pendingParams.Add((name, type));
        return $"{type} {Id(name)}";
    }
    private int _unnamedParamSeq;
    // C array-parameter decay: `T arr[]` / `T arr[N]` ≡ `T* arr` per
    // C99 §6.7.5.3p7. The size in the sized form is informational only —
    // we discard it (intentionally don't evaluate Arg3) since C semantics
    // give the call site no way to observe a mismatch anyway.
    public EmitContent Visit(C.ParamArrayUnsized n) { _pendingParams.Add((T(n.Arg1), T(n.Arg0) + "*")); return $"{T(n.Arg0)}* {Id(T(n.Arg1))}"; }
    public EmitContent Visit(C.ParamArraySized n) { _pendingParams.Add((T(n.Arg1), T(n.Arg0) + "*")); return $"{T(n.Arg0)}* {Id(T(n.Arg1))}"; }
    public EmitContent Visit(C.ParamsCons n) => $"{T(n.Arg0)}, {T(n.Arg2)}";
    public EmitContent Visit(C.ParamsOne n) => T(n.Arg0);
    public EmitContent Visit(C.ParamsVararg n) => $"{T(n.Arg0)}, params object[] _va";

    // Types — pointer composition + tag types stay direct; everything that
    // accumulates declaration specifiers (signed/unsigned, short/long, int/
    // char/float/double/void) goes through TypeSpec → TypeSpecList →
    // ResolveTypeSpec, matching how real C compilers handle the
    // free-order specifier sequence.
    public EmitContent Visit(C.TypePtr n) => $"{T(n.Arg0)}*";
    // `T * const` / `T * volatile` / `T * restrict` — a qualifier after the
    // pointer star. dotcc has no C# equivalent (no readonly locals, no aliasing
    // model), so the qualifier is dropped: the type is just the pointer. (A
    // future optimization could turn a `restrict` parameter into a by-ref /
    // no-alias hint.)
    public EmitContent Visit(C.TypePtrQualConst n)    => $"{T(n.Arg0)}*";
    public EmitContent Visit(C.TypePtrQualVolatile n) => $"{T(n.Arg0)}*";
    public EmitContent Visit(C.TypePtrQualRestrict n) => $"{T(n.Arg0)}*";

    // Each TypeSpec keyword maps to its own bracketed marker — `<int>`,
    // `<unsigned>`, `<_Bool>` etc. Bracketing makes the markers
    // self-delimiting (no opaque single-char shorthand to memorise) and
    // makes accumulated lists trivially parseable: `<unsigned><long><int>`.
    // TypeSpecList concatenates; ResolveTypeSpec splits on `<...>` segments.
    // TypeSpec visitors emit single-element SpecList; TypeSpecList* accumulate
    // them. TypeFromSpec resolves the multiset to a final C# type name.
    // No more sentinel-encoded marker strings — the list IS the schema.
    private static EmitContent.SpecList Spec(string kw) => new(new[] { kw });

    public EmitContent Visit(C.TsInt n)      => Spec("int");
    public EmitContent Visit(C.TsChar n)     => Spec("char");
    public EmitContent Visit(C.TsFloat n)    => Spec("float");
    public EmitContent Visit(C.TsDouble n)   => Spec("double");
    public EmitContent Visit(C.TsVoid n)     => Spec("void");
    public EmitContent Visit(C.TsShort n)    => Spec("short");
    public EmitContent Visit(C.TsLong n)     => Spec("long");
    public EmitContent Visit(C.TsUnsigned n) => Spec("unsigned");
    public EmitContent Visit(C.TsSigned n)   => Spec("signed");
    public EmitContent Visit(C.TsBool n)     => Spec("_Bool");
    public EmitContent Visit(C.TsFloat128 n) => Spec("Float128");
    // Type qualifiers — accumulate into the spec list but ResolveTypeSpec's
    // switch has no case for them, so they're silently dropped (C# has no
    // equivalent). `const char *p` lowers exactly like `char *p`.
    public EmitContent Visit(C.TsConst n)    => Spec("const");
    public EmitContent Visit(C.TsVolatile n) => Spec("volatile");

    public EmitContent Visit(C.TypeSpecListOne n)  => S(n.Arg0) is var specs
        ? new EmitContent.SpecList(specs) : throw new InvalidOperationException();

    public EmitContent Visit(C.TypeSpecListCons n)
    {
        var prev = S(n.Arg0);
        var next = S(n.Arg1);
        var combined = new List<string>(prev.Count + next.Count);
        combined.AddRange(prev);
        combined.AddRange(next);
        return new EmitContent.SpecList(combined);
    }

    public EmitContent Visit(C.TypeFromSpec n)
    {
        var specs = S(n.Arg0);
        // Dialect gates for type-specifier features (once per resolved type,
        // with a source line). `_Bool`/`long long` are C99; `_Float128` is C23.
        if (_dialectGate is not null)
        {
            var longs = 0;
            foreach (var s in specs)
            {
                if (s == "_Bool") { Gate(1999, "_Bool", n.Arg0); }
                else if (s == "Float128") { Gate(2023, "_Float128 / __float128", n.Arg0); }
                else if (s == "long") { longs++; }
            }
            if (longs >= 2) { Gate(1999, "long long", n.Arg0); }
        }
        return ResolveTypeSpec(specs);
    }

    /// <summary>
    /// Resolve a declaration-specifier marker string (concatenated by
    /// TypeSpec/TypeSpecList visitors) to a C# type name. Order-insensitive:
    /// `long unsigned int` and `unsigned int long` both produce <c>"LUi"</c>
    /// which resolves to <c>"ulong"</c>. Long and long-long both map to
    /// C# <c>long</c> (64-bit unconditionally in C#) — dotcc accepts the
    /// MSVC 32-bit `long` semantic loss as a documented quirk.
    /// </summary>
    private static string ResolveTypeSpec(IReadOnlyList<string> specs)
    {
        // Single-pass count of each specifier class. Duplicates AND
        // contradictions surface in the same loop. The input list IS the
        // typed schema (EmitContent.SpecList) — no string encoding, no
        // regex parsing, no Contains brittleness.
        var unsignedCount = 0;
        var signedCount = 0;
        var shortCount = 0;
        var longCount = 0;
        var boolCount = 0;
        var float128Count = 0;
        string? baseKw = null;
        var baseCount = 0;
        var baseConflict = false;

        foreach (var kw in specs)
        {
            switch (kw)
            {
                case "unsigned": unsignedCount++; break;
                case "signed":   signedCount++; break;
                case "short":    shortCount++; break;
                case "long":     longCount++; break;
                case "_Bool":    boolCount++; break;
                case "Float128": float128Count++; break;
                case "int":
                case "char":
                case "float":
                case "double":
                case "void":
                    if (baseKw is null) { baseKw = kw; baseCount = 1; }
                    else if (baseKw == kw) { baseCount++; }
                    else { baseConflict = true; }
                    break;
            }
        }

        // Validation. Each rule mirrors a real-C diagnostic.
        if (boolCount > 0 && (boolCount > 1 || baseKw is not null
            || unsignedCount > 0 || signedCount > 0 || shortCount > 0 || longCount > 0))
        {
            throw new CompileException(
                $"`_Bool` cannot be combined with other type specifiers (got `{PrettySpecs(specs)}`)");
        }
        if (float128Count > 0 && (float128Count > 1 || boolCount > 0 || baseKw is not null
            || unsignedCount > 0 || signedCount > 0 || shortCount > 0 || longCount > 0))
        {
            throw new CompileException(
                $"`_Float128` cannot be combined with other type specifiers (got `{PrettySpecs(specs)}`)");
        }
        if (unsignedCount > 0 && signedCount > 0)
        {
            throw new CompileException(
                $"cannot combine `signed` and `unsigned` (got `{PrettySpecs(specs)}`)");
        }
        if (unsignedCount > 1)
        {
            throw new CompileException(
                $"duplicate `unsigned` specifier (got `{PrettySpecs(specs)}`)");
        }
        if (signedCount > 1)
        {
            throw new CompileException(
                $"duplicate `signed` specifier (got `{PrettySpecs(specs)}`)");
        }
        if (shortCount > 0 && longCount > 0)
        {
            throw new CompileException(
                $"cannot combine `short` and `long` (got `{PrettySpecs(specs)}`)");
        }
        if (shortCount > 1)
        {
            throw new CompileException(
                $"duplicate `short` specifier (got `{PrettySpecs(specs)}`)");
        }
        if (longCount > 2)
        {
            throw new CompileException(
                $"cannot have more than two `long`s (got `{PrettySpecs(specs)}`)");
        }
        if (baseConflict)
        {
            throw new CompileException(
                $"cannot combine multiple base types (got `{PrettySpecs(specs)}`)");
        }
        if (baseCount > 1)
        {
            throw new CompileException(
                $"duplicate `{baseKw}` specifier (got `{PrettySpecs(specs)}`)");
        }

        // `float` / `void` take no size or sign modifiers. `double` takes the
        // single size modifier C allows on a real type — `long double` — which
        // dotcc lowers to C# `double`. The CLI is dotcc's target "ABI", and
        // `double` is the widest IEEE float it offers: there's no wider managed
        // type to map to and no hardware `long double` width (x87 80-bit,
        // aarch64 binary128) to chase, since we emit IL, not native code. This
        // mirrors dotcc's existing `long long` → C# `long` collapse (both
        // 64-bit on the CLI) — a documented narrowing on platforms whose native
        // `long double` is wider; `_Float128` remains the route to true 128-bit.
        // `double` still rejects `short`, sign, and a second `long`.
        if (baseKw is "float" or "void")
        {
            if (unsignedCount > 0 || signedCount > 0 || shortCount > 0 || longCount > 0)
            {
                throw new CompileException(
                    $"`{baseKw}` cannot take size or sign modifiers (got `{PrettySpecs(specs)}`)");
            }
        }
        else if (baseKw == "double")
        {
            if (unsignedCount > 0 || signedCount > 0 || shortCount > 0)
            {
                throw new CompileException(
                    $"`double` cannot take sign or `short` modifiers (got `{PrettySpecs(specs)}`)");
            }
            if (longCount > 1)
            {
                throw new CompileException(
                    $"`long long double` is not a valid type (got `{PrettySpecs(specs)}`)");
            }
            // longCount == 1 → `long double`, accepted; resolves to `double` below.
        }

        // Resolve. Order: _Bool first (mutually exclusive), then non-int
        // bases, then char (with signedness), then sized-int family.
        if (boolCount == 1) { return "CBool"; }
        if (float128Count == 1) { return "Float128"; }
        if (baseKw == "float")  { return "float"; }
        if (baseKw == "double") { return "double"; }  // incl. `long double`
        if (baseKw == "void")   { return "void"; }
        if (baseKw == "char")
        {
            // dotcc's `char` is `byte` (unsigned). `signed char` → sbyte.
            return signedCount > 0 ? "sbyte" : "byte";
        }
        if (shortCount > 0) { return unsignedCount > 0 ? "ushort" : "short"; }
        if (longCount > 0)  { return unsignedCount > 0 ? "ulong"  : "long"; }
        return unsignedCount > 0 ? "uint" : "int";
    }

    /// <summary>
    /// Render a typed specifier list back as space-separated C keywords,
    /// preserving the order the user wrote them. Used only for error
    /// messages so they read like a real compiler diagnostic.
    /// </summary>
    private static string PrettySpecs(IReadOnlyList<string> specs) =>
        string.Join(" ", specs);

    // Block / statements
    public EmitContent Visit(C.Block n)
    {
        // C90 forbids a declaration after a statement in a block (mixed
        // declarations and code is C99). The StmtList reductions below set
        // _blkOutOfOrder for this block; report it once here. n.Arg0 is `{`,
        // Arg1 is the ScopeEnter marker, Arg2 the StmtList.
        if (_dialectGate is not null && _blkOutOfOrder)
        {
            Gate(1999, "mixed declarations and statements", n.Arg0);
        }
        var body = "{\n" + IndentEach(T(n.Arg2)) + "}\n";
        PopScope();  // close the frame ScopeEnter opened after `{`
        return body;
    }
    public EmitContent Visit(C.BlockEmpty n) { PopScope(); return "{ }\n"; }
    // StmtList builds right-to-left (Arg0 = this statement, Arg1 = the rest).
    // Track, for the C90 mixed-declarations gate, whether the sub-list holds a
    // declaration and whether a declaration follows a non-declaration in it.
    public EmitContent Visit(C.StmtsCons n)
    {
        if (_dialectGate is not null)
        {
            var thisIsDecl = n.Arg0.Content is EmitContent.DeclStmtMarker;
            // Arg0 precedes the rest: a non-decl here with a decl later is mixed.
            if (!thisIsDecl && _blkContainsDecl) { _blkOutOfOrder = true; }
            _blkContainsDecl = thisIsDecl || _blkContainsDecl;
        }
        return T(n.Arg0) + T(n.Arg1);
    }
    public EmitContent Visit(C.StmtsOne n)
    {
        if (_dialectGate is not null)
        {
            // Rightmost statement — starts a fresh per-block accumulator.
            _blkContainsDecl = n.Arg0.Content is EmitContent.DeclStmtMarker;
            _blkOutOfOrder = false;
        }
        return T(n.Arg0);
    }
    // Conditions are wrapped with B(...) so int- and pointer-valued conditions
    // (`while (1)`, `if (p)`, `for (...; n; ...)`) typecheck. The B overloads
    // live in BuildShell — see Compiler.BuildShell.
    public EmitContent Visit(C.StmtIf n)
    {
        // setjmp in an `if` without an `else` can't be locally rewritten
        // — there's no "normal path" to put in the try block. Fail with
        // a clear hint; the user should add an else branch.
        if (n.Arg2.Content is EmitContent.SetjmpCall
            or EmitContent.SetjmpCheckZero)
        {
            throw new CompileException(
                "setjmp(env) in an `if` condition requires a matching `else` clause; " +
                "see the supported patterns in <setjmp.h>'s header comment.");
        }
        return $"if ({CondOf(n.Arg2)}) {T(n.Arg4)}";
    }

    public EmitContent Visit(C.StmtIfElse n)
    {
        // Setjmp recognition via AST inspection — no text matching.
        // Visit(Call) for setjmp returns a SetjmpCall variant; Visit(Eq)/
        // Visit(Neq) escalate to SetjmpCheckZero when one operand is
        // setjmp and the other is the literal 0. Both shapes get the
        // try/catch rewrite here.
        switch (n.Arg2.Content)
        {
            case EmitContent.SetjmpCall sj:
                // `if (setjmp(env))         { recovery } else { normal }`
                //   setjmp is truthy ONLY on the longjmp re-entry, so:
                //     then-branch = recovery (catch body)
                //     else-branch = normal   (try body)
                return EmitSetjmpRewrite(sj.EnvName, tryBody: T(n.Arg6), catchBody: T(n.Arg4));

            case EmitContent.SetjmpCheckZero scz:
                // `if (setjmp(env) == 0)    { normal }   else { recovery }`
                //   TruthyOnFirstCall=true: then-branch = normal, else = recovery.
                // `if (setjmp(env) != 0)    { recovery } else { normal }`
                //   TruthyOnFirstCall=false: then-branch = recovery, else = normal.
                var (tryBody, catchBody) = scz.TruthyOnFirstCall
                    ? (T(n.Arg4), T(n.Arg6))
                    : (T(n.Arg6), T(n.Arg4));
                return EmitSetjmpRewrite(scz.EnvName, tryBody, catchBody);

            default:
                return $"if ({CondOf(n.Arg2)}) {T(n.Arg4)}else {T(n.Arg6)}";
        }
    }

    /// <summary>
    /// Emit the <c>try / catch when</c> shape that lowers a recognised
    /// <c>setjmp/longjmp</c> idiom. The <c>when</c> filter matches on
    /// the env-token identity so nested setjmps stay disambiguated.
    /// </summary>
    private static string EmitSetjmpRewrite(string envName, string tryBody, string catchBody) =>
        $"try {tryBody}" +
        $"catch (Libc.LongJmpException __jmp) when (__jmp.Token == {envName}) " +
        $"{{ var __longjmp_value = __jmp.Value; {catchBody} }}";
    public EmitContent Visit(C.StmtWhile n) => $"while ({CondOf(n.Arg2)}) {T(n.Arg4)}";

    // `do Stmt while (E) ;` — body runs at least once. C# accepts the same
    // shape; only the condition needs Cond.B wrapping. Note the trailing
    // semicolon is required in both C and C#.
    public EmitContent Visit(C.StmtDoWhile n) =>
        $"do {T(n.Arg1)}while ({CondOf(n.Arg4)});\n";

    // `for (Decl; E; E) Stmt` — emit C#'s for verbatim. C# accepts the same
    // shape; the init declaration scopes to the loop body. The init Decl
    // here is the LHS (`int i = 0` form); the StripOuterParens on the
    // incr keeps the emitter from wrapping `i++` in extra parens that C#
    // rejects in for-clause position.
    // for-loop clauses. ForCond produces the already-Cond.B-wrapped condition
    // (or C# `true` when empty); ForPost the update text (or empty). So the
    // Stmt productions just splice the pieces — no CondOf here.
    public EmitContent Visit(C.ForCondExpr n)  => CondOf(n.Arg0);
    public EmitContent Visit(C.ForCondEmpty n) => "true";
    public EmitContent Visit(C.ForPostExprs n) => T(n.Arg0);
    public EmitContent Visit(C.ForPostEmpty n) => "";
    public EmitContent Visit(C.StmtForDecl n)
    {
        // ScopeEnter (Arg2) opened the for-init frame after `(`; indices below
        // are shifted by one accordingly. Decl=Arg3, ForCond=Arg5, ForPost=Arg7,
        // body=Arg9. Pop the frame once the whole statement is built.
        Gate(1999, "declaration in `for` initializer", n.Arg0);  // C99
        var s = $"for ({T(n.Arg3)}; {T(n.Arg5)}; {T(n.Arg7)}) {T(n.Arg9)}";
        PopScope();
        return s;
    }
    public EmitContent Visit(C.StmtForExpr n) =>
        $"for ({T(n.Arg2)}; {T(n.Arg4)}; {T(n.Arg6)}) {T(n.Arg8)}";
    public EmitContent Visit(C.StmtForNoInit n) =>
        $"for (; {T(n.Arg3)}; {T(n.Arg5)}) {T(n.Arg7)}";

    // Comma-separated expression list used in for-init / for-update.
    // C# accepts `for (i=0, j=10; …; i++, j--)` natively, so we just
    // splice the expressions together with `, ` between them — no
    // helper, no parens, no special lowering. The single-expression
    // form passes through unchanged so for-loops with a lone init or
    // update still emit identically to before.
    public EmitContent Visit(C.CommaExprOne n) => StripOuterParens(T(n.Arg0));
    public EmitContent Visit(C.CommaExprCons n) => $"{T(n.Arg0)}, {StripOuterParens(T(n.Arg2))}";
    public EmitContent Visit(C.StmtReturn n)
    {
        // Reconcile the returned value's enum-ness with the declared return type
        // (same rule as a decl/assignment sink): enum fn ← non-matching value
        // gets `(Enum)`, non-enum fn ← enum value decays to `(int)`.
        var text = T(n.Arg1);
        var exprEnum = EnumOf(n.Arg1);
        if (_currentFunctionReturnType is { } ret)
        {
            if (_enumTags.Contains(ret)) { if (exprEnum != ret) { text = $"({ret})({text})"; } }
            else if (exprEnum is not null) { text = $"(int)({text})"; }
        }
        return $"return {text};\n";
    }
    public EmitContent Visit(C.StmtReturnVoid n) => "return;\n";
    public EmitContent Visit(C.StmtBreak n) => "break;\n";
    public EmitContent Visit(C.StmtContinue n) => "continue;\n";

    // switch (E) Block — switch body is a plain Block. `case X:` and
    // `default:` are statement-level labels (see CaseLabel/DefaultLabel)
    // that can appear anywhere inside the Block — including nested inside
    // a do-while or other control flow, enabling Duff's-device-shaped code.
    // C# accepts the same shape.
    public EmitContent Visit(C.StmtSwitch n) =>
        $"switch ({T(n.Arg2)}) {T(n.Arg4)}";

    // Statement-level case/default labels. Body is a single Stmt (which
    // may itself be another labeled stmt — `case 1: case 2: do_thing();`
    // chains naturally).
    public EmitContent Visit(C.CaseLabel n) =>
        $"case {T(n.Arg1)}:\n{T(n.Arg3)}";
    public EmitContent Visit(C.DefaultLabel n) =>
        $"default:\n{T(n.Arg2)}";
    // Tagged as a declaration statement so the C90 mixed-declarations gate can
    // distinguish it from a non-declaration in the enclosing block. Renders to
    // the same text everywhere (T() unwraps the marker).
    public EmitContent Visit(C.StmtDecl n) => new EmitContent.DeclStmtMarker($"{T(n.Arg0)};\n");

    // Block-scope `static T x [= C];`. Each declarator becomes a mangled
    // `public static unsafe` field in DotCcGlobals (static storage duration,
    // persists across calls, initialised once — all native to a C# static
    // field), and we register name→mangled so Visit(Var) rewrites in-function
    // uses. The statement itself emits nothing into the function body — the
    // storage lives in the globals class, not as a local. C requires static
    // initialisers to be constant expressions, so the field initialiser (which
    // also runs exactly once) is an exact match.
    public EmitContent Visit(C.StmtStaticDecl n)
    {
        var type = T(n.Arg1);
        var entries = DE(n.Arg2);
        var fn = _currentFunctionName ?? "fn";
        var mangled = new List<EmitContent.DeclEntry>(entries.Count);
        foreach (var e in entries)
        {
            var name = $"__static_{fn}_{e.Name}";
            _fnStatics[e.Name] = name;
            mangled.Add(new EmitContent.DeclEntry(name, e.Init));
        }
        EmitGlobalFields(type, mangled);
        return string.Empty;
    }

    // `goto label;` — C# accepts the same keyword + identifier syntax with
    // identical forward-reference semantics inside a method body, so the
    // lowering is verbatim.
    public EmitContent Visit(C.StmtGoto n) => $"goto {Id(T(n.Arg1))};\n";

    // `label: Stmt` — emit the label followed by the body statement.
    // Whitespace shape: label on its own line for readability.
    public EmitContent Visit(C.StmtLabel n) =>
        $"{Id(T(n.Arg0))}:\n{T(n.Arg2)}";

    // Empty statement `;` — required pre-C23 if you want to label the end
    // of a block (`end: ;`). Emit as a bare semicolon; C# parses it as an
    // empty statement too.
    public EmitContent Visit(C.StmtEmpty n) => ";\n";
    public EmitContent Visit(C.StmtExpr n)
    {
        // Comma operator as a statement (`a, b, c;`): the result is discarded
        // and C has a sequence point at each comma, so emit each operand as its
        // own statement. (C# has no comma operator, and `(a, b).Item2;` isn't a
        // valid statement-expression — CS0201 — so the tuple form can't be used
        // here.) An operand that isn't a valid C# statement-expression (a bare
        // value with no side effect) reaches Roslyn as CS0201 — the same loud
        // failure C# gives such pointless code.
        if (n.Arg0.Content is EmitContent.CommaSeq seq)
        {
            var sb = new StringBuilder();
            foreach (var op in seq.Operands) { sb.Append(StripOuterParens(op)).Append(";\n"); }
            return sb.ToString();
        }
        // CS0201: bare parenthesized assignment isn't a statement. Peel the
        // outer parens that our binop emitters wrap on.
        return $"{StripOuterParens(T(n.Arg0))};\n";
    }

    // Declarations
    // `Type DeclItemList` — covers single (`int x;`), single-with-init
    // (`int x = 5;`), and multi-declarator (`int x, y, z;`,
    // `int x = 1, y = 2;`) forms. C# accepts the same `int x, y = 5, z;`
    // syntax so the lowering is verbatim.
    public EmitContent Visit(C.Decl n)
    {
        var type = T(n.Arg0);
        var entries = DE(n.Arg1);
        // Register name + declared type for shadow resolution and enum typing,
        // and assign each declarator its (possibly renamed) C# identifier. Raw
        // names stay in `entries` for the side-table logic below; `renamed`
        // maps raw → emitted for the actual C# text.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            NoteLocal(e.Name);
            if (_currentFunctionName is not null) { _localTypes[e.Name] = type; }
            renamed[e.Name] = DeclareLocal(e.Name);
        }

        // malloc → stack-value peephole: only single-declarator
        // `S* p = (S*)malloc(sizeof(S));` is eligible. Record whether the
        // declared type matched `S*` (needed by the analysis pass) and, in the
        // emit pass, swap the heap allocation for a stack value when the
        // variable was found promotable.
        if (entries.Count == 1 && entries[0].MallocStructType is string structType
            && _currentFunctionName is string fn)
        {
            var typeMatches = type.Replace(" ", "") == structType + "*";
            if (_fnMalloc.TryGetValue(entries[0].Name, out var mv)) { mv.TypeMatches = typeMatches; }
            if (typeMatches && _promotableIn.Contains((fn, entries[0].Name)))
            {
                // `new S()` value-initialises (zero) the struct on the stack —
                // no native heap alloc, matching free dropped. Reads happen
                // after explicit field writes in any promotable function (the
                // usage analysis guarantees `->`-only access), so zero-init vs
                // C's uninitialised malloc is unobservable.
                return $"{structType} {Id(renamed[entries[0].Name])} = new {structType}()";
            }
        }

        // Reconcile each initializer's enum-ness with the declared type
        // (`Color c = 2` → `(Color)(2)`; `int x = c` → `(int)(c)`), then swap in
        // the (possibly renamed) declarator name for emission.
        entries = entries.Select(e => e with { Init = ReconcileEnumInit(type, e), Name = renamed[e.Name] }).ToList();
        return $"{type} {DeclEntriesToBlockScopeString(entries)}";
    }

    // Reconcile a declared type with an initializer's enum-ness, inserting the
    // int↔enum cast C# requires when they disagree (C lets enums and ints flow
    // freely; C# doesn't). Returns the maybe-wrapped init; null passes through.
    private string? ReconcileEnumInit(string declType, EmitContent.DeclEntry e)
    {
        if (e.Init is not { } init) { return null; }
        if (_enumTags.Contains(declType))
        {
            // Enum-typed slot: cast a non-matching source (int→enum, or a
            // different enum). A source already of this enum needs nothing.
            return e.InitEnumType == declType ? init : $"({declType})({init})";
        }
        // Non-enum slot fed an enum value: decay to int (int→numeric is implicit).
        return e.InitEnumType is null ? init : $"(int)({init})";
    }

    // DeclItemList: structured accumulator of (name, init?) pairs.
    // Returning a typed DeclEntries instead of a pre-joined string lets
    // the file-scope GlobalDeclList visitor apply per-entry policy
    // (e.g. auto-init LongJmpToken with `new T()` for the no-init form)
    // while the block-scope Decl visitor still gets the same C# multi-
    // declarator join shape via DeclEntriesToBlockScopeString.
    public EmitContent Visit(C.DeclItemListOne n)  => (EmitContent.DeclEntries)n.Arg0.Content;
    public EmitContent Visit(C.DeclItemListCons n)
    {
        var prev = DE(n.Arg0);
        var next = DE(n.Arg2);
        var combined = new List<EmitContent.DeclEntry>(prev.Count + next.Count);
        combined.AddRange(prev);
        combined.AddRange(next);
        return new EmitContent.DeclEntries(combined);
    }

    // DeclItem: a single declarator. Carries the (name, init?) pair
    // upward via DeclEntries so the consuming Decl/GlobalDeclList can
    // decide how to materialise it. The plain `int x;` form has Init=null;
    // the consumer fills in `= default` (block scope) or omits the
    // initializer (file scope, value types — C# zero-inits class fields).
    public EmitContent Visit(C.DeclItem n) =>
        new EmitContent.DeclEntries(new[] { new EmitContent.DeclEntry(T(n.Arg0), null) });
    public EmitContent Visit(C.DeclItemInit n)
    {
        var name = T(n.Arg0);
        // `S* p = (S*)malloc(sizeof(S))` — the init reduced to a MallocSizeof
        // marker. Register the variable as a stack-promotion candidate for
        // this function and remember the struct type on the entry so Visit(Decl)
        // can emit the promoted form. T() renders the marker's low-level text
        // for the (default) non-promoted path.
        string? mallocType = null;
        if (n.Arg2.Content is EmitContent.MallocSizeof ms && _currentFunctionName is not null)
        {
            mallocType = ms.StructType;
            if (!_fnMalloc.ContainsKey(name)) { _fnMalloc[name] = new MallocVar { StructType = ms.StructType }; }
        }
        return new EmitContent.DeclEntries(new[] { new EmitContent.DeclEntry(name, T(n.Arg2), mallocType, EnumOf(n.Arg2)) });
    }

    // Join structured DeclEntries into the block-scope C# multi-declarator
    // form: `name = default, name = expr, ...`. Block scope can't omit the
    // initializer on locals (C# enforces definite-assignment for struct
    // fields used after declaration), so we fill no-init entries with
    // `= default` — zero-initialized for all our types (0 / null / empty
    // struct), which matches the observable behavior of well-written C.
    private static string DeclEntriesToBlockScopeString(IReadOnlyList<EmitContent.DeclEntry> entries)
    {
        return string.Join(", ", entries.Select(e => $"{Id(e.Name)} = {e.Init ?? "default"}"));
    }
    // C `T arr[D1][D2]…` → C# `T* arr = stackalloc T[D1*D2*…]`. C# stackalloc is
    // 1-D, so a multi-dimensional array is FLATTENED to a single contiguous
    // block (matching C's row-major layout); the subscript visitor rewrites
    // `a[i][j]` to flat pointer arithmetic. stackalloc keeps the array in the
    // same lifetime as locals (no heap, no GC pin), matching C automatics.
    // ArrDims (Arg2) carries the dimension expression texts, outer→inner.
    public EmitContent Visit(C.DeclArr n)
    {
        var elem = T(n.Arg0);
        var name = T(n.Arg1);
        var dims = A(n.Arg2);
        NoteLocal(name);
        var emitted = DeclareLocal(name);

        // Try every dimension as a literal int.
        var sizes = new List<int>(dims.Count);
        foreach (var d in dims)
        {
            if (int.TryParse(d, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) { sizes.Add(v); }
            else { sizes.Clear(); break; }
        }

        if (sizes.Count == 0)
        {
            // A non-literal extent. 1-D runtime size (VLA-ish) lowers directly
            // to `stackalloc T[expr]`; multi-dim with a non-constant dimension
            // can't be flattened at compile time — reject clearly.
            if (dims.Count != 1)
            {
                throw new CompileException(
                    $"multi-dimensional array `{name}` needs constant dimensions");
            }
            return $"{elem}* {Id(emitted)} = stackalloc {elem}[{StripOuterParens(dims[0])}]";
        }

        // Build the nested CType (outer→inner) + total element count, record it
        // (keyed by RAW name), and flatten the allocation.
        CType cty = new CType.Sized(elem);
        var total = 1;
        for (var i = sizes.Count - 1; i >= 0; i--) { cty = new CType.Arr(cty, sizes[i]); total *= sizes[i]; }
        NoteLocalArray(name, cty);
        return $"{elem}* {Id(emitted)} = stackalloc {elem}[{total}]";
    }

    // C `T arr[N] = {1, 2, 3}` (or `T arr[] = {…}`) → C# `T* arr = stackalloc T[]{ 1, 2, 3 }`.
    // The explicit-size form ignores the size operand because C# infers it
    // from the initializer; both shapes share the same emit. ArgList arrives
    // as a typed EmitContent.Args (read via A()) — no sentinel decoding. The
    // element count for sizeof is the initializer length.
    public EmitContent Visit(C.DeclArrInit n)
    {
        var name = T(n.Arg1);
        var args = A(n.Arg7);
        NoteLocal(name);
        NoteLocalArray(name, new CType.Arr(new CType.Sized(T(n.Arg0)), args.Count));
        return EmitArrInit(T(n.Arg0), DeclareLocal(name), args);
    }
    public EmitContent Visit(C.DeclArrInitImplicit n)
    {
        var name = T(n.Arg1);
        var args = A(n.Arg6);
        NoteLocal(name);
        NoteLocalArray(name, new CType.Arr(new CType.Sized(T(n.Arg0)), args.Count));
        return EmitArrInit(T(n.Arg0), DeclareLocal(name), args);
    }

    // Function-pointer local declarator: `int (*fp)(int) [= E]` → C#
    // `delegate*<int, int> fp [= …]`. C# puts the return type LAST in the type
    // arg list (opposite of C). The fn-ptr TYPE's parameters reduce through the
    // Param visitors and stage into _pendingParams — discard them (only an
    // FnSig's StartFn should adopt staged params; see TypedefFnPtr). A bare
    // function-name initializer (`= f`, which C decays to a pointer) is given
    // the `&` C# requires; an explicit `&f` passes through.
    public EmitContent Visit(C.DeclFnPtr n)            => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), T(n.Arg6), null);
    public EmitContent Visit(C.DeclFnPtrNoArgs n)      => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), "",        null);
    public EmitContent Visit(C.DeclFnPtrInit n)        => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), T(n.Arg6), n.Arg9);
    public EmitContent Visit(C.DeclFnPtrNoArgsInit n)  => EmitFnPtrLocal(T(n.Arg0), T(n.Arg3), "",        n.Arg8);

    private EmitContent EmitFnPtrLocal(string ret, string name, string pars, Item? init)
    {
        _pendingParams.Clear();
        NoteLocal(name);
        var type = pars.Length == 0
            ? $"delegate*<{ret}>"
            : $"delegate*<{StripParamNames(pars)}, {ret}>";
        if (_currentFunctionName is not null) { _localTypes[name] = type; }
        var emitted = DeclareLocal(name);
        if (init is null) { return $"{type} {Id(emitted)} = default"; }
        var initText = T(init);
        // `= f` (C decays a function to its address) needs `&f` in C#.
        if (_fnReturnTypes.ContainsKey(Unescape(initText))) { initText = "&" + initText; }
        return $"{type} {Id(emitted)} = {initText}";
    }

    // ArrDims — the `[D1][D2]…` dimension list of a multi-dimensional array
    // declarator. Produces the dimension expression texts (outer→inner) as an
    // Args list for Visit(C.DeclArr) to flatten. `[` E `]`: E is Arg1 (one) /
    // Arg2 (cons).
    public EmitContent Visit(C.ArrDimsOne n) =>
        new EmitContent.Args(new[] { StripOuterParens(T(n.Arg1)) });
    public EmitContent Visit(C.ArrDimsCons n)
    {
        var prev = A(n.Arg0);
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(StripOuterParens(T(n.Arg2)));
        return new EmitContent.Args(combined);
    }

    // Record a block-scope array's full CType for sizeof / multi-dim subscript
    // rewriting. No-op at file scope (globals handled in EmitGlobalFields; not
    // yet array-aware).
    private void NoteLocalArray(string rawName, CType arrayType)
    {
        if (_currentFunctionName is not null) { _localArrayInfo[rawName] = arrayType; }
    }

    private static string EmitArrInit(string type, string name, IReadOnlyList<string> args) =>
        $"{type}* {Id(name)} = stackalloc {type}[]{{ {string.Join(", ", args)} }}";

    // `Point p = { .x = 1, .y = 2 };` — designated initializer (C99). The
    // user named the fields directly so we don't need _structFields here:
    // the MemberInitList already emits `field = value` pairs in the right
    // shape for C#'s object-initializer syntax. Order of fields can differ
    // from declaration order (C99 allows it; C# does too).
    public EmitContent Visit(C.DeclStructDesignated n)
    {
        Gate(1999, "designated initializers", n.Arg0);  // C99
        var type = T(n.Arg0);
        var name = T(n.Arg1);
        NoteLocal(name);
        var members = IM(n.Arg4);  // typed InitMembers list
        return $"{type} {Id(DeclareLocal(name))} = new {type} {{ {string.Join(", ", members)} }}";
    }

    // MemberInitListOne / MemberInitListCons accumulate `.field = expr`
    // items as a typed EmitContent.InitMembers. MemberInit emits the
    // individual `field = expr` snippet (plain text, joined at the
    // DeclStructDesignated consumer).
    public EmitContent Visit(C.MemberInitListOne n) =>
        new EmitContent.InitMembers(new[] { T(n.Arg0) });
    public EmitContent Visit(C.MemberInitListCons n)
    {
        var prev = IM(n.Arg0);
        var next = T(n.Arg2);
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(next);
        return new EmitContent.InitMembers(combined);
    }
    // Trailing comma in a designated initializer — the list is unchanged.
    public EmitContent Visit(C.MemberInitListTrail n) => (EmitContent.InitMembers)n.Arg0.Content;
    public EmitContent Visit(C.MemberInit n) => $"{Id(T(n.Arg1))} = {T(n.Arg3)}";

    // InitList — brace-initializer elements (optional trailing comma).
    // Left-recursive; produces the same Args payload the init visitors read.
    public EmitContent Visit(C.InitListOne n) => new EmitContent.Args(new[] { T(n.Arg0) });
    public EmitContent Visit(C.InitListCons n)
    {
        var prev = ((EmitContent.Args)n.Arg0.Content).Values;
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(T(n.Arg2));
        return new EmitContent.Args(combined);
    }
    public EmitContent Visit(C.InitListTrail n) => (EmitContent.Args)n.Arg0.Content;

    // `Point p = {1, 2};` — struct aggregate init. C# can't take positional
    // initializers on a struct, so we look up the struct's field names (from
    // _structFields, populated by StructDef / TypedefStruct / UnionDef) and
    // emit a named-initializer: `Point p = new Point { x = 1, y = 2 };`.
    // If the type isn't a known struct (e.g. user wrote it for a typedef'd
    // primitive), fall back to a zero init with a comment — Roslyn will
    // surface the real error if the user pursued it.
    public EmitContent Visit(C.DeclStructInit n)
    {
        var type = T(n.Arg0);
        var name = T(n.Arg1);
        NoteLocal(name);
        var emittedName = DeclareLocal(name);
        var values = A(n.Arg4);  // typed EmitContent.Args — no sentinel split

        if (!_structFields.TryGetValue(type, out var fields))
        {
            return $"{type} {Id(emittedName)} = default /* dotcc: unknown struct '{type}' for aggregate init */";
        }

        var sb = new StringBuilder();
        sb.Append(type).Append(' ').Append(Id(emittedName)).Append(" = new ").Append(type).Append(" { ");
        var count = Math.Min(values.Count, fields.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            sb.Append(Id(fields[i])).Append(" = ").Append(values[i]);
        }
        sb.Append(" }");
        return sb.ToString();
    }

    // Expressions — paren-heavy to stay precedence-safe.
    public EmitContent Visit(C.Assign n)
    {
        // `c = E`: an enum-typed lvalue takes an int→enum cast on a non-matching
        // source; a non-enum lvalue (`x = c`) decays an enum source to int. The
        // assignment expression's value carries the lvalue's enum type.
        var lhsEnum = EnumOf(n.Arg0);
        var rhs = T(n.Arg2);
        var rhsEnum = EnumOf(n.Arg2);
        if (lhsEnum is not null)
        {
            if (rhsEnum != lhsEnum) { rhs = $"({lhsEnum})({rhs})"; }
            return new EmitContent.Text($"({T(n.Arg0)} = {rhs})", lhsEnum);
        }
        if (rhsEnum is not null) { rhs = $"(int)({rhs})"; }
        return $"({T(n.Arg0)} = {rhs})";
    }
    // `lhs OP= rhs`. C#'s enum compound-assign is unreliable (`enum |= int`,
    // `enum *= int` are errors), so when the lvalue is enum-typed we expand to
    // the explicit `lhs = (Enum)((int)lhs OP rhs)` form — the lvalue is assumed
    // side-effect-free, which holds for the simple variables C enum/flags code
    // uses. Otherwise keep `OP=`, decaying an enum rhs to int (`x += c` would be
    // `int += enum` → C# error).
    private string CompoundAssign(Item lhsIt, string op, Item rhsIt)
    {
        var lhs = T(lhsIt);
        var rhs = IntDecay(rhsIt);
        if (EnumOf(lhsIt) is { } e) { return $"({lhs} = ({e})((int){lhs} {op} {rhs}))"; }
        return $"({lhs} {op}= {rhs})";
    }
    public EmitContent Visit(C.AddAssign n) => CompoundAssign(n.Arg0, "+", n.Arg2);
    public EmitContent Visit(C.SubAssign n) => CompoundAssign(n.Arg0, "-", n.Arg2);
    public EmitContent Visit(C.MulAssign n) => CompoundAssign(n.Arg0, "*", n.Arg2);
    public EmitContent Visit(C.DivAssign n) => CompoundAssign(n.Arg0, "/", n.Arg2);
    public EmitContent Visit(C.ModAssign n) => CompoundAssign(n.Arg0, "%", n.Arg2);
    // Logical `||` and `&&` — wrap each operand with Cond.B so the C-truthy
    // conversion works for int / double / pointer AND bool (when the
    // operand is already a comparison result like `a == NULL`). The
    // previous `!= 0` form broke when an operand was bool because
    // `bool != 0` isn't a valid C# expression.
    // `&&` / `||` likewise yield C `int` 0/1 — same CBool wrap so the result is
    // usable as an int (`int flag = a && b;`). Operands keep their Cond.B truthy
    // conversion; the bool the C# `&&`/`||` produces is cast to CBool.
    public EmitContent Visit(C.Lor n) =>
        $"((CBool)({CondOf(n.Arg0)} || {CondOf(n.Arg2)}))";
    public EmitContent Visit(C.Land n) =>
        $"((CBool)({CondOf(n.Arg0)} && {CondOf(n.Arg2)}))";
    // Equality: same CBool wrap on the textual fallback. The setjmp path returns
    // a SetjmpCheckZero variant consumed directly by StmtIfElse (a conditional
    // context), so it must NOT be wrapped. Enum operands decay to int.
    public EmitContent Visit(C.Eq n) => MaybeSetjmpCompare(n.Arg0, n.Arg2, isEquals: true)
        ?? (EmitContent)$"((CBool)({IntDecay(n.Arg0)} == {IntDecay(n.Arg2)}))";
    public EmitContent Visit(C.Neq n) => MaybeSetjmpCompare(n.Arg0, n.Arg2, isEquals: false)
        ?? (EmitContent)$"((CBool)({IntDecay(n.Arg0)} != {IntDecay(n.Arg2)}))";

    /// <summary>
    /// If one side of an <c>==</c>/<c>!=</c> comparison is a
    /// <see cref="EmitContent.SetjmpCall"/> and the other is the
    /// literal <c>0</c>, return a <see cref="EmitContent.SetjmpCheckZero"/>
    /// variant for the enclosing <c>StmtIfElse</c> to consume. Otherwise
    /// return null so the caller falls back to the normal textual emit.
    /// </summary>
    private static EmitContent? MaybeSetjmpCompare(Item left, Item right, bool isEquals)
    {
        if (left.Content is EmitContent.SetjmpCall lsj && IsLiteralZero(right))
        {
            return new EmitContent.SetjmpCheckZero(lsj.EnvName, TruthyOnFirstCall: isEquals);
        }
        if (right.Content is EmitContent.SetjmpCall rsj && IsLiteralZero(left))
        {
            return new EmitContent.SetjmpCheckZero(rsj.EnvName, TruthyOnFirstCall: isEquals);
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="it"/> is the literal integer <c>0</c>.
    /// Accepts the raw lexer token (NUM "0") and the visitor-produced
    /// <c>EmitContent.Text("0")</c>; rejects everything else including
    /// <c>0.0</c>, <c>(0)</c> (which would be a parenthesised primary),
    /// and any constant expression that happens to evaluate to zero.
    /// </summary>
    private static bool IsLiteralZero(Item it) => it.Content switch
    {
        EmitContent.Text { Value: "0" } => true,
        string s => s == "0",
        _ => false,
    };
    // Relational operators yield C `int` 0/1, NOT a bool — `int x = a > b;`,
    // `(a>0)+(b>0)`, `return a<b;` from an int function, and `printf("%d", a==b)`
    // are all legal C. C# `<`/`>`/… produce `bool`, which can't land in those
    // int positions, so we cast the result to `CBool` (the integer-typed _Bool
    // value): CBool→int carries it into arithmetic/assignment/args/return, and a
    // `Cond.B(CBool)` overload carries it into conditional positions. Nested
    // comparisons (`(a>b)==(c>d)`) resolve via CBool→int on both operands.
    public EmitContent Visit(C.Lt n) => $"((CBool)({IntDecay(n.Arg0)} < {IntDecay(n.Arg2)}))";
    public EmitContent Visit(C.Gt n) => $"((CBool)({IntDecay(n.Arg0)} > {IntDecay(n.Arg2)}))";
    public EmitContent Visit(C.Le n) => $"((CBool)({IntDecay(n.Arg0)} <= {IntDecay(n.Arg2)}))";
    public EmitContent Visit(C.Ge n) => $"((CBool)({IntDecay(n.Arg0)} >= {IntDecay(n.Arg2)}))";
    // Bitwise — same precedence and semantics in C# (binary `& | ^ << >>`,
    // unary `~`). The visitor just emits the C# operator verbatim.
    public EmitContent Visit(C.BOr n)  => $"({IntDecay(n.Arg0)} | {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.BXor n) => $"({IntDecay(n.Arg0)} ^ {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.BAnd n) => $"({IntDecay(n.Arg0)} & {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.Shl n)  => $"({IntDecay(n.Arg0)} << {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.Shr n)  => $"({IntDecay(n.Arg0)} >> {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.BNot n) => $"(~{IntDecay(n.Arg1)})";

    // Logical NOT: lower to `(Cond.B(E) ? 0 : 1)` so the result is int,
    // matching C's `!x` yielding 0 or 1 (never a bool). Cond.B picks the
    // right truthy overload based on E's type (int/double/pointer/bool).
    public EmitContent Visit(C.LNot n) => $"({CondOf(n.Arg1)} ? 0 : 1)";

    // Ternary `c ? a : b` — Cond.B wraps the C-truthy condition. The two
    // branches need a common C# type; the user is responsible for keeping
    // them compatible (matches the C constraint that the branches share
    // arithmetic conversions).
    public EmitContent Visit(C.Ternary n) =>
        $"({CondOf(n.Arg0)} ? {T(n.Arg2)} : {T(n.Arg4)})";
    public EmitContent Visit(C.AndAssign n) => CompoundAssign(n.Arg0, "&", n.Arg2);
    public EmitContent Visit(C.OrAssign n)  => CompoundAssign(n.Arg0, "|", n.Arg2);
    public EmitContent Visit(C.XorAssign n) => CompoundAssign(n.Arg0, "^", n.Arg2);
    public EmitContent Visit(C.ShlAssign n) => CompoundAssign(n.Arg0, "<<", n.Arg2);
    public EmitContent Visit(C.ShrAssign n) => CompoundAssign(n.Arg0, ">>", n.Arg2);

    public EmitContent Visit(C.Add n) => $"({IntDecay(n.Arg0)} + {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.Sub n) => $"({IntDecay(n.Arg0)} - {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.Mul n) => $"({IntDecay(n.Arg0)} * {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.Div n) => $"({IntDecay(n.Arg0)} / {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.Mod n) => $"({IntDecay(n.Arg0)} % {IntDecay(n.Arg2)})";
    public EmitContent Visit(C.Cast n)
    {
        var castType = T(n.Arg1);
        // `(S*)malloc(sizeof(S))` — propagate the MallocSizeof marker through a
        // matching pointer cast so the enclosing declaration can still recognise
        // it. The marker's low-level text grows to include the cast, so the
        // non-promoted path is byte-identical to before.
        if (n.Arg3.Content is EmitContent.MallocSizeof ms
            && castType.Replace(" ", "") == ms.StructType + "*")
        {
            return new EmitContent.MallocSizeof(ms.StructType, $"(({castType}){ms.LowLevelText})");
        }
        // A cast to an enum type yields an enum value (C# allows int→enum and
        // enum→enum casts directly); tag it so downstream consumers reconcile.
        // Also carry the cast's type for sizeof (`sizeof((char)x)` == 1).
        return new EmitContent.Text($"(({castType}){T(n.Arg3)})",
            _enumTags.Contains(castType) ? castType : null, new CType.Sized(castType));
    }
    // `*p` / `p[i]` synthesize the element/pointee type for sizeof.
    public EmitContent Visit(C.Deref n) => Typed($"(*{T(n.Arg1)})", TyOf(n.Arg1)?.ElementType());
    public EmitContent Visit(C.AddrOf n) => $"(&{T(n.Arg1)})";
    public EmitContent Visit(C.Neg n) => $"(-{IntDecay(n.Arg1)})";
    // Prefix ++/-- — strip outer parens of operand to avoid CS0131 on a
    // parenthesised lvalue. `(x)++` would parse as post-inc on a parens
    // expression, but C# accepts `++x` directly. Enum-ness propagates (C# ++/--
    // work on enums) so a `c++` value used in an int slot still gets decayed.
    public EmitContent Visit(C.PreInc n) => new EmitContent.Text($"(++{StripOuterParens(T(n.Arg1))})", EnumOf(n.Arg1));
    public EmitContent Visit(C.PreDec n) => new EmitContent.Text($"(--{StripOuterParens(T(n.Arg1))})", EnumOf(n.Arg1));
    // Postfix ++/-- — same stripping; emit `x++` rather than `(x)++`.
    public EmitContent Visit(C.PostInc n) => new EmitContent.Text($"({StripOuterParens(T(n.Arg0))}++)", EnumOf(n.Arg0));
    public EmitContent Visit(C.PostDec n) => new EmitContent.Text($"({StripOuterParens(T(n.Arg0))}--)", EnumOf(n.Arg0));
    // Subscript `expr[i]`. For a 1-D array / pointer it's an ordinary C#
    // pointer subscript (matching C). For a MULTI-dimensional array, dotcc
    // flattened the storage, so a PARTIAL index (more dimensions remain) is
    // pointer arithmetic: `a[i]` is `a + i*stride`, where stride is the element
    // count of the remaining sub-array; a FULL index (last dimension) is the
    // ordinary subscript. The base is NOT outer-paren-stripped (a partial-index
    // base like `(a + i*3)` must keep its parens so the next `[…]` binds to the
    // whole expression, not the trailing operand). An enum index decays to int.
    public EmitContent Visit(C.Subscript n)
    {
        var baseTy = TyOf(n.Arg0);
        var baseText = T(n.Arg0);
        var idx = StripOuterParens(IntDecay(n.Arg2));
        if (baseTy is CType.Arr { Element: CType.Arr } arr)
        {
            return Typed($"({baseText} + ({idx}) * {FlatSize(arr.Element)})", arr.Element);
        }
        return Typed($"({baseText}[{idx}])", baseTy?.ElementType());
    }

    // Number of scalar elements in a (possibly nested) array CType — the flat
    // stride of a multi-dim array's sub-array. Scalars/pointers count as 1.
    private static int FlatSize(CType t) => t switch
    {
        CType.Arr a => a.Count * FlatSize(a.Element),
        _ => 1,
    };

    // Member access — `.` on a struct value, `->` on a struct pointer.
    // C# accepts both syntaxes in unsafe context (where all our user code
    // lives), so emit verbatim.
    public EmitContent Visit(C.MemberDot n) =>
        $"({StripOuterParens(T(n.Arg0))}.{Id(T(n.Arg2))})";
    public EmitContent Visit(C.MemberArrow n)
    {
        var baseExpr = StripOuterParens(T(n.Arg0));
        var member = Id(T(n.Arg2));
        // Count a `->` whose base is a malloc-candidate variable, and choose the
        // C# operator: a promoted stack value uses `.`, a low-level pointer `->`.
        // The malloc maps are keyed by the RAW C name, but `baseExpr` is the
        // emitted (possibly @-escaped) text — match on the unescaped name, emit
        // with the escaped one.
        var rawBase = Unescape(baseExpr);
        if (_currentFunctionName is string fn && _fnMalloc.TryGetValue(rawBase, out var mv))
        {
            mv.ArrowRefs++;
            if (_promotableIn.Contains((fn, rawBase)))
            {
                return $"({baseExpr}.{member})";
            }
        }
        return $"({baseExpr}->{member})";
    }

    // `sizeof(Type)` — emit C# sizeof. Valid in unsafe contexts for any
    // unmanaged type (which all our types are). Returns a structured marker so
    // a single-arg malloc can recognise `malloc(sizeof(S))` structurally; T()
    // renders it back to `sizeof(S)` everywhere else.
    public EmitContent Visit(C.SizeofType n) => new EmitContent.SizeofType(T(n.Arg2));

    // `sizeof expr` — read the operand's synthesized CType (propagated up by the
    // expression visitors) and emit its byte size. Arrays compute
    // `count * sizeof(element)` (the C# pointer-lowering makes C# sizeof wrong);
    // everything else defers to C# `sizeof(type)`. The result is itself an int
    // (size_t in C). If the operand's type wasn't synthesized, fail clearly
    // rather than emit a wrong size.
    public EmitContent Visit(C.SizeofExpr n)
    {
        var t = TyOf(n.Arg1)
            ?? throw new CompileException(
                "`sizeof` of this expression isn't supported yet — dotcc resolves sizeof of a "
                + "variable, array, subscript, dereference, cast, literal, or call result. "
                + "Use `sizeof(Type)` if you can.");
        return Typed(SizeofText(t), new CType.Sized("int"));
    }

    // C# expression for the byte size of a CType. An array is count*sizeof(elem)
    // (recursive for nested arrays); anything else is a direct C# `sizeof(T)`.
    private static string SizeofText(CType t) => t switch
    {
        CType.Arr a => $"({a.Count} * {SizeofText(a.Element)})",
        CType.Sized s => $"sizeof({s.CsType})",
        _ => throw new CompileException("internal: unknown CType in sizeof"),
    };

    public EmitContent Visit(C.Call n)
    {
        var callee = T(n.Arg0);
        var argsContent = (EmitContent.Args)n.Arg2.Content;
        var args = argsContent.Values;  // strongly-typed arg list, no sentinel splitting
        // A local/param that SHADOWS a libc builtin name (e.g. a function-pointer
        // local named `printf` or `malloc`) is an ordinary call through that
        // variable — skip the builtin lowering entirely. (_localNames is keyed by
        // the raw C name; callee is the emitted, possibly @-escaped text.) For
        // normal code no local is named after a builtin, so this never fires.
        if (_localNames.Contains(Unescape(callee)))
        {
            return $"{callee}({string.Join(", ", args)})";
        }
        // malloc(sizeof(S)) — emit a MallocSizeof marker (carrying the struct
        // type for the stack-promotion peephole and the verbatim low-level
        // text for the fallback). Recognised structurally: the sole argument
        // reduced to a SizeofType marker, no string parsing.
        if (callee == "malloc" && args.Count == 1
            && argsContent.SoleArg is EmitContent.SizeofType sz)
        {
            return new EmitContent.MallocSizeof(sz.TypeName, $"malloc({args[0]})");
        }
        // free(p) — count it against the candidate `p` and, in the emit pass,
        // drop it entirely when `p` was promoted to a stack value (nothing to
        // free). Non-promotable / non-candidate frees fall through to a normal
        // call below.
        // Match the malloc maps on the RAW name (args[0] is the emitted,
        // possibly @-escaped argument text).
        var freeArg = args.Count == 1 ? Unescape(args[0]) : null;
        if (callee == "free" && freeArg is not null
            && _currentFunctionName is string fn && _fnMalloc.TryGetValue(freeArg, out var fmv))
        {
            fmv.FreeRefs++;
            if (_promotableIn.Contains((fn, freeArg))) { return ""; }
        }
        // setjmp(env) — return a SetjmpCall marker variant rather than
        // emit as a regular function call. The parent visitor (Equ for
        // `setjmp(env) == 0` or StmtIfElse for the bare condition)
        // recognises the variant and rewrites the surrounding if/else
        // into a try/catch shape. Any other context lets the variant
        // escape to T(), which throws CompileException — setjmp is a
        // non-local-jump primitive, not a regular call.
        if (callee == "setjmp" && args.Count == 1)
        {
            return new EmitContent.SetjmpCall(args[0]);
        }
        // printf-family fluent lowering. C `printf("%d %s", x, s)` → C#
        // `printf(L("%d %s\0"u8)).Arg(x).Arg(s).Done()` — works around
        // `params object[]` not accepting raw pointers. The callee name
        // arrives unmapped (post-MapBuiltin-as-identity) so we match the
        // C spelling. `fprintf(stream, fmt, …)` follows the same shape
        // but with the stream as the first call arg.
        // A varargs `%d` slot takes a C int; an enum argument is a C int too,
        // but `.Arg(int)` won't bind a C# enum — decay enum args to (int).
        var argEnums = argsContent.ArgEnums;
        string VarArg(int i) => argEnums is { } ae && ae[i] is not null ? $"(int){args[i]}" : args[i];
        if (callee == "printf")
        {
            var sb = new StringBuilder();
            sb.Append("printf(").Append(args[0]).Append(')');
            for (int i = 1; i < args.Count; i++)
            {
                sb.Append(".Arg(").Append(VarArg(i)).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        if (callee == "fprintf" && args.Count >= 2)
        {
            var sb = new StringBuilder();
            sb.Append("fprintf(").Append(args[0]).Append(", ").Append(args[1]).Append(')');
            for (int i = 2; i < args.Count; i++)
            {
                sb.Append(".Arg(").Append(VarArg(i)).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        var callText = $"{callee}({string.Join(", ", args)})";
        // Tag the result with the callee's return type: the enum type drives
        // int↔enum reconciliation at the call site, and the CType lets sizeof of
        // a call result resolve.
        if (_fnReturnTypes.TryGetValue(Unescape(callee), out var rt))
        {
            return new EmitContent.Text(callText, _enumTags.Contains(rt) ? rt : null, new CType.Sized(rt));
        }
        return callText;
    }

    public EmitContent Visit(C.CallNoArgs n)
    {
        var callee = T(n.Arg0);
        if (callee == "printf") { return "printf(L(\"\\0\"u8)).Done()"; }
        return $"{callee}()";
    }

    // ArgsCons (`ArgList → E ',' ArgList`) prepends the new expression
    // (Arg0, Text) onto the recursively-built ArgList (Arg2, Args).
    // ArgsOne wraps the single E into a one-element Args list.
    // Call / DeclArrInit / DeclArrInitImplicit consume via A() — no
    // sentinel splitting, no encoded strings.
    public EmitContent Visit(C.ArgsCons n)
    {
        var head = T(n.Arg0);
        var tailArgs = (EmitContent.Args)n.Arg2.Content;
        var tail = tailArgs.Values;
        var combined = new List<string>(tail.Count + 1) { head };
        combined.AddRange(tail);
        // Carry per-arg enum types so the printf path can decay enum→int.
        var enums = new List<string?>(tail.Count + 1) { EnumOf(n.Arg0) };
        enums.AddRange(tailArgs.ArgEnums ?? new string?[tail.Count]);
        return new EmitContent.Args(combined, ArgEnums: enums);
    }

    // SoleArg keeps the single argument's structured Content alongside its
    // rendered text, so a one-arg call (malloc) can inspect it structurally.
    public EmitContent Visit(C.ArgsOne n) =>
        new EmitContent.Args(new[] { T(n.Arg0) }, n.Arg0.Content as EmitContent, new[] { EnumOf(n.Arg0) });

    // Variable reference. Three emit-time rewrites:
    //   - `__func__` → `L("name\0"u8)` using `_currentFunctionName` (set
    //     by the enclosing FnSig action before this Var visit runs —
    //     LALR bottom-up, FnSig fully reduces before Block descends);
    //   - enumerator → `EnumName.Member` so bare `Red` lands as `Color.Red`;
    //   - builtin name → BCL helper for `malloc` / `free` / `printf`.
    // Otherwise pass the identifier through verbatim.
    public EmitContent Visit(C.Var n)
    {
        var name = T(n.Arg0);
        // Count every reference to a malloc candidate. The promotion check
        // (in FuncDef) requires TotalRefs == ArrowRefs + FreeRefs, so any use
        // that isn't a `->` base or a free arg (return, function arg, address-of,
        // pointer arithmetic, comparison, reassignment) tips the balance and
        // disqualifies the variable — exactly the escapes we must not promote.
        if (_fnMalloc.TryGetValue(name, out var mv)) { mv.TotalRefs++; }
        if (name == "__func__")
        {
            Gate(1999, "__func__", n.Arg0);  // C99 predefined identifier
            // `_currentFunctionName` is the enclosing function being
            // reduced. If it's null we're outside any function (illegal
            // C use of __func__) — emit a sentinel so Roslyn surfaces the
            // bug as an undefined-identifier diagnostic rather than us
            // silently producing wrong code.
            var fn = _currentFunctionName
                ?? throw new CompileException("`__func__` used outside any function definition");
            return Typed($"L(\"{fn}\\0\"u8)", new CType.Sized("byte*"));
        }
        // A block-scope `static` local shadows any global/enumerator of the
        // same name within this function — rewrite to its mangled global field.
        if (_fnStatics.TryGetValue(name, out var staticField))
        {
            return staticField;
        }
        // An enumerator constant resolves to `EnumName.Member` — but only if no
        // local/param of the same name shadows it. Without this guard a local
        // named like an enum constant would emit the (non-lvalue) `EnumName.X`
        // at every use and fail to compile.
        if (!_localNames.Contains(name) && _enumerators.TryGetValue(name, out var enumName))
        {
            return new EmitContent.Text($"{enumName}.{Id(name)}", enumName, new CType.Sized(enumName));
        }
        // An enum-typed variable read is itself enum-valued — tag it so consuming
        // nodes insert the int↔enum casts C# requires (`int x = c`, `c & MASK`,
        // …). Also carry the full CType (incl. array element+count) for sizeof.
        // Locals/params win over globals (shadowing). If a scope frame renamed
        // this local (block-shadow alpha-rename), emit the renamed identifier;
        // otherwise it's a global / function / builtin → raw name through
        // MapBuiltin. CType / enum lookups stay keyed by the RAW name.
        var resolved = ResolveLocal(name);
        var mapped = resolved is not null ? Id(resolved) : MapBuiltin(name);
        var cty = VarCType(name);
        var enumTag = cty is CType.Sized sz && _enumTags.Contains(sz.CsType) ? sz.CsType : null;
        return new EmitContent.Text(mapped, enumTag, cty);
    }

    // Synthesize a variable's CType from the symbol tables — array info first
    // (arrays lower to pointers, so this is the only place element+count
    // survive), then the plain local/global type. Null for builtins / unknowns.
    private CType? VarCType(string name)
    {
        if (_localArrayInfo.TryGetValue(name, out var la)) { return la; }
        if (_globalArrayInfo.TryGetValue(name, out var ga)) { return ga; }
        if (_localTypes.TryGetValue(name, out var lt)) { return new CType.Sized(lt); }
        if (_globalTypes.TryGetValue(name, out var gt)) { return new CType.Sized(gt); }
        return null;
    }
    // Integer literal. Decimal and hex (`0x…`) pass through (C# accepts both
    // verbatim); binary (`0b…`, C23) passes through too (C# has `0b`) but is
    // gated. A `0`-prefixed OCTAL constant (`0755`) is the one form C# can't
    // take literally — a leading `0` is plain decimal in C#, so `0755` would
    // silently mean 755 instead of 493. dotcc converts it to its value. C
    // suffixes (u/U/l/L, incl. the C99 `ll`) are normalised to C#'s (no `ll` —
    // both `l` and `ll` mean 64-bit `long` in C#).
    public EmitContent Visit(C.Num n)
    {
        var raw = T(n.Arg0);
        // Split trailing C suffix (u/U/l/L) off the digits. `ls` counts l/L
        // (>=2 is the C99 `long long` suffix); the suffix also fixes the
        // literal's type for sizeof.
        var end = raw.Length;
        var ls = 0;
        var hasU = false;
        while (end > 0 && (raw[end - 1] is 'u' or 'U' or 'l' or 'L'))
        {
            if (raw[end - 1] is 'l' or 'L') { ls++; } else { hasU = true; }
            end--;
        }
        var digits = raw[..end];
        // C23 digit separators (`1'000'000`) — strip the `'`s before parsing the
        // value; C# uses `_` and doesn't need them at all. The lexer only allows
        // a `'` between two digits, so removal is unambiguous.
        if (digits.IndexOf('\'') >= 0)
        {
            Gate(2023, "digit separators in a numeric literal", n.Arg0);  // C23
            digits = digits.Replace("'", "");
        }
        if (_dialectGate is not null && ls >= 2) { Gate(1999, "`long long` (ll) integer suffix", n.Arg0); }
        var ct = ls > 0 ? (hasU ? "ulong" : "long") : (hasU ? "uint" : "int");

        string valueText;
        if (digits.Length >= 2 && digits[0] == '0' && (digits[1] is 'x' or 'X'))
        {
            valueText = digits + CsIntSuffix(hasU, ls);          // hex — C# accepts 0x… verbatim
        }
        else if (digits.Length >= 2 && digits[0] == '0' && (digits[1] is 'b' or 'B'))
        {
            Gate(2023, "binary integer literal (`0b`)", n.Arg0);  // C23
            valueText = digits + CsIntSuffix(hasU, ls);          // binary — C# accepts 0b… verbatim
        }
        else if (digits.Length >= 2 && digits[0] == '0')
        {
            // C octal (`0`-prefix). Convert to its value and emit decimal, since
            // C# would read the leading 0 as a no-op and the literal as decimal.
            foreach (var d in digits)
            {
                if (d is < '0' or > '7')
                {
                    throw new CompileException($"invalid digit '{d}' in octal constant `{raw}`");
                }
            }
            ulong value;
            try { value = Convert.ToUInt64(digits, 8); }
            catch (OverflowException) { throw new CompileException($"octal constant `{raw}` is too large"); }
            valueText = value.ToString(System.Globalization.CultureInfo.InvariantCulture) + CsIntSuffix(hasU, ls);
        }
        else
        {
            valueText = digits + CsIntSuffix(hasU, ls);          // decimal
        }
        return Typed(valueText, new CType.Sized(ct));
    }

    // Map a C integer suffix (any u/U + any l/L) to C#'s canonical form. C# has
    // no `ll` (both `l` and `ll` are 64-bit `long`), so any number of l's → "L".
    private static string CsIntSuffix(bool hasU, int lCount) => (hasU, lCount) switch
    {
        (false, 0) => "",
        (true, 0)  => "u",
        (false, _) => "L",     // any l's → long
        (true, _)  => "UL",    // u + any l's → ulong
    };
    public EmitContent Visit(C.Flt n)
    {
        var raw = T(n.Arg0);
        var isFloat = raw.Length > 0 && (raw[^1] is 'f' or 'F');
        return Typed(raw, new CType.Sized(isFloat ? "float" : "double"));
    }

    // Adjacent string literals concatenate (C translation phase 6). strSeqOne/
    // Cons collect each segment's inner body (quotes stripped, escapes intact);
    // Visit(C.Str) decodes + emits once.
    public EmitContent Visit(C.StrSeqOne n) =>
        new EmitContent.StrParts(new[] { StripStrQuotes(T(n.Arg0)) });
    public EmitContent Visit(C.StrSeqCons n)
    {
        var prev = ((EmitContent.StrParts)n.Arg0.Content).Bodies;
        var combined = new List<string>(prev.Count + 1);
        combined.AddRange(prev);
        combined.Add(StripStrQuotes(T(n.Arg1)));
        return new EmitContent.StrParts(combined);
    }

    public EmitContent Visit(C.Str n)
    {
        // Decode each segment's C escapes to bytes INDEPENDENTLY, then
        // concatenate — so a `\x…` in one segment can't greedily eat the next
        // segment's leading hex digit. Emit one greedy-safe u8 literal; the
        // CType length is the decoded byte count + 1 for the NUL.
        var parts = (EmitContent.StrParts)n.Arg0.Content;
        var items = new List<StrItem>();
        foreach (var body in parts.Bodies) { DecodeCStringBody(body, items); }
        var (escaped, byteLen) = EmitU8(items);
        return Typed($"L(\"{escaped}\\0\"u8)", new CType.Arr(new CType.Sized("byte"), byteLen + 1));
    }

    // C char literal — type `int`, but our `char` is `byte`. Decode the escape
    // (or plain char) to its byte value: a plain printable ASCII char stays
    // readable as `(byte)'c'`, everything else (named/octal/hex escape, control)
    // lowers to `(byte)N`. sizeof('a') is sizeof(int) per C.
    public EmitContent Visit(C.Chr n)
    {
        var raw = T(n.Arg0);
        if (raw is null || raw.Length < 3) { return Typed("(byte)0", new CType.Sized("int")); }
        var body = raw[1..^1];
        var i = 0;
        var item = DecodeEscapeOrChar(body, ref i);
        string text;
        if (!item.IsByte && item.Value is >= 0x20 and <= 0x7E && item.Value != '\'' && item.Value != '\\')
        {
            text = $"(byte)'{(char)item.Value}'";
        }
        else
        {
            text = $"(byte){(item.IsByte ? item.Value : item.Value & 0xFF)}";
        }
        return Typed(text, new CType.Sized("int"));
    }

    // Comma operator (`Expr → Expr ',' E`). Accumulate operands left-to-right
    // into a CommaSeq; the value/statement lowering happens at the consumer
    // (Paren / StmtExpr) since C# has no comma operator.
    public EmitContent Visit(C.CommaOp n)
    {
        var ops = n.Arg0.Content is EmitContent.CommaSeq cs
            ? new List<string>(cs.Operands)
            : new List<string> { T(n.Arg0) };
        ops.Add(T(n.Arg2));
        return new EmitContent.CommaSeq(ops);
    }

    public EmitContent Visit(C.Paren n)
    {
        // Comma operator in value position: C# has no comma operator, but a
        // tuple evaluates its elements left-to-right and yields them all, so
        // `(a, b, c).Item3` reproduces "evaluate a, b, c in order, value is c".
        // (≤7 operands — ValueTuple's direct ItemN range; longer chains don't
        // occur in real code, so fail clearly rather than emit a wrong shape.)
        // Pointer/void operands can't go in a C# tuple — those reach Roslyn as a
        // type error rather than a silent miscompile, which is the safe failure.
        if (n.Arg1.Content is EmitContent.CommaSeq seq)
        {
            if (seq.Operands.Count > 7)
            {
                throw new CompileException(
                    "comma-operator chains longer than 7 operands aren't supported");
            }
            var joined = string.Join(", ", seq.Operands.Select(StripOuterParens));
            return $"({joined}).Item{seq.Operands.Count}";
        }
        return new EmitContent.Text($"({T(n.Arg1)})", EnumOf(n.Arg1), TyOf(n.Arg1));
    }

    // C23 keyword constants (only reached under -std=c23, via the rewriter's
    // ID->terminal promotion). `_Bool` lowers to C# `bool`, so the boolean
    // literals lower to their C# spellings; `nullptr` matches <stddef.h>'s
    // `#define NULL null` lowering. Pre-C23 these spellings stay ID and reach
    // the macro-supplied values through `Visit(C.Var)` instead.
    // C23 `true`/`false` lower to the integer literals 1/0 (normalized through
    // CBool's int conversion when stored to a `_Bool`, and usable directly as
    // ints — matching C, where `true`/`false` have value 1/0). Emitting them as
    // ints (not the C# `true`/`false` keywords) is also what lets a user
    // identifier spelled `true`/`false` be @-escaped: the keyword spelling now
    // only ever reaches Visit(Var) when it's a real identifier.
    public EmitContent Visit(C.LitTrue n)    => "1";
    public EmitContent Visit(C.LitFalse n)   => "0";
    public EmitContent Visit(C.LitNullptr n) => "null";

    // `_Static_assert(expr [, "msg"]);` (C11; C23 lowercase `static_assert`
    // and message-optional form). A compile-time-only construct with no
    // observable runtime behaviour, so for any program where the assertion
    // holds the correct emit is *nothing*. dotcc has no constant evaluator
    // yet, so we don't verify the condition — we drop it to a self-delimiting
    // block comment (carrying the message for traceability) and let Roslyn
    // compile the rest. This is observably equivalent to clang for every valid
    // program; a *false* static_assert that clang would reject is silently
    // accepted (documented limitation in C-SUPPORT.md). Works at both file
    // scope (Fn) and block scope (Stmt) — the comment is inert in either.
    // `_Static_assert` (and the C23 lowercase `static_assert` promoted onto it)
    // is a C11 feature — gate under < c11. Arg0 is the `_Static_assert` token.
    public EmitContent Visit(C.StaticAssert n)          { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(T(n.Arg4)); }
    public EmitContent Visit(C.StaticAssertNoMsg n)     { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(null); }
    public EmitContent Visit(C.StaticAssertStmt n)      { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(T(n.Arg4)); }
    public EmitContent Visit(C.StaticAssertStmtNoMsg n) { Gate(2011, "_Static_assert", n.Arg0); return StaticAssertComment(null); }

    /// <summary>
    /// Render a dropped <c>_Static_assert</c> as an inert block comment.
    /// <paramref name="rawMsg"/> is the raw STRING lexeme (quotes included) or
    /// null for the C23 message-less form. Any <c>*/</c> in the message is
    /// neutralised so it can't close the comment early.
    /// </summary>
    private static string StaticAssertComment(string? rawMsg)
    {
        var tail = rawMsg is null ? "" : ": " + rawMsg.Replace("*/", "* /");
        return $"/* static_assert (compile-time, not evaluated){tail} */";
    }

    /// <summary>
    /// Identity for now — kept as a seam in case future grammar features
    /// need to remap a C identifier to a different C# name before emit.
    /// </summary>
    /// <remarks>
    /// Previously this remapped <c>printf → Printf</c>, <c>malloc → Malloc</c>,
    /// <c>free → Free</c> to reach BuildShell's top-level (uppercase) helper
    /// functions. After the DotCC.Libc unification, the emitted shell has
    /// <c>using static Libc;</c> which brings the lowercase C-spelled
    /// methods directly into scope — so the remapping became unnecessary.
    /// </remarks>
    private static string MapBuiltin(string name) => Id(name);

    // ---- C#-keyword escaping --------------------------------------------
    // A C identifier can be a C# reserved keyword (`new`, `lock`, `is`,
    // `string`, `this`, `ref`, …) — all valid C names but illegal as bare C#
    // identifiers. C# allows them when prefixed with `@`, so we escape any such
    // name wherever a C identifier is emitted AS a C# identifier (declarators,
    // references, params, fields, member access, labels, enum constants).
    // Escaping is purely a function of the name, so a declaration and all its
    // references escape identically — consistency is automatic. CRUCIALLY this
    // is applied only at the *emit* point: structured data, side-table keys,
    // and the static/malloc name-mangling all keep the RAW C name, so they
    // never see an `@`.
    private static readonly HashSet<string> _csReservedKeywords = new(StringComparer.Ordinal)
    {
        // `true` and `false` ARE escaped: they now lower to the integer
        // literals 1/0 (via <stdbool.h> and the c23 LitTrue/LitFalse path), so
        // the spelling `true`/`false` only ever reaches Visit(Var) as a real
        // user identifier — safe to @-escape. `null` is still EXCLUDED: dotcc
        // emits it as the bare C# `null` literal (the only expression that
        // implicitly converts to any pointer type — see <stddef.h>'s
        // `#define NULL null`), and a macro-supplied `null` is indistinguishable
        // from a user variable named `null`, so a variable named `null` stays
        // the lone residual edge. `default` is also omitted: it's a C keyword
        // (never a C identifier) and dotcc emits it for value-init.
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal",
        "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while",
    };

    /// <summary>
    /// Escape a C identifier for emission as a C# identifier: prefix it with
    /// <c>@</c> when it collides with a C# reserved keyword, otherwise return
    /// it unchanged. (Most C keywords here — <c>int</c>, <c>for</c>, … — can
    /// never be C identifiers, so the rule fires only for the C#-only reserved
    /// words like <c>new</c>/<c>lock</c>/<c>string</c>.)
    /// </summary>
    internal static string Id(string name) =>
        _csReservedKeywords.Contains(name) ? "@" + name : name;

    /// <summary>
    /// Inverse of <see cref="Id"/> for a single emitted identifier: strip a
    /// leading <c>@</c> escape. Used when an emitted name (already escaped by
    /// <see cref="Visit(C.Var)"/>) must be matched against a side table keyed
    /// by the RAW C name (e.g. the malloc-promotion maps).
    /// </summary>
    private static string Unescape(string emitted) =>
        emitted.Length > 0 && emitted[0] == '@' ? emitted.Substring(1) : emitted;

    // ---- C string/char escape decoding ----------------------------------
    // One element of a decoded body: either a literal source character (to be
    // UTF-8 encoded by the C# u8 literal) or a decoded escape byte (0–255).
    private readonly record struct StrItem(bool IsByte, int Value);

    private static string StripStrQuotes(string raw) =>
        raw is { Length: >= 2 } ? raw[1..^1] : "";

    // Decode the char or escape sequence starting at body[i], advancing i past
    // it. Handles the named escapes, GNU `\e`, 1–3-digit octal, and greedy
    // `\xNN` hex — each to its byte value; an unknown escape yields the char.
    private static StrItem DecodeEscapeOrChar(string body, ref int i)
    {
        char c = body[i];
        if (c != '\\' || i + 1 >= body.Length) { i++; return new StrItem(false, c); }
        i++;                  // consume the backslash
        char e = body[i];
        i++;                  // consume the escape selector (octal/hex re-advance below)
        switch (e)
        {
            case 'n':  return new StrItem(true, 0x0A);
            case 't':  return new StrItem(true, 0x09);
            case 'r':  return new StrItem(true, 0x0D);
            case '\\': return new StrItem(true, 0x5C);
            case '"':  return new StrItem(true, 0x22);
            case '\'': return new StrItem(true, 0x27);
            case '?':  return new StrItem(true, 0x3F);
            case 'a':  return new StrItem(true, 0x07);
            case 'b':  return new StrItem(true, 0x08);
            case 'f':  return new StrItem(true, 0x0C);
            case 'v':  return new StrItem(true, 0x0B);
            case 'e':  return new StrItem(true, 0x1B);  // GNU \e (ESC)
            case 'x':
            {
                int val = 0, cnt = 0;
                while (i < body.Length && Uri.IsHexDigit(body[i])) { val = val * 16 + HexVal(body[i]); i++; cnt++; }
                if (cnt == 0) { throw new CompileException("`\\x` used with no following hex digits"); }
                return new StrItem(true, val & 0xFF);
            }
            case >= '0' and <= '7':
            {
                int val = e - '0', cnt = 1;
                while (i < body.Length && cnt < 3 && body[i] is >= '0' and <= '7') { val = val * 8 + (body[i] - '0'); i++; cnt++; }
                return new StrItem(true, val & 0xFF);
            }
            default: return new StrItem(false, e);  // unknown escape → the char itself
        }
    }

    private static void DecodeCStringBody(string body, List<StrItem> into)
    {
        var i = 0;
        while (i < body.Length) { into.Add(DecodeEscapeOrChar(body, ref i)); }
    }

    private static int HexVal(char c) => c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;

    // Re-emit decoded items as a greedy-safe C# u8-literal body, returning the
    // escaped text and byte length. Source chars pass through (the u8 literal
    // UTF-8-encodes them — matching C's UTF-8 source bytes); decoded escape
    // bytes become a named escape or `\xHH`, and a `\xHH` is never left next to
    // a literal hex digit (C# would greedily fold it into the escape). A decoded
    // escape byte > 0x7F can't be one byte in a u8 literal (C# UTF-8-encodes
    // `\x80`+ as multi-byte), so fail loudly rather than miscompile.
    private static (string Escaped, int ByteLen) EmitU8(List<StrItem> items)
    {
        var sb = new StringBuilder(items.Count + 8);
        var len = 0;
        var prevHex = false;
        foreach (var it in items)
        {
            if (!it.IsByte)
            {
                char ch = (char)it.Value;
                if (ch < 0x80)
                {
                    len += 1;
                    if (ch == '"') { sb.Append("\\\""); prevHex = false; }
                    else if (ch == '\\') { sb.Append("\\\\"); prevHex = false; }
                    else if (ch is >= (char)0x20 and <= (char)0x7E)
                    {
                        if (prevHex && Uri.IsHexDigit(ch)) { sb.Append("\\x").Append(((int)ch).ToString("X2")); prevHex = true; }
                        else { sb.Append(ch); prevHex = false; }
                    }
                    else { sb.Append("\\x").Append(((int)ch).ToString("X2")); prevHex = true; }
                }
                else
                {
                    // Non-ASCII source char → emit literally; the u8 literal
                    // UTF-8-encodes it (matches C's UTF-8 source bytes).
                    len += System.Text.Encoding.UTF8.GetByteCount(ch.ToString());
                    sb.Append(ch);
                    prevHex = false;
                }
            }
            else
            {
                int b = it.Value;
                if (b > 0x7F)
                {
                    throw new CompileException(
                        $"string escape byte 0x{b:X2} > 0x7F isn't representable in a UTF-8 literal yet "
                        + "(dotcc emits string data as a C# u8 literal).");
                }
                len += 1;
                switch (b)
                {
                    case 0x0A: sb.Append("\\n"); prevHex = false; break;
                    case 0x0D: sb.Append("\\r"); prevHex = false; break;
                    case 0x09: sb.Append("\\t"); prevHex = false; break;
                    case 0x22: sb.Append("\\\""); prevHex = false; break;
                    case 0x5C: sb.Append("\\\\"); prevHex = false; break;
                    default: sb.Append("\\x").Append(b.ToString("X2")); prevHex = true; break;
                }
            }
        }
        return (sb.ToString(), len);
    }

    private static int CountCommas(string s)
    {
        var count = 0;
        foreach (var c in s) { if (c == ',') { count++; } }
        return count;
    }

    private static string StripOuterParens(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 2 || s[0] != '(' || s[^1] != ')') { return s; }
        var depth = 0;
        for (var i = 0; i < s.Length - 1; i++)
        {
            if (s[i] == '(') { depth++; }
            else if (s[i] == ')') { depth--; if (depth == 0) { return s; } }
        }
        return s.Substring(1, s.Length - 2);
    }

    private static string IndentEach(string block)
    {
        if (string.IsNullOrEmpty(block)) { return block; }
        var sb = new StringBuilder(block.Length + 32);
        var first = true;
        foreach (var line in block.Split('\n'))
        {
            if (!first) { sb.Append('\n'); }
            first = false;
            if (line.Length == 0) { continue; }
            sb.Append("    ").Append(line);
        }
        if (block.EndsWith('\n')) { sb.Append('\n'); }
        return sb.ToString();
    }
}
