#nullable enable

using System;
using System.Text;

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
internal sealed class CSharpEmitter : C.IVisitor<string>
{
    // Separator used by ArgsCons so Call can split args back out for printf
    // specialisation. U+0001 SOH can't appear in source that survived the lexer,
    // so splitting on it is unambiguous.
    private const char ArgSep = '';

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

    public string Visit(C.FuncDef n)
    {
        var type = (string)n.Arg0.Content;
        var name = (string)n.Arg1.Content;
        var pars = (string)n.Arg3.Content;
        var body = ResolveFuncPlaceholder((string)n.Arg5.Content, name);
        if (name == "main") { MainArity = CountCommas(pars) + 1; }
        else { _exports.Add(new Export(name, type, pars)); }
        return $"static unsafe {type} {name}({pars})\n{body}";
    }

    public string Visit(C.FuncDefNoArgs n)
    {
        var type = (string)n.Arg0.Content;
        var name = (string)n.Arg1.Content;
        var body = ResolveFuncPlaceholder((string)n.Arg4.Content, name);
        if (name == "main") { MainArity = 0; }
        else { _exports.Add(new Export(name, type, "")); }
        return $"static unsafe {type} {name}()\n{body}";
    }

    // `static T name(args) { … }` — internal linkage. Body emit is identical
    // to the bare form; we just skip adding the function to the exports list
    // so library mode keeps it private to the assembly.
    public string Visit(C.FuncDefStatic n)
    {
        var type = (string)n.Arg1.Content;
        var name = (string)n.Arg2.Content;
        var pars = (string)n.Arg4.Content;
        var body = ResolveFuncPlaceholder((string)n.Arg6.Content, name);
        if (name == "main") { MainArity = CountCommas(pars) + 1; }
        return $"static unsafe {type} {name}({pars})\n{body}";
    }

    public string Visit(C.FuncDefStaticNoArgs n)
    {
        var type = (string)n.Arg1.Content;
        var name = (string)n.Arg2.Content;
        var body = ResolveFuncPlaceholder((string)n.Arg5.Content, name);
        if (name == "main") { MainArity = 0; }
        return $"static unsafe {type} {name}()\n{body}";
    }

    // `T name(void) { … }` — C's explicit "no args" form. Lowers identically
    // to `T name() { … }`; the `void` is purely a parameter-list marker, not
    // a real parameter type.
    public string Visit(C.FuncDefVoidArgs n)
    {
        var type = (string)n.Arg0.Content;
        var name = (string)n.Arg1.Content;
        var body = ResolveFuncPlaceholder((string)n.Arg5.Content, name);
        if (name == "main") { MainArity = 0; }
        else { _exports.Add(new Export(name, type, "")); }
        return $"static unsafe {type} {name}()\n{body}";
    }

    public string Visit(C.FuncDefStaticVoidArgs n)
    {
        var type = (string)n.Arg1.Content;
        var name = (string)n.Arg2.Content;
        var body = ResolveFuncPlaceholder((string)n.Arg6.Content, name);
        if (name == "main") { MainArity = 0; }
        return $"static unsafe {type} {name}()\n{body}";
    }

    // Prototypes: C# methods hoist, so we emit nothing.
    public string Visit(C.ProtoDef n) => string.Empty;
    public string Visit(C.ProtoDefNoArgs n) => string.Empty;
    public string Visit(C.ProtoDefVoidArgs n) => string.Empty;
    public string Visit(C.ProtoDefStatic n) => string.Empty;
    public string Visit(C.ProtoDefStaticNoArgs n) => string.Empty;
    public string Visit(C.ProtoDefStaticVoidArgs n) => string.Empty;

    // `struct ID { fields } ;` — emit a C# struct declaration into the side
    // channel; contribute nothing to the function-emit stream. The struct
    // is marked `unsafe` so it can legally contain pointer fields; all our
    // C structs are by definition unmanaged (no GC refs in their fields)
    // so this is sound.
    public string Visit(C.StructDef n)
    {
        var name = (string)n.Arg1.Content;
        var members = (string)n.Arg3.Content;
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
    public string Visit(C.StructFwd n) => string.Empty;

    public string Visit(C.MembersCons n) => (string)n.Arg0.Content + (string)n.Arg1.Content;
    public string Visit(C.MembersOne n)  => (string)n.Arg0.Content;
    // `Type ID ;` member — emit as public field. C convention is that all
    // struct fields are accessible to anyone with a pointer; matching that
    // requires `public` in C#. Field names also pushed onto _pendingFields
    // so the enclosing StructDef / TypedefStruct / UnionDef can index them
    // by struct name for the aggregate-init lookup later.
    public string Visit(C.StructMember n)
    {
        var fieldName = (string)n.Arg1.Content;
        _pendingFields.Add(fieldName);
        return $"public {(string)n.Arg0.Content} {fieldName};\n";
    }

    private void DrainPendingFields(string typeName)
    {
        _structFields[typeName] = new List<string>(_pendingFields);
        _pendingFields.Clear();
    }
    // `struct ID` as a type reference — emit just the ID. C# doesn't use the
    // `struct` keyword in usage position (only in declaration), and the
    // generated struct decl shares the same name.
    public string Visit(C.TypeStruct n) => (string)n.Arg1.Content;

    // `enum ID` as a type reference — lowers to plain `int` because we emit
    // enumerators as `const int` rather than a C# enum (so the bare names
    // are usable like C's int constants without explicit casts).
    public string Visit(C.TypeEnum n) => "int";

    // `union ID` as a type reference — emit just the ID. The
    // [StructLayout(LayoutKind.Explicit)] struct declaration shares the name.
    public string Visit(C.TypeUnion n) => (string)n.Arg1.Content;

    // `union Name { Type f1; Type f2; … } ;` — emit a C# struct with
    // [StructLayout(LayoutKind.Explicit)] and [FieldOffset(0)] on each
    // member, giving C's overlapping-storage semantics. Reuses the
    // MemberList parsed for struct (one `Type ID ;` per member).
    public string Visit(C.UnionDef n)
    {
        var name = (string)n.Arg1.Content;
        var members = (string)n.Arg3.Content;
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
    public string Visit(C.EnumDef n)
    {
        var enumName = (string)n.Arg1.Content;
        var items = ((string)n.Arg3.Content).Split(EnumSep);
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

    private const char EnumSep = '';
    public string Visit(C.EnumListOne n)  => (string)n.Arg0.Content;
    public string Visit(C.EnumListCons n) => $"{(string)n.Arg0.Content}{EnumSep}{(string)n.Arg2.Content}";
    public string Visit(C.EnumItem n)     => (string)n.Arg0.Content;
    public string Visit(C.EnumItemInit n) => $"{(string)n.Arg0.Content}={(string)n.Arg2.Content}";

    // `Type -> TYPE_NAME` — the rewriter-synthesised terminal carrying a
    // typedef'd name. The Content is the raw identifier string; emit it
    // verbatim since the using-alias (or struct decl) we emitted for the
    // typedef already binds that name in C#'s namespace.
    public string Visit(C.TypeName n) => (string)n.Arg0.Content;

    // `typedef Type ID ;` — register an `using unsafe Alias = Type;` line in
    // the aliases side channel. Suppressed when Alias == Type (e.g.
    // `typedef struct Foo Foo;` where Type already lowers to `Foo`) since
    // C# rejects a self-alias and the struct named Foo already exists.
    // Suppressed too when the alias was already emitted earlier in the same
    // translation unit (deduplication — real C allows redeclaration to the
    // same type, real C# rejects duplicate aliases).
    public string Visit(C.TypedefAlias n)
    {
        var type = (string)n.Arg1.Content;
        var alias = (string)n.Arg2.Content;
        if (alias != type && _aliasNames.Add(alias))
        {
            _aliases.Append("using unsafe ").Append(alias).Append(" = ").Append(type).Append(";\n");
        }
        return string.Empty;
    }

    // `typedef Ret (*Name)(args);` → `using unsafe Name = delegate*<args, Ret>;`.
    // C# function-pointer types put the return type LAST in the type arg
    // list (opposite of C's "return type first" syntax). The visitor strips
    // parameter names from the ParamList — C# function pointers are
    // by-type-only — by splitting on commas and dropping the trailing ID
    // from each "Type ID" chunk.
    public string Visit(C.TypedefFnPtr n)
    {
        var ret = (string)n.Arg1.Content;
        var name = (string)n.Arg4.Content;
        var pars = (string)n.Arg7.Content;
        var typesOnly = StripParamNames(pars);
        _aliasNames.Add(name);
        _aliases.Append("using unsafe ").Append(name).Append(" = delegate*<")
            .Append(typesOnly).Append(", ").Append(ret).Append(">;\n");
        return string.Empty;
    }

    public string Visit(C.TypedefFnPtrNoArgs n)
    {
        var ret = (string)n.Arg1.Content;
        var name = (string)n.Arg4.Content;
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
    public string Visit(C.TypedefStruct n)
    {
        var tag = (string)n.Arg2.Content;
        var members = (string)n.Arg4.Content;
        var alias = (string)n.Arg6.Content;
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

    public string Visit(C.FnsCons n) =>
        (string)n.Arg0.Content + (((string)n.Arg0.Content).Length > 0 ? "\n\n" : "") + (string)n.Arg1.Content;

    public string Visit(C.FnsOne n) => (string)n.Arg0.Content;

    // Params
    public string Visit(C.Param n) => $"{(string)n.Arg0.Content} {(string)n.Arg1.Content}";
    public string Visit(C.ParamsCons n) => $"{(string)n.Arg0.Content}, {(string)n.Arg2.Content}";
    public string Visit(C.ParamsOne n) => (string)n.Arg0.Content;
    public string Visit(C.ParamsVararg n) => $"{(string)n.Arg0.Content}, params object[] _va";

    // Types — pointer composition + tag types stay direct; everything that
    // accumulates declaration specifiers (signed/unsigned, short/long, int/
    // char/float/double/void) goes through TypeSpec → TypeSpecList →
    // ResolveTypeSpec, matching how real C compilers handle the
    // free-order specifier sequence.
    public string Visit(C.TypePtr n) => $"{(string)n.Arg0.Content}*";

    // Each TypeSpec keyword maps to a single-char marker. Multi-char-friendly
    // because `long long` shows up as `LL`. TypeSpecList concatenates the
    // markers in order; ResolveTypeSpec inspects the final string.
    public string Visit(C.TsInt n)      => "i";
    public string Visit(C.TsChar n)     => "c";
    public string Visit(C.TsFloat n)    => "f";
    public string Visit(C.TsDouble n)   => "d";
    public string Visit(C.TsVoid n)     => "v";
    public string Visit(C.TsShort n)    => "H";   // (capital H — 's' is reserved for signed)
    public string Visit(C.TsLong n)     => "L";
    public string Visit(C.TsUnsigned n) => "U";
    public string Visit(C.TsSigned n)   => "S";

    public string Visit(C.TypeSpecListOne n)  => (string)n.Arg0.Content;
    public string Visit(C.TypeSpecListCons n) => (string)n.Arg0.Content + (string)n.Arg1.Content;

    public string Visit(C.TypeFromSpec n) => ResolveTypeSpec((string)n.Arg0.Content);

    /// <summary>
    /// Resolve a declaration-specifier marker string (concatenated by
    /// TypeSpec/TypeSpecList visitors) to a C# type name. Order-insensitive:
    /// `long unsigned int` and `unsigned int long` both produce <c>"LUi"</c>
    /// which resolves to <c>"ulong"</c>. Long and long-long both map to
    /// C# <c>long</c> (64-bit unconditionally in C#) — dotcc accepts the
    /// MSVC 32-bit `long` semantic loss as a documented quirk.
    /// </summary>
    private static string ResolveTypeSpec(string markers)
    {
        var isUnsigned = markers.Contains('U');
        var isSigned   = markers.Contains('S');
        var isShort    = markers.Contains('H');
        var longCount  = 0;
        foreach (var c in markers) { if (c == 'L') { longCount++; } }

        char? baseChar = null;
        foreach (var c in "icfdv")
        {
            if (markers.Contains(c)) { baseChar = c; break; }
        }

        // Non-integer bases ignore signedness/size modifiers (semantically
        // invalid in real C, but our job here is to emit *something* —
        // Roslyn will reject any genuinely-bogus uses downstream).
        if (baseChar == 'f') { return "float"; }
        if (baseChar == 'd') { return "double"; }
        if (baseChar == 'v') { return "void"; }
        if (baseChar == 'c')
        {
            // dotcc's `char` is `byte` (unsigned). `signed char` becomes
            // sbyte; `unsigned char` stays byte.
            if (isSigned) { return "sbyte"; }
            return "byte";
        }

        // Integer family.
        if (isShort) { return isUnsigned ? "ushort" : "short"; }
        if (longCount > 0) { return isUnsigned ? "ulong" : "long"; }
        return isUnsigned ? "uint" : "int";
    }

    // Block / statements
    public string Visit(C.Block n) => "{\n" + IndentEach((string)n.Arg1.Content) + "}\n";
    public string Visit(C.BlockEmpty n) => "{ }\n";
    public string Visit(C.StmtsCons n) => (string)n.Arg0.Content + (string)n.Arg1.Content;
    public string Visit(C.StmtsOne n) => (string)n.Arg0.Content;
    // Conditions are wrapped with B(...) so int- and pointer-valued conditions
    // (`while (1)`, `if (p)`, `for (...; n; ...)`) typecheck. The B overloads
    // live in BuildShell — see Compiler.BuildShell.
    public string Visit(C.StmtIf n) => $"if (Cond.B({(string)n.Arg2.Content})) {(string)n.Arg4.Content}";
    public string Visit(C.StmtIfElse n) =>
        $"if (Cond.B({(string)n.Arg2.Content})) {(string)n.Arg4.Content}else {(string)n.Arg6.Content}";
    public string Visit(C.StmtWhile n) => $"while (Cond.B({(string)n.Arg2.Content})) {(string)n.Arg4.Content}";

    // `do Stmt while (E) ;` — body runs at least once. C# accepts the same
    // shape; only the condition needs Cond.B wrapping. Note the trailing
    // semicolon is required in both C and C#.
    public string Visit(C.StmtDoWhile n) =>
        $"do {(string)n.Arg1.Content}while (Cond.B({(string)n.Arg4.Content}));\n";

    // `for (Decl; E; E) Stmt` — emit C#'s for verbatim. C# accepts the same
    // shape; the init declaration scopes to the loop body. The init Decl
    // here is the LHS (`int i = 0` form); the StripOuterParens on the
    // incr keeps the emitter from wrapping `i++` in extra parens that C#
    // rejects in for-clause position.
    public string Visit(C.StmtForDecl n) =>
        $"for ({(string)n.Arg2.Content}; Cond.B({(string)n.Arg4.Content}); {StripOuterParens((string)n.Arg6.Content)}) {(string)n.Arg8.Content}";
    public string Visit(C.StmtForExpr n) =>
        $"for ({StripOuterParens((string)n.Arg2.Content)}; Cond.B({(string)n.Arg4.Content}); {StripOuterParens((string)n.Arg6.Content)}) {(string)n.Arg8.Content}";
    public string Visit(C.StmtReturn n) => $"return {(string)n.Arg1.Content};\n";
    public string Visit(C.StmtReturnVoid n) => "return;\n";
    public string Visit(C.StmtBreak n) => "break;\n";
    public string Visit(C.StmtContinue n) => "continue;\n";

    // switch (E) { case … default … } — emit C# switch verbatim. The
    // condition value is an int (no Cond.B wrapping; C# switch accepts
    // numeric directly). C# requires each case body end with break/return —
    // user is expected to write `break;` (real C convention). Fall-through
    // surfaces as a downstream C# error if the user omits the break.
    public string Visit(C.StmtSwitch n) =>
        $"switch ({(string)n.Arg2.Content}) {{\n{IndentEach((string)n.Arg5.Content)}}}\n";

    public string Visit(C.CasesCons n) => (string)n.Arg0.Content + (string)n.Arg1.Content;
    public string Visit(C.CasesOne n)  => (string)n.Arg0.Content;
    public string Visit(C.CaseValue n) =>
        $"case {(string)n.Arg1.Content}:\n{IndentEach((string)n.Arg3.Content)}";
    public string Visit(C.CaseDefault n) =>
        $"default:\n{IndentEach((string)n.Arg2.Content)}";
    public string Visit(C.StmtDecl n) => $"{(string)n.Arg0.Content};\n";
    public string Visit(C.StmtExpr n) =>
        // CS0201: bare parenthesized assignment isn't a statement. Peel the
        // outer parens that our binop emitters wrap on.
        $"{StripOuterParens((string)n.Arg0.Content)};\n";

    // Declarations
    // `Type DeclItemList` — covers single (`int x;`), single-with-init
    // (`int x = 5;`), and multi-declarator (`int x, y, z;`,
    // `int x = 1, y = 2;`) forms. C# accepts the same `int x, y = 5, z;`
    // syntax so the lowering is verbatim.
    public string Visit(C.Decl n) => $"{(string)n.Arg0.Content} {(string)n.Arg1.Content}";

    // DeclItemList: comma-joined sequence of DeclItem strings. Cons emits
    // `prev, next` and One just forwards the single item — the comma
    // separator between items appears here, while the Type prefix lands
    // in Decl above.
    public string Visit(C.DeclItemListOne n)  => (string)n.Arg0.Content;
    public string Visit(C.DeclItemListCons n) => $"{(string)n.Arg0.Content}, {(string)n.Arg2.Content}";

    // DeclItem: a single declarator. The plain `int x;` form emits `int x = default`
    // because C# enforces definite-assignment on struct fields — relevant for
    // [StructLayout(Explicit)] unions where writing one member doesn't satisfy
    // the others. `default` is zero-initialized for all our types (0 for ints,
    // null for pointers, zero struct for value types), which matches the
    // observable behavior of well-written C (where reading uninitialized
    // locals is undefined behavior anyway).
    public string Visit(C.DeclItem n)     => $"{(string)n.Arg0.Content} = default";
    public string Visit(C.DeclItemInit n) => $"{(string)n.Arg0.Content} = {(string)n.Arg2.Content}";
    // C `T arr[N]` → C# `T* arr = stackalloc T[N]`. Uses stackalloc (no heap
    // alloc, no GC pin) so arrays live in the same lifetime as locals — matches
    // C semantics for block-scoped automatic arrays. Pointer subscript `arr[i]`
    // works directly in C# unsafe contexts (it desugars to `*(arr + i)`).
    public string Visit(C.DeclArr n) =>
        $"{(string)n.Arg0.Content}* {(string)n.Arg1.Content} = stackalloc {(string)n.Arg0.Content}[{StripOuterParens((string)n.Arg3.Content)}]";

    // C `T arr[N] = {1, 2, 3}` (or `T arr[] = {…}`) → C# `T* arr = stackalloc T[]{ 1, 2, 3 }`.
    // The explicit-size form ignores the size operand because C# infers it
    // from the initializer; both shapes share the same emit. ArgList comes
    // through with the ArgSep sentinel (U+0001) — split and rejoin with `, `.
    public string Visit(C.DeclArrInit n) =>
        EmitArrInit((string)n.Arg0.Content, (string)n.Arg1.Content, (string)n.Arg7.Content);
    public string Visit(C.DeclArrInitImplicit n) =>
        EmitArrInit((string)n.Arg0.Content, (string)n.Arg1.Content, (string)n.Arg6.Content);

    private static string EmitArrInit(string type, string name, string argList) =>
        $"{type}* {name} = stackalloc {type}[]{{ {argList.Replace(ArgSep.ToString(), ", ")} }}";

    // `Point p = { .x = 1, .y = 2 };` — designated initializer (C99). The
    // user named the fields directly so we don't need _structFields here:
    // the MemberInitList already emits `field = value` pairs in the right
    // shape for C#'s object-initializer syntax. Order of fields can differ
    // from declaration order (C99 allows it; C# does too).
    public string Visit(C.DeclStructDesignated n)
    {
        var type = (string)n.Arg0.Content;
        var name = (string)n.Arg1.Content;
        var members = (string)n.Arg4.Content;
        return $"{type} {name} = new {type} {{ {members} }}";
    }

    public string Visit(C.MemberInitListOne n) => (string)n.Arg0.Content;
    public string Visit(C.MemberInitListCons n) => $"{(string)n.Arg0.Content}, {(string)n.Arg2.Content}";
    public string Visit(C.MemberInit n) => $"{(string)n.Arg1.Content} = {(string)n.Arg3.Content}";

    // `Point p = {1, 2};` — struct aggregate init. C# can't take positional
    // initializers on a struct, so we look up the struct's field names (from
    // _structFields, populated by StructDef / TypedefStruct / UnionDef) and
    // emit a named-initializer: `Point p = new Point { x = 1, y = 2 };`.
    // If the type isn't a known struct (e.g. user wrote it for a typedef'd
    // primitive), fall back to a zero init with a comment — Roslyn will
    // surface the real error if the user pursued it.
    public string Visit(C.DeclStructInit n)
    {
        var type = (string)n.Arg0.Content;
        var name = (string)n.Arg1.Content;
        var argList = (string)n.Arg4.Content;
        var values = string.IsNullOrEmpty(argList)
            ? Array.Empty<string>()
            : argList.Split(ArgSep);

        if (!_structFields.TryGetValue(type, out var fields))
        {
            return $"{type} {name} = default /* dotcc: unknown struct '{type}' for aggregate init */";
        }

        var sb = new StringBuilder();
        sb.Append(type).Append(' ').Append(name).Append(" = new ").Append(type).Append(" { ");
        var count = Math.Min(values.Length, fields.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) { sb.Append(", "); }
            sb.Append(fields[i]).Append(" = ").Append(values[i]);
        }
        sb.Append(" }");
        return sb.ToString();
    }

    // Expressions — paren-heavy to stay precedence-safe.
    public string Visit(C.Assign n) => $"({(string)n.Arg0.Content} = {(string)n.Arg2.Content})";
    public string Visit(C.AddAssign n) => $"({(string)n.Arg0.Content} += {(string)n.Arg2.Content})";
    public string Visit(C.SubAssign n) => $"({(string)n.Arg0.Content} -= {(string)n.Arg2.Content})";
    public string Visit(C.MulAssign n) => $"({(string)n.Arg0.Content} *= {(string)n.Arg2.Content})";
    public string Visit(C.DivAssign n) => $"({(string)n.Arg0.Content} /= {(string)n.Arg2.Content})";
    public string Visit(C.ModAssign n) => $"({(string)n.Arg0.Content} %= {(string)n.Arg2.Content})";
    public string Visit(C.Lor n) =>
        $"({(string)n.Arg0.Content} != 0 || {(string)n.Arg2.Content} != 0)";
    public string Visit(C.Land n) =>
        $"({(string)n.Arg0.Content} != 0 && {(string)n.Arg2.Content} != 0)";
    public string Visit(C.Eq n) => $"({(string)n.Arg0.Content} == {(string)n.Arg2.Content})";
    public string Visit(C.Neq n) => $"({(string)n.Arg0.Content} != {(string)n.Arg2.Content})";
    public string Visit(C.Lt n) => $"({(string)n.Arg0.Content} < {(string)n.Arg2.Content})";
    public string Visit(C.Gt n) => $"({(string)n.Arg0.Content} > {(string)n.Arg2.Content})";
    public string Visit(C.Le n) => $"({(string)n.Arg0.Content} <= {(string)n.Arg2.Content})";
    public string Visit(C.Ge n) => $"({(string)n.Arg0.Content} >= {(string)n.Arg2.Content})";
    // Bitwise — same precedence and semantics in C# (binary `& | ^ << >>`,
    // unary `~`). The visitor just emits the C# operator verbatim.
    public string Visit(C.BOr n)  => $"({(string)n.Arg0.Content} | {(string)n.Arg2.Content})";
    public string Visit(C.BXor n) => $"({(string)n.Arg0.Content} ^ {(string)n.Arg2.Content})";
    public string Visit(C.BAnd n) => $"({(string)n.Arg0.Content} & {(string)n.Arg2.Content})";
    public string Visit(C.Shl n)  => $"({(string)n.Arg0.Content} << {(string)n.Arg2.Content})";
    public string Visit(C.Shr n)  => $"({(string)n.Arg0.Content} >> {(string)n.Arg2.Content})";
    public string Visit(C.BNot n) => $"(~{(string)n.Arg1.Content})";

    // Logical NOT: lower to `(Cond.B(E) ? 0 : 1)` so the result is int,
    // matching C's `!x` yielding 0 or 1 (never a bool). Cond.B picks the
    // right truthy overload based on E's type (int/double/pointer/bool).
    public string Visit(C.LNot n) => $"(Cond.B({(string)n.Arg1.Content}) ? 0 : 1)";

    // Ternary `c ? a : b` — Cond.B wraps the C-truthy condition. The two
    // branches need a common C# type; the user is responsible for keeping
    // them compatible (matches the C constraint that the branches share
    // arithmetic conversions).
    public string Visit(C.Ternary n) =>
        $"(Cond.B({(string)n.Arg0.Content}) ? {(string)n.Arg2.Content} : {(string)n.Arg4.Content})";
    public string Visit(C.AndAssign n) => $"({(string)n.Arg0.Content} &= {(string)n.Arg2.Content})";
    public string Visit(C.OrAssign n)  => $"({(string)n.Arg0.Content} |= {(string)n.Arg2.Content})";
    public string Visit(C.XorAssign n) => $"({(string)n.Arg0.Content} ^= {(string)n.Arg2.Content})";
    public string Visit(C.ShlAssign n) => $"({(string)n.Arg0.Content} <<= {(string)n.Arg2.Content})";
    public string Visit(C.ShrAssign n) => $"({(string)n.Arg0.Content} >>= {(string)n.Arg2.Content})";

    public string Visit(C.Add n) => $"({(string)n.Arg0.Content} + {(string)n.Arg2.Content})";
    public string Visit(C.Sub n) => $"({(string)n.Arg0.Content} - {(string)n.Arg2.Content})";
    public string Visit(C.Mul n) => $"({(string)n.Arg0.Content} * {(string)n.Arg2.Content})";
    public string Visit(C.Div n) => $"({(string)n.Arg0.Content} / {(string)n.Arg2.Content})";
    public string Visit(C.Mod n) => $"({(string)n.Arg0.Content} % {(string)n.Arg2.Content})";
    public string Visit(C.Cast n) => $"(({(string)n.Arg1.Content}){(string)n.Arg3.Content})";
    public string Visit(C.Deref n) => $"(*{(string)n.Arg1.Content})";
    public string Visit(C.AddrOf n) => $"(&{(string)n.Arg1.Content})";
    public string Visit(C.Neg n) => $"(-{(string)n.Arg1.Content})";
    // Prefix ++/-- — strip outer parens of operand to avoid CS0131 on a
    // parenthesised lvalue. `(x)++` would parse as post-inc on a parens
    // expression, but C# accepts `++x` directly.
    public string Visit(C.PreInc n) => $"(++{StripOuterParens((string)n.Arg1.Content)})";
    public string Visit(C.PreDec n) => $"(--{StripOuterParens((string)n.Arg1.Content)})";
    // Postfix ++/-- — same stripping; emit `x++` rather than `(x)++`.
    public string Visit(C.PostInc n) => $"({StripOuterParens((string)n.Arg0.Content)}++)";
    public string Visit(C.PostDec n) => $"({StripOuterParens((string)n.Arg0.Content)}--)";
    // Subscript `expr[i]` — emit as-is; C# pointer subscript matches C semantics.
    public string Visit(C.Subscript n) =>
        $"({StripOuterParens((string)n.Arg0.Content)}[{StripOuterParens((string)n.Arg2.Content)}])";

    // Member access — `.` on a struct value, `->` on a struct pointer.
    // C# accepts both syntaxes in unsafe context (where all our user code
    // lives), so emit verbatim.
    public string Visit(C.MemberDot n) =>
        $"({StripOuterParens((string)n.Arg0.Content)}.{(string)n.Arg2.Content})";
    public string Visit(C.MemberArrow n) =>
        $"({StripOuterParens((string)n.Arg0.Content)}->{(string)n.Arg2.Content})";

    // `sizeof(Type)` — emit C# sizeof. Valid in unsafe contexts for any
    // unmanaged type (which all our types are).
    public string Visit(C.SizeofType n) => $"sizeof({(string)n.Arg2.Content})";

    public string Visit(C.Call n)
    {
        var callee = (string)n.Arg0.Content;
        var argsRaw = (string)n.Arg2.Content;
        if (callee == "Printf")
        {
            var args = argsRaw.Split(ArgSep);
            var sb = new StringBuilder();
            sb.Append("Printf(").Append(args[0]).Append(')');
            for (int i = 1; i < args.Length; i++)
            {
                sb.Append(".Arg(").Append(args[i]).Append(')');
            }
            sb.Append(".Done()");
            return sb.ToString();
        }
        return $"{callee}({argsRaw.Replace(ArgSep.ToString(), ", ")})";
    }

    public string Visit(C.CallNoArgs n)
    {
        var callee = (string)n.Arg0.Content;
        if (callee == "Printf") { return "Printf(L(\"\\0\"u8)).Done()"; }
        return $"{callee}()";
    }

    public string Visit(C.ArgsCons n) =>
        $"{(string)n.Arg0.Content}{ArgSep}{(string)n.Arg2.Content}";

    public string Visit(C.ArgsOne n) => (string)n.Arg0.Content;

    // Variable reference. Three emit-time rewrites: `__func__` → a
    // placeholder that Visit(FuncDef*) resolves later (once it knows the
    // enclosing function's name); enumerator → `EnumName.Member` for any
    // bare ID matching a previously-declared enumerator (so C code writing
    // `Red` lands as `Color.Red`); builtin name → BCL helper for
    // `malloc` / `free` / `printf`. Otherwise pass through.
    public string Visit(C.Var n)
    {
        var name = (string)n.Arg0.Content;
        if (name == "__func__") { return FuncPlaceholder; }
        if (_enumerators.TryGetValue(name, out var enumName))
        {
            return $"{enumName}.{name}";
        }
        return MapBuiltin(name);
    }

    /// <summary>
    /// Placeholder Visit(Var) emits for <c>__func__</c>. Each Visit(FuncDef*)
    /// post-processes its body string and replaces every occurrence with
    /// the function's actual name wrapped in the dotcc UTF-8 string-literal
    /// idiom. The token is intentionally not a valid C# expression — if
    /// substitution misses (e.g. <c>__func__</c> outside any function, which
    /// is illegal C), Roslyn rejects the result and surfaces the bug.
    /// </summary>
    private const string FuncPlaceholder = "__DOTCC_CURRENT_FUNC__";

    /// <summary>
    /// Resolve <see cref="FuncPlaceholder"/> in a function body. Each
    /// occurrence becomes <c>L("fnname\0"u8)</c> — a byte* to the pinned
    /// UTF-8 RVA — so the result is callable as a C-string anywhere a
    /// <c>byte*</c> is expected (printf, strlen, etc.).
    /// </summary>
    private static string ResolveFuncPlaceholder(string body, string fnName)
        => body.Contains(FuncPlaceholder)
            ? body.Replace(FuncPlaceholder, $"L(\"{fnName}\\0\"u8)")
            : body;
    // Integer literal — pass-through for unsuffixed; normalize C suffixes
    // (u/U/l/L/ll/LL/ul/ull, case-insensitive, order-insensitive) to C#'s
    // equivalents (no `ll` form — both `l` and `ll` mean 64-bit `long` in
    // C#, since C# `long` is unconditionally 64-bit).
    public string Visit(C.Num n) => NormalizeIntSuffix((string)n.Arg0.Content);

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
    public string Visit(C.Flt n) => (string)n.Arg0.Content;

    public string Visit(C.Str n)
    {
        var raw = (string)n.Arg0.Content;
        if (raw is null || raw.Length < 2) { return "L(\"\\0\"u8)"; }
        var body = raw[1..^1];
        return $"L(\"{EscapeForUtf8Literal(body)}\\0\"u8)";
    }

    // `'a'`, `'\n'`, `'\\'` etc. — C char literal. Our `char` is C# `byte`,
    // so we lower to `(byte)'X'` where X is the unescaped char value.
    // Pass the C escape sequence through to C#'s char literal syntax —
    // both languages accept `\n`, `\t`, `\\`, `\'`, `\"`, `\0`, `\r`.
    public string Visit(C.Chr n)
    {
        var raw = (string)n.Arg0.Content;
        if (raw is null || raw.Length < 3) { return "(byte)0"; }
        var body = raw[1..^1];
        return $"(byte)'{body}'";
    }

    public string Visit(C.Paren n) => $"({(string)n.Arg1.Content})";

    private static string MapBuiltin(string name) => name switch
    {
        "malloc" => "Malloc",
        "free" => "Free",
        "printf" => "Printf",
        _ => name,
    };

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
