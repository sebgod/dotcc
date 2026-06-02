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
    private string T(Item it) => it.Content switch
    {
        EmitContent.Text t => t.Value,
        EmitContent.DeclStmtMarker d => d.Value,
        // A void-typed ternary reaching a VALUE consumer is invalid C (a void
        // expression can't be value-used); render its (invalid-in-C#) ternary
        // text so Roslyn errors loudly. Statement/body consumers special-case
        // VoidCond BEFORE calling T() and emit its if/else form instead.
        EmitContent.VoidCond vc => vc.Value,
        // A value-context comma needing statement hoisting (void leading operand)
        // reached a consumer that can't lift statements (a function argument, a
        // tuple element, …). The statement-level sinks (assignment / decl-init /
        // return / expression-statement) special-case SeqExpr BEFORE T(); reaching
        // here means a context dotcc can't hoist into — fail loudly, don't miscompile.
        EmitContent.SeqExpr =>
            throw new CompileException(
                "a comma expression with a void leading operand (e.g. a bounds-check " +
                "macro like Lua's `luaM_newvectorchecked`) is only supported where its " +
                "statements can be hoisted — an assignment, declaration initializer, " +
                "`return`, or expression statement. It can't be used as a function " +
                "argument or other nested value position."),
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
        // A bare comma sequence reaching a VALUE consumer (e.g. an unparenthesized
        // comma used where a value is needed) lowers to the same `(a, b).ItemN`
        // tuple a parenthesized `(a, b)` does. Discard consumers (StmtExpr / the
        // controlling-expression visitors / `(void)` casts) special-case the
        // CommaSeq BEFORE calling T(), so they never hit this path.
        EmitContent.CommaSeq cs => CommaTupleText(cs.Operands, cs.OperandTypes),
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

    /// <summary>True when this <c>Type</c> child carried a C99 <c>inline</c>
    /// function specifier (so the enclosing function gets AggressiveInlining).</summary>
    private static bool InlineOf(Item it) => it.Content is EmitContent.Text { Inline: true };

    /// <summary>True when this <c>Type</c> child carried a C11 <c>_Noreturn</c>
    /// function specifier (so the enclosing function gets [DoesNotReturn]).</summary>
    private static bool NoreturnOf(Item it) => it.Content is EmitContent.Text { Noreturn: true };

    /// <summary>Build a Text result carrying a synthesized type (for sizeof).</summary>
    private static EmitContent Typed(string text, CType? ty) => new EmitContent.Text(text, null, ty);

    /// <summary>Read an operand's text, decaying an enum value to its `int`
    /// underlying so it can take part in a C int operation.</summary>
    private string IntDecay(Item it) => EnumOf(it) is null ? T(it) : $"(int){T(it)}";

    /// <summary>Wrap a child as a C-truthy condition (`Cond.B(...)`), decaying an
    /// enum first — `Cond.B` has int/double/pointer/bool overloads but not enum,
    /// so `if (color)` must become `Cond.B((int)color)`.</summary>
    private string CondOf(Item it) => $"Cond.B({IntDecay(it)})";

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

    // -Wconversion sink. Non-null ONLY on the emit pass when -Wconversion is set;
    // null otherwise — so the narrowing-store check (CoerceStore) is a no-op on the
    // default path and collects each store once. (The cast it inserts to satisfy
    // C# is emitted regardless of the gate — only the WARNING is gated.)
    private readonly ConversionGate? _conversionGate;

    public CSharpEmitter(
        IReadOnlySet<(string Fn, string Var)>? promotable = null,
        DialectGate? dialectGate = null,
        ConversionGate? conversionGate = null)
    {
        _promotableIn = promotable ?? new HashSet<(string, string)>();
        _dialectGate = dialectGate;
        _conversionGate = conversionGate;
    }

    // Flag a construct introduced by a standard newer than the active dialect.
    // `introducedEra` is the ISO year (matches CDialect.Version, keyed by year:
    // 1999 / 2011 / 2023). Source line from the construct's first token. No-op
    // when not gating.
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

    // Per-name copy of the type declarations, in emit order. Whole-program emit
    // uses _structs (above) unchanged; separate compilation (`--emit=obj`) reads
    // this so the link step can union types by name across translation units
    // (a shared header's struct appears in every TU's object). Populated
    // alongside each _structs append.
    private readonly Dictionary<string, string> _typeDecls = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> TypeDecls => _typeDecls;

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
    // `\x80`+ as multi-byte); Visit(C.Str) routes such strings to EmitByteArray
    // before reaching here, so the guard below is defensive (should never fire).
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
                    // Defensive: Visit(C.Str) sends high-byte strings to the
                    // byte-array path, so this should be unreachable.
                    throw new CompileException(
                        $"string escape byte 0x{b:X2} > 0x7F reached the u8-literal path "
                        + "(expected the byte-array lowering) — please report this.");
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

    // Build the EXACT C byte sequence as a C# constant byte-array initializer
    // (`new byte[]{ 0xHH, …, 0 }`, NUL-terminated). Used when a decoded escape
    // byte > 0x7F can't ride a u8 literal (C# would UTF-8 re-encode `\x80`+ into
    // two bytes). Each decoded escape byte goes in verbatim; a source char is
    // expanded to its UTF-8 bytes (matching C's UTF-8 source encoding, exactly
    // as the u8 path does). Roslyn lowers `new byte[]{consts}` in a
    // ReadOnlySpan<byte> position to an RVA blob — fixed address, no allocation,
    // no GC move — so L()'s pinned pointer stays valid for the program lifetime,
    // identical to the u8-literal case. Returns the initializer text and the
    // byte length (excluding the NUL the caller accounts for).
    private static (string Text, int ByteLen) EmitByteArray(List<StrItem> items)
    {
        var bytes = new List<int>(items.Count + 1);
        foreach (var it in items)
        {
            if (it.IsByte) { bytes.Add(it.Value & 0xFF); }
            else
            {
                foreach (var u in System.Text.Encoding.UTF8.GetBytes(((char)it.Value).ToString()))
                {
                    bytes.Add(u);
                }
            }
        }
        var sb = new StringBuilder("new byte[]{ ");
        foreach (var b in bytes) { sb.Append("0x").Append(b.ToString("X2")).Append(", "); }
        sb.Append("0 }");  // NUL terminator
        return (sb.ToString(), bytes.Count);
    }

    private static int CountCommas(string s)
    {
        var count = 0;
        foreach (var c in s) { if (c == ',') { count++; } }
        return count;
    }

    /// <summary>
    /// Strip ALL redundant outer paren layers (<c>((x))</c> → <c>x</c>). Iterating
    /// matters for a macro-parenthesized assignment used as a statement-expression:
    /// <c>step()</c> = <c>(i = i+1)</c> lowers to <c>((i = (i+1)))</c> (the assign
    /// emitter's parens plus the macro's), and a parenthesized assignment
    /// <c>(i = …);</c> is CS0201 in C# — only the bare assignment is a valid
    /// statement-expression. One pass would leave a layer; the loop reduces it to
    /// <c>i = (i+1)</c>. A paren whose match closes before the end (<c>(a)(b)</c>,
    /// <c>(a) + (b)</c>) is NOT redundant and is left intact.
    /// </summary>
    private static string StripOuterParens(string s)
    {
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            var depth = 0;
            var redundant = true;
            for (var i = 0; i < s.Length - 1; i++)
            {
                if (s[i] == '(') { depth++; }
                else if (s[i] == ')') { depth--; if (depth == 0) { redundant = false; break; } }
            }
            if (!redundant) { break; }
            s = s.Substring(1, s.Length - 2);
        }
        return s;
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
