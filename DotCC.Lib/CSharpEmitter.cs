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
        string s => s,
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

    // Name of the function currently being reduced. Set by each fnSig*
    // action; cleared by funcDef/funcProto when the enclosing Fn finishes.
    // Read by Visit(Var) so `__func__` resolves directly to the enclosing
    // function's name — no placeholder-string substitution.
    private string? _currentFunctionName;

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
        _currentFunctionName = null;  // exit function scope
        return $"static unsafe {sig.Type} {sig.Name}({sig.Params})\n{body}";
    }

    public EmitContent Visit(C.FuncProto n)
    {
        // Prototypes emit nothing — C# methods hoist. We still need to
        // unwind the FnSig's _currentFunctionName since the body wasn't
        // visited but the name was set.
        _currentFunctionName = null;
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

    // File-scope variable declarations. Appended to the _globals side
    // channel; the shell wraps them in a `static unsafe class DotCcGlobals`
    // declared in the type-decls section. Field type uses the QualifyPredefinedTypeName
    // pass so `jmp_buf env;` (which after typedef lowering references
    // `LongJmpToken`) reaches `Libc.LongJmpToken` correctly — bare
    // nested-type names don't resolve at class-member-decl position
    // for the same reason the alias-emit path qualifies them.
    public EmitContent Visit(C.GlobalVar n)
    {
        var rawType = T(n.Arg0);
        var type = QualifyPredefinedTypeName(rawType);
        var name = T(n.Arg1);
        // Reference types need `new T()`; value types initialize to default
        // by C# semantics. Without an initializer, a C# reference field
        // defaults to null, which breaks reference-equality dispatch
        // (the longjmp exception filter compares Token instances). For
        // predefined ref types (LongJmpToken) AND aliases that resolve
        // to them (jmp_buf via typedef), auto-instantiate.
        var isRefType = IsPredefinedRefTypeName(rawType) || _refTypeAliases.Contains(rawType);
        var init = isRefType ? $" = new {type}()" : string.Empty;
        _globals.Append("    public static unsafe ").Append(type).Append(' ').Append(name).Append(init).Append(";\n");
        return string.Empty;
    }

    public EmitContent Visit(C.GlobalVarInit n)
    {
        var type = QualifyPredefinedTypeName(T(n.Arg0));
        var name = T(n.Arg1);
        var init = T(n.Arg3);
        _globals.Append("    public static unsafe ").Append(type).Append(' ').Append(name).Append(" = ").Append(init).Append(";\n");
        return string.Empty;
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
        _pendingFields.Add(fieldName);
        return $"public {T(n.Arg0)} {fieldName};\n";
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

    // `enum ID` as a type reference — lowers to plain `int` because we emit
    // enumerators as `const int` rather than a C# enum (so the bare names
    // are usable like C's int constants without explicit casts).
    public EmitContent Visit(C.TypeEnum n) => "int";

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

    // Struct/union/typedef-struct field-name tracker. Same precedent as
    // `_enumerators`: visitor-time symbol table. StructMember pushes each
    // field's name onto `_pendingFields` during child-visit; the enclosing
    // StructDef / TypedefStruct / UnionDef drains it into `_structFields`
    // keyed by the type name. The struct-aggregate-init visitor consults
    // this so `Point p = {1, 2};` lands as `Point p = new Point { x = 1, y = 2 };`
    // (C# has no positional-init form for structs — it needs named members).
    private readonly List<string> _pendingFields = new();
    private readonly Dictionary<string, List<string>> _structFields = new(StringComparer.Ordinal);

    // `enum Name { A, B = 5, C } ;` — emit a `static class Name { public const
    // int A = …; … }` into the type-decl side channel. `static class` + `const
    // int` (rather than a real C# `enum`) avoids the awkward enum-to-int casts
    // every use site would otherwise need: `Color.Red` is already `int 0`,
    // usable directly as a C function arg, printf operand, switch case, etc.
    // Auto-numbering: each item with no explicit initializer takes `prev + 1`;
    // the first such item is 0. Returns empty so the function-emit stream
    // stays free of type decls (which C# requires AFTER top-level statements).
    public EmitContent Visit(C.EnumDef n)
    {
        var enumName = T(n.Arg1);
        var items = EI(n.Arg3);  // typed EmitContent.EnumItems — no sentinel split
        _structs.Append("static class ").Append(enumName).Append("\n{\n");
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
            _enumerators[itemName] = enumName;
            _structs.Append("    public const int ").Append(itemName).Append(" = ").Append(valueText).Append(";\n");
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
    public EmitContent Visit(C.Param n) => $"{T(n.Arg0)} {T(n.Arg1)}";
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

    public EmitContent Visit(C.TypeFromSpec n) => ResolveTypeSpec(S(n.Arg0));

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
        if (boolCount == 1) { return "bool"; }
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
    public EmitContent Visit(C.Block n) => "{\n" + IndentEach(T(n.Arg1)) + "}\n";
    public EmitContent Visit(C.BlockEmpty n) => "{ }\n";
    public EmitContent Visit(C.StmtsCons n) => T(n.Arg0) + T(n.Arg1);
    public EmitContent Visit(C.StmtsOne n) => T(n.Arg0);
    // Conditions are wrapped with B(...) so int- and pointer-valued conditions
    // (`while (1)`, `if (p)`, `for (...; n; ...)`) typecheck. The B overloads
    // live in BuildShell — see Compiler.BuildShell.
    public EmitContent Visit(C.StmtIf n)
    {
        var condText = T(n.Arg2);
        if (TryRecognizeSetjmpInIf(condText, out var envName, out var conditionIsZero))
        {
            // `if (setjmp(env)) { recovery }` with no else: the normal
            // path continues after the if. We can't wrap "the rest of
            // the function" locally — that's the documented limitation.
            // Require an else branch for setjmp idioms.
            throw new CompileException(
                $"setjmp(env) in an `if` condition requires a matching `else` clause; " +
                $"see the supported patterns in <setjmp.h>'s header comment.");
        }
        return $"if (Cond.B({condText})) {T(n.Arg4)}";
    }

    public EmitContent Visit(C.StmtIfElse n)
    {
        var condText = T(n.Arg2);
        var thenBody = T(n.Arg4);
        var elseBody = T(n.Arg6);
        if (TryRecognizeSetjmpInIf(condText, out var envName, out var conditionIsZero))
        {
            // Map if/else to try/catch based on which condition shape
            // we matched:
            //   if (setjmp(env))         { recovery } else { normal }
            //     → conditionIsZero == false → catch=then, try=else
            //   if (setjmp(env) == 0)    { normal }   else { recovery }
            //     → conditionIsZero == true  → catch=else, try=then
            var (tryBody, catchBody) = conditionIsZero
                ? (thenBody, elseBody)
                : (elseBody, thenBody);
            return
                $"try {tryBody}" +
                $"catch (Libc.LongJmpException __jmp) when (__jmp.Token == {envName}) " +
                $"{{ var __longjmp_value = __jmp.Value; {catchBody} }}";
        }
        return $"if (Cond.B({condText})) {thenBody}else {elseBody}";
    }

    /// <summary>
    /// Pattern-match a condition text for the standard <c>setjmp</c>
    /// idioms. Returns the env-name and whether the condition was
    /// <c>== 0</c> (true on first call) or bare (true on longjmp
    /// recovery).
    /// </summary>
    private static bool TryRecognizeSetjmpInIf(string condText, out string envName, out bool conditionIsZero)
    {
        // Whitespace + parens are added by various visitors; canonicalise
        // by stripping outer parens repeatedly before matching.
        var s = condText.Trim();
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            // Only strip if these parens are balanced as the outermost.
            var depth = 0;
            var balanced = true;
            for (var i = 0; i < s.Length - 1; i++)
            {
                if (s[i] == '(') { depth++; }
                else if (s[i] == ')') { depth--; if (depth == 0) { balanced = false; break; } }
            }
            if (!balanced) { break; }
            s = s[1..^1].Trim();
        }

        // Shape A: `setjmp(NAME)` — truthy on recovery.
        // Shape B: `setjmp(NAME) == 0` (or `0 == setjmp(NAME)`) — truthy on first call.
        // The emit puts spaces around `==`; allow `0 == setjmp(...)` form
        // too in case future visitors flip the operands.
        var setjmpMatch = SetjmpCallPattern.Match(s);
        if (setjmpMatch.Success && setjmpMatch.Index == 0 && setjmpMatch.Length == s.Length)
        {
            envName = setjmpMatch.Groups[1].Value;
            conditionIsZero = false;
            return true;
        }

        var eqMatch = SetjmpEqualsZeroPattern.Match(s);
        if (eqMatch.Success)
        {
            envName = eqMatch.Groups[1].Value;
            conditionIsZero = true;
            return true;
        }

        envName = string.Empty;
        conditionIsZero = false;
        return false;
    }

    // Match `setjmp(IDENT)` with optional whitespace; capture the env name.
    private static readonly System.Text.RegularExpressions.Regex SetjmpCallPattern =
        new(@"^\s*setjmp\s*\(\s*(\w+)\s*\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Match `setjmp(IDENT) == 0` or `0 == setjmp(IDENT)`, with optional parens
    // around the setjmp call or the zero. The emit's Equ visitor wraps both
    // operands in parens, so the common shape we'll actually see is
    // `(setjmp(env)) == (0)`.
    private static readonly System.Text.RegularExpressions.Regex SetjmpEqualsZeroPattern =
        new(@"^\s*\(?\s*setjmp\s*\(\s*(\w+)\s*\)\s*\)?\s*==\s*\(?\s*0\s*\)?\s*$|^\s*\(?\s*0\s*\)?\s*==\s*\(?\s*setjmp\s*\(\s*(\w+)\s*\)\s*\)?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    public EmitContent Visit(C.StmtWhile n) => $"while (Cond.B({T(n.Arg2)})) {T(n.Arg4)}";

    // `do Stmt while (E) ;` — body runs at least once. C# accepts the same
    // shape; only the condition needs Cond.B wrapping. Note the trailing
    // semicolon is required in both C and C#.
    public EmitContent Visit(C.StmtDoWhile n) =>
        $"do {T(n.Arg1)}while (Cond.B({T(n.Arg4)}));\n";

    // `for (Decl; E; E) Stmt` — emit C#'s for verbatim. C# accepts the same
    // shape; the init declaration scopes to the loop body. The init Decl
    // here is the LHS (`int i = 0` form); the StripOuterParens on the
    // incr keeps the emitter from wrapping `i++` in extra parens that C#
    // rejects in for-clause position.
    public EmitContent Visit(C.StmtForDecl n) =>
        $"for ({T(n.Arg2)}; Cond.B({T(n.Arg4)}); {T(n.Arg6)}) {T(n.Arg8)}";
    public EmitContent Visit(C.StmtForExpr n) =>
        $"for ({T(n.Arg2)}; Cond.B({T(n.Arg4)}); {T(n.Arg6)}) {T(n.Arg8)}";

    // Comma-separated expression list used in for-init / for-update.
    // C# accepts `for (i=0, j=10; …; i++, j--)` natively, so we just
    // splice the expressions together with `, ` between them — no
    // helper, no parens, no special lowering. The single-expression
    // form passes through unchanged so for-loops with a lone init or
    // update still emit identically to before.
    public EmitContent Visit(C.CommaExprOne n) => StripOuterParens(T(n.Arg0));
    public EmitContent Visit(C.CommaExprCons n) => $"{T(n.Arg0)}, {StripOuterParens(T(n.Arg2))}";
    public EmitContent Visit(C.StmtReturn n) => $"return {T(n.Arg1)};\n";
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
    public EmitContent Visit(C.StmtDecl n) => $"{T(n.Arg0)};\n";

    // `goto label;` — C# accepts the same keyword + identifier syntax with
    // identical forward-reference semantics inside a method body, so the
    // lowering is verbatim.
    public EmitContent Visit(C.StmtGoto n) => $"goto {T(n.Arg1)};\n";

    // `label: Stmt` — emit the label followed by the body statement.
    // Whitespace shape: label on its own line for readability.
    public EmitContent Visit(C.StmtLabel n) =>
        $"{T(n.Arg0)}:\n{T(n.Arg2)}";

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
    public EmitContent Visit(C.Decl n) => $"{T(n.Arg0)} {T(n.Arg1)}";

    // DeclItemList: comma-joined sequence of DeclItem strings. Cons emits
    // `prev, next` and One just forwards the single item — the comma
    // separator between items appears here, while the Type prefix lands
    // in Decl above.
    public EmitContent Visit(C.DeclItemListOne n)  => T(n.Arg0);
    public EmitContent Visit(C.DeclItemListCons n) => $"{T(n.Arg0)}, {T(n.Arg2)}";

    // DeclItem: a single declarator. The plain `int x;` form emits `int x = default`
    // because C# enforces definite-assignment on struct fields — relevant for
    // [StructLayout(Explicit)] unions where writing one member doesn't satisfy
    // the others. `default` is zero-initialized for all our types (0 for ints,
    // null for pointers, zero struct for value types), which matches the
    // observable behavior of well-written C (where reading uninitialized
    // locals is undefined behavior anyway).
    public EmitContent Visit(C.DeclItem n)     => $"{T(n.Arg0)} = default";
    public EmitContent Visit(C.DeclItemInit n) => $"{T(n.Arg0)} = {T(n.Arg2)}";
    // C `T arr[N]` → C# `T* arr = stackalloc T[N]`. Uses stackalloc (no heap
    // alloc, no GC pin) so arrays live in the same lifetime as locals — matches
    // C semantics for block-scoped automatic arrays. Pointer subscript `arr[i]`
    // works directly in C# unsafe contexts (it desugars to `*(arr + i)`).
    public EmitContent Visit(C.DeclArr n) =>
        $"{T(n.Arg0)}* {T(n.Arg1)} = stackalloc {T(n.Arg0)}[{StripOuterParens(T(n.Arg3))}]";

    // C `T arr[N] = {1, 2, 3}` (or `T arr[] = {…}`) → C# `T* arr = stackalloc T[]{ 1, 2, 3 }`.
    // The explicit-size form ignores the size operand because C# infers it
    // from the initializer; both shapes share the same emit. ArgList arrives
    // as a typed EmitContent.Args (read via A()) — no sentinel decoding.
    public EmitContent Visit(C.DeclArrInit n) =>
        EmitArrInit(T(n.Arg0), T(n.Arg1), A(n.Arg7));
    public EmitContent Visit(C.DeclArrInitImplicit n) =>
        EmitArrInit(T(n.Arg0), T(n.Arg1), A(n.Arg6));

    private static string EmitArrInit(string type, string name, IReadOnlyList<string> args) =>
        $"{type}* {name} = stackalloc {type}[]{{ {string.Join(", ", args)} }}";

    // `Point p = { .x = 1, .y = 2 };` — designated initializer (C99). The
    // user named the fields directly so we don't need _structFields here:
    // the MemberInitList already emits `field = value` pairs in the right
    // shape for C#'s object-initializer syntax. Order of fields can differ
    // from declaration order (C99 allows it; C# does too).
    public EmitContent Visit(C.DeclStructDesignated n)
    {
        var type = T(n.Arg0);
        var name = T(n.Arg1);
        var members = IM(n.Arg4);  // typed InitMembers list
        return $"{type} {name} = new {type} {{ {string.Join(", ", members)} }}";
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
    public EmitContent Visit(C.MemberInit n) => $"{T(n.Arg1)} = {T(n.Arg3)}";

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
        var values = A(n.Arg4);  // typed EmitContent.Args — no sentinel split

        if (!_structFields.TryGetValue(type, out var fields))
        {
            return $"{type} {name} = default /* dotcc: unknown struct '{type}' for aggregate init */";
        }

        var sb = new StringBuilder();
        sb.Append(type).Append(' ').Append(name).Append(" = new ").Append(type).Append(" { ");
        var count = Math.Min(values.Count, fields.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            sb.Append(fields[i]).Append(" = ").Append(values[i]);
        }
        sb.Append(" }");
        return sb.ToString();
    }

    // Expressions — paren-heavy to stay precedence-safe.
    public EmitContent Visit(C.Assign n) => $"({T(n.Arg0)} = {T(n.Arg2)})";
    public EmitContent Visit(C.AddAssign n) => $"({T(n.Arg0)} += {T(n.Arg2)})";
    public EmitContent Visit(C.SubAssign n) => $"({T(n.Arg0)} -= {T(n.Arg2)})";
    public EmitContent Visit(C.MulAssign n) => $"({T(n.Arg0)} *= {T(n.Arg2)})";
    public EmitContent Visit(C.DivAssign n) => $"({T(n.Arg0)} /= {T(n.Arg2)})";
    public EmitContent Visit(C.ModAssign n) => $"({T(n.Arg0)} %= {T(n.Arg2)})";
    // Logical `||` and `&&` — wrap each operand with Cond.B so the C-truthy
    // conversion works for int / double / pointer AND bool (when the
    // operand is already a comparison result like `a == NULL`). The
    // previous `!= 0` form broke when an operand was bool because
    // `bool != 0` isn't a valid C# expression.
    public EmitContent Visit(C.Lor n) =>
        $"(Cond.B({T(n.Arg0)}) || Cond.B({T(n.Arg2)}))";
    public EmitContent Visit(C.Land n) =>
        $"(Cond.B({T(n.Arg0)}) && Cond.B({T(n.Arg2)}))";
    public EmitContent Visit(C.Eq n) => $"({T(n.Arg0)} == {T(n.Arg2)})";
    public EmitContent Visit(C.Neq n) => $"({T(n.Arg0)} != {T(n.Arg2)})";
    public EmitContent Visit(C.Lt n) => $"({T(n.Arg0)} < {T(n.Arg2)})";
    public EmitContent Visit(C.Gt n) => $"({T(n.Arg0)} > {T(n.Arg2)})";
    public EmitContent Visit(C.Le n) => $"({T(n.Arg0)} <= {T(n.Arg2)})";
    public EmitContent Visit(C.Ge n) => $"({T(n.Arg0)} >= {T(n.Arg2)})";
    // Bitwise — same precedence and semantics in C# (binary `& | ^ << >>`,
    // unary `~`). The visitor just emits the C# operator verbatim.
    public EmitContent Visit(C.BOr n)  => $"({T(n.Arg0)} | {T(n.Arg2)})";
    public EmitContent Visit(C.BXor n) => $"({T(n.Arg0)} ^ {T(n.Arg2)})";
    public EmitContent Visit(C.BAnd n) => $"({T(n.Arg0)} & {T(n.Arg2)})";
    public EmitContent Visit(C.Shl n)  => $"({T(n.Arg0)} << {T(n.Arg2)})";
    public EmitContent Visit(C.Shr n)  => $"({T(n.Arg0)} >> {T(n.Arg2)})";
    public EmitContent Visit(C.BNot n) => $"(~{T(n.Arg1)})";

    // Logical NOT: lower to `(Cond.B(E) ? 0 : 1)` so the result is int,
    // matching C's `!x` yielding 0 or 1 (never a bool). Cond.B picks the
    // right truthy overload based on E's type (int/double/pointer/bool).
    public EmitContent Visit(C.LNot n) => $"(Cond.B({T(n.Arg1)}) ? 0 : 1)";

    // Ternary `c ? a : b` — Cond.B wraps the C-truthy condition. The two
    // branches need a common C# type; the user is responsible for keeping
    // them compatible (matches the C constraint that the branches share
    // arithmetic conversions).
    public EmitContent Visit(C.Ternary n) =>
        $"(Cond.B({T(n.Arg0)}) ? {T(n.Arg2)} : {T(n.Arg4)})";
    public EmitContent Visit(C.AndAssign n) => $"({T(n.Arg0)} &= {T(n.Arg2)})";
    public EmitContent Visit(C.OrAssign n)  => $"({T(n.Arg0)} |= {T(n.Arg2)})";
    public EmitContent Visit(C.XorAssign n) => $"({T(n.Arg0)} ^= {T(n.Arg2)})";
    public EmitContent Visit(C.ShlAssign n) => $"({T(n.Arg0)} <<= {T(n.Arg2)})";
    public EmitContent Visit(C.ShrAssign n) => $"({T(n.Arg0)} >>= {T(n.Arg2)})";

    public EmitContent Visit(C.Add n) => $"({T(n.Arg0)} + {T(n.Arg2)})";
    public EmitContent Visit(C.Sub n) => $"({T(n.Arg0)} - {T(n.Arg2)})";
    public EmitContent Visit(C.Mul n) => $"({T(n.Arg0)} * {T(n.Arg2)})";
    public EmitContent Visit(C.Div n) => $"({T(n.Arg0)} / {T(n.Arg2)})";
    public EmitContent Visit(C.Mod n) => $"({T(n.Arg0)} % {T(n.Arg2)})";
    public EmitContent Visit(C.Cast n) => $"(({T(n.Arg1)}){T(n.Arg3)})";
    public EmitContent Visit(C.Deref n) => $"(*{T(n.Arg1)})";
    public EmitContent Visit(C.AddrOf n) => $"(&{T(n.Arg1)})";
    public EmitContent Visit(C.Neg n) => $"(-{T(n.Arg1)})";
    // Prefix ++/-- — strip outer parens of operand to avoid CS0131 on a
    // parenthesised lvalue. `(x)++` would parse as post-inc on a parens
    // expression, but C# accepts `++x` directly.
    public EmitContent Visit(C.PreInc n) => $"(++{StripOuterParens(T(n.Arg1))})";
    public EmitContent Visit(C.PreDec n) => $"(--{StripOuterParens(T(n.Arg1))})";
    // Postfix ++/-- — same stripping; emit `x++` rather than `(x)++`.
    public EmitContent Visit(C.PostInc n) => $"({StripOuterParens(T(n.Arg0))}++)";
    public EmitContent Visit(C.PostDec n) => $"({StripOuterParens(T(n.Arg0))}--)";
    // Subscript `expr[i]` — emit as-is; C# pointer subscript matches C semantics.
    public EmitContent Visit(C.Subscript n) =>
        $"({StripOuterParens(T(n.Arg0))}[{StripOuterParens(T(n.Arg2))}])";

    // Member access — `.` on a struct value, `->` on a struct pointer.
    // C# accepts both syntaxes in unsafe context (where all our user code
    // lives), so emit verbatim.
    public EmitContent Visit(C.MemberDot n) =>
        $"({StripOuterParens(T(n.Arg0))}.{T(n.Arg2)})";
    public EmitContent Visit(C.MemberArrow n) =>
        $"({StripOuterParens(T(n.Arg0))}->{T(n.Arg2)})";

    // `sizeof(Type)` — emit C# sizeof. Valid in unsafe contexts for any
    // unmanaged type (which all our types are).
    public EmitContent Visit(C.SizeofType n) => $"sizeof({T(n.Arg2)})";

    public EmitContent Visit(C.Call n)
    {
        var callee = T(n.Arg0);
        var args = A(n.Arg2);  // strongly-typed arg list, no sentinel splitting
        // printf-family fluent lowering. C `printf("%d %s", x, s)` → C#
        // `printf(L("%d %s\0"u8)).Arg(x).Arg(s).Done()` — works around
        // `params object[]` not accepting raw pointers. The callee name
        // arrives unmapped (post-MapBuiltin-as-identity) so we match the
        // C spelling. `fprintf(stream, fmt, …)` follows the same shape
        // but with the stream as the first call arg.
        if (callee == "printf")
        {
            var sb = new StringBuilder();
            sb.Append("printf(").Append(args[0]).Append(')');
            for (int i = 1; i < args.Count; i++)
            {
                sb.Append(".Arg(").Append(args[i]).Append(')');
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
                sb.Append(".Arg(").Append(args[i]).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        return $"{callee}({string.Join(", ", args)})";
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
        var tail = A(n.Arg2);
        var combined = new List<string>(tail.Count + 1) { head };
        combined.AddRange(tail);
        return new EmitContent.Args(combined);
    }

    public EmitContent Visit(C.ArgsOne n) => new EmitContent.Args(new[] { T(n.Arg0) });

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
        if (name == "__func__")
        {
            // `_currentFunctionName` is the enclosing function being
            // reduced. If it's null we're outside any function (illegal
            // C use of __func__) — emit a sentinel so Roslyn surfaces the
            // bug as an undefined-identifier diagnostic rather than us
            // silently producing wrong code.
            var fn = _currentFunctionName
                ?? throw new CompileException("`__func__` used outside any function definition");
            return $"L(\"{fn}\\0\"u8)";
        }
        if (_enumerators.TryGetValue(name, out var enumName))
        {
            return $"{enumName}.{name}";
        }
        return MapBuiltin(name);
    }
    // Integer literal — pass-through for unsuffixed; normalize C suffixes
    // (u/U/l/L/ll/LL/ul/ull, case-insensitive, order-insensitive) to C#'s
    // equivalents (no `ll` form — both `l` and `ll` mean 64-bit `long` in
    // C#, since C# `long` is unconditionally 64-bit).
    public EmitContent Visit(C.Num n) => NormalizeIntSuffix(T(n.Arg0));

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

    public EmitContent Visit(C.Paren n) => $"({T(n.Arg1)})";

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
    private static string MapBuiltin(string name) => name;

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
