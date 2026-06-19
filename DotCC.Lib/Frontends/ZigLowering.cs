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
internal sealed class ZigLowering
{
    private readonly IrBuilder _ir;
    private readonly SymbolTable _symbols;

    /// <summary>The flat global error set: each distinct <c>error.Foo</c> name → a stable
    /// non-zero code (0 is the success sentinel). Shared across the units of one build (the
    /// caller passes one dictionary) so a given error name gets one code program-wide — V1
    /// erases the error SET, so a single space suffices. See <see cref="ErrorCode"/>.</summary>
    private readonly Dictionary<string, int> _errorCodes;

    /// <summary>The return type of the function whose body is currently being lowered (null
    /// outside a body / for a void-less return). When it is a <see cref="CType.ErrorUnion"/>,
    /// <c>return</c> wraps its value as an error union (<see cref="LowerReturn"/>).</summary>
    private CType? _currentFnRet;

    /// <summary>Monotonic counter for destructure temporaries (<c>__tupN</c>): a destructure
    /// <c>const a, const b = e;</c> evaluates <c>e</c> ONCE into <c>__tupN</c>, then binds each
    /// name to its positional element. The temp lives in the enclosing (brace-less
    /// <see cref="Seq"/>) scope alongside the binders, so repeated destructures in one block need
    /// distinct temp names (Milestone G).</summary>
    private int _tupleTempCounter;

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
    /// <c>EnumName.member</c>.</summary>
    private readonly Dictionary<string, Dictionary<string, Symbol>> _enumMembers = new(System.StringComparer.Ordinal);

    /// <summary>Per container (struct) name, each method name → the mangled free-function
    /// <see cref="Symbol"/> it lowers to (<c>TypeName_method</c>). Populated in pass 1 (so a
    /// method body can forward-reference a sibling method) and consulted by
    /// <see cref="LowerMethodCall"/> to rewrite a UFCS instance call (<c>p.method(…)</c>) or a
    /// static/associated call (<c>Type.func(…)</c>) to that free function.</summary>
    private readonly Dictionary<string, Dictionary<string, Symbol>> _methods = new(System.StringComparer.Ordinal);

    /// <summary>The container (struct) whose method signature / body is currently being lowered,
    /// so a <c>@This()</c> type resolves to it (null outside a method — a <c>@This()</c> there is
    /// an error). Set around method declaration (<see cref="DeclareMethod"/>) and pass-2 body
    /// lowering, mirroring how <see cref="_currentFnRet"/> tracks the active return type.</summary>
    private string? _currentContainer;

    /// <summary>Per container name, each <c>const Self = @This();</c> alias → the container's own
    /// type. A container-scoped self alias is the ubiquitous Zig idiom for naming the receiver type
    /// inside its methods without repeating the container name (any alias name works, not just
    /// <c>Self</c>). Populated in pass 0b (so a method signature can spell its receiver as the
    /// alias); consulted by <see cref="ResolveSelfAlias"/> only while a method of that container is
    /// being lowered (<see cref="_currentContainer"/> set), so the alias is genuinely scoped — two
    /// containers may each declare <c>const Self = @This();</c> without colliding. (A non-<c>@This()</c>
    /// value const — a namespaced constant — is not lowered yet; it needs top-level globals.)</summary>
    private readonly Dictionary<string, Dictionary<string, CType>> _selfAliases = new(System.StringComparer.Ordinal);

    /// <summary>Per container name, each namespaced VALUE <c>const</c> member → its (optional type
    /// annotation + ) right-hand-side expression, stored unlowered. A container-level <c>const</c>
    /// is a comptime constant in Zig, so a <c>Type.NAME</c> use inlines the expression — lowered
    /// fresh at each use site (with the annotation as its sink), which needs no global storage. So a
    /// <c>const max = 100;</c> / <c>const default = Color.red;</c> member reads as <c>Type.max</c> /
    /// <c>Type.default</c>. (A container-level <c>var</c> — a mutable global — is still rejected; it
    /// needs real top-level global storage. A const RHS that references a sibling const by bare name
    /// is unsupported — qualify it as <c>Type.sibling</c>.)</summary>
    private readonly Dictionary<string, Dictionary<string, (Item? typeItem, Item rhs)>> _containerConsts = new(System.StringComparer.Ordinal);

    /// <summary>A tagged union (<c>union(enum)</c>) lowered to the FAITHFUL C tagged-union shape:
    /// an outer struct <c>{ U_Tag __tag; U_Payload __payload; }</c> whose <c>__payload</c> is a
    /// nested <c>[StructLayout(Explicit)]</c> union (every payload variant overlaid at offset 0,
    /// via the shared C union machinery — <c>IsUnion=true</c>). Overlapping payloads match Zig's
    /// memory model (correct size). A union with only void variants has no <c>__payload</c> (it is
    /// just a tag). Holds what construction (<see cref="BuildUnionInit"/>) and a union
    /// <c>switch</c> (<see cref="LowerUnionSwitch"/>) need.</summary>
    private sealed record ZigUnionInfo(
        CType.Enum TagType,
        string TagFieldName,
        string? PayloadTypeName,        // the nested overlapping-payload union type (null if every variant is void)
        string PayloadFieldName,
        IReadOnlyDictionary<string, CType?> Variants)   // variant name → payload type (null = void)
    {
        public string Name => TagType.Name[..^TagSuffix.Length];   // `U_Tag` → `U`
    }

    /// <summary>Registered tagged unions: the union struct name → its <see cref="ZigUnionInfo"/>.</summary>
    private readonly Dictionary<string, ZigUnionInfo> _unions = new(System.StringComparer.Ordinal);

    /// <summary>Module-import aliases (Milestone F): the bound name of a <c>const X =
    /// @import("std");</c> → the module string (<c>"std"</c>). Comptime — no runtime decl is
    /// emitted; the alias roots a dotted-path resolution (<see cref="TryResolveStdPath"/>) so
    /// <c>X.heap.page_allocator</c> / <c>X.mem.Allocator</c> resolve. Only <c>"std"</c> is
    /// modeled; any other module errors. Function-flat (no nested-scope shadowing), like the
    /// self-alias / container-const tracking.</summary>
    private readonly Dictionary<string, string> _imports = new(System.StringComparer.Ordinal);

    /// <summary>Bindings to the statically-known default allocator (Milestone F): a <c>const a =
    /// std.heap.page_allocator;</c> (or <c>c_allocator</c>) records <c>a → CHeap</c> so a later
    /// <c>a.alloc(…)</c> DEVIRTUALIZES to a direct <c>Libc.malloc</c> (no vtable). Comptime — no
    /// runtime decl; a use of <c>a</c> as a VALUE materializes <c>ZigAlloc.CHeap()</c>. Only the
    /// C-heap default is provable in V1; an opaque <c>std.mem.Allocator</c> parameter or an
    /// <c>fba.allocator()</c> result is never recorded here (→ indirect dispatch).</summary>
    private readonly Dictionary<string, AllocKind> _defaultAllocatorBindings = new(System.StringComparer.Ordinal);

    /// <summary>The provable kind of an allocator operand. V1 has exactly one provable kind — the
    /// C heap (the statically-known <c>page_allocator</c>/<c>c_allocator</c> default), which
    /// devirtualizes to direct <c>Libc.malloc</c>/<c>free</c>.</summary>
    private enum AllocKind { CHeap }

    /// <summary>The runtime <c>FixedBufferAllocator</c> type name (a <see cref="CType.Named"/>), as
    /// spelled by the spliced <c>ZigAlloc.cs</c> — the second concrete allocator.</summary>
    private const string FbaTypeName = "FixedBufferAllocator";

    /// <summary>The discriminant field name on a lowered tagged-union struct — a leading
    /// double-underscore so it can't collide with a user variant (a Zig field name).</summary>
    private const string TagFieldName = "__tag";

    /// <summary>The nested overlapping-payload union field on a lowered tagged-union struct.</summary>
    private const string PayloadFieldName = "__payload";

    /// <summary>Suffix for a synthesized tag enum's name (<c>U</c> → <c>U_Tag</c>).</summary>
    private const string TagSuffix = "_Tag";

    /// <summary>Suffix for the synthesized nested payload-union type (<c>U</c> → <c>U_Payload</c>).</summary>
    private const string PayloadSuffix = "_Payload";

    public ZigLowering(IrBuilder ir, INameLegalizer names, Dictionary<string, int>? errorCodes = null)
    {
        _ir = ir;
        _symbols = new SymbolTable(names);
        _errorCodes = errorCodes ?? new Dictionary<string, int>(System.StringComparer.Ordinal);
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

    private static string Tok(Item it) => it.Content as string
        ?? throw new IrUnsupportedException("expected a token lexeme");

    public void Lower(Item root)
    {
        // Three passes. Pass 0 registers container TYPES (struct/enum) so a signature,
        // body, or another container's field can reference one declared later (a forward /
        // self reference). Pass 1 declares every function signature in the (global) scope —
        // Zig has no prototypes, so a call may forward-reference a function defined later.
        // Pass 2 lowers each body against the now-complete type + signature environment. An
        // `extern fn` prototype is declared in pass 1 too but has no body to lower.
        var decls = Flatten(root);

        // Container methods (struct/enum/union), collected across pass 0 as mangled
        // `TypeName_method` free functions and declared in pass 1 (so a method body can
        // forward-reference a sibling). A method is container-kind-agnostic at this point — it is
        // keyed only by the container NAME (see DeclareMethod / LowerMethodCall).
        var containerMethods = new List<(string container, Item fnDef)>();

        // Pass 0a: register every container NAME first (a struct/union as a `CType.Named`
        // placeholder, an enum fully — enums are self-contained int constants), so pass 0b
        // can resolve a struct field that points to a struct/enum declared further down. An enum
        // is fully registered here (incl. its consts), so its methods are collected here too.
        foreach (var decl in decls)
        {
            switch (Unwrap(decl).Content)
            {
                case Zig.StructDecl s:      _containerTypes[Tok(s.Arg1)] = new CType.Named(Tok(s.Arg1)); break;
                case Zig.StructDeclEmpty s: _containerTypes[Tok(s.Arg1)] = new CType.Named(Tok(s.Arg1)); break;
                case Zig.EnumDecl e:        foreach (var m in RegisterEnumZig(e.Arg1, null, e.Arg5)) { containerMethods.Add((Tok(e.Arg1), m)); } break;       // const IDENT = enum { EnumFields } ;
                case Zig.EnumDeclTyped e:   foreach (var m in RegisterEnumZig(e.Arg1, e.Arg5, e.Arg8)) { containerMethods.Add((Tok(e.Arg1), m)); } break;     // const IDENT = enum ( Type ) { EnumFields } ;
                case Zig.UnionDeclEnum u:   _containerTypes[Tok(u.Arg1)] = new CType.Named(Tok(u.Arg1)); break;  // const IDENT = union(enum) { … } ;
                // A top-level comptime allocator/namespace binding (`const std = @import("std");`)
                // — recorded HERE in pass 0 so a pass-1 signature (`fn f(a: std.mem.Allocator)`)
                // resolves the import alias. Emits no decl (Milestone F). A non-comptime const
                // falls through (rejected in pass 1 as an unsupported top-level global).
                case Zig.ConstDecl d:      TryComptimeConstBinding(Tok(d.Arg1), d.Arg3); break;  // const IDENT = RhsExpr ;
                case Zig.ConstDeclTyped d: TryComptimeConstBinding(Tok(d.Arg1), d.Arg5); break;  // const IDENT : Type = RhsExpr ;
            }
        }
        // Pass 0b: build struct field layouts (field types now resolve through 0a), register each
        // struct's/union's consts, and collect their methods.
        foreach (var decl in decls)
        {
            switch (Unwrap(decl).Content)
            {
                case Zig.StructDecl s:      // const IDENT = struct { Members } ;
                {
                    var (fields, methods, consts) = SplitMembers(s.Arg5);
                    RegisterStruct(Tok(s.Arg1), fields);
                    RegisterContainerConsts(Tok(s.Arg1), consts);
                    foreach (var m in methods) { containerMethods.Add((Tok(s.Arg1), m)); }
                    break;
                }
                case Zig.StructDeclEmpty s: RegisterStruct(Tok(s.Arg1), System.Array.Empty<Item>()); break;  // const IDENT = struct { } ;
                case Zig.UnionDeclEnum u:   foreach (var m in RegisterUnion(Tok(u.Arg1), u.Arg8)) { containerMethods.Add((Tok(u.Arg1), m)); } break;  // const IDENT = union(enum) { UnionMembers } ;
            }
        }

        // Pass 1: function signatures. Free functions first (a top-level decl), then struct
        // methods (mangled `TypeName_method` free functions, recorded in `_methods` for call
        // rewriting). Declaring every signature up front lets a call forward-reference any
        // function — including a sibling method.
        var entries = new List<(Symbol sym, List<(string name, CType type)> ps, Item body, string? container)>();
        foreach (var decl in decls)
        {
            var d = Unwrap(decl);   // unwrap `pub`
            switch (d.Content)
            {
                case Zig.ExternFnProto f:       DeclareExternFn(f.Arg2, f.Arg4, f.Arg6); break;  // extern fn IDENT ( Params ) Type ;
                case Zig.ExternFnProtoNoArgs f: DeclareExternFn(f.Arg2, null, f.Arg5); break;     // extern fn IDENT ( ) Type ;
                case Zig.FnDef f:          entries.Add(AsEntry(DeclareFn(f.Arg1, f.Arg3, f.Arg5, f.Arg6), null)); break;
                case Zig.FnDefNoArgs f:    entries.Add(AsEntry(DeclareFn(f.Arg1, null, f.Arg4, f.Arg5), null)); break;
                case Zig.FnDefErr f:       entries.Add(AsEntry(DeclareFn(f.Arg1, f.Arg3, f.Arg6, f.Arg7, errUnion: true), null)); break;   // `!T` return → ErrorUnion(T)
                case Zig.FnDefNoArgsErr f: entries.Add(AsEntry(DeclareFn(f.Arg1, null, f.Arg5, f.Arg6, errUnion: true), null)); break;
                // Container decls were handled in pass 0 — skip here.
                case Zig.StructDecl or Zig.StructDeclEmpty or Zig.EnumDecl or Zig.EnumDeclTyped or Zig.UnionDeclEnum: break;
                // A top-level `const` that pass 0 recorded as a comptime allocator/namespace
                // binding emits no decl; any OTHER top-level const/var is a runtime global, which
                // the Zig front-end doesn't support yet (Milestone F).
                case Zig.ConstDecl cd      when IsComptimeBound(Tok(cd.Arg1)): break;
                case Zig.ConstDeclTyped cd when IsComptimeBound(Tok(cd.Arg1)): break;
                case Zig.ConstDecl or Zig.ConstDeclTyped or Zig.VarDecl or Zig.VarDeclTyped:
                    throw new IrUnsupportedException(
                        "zig top-level `const`/`var`: only a comptime `@import`/allocator binding, a struct/enum/union, or a function is supported (a runtime top-level global is not)");
                default: throw new IrUnsupportedException("zig top-level decl: " + (d.Content?.GetType().Name ?? "null"));
            }
        }
        foreach (var (container, fnDef) in containerMethods) { entries.Add(DeclareMethod(container, fnDef)); }

        // Pass 2: bodies. `_currentContainer` is set for a method body so its `@This()` resolves.
        foreach (var (sym, ps, body, container) in entries)
        {
            _currentContainer = container;
            LowerFnBody(sym, ps, body);
            _currentContainer = null;
        }
    }

    /// <summary>Tag a pass-1 function entry with the container it belongs to (null for a free
    /// function), so pass 2 can set <see cref="_currentContainer"/> while lowering its body.</summary>
    private static (Symbol sym, List<(string name, CType type)> ps, Item body, string? container) AsEntry(
        (Symbol sym, List<(string name, CType type)> ps, Item body) e, string? container)
        => (e.sym, e.ps, e.body, container);

    /// <summary>Unwrap a top-level decl's optional <c>pub</c> wrapper (<see cref="Zig.PubFn"/>)
    /// to its inner declaration; a non-<c>pub</c> decl is returned unchanged. (D1 container
    /// decls are not <c>pub</c>-wrappable yet — single-file programs don't need export
    /// visibility; deferred to the methods milestone.)</summary>
    private static Item Unwrap(Item decl) => decl.Content is Zig.PubFn p ? p.Arg1 : decl;

    // ---- top level -------------------------------------------------------

    /// <summary>Pass 1: declare a function's signature (return + parameter types) in
    /// the global scope and bundle its body for pass 2. Declaring all signatures up
    /// front is what lets a call forward-reference a function defined later.</summary>
    private (Symbol sym, List<(string name, CType type)> ps, Item body) DeclareFn(
        Item nameTok, Item? paramsItem, Item retType, Item body, bool errUnion = false, string? mangledName = null)
    {
        var ret = LowerType(retType);
        // A `!T` return (Zig's inferred error set) wraps the payload in an error union;
        // V1 erases the set, so the leading `!` just marks the union (see CType.ErrorUnion).
        if (errUnion) { ret = new CType.ErrorUnion(ret); }
        var paramInfos = CollectParamInfos(paramsItem, out var variadic);
        // Zig allows `...` ONLY in an extern prototype — a non-extern variadic fn is
        // a compile error. Reject it the same way (faithful to Zig; our subset has no
        // way to access varargs from a Zig body anyway).
        if (variadic)
        {
            throw new IrUnsupportedException(
                $"function '{Tok(nameTok)}': a non-extern Zig function cannot be variadic (use `extern fn`)");
        }

        var funcSym = _symbols.Declare(new Symbol
        {
            // A method is lowered to a free function under its mangled `TypeName_method` name
            // (so it can be `&fn`-addressed and called directly); a plain function keeps its name.
            Name = mangledName ?? Tok(nameTok),
            Kind = SymKind.Func,
            Type = new CType.Func(ret, paramInfos.Select(p => p.type).ToList(), false),
            IsGlobal = true,
        });
        return (funcSym, paramInfos, body);
    }

    /// <summary>Pass 1 for a struct method: declare it as a free function named
    /// <c>TypeName_method</c> (its receiver, if any, is the ordinary first parameter, so the
    /// body lowers exactly like a free function — <c>self.x</c> is plain field access) and record
    /// it in <see cref="_methods"/> for call rewriting. <see cref="_currentContainer"/> is set so
    /// a <c>@This()</c> in a parameter type resolves to the container.</summary>
    private (Symbol sym, List<(string name, CType type)> ps, Item body, string? container) DeclareMethod(
        string container, Item fnDef)
    {
        Item nameTok; Item? paramsItem; Item retType; Item body; bool errUnion;
        switch (fnDef.Content)
        {
            case Zig.FnDef f:          nameTok = f.Arg1; paramsItem = f.Arg3; retType = f.Arg5; body = f.Arg6; errUnion = false; break;
            case Zig.FnDefNoArgs f:    nameTok = f.Arg1; paramsItem = null;   retType = f.Arg4; body = f.Arg5; errUnion = false; break;
            case Zig.FnDefErr f:       nameTok = f.Arg1; paramsItem = f.Arg3; retType = f.Arg6; body = f.Arg7; errUnion = true;  break;
            case Zig.FnDefNoArgsErr f: nameTok = f.Arg1; paramsItem = null;   retType = f.Arg5; body = f.Arg6; errUnion = true;  break;
            default: throw new IrUnsupportedException("zig method: " + (fnDef.Content?.GetType().Name ?? "null"));
        }
        var methodName = Tok(nameTok);

        _currentContainer = container;
        var e = DeclareFn(nameTok, paramsItem, retType, body, errUnion: errUnion, mangledName: container + "_" + methodName);
        _currentContainer = null;

        if (!_methods.TryGetValue(container, out var methods))
        {
            methods = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
            _methods[container] = methods;
        }
        if (!methods.TryAdd(methodName, e.sym))
        {
            throw new IrUnsupportedException($"struct '{container}' declares '{methodName}' more than once");
        }
        return AsEntry(e, container);
    }

    /// <summary>Collect a parameter list's <c>(name, type)</c> infos in source order,
    /// detecting the variadic marker <c>...</c> (Zig's <c>DOT3</c> ParamDecl). The
    /// marker carries no name/type, so it is excluded from the infos and instead sets
    /// <paramref name="variadic"/>; it must be the LAST parameter (C / Zig both require
    /// the fixed params to precede the pack).</summary>
    private List<(string name, CType type)> CollectParamInfos(Item? paramsItem, out bool variadic)
    {
        variadic = false;
        var infos = new List<(string name, CType type)>();
        if (paramsItem is null) { return infos; }

        var ps = Flatten(paramsItem);
        for (var i = 0; i < ps.Count; i++)
        {
            switch (ps[i].Content)
            {
                case Zig.ParamVariadic:
                    if (i != ps.Count - 1)
                    {
                        throw new IrUnsupportedException("zig `...` must be the final parameter");
                    }
                    variadic = true;
                    break;
                case Zig.Param pm:
                    infos.Add((Tok(pm.Arg0), LowerType(pm.Arg2)));
                    break;
                default:
                    throw new IrUnsupportedException("zig param: " + (ps[i].Content?.GetType().Name ?? "null"));
            }
        }
        return infos;
    }

    /// <summary>Declare an <c>extern fn</c> prototype: a function symbol with no body
    /// (so no <see cref="FuncDef"/>). <c>FromSystemHeader = true</c> marks it as
    /// externally provided (libc, linked with <c>-lc</c>) — exactly the marker the C
    /// frontend puts on a libc prototype — so <see cref="LowerCall"/> renders the call
    /// by its bare name (no <c>CalleeSym</c>), routing it to dotcc's <c>Libc</c> runtime
    /// the same way a C program's libc call does. A trailing <c>...</c> (the
    /// <c>fn(fixed, ...)</c> form, e.g. printf) sets the function type's
    /// <c>Variadic</c> flag: the fixed params still coerce at the call, while the
    /// trailing args take C's default argument promotions.</summary>
    private void DeclareExternFn(Item nameTok, Item? paramsItem, Item retType)
    {
        var ret = LowerType(retType);
        var paramInfos = CollectParamInfos(paramsItem, out var variadic);
        _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok),
            Kind = SymKind.Func,
            Type = new CType.Func(ret, paramInfos.Select(p => p.type).ToList(), variadic),
            IsGlobal = true,
            FromSystemHeader = true,
        });
    }

    /// <summary>Pass 2: lower a function body. Params share the function's top scope
    /// (a top-block redecl of a param name is an error in C; Zig likewise), so they
    /// are declared inside the function scope before the body.</summary>
    private void LowerFnBody(Symbol funcSym, List<(string name, CType type)> paramInfos, Item body)
    {
        _currentFnRet = (funcSym.Type as CType.Func)?.Return;
        _symbols.BeginFunction();
        _symbols.EnterScope();
        var paramSyms = paramInfos
            .Select(p => _symbols.Declare(new Symbol { Name = p.name, Kind = SymKind.Param, Type = p.type }))
            .ToList();
        var blk = LowerBlock(body);
        _symbols.ExitScope();

        _ir.Functions.Add(new FuncDef(funcSym, paramSyms, blk, false));
    }

    // ---- containers (structs / enums) ------------------------------------

    /// <summary>Register a Zig <c>struct</c> declaration: build its field layout (each
    /// <c>name: Type</c> field's type resolved through <see cref="LowerType"/>, so it can
    /// reference any container registered in pass 0) and hand it to the shared IR aggregate
    /// table via <see cref="IrBuilder.RegisterStructType"/>. <paramref name="fieldItems"/> are
    /// the body's field members (each a <see cref="Zig.StructField"/>), already split out from
    /// any methods by <see cref="SplitMembers"/>; empty for a <c>struct {}</c>.</summary>
    private void RegisterStruct(string name, IReadOnlyList<Item> fieldItems)
    {
        var fields = new List<StructField>();
        foreach (var fd in fieldItems)
        {
            var f = (Zig.StructField)fd.Content!;   // FieldDecl -> IDENT ':' Type
            fields.Add(new StructField(Tok(f.Arg0), LowerType(f.Arg2)));
        }
        _ir.RegisterStructType(name, fields, isUnion: false);
    }

    /// <summary>Split a struct container body (<c>FieldDecls</c> = a list of <c>Member</c>) into
    /// its field declarations (each a <see cref="Zig.StructField"/>, for the layout), its methods
    /// (the inner <c>FnDef</c> item of each <c>fn</c>/<c>pub fn</c> member, declared as mangled free
    /// functions in pass 1), and its <c>const</c> members (each a <c>VarDecl</c> item, processed by
    /// <see cref="RegisterContainerConsts"/>). A method is always lowered to an internal free
    /// function, so <c>pub</c> carries no extra meaning yet (export visibility for a single-file
    /// program is a no-op) and is dropped here.</summary>
    private static (List<Item> fields, List<Item> methods, List<Item> consts) SplitMembers(Item membersItem)
    {
        var fields = new List<Item>();
        var methods = new List<Item>();
        var consts = new List<Item>();
        foreach (var m in Flatten(membersItem))
        {
            switch (m.Content)
            {
                case Zig.MemberField mf:     fields.Add(mf.Arg0); break;   // FieldDecl ','  → StructField
                case Zig.MemberFieldLast mf: fields.Add(mf.Arg0); break;   // FieldDecl       → StructField
                case Zig.MemberMethod mm:    methods.Add(mm.Arg0); break;  // FnDef
                case Zig.MemberPubMethod mm: methods.Add(mm.Arg1); break;  // 'pub' FnDef
                case Zig.MemberConst mc:     consts.Add(mc.Arg0); break;   // VarDecl
                case Zig.MemberPubConst mc:  consts.Add(mc.Arg1); break;   // 'pub' VarDecl
                default: throw new IrUnsupportedException("zig container member: " + (m.Content?.GetType().Name ?? "null"));
            }
        }
        return (fields, methods, consts);
    }

    /// <summary>Split an enum body (<c>EnumFields</c> = a list of <c>EnumMember</c>) into its value
    /// fields (each a <see cref="Zig.EnumField"/> / <see cref="Zig.EnumFieldInit"/>), its methods
    /// (the inner <c>FnDef</c> of each <c>fn</c>/<c>pub fn</c> member), and its <c>const</c> members
    /// (each a <c>VarDecl</c>) — the enum analogue of <see cref="SplitMembers"/>.</summary>
    private static (List<Item> fields, List<Item> methods, List<Item> consts) SplitEnumMembers(Item membersItem)
    {
        var fields = new List<Item>();
        var methods = new List<Item>();
        var consts = new List<Item>();
        foreach (var m in Flatten(membersItem))
        {
            switch (m.Content)
            {
                case Zig.EnumMemberField mf:     fields.Add(mf.Arg0); break;   // EnumField ','
                case Zig.EnumMemberFieldLast mf: fields.Add(mf.Arg0); break;   // EnumField
                case Zig.EnumMemberMethod mm:    methods.Add(mm.Arg0); break;  // FnDef
                case Zig.EnumMemberPubMethod mm: methods.Add(mm.Arg1); break;  // 'pub' FnDef
                case Zig.EnumMemberConst mc:     consts.Add(mc.Arg0); break;   // VarDecl
                case Zig.EnumMemberPubConst mc:  consts.Add(mc.Arg1); break;   // 'pub' VarDecl
                default: throw new IrUnsupportedException("zig enum member: " + (m.Content?.GetType().Name ?? "null"));
            }
        }
        return (fields, methods, consts);
    }

    /// <summary>Split a union body (<c>UnionVariants</c> = a list of <c>UnionMember</c>) into its
    /// variants (each a <see cref="Zig.UnionVariantPayload"/> / <see cref="Zig.UnionVariantVoid"/>),
    /// its methods, and its <c>const</c> members — the union analogue of
    /// <see cref="SplitMembers"/>.</summary>
    private static (List<Item> variants, List<Item> methods, List<Item> consts) SplitUnionMembers(Item membersItem)
    {
        var variants = new List<Item>();
        var methods = new List<Item>();
        var consts = new List<Item>();
        foreach (var m in Flatten(membersItem))
        {
            switch (m.Content)
            {
                case Zig.UnionMemberVariant mv:     variants.Add(mv.Arg0); break;   // UnionVariant ','
                case Zig.UnionMemberVariantLast mv: variants.Add(mv.Arg0); break;   // UnionVariant
                case Zig.UnionMemberMethod mm:      methods.Add(mm.Arg0); break;    // FnDef
                case Zig.UnionMemberPubMethod mm:   methods.Add(mm.Arg1); break;    // 'pub' FnDef
                case Zig.UnionMemberConst mc:       consts.Add(mc.Arg0); break;     // VarDecl
                case Zig.UnionMemberPubConst mc:    consts.Add(mc.Arg1); break;     // 'pub' VarDecl
                default: throw new IrUnsupportedException("zig union member: " + (m.Content?.GetType().Name ?? "null"));
            }
        }
        return (variants, methods, consts);
    }

    /// <summary>Register a container's <c>const</c> members. Two forms: the self-type alias
    /// <c>const Self = @This();</c> (any alias name; RHS <c>@This()</c>) → records <c>alias → the
    /// container's own type</c> in <see cref="_selfAliases"/> (so a method can spell its receiver /
    /// return / local type as the alias); and a namespaced VALUE const <c>const NAME = expr;</c> (or
    /// <c>const NAME: T = expr;</c>) → records the RHS in <see cref="_containerConsts"/> for inlining
    /// at each <c>Type.NAME</c> use site (container-level consts are comptime in Zig). Runs in pass
    /// 0b (after <see cref="_containerTypes"/> is populated, before signatures) so a method signature
    /// can use a self alias. A container-level <c>var</c> (a mutable global) is rejected — it needs
    /// real top-level global storage, which the Zig front-end doesn't lower yet.</summary>
    private void RegisterContainerConsts(string container, IReadOnlyList<Item> constItems)
    {
        foreach (var c in constItems)
        {
            Item nameTok; Item? typeItem; Item rhs; bool isVar;
            switch (c.Content)
            {
                case Zig.ConstDecl d:      nameTok = d.Arg1; typeItem = null;   rhs = d.Arg3; isVar = false; break;  // const IDENT = RhsExpr ;
                case Zig.ConstDeclTyped d: nameTok = d.Arg1; typeItem = d.Arg3; rhs = d.Arg5; isVar = false; break;  // const IDENT : Type = RhsExpr ;
                case Zig.VarDecl d:        nameTok = d.Arg1; typeItem = null;   rhs = d.Arg3; isVar = true;  break;
                case Zig.VarDeclTyped d:   nameTok = d.Arg1; typeItem = d.Arg3; rhs = d.Arg5; isVar = true;  break;
                default: throw new IrUnsupportedException("zig container const: " + (c.Content?.GetType().Name ?? "null"));
            }
            var cname = Tok(nameTok);

            // A container-level `var` is a namespaced mutable GLOBAL — it needs real top-level global
            // storage, which the Zig front-end doesn't lower yet. Reject loudly.
            if (isVar)
            {
                throw new IrUnsupportedException(
                    $"container '{container}' member `var {cname}` (a namespaced mutable global) is not supported yet — "
                    + "use a `const` (a comptime value)");
            }

            // `const Alias = @This();` — the self-type alias. `@This()` is a no-arg builtin; the
            // transparent expression productions collapse, so the RHS content is it directly.
            if (typeItem is null && rhs.Content is Zig.BuiltinCallNoArgs b && Tok(b.Arg0) == "@This")
            {
                if (!_selfAliases.TryGetValue(container, out var aliases))
                {
                    aliases = new Dictionary<string, CType>(System.StringComparer.Ordinal);
                    _selfAliases[container] = aliases;
                }
                aliases[cname] = _containerTypes[container];
                continue;
            }

            // A namespaced VALUE const — store its (annotation + ) RHS to inline at each `Type.NAME`
            // use (see the `Zig.Field` case in LowerExpr).
            if (!_containerConsts.TryGetValue(container, out var consts))
            {
                consts = new Dictionary<string, (Item?, Item)>(System.StringComparer.Ordinal);
                _containerConsts[container] = consts;
            }
            if (!consts.TryAdd(cname, (typeItem, rhs)))
            {
                throw new IrUnsupportedException($"container '{container}' declares const '{cname}' more than once");
            }
        }
    }

    /// <summary>Register a Zig <c>union(enum)</c> declaration as the faithful C tagged-union shape
    /// (see <see cref="ZigUnionInfo"/>): synthesize the tag enum <c>U_Tag</c> (a member per variant,
    /// value = its index) + per-member symbols (so <c>.variant</c> resolves to a tag constant); a
    /// nested overlapping-payload union <c>U_Payload</c> (<c>IsUnion=true</c> → every payload
    /// variant at <c>[FieldOffset(0)]</c>, reusing the shared C union machinery); and the outer
    /// struct <c>U</c> = <c>{ __tag, __payload }</c>. A union with only void variants gets no
    /// <c>__payload</c>. Records the <see cref="ZigUnionInfo"/> for construction + <c>switch</c>.
    /// Variant payload types resolve through pass 0a, so a variant may name any container. Returns
    /// the body's method items (each a <c>FnDef</c>) for declaration in pass 1, and registers its
    /// consts (e.g. <c>const Self = @This();</c> → the outer struct type).</summary>
    private List<Item> RegisterUnion(string name, Item variantsItem)
    {
        var (variantItems, methods, consts) = SplitUnionMembers(variantsItem);
        var variants = new List<(string name, CType? payload)>();
        foreach (var v in variantItems)
        {
            switch (v.Content)
            {
                case Zig.UnionVariantPayload vp: variants.Add((Tok(vp.Arg0), LowerType(vp.Arg2))); break;  // IDENT ':' Type
                case Zig.UnionVariantVoid vv:    variants.Add((Tok(vv.Arg0), null)); break;                 // IDENT
                default: throw new IrUnsupportedException("zig union variant: " + (v.Content?.GetType().Name ?? "null"));
            }
        }

        // Synthesize the tag enum `U_Tag` + its member symbols (variant name → tag constant).
        var tagName = name + TagSuffix;
        var tagType = new CType.Enum(tagName, CType.Int);
        var tagMembers = new List<EnumMember>();
        var tagSyms = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
        var variantMap = new Dictionary<string, CType?>(System.StringComparer.Ordinal);
        long idx = 0;
        foreach (var (vname, payload) in variants)
        {
            tagMembers.Add(new EnumMember(vname, idx));
            tagSyms[vname] = new Symbol { Name = vname, Kind = SymKind.EnumConst, Type = tagType, ConstValue = idx, IsGlobal = true };
            variantMap[vname] = payload;
            idx++;
        }
        _ir.RegisterEnumType(tagName, CType.Int, tagMembers);
        _containerTypes[tagName] = tagType;
        _enumMembers[tagName] = tagSyms;

        // Nested overlapping-payload union `U_Payload` (one overlaid field per PAYLOAD variant) —
        // only when at least one variant carries a payload; an all-void union is just a tag.
        string? payloadTypeName = null;
        var payloadFields = new List<StructField>();
        foreach (var (vname, payload) in variants)
        {
            if (payload is not null) { payloadFields.Add(new StructField(vname, payload)); }
        }
        if (payloadFields.Count > 0)
        {
            payloadTypeName = name + PayloadSuffix;
            _ir.RegisterStructType(payloadTypeName, payloadFields, isUnion: true);   // [StructLayout(Explicit)], all at offset 0
        }

        // Outer discriminated struct `U` = { __tag; (__payload if any) }.
        var fields = new List<StructField> { new StructField(TagFieldName, tagType) };
        if (payloadTypeName is not null) { fields.Add(new StructField(PayloadFieldName, new CType.Named(payloadTypeName))); }
        _ir.RegisterStructType(name, fields, isUnion: false);

        _unions[name] = new ZigUnionInfo(tagType, TagFieldName, payloadTypeName, PayloadFieldName, variantMap);
        RegisterContainerConsts(name, consts);   // e.g. `const Self = @This();` → the outer struct type
        return methods;
    }

    /// <summary>Register a Zig <c>enum</c> declaration: assign each member its value
    /// (explicit <c>= value</c> via <see cref="ZigConstEval"/>, else auto-incremented from
    /// the previous, starting at 0), build the shared <see cref="EnumTypeDef"/> via
    /// <see cref="IrBuilder.RegisterEnumType"/>, and record a per-member
    /// <see cref="SymKind.EnumConst"/> symbol (so <c>Color.red</c> / a sink-typed
    /// <c>.red</c> resolve to an <see cref="EnumConstRef"/>). The underlying type is the
    /// <c>enum(T)</c> base, else C's default <see cref="CType.Int"/>. Returns the body's method
    /// items (each a <c>FnDef</c>) for declaration in pass 1, and registers its consts (e.g.
    /// <c>const Self = @This();</c> → the enum type).</summary>
    private List<Item> RegisterEnumZig(Item nameTok, Item? underlyingType, Item membersItem)
    {
        var name = Tok(nameTok);
        var (fieldItems, methods, consts) = SplitEnumMembers(membersItem);
        var underlying = underlyingType is not null ? LowerType(underlyingType) : CType.Int;
        var enumType = new CType.Enum(name, underlying);
        var members = new List<EnumMember>();
        var memberSyms = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
        long next = 0;
        foreach (var emItem in fieldItems)
        {
            string mName;
            Item? valExpr;
            switch (emItem.Content)
            {
                case Zig.EnumField ef:     mName = Tok(ef.Arg0); valExpr = null; break;       // IDENT
                case Zig.EnumFieldInit ef: mName = Tok(ef.Arg0); valExpr = ef.Arg2; break;    // IDENT '=' Expr
                default: throw new IrUnsupportedException("zig enum member: " + (emItem.Content?.GetType().Name ?? "null"));
            }
            if (valExpr is not null)
            {
                next = ZigConstEval(LowerExpr(valExpr))
                    ?? throw new IrUnsupportedException($"enum '{name}' member '{mName}': value must be a constant integer expression");
            }
            members.Add(new EnumMember(mName, next));
            memberSyms[mName] = new Symbol
            {
                Name = mName, Kind = SymKind.EnumConst, Type = enumType, ConstValue = next, IsGlobal = true,
            };
            next++;
        }
        _ir.RegisterEnumType(name, underlying, members);
        _containerTypes[name] = enumType;
        _enumMembers[name] = memberSyms;
        RegisterContainerConsts(name, consts);   // e.g. `const Self = @This();` → the enum type
        return methods;
    }

    /// <summary>Const-fold a lowered enum-member initializer to its integer value, or null
    /// if not a constant the D1 subset evaluates (an int literal, a parenthesized such, or a
    /// unary <c>-</c>/<c>~</c> of one). Wider constant expressions are deferred.</summary>
    private static long? ZigConstEval(CExpr e) => e switch
    {
        LitInt i => i.Value,
        Paren p => ZigConstEval(p.Inner),
        Unary u => u.Op switch
        {
            UnOp.Neg => ZigConstEval(u.Operand) is { } v ? -v : null,
            UnOp.BitNot => ZigConstEval(u.Operand) is { } v ? ~v : null,
            _ => null,
        },
        _ => null,
    };

    /// <summary>Resolve a bare enum literal <c>.member</c> against the enum type its sink
    /// names — an <see cref="EnumConstRef"/> the shared backend renders as
    /// <c>EnumName.member</c>.</summary>
    private CExpr ResolveEnumLit(string member, CType.Enum en)
    {
        if (_enumMembers.TryGetValue(en.Name, out var syms) && syms.TryGetValue(member, out var sym))
        {
            return new EnumConstRef(sym) { Type = en };
        }
        throw new IrUnsupportedException($"enum '{en.Name}' has no member '{member}'");
    }

    /// <summary>Lower an anonymous struct literal <c>.{ .f = v, … }</c> against the struct
    /// type its sink names (Zig's result-location inference). Each <c>.field = value</c>
    /// pairs the field with its declared type (looked up via
    /// <see cref="IrBuilder.StructFieldType"/>) so the value coerces as C would at the
    /// store; the value is itself lowered at that field type as its sink (so a nested
    /// <c>.{…}</c> or <c>.member</c> resolves). An omitted field takes C#'s zero default —
    /// matching C's partial-init / Zig's required-field rule isn't enforced in D1. An empty
    /// <c>.{}</c> zero-inits every field.</summary>
    private CExpr LowerStructInit(Item initItem, CType? sink)
    {
        // The empty `.{}` (AnonStructInitEmpty) carries no field list — zero-init every field.
        IReadOnlyList<Item> fields = initItem.Content is Zig.AnonStructInit a ? Flatten(a.Arg2) : [];   // Primary -> '.' '{' FieldInits '}'
        // A `.{…}` is a TUPLE when its elements are positional (`.{a, b}`) and a STRUCT/UNION when
        // they are named (`.{.f = v}`). Zig never mixes the two in one literal — reject that early.
        bool anyPositional = false, anyNamed = false;
        foreach (var f in fields)
        {
            if (f.Content is Zig.FieldInitPositional) { anyPositional = true; } else { anyNamed = true; }
        }
        if (anyPositional && anyNamed)
        {
            throw new IrUnsupportedException(
                "zig `.{…}` mixes positional and named fields — a tuple literal is all-positional, a struct literal all-named");
        }
        // Tuple literal: a positional list, or a (positional/empty) `.{…}` whose sink IS a tuple.
        // Element types come from the sink, or are inferred from the elements when there's none.
        if (sink?.Unqualified is CType.Tuple tupleSink)
        {
            if (anyNamed)
            {
                throw new IrUnsupportedException(
                    "zig tuple `.{…}` at a tuple type must use positional elements, not `.field = …`");
            }
            return BuildTupleInit(fields, tupleSink);
        }
        if (anyPositional)
        {
            return BuildTupleInit(fields, null);   // inferred tuple (no sink, e.g. `const t = .{a, b};`)
        }
        // Named struct / union — needs a known struct result type.
        if (sink?.Unqualified is not CType.Named named)
        {
            throw new IrUnsupportedException(
                "zig anonymous struct literal `.{…}` needs a known struct result type (a typed const/var, a return, or a field)");
        }
        // A tagged-union sink → a union literal (sets the tag + exactly one payload variant).
        if (_unions.TryGetValue(named.Name, out var uinfo)) { return BuildUnionInit(fields, uinfo); }
        return BuildStructInit(fields, named);
    }

    /// <summary>Build a tuple literal <c>.{ a, b, … }</c> (Milestone G) → <see cref="TupleNew"/>.
    /// With a <paramref name="sink"/> tuple type, each element lowers at its declared element type
    /// as its sink (so a nested <c>.{…}</c>/<c>.member</c> resolves) and the count must match; with
    /// no sink the element types are inferred from the elements themselves
    /// (<c>const t = .{a, b};</c>). Arity 1..7 (empty and &gt; 7 deferred — see
    /// <see cref="LowerTupleType"/>).</summary>
    private CExpr BuildTupleInit(IReadOnlyList<Item> posItems, CType.Tuple? sink)
    {
        if (sink is not null && posItems.Count != sink.Elements.Count)
        {
            throw new IrUnsupportedException(
                $"zig tuple literal has {posItems.Count} element(s) but the target tuple has {sink.Elements.Count}");
        }
        if (posItems.Count is 0 or > 7)
        {
            throw new IrUnsupportedException(
                $"zig tuple literal arity {posItems.Count} is not supported (1..7; an empty tuple and arity > 7 are deferred)");
        }
        var elems = new List<CExpr>();
        var types = new List<CType>();
        for (int i = 0; i < posItems.Count; i++)
        {
            var pos = (Zig.FieldInitPositional)posItems[i].Content!;   // FieldInit -> Expr
            if (sink is not null)
            {
                var et = sink.Elements[i];
                elems.Add(LowerExprSink(pos.Arg0, et));
                types.Add(et);
            }
            else
            {
                var e = LowerExpr(pos.Arg0);
                elems.Add(e);
                types.Add(e.Type);
            }
        }
        var tt = sink ?? new CType.Tuple(types);
        return new TupleNew(elems, tt) { Type = tt };
    }

    /// <summary>Lower a TYPED struct literal `Type{ .field = … }` — Zig's CurlySuffixExpr
    /// (`CurlySuffix -> Type '{' FieldInits '}'`). Unlike the anonymous `.{…}` form, the struct
    /// type is named explicitly by the leading <c>Type</c>, so this needs NO sink and is valid in
    /// any expression position (e.g. <c>&amp;Point{…}</c>, a bare subexpression). The leading type
    /// must resolve to a registered struct.</summary>
    private CExpr LowerTypedStructInit(Item typeItem, IReadOnlyList<Item> fieldInitItems)
    {
        var t = LowerType(typeItem);
        if (t.Unqualified is not CType.Named named)
        {
            throw new IrUnsupportedException(
                $"zig typed struct literal `Type{{…}}` requires a struct type, got {t.Describe()}");
        }
        // A typed tagged-union literal `U{ .variant = … }` sets the tag + the one payload variant.
        if (_unions.TryGetValue(named.Name, out var uinfo)) { return BuildUnionInit(fieldInitItems, uinfo); }
        return BuildStructInit(fieldInitItems, named);
    }

    /// <summary>Shared back half of both struct-literal forms (anonymous `.{…}` and typed
    /// `Type{…}`): turn the `.field = expr` items into <see cref="FieldInit"/>s against a known
    /// struct type. Each field's declared type (via <see cref="IrBuilder.StructFieldType"/>) is
    /// the value's sink, so a nested `.{…}`/`.member` resolves; an unknown field errors
    /// precisely. An omitted field takes C#'s zero default (D1 doesn't enforce Zig's
    /// required-field rule).</summary>
    private CExpr BuildStructInit(IReadOnlyList<Item> fieldInitItems, CType.Named named)
    {
        var members = new List<FieldInit>();
        foreach (var fiItem in fieldInitItems)
        {
            var fi = (Zig.FieldInit)fiItem.Content!;   // FieldInit -> '.' IDENT '=' Expr
            var fname = Tok(fi.Arg1);
            var ftype = _ir.StructFieldType(named, fname)
                ?? throw new IrUnsupportedException($"struct '{named.Name}' has no field '{fname}'");
            members.Add(new FieldInit(fname, ftype, LowerExprSink(fi.Arg3, ftype)));
        }
        return new StructInit(members) { Type = named };
    }

    /// <summary>Build a tagged-union PAYLOAD literal — <c>.{ .variant = value }</c> or
    /// <c>U{ .variant = value }</c> — as a <see cref="StructInit"/> that sets BOTH the
    /// <see cref="TagFieldName"/> discriminant (to the variant's tag constant) and the variant's
    /// payload field (the value lowered at the payload type as its sink). Exactly one variant must
    /// be set; a void variant is constructed with the bare <c>.variant</c> form
    /// (<see cref="BuildVoidVariant"/>), not this one.</summary>
    private CExpr BuildUnionInit(IReadOnlyList<Item> fieldInitItems, ZigUnionInfo info)
    {
        if (fieldInitItems.Count != 1)
        {
            throw new IrUnsupportedException(
                $"zig tagged-union literal for '{info.Name}' must set exactly one variant (got {fieldInitItems.Count})");
        }
        var fi = (Zig.FieldInit)fieldInitItems[0].Content!;   // FieldInit -> '.' IDENT '=' Expr
        var variant = Tok(fi.Arg1);
        if (!info.Variants.TryGetValue(variant, out var payloadType))
        {
            throw new IrUnsupportedException($"union '{info.Name}' has no variant '{variant}'");
        }
        if (payloadType is null)
        {
            throw new IrUnsupportedException(
                $"union '{info.Name}' variant '{variant}' is a void variant — construct it as `.{variant}`, not `.{{ .{variant} = … }}`");
        }
        // Nested: new U { __tag = U_Tag.variant, __payload = new U_Payload { variant = value } }.
        var payloadNamed = new CType.Named(info.PayloadTypeName!);
        var payloadInit = new StructInit(new List<FieldInit>
        {
            new FieldInit(variant, payloadType, LowerExprSink(fi.Arg3, payloadType)),
        }) { Type = payloadNamed };
        var members = new List<FieldInit>
        {
            new FieldInit(info.TagFieldName, info.TagType, ResolveEnumLit(variant, info.TagType)),
            new FieldInit(info.PayloadFieldName, payloadNamed, payloadInit),
        };
        return new StructInit(members) { Type = new CType.Named(info.Name) };
    }

    /// <summary>Build a tagged-union VOID variant — the bare <c>.variant</c> form at a union sink
    /// — as a <see cref="StructInit"/> that sets only the <see cref="TagFieldName"/> discriminant
    /// (the payload fields take their zero default). The variant must be a void / tag-only
    /// variant.</summary>
    private CExpr BuildVoidVariant(ZigUnionInfo info, string variant)
    {
        if (!info.Variants.TryGetValue(variant, out var payloadType))
        {
            throw new IrUnsupportedException($"union '{info.Name}' has no variant '{variant}'");
        }
        if (payloadType is not null)
        {
            throw new IrUnsupportedException(
                $"union '{info.Name}' variant '{variant}' carries a payload — construct it as `.{{ .{variant} = … }}`");
        }
        var members = new List<FieldInit> { new FieldInit(info.TagFieldName, info.TagType, ResolveEnumLit(variant, info.TagType)) };
        return new StructInit(members) { Type = new CType.Named(info.Name) };
    }

    /// <summary>Lower an expression that has a known result type (a "sink"): the two
    /// result-located Zig forms — a bare enum literal <c>.member</c> and an anonymous struct
    /// literal <c>.{…}</c> — need that type to resolve, so they're dispatched here; every
    /// other expression ignores the sink and lowers via <see cref="LowerExpr"/> (the backend
    /// still coerces at the store). Used at each typed sink: a typed decl init, a
    /// <c>return</c>, an assignment target, a switch case value, a struct-literal field.</summary>
    private CExpr LowerExprSink(Item expr, CType? sink)
    {
        switch (expr.Content)
        {
            // A bare `.variant` at a tagged-union sink constructs its VOID variant (set the tag).
            case Zig.EnumLit el when sink?.Unqualified is CType.Named n && _unions.TryGetValue(n.Name, out var uinfo):
                return BuildVoidVariant(uinfo, Tok(el.Arg1));
            case Zig.EnumLit el when sink?.Unqualified is CType.Enum en:  // '.' IDENT
                return ResolveEnumLit(Tok(el.Arg1), en);
            case Zig.AnonStructInit:
            case Zig.AnonStructInitEmpty:
                return LowerStructInit(expr, sink);
            // `var x: T = undefined;` (scalar) → `default(T)` (Zig's uninitialized; a zeroed
            // over-approximation). An array sink is handled earlier in DeclOf (stackalloc).
            case Zig.UndefinedLit:
                return new DefaultLit { Type = sink ?? CType.Int };
            default:
            {
                var lowered = LowerExpr(expr);
                // Array / string-literal → slice coercion at a `[]T` / `[]const T` sink (Zig's
                // implicit `*[N]T` → `[]T` and string-literal `*const [N:0]u8` → `[]const u8`).
                // A value already of slice type passes through (e.g. forwarding a `[]const u8`).
                if (sink?.Unqualified is CType.Slice slc && lowered.Type.Unqualified is not CType.Slice)
                {
                    return CoerceToSlice(lowered, slc);
                }
                return lowered;
            }
        }
    }

    /// <summary>Coerce an array or string-literal value into a slice fat pointer at a
    /// <c>[]T</c> / <c>[]const T</c> sink (Zig's array→slice coercion). A string literal is
    /// <c>*const [N:0]u8</c> — its <c>.len</c> excludes the sentinel NUL, so the count is the
    /// <see cref="LitStr"/>'s byte length (which INCLUDES the NUL) minus one; a plain array
    /// <c>[N]T</c> keeps its full element count.</summary>
    private CExpr CoerceToSlice(CExpr value, CType.Slice sliceType)
    {
        if (value.Type.Unqualified is not CType.Array { Count: { } n })
        {
            throw new IrUnsupportedException(
                $"cannot coerce {value.Type.Describe()} to slice {sliceType.Describe()} (need an array or string literal)");
        }
        long count = value is LitStr ? n - 1 : n;   // string literal drops the trailing NUL
        var lenLit = new LitInt(count.ToString(CultureInfo.InvariantCulture), count) { Type = CType.ULong };
        var elem = sliceType.Element;
        return new SliceNew(value, lenLit, elem.Unqualified, elem.IsConst) { Type = sliceType };
    }

    // ---- statements ------------------------------------------------------

    private Block LowerBlock(Item block)
    {
        var stmts = new List<CStmt>();
        switch (block.Content)
        {
            case Zig.BlockEmpty: break;
            case Zig.Block b:
                foreach (var s in Flatten(b.Arg1)) { stmts.Add(LowerStmt(s)); }
                break;
            default: throw new IrUnsupportedException("zig block: " + (block.Content?.GetType().Name ?? "null"));
        }
        return new Block(stmts);
    }

    private CStmt LowerStmt(Item stmt)
    {
        switch (stmt.Content)
        {
            // A `const` may be a comptime allocator/namespace binding (`const std = @import("std");`,
            // `const a = std.heap.page_allocator;`) — recorded with NO runtime decl (Milestone F).
            case Zig.ConstDecl d:       return DeclOrComptime(d.Arg1, null, d.Arg3);
            case Zig.ConstDeclTyped d:  return DeclOrComptime(d.Arg1, d.Arg3, d.Arg5);
            case Zig.VarDecl d:         return DeclOf(d.Arg1, null, d.Arg3);
            case Zig.VarDeclTyped d:    return DeclOf(d.Arg1, d.Arg3, d.Arg5);
            // `const a, const b = e;` (Milestone G) — destructure a tuple value: single-eval the
            // RHS, then bind each name to its positional element. See LowerDestructure.
            case Zig.StmtDestructure sd: return LowerDestructure(sd);
            case Zig.StmtReturn r:      return LowerReturn(r.Arg1);
            case Zig.StmtReturnVoid:    return LowerReturnVoid();
            case Zig.StmtExpr e:        return new ExprStmt(LowerExpr(e.Arg0));

            // `x = value;`  → an assignment used as a statement. `_ = value;` is Zig's
            // explicit DISCARD (it forbids ignoring a non-void result) — lower it to a
            // bare expression statement, evaluated for its side effects.
            case Zig.StmtAssign a:
            {
                if (a.Arg0.Content is Zig.Ident lhs && Tok(lhs.Arg0) == "_")
                {
                    return new ExprStmt(LowerExpr(a.Arg2));
                }
                var target = LowerExpr(a.Arg0);
                var value = LowerExprSink(a.Arg2, target.Type);   // target type is the sink (`x = .member;`)
                return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
            }

            // if (cond) then [else else]  — `then`/`else`/`body` are themselves Stmts
            // (a single statement or a brace Block), which LowerStmt handles uniformly.
            case Zig.StmtIf f:          return new If(LowerExpr(f.Arg2), LowerStmt(f.Arg4), null);
            case Zig.StmtIfElse f:      return new If(LowerExpr(f.Arg2), LowerStmt(f.Arg4), LowerStmt(f.Arg6));
            case Zig.StmtWhile w:       return new While(LowerExpr(w.Arg2), LowerStmt(w.Arg4));

            // `while (cond) : (cont) body` → the C IR `For` (no init): the cont runs after each
            // iteration AND on `continue`, exactly matching C's for-update — so `continue`
            // inside the loop runs the cont, faithful to Zig. The assignment cont (`i = i + 1`)
            // builds an Assign CExpr post (mirroring StmtAssign); the bare-expr cont a plain one.
            case Zig.StmtWhileCont w:
                return new For(null, LowerExpr(w.Arg2), LowerExpr(w.Arg6), LowerStmt(w.Arg8));
            case Zig.StmtWhileContAssign w:
            {
                var post = LowerExpr(w.Arg6);
                var postVal = LowerExpr(w.Arg8);
                var postAssign = new Assign(null, post, postVal) { Type = post.Type };
                return new For(null, LowerExpr(w.Arg2), postAssign, LowerStmt(w.Arg10));
            }

            // `break;` / `continue;` — reuse the C IR loop-control nodes (the C# backend
            // renders them verbatim; valid inside the while/for forms above).
            case Zig.StmtBreak:    return new Break();
            case Zig.StmtContinue: return new Continue();

            // `switch (subject) { prongs }` → the C IR Switch (subject=Arg2, prongs=Arg5 for both
            // the plain and trailing-comma forms). A tagged-union subject takes the capture path.
            case Zig.StmtSwitch s:         return LowerSwitchStmt(s.Arg2, s.Arg5);
            case Zig.StmtSwitchTrailing s: return LowerSwitchStmt(s.Arg2, s.Arg5);

            // `for (start..end) |i| body` → C `for (usize i = start; i < end; i++) body`. The
            // capture `i` is the usize loop index (its own scope so it doesn't leak); the end
            // is cast to usize so the comparison is unsigned-clean (C# forbids ulong<>signed).
            case Zig.StmtForRange f:
            {
                _symbols.EnterScope();
                var start = LowerExpr(f.Arg2);
                var end = LowerExpr(f.Arg4);
                var iSym = _symbols.Declare(new Symbol { Name = Tok(f.Arg7), Kind = SymKind.Var, Type = CType.ULong });
                var iRef = new VarRef(iSym) { Type = CType.ULong, IsLValue = true };
                var init = new DeclStmt(new List<LocalDecl> { new(iSym, start) });
                var cond = new Binary(BinOp.Lt, iRef, new Cast(CType.ULong, end) { Type = CType.ULong }) { Type = CType.Int };
                var post = new Unary(UnOp.PostInc, iRef) { Type = CType.ULong };
                var body = LowerStmt(f.Arg9);
                _symbols.ExitScope();
                return new For(init, cond, post, body);
            }

            // `for (s) |x| body` — iterate a slice's elements (x = a per-iteration copy).
            case Zig.StmtForSlice f:     // for '(' Expr ')' '|' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg5), null, f.Arg7);
            // `for (s, 0..) |x, i| body` — also bind the usize index (counter + start).
            case Zig.StmtForSliceIdx f:  // for '(' Expr ',' Expr '..' ')' '|' IDENT ',' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg8), (Tok(f.Arg10), LowerExpr(f.Arg4)), f.Arg12);

            // A brace block in statement position (`Stmt -> Block`, pass-through).
            case Zig.Block:
            case Zig.BlockEmpty:        return LowerBlock(stmt);

            default: throw new IrUnsupportedException("zig statement: " + (stmt.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>Lower a local <c>const</c> declaration, intercepting a comptime allocator /
    /// namespace binding first (Milestone F): <c>const std = @import("std");</c> /
    /// <c>const a = std.heap.page_allocator;</c> carry no runtime value, so they register the
    /// alias (<see cref="TryComptimeConstBinding"/>) and emit nothing (an empty <see cref="Seq"/>).
    /// Any other <c>const</c> is an ordinary <see cref="DeclOf"/>.</summary>
    private CStmt DeclOrComptime(Item nameTok, Item? typeItem, Item initExpr)
        => TryComptimeConstBinding(Tok(nameTok), initExpr)
            ? new Seq(new List<CStmt>())
            : DeclOf(nameTok, typeItem, initExpr);

    private CStmt DeclOf(Item nameTok, Item? typeItem, Item initExpr)
    {
        // Compute the declared type FIRST: a result-located init (`.member` / `.{…}`) needs
        // it as its sink, so resolve the annotation before lowering the initializer.
        var declared = typeItem is not null ? LowerType(typeItem) : null;
        // `var b: [N]T = undefined;` → a stackalloc'd C array (ArrayDecl → `T* b = stackalloc
        // T[N]`), so `b[i]` / `b[lo..hi]` reuse the array paths and yield a stack-backed slice.
        // Only `undefined` init is supported (positional `.{…}` array literals are deferred).
        if (declared is CType.Array arr)
        {
            if (initExpr.Content is not Zig.UndefinedLit)
            {
                throw new IrUnsupportedException(
                    "a `[N]T` array local must be initialized with `undefined` (positional array literals are not supported yet)");
            }
            var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
            var count = new LitInt((arr.Count ?? 0).ToString(CultureInfo.InvariantCulture), arr.Count ?? 0) { Type = CType.Int };
            return new ArrayDecl(sym, arr.Element, count, null);   // C# zero-fills the stackalloc
        }
        var init = LowerExprSink(initExpr, declared);
        var type = declared ?? init.Type ?? CType.Int;
        var sym2 = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = type });
        return new DeclStmt(new List<LocalDecl> { new(sym2, init) });
    }

    /// <summary>Lower a destructure binding <c>const a, const b = e;</c> (Milestone G). The RHS is
    /// evaluated ONCE into a fresh tuple temp (<c>__tupN</c>), then each binder reads its positional
    /// element (<c>__tupN.ItemK</c>). Emitted as a brace-less <see cref="Seq"/> so the binders land
    /// in the ENCLOSING scope (a <see cref="Block"/> would wrongly scope them). The RHS must be a
    /// tuple whose arity matches the binder count. V1 binders are <c>const</c>/<c>var</c> only — the
    /// const-ness isn't enforced (both lower to a C# local); the assign-to-existing form is
    /// deferred (see the grammar).</summary>
    private CStmt LowerDestructure(Zig.StmtDestructure d)
    {
        // Binders in source order: the leading one (Arg0) + the rest (the Arg2 list).
        var binders = new List<Item> { d.Arg0 };
        binders.AddRange(Flatten(d.Arg2));
        var rhs = LowerExpr(d.Arg4);   // RhsExpr is transparent — the underlying Expr/IfExpr comes through
        if (rhs.Type.Unqualified is not CType.Tuple tup)
        {
            throw new IrUnsupportedException(
                $"zig destructure `const a, … = e` needs a tuple value; got {rhs.Type.Describe()}");
        }
        if (tup.Elements.Count != binders.Count)
        {
            throw new IrUnsupportedException(
                $"zig destructure binds {binders.Count} name(s) but the tuple has {tup.Elements.Count} element(s)");
        }
        var stmts = new List<CStmt>();
        // The single-eval temp: `var __tupN = e;`.
        var tmp = _symbols.Declare(new Symbol { Name = "__tup" + _tupleTempCounter++, Kind = SymKind.Var, Type = tup });
        stmts.Add(new DeclStmt(new List<LocalDecl> { new(tmp, rhs) }));
        var tmpRef = new VarRef(tmp) { Type = tup, IsLValue = true };
        for (int i = 0; i < binders.Count; i++)
        {
            var name = binders[i].Content switch
            {
                Zig.DestructBindConst c => Tok(c.Arg1),   // 'const' IDENT
                Zig.DestructBindVar v   => Tok(v.Arg1),   // 'var' IDENT
                _ => throw new IrUnsupportedException(
                    "zig destructure binder: " + (binders[i].Content?.GetType().Name ?? "null")),
            };
            var et = tup.Elements[i];
            var read = new TupleIndex(tmpRef, i, et) { Type = et };
            var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = et });
            stmts.Add(new DeclStmt(new List<LocalDecl> { new(sym, read) }));
        }
        return new Seq(stmts);
    }

    /// <summary>Dispatch a <c>switch</c> statement: lower the subject once, then route a
    /// tagged-union subject (a value or pointer-to a registered <c>union(enum)</c>) to
    /// <see cref="LowerUnionSwitch"/> (the tag-discriminant + payload-capture path) and any other
    /// subject to the plain <see cref="LowerSwitch"/>.</summary>
    private CStmt LowerSwitchStmt(Item subjectItem, Item prongsItem)
    {
        var subject = LowerExpr(subjectItem);
        var u = subject.Type.Unqualified;
        var uname = u switch
        {
            CType.Named n => n.Name,
            CType.Pointer { Pointee: var pe } when pe.Unqualified is CType.Named pn => pn.Name,
            _ => null,
        };
        if (uname is not null && _unions.TryGetValue(uname, out var info))
        {
            return LowerUnionSwitch(subject, prongsItem, info);
        }
        return LowerSwitch(subject, prongsItem);
    }

    /// <summary>Lower a non-union <c>switch (subject) { prong, … }</c> to the C IR
    /// <see cref="Switch"/>. Each prong (<c>CaseVals =&gt; Block</c>) becomes a
    /// <see cref="SwitchSection"/>: its case values are the labels (<c>else</c> → the null
    /// default label), and its braced block is the body. Zig switch has NO fall-through, so a
    /// terminating <see cref="Break"/> is appended to any section that doesn't already end
    /// control flow — otherwise the C# backend would synthesize C's fall-through jump. A payload
    /// capture <c>|x|</c> here is an error (only a tagged-union switch binds a payload).</summary>
    private CStmt LowerSwitch(CExpr subject, Item prongsItem)
    {
        var sections = new List<SwitchSection>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            if (prongItem.Content is Zig.ProngCapture)
            {
                throw new IrUnsupportedException(
                    "zig switch payload capture `|x|` is only valid on a tagged-union switch");
            }
            var p = (Zig.Prong)prongItem.Content!;            // CaseVals=Arg0, '=>'=Arg1, Block=Arg2
            var labels = LowerCaseVals(p.Arg0, subject.Type); // case values compare against the subject
            var body = new List<CStmt> { LowerBlock(p.Arg2) }; // the braced block, scoped
            if (!EndsInJump(body)) { body.Add(new Break()); }  // no Zig fall-through
            sections.Add(new SwitchSection(labels, body));
        }
        return new Switch(subject, sections);
    }

    /// <summary>Lower a <c>switch</c> over a tagged union: switch on the <see cref="TagFieldName"/>
    /// discriminant, with <c>.variant</c> case labels resolving against the tag enum. A
    /// <c>|x|</c> payload capture binds <c>x</c> to the matched variant's payload field (by value)
    /// at the top of that prong's block. The subject is hoisted to a temp first (unless it is
    /// already a simple variable) so each capture re-reads it without re-evaluating a
    /// side-effecting subject expression.</summary>
    private CStmt LowerUnionSwitch(CExpr subject, Item prongsItem, ZigUnionInfo info)
    {
        var isPtr = subject.Type.Unqualified is CType.Pointer;
        var pre = new List<CStmt>();
        CExpr unionRef;
        if (subject is VarRef)
        {
            unionRef = subject;   // a bare variable — safe to re-reference per prong
        }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__un", Kind = SymKind.Var, Type = subject.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, subject) }));
            unionRef = new VarRef(tmp) { Type = subject.Type, IsLValue = true };
        }
        var disc = new Member(unionRef, info.TagFieldName, isPtr) { Type = info.TagType, IsLValue = true };

        var sections = new List<SwitchSection>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            Item caseVals; string? captureName; Item block;
            switch (prongItem.Content)
            {
                case Zig.Prong p:        caseVals = p.Arg0; captureName = null;        block = p.Arg2; break;
                case Zig.ProngCapture p: caseVals = p.Arg0; captureName = Tok(p.Arg3); block = p.Arg5; break;
                default: throw new IrUnsupportedException("zig switch prong: " + (prongItem.Content?.GetType().Name ?? "null"));
            }
            var labels = LowerCaseVals(caseVals, info.TagType);   // `.variant` → EnumConstRef(U_Tag.variant)

            List<CStmt> body;
            if (captureName is not null && captureName != "_")
            {
                var variant = SingleVariantName(caseVals, info);
                var payloadType = info.Variants[variant]
                    ?? throw new IrUnsupportedException(
                        $"union '{info.Name}' variant '{variant}' is a void variant — it has no payload to capture with `|{captureName}|`");
                _symbols.EnterScope();
                var capSym = _symbols.Declare(new Symbol { Name = captureName, Kind = SymKind.Var, Type = payloadType });
                // `var x = __un.__payload.variant;` — read the overlaid payload field.
                var payloadBase = new Member(unionRef, info.PayloadFieldName, isPtr) { Type = new CType.Named(info.PayloadTypeName!), IsLValue = true };
                var capInit = new Member(payloadBase, variant, false) { Type = payloadType, IsLValue = true };
                var inner = LowerBlock(block);
                _symbols.ExitScope();
                var combined = new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(capSym, capInit) }) };
                combined.AddRange(inner.Stmts);
                body = new List<CStmt> { new Block(combined) };
            }
            else
            {
                body = new List<CStmt> { LowerBlock(block) };
            }
            if (!EndsInJump(body)) { body.Add(new Break()); }   // no Zig fall-through
            sections.Add(new SwitchSection(labels, body));
        }

        // A Zig union switch is exhaustive; C# can't prove a tag switch covers every case, so
        // without an `else` it would reject the enclosing function ("not all code paths return",
        // CS0161). Make the LAST prong the `default` — for an exhaustive switch (which valid Zig
        // requires) the last variant's tag is the only value that reaches it, so this is
        // semantics-preserving and needs no synthetic statement.
        if (sections.Count > 0 && !sections.Any(s => s.Labels.Any(l => l.CaseExpr is null)))
        {
            sections[^1] = sections[^1] with { Labels = new List<SwitchLabel> { new SwitchLabel(null) } };
        }

        var sw = new Switch(disc, sections);
        if (pre.Count == 0) { return sw; }
        pre.Add(sw);
        return new Block(pre);   // { var __un = subject; switch (__un.__tag) { … } }
    }

    /// <summary>Lower a for-over-slice — <c>for (s) |x| body</c> and (when <paramref name="index"/>
    /// is set) <c>for (s, START..) |x, i| body</c> — to the C IR <c>for</c>:
    /// <code>{ var __s = s; for (usize __i = 0; __i &lt; __s.Len; __i++) { var x = __s.Ptr[__i];
    /// [var i = __i + START;] body } }</code>
    /// The element capture <c>x</c> is a per-iteration copy (Zig's by-value <c>|x|</c>; the by-ref
    /// <c>|*x|</c> form is deferred). The slice is hoisted to <c>__s</c> unless it is already a bare
    /// variable, so <c>.Len</c>/<c>.Ptr</c> aren't re-evaluated with side effects.</summary>
    private CStmt LowerForSlice(CExpr sliceExpr, string elemName, (string name, CExpr start)? index, Item bodyItem)
    {
        if (sliceExpr.Type.Unqualified is not CType.Slice slc)
        {
            throw new IrUnsupportedException($"for-over-slice needs a slice; got {sliceExpr.Type.Describe()}");
        }
        var pre = new List<CStmt>();
        CExpr sliceRef;
        if (sliceExpr is VarRef)
        {
            sliceRef = sliceExpr;
        }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__s", Kind = SymKind.Var, Type = sliceExpr.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, sliceExpr) }));
            sliceRef = new VarRef(tmp) { Type = sliceExpr.Type, IsLValue = true };
        }

        _symbols.EnterScope();
        // usize __i = 0; __i < __s.Len; __i++
        var iSym = _symbols.Declare(new Symbol { Name = "__i", Kind = SymKind.Var, Type = CType.ULong });
        var iRef = new VarRef(iSym) { Type = CType.ULong, IsLValue = true };
        var init = new DeclStmt(new List<LocalDecl> { new(iSym, new LitInt("0", 0) { Type = CType.ULong }) });
        var lenMember = new Member(sliceRef, "Len", false) { Type = CType.ULong, IsLValue = true };
        var cond = new Binary(BinOp.Lt, iRef, lenMember) { Type = CType.Int };
        var post = new Unary(UnOp.PostInc, iRef) { Type = CType.ULong };

        // body: prepend `var x = __s.Ptr[__i];` (the element copy) and, for the index form,
        // `var i = __i + START;`.
        var ptrMember = new Member(sliceRef, "Ptr", false) { Type = new CType.Pointer(slc.Element) };
        var elemInit = new DotCC.Ir.Index(ptrMember, iRef) { Type = slc.Element, IsLValue = true };
        var elemSym = _symbols.Declare(new Symbol { Name = elemName, Kind = SymKind.Var, Type = slc.Element });
        var bodyStmts = new List<CStmt> { new DeclStmt(new List<LocalDecl> { new(elemSym, elemInit) }) };
        if (index is { } idx)
        {
            var idxInit = new Binary(BinOp.Add, iRef, new Cast(CType.ULong, idx.start) { Type = CType.ULong }) { Type = CType.ULong };
            var idxSym = _symbols.Declare(new Symbol { Name = idx.name, Kind = SymKind.Var, Type = CType.ULong });
            bodyStmts.Add(new DeclStmt(new List<LocalDecl> { new(idxSym, idxInit) }));
        }
        bodyStmts.Add(LowerStmt(bodyItem));
        _symbols.ExitScope();

        var forStmt = new For(init, cond, post, new Block(bodyStmts));
        if (pre.Count == 0) { return forStmt; }
        pre.Add(forStmt);
        return new Block(pre);
    }

    /// <summary>The single <c>.variant</c> a tagged-union capture prong matches — a capture
    /// (<c>|x|</c>) is only meaningful on a prong that selects exactly one payload variant, so an
    /// <c>else</c>, a multi-value prong, or an unknown variant is rejected.</summary>
    private string SingleVariantName(Item caseVals, ZigUnionInfo info)
    {
        var vals = Flatten(caseVals);
        if (vals.Count != 1 || vals[0].Content is not Zig.EnumLit el)
        {
            var what = caseVals.Content is Zig.CaseElse ? "`else`" : $"{vals.Count} value(s)";
            throw new IrUnsupportedException(
                $"a tagged-union capture prong must match exactly one `.variant` (got {what})");
        }
        var variant = Tok(el.Arg1);
        if (!info.Variants.ContainsKey(variant))
        {
            throw new IrUnsupportedException($"union '{info.Name}' has no variant '{variant}'");
        }
        return variant;
    }

    /// <summary>Lower a prong's case values to switch labels: <c>else</c> → the single null
    /// (default) label; otherwise each comma-separated value Expr → one constant label,
    /// lowered against the subject type as its sink (so a <c>.member</c> case resolves when
    /// switching on an enum).</summary>
    private List<SwitchLabel> LowerCaseVals(Item caseVals, CType? sink)
    {
        if (caseVals.Content is Zig.CaseElse)
        {
            return new List<SwitchLabel> { new SwitchLabel(null) };
        }
        var labels = new List<SwitchLabel>();
        foreach (var v in Flatten(caseVals)) { labels.Add(new SwitchLabel(LowerExprSink(v, sink))); }
        return labels;
    }

    /// <summary>True when a lowered statement list provably ends control flow (so no
    /// synthetic <see cref="Break"/> is needed for a switch section). Mirrors the C# backend's
    /// own <c>Terminates</c>.</summary>
    private static bool EndsInJump(IReadOnlyList<CStmt> body) =>
        body.Count > 0 && Terminates(body[^1]);

    private static bool Terminates(CStmt s) => s switch
    {
        Return or Break or Continue or Goto => true,
        Block b => b.Stmts.Count > 0 && Terminates(b.Stmts[^1]),
        If f => f.Else is { } e && Terminates(f.Then) && Terminates(e),
        _ => false,
    };

    /// <summary>Lower <c>return e;</c>. In a <c>!T</c> function the value becomes an error
    /// union: <c>return error.Foo;</c> → an <see cref="ErrUnionErr"/>; a value that is ALREADY
    /// an error union (<c>return f();</c> where <c>f</c> returns <c>!U</c>) is returned as-is
    /// (Zig doesn't auto-unwrap); any plain value is wrapped in an <see cref="ErrUnionOk"/>.
    /// Outside an error-union function it is a plain <see cref="Return"/>.</summary>
    private CStmt LowerReturn(Item valueItem)
    {
        if (_currentFnRet is CType.ErrorUnion eu)
        {
            if (IsErrorLit(valueItem, out var errName))
            {
                return new Return(new ErrUnionErr(ErrorCode(errName)) { Type = eu });
            }
            var v = LowerExpr(valueItem);
            if (v.Type.Unqualified is CType.ErrorUnion) { return new Return(v); }
            return new Return(new ErrUnionOk(v) { Type = eu });
        }
        // The return type is the sink, so `return .member;` / `return .{…};` resolve against
        // a struct/enum-returning function.
        return new Return(LowerExprSink(valueItem, _currentFnRet));
    }

    /// <summary>Lower <c>return;</c>. In a <c>!void</c> function it is a success error union
    /// with no payload (<c>ErrUnion&lt;Unit&gt;.Ok(default)</c>); otherwise a plain
    /// <c>return;</c>.</summary>
    private CStmt LowerReturnVoid() =>
        _currentFnRet is CType.ErrorUnion eu
            ? new Return(new ErrUnionOk(null) { Type = eu })
            : new Return(null);

    /// <summary>True when an item is an <c>error.Foo</c> literal, yielding the error name.</summary>
    private static bool IsErrorLit(Item it, out string name)
    {
        if (it.Content is Zig.ErrorLit e) { name = Tok(e.Arg2); return true; }
        name = "";
        return false;
    }

    // ---- expressions -----------------------------------------------------

    private CExpr LowerExpr(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.IntLit i:
            {
                var text = Tok(i.Arg0);
                var value = long.Parse(text, CultureInfo.InvariantCulture);
                return new LitInt(text, value) { Type = CType.Int };
            }
            case Zig.FloatLit f: return new LitFloat(Tok(f.Arg0)) { Type = CType.Double };

            // A string literal. Zig's escape set overlaps C's for the common cases
            // (`\n`/`\t`/`\\`/`\"`/`\xNN`), so we reuse the C string machinery: the raw
            // quoted lexeme becomes a single LitStr segment, typed `char[N]` (decoded
            // byte count incl. NUL) so it decays to `char*` exactly like a C literal —
            // and the C# backend lowers it to the same pooled `Libc.L("…"u8)` pointer.
            // (Zig's `\u{…}` and multiline `\\` strings are deferred.)
            case Zig.StrLit s:
            {
                var segs = new List<string> { Tok(s.Arg0) };   // raw lexeme, quotes included
                DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
                return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
            }
            case Zig.Ident id:
            {
                var name = Tok(id.Arg0);
                // A const bound to the statically-known default allocator (Milestone F) emitted no
                // runtime decl; used here as a VALUE (e.g. passed to a `std.mem.Allocator`
                // parameter) it materializes the C-heap allocator. (As a `.alloc`/`.free` RECEIVER
                // it never reaches this case — LowerMethodCall short-circuits to the devirt path.)
                if (_defaultAllocatorBindings.ContainsKey(name)) { return MaterializeCHeap(); }
                var sym = _symbols.Resolve(name)
                    ?? throw new IrUnsupportedException($"unresolved identifier '{name}'");
                return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
            }
            case Zig.Grouped g: { var inner = LowerExpr(g.Arg1); return new Paren(inner) { Type = inner.Type }; }

            // if (cond) a else b  — the if-EXPRESSION, lowered to a ternary. Both
            // branches are RhsExpr; the backend wraps the condition in Cond.B.
            case Zig.IfExpr e:
            {
                var then = LowerExpr(e.Arg4);
                return new CondExpr(LowerExpr(e.Arg2), then, LowerExpr(e.Arg6)) { Type = then.Type };
            }

            // arithmetic
            case Zig.Add a:     return Bin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.Sub a:     return Bin(BinOp.Sub, a.Arg0, a.Arg2);
            case Zig.Mul a:     return Bin(BinOp.Mul, a.Arg0, a.Arg2);
            case Zig.DivOp a:   return Bin(BinOp.Div, a.Arg0, a.Arg2);
            case Zig.ModOp a:   return Bin(BinOp.Mod, a.Arg0, a.Arg2);
            // comparison (non-associative in the grammar)
            case Zig.CmpEq a:   return Bin(BinOp.Eq, a.Arg0, a.Arg2);
            case Zig.CmpNe a:   return Bin(BinOp.Ne, a.Arg0, a.Arg2);
            case Zig.CmpLt a:   return Bin(BinOp.Lt, a.Arg0, a.Arg2);
            case Zig.CmpGt a:   return Bin(BinOp.Gt, a.Arg0, a.Arg2);
            case Zig.CmpLe a:   return Bin(BinOp.Le, a.Arg0, a.Arg2);
            case Zig.CmpGe a:   return Bin(BinOp.Ge, a.Arg0, a.Arg2);
            // boolean (short-circuit)
            case Zig.BoolOr a:  return Bin(BinOp.LogOr, a.Arg0, a.Arg2);
            case Zig.BoolAnd a: return Bin(BinOp.LogAnd, a.Arg0, a.Arg2);
            // bitwise / shift
            case Zig.BitAnd a:  return Bin(BinOp.BitAnd, a.Arg0, a.Arg2);
            case Zig.BitXor a:  return Bin(BinOp.BitXor, a.Arg0, a.Arg2);
            case Zig.BitOr a:   return Bin(BinOp.BitOr, a.Arg0, a.Arg2);
            case Zig.Shl a:     return Bin(BinOp.Shl, a.Arg0, a.Arg2);
            case Zig.Shr a:     return Bin(BinOp.Shr, a.Arg0, a.Arg2);
            // value prefix
            case Zig.PreNeg p:    return Pre(UnOp.Neg, p.Arg1);
            case Zig.PreBitNot p: return Pre(UnOp.BitNot, p.Arg1);
            case Zig.PreNot p:    return Pre(UnOp.LogNot, p.Arg1);
            // Address-of `&x` → a `*T` pointer. Mark a var/param operand AddressTaken so
            // the backend emits a moveable-variable pointer (mirrors IrBuilder.Un's
            // single-site rule). `try` still needs error unions (Milestone B).
            case Zig.PreAddrOf p:
            {
                var operand = LowerExpr(p.Arg1);
                if (Unparen(operand) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
                {
                    s.AddressTaken = true;
                }
                return new Unary(UnOp.AddrOf, operand) { Type = new CType.Pointer(operand.Type) };
            }
            // `try e` — unwrap the error union's payload, or propagate its error by throwing
            // ZigErrorReturn (caught at the enclosing `!T` function's emitted try/catch — the
            // backend's Func wrap). An expression, so it works in any position.
            case Zig.PreTry p:
            {
                var inner = LowerExpr(p.Arg1);
                if (inner.Type.Unqualified is not CType.ErrorUnion eu)
                {
                    throw new IrUnsupportedException("zig `try` requires an error-union operand");
                }
                return new ZigTry(inner) { Type = eu.Payload };
            }

            // `base.field` (Suffix '.' IDENT) — three meanings split here. When the base is a bare
            // identifier naming a container TYPE (not a variable): `Type.NAME` resolves to a
            // namespaced VALUE const if the container declares one (the comptime RHS, inlined fresh
            // here with its annotation as the sink); else `EnumName.member` → an EnumConstRef.
            // Otherwise it's struct field access on a value/pointer; Zig has no `->`, so `p.x` on a
            // pointer auto-derefs (emit `->`). The field type comes from the shared aggregate table.
            case Zig.Field fld:
            {
                var fieldName = Tok(fld.Arg2);
                // A dotted std path used as a VALUE (Milestone F): the C-heap default
                // (`std.heap.page_allocator`/`c_allocator`) materializes a runtime Allocator; a std
                // TYPE used as a value, or any unmodeled std path, errors. (A `.alloc(…)` /
                // `.init(…)` CALL never reaches here — the callee Field goes through LowerMethodCall.)
                if (TryResolveStdPath(expr, out var stdPath))
                {
                    return stdPath switch
                    {
                        "std.heap.page_allocator" or "std.heap.c_allocator" => MaterializeCHeap(),
                        "std.mem.Allocator" or "std.heap.FixedBufferAllocator" => throw new IrUnsupportedException(
                            $"zig `{stdPath}` is a type, not a value"),
                        _ => throw new IrUnsupportedException(
                            $"zig std path `{stdPath}` is not modeled (values: std.heap.page_allocator / std.heap.c_allocator)"),
                    };
                }
                if (fld.Arg0.Content is Zig.Ident cbid
                    && _symbols.Resolve(Tok(cbid.Arg0)) is null
                    && TryLookupContainerType(Tok(cbid.Arg0), out var cbaseTy)
                    && ContainerTypeName(cbaseTy) is { } cContainer
                    && _containerConsts.TryGetValue(cContainer, out var cconsts)
                    && cconsts.TryGetValue(fieldName, out var centry))
                {
                    var csink = centry.typeItem is not null ? LowerType(centry.typeItem) : null;
                    return LowerExprSink(centry.rhs, csink);
                }
                if (fld.Arg0.Content is Zig.Ident bid
                    && TryLookupContainerType(Tok(bid.Arg0), out var baseTy)
                    && baseTy.Unqualified is CType.Enum en)
                {
                    return ResolveEnumLit(fieldName, en);
                }
                var structExpr = LowerExpr(fld.Arg0);
                var arrow = structExpr.Type.Unqualified is CType.Pointer;   // Zig `p.x` auto-derefs
                // Slice `.len` / `.ptr` — the runtime Slice<T> exposes `Len` (ulong) and
                // `Ptr` (T*); a `[]const T`'s `.ptr` is a pointer-to-const.
                if (structExpr.Type.Unqualified is CType.Slice slc)
                {
                    return fieldName switch
                    {
                        "len" => new Member(structExpr, "Len", arrow) { Type = CType.ULong, IsLValue = true },
                        "ptr" => new Member(structExpr, "Ptr", arrow) { Type = new CType.Pointer(slc.Element), IsLValue = true },
                        _ => throw new IrUnsupportedException($"slice has no field '{fieldName}' (only .len / .ptr)"),
                    };
                }
                // Tagged-union payload access `u.variant` → `u.__payload.variant` (unchecked,
                // like Zig's release-mode field access; the tag isn't a user-facing field).
                if (TryContainerName(structExpr.Type, out var cname)
                    && _unions.TryGetValue(cname, out var uinfo)
                    && uinfo.Variants.TryGetValue(fieldName, out var vpayload) && vpayload is not null)
                {
                    var payloadBase = new Member(structExpr, uinfo.PayloadFieldName, arrow) { Type = new CType.Named(uinfo.PayloadTypeName!), IsLValue = true };
                    return new Member(payloadBase, fieldName, false) { Type = vpayload, IsLValue = true };
                }
                var ftype = _ir.StructFieldType(structExpr.Type, fieldName)
                    ?? throw new IrUnsupportedException($"no field '{fieldName}' on type {structExpr.Type.Describe()}");
                return new Member(structExpr, fieldName, arrow) { Type = ftype, IsLValue = true };
            }

            // A bare enum literal `.member` or anonymous struct literal `.{…}` outside a
            // typed sink — Zig requires a known result type for both, so reject loudly here
            // (the sink-aware paths in LowerExprSink handle the valid cases).
            case Zig.EnumLit:
                throw new IrUnsupportedException(
                    "zig enum literal `.member` needs a known result type (use a typed declaration, a return, an assignment, or a switch on the enum)");
            // A `.{…}` with no sink: a POSITIONAL list is an inferred tuple literal (`const t =
            // .{a, b};`); a NAMED list still needs a struct result type, so LowerStructInit errors
            // there as before (Milestone G routes both through the one method).
            case Zig.AnonStructInit:
            case Zig.AnonStructInitEmpty:
                return LowerStructInit(expr, null);

            // Typed struct literal `Type{ .field = … }` (Zig's CurlySuffixExpr). The struct type
            // is named explicitly, so — unlike `.{…}` — it needs no sink and is valid anywhere.
            case Zig.TypedStructInit t:       // CurlySuffix -> Type '{' FieldInits '}'
                return LowerTypedStructInit(t.Arg0, Flatten(t.Arg2));
            case Zig.TypedStructInitEmpty t:  // CurlySuffix -> Type '{' '}'
                return LowerTypedStructInit(t.Arg0, []);

            // Postfix deref `p.*` and subscript `a[i]` → the C Unary(Deref)/Index IR.
            case Zig.Deref d:
            {
                var operand = LowerExpr(d.Arg0);
                var pointee = operand.Type.Unqualified switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => operand.Type,
                };
                return new Unary(UnOp.Deref, operand) { Type = pointee, IsLValue = true };
            }
            case Zig.Index ix:
            {
                var baseExpr = LowerExpr(ix.Arg0);
                var idx = LowerExpr(ix.Arg2);
                // A tuple subscript `t[N]` (N a literal) reads the Nth element → `.ItemN+1`
                // (Milestone G). A tuple has no runtime indexing (the field is statically named),
                // so a non-literal index is rejected.
                if (baseExpr.Type.Unqualified is CType.Tuple tup)
                {
                    if (idx is not LitInt { Value: { } n })
                    {
                        throw new IrUnsupportedException(
                            "zig tuple index must be an integer literal (a tuple has no runtime indexing)");
                    }
                    if (n < 0 || n >= tup.Elements.Count)
                    {
                        throw new IrUnsupportedException(
                            $"zig tuple index {n} is out of range (the tuple has {tup.Elements.Count} element(s))");
                    }
                    return new TupleIndex(baseExpr, (int)n, tup.Elements[(int)n]) { Type = tup.Elements[(int)n] };
                }
                // A slice subscript indexes through its data pointer: `s[i]` → `s.Ptr[i]`.
                if (baseExpr.Type.Unqualified is CType.Slice slc)
                {
                    var ptr = new Member(baseExpr, "Ptr", false) { Type = new CType.Pointer(slc.Element) };
                    return new DotCC.Ir.Index(ptr, idx) { Type = slc.Element, IsLValue = true };
                }
                var elem = baseExpr.Type switch
                {
                    CType.Pointer p => p.Pointee,
                    CType.Array a => a.Element,
                    _ => CType.Int,
                };
                return new DotCC.Ir.Index(baseExpr, idx) { Type = elem, IsLValue = true };
            }

            // Slicing `a[lo..hi]` → a sub-slice fat pointer `{ a.ptr + lo, hi - lo }`. The base
            // may be a slice (re-slice through `.Ptr`), a pointer, or an array (decays); the
            // element type + const-ness ride into the resulting `[]T` / `[]const T`.
            case Zig.SliceRange sr:
            {
                var baseExpr = LowerExpr(sr.Arg0);
                var lo = LowerExpr(sr.Arg2);
                var hi = LowerExpr(sr.Arg4);
                CExpr basePtr;
                CType element;
                switch (baseExpr.Type.Unqualified)
                {
                    case CType.Slice s:
                        basePtr = new Member(baseExpr, "Ptr", false) { Type = new CType.Pointer(s.Element) };
                        element = s.Element;
                        break;
                    case CType.Pointer p:
                        basePtr = baseExpr;
                        element = p.Pointee;
                        break;
                    case CType.Array a:
                        basePtr = baseExpr;   // decays to its element pointer
                        element = a.Element;
                        break;
                    default:
                        throw new IrUnsupportedException($"cannot slice a {baseExpr.Type.Describe()} (need a slice, pointer, or array)");
                }
                var ptr = new Binary(BinOp.Add, basePtr, lo) { Type = new CType.Pointer(element) };
                // len = (ulong)(hi - lo). The explicit cast covers non-constant bounds, where
                // a signed `int` difference has no implicit conversion to the ctor's `ulong`.
                var diff = new Binary(BinOp.Sub, hi, lo) { Type = hi.Type };
                var len = new Cast(CType.ULong, diff) { Type = CType.ULong };
                return new SliceNew(ptr, len, element.Unqualified, element.IsConst) { Type = new CType.Slice(element) };
            }

            // `.?` optional unwrap. A value optional (CType.Optional → C# `T?`) unwraps via
            // `.Value` (panics on none, matching Zig's `.?`-on-null). An optional POINTER is
            // a bare `T*`, so unwrapping is the identity (the non-null pointer is the same
            // value). [V1: the pointer form does not runtime-check for null.]
            case Zig.Unwrap u:
            {
                var operand = LowerExpr(u.Arg0);
                if (operand.Type.Unqualified is CType.Optional opt)
                {
                    return new Member(operand, "Value", false) { Type = opt.Inner };
                }
                return operand;
            }

            // `@as(T, expr)` — the explicit-type cast builtin → the C Cast IR (RenderCast).
            // Zig 0.16's `@intCast`/`@ptrCast` are result-location-typed (single arg, no
            // explicit target type), which needs the context-type inference dotcc lacks —
            // deferred. arg0 is a type spelled as an expression (handled by LowerType).
            case Zig.BuiltinCall b:
            {
                var bname = Tok(b.Arg0);
                var bargs = Flatten(b.Arg2);
                switch (bname)
                {
                    case "@as":
                        // `@as(T, expr)` — the explicit-type cast → the C Cast IR.
                        if (bargs.Count != 2)
                        {
                            throw new IrUnsupportedException($"zig `@as` expects (type, value); got {bargs.Count} argument(s)");
                        }
                        var target = LowerType(bargs[0]);
                        return new Cast(target, LowerExpr(bargs[1])) { Type = target };
                    case "@intFromEnum":
                        // `@intFromEnum(e)` — the enum's integer value → decay to the
                        // underlying type (the same Cast the backend uses for C's enum→int).
                        if (bargs.Count != 1)
                        {
                            throw new IrUnsupportedException($"zig `@intFromEnum` expects (enum); got {bargs.Count} argument(s)");
                        }
                        var operand = LowerExpr(bargs[0]);
                        if (operand.Type.Unqualified is not CType.Enum en)
                        {
                            throw new IrUnsupportedException("zig `@intFromEnum` expects an enum operand");
                        }
                        return new Cast(en.Underlying, operand) { Type = en.Underlying };
                    default:
                        throw new IrUnsupportedException($"zig builtin '{bname}' not lowered yet (only @as / @intFromEnum are supported)");
                }
            }

            // `null` — reuse the C null-pointer node (renders C# `null`, valid for BOTH a
            // pointer sink `T*` and a value-optional sink `T?`). In Zig `null` only appears
            // at a typed sink, so the backend's store-coercion gives it the right form.
            case Zig.NullLit: return new NullPtr { Type = new CType.Pointer(CType.Void) };

            // `undefined` — uninitialized storage. Without a sink we can only emit a zeroed
            // `default` (an over-approximation of Zig's "any value"; a correct program writes
            // before reading). At a typed sink LowerExprSink types it precisely; an array local
            // takes the dedicated stackalloc path in DeclOf.
            case Zig.UndefinedLit: return new DefaultLit { Type = CType.Int };

            // `a orelse b`. A value optional → C#'s `??` (single-eval LHS, lazy RHS) via
            // NullCoalesce. An optional POINTER → `a != null ? a : b` (C# `??` doesn't apply
            // to pointers); the LHS is named twice there, so a non-trivial (side-effecting)
            // left operand is rejected rather than silently double-evaluated. `orelse return`
            // (a noreturn RHS) isn't expressible in the grammar yet — that's Milestone B2.
            case Zig.OrElse o:
            {
                var left = LowerExpr(o.Arg0);
                var right = LowerExpr(o.Arg2);
                if (left.Type.Unqualified is CType.Optional opt)
                {
                    return new NullCoalesce(left, right) { Type = opt.Inner };
                }
                if (left.Type.Unqualified is CType.Pointer)
                {
                    if (!IsSimpleReeval(left))
                    {
                        throw new IrUnsupportedException(
                            "zig `orelse` on a pointer with a non-trivial left operand not lowered yet (it would be double-evaluated)");
                    }
                    var notNull = new Binary(BinOp.Ne, left, new NullPtr { Type = new CType.Pointer(CType.Void) }) { Type = CType.Int };
                    return new CondExpr(notNull, left, right) { Type = left.Type };
                }
                throw new IrUnsupportedException("zig `orelse` requires an optional left operand");
            }

            // `a catch b` — the error union's payload on success, else the fallback `b` (no
            // propagation). Lowers to the eager `ErrUnion.Catch(a, b)`; since C# evaluates `b`
            // before the call, the fallback must be side-effect-free for that to match Zig's
            // lazy form, so a non-trivial fallback is rejected (deferred) — mirrors the pointer
            // `orelse` rule in B1. `catch |e| …` capture and `catch return` are deferred too.
            case Zig.CatchOp c:
            {
                var union = LowerExpr(c.Arg0);
                if (union.Type.Unqualified is not CType.ErrorUnion eu)
                {
                    throw new IrUnsupportedException("zig `catch` requires an error-union left operand");
                }
                var fallback = LowerExpr(c.Arg2);
                if (!IsSimpleReeval(fallback))
                {
                    throw new IrUnsupportedException(
                        "zig `catch` with a side-effecting fallback not lowered yet (only a literal / variable fallback; `catch |e| …` capture and `catch return` are deferred)");
                }
                return new ZigCatch(union, fallback) { Type = eu.Payload };
            }

            // A bare `error.Foo` value (type `anyerror`) — only `return error.Foo;` is lowered
            // (handled in LowerReturn); a bare error value elsewhere needs error-set modelling.
            case Zig.ErrorLit:
                throw new IrUnsupportedException(
                    "zig bare `error.X` value not lowered yet (only `return error.X;` in a `!T` function)");

            // call of a named function (bare-identifier callee).
            case Zig.CallArgs c:   return LowerCall(c.Arg0, c.Arg2);
            case Zig.CallNoArgs c: return LowerCall(c.Arg0, null);

            default: throw new IrUnsupportedException("zig expression: " + (expr.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>Lower a call. Two callee shapes: a bare identifier bound to a named function
    /// (free function or <c>extern</c>/libc) → <see cref="BuildCall"/>; a <c>base.name(args)</c>
    /// field callee → <see cref="LowerMethodCall"/> (a UFCS instance method or a static/associated
    /// function). An indirect / function-pointer callee is still deferred.</summary>
    private CExpr LowerCall(Item calleeItem, Item? argListItem)
    {
        var argItems = argListItem is null ? new List<Item>() : Flatten(argListItem);

        // `base.method(args)` — a method (UFCS) or associated-function call.
        if (calleeItem.Content is Zig.Field fld)
        {
            return LowerMethodCall(fld, argItems);
        }

        if (calleeItem.Content is not Zig.Ident id)
        {
            throw new IrUnsupportedException("zig call: only a bare-identifier or `base.method` callee is lowered yet (got "
                + (calleeItem.Content?.GetType().Name ?? "null") + ")");
        }
        var name = Tok(id.Arg0);
        var sym = _symbols.Resolve(name)
            ?? throw new IrUnsupportedException($"call to unresolved name '{name}'");
        if (sym.Type.Unqualified is not CType.Func)
        {
            throw new IrUnsupportedException($"'{name}' is not a function (indirect / fn-ptr calls deferred)");
        }
        return BuildCall(sym, argItems, receiver: null);
    }

    /// <summary>Build an IR <see cref="Call"/> to a resolved function symbol, optionally with a
    /// synthesized leading <paramref name="receiver"/> argument (an instance method's <c>self</c>).
    /// Carries the callee's parameter types (so the backend coerces each argument as C does at a
    /// call) and the symbol (so it emits the legalized target name); an <c>extern</c>/libc symbol
    /// (<see cref="Symbol.FromSystemHeader"/>) drops the symbol so the call binds to dotcc's
    /// <c>Libc</c> runtime by bare name. Each fixed argument's parameter type is its sink (Zig
    /// result-locates call arguments), accounting for the receiver's parameter slot.</summary>
    private CExpr BuildCall(Symbol sym, IReadOnlyList<Item> argItems, CExpr? receiver)
    {
        var fn = (CType.Func)sym.Type.Unqualified;
        var args = new List<CExpr>(argItems.Count + 1);
        var paramOffset = 0;
        if (receiver is not null) { args.Add(receiver); paramOffset = 1; }

        // Each fixed argument's parameter type is its sink (Zig result-locates a call argument),
        // so `f(.member)` / `f(.{…})` resolve against the parameter. The receiver, if any, has
        // already consumed parameter slot 0. A variadic tail argument has no fixed parameter
        // type → no sink (plain LowerExpr).
        for (var i = 0; i < argItems.Count; i++)
        {
            var pIndex = i + paramOffset;
            var paramSink = pIndex < fn.Params.Count ? fn.Params[pIndex] : null;
            args.Add(LowerExprSink(argItems[i], paramSink));
        }

        // A variadic callee (printf) needs AT LEAST the fixed params; the rest are the variadic
        // tail. A fixed-arity callee needs an exact match.
        var arityOk = fn.Variadic ? args.Count >= fn.Params.Count : args.Count == fn.Params.Count;
        if (!arityOk)
        {
            throw new IrUnsupportedException(
                $"call to '{sym.Name}': expected {(fn.Variadic ? "at least " : "")}{fn.Params.Count} argument(s), got {args.Count}");
        }
        // Zig parity (the differential oracle caught dotcc being too lenient here): an untyped
        // comptime numeric literal has no fixed-size ABI type, so Zig forbids passing it to a
        // C-variadic — `printf("%d", 42)` is an error, `@as(c_int, 42)` is required. The variadic
        // tail begins at argItems index `fn.Params.Count - paramOffset`. (Methods are never
        // variadic, so paramOffset is 0 whenever this branch runs.)
        if (fn.Variadic)
        {
            for (var k = fn.Params.Count - paramOffset; k < argItems.Count; k++)
            {
                if (IsComptimeUntypedNumeric(argItems[k]))
                {
                    _ir.Diagnostics.Add(new Diagnostic(Severity.Error,
                        "integer and float literals passed to variadic function must be casted to a fixed-size number type",
                        SrcPos.From(argItems[k])));
                }
            }
        }

        // An extern/libc function (FromSystemHeader) renders by its bare name — no CalleeSym —
        // so it binds to dotcc's Libc runtime (and printf/scanf hit the fluent builder), exactly
        // as a C program's libc call does. A user Zig function (or method) carries its symbol so
        // the (possibly legalized / mangled) target name is used.
        var calleeSym = sym.FromSystemHeader ? null : sym;
        return new Call(sym.Name, args, fn.Params, calleeSym) { Type = fn.Return };
    }

    /// <summary>Lower a <c>base.name(args)</c> call. Two shapes: (A) a STATIC / associated call
    /// <c>Type.func(args)</c> — the base is a bare identifier naming a registered struct (and is
    /// NOT a variable in scope) — every argument is explicit, no receiver; (B) an INSTANCE call
    /// <c>expr.method(args)</c> — the base value is the receiver, adjusted (Zig UFCS auto-ref/
    /// deref) to the method's declared first-parameter form. Both rewrite to the mangled free
    /// function <c>TypeName_method</c> recorded in <see cref="_methods"/>.</summary>
    private CExpr LowerMethodCall(Zig.Field fld, IReadOnlyList<Item> argItems)
    {
        var methodName = Tok(fld.Arg2);

        // --- Zig allocators (Milestone F), before the generic method dispatch ---
        // `std.heap.FixedBufferAllocator.init(buf)` — a static call on the std FBA type.
        if (methodName == "init" && TryResolveStdPath(fld.Arg0, out var basePath) && basePath == "std.heap.FixedBufferAllocator")
        {
            return LowerFbaInit(argItems);
        }
        // `a.alloc(T, n)` / `a.free(s)` (and the deferred `create`/`destroy`) on a known-default
        // (→ devirt) or an Allocator-typed receiver (→ indirect). A same-named method on a
        // non-allocator receiver falls through to the generic dispatch below.
        if (methodName is "alloc" or "free" or "create" or "destroy"
            && TryLowerAllocatorMethod(fld, methodName, argItems, out var allocExpr))
        {
            return allocExpr;
        }

        // (A) `Type.func(args)` — a bare-identifier base naming a registered container type (a
        // struct/union `Named`, an enum `Enum`, or the self alias `Self`), and NOT a variable →
        // the associated/static function; all arguments are explicit (no receiver). A self alias
        // maps to the real container name so the method lookup and the mangled target match the
        // explicit-name form. (An `EnumName.member` non-call resolves to a tag constant earlier, in
        // the Zig.Field case; this branch only fires for a CALL whose method is a function.)
        if (fld.Arg0.Content is Zig.Ident bid
            && TryLookupContainerType(Tok(bid.Arg0), out var baseTy)
            && ContainerTypeName(baseTy) is { } typeName
            && _symbols.Resolve(Tok(bid.Arg0)) is null)
        {
            if (!_methods.TryGetValue(typeName, out var byType) || !byType.TryGetValue(methodName, out var staticSym))
            {
                throw new IrUnsupportedException($"'{typeName}' has no function '{methodName}'");
            }
            return BuildCall(staticSym, argItems, receiver: null);
        }

        // (B) `expr.method(args)` — the base is an instance of a container type.
        var recv = LowerExpr(fld.Arg0);
        // `fba.allocator()` — a FixedBufferAllocator hands out an Allocator fat pointer over
        // itself (Milestone F). Needs `&fba` as the vtable context; the result is opaque (→ the
        // indirect dispatch path). Handled before the generic _methods lookup (FBA is a runtime
        // type, not a Zig-declared container).
        if (methodName == "allocator" && recv.Type.Unqualified is CType.Named { Name: FbaTypeName })
        {
            if (argItems.Count != 0)
            {
                throw new IrUnsupportedException("zig `fba.allocator()` takes no arguments");
            }
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
            {
                s.AddressTaken = true;
            }
            var fbaAddr = new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
            return new Call("ZigAlloc.FbaAllocator", new List<CExpr> { fbaAddr },
                new List<CType> { new CType.Pointer(new CType.Named(FbaTypeName)) }, null) { Type = new CType.Allocator() };
        }
        if (!TryContainerName(recv.Type, out var container))
        {
            throw new IrUnsupportedException(
                $"zig method call `.{methodName}()` needs a struct (or pointer-to-struct) receiver, got {recv.Type.Describe()}");
        }
        if (!_methods.TryGetValue(container, out var methods) || !methods.TryGetValue(methodName, out var msym))
        {
            throw new IrUnsupportedException($"struct '{container}' has no method '{methodName}'");
        }
        var mfn = (CType.Func)msym.Type.Unqualified;
        if (mfn.Params.Count == 0)
        {
            throw new IrUnsupportedException(
                $"'{container}.{methodName}' takes no parameters — call it as `{container}.{methodName}(…)`, not on an instance");
        }
        var receiver = AdjustReceiver(recv, mfn.Params[0]);
        return BuildCall(msym, argItems, receiver);
    }

    /// <summary>Try to lower an allocator method call <c>a.alloc(T, n)</c> / <c>a.free(s)</c>
    /// (Milestone F). The receiver is DEVIRTUALIZED to a direct <c>Libc.malloc</c>/<c>free</c> when
    /// it is provably the statically-known C-heap default (<see cref="TryKnownAllocatorKind"/>);
    /// otherwise the receiver is lowered and, if it is an <see cref="CType.Allocator"/>, dispatched
    /// indirectly through its vtable. Returns <c>false</c> (so the caller falls through to the
    /// generic method dispatch) when the receiver is neither — i.e. a same-named method on a
    /// non-allocator type. <c>create</c>/<c>destroy</c> on an allocator are a clear deferred error.</summary>
    private bool TryLowerAllocatorMethod(Zig.Field fld, string methodName, IReadOnlyList<Item> argItems, out CExpr result)
    {
        result = null!;
        bool devirt = TryKnownAllocatorKind(fld.Arg0, out _);
        CExpr? recv = null;
        if (!devirt)
        {
            recv = LowerExpr(fld.Arg0);
            if (recv.Type.Unqualified is not CType.Allocator) { return false; }   // not an allocator → generic dispatch
        }

        switch (methodName)
        {
            case "alloc":
            {
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException($"zig allocator `.alloc` expects (type, count); got {argItems.Count} argument(s)");
                }
                var elem = LowerType(argItems[0]);
                var count = LowerExpr(argItems[1]);
                result = new AllocCall(devirt ? null : recv, elem, count, ErrorCode("OutOfMemory"))
                {
                    Type = new CType.ErrorUnion(new CType.Slice(elem)),
                };
                return true;
            }
            case "free":
            {
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig allocator `.free` expects (slice); got {argItems.Count} argument(s)");
                }
                var sliceExpr = LowerExpr(argItems[0]);
                if (sliceExpr.Type.Unqualified is not CType.Slice slc)
                {
                    throw new IrUnsupportedException($"zig allocator `.free` expects a slice argument, got {sliceExpr.Type.Describe()}");
                }
                result = new FreeCall(devirt ? null : recv, sliceExpr, slc.Element) { Type = CType.Void };
                return true;
            }
            default:   // create / destroy — single-object alloc
                throw new IrUnsupportedException(
                    $"zig allocator `.{methodName}` is deferred (single-object alloc needs an error-union-over-pointer); use `.alloc`/`.free`");
        }
    }

    /// <summary>Lower <c>std.heap.FixedBufferAllocator.init(&amp;buf)</c> (Milestone F) to
    /// <c>FixedBufferAllocator.Init(bytePtr, capacity)</c>. <paramref name="argItems"/> is the
    /// single <c>&amp;buf</c> argument, where <c>buf</c> is a <c>[N]T</c> array local — which
    /// already lowers to a stackalloc'd pointer, so the buffer pointer is the array value itself
    /// (cast to <c>byte*</c>) and the capacity is <c>N * sizeof(T)</c> bytes.</summary>
    private CExpr LowerFbaInit(IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != 1)
        {
            throw new IrUnsupportedException($"zig `FixedBufferAllocator.init` expects (buffer); got {argItems.Count} argument(s)");
        }
        // Accept `&buf` (the idiom) or a bare `buf`; either way the array local is the byte run.
        var inner = argItems[0].Content is Zig.PreAddrOf ad ? ad.Arg1 : argItems[0];
        var buf = LowerExpr(inner);
        if (buf.Type.Unqualified is not CType.Array a || a.Count is not int n)
        {
            throw new IrUnsupportedException(
                "zig `FixedBufferAllocator.init` expects `&buf` where buf is a fixed-size `[N]T` array local");
        }
        var bytePtr = new Cast(new CType.Pointer(CType.UChar), buf) { Type = new CType.Pointer(CType.UChar) };
        long bytes = (long)n * a.Element.Unqualified.SizeOf;
        var cap = new LitInt(bytes.ToString(CultureInfo.InvariantCulture), bytes) { Type = CType.ULong };
        return new Call("FixedBufferAllocator.Init", new List<CExpr> { bytePtr, cap },
            new List<CType> { new CType.Pointer(CType.UChar), CType.ULong }, null) { Type = new CType.Named(FbaTypeName) };
    }

    /// <summary>Resolve the container name a receiver expression's type names — a
    /// <see cref="CType.Named"/> struct/union or a <see cref="CType.Enum"/>, as a value or a pointer
    /// to one (<c>Point</c> / <c>*Point</c> / <c>Color</c> / <c>*Color</c>) — for instance-method
    /// dispatch.</summary>
    private static bool TryContainerName(CType t, out string name)
    {
        var u = t.Unqualified;
        if (u is CType.Pointer p) { u = p.Pointee.Unqualified; }
        switch (u)
        {
            case CType.Named n: name = n.Name; return true;
            case CType.Enum e:  name = e.Name; return true;
            default: name = ""; return false;
        }
    }

    /// <summary>The container name a type names for static-call (<c>Type.func(…)</c>) dispatch — a
    /// struct/union (<see cref="CType.Named"/>) or an enum (<see cref="CType.Enum"/>); null for any
    /// other type.</summary>
    private static string? ContainerTypeName(CType t) => t.Unqualified switch
    {
        CType.Named n => n.Name,
        CType.Enum e  => e.Name,
        _ => null,
    };

    /// <summary>Adjust an instance-method receiver to the method's declared first-parameter form
    /// (Zig UFCS auto-ref/deref): a value receiver to a <c>*Self</c> method takes its address (a
    /// var/param operand is marked address-taken; a non-lvalue is materialized to a temp by the
    /// backend's <c>&amp;rvalue</c> rule); a pointer receiver to a value-<c>Self</c> method is
    /// dereferenced; matching forms (both pointer or both value) pass through unchanged.</summary>
    private static CExpr AdjustReceiver(CExpr recv, CType paramType)
    {
        var paramIsPtr = paramType.Unqualified is CType.Pointer;
        var recvIsPtr = recv.Type.Unqualified is CType.Pointer;
        if (paramIsPtr && !recvIsPtr)
        {
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
            {
                s.AddressTaken = true;
            }
            return new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
        }
        if (!paramIsPtr && recvIsPtr)
        {
            var pointee = ((CType.Pointer)recv.Type.Unqualified).Pointee;
            return new Unary(UnOp.Deref, recv) { Type = pointee, IsLValue = true };
        }
        return recv;
    }

    /// <summary>Lower a binary op, synthesizing the result type the way the C# backend
    /// will treat it: usual-arithmetic for arithmetic/bitwise, the promoted left type
    /// for a shift (operands promote independently), and <c>int</c> for a relational /
    /// boolean (the backend renders those as an integer-valued <c>(CBool)(…)</c>).</summary>
    private CExpr Bin(BinOp op, Item l, Item r)
    {
        // `==` / `!=` may compare an enum value against a bare `.member` literal (`self == .red`),
        // which Zig result-locates against the other operand's enum type — so those two operands
        // get the enum-aware lowering; everything else lowers both sides plainly.
        var (left, right) = op is BinOp.Eq or BinOp.Ne
            ? LowerComparisonOperands(l, r)
            : (LowerExpr(l), LowerExpr(r));
        var type = op switch
        {
            BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge
                or BinOp.LogAnd or BinOp.LogOr => CType.Int,
            BinOp.Shl or BinOp.Shr => CType.IntegerPromote(left.Type),
            _ => CType.UsualArithmetic(left.Type, right.Type),
        };
        return new Binary(op, left, right) { Type = type };
    }

    /// <summary>Lower the two operands of an <c>==</c> / <c>!=</c> comparison, result-locating a
    /// bare enum literal <c>.member</c> against the OTHER operand's enum type — Zig's
    /// <c>self == .red</c> (the idiomatic enum-method test). The concrete side is lowered first; if
    /// it is enum-typed, the <c>.member</c> resolves to that enum's tag constant. When neither side
    /// is a bare <c>.member</c> (or the concrete side isn't an enum), both lower normally — so a
    /// bare literal with no enum partner still hits <see cref="LowerExpr"/>'s loud rejection.</summary>
    private (CExpr left, CExpr right) LowerComparisonOperands(Item l, Item r)
    {
        if (r.Content is Zig.EnumLit rel && l.Content is not Zig.EnumLit)
        {
            var left = LowerExpr(l);
            return left.Type.Unqualified is CType.Enum en
                ? (left, ResolveEnumLit(Tok(rel.Arg1), en))
                : (left, LowerExpr(r));
        }
        if (l.Content is Zig.EnumLit lel && r.Content is not Zig.EnumLit)
        {
            var right = LowerExpr(r);
            return right.Type.Unqualified is CType.Enum en
                ? (ResolveEnumLit(Tok(lel.Arg1), en), right)
                : (LowerExpr(l), right);
        }
        return (LowerExpr(l), LowerExpr(r));
    }

    /// <summary>Lower a value-prefix unary op. <c>!x</c> yields an int (the backend
    /// renders it 0/1); <c>-x</c>/<c>~x</c> take the integer-promoted operand type.</summary>
    private CExpr Pre(UnOp op, Item operandItem)
    {
        var operand = LowerExpr(operandItem);
        var type = op == UnOp.LogNot ? CType.Int : CType.IntegerPromote(operand.Type);
        return new Unary(op, operand) { Type = type };
    }

    /// <summary>Peel redundant <see cref="Paren"/> wrappers to reach the inner expr
    /// (so `&(x)` still marks `x` AddressTaken). Mirrors <c>IrBuilder.Unparen</c>.</summary>
    private static CExpr Unparen(CExpr e) => e is Paren p ? Unparen(p.Inner) : e;

    /// <summary>True for an expression with no side effects, safe to render more than once
    /// — the pointer <c>orelse</c> lowers to <c>a != null ? a : b</c>, naming <c>a</c>
    /// twice. Conservative: a var/param read, a literal, <c>null</c>, or a parenthesized
    /// such; anything else (a call, an assignment) is rejected to avoid double evaluation.</summary>
    private static bool IsSimpleReeval(CExpr e) => e switch
    {
        VarRef or NullPtr or LitInt or LitFloat => true,
        Paren p => IsSimpleReeval(p.Inner),
        _ => false,
    };

    /// <summary>True if the expression is a comptime-only numeric value — an int/float
    /// literal, or arithmetic over such (Zig's <c>comptime_int</c>/<c>comptime_float</c>).
    /// These have no fixed-size ABI type, so Zig forbids passing them to a C-variadic.
    /// The moment a concrete-typed leaf appears (identifier, call, <c>@as</c>, deref,
    /// index) the expression is typed and allowed across the variadic boundary.</summary>
    private static bool IsComptimeUntypedNumeric(Item it) => it.Content switch
    {
        Zig.IntLit or Zig.FloatLit => true,
        Zig.Grouped g   => IsComptimeUntypedNumeric(g.Arg1),
        Zig.PreNeg p    => IsComptimeUntypedNumeric(p.Arg1),
        Zig.PreBitNot p => IsComptimeUntypedNumeric(p.Arg1),
        Zig.Add a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Sub a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Mul a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.DivOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.ModOp a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitAnd a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitXor a => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.BitOr a  => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shl a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        Zig.Shr a    => IsComptimeUntypedNumeric(a.Arg0) && IsComptimeUntypedNumeric(a.Arg2),
        _ => false,
    };

    // ---- types -----------------------------------------------------------

    // ---- allocators / std (Milestone F) ----------------------------------

    /// <summary>Try to record a comptime <c>const</c> binding that carries no runtime value
    /// (Milestone F): <c>const X = @import("std");</c> registers a module alias in
    /// <see cref="_imports"/>; <c>const a = std.heap.page_allocator;</c> (or a const bound to
    /// another default binding) registers a known-default allocator in
    /// <see cref="_defaultAllocatorBindings"/>. Both emit NO decl (returns <c>true</c> → the
    /// caller drops the statement). A non-comptime RHS returns <c>false</c> (a normal decl). Only
    /// <c>const</c> bindings reach here. A non-<c>std</c> module errors clearly.</summary>
    private bool TryComptimeConstBinding(string name, Item rhs)
    {
        if (rhs.Content is Zig.BuiltinCall b && Tok(b.Arg0) == "@import")
        {
            var bargs = Flatten(b.Arg2);
            if (bargs.Count == 1 && bargs[0].Content is Zig.StrLit sl)
            {
                var module = UnquoteStringLiteral(Tok(sl.Arg0));
                if (module != "std")
                {
                    throw new IrUnsupportedException(
                        $"zig `@import(\"{module}\")` is not modeled — only `@import(\"std\")` (and only its allocator paths: std.mem.Allocator, std.heap.page_allocator/c_allocator/FixedBufferAllocator)");
                }
                _imports[name] = module;
                return true;
            }
        }
        if (TryKnownAllocatorKind(rhs, out var kind))
        {
            _defaultAllocatorBindings[name] = kind;
            return true;
        }
        return false;
    }

    /// <summary>Walk a dotted access chain (<see cref="Zig.Field"/> over a <see cref="Zig.Ident"/>
    /// root) rooted at a module-import alias, returning the canonical dotted path with the MODULE
    /// name as its root (e.g. <c>"std.heap.page_allocator"</c>) regardless of the alias spelling.
    /// Works in both expression and type position (same AST shape). Returns <c>false</c> for any
    /// chain not rooted at an <see cref="_imports"/> alias.</summary>
    private bool TryResolveStdPath(Item expr, out string path)
    {
        path = "";
        var segments = new List<string>();
        var cur = expr;
        while (cur.Content is Zig.Field f)
        {
            segments.Add(Tok(f.Arg2));
            cur = f.Arg0;
        }
        if (cur.Content is not Zig.Ident id || !_imports.TryGetValue(Tok(id.Arg0), out var module))
        {
            return false;
        }
        segments.Add(module);
        segments.Reverse();
        path = string.Join(".", segments);
        return true;
    }

    /// <summary>True when <paramref name="expr"/> is provably the statically-known default
    /// allocator — a <c>const</c> bound to it (<see cref="_defaultAllocatorBindings"/>), or a
    /// direct <c>std.heap.page_allocator</c> / <c>std.heap.c_allocator</c> path. This is the
    /// devirtualization predicate; an opaque parameter or an <c>fba.allocator()</c> result is NOT
    /// provable here (→ indirect dispatch).</summary>
    private bool TryKnownAllocatorKind(Item expr, out AllocKind kind)
    {
        if (expr.Content is Zig.Ident id && _defaultAllocatorBindings.TryGetValue(Tok(id.Arg0), out kind))
        {
            return true;
        }
        if (TryResolveStdPath(expr, out var path) && (path is "std.heap.page_allocator" or "std.heap.c_allocator"))
        {
            kind = AllocKind.CHeap;
            return true;
        }
        kind = AllocKind.CHeap;
        return false;
    }

    /// <summary>True when <paramref name="name"/> is a comptime allocator/namespace binding
    /// recorded by <see cref="TryComptimeConstBinding"/> (a module import or a known-default
    /// allocator) — so pass 1 skips its (non-existent) top-level decl.</summary>
    private bool IsComptimeBound(string name)
        => _imports.ContainsKey(name) || _defaultAllocatorBindings.ContainsKey(name);

    /// <summary>Strip the surrounding double quotes from a Zig string-literal lexeme. Used only
    /// for the simple identifier-shaped module name in <c>@import("…")</c> (no escapes).</summary>
    private static string UnquoteStringLiteral(string raw)
        => raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;

    /// <summary>The materialized C-heap default allocator as a runtime <see cref="CType.Allocator"/>
    /// value (<c>ZigAlloc.CHeap()</c>) — emitted when the statically-known default flows into an
    /// opaque allocator sink (a value position, not a devirtualizable <c>.alloc</c> receiver).</summary>
    private static CExpr MaterializeCHeap()
        => new Call("ZigAlloc.CHeap", new List<CExpr>(), new List<CType>(), null) { Type = new CType.Allocator() };

    private CType LowerType(Item type) => type.Content switch
    {
        Zig.Ident id => LowerTypeName(Tok(id.Arg0)),
        // A dotted std type (Milestone F): `std.mem.Allocator` → the runtime Allocator fat
        // pointer; `std.heap.FixedBufferAllocator` → the concrete bump allocator. Any other std
        // path in type position errors clearly (`std` is a known-paths resolver, not a real model).
        Zig.Field => LowerStdType(type),
        // Pointer types. `*T` and the C-pointer `[*c]T` both lower to a plain
        // `T*` (the C-pointer's null/arithmetic semantics ARE C's pointer). The
        // pointee `const` rides as a TypeQual so const-correctness sees it; it
        // doesn't change the C# spelling (`[*c]const u8` and `[*c]u8` are both
        // `byte*`). `[*c]const u8` is exactly the type of printf's format param.
        Zig.TyPointer p    => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyPtrConst p   => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TyCPtr p       => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyCPtrConst p  => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        // `?T` optional. An optional POINTER `?*T` lowers to a bare nullable `T*` (Zig's
        // own niche — null = none, zero cost; a non-optional `*T` loses its non-null
        // guarantee, a documented leniency). A `?T` over a value type lowers to C#
        // Nullable<T> via CType.Optional, so `null`/`.?`/`orelse` map to C#'s built-ins.
        Zig.TyOptional opt => LowerOptional(opt.Arg1),
        // `E!T` error-union type → CType.ErrorUnion(T). V1 erases the error SET (Arg0, the
        // Suffix naming the set), so `anyerror!T` and a named `E!T` lower identically — the
        // payload is what the backend renders (`ErrUnion<T>`). See [[CType.ErrorUnion]].
        Zig.ErrUnion eu => new CType.ErrorUnion(LowerType(eu.Arg2)),
        // `[]T` / `[]const T` slice → CType.Slice (the runtime Slice<T> / ConstSlice<T> fat
        // pointer). `[]const T` carries the `const` on the element, so the backend renders it
        // as `ConstSlice<T>` — element-only const, like the pointer forms above. See
        // [[CType.Slice]]. (`[N]T` arrays and `[*]T` many-pointers are still deferred.)
        Zig.TySlice s      => new CType.Slice(LowerType(s.Arg2)),
        Zig.TySliceConst s => new CType.Slice(LowerType(s.Arg3).WithQuals(TypeQual.Const)),
        // `[N]T` fixed-size array → CType.Array(element, N). N must be an integer literal
        // (a general comptime const-expr size is deferred). A `var b: [N]T` local lowers to a
        // stackalloc'd C array (see DeclOf), so slicing it (`b[lo..hi]`) yields a stack-backed slice.
        Zig.TyArray a => new CType.Array(LowerType(a.Arg3), ConstEvalArraySize(a.Arg1)),
        // Tuple TYPE `struct { T1, T2, … }` (Milestone G) → CType.Tuple → C# System.ValueTuple<…>.
        // Used as a function return type or a var/param annotation; nested tuple types compose.
        Zig.TyTuple t => LowerTupleType(t.Arg2),
        // `@This()` — Zig's reflective self-type → the container currently being lowered, so
        // `self: @This()` / `self: *@This()` name the receiver without repeating the type name.
        // The `const Self = @This();` alias form (the common Zig idiom) is also supported — it
        // registers a container-scoped type alias (see RegisterContainerConsts / ResolveSelfAlias)
        // so `Self` resolves here through LowerTypeName.
        Zig.BuiltinCallNoArgs b when Tok(b.Arg0) == "@This" => CurrentContainerType(),
        _ => throw new IrUnsupportedException("zig type: " + (type.Content?.GetType().Name ?? "null")),
    };

    /// <summary>Lower a dotted std type (Milestone F): <c>std.mem.Allocator</c> → the runtime
    /// <see cref="CType.Allocator"/> fat pointer; <c>std.heap.FixedBufferAllocator</c> → the
    /// concrete <see cref="CType.Named"/> bump allocator. Any other dotted type errors — either a
    /// chain not rooted at a std import (so not a known type at all) or an unmodeled std path.</summary>
    private CType LowerStdType(Item f)
    {
        if (TryResolveStdPath(f, out var path))
        {
            return path switch
            {
                "std.mem.Allocator" => new CType.Allocator(),
                "std.heap.FixedBufferAllocator" => new CType.Named(FbaTypeName),
                _ => throw new IrUnsupportedException(
                    $"zig type `{path}` is not modeled (std types: std.mem.Allocator, std.heap.FixedBufferAllocator)"),
            };
        }
        throw new IrUnsupportedException(
            $"zig type: a dotted type `{Tok(((Zig.Field)f.Content!).Arg2)}` that is not a modeled std path");
    }

    /// <summary>Lower a tuple TYPE body (the <c>T1, T2, …</c> inside <c>struct { … }</c> at a Type
    /// position) to a <see cref="CType.Tuple"/>. V1 supports arity 1..7 (an empty tuple and
    /// arity &gt; 7 — which would need ValueTuple's <c>TRest</c> nesting — are deferred with a clear
    /// error). Each element is itself a <see cref="LowerType"/>, so nested tuple types compose.</summary>
    private CType LowerTupleType(Item tupleTypes)
    {
        var elems = Flatten(tupleTypes).Select(LowerType).ToList();
        if (elems.Count is 0 or > 7)
        {
            throw new IrUnsupportedException(
                $"zig tuple type arity {elems.Count} is not supported (1..7; an empty tuple and arity > 7 are deferred)");
        }
        return new CType.Tuple(elems);
    }

    /// <summary>The type the enclosing container's <c>@This()</c> resolves to — the struct/enum
    /// whose method is currently being lowered. An error outside a method (no container in
    /// scope).</summary>
    private CType CurrentContainerType() =>
        _currentContainer is { } c && _containerTypes.TryGetValue(c, out var t)
            ? t
            : throw new IrUnsupportedException("zig `@This()` is only supported inside a container method");

    /// <summary>Resolve a Zig type spelled as a bare identifier: a container-scoped self alias
    /// (<c>const Self = @This();</c>) wins first, then a registered container (struct →
    /// <see cref="CType.Named"/>, enum → <see cref="CType.Enum"/>), then the primitive table — so a
    /// user type name (or a self alias inside its own method) resolves before <see cref="LowerPrim"/>
    /// would throw on it.</summary>
    private CType LowerTypeName(string name) =>
        ResolveSelfAlias(name)
        ?? (_containerTypes.TryGetValue(name, out var ct) ? ct : LowerPrim(name));

    /// <summary>Resolve a type name that is a container-scoped self alias (<c>const Self =
    /// @This();</c>), valid only while a method of the declaring container is being lowered
    /// (<see cref="_currentContainer"/> set). Returns <c>null</c> when it is not such an alias.</summary>
    private CType? ResolveSelfAlias(string name) =>
        _currentContainer is { } c
        && _selfAliases.TryGetValue(c, out var m)
        && m.TryGetValue(name, out var t)
            ? t : null;

    /// <summary>Look up the container type named at a use site — a registered struct/enum/union
    /// name, or a container-scoped self alias (<c>Self</c>) when inside that container's method.
    /// Drives <c>Type.func()</c> / <c>EnumName.member</c> resolution (a self alias maps through to
    /// the real container type, so <c>Self.init(…)</c> binds to the same mangled method as the
    /// explicit name).</summary>
    private bool TryLookupContainerType(string name, out CType type)
    {
        var alias = ResolveSelfAlias(name);
        if (alias is not null) { type = alias; return true; }
        return _containerTypes.TryGetValue(name, out type!);
    }

    /// <summary>Lower a Zig optional payload type: a pointer payload stays a bare nullable
    /// pointer (the niche); any other payload is wrapped in <see cref="CType.Optional"/>
    /// (→ C# <c>T?</c>).</summary>
    private CType LowerOptional(Item innerType)
    {
        var inner = LowerType(innerType);
        return inner.Unqualified is CType.Pointer ? inner : new CType.Optional(inner);
    }

    /// <summary>Const-evaluate a <c>[N]T</c> array size — an integer literal <c>N</c> (a general
    /// comptime const-expression size is deferred). Throws on a non-literal size.</summary>
    private int ConstEvalArraySize(Item sizeExpr) => sizeExpr.Content switch
    {
        Zig.IntLit i => (int)long.Parse(Tok(i.Arg0), CultureInfo.InvariantCulture),
        _ => throw new IrUnsupportedException("a `[N]T` array size must be an integer literal"),
    };

    /// <summary>Map a Zig primitive type name to its faithful C# lowering. The
    /// fixed-width integers carry real signedness (i8 → <c>sbyte</c>, u8 → <c>byte</c>,
    /// …), unlike the earlier slice that collapsed both 8-bit forms to <c>byte</c>.
    /// <c>usize</c>/<c>isize</c> map to the LP64 64-bit <c>size_t</c>/<c>long</c>
    /// (width-correct on dotcc's target; a dedicated pointer-width type is a later
    /// refinement). <c>comptime_int</c>/<c>comptime_float</c> and the bigger/arbitrary
    /// <c>iN</c>/<c>uN</c> widths are deferred.</summary>
    private static CType LowerPrim(string name) => name switch
    {
        "void" => CType.Void,
        "bool" => CType.Bool,
        "i8"  => CType.SChar,    // → C# sbyte
        "u8"  => CType.UChar,    // → C# byte
        "i16" => CType.Short,
        "u16" => CType.UShort,
        "i32" => CType.Int,
        "u32" => CType.UInt,
        "i64" => CType.Long,
        "u64" => CType.ULong,
        "isize" => CType.Long,   // LP64: pointer-width signed
        "usize" => CType.ULong,  // LP64: pointer-width unsigned (== size_t)
        "f32" => CType.Float,
        "f64" => CType.Double,
        // C-ABI types for `extern fn` libc FFI (LP64, matching dotcc's __LP64__ trio:
        // `c_long`/`c_ulong` are 8 bytes). These map onto the same well-known prims the
        // C frontend uses, so RenderType + the coercion tables already cover them.
        "c_char" => CType.Char,
        "c_short" => CType.Short,
        "c_ushort" => CType.UShort,
        "c_int" => CType.Int,
        "c_uint" => CType.UInt,
        "c_long" => CType.Long,
        "c_ulong" => CType.ULong,
        "c_longlong" => CType.LongLong,
        "c_ulonglong" => CType.ULongLong,
        _ => throw new IrUnsupportedException($"zig type '{name}' not supported yet (slice)"),
    };

    // ---- helpers ---------------------------------------------------------

    /// <summary>Flatten a left-recursive list spine (Decls / Stmts / Params / ArgList)
    /// into source order with an explicit stack — same anti-stack-overflow walk as
    /// <see cref="IrBuilder"/>'s <c>FlattenFns</c>. The cons/one node types are disjoint
    /// across the four list kinds, and the walk stops at the first non-list node (so a
    /// nested Block's own Stmts aren't pulled into the parent), so one method serves all
    /// four with no cross-contamination.</summary>
    private static List<Item> Flatten(Item it)
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
                case Zig.ArgsCons c:   stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Expr, ',', ArgList]
                case Zig.ArgsOne o:    stack.Push(o.Arg0); break;
                case Zig.ProngsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Prongs, ',', Prong] (left-recursive)
                case Zig.ProngsOne o:  stack.Push(o.Arg0); break;
                case Zig.CaseValsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [Expr, ',', CaseVals]
                case Zig.CaseValsOne o:  stack.Push(o.Arg0); break;
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
                case Zig.DestructBindsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [DestructBinds, ',', DestructBind]
                case Zig.DestructBindsOne o:  stack.Push(o.Arg0); break;
                default: ordered.Add(n); break;
            }
        }
        return ordered;
    }
}
