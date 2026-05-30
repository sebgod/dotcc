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
        // Adopt the params staged during this FnSig's ParamList reduction.
        foreach (var p in _pendingParams) { _localNames.Add(p.Name); _localTypes[p.Name] = p.Type; }
        _pendingParams.Clear();
        return new EmitContent.FnHeader(type, name, pars, isStatic);
    }

    // ---- Function definition / prototype --------------------------------
    // `Fn → FnSig Block` and `Fn → FnSig ';'`. The FnSig has already run
    // and stashed (type/name/params/isStatic) into a typed FnHeader plus
    // set `_currentFunctionName` for the body's Var visits to consume.
    // Now we do the bookkeeping (MainArity, exports list) and emit/clear.

    public EmitContent Visit(C.FuncDef n)
    {
        var sig = FH(n.Arg0);
        var body = T(n.Arg1);
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
        // Escape the method name for C# emission; sig.Name stays raw above for
        // the `main` check and the export list (the C-ABI EntryPoint keeps the
        // real name). A call to this function escapes identically via Visit(Var).
        return $"static unsafe {sig.Type} {Id(sig.Name)}({sig.Params})\n{body}";
    }

    public EmitContent Visit(C.FuncProto n)
    {
        // Prototypes emit nothing — C# methods hoist. We still need to
        // unwind the FnSig's _currentFunctionName since the body wasn't
        // visited but the name was set.
        _currentFunctionName = null;
        _currentFunctionReturnType = null;
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
            // Track global var → type for enum-typing of references inside functions.
            if (_enumTags.Contains(rawType)) { _globalTypes[entry.Name] = rawType; }
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

        // float / double / void don't take signedness or size modifiers.
        if ((baseKw is "float" or "double" or "void")
            && (unsignedCount > 0 || signedCount > 0 || shortCount > 0 || longCount > 0))
        {
            throw new CompileException(
                $"`{baseKw}` cannot take size or sign modifiers (got `{PrettySpecs(specs)}`)");
        }

        // Resolve. Order: _Bool first (mutually exclusive), then non-int
        // bases, then char (with signedness), then sized-int family.
        if (boolCount == 1) { return "CBool"; }
        if (float128Count == 1) { return "Float128"; }
        if (baseKw == "float")  { return "float"; }
        if (baseKw == "double") { return "double"; }
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
        // _blkOutOfOrder for this block; report it once here. n.Arg0 is `{`.
        if (_dialectGate is not null && _blkOutOfOrder)
        {
            Gate(1999, "mixed declarations and statements", n.Arg0);
        }
        return "{\n" + IndentEach(T(n.Arg1)) + "}\n";
    }
    public EmitContent Visit(C.BlockEmpty n) => "{ }\n";
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
    public EmitContent Visit(C.StmtForDecl n)
    {
        Gate(1999, "declaration in `for` initializer", n.Arg0);  // C99
        return $"for ({T(n.Arg2)}; {CondOf(n.Arg4)}; {T(n.Arg6)}) {T(n.Arg8)}";
    }
    public EmitContent Visit(C.StmtForExpr n) =>
        $"for ({T(n.Arg2)}; {CondOf(n.Arg4)}; {T(n.Arg6)}) {T(n.Arg8)}";

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
    public EmitContent Visit(C.StmtExpr n) =>
        // CS0201: bare parenthesized assignment isn't a statement. Peel the
        // outer parens that our binop emitters wrap on.
        $"{StripOuterParens(T(n.Arg0))};\n";

    // Declarations
    // `Type DeclItemList` — covers single (`int x;`), single-with-init
    // (`int x = 5;`), and multi-declarator (`int x, y, z;`,
    // `int x = 1, y = 2;`) forms. C# accepts the same `int x, y = 5, z;`
    // syntax so the lowering is verbatim.
    public EmitContent Visit(C.Decl n)
    {
        var type = T(n.Arg0);
        var entries = DE(n.Arg1);
        // Register name + declared type for shadow resolution and enum typing.
        foreach (var e in entries)
        {
            NoteLocal(e.Name);
            if (_currentFunctionName is not null) { _localTypes[e.Name] = type; }
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
                return $"{structType} {Id(entries[0].Name)} = new {structType}()";
            }
        }

        // Reconcile each initializer's enum-ness with the declared type
        // (`Color c = 2` → `(Color)(2)`; `int x = c` → `(int)(c)`).
        entries = entries.Select(e => e with { Init = ReconcileEnumInit(type, e) }).ToList();
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
    // C `T arr[N]` → C# `T* arr = stackalloc T[N]`. Uses stackalloc (no heap
    // alloc, no GC pin) so arrays live in the same lifetime as locals — matches
    // C semantics for block-scoped automatic arrays. Pointer subscript `arr[i]`
    // works directly in C# unsafe contexts (it desugars to `*(arr + i)`).
    public EmitContent Visit(C.DeclArr n)
    {
        NoteLocal(T(n.Arg1));
        return $"{T(n.Arg0)}* {Id(T(n.Arg1))} = stackalloc {T(n.Arg0)}[{StripOuterParens(T(n.Arg3))}]";
    }

    // C `T arr[N] = {1, 2, 3}` (or `T arr[] = {…}`) → C# `T* arr = stackalloc T[]{ 1, 2, 3 }`.
    // The explicit-size form ignores the size operand because C# infers it
    // from the initializer; both shapes share the same emit. ArgList arrives
    // as a typed EmitContent.Args (read via A()) — no sentinel decoding.
    public EmitContent Visit(C.DeclArrInit n)
    {
        NoteLocal(T(n.Arg1));
        return EmitArrInit(T(n.Arg0), T(n.Arg1), A(n.Arg7));
    }
    public EmitContent Visit(C.DeclArrInitImplicit n)
    {
        NoteLocal(T(n.Arg1));
        return EmitArrInit(T(n.Arg0), T(n.Arg1), A(n.Arg6));
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
        return $"{type} {Id(name)} = new {type} {{ {string.Join(", ", members)} }}";
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
    public EmitContent Visit(C.MemberInit n) => $"{Id(T(n.Arg1))} = {T(n.Arg3)}";

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
        var values = A(n.Arg4);  // typed EmitContent.Args — no sentinel split

        if (!_structFields.TryGetValue(type, out var fields))
        {
            return $"{type} {Id(name)} = default /* dotcc: unknown struct '{type}' for aggregate init */";
        }

        var sb = new StringBuilder();
        sb.Append(type).Append(' ').Append(Id(name)).Append(" = new ").Append(type).Append(" { ");
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
        return new EmitContent.Text($"(({castType}){T(n.Arg3)})",
            _enumTags.Contains(castType) ? castType : null);
    }
    public EmitContent Visit(C.Deref n) => $"(*{T(n.Arg1)})";
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
    // Subscript `expr[i]` — emit as-is; C# pointer subscript matches C semantics.
    // An enum index decays to int (C# can't index with an enum).
    public EmitContent Visit(C.Subscript n) =>
        $"({StripOuterParens(T(n.Arg0))}[{StripOuterParens(IntDecay(n.Arg2))}])";

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
        // Tag the result with the callee's enum return type so a consuming
        // context reconciles (decay in an int slot, no cast in an enum slot).
        if (_fnReturnTypes.TryGetValue(Unescape(callee), out var rt) && _enumTags.Contains(rt))
        {
            return new EmitContent.Text(callText, rt);
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
            return $"L(\"{fn}\\0\"u8)";
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
            return new EmitContent.Text($"{enumName}.{Id(name)}", enumName);
        }
        // An enum-typed variable read is itself enum-valued — tag it so consuming
        // nodes insert the int↔enum casts C# requires (`int x = c`, `c & MASK`,
        // …). Locals/params win over globals (shadowing).
        var mapped = MapBuiltin(name);
        if ((_localTypes.TryGetValue(name, out var vt) || _globalTypes.TryGetValue(name, out vt))
            && _enumTags.Contains(vt))
        {
            return new EmitContent.Text(mapped, vt);
        }
        return mapped;
    }
    // Integer literal — pass-through for unsuffixed; normalize C suffixes
    // (u/U/l/L/ll/LL/ul/ull, case-insensitive, order-insensitive) to C#'s
    // equivalents (no `ll` form — both `l` and `ll` mean 64-bit `long` in
    // C#, since C# `long` is unconditionally 64-bit).
    public EmitContent Visit(C.Num n)
    {
        var raw = T(n.Arg0);
        // The `ll`/`LL`/`ull`/`ULL` suffix (>= 2 consecutive Ls) is the C99
        // `long long` integer-literal suffix — gate under < c99.
        if (_dialectGate is not null)
        {
            var ls = 0;
            for (int i = raw.Length - 1; i >= 0 && (raw[i] is 'l' or 'L' or 'u' or 'U'); i--)
            {
                if (raw[i] is 'l' or 'L') { ls++; }
            }
            if (ls >= 2) { Gate(1999, "`long long` (ll) integer suffix", n.Arg0); }
        }
        return NormalizeIntSuffix(raw);
    }

    private static string NormalizeIntSuffix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) { return raw; }
        var end = raw.Length;
        while (end > 0 && (raw[end - 1] is 'u' or 'U' or 'l' or 'L')) { end--; }
        if (end == raw.Length) { return raw; }
        var digits = raw[..end];
        var suffix = raw[end..];
        var hasU = false;
        var lCount = 0;
        foreach (var c in suffix)
        {
            if (c is 'u' or 'U') { hasU = true; }
            else if (c is 'l' or 'L') { lCount++; }
        }
        // C# suffix mapping: u → uint/ulong (compiler-chosen), L → long,
        // UL → ulong. C# accepts both lowercase and uppercase; we emit the
        // C# canonical uppercase form for readability.
        return (hasU, lCount) switch
        {
            (false, 0) => digits,           // unreachable (no suffix removed)
            (true, 0)  => digits + "u",
            (false, _) => digits + "L",     // any number of l's → long
            (true, _)  => digits + "UL",    // u + any l's → ulong
        };
    }
    public EmitContent Visit(C.Flt n) => T(n.Arg0);

    public EmitContent Visit(C.Str n)
    {
        var raw = T(n.Arg0);
        if (raw is null || raw.Length < 2) { return "L(\"\\0\"u8)"; }
        var body = raw[1..^1];
        return $"L(\"{EscapeForUtf8Literal(body)}\\0\"u8)";
    }

    // `'a'`, `'\n'`, `'\\'` etc. — C char literal. Our `char` is C# `byte`,
    // so we lower to `(byte)'X'` where X is the unescaped char value.
    // Pass the C escape sequence through to C#'s char literal syntax —
    // both languages accept `\n`, `\t`, `\\`, `\'`, `\"`, `\0`, `\r`.
    public EmitContent Visit(C.Chr n)
    {
        var raw = T(n.Arg0);
        if (raw is null || raw.Length < 3) { return "(byte)0"; }
        var body = raw[1..^1];
        return $"(byte)'{body}'";
    }

    public EmitContent Visit(C.Paren n) => new EmitContent.Text($"({T(n.Arg1)})", EnumOf(n.Arg1));

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

    private static string EscapeForUtf8Literal(string body)
    {
        var sb = new StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                sb.Append('\\').Append(body[i + 1]);
                i++;
                continue;
            }
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
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
