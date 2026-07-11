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

    /// <summary>True while lowering a function body that contains at least one <c>errdefer</c>
    /// (pre-scanned in <see cref="LowerFnBody"/>). When set, a <c>return error.X;</c> is routed
    /// through a thrown <see cref="ZigErrorThrow"/> rather than a direct <see cref="ErrUnionErr"/>
    /// return, so the error propagates through the <c>errdefer</c> <c>catch</c>(es) on the stack
    /// (Milestone H). A function with no <c>errdefer</c> keeps the direct-return form untouched.</summary>
    private bool _currentFnHasErrdefer;

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

    /// <summary>The name of the function whose body is currently being lowered (pass 2) — the prefix
    /// for an in-function container's mangled IR type name (wall-plan W2, <c>&lt;fn&gt;__&lt;P&gt;</c>).
    /// Set/cleared around each body in <see cref="LowerFnBody"/>.</summary>
    private string _currentFnName = "";

    /// <summary>Mangled IR type names of every in-function container registered so far (wall-plan W2)
    /// — the dup guard, since <see cref="IrBuilder.RegisterStructType"/> silently no-ops a repeat name
    /// (so a redeclared local would otherwise miscompile). Global for the build: a mangled name is
    /// unique per (function, container), and the IR type it names is emitted once program-wide.</summary>
    private readonly HashSet<string> _localContainers = new(System.StringComparer.Ordinal);

    /// <summary>Plain-name → previous <see cref="_containerTypes"/> binding shadowed by an in-function
    /// container in the CURRENT body (wall-plan W2), restored at body exit so a local <c>const Point =
    /// struct{…}</c> does NOT leak its type into a sibling function (the mangled IR type stays
    /// registered globally — this only scopes the plain-name alias). Reset per function.</summary>
    private readonly List<(string Name, CType? Prev)> _localContainerShadows = new();

    /// <summary>Alias-name → previous <see cref="_typeAliases"/> binding shadowed by a comptime-TYPE
    /// parameter seed while lowering a generic instance's signature / body (wall-plan W3b), restored so
    /// a type param <c>T ↦ i32</c> does not leak into the next drained instance / a sibling function,
    /// and a nested instantiation whose type param shares the name <c>T</c> does not clobber the outer
    /// one. The proven W2 shadow pattern (<see cref="_localContainerShadows"/>), applied to the
    /// function-flat type-alias map; reset per body / per instantiation signature.</summary>
    private readonly List<(string Name, CType? Prev)> _typeAliasShadows = new();

    /// <summary>Per container name, each <c>const Self = @This();</c> alias → the container's own
    /// type. A container-scoped self alias is the ubiquitous Zig idiom for naming the receiver type
    /// inside its methods without repeating the container name (any alias name works, not just
    /// <c>Self</c>). Populated in pass 0b (so a method signature can spell its receiver as the
    /// alias); consulted by <see cref="ResolveSelfAlias"/> only while a method of that container is
    /// being lowered (<see cref="_currentContainer"/> set), so the alias is genuinely scoped — two
    /// containers may each declare <c>const Self = @This();</c> without colliding. (A non-<c>@This()</c>
    /// value const — a namespaced constant — is not lowered yet; it needs top-level globals.)</summary>
    private readonly Dictionary<string, Dictionary<string, CType>> _selfAliases = new(System.StringComparer.Ordinal);

    /// <summary>Per <c>(struct name, field name)</c>, the RAW default-value AST of a
    /// <c>field: T = default</c> declaration (std S9). Stored unlowered and materialized lazily — a
    /// <c>.{…}</c> literal that OMITS the field appends <c>LowerExprSink(default, fieldType)</c>
    /// (see <see cref="BuildStructInit"/>), so a NON-ZERO default is honored (a zero default already
    /// matched C#'s zero-init). Keyed by the registered struct name, so a mangled in-function
    /// container (<c>&lt;fn&gt;__&lt;P&gt;</c>) and a top-level struct never collide.</summary>
    private readonly Dictionary<(string Struct, string Field), Item> _structFieldDefaults = new();

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
    /// resolved in a post-pass after pass 2 — when every function body is lowered, so a
    /// <c>comptime fib(10)</c> can interpret its callee regardless of declaration order. Each node is
    /// shared by reference in the IR; resolving it patches its <see cref="ComptimeFold.Resolved"/> in place.</summary>
    private readonly List<ComptimeFold> _pendingComptimeFolds = new();

    /// <summary>Lowering-time values of <c>comptime var</c> / <c>comptime const</c> locals (Milestone T,
    /// part 3 — the loop counter of an <c>inline while</c>). Keyed by Symbol IDENTITY (the same instance
    /// the symbol table hands every reference). A reference to one of these substitutes its CURRENT value
    /// as a literal during lowering (the <c>Zig.Ident</c> case), so the condition / continue-expression /
    /// body of an <c>inline while</c> fold and unroll. Updated as comptime mutations are processed in
    /// source order, matching Zig's sequential comptime semantics. No runtime decl is ever emitted.</summary>
    private readonly Dictionary<Symbol, (long Value, CType Type)> _comptimeVars = new();

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
    private sealed record ZigUnionInfo(
        string Name,                    // the outer discriminated-union struct name (`U`)
        CType.Enum TagType,             // the tag enum — auto-synthesized `U_Tag`, or a named `union(SomeEnum)` enum
        string TagFieldName,
        string? PayloadTypeName,        // the nested overlapping-payload union type (null if every variant is void)
        string PayloadFieldName,
        IReadOnlyDictionary<string, CType?> Variants);  // variant name → payload type (null = void)

    /// <summary>Registered tagged unions: the union struct name → its <see cref="ZigUnionInfo"/>.</summary>
    private readonly Dictionary<string, ZigUnionInfo> _unions = new(System.StringComparer.Ordinal);

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

    public ZigLowering(IrBuilder ir, INameLegalizer names, Dictionary<string, int>? errorCodes = null, bool testMode = false)
    {
        _ir = ir;
        _symbols = new SymbolTable(names);
        _errorCodes = errorCodes ?? new Dictionary<string, int>(System.StringComparer.Ordinal);
        _testMode = testMode;
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
                case Zig.StructDecl s:        _containerTypes[Tok(s.Arg1)] = new CType.Named(Tok(s.Arg1)); break;
                case Zig.StructDeclEmpty s:   _containerTypes[Tok(s.Arg1)] = new CType.Named(Tok(s.Arg1)); break;
                case Zig.ExternStructDecl s:  _containerTypes[Tok(s.Arg1)] = new CType.Named(Tok(s.Arg1)); break;  // const IDENT = extern struct { … } ;
                case Zig.PackedStructDecl s:  _containerTypes[Tok(s.Arg1)] = new CType.Named(Tok(s.Arg1)); break;  // const IDENT = packed struct { … } ;
                case Zig.EnumDecl e:        foreach (var m in RegisterEnumZig(e.Arg1, null, e.Arg5)) { containerMethods.Add((Tok(e.Arg1), m)); } break;       // const IDENT = enum { EnumFields } ;
                case Zig.EnumDeclTyped e:   foreach (var m in RegisterEnumZig(e.Arg1, e.Arg5, e.Arg8)) { containerMethods.Add((Tok(e.Arg1), m)); } break;     // const IDENT = enum ( Type ) { EnumFields } ;
                case Zig.UnionDeclEnum u:     _containerTypes[Tok(u.Arg1)] = new CType.Named(Tok(u.Arg1)); break;  // const IDENT = union(enum) { … } ;
                case Zig.UnionDeclTagged u:   _containerTypes[Tok(u.Arg1)] = new CType.Named(Tok(u.Arg1)); break;  // const IDENT = union(SomeEnum) { … } ;
                case Zig.UnionDeclUntagged u: _containerTypes[Tok(u.Arg1)] = new CType.Named(Tok(u.Arg1)); break;  // const IDENT = union { … } ;
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
                case Zig.ExternStructDecl s:  // const IDENT = extern struct { Members } ;
                {
                    var (fields, methods, consts) = SplitMembers(s.Arg6);
                    RegisterStruct(Tok(s.Arg1), fields, AggregateLayout.Sequential);
                    RegisterContainerConsts(Tok(s.Arg1), consts);
                    foreach (var m in methods) { containerMethods.Add((Tok(s.Arg1), m)); }
                    break;
                }
                case Zig.PackedStructDecl s:  // const IDENT = packed struct { Members } ;
                {
                    var (fields, methods, consts) = SplitMembers(s.Arg6);
                    RegisterStruct(Tok(s.Arg1), fields, AggregateLayout.Packed);
                    RegisterContainerConsts(Tok(s.Arg1), consts);
                    foreach (var m in methods) { containerMethods.Add((Tok(s.Arg1), m)); }
                    break;
                }
                case Zig.UnionDeclEnum u:   foreach (var m in RegisterUnion(Tok(u.Arg1), u.Arg8)) { containerMethods.Add((Tok(u.Arg1), m)); } break;  // const IDENT = union(enum) { UnionMembers } ;
                case Zig.UnionDeclTagged u: foreach (var m in RegisterUnionTagged(Tok(u.Arg1), Tok(u.Arg5), u.Arg8)) { containerMethods.Add((Tok(u.Arg1), m)); } break;  // const IDENT = union(SomeEnum) { UnionMembers } ;
                case Zig.UnionDeclUntagged u: foreach (var m in RegisterUnionUntagged(Tok(u.Arg1), u.Arg5)) { containerMethods.Add((Tok(u.Arg1), m)); } break;  // const IDENT = union { UnionMembers } ;
            }
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
                case Zig.FnDef f:          AddFnEntry(DeclareFn(f.Arg1, f.Arg3, f.Arg6, f.Arg7)); break;
                case Zig.FnDefNoArgs f:    AddFnEntry(DeclareFn(f.Arg1, null, f.Arg5, f.Arg6)); break;
                case Zig.FnDefErr f:       AddFnEntry(DeclareFn(f.Arg1, f.Arg3, f.Arg7, f.Arg8, errUnion: true)); break;   // `!T` return → ErrorUnion(T)
                case Zig.FnDefNoArgsErr f: AddFnEntry(DeclareFn(f.Arg1, null, f.Arg6, f.Arg7, errUnion: true)); break;
                // Container decls were handled in pass 0 — skip here.
                case Zig.StructDecl or Zig.StructDeclEmpty or Zig.ExternStructDecl or Zig.PackedStructDecl or Zig.EnumDecl or Zig.EnumDeclTyped or Zig.UnionDeclEnum or Zig.UnionDeclTagged or Zig.UnionDeclUntagged: break;
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
                // A top-level container FIELD means this file is being used as an instantiable
                // struct type (`@This()` at file scope). We PARSE it (road-to-zig-std S9, the largest
                // std parse bucket), but reifying the file-as-struct — a synthesized named type whose
                // members are these fields plus every sibling decl — is the S1 lift, not yet done.
                // Fail loudly so the gap is visible rather than silently mis-lowered.
                case Zig.TopField: throw new IrUnsupportedException(
                    "top-level container field (file-as-struct type) is not yet lowered (road-to-zig-std S1)");
                default: throw new IrUnsupportedException("zig top-level decl: " + (d.Content?.GetType().Name ?? "null"));
            }
        }
        foreach (var (container, fnDef) in containerMethods) { entries.Add(DeclareMethod(container, fnDef)); }

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

        // Pass 2: bodies. `_currentContainer` is set for a method body so its `@This()` resolves.
        foreach (var (sym, ps, body, container) in entries)
        {
            _currentContainer = container;
            LowerFnBody(sym, ps, body);
            _currentContainer = null;
        }

        // Pass 2.5 (wall-plan W3a): drain the monomorphization worklist. A generic call enqueued a
        // request during pass 2 (or during an earlier drained instance); each instance body lowers HERE,
        // at top level — never nested in another body's lowering — so the per-fn lowering state starts
        // clean (the re-entrancy-safe design the plan's audit demanded). A cursor loop (not a fixed
        // count) picks up transitive / recursive instantiations an instance body enqueues; the total is
        // bounded by MaxInstantiations (enforced at enqueue). Runs BEFORE the comptime-fold pass so a
        // `comptime EXPR` inside an instance body is resolved alongside the base-body folds below.
        for (var i = 0; i < _pendingInstantiations.Count; i++)
        {
            LowerInstantiationBody(_pendingInstantiations[i]);
        }

        // Pass 3 (Milestone T): resolve every deferred `comptime EXPR`. All function bodies are now
        // lowered (in `_ir.Functions`), so a comptime call can interpret its callee. Each fold is
        // evaluated by the shared comptime interpreter and patched in place with the spliced literal;
        // a non-constant `comptime` value is a loud error.
        foreach (var fold in _pendingComptimeFolds)
        {
            fold.Resolved = _ir.ResolveComptimeFold(fold.Inner)
                ?? throw new IrUnsupportedException(
                    "`comptime` expression did not evaluate to a compile-time constant value");
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
                case Zig.ConstDecl d      when !IsComptimeBound(Tok(d.Arg1)):
                    if (!TryComptimeConstBinding(Tok(d.Arg1), d.Arg3)) { LowerGlobal(d.Arg1, null, d.Arg3); }
                    break;  // const IDENT = RhsExpr ;
                case Zig.ConstDeclTyped d when !IsComptimeBound(Tok(d.Arg1)):
                    if (!TryComptimeConstBinding(Tok(d.Arg1), d.Arg5)) { LowerGlobal(d.Arg1, d.Arg3, d.Arg5); }
                    break;  // const IDENT : Type = RhsExpr ;
                case Zig.VarDecl d:        LowerGlobal(d.Arg1, null,   d.Arg3); break;  // var IDENT = RhsExpr ;
                case Zig.VarDeclTyped d:   LowerGlobal(d.Arg1, d.Arg3, d.Arg5); break;  // var IDENT : Type = RhsExpr ;
                // A typed global with align/linksection modifiers (Milestone R, part 5) — modifiers
                // ignored (no-op on the managed target); RhsExpr is one slot right of the Type.
                case Zig.ConstDeclTypedMods d: LowerGlobal(d.Arg1, d.Arg3, d.Arg6); break;  // const IDENT : Type DeclMods = RhsExpr ;
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
    /// is lowered at module scope, so it must be a constant / module-resolvable value.</summary>
    private void LowerGlobal(Item nameTok, Item? typeItem, Item rhsItem, bool threadLocal = false)
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
        // A labeled value-block initializes via runtime statements (a temp + control flow); a global
        // must be comptime-initialized, so it can't host one. Clear error (not the generic
        // expression-position one, which would read oddly for a global).
        if (rhsItem.Content is Zig.LabeledBlock)
        {
            throw new IrUnsupportedException(
                $"a labeled value-block can't initialize the global '{Tok(nameTok)}' (a global needs a comptime value)");
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
        var init = LowerExprSink(rhsItem, declared);
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
            AddArrayGlobal(Tok(nameTok), (CType.Array)sa.Type,
                new PinnedArray(sa.Element, sa.Elems, null) { Type = new CType.Pointer(sa.Element) });
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
        var sym = _symbols.Declare(new Symbol
        {
            Name = Tok(nameTok), Kind = SymKind.Var, Type = type, Storage = Storage.Static, IsGlobal = true,
            IsThreadLocal = threadLocal,
        });
        _ir.Globals.Add(new GlobalVar(sym, init));
    }

    /// <summary>Record a <c>[N]T</c> array global: an array-typed static symbol (so references
    /// resolve + <c>sizeof</c> is exact) backed by the pinned <paramref name="pinned"/> store
    /// (rendered as a stable <c>T*</c>). The symbol is declared after the initializer is lowered, so
    /// a literal element can reference an earlier global but never the array itself.</summary>
    private void AddArrayGlobal(string name, CType.Array arr, CExpr pinned)
    {
        var sym = _symbols.Declare(new Symbol
        {
            Name = name, Kind = SymKind.Var, Type = arr, Storage = Storage.Static, IsGlobal = true,
        });
        _ir.Globals.Add(new GlobalVar(sym, pinned));
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
        try
        {
            var sink = typeItem is not null ? LowerType(typeItem) : null;
            return LowerExprSink(rhs, sink);
        }
        finally
        {
            _currentConstContainer = prev;
            _constResolving.Remove(key);
        }
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
    private static Item Unwrap(Item decl) => decl.Content switch
    {
        Zig.PubFn p         => p.Arg1,   // `pub FnDef`
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
                case Zig.ParamsTrail t: stack.Push(t.Arg0); break;  // [Param, ','] trailing comma
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
                case Zig.FnTypeParamsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [FnTypeParam, ',', FnTypeParams] (right-recursive)
                case Zig.FnTypeParamsOne o:  stack.Push(o.Arg0); break;
                case Zig.FnTypeParamsTrail t: stack.Push(t.Arg0); break;  // [FnTypeParam, ','] trailing comma
                case Zig.DestructBindsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [DestructBinds, ',', DestructBind]
                case Zig.DestructBindsOne o:  stack.Push(o.Arg0); break;
                default: ordered.Add(n); break;
            }
        }
        return ordered;
    }
}
