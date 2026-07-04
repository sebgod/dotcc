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
                case Zig.FnDef f:          entries.Add(AsEntry(DeclareFn(f.Arg1, f.Arg3, f.Arg6, f.Arg7), null)); break;
                case Zig.FnDefNoArgs f:    entries.Add(AsEntry(DeclareFn(f.Arg1, null, f.Arg5, f.Arg6), null)); break;
                case Zig.FnDefErr f:       entries.Add(AsEntry(DeclareFn(f.Arg1, f.Arg3, f.Arg7, f.Arg8, errUnion: true), null)); break;   // `!T` return → ErrorUnion(T)
                case Zig.FnDefNoArgsErr f: entries.Add(AsEntry(DeclareFn(f.Arg1, null, f.Arg6, f.Arg7, errUnion: true), null)); break;
                // Container decls were handled in pass 0 — skip here.
                case Zig.StructDecl or Zig.StructDeclEmpty or Zig.ExternStructDecl or Zig.PackedStructDecl or Zig.EnumDecl or Zig.EnumDeclTyped or Zig.UnionDeclEnum or Zig.UnionDeclTagged or Zig.UnionDeclUntagged: break;
                // A top-level `const`/`var` is either a comptime binding (an `@import`/allocator
                // alias recorded in pass 0, which emits no decl) or a runtime global — both are
                // resolved by the global pass below (LowerTopLevelGlobals), so skip them here.
                case Zig.ConstDecl or Zig.ConstDeclTyped or Zig.VarDecl or Zig.VarDeclTyped
                  or Zig.ConstDeclTypedMods or Zig.VarDeclTypedMods or Zig.VarDeclThreadLocal: break;
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
                case Zig.ConstDecl d      when !IsComptimeBound(Tok(d.Arg1)): LowerGlobal(d.Arg1, null,   d.Arg3); break;  // const IDENT = RhsExpr ;
                case Zig.ConstDeclTyped d when !IsComptimeBound(Tok(d.Arg1)): LowerGlobal(d.Arg1, d.Arg3, d.Arg5); break;  // const IDENT : Type = RhsExpr ;
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
        // Stash the raw return-type AST so the body can resolve its declared error set in pass 2
        // (the set decls aren't processed until pass 1.5) for the foreign-error return check.
        if (ret is CType.ErrorUnion) { _fnErrorReturnTypes[funcSym] = (retType, errUnion); }
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
            // The optional CallConv (Milestone R, part 5) shifts the return type + body one slot right.
            case Zig.FnDef f:          nameTok = f.Arg1; paramsItem = f.Arg3; retType = f.Arg6; body = f.Arg7; errUnion = false; break;
            case Zig.FnDefNoArgs f:    nameTok = f.Arg1; paramsItem = null;   retType = f.Arg5; body = f.Arg6; errUnion = false; break;
            case Zig.FnDefErr f:       nameTok = f.Arg1; paramsItem = f.Arg3; retType = f.Arg7; body = f.Arg8; errUnion = true;  break;
            case Zig.FnDefNoArgsErr f: nameTok = f.Arg1; paramsItem = null;   retType = f.Arg6; body = f.Arg7; errUnion = true;  break;
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
        _currentFnHasErrdefer = false;   // set lazily as `errdefer`s are encountered (Milestone H)
        // The declared error set for the foreign-error return check (Milestone X, part 3); resolved
        // NOW (pass 2 — all `const E = error{…}` set decls are processed by here). Null (unconstrained)
        // for an inferred `!T` / `anyerror!T` or a non-error-union function.
        _currentFnErrorSet =
            _fnErrorReturnTypes.TryGetValue(funcSym, out var rt)
            && TryDeclaredErrorSet(rt.retType, rt.errUnion, out var esName, out var esMembers)
                ? (esName, esMembers)
                : null;
        _symbols.BeginFunction();
        _symbols.EnterScope();
        var paramSyms = paramInfos
            .Select(p => _symbols.Declare(new Symbol { Name = p.name, Kind = SymKind.Param, Type = p.type }))
            .ToList();
        var blk = LowerBlock(body);
        // Milestone O part 5 — demote a non-escaping, freed, constant-size byte slice allocated
        // through the devirtualized C-heap default (`page_allocator`/`c_allocator`) to a `stackalloc`
        // backing. Runs BEFORE ExitScope so the synthetic backing-buffer temp uniquifies against this
        // function's names (BeginFunction cleared `_usedNames`; ExitScope would not).
        blk = PromoteStackSlices(blk);
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
    private void RegisterStruct(string name, IReadOnlyList<Item> fieldItems, AggregateLayout layout = AggregateLayout.Default)
    {
        var fields = new List<StructField>();
        foreach (var fd in fieldItems)
        {
            var f = (Zig.StructField)fd.Content!;   // FieldDecl -> IDENT ':' Type
            fields.Add(new StructField(Tok(f.Arg0), LowerType(f.Arg2)));
        }
        _ir.RegisterStructType(name, fields, isUnion: false, layout);
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
    /// can use a self alias. A container-level <c>var</c> (a namespaced mutable global) is COLLECTED
    /// here and lowered to a real global in pass 1.5 (<see cref="LowerContainerVar"/>).</summary>
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

            // A container-level `var` is a namespaced mutable GLOBAL (Milestone R, part 6). Collect it
            // now (pass 0b); it's lowered to a real GlobalVar in pass 1.5 (LowerContainerVar) — deferred
            // so its initializer can reference functions (declared in pass 1). A `Type.name` read/write
            // then resolves to the global's VarRef (see the `Zig.Field` case + the container-var path).
            if (isVar)
            {
                _pendingContainerVars.Add((container, cname, typeItem, rhs));
                continue;
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
        var variants = ParseUnionVariants(variantItems);

        // Synthesize the tag enum `U_Tag` + its member symbols (variant name → tag constant = index).
        var tagName = name + TagSuffix;
        var tagType = new CType.Enum(tagName, CType.Int);
        var tagMembers = new List<EnumMember>();
        var tagSyms = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
        long idx = 0;
        foreach (var (vname, _) in variants)
        {
            tagMembers.Add(new EnumMember(vname, idx));
            tagSyms[vname] = new Symbol { Name = vname, Kind = SymKind.EnumConst, Type = tagType, ConstValue = idx, IsGlobal = true };
            idx++;
        }
        _ir.RegisterEnumType(tagName, CType.Int, tagMembers);
        _containerTypes[tagName] = tagType;
        _enumMembers[tagName] = tagSyms;

        FinishUnion(name, variants, tagType);
        RegisterContainerConsts(name, consts);   // e.g. `const Self = @This();` → the outer struct type
        return methods;
    }

    /// <summary>Register a Zig <c>union(SomeEnum)</c> declaration — a tagged union whose discriminant
    /// is an EXISTING, named enum rather than an auto-synthesized one (Milestone R). Reuses the named
    /// enum (registered fully in pass 0a, so it's available here in pass 0b) as the tag type, then
    /// builds the same payload-union + outer-struct shape as <see cref="RegisterUnion"/> via
    /// <see cref="FinishUnion"/> — so construction (<see cref="BuildUnionInit"/>) and <c>switch</c>
    /// (<see cref="LowerUnionSwitch"/>) work unchanged (they key off <see cref="ZigUnionInfo.TagType"/>
    /// + the tag enum's member symbols, which a named tag enum already carries). Each variant must
    /// name a member of the tag enum; an extra enum member with no variant is tolerated (a V1 leniency
    /// — Zig requires the sets to correspond exactly).</summary>
    private List<Item> RegisterUnionTagged(string name, string tagEnumName, Item variantsItem)
    {
        var (variantItems, methods, consts) = SplitUnionMembers(variantsItem);
        var variants = ParseUnionVariants(variantItems);

        if (!_containerTypes.TryGetValue(tagEnumName, out var t) || t is not CType.Enum tagType
            || !_enumMembers.TryGetValue(tagEnumName, out var tagSyms))
        {
            throw new IrUnsupportedException(
                $"zig `union({tagEnumName})` tag must name a declared enum type; '{tagEnumName}' is not one");
        }
        foreach (var (vname, _) in variants)
        {
            if (!tagSyms.ContainsKey(vname))
            {
                throw new IrUnsupportedException(
                    $"zig `union({tagEnumName})` variant '{vname}' is not a member of enum '{tagEnumName}'");
            }
        }

        FinishUnion(name, variants, tagType);
        RegisterContainerConsts(name, consts);
        return methods;
    }

    /// <summary>Parse a union body's variant items into <c>(name, payload?)</c> pairs — a
    /// payload variant (<c>name: Type</c>) or a void/tag-only variant (<c>name</c>).</summary>
    private List<(string name, CType? payload)> ParseUnionVariants(IReadOnlyList<Item> variantItems)
    {
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
        return variants;
    }

    /// <summary>Finish registering a tagged union once its variants + tag enum are known (shared by
    /// the auto-tag <c>union(enum)</c> and the explicit-tag <c>union(SomeEnum)</c> forms): build the
    /// nested overlapping-payload union <c>U_Payload</c> (one <c>[FieldOffset(0)]</c> field per PAYLOAD
    /// variant — none for an all-void union), the outer discriminated struct <c>U = { __tag,
    /// (__payload?) }</c>, and record the <see cref="ZigUnionInfo"/> for construction + <c>switch</c>.</summary>
    private void FinishUnion(string name, IReadOnlyList<(string name, CType? payload)> variants, CType.Enum tagType)
    {
        var variantMap = new Dictionary<string, CType?>(System.StringComparer.Ordinal);
        var payloadFields = new List<StructField>();
        foreach (var (vname, payload) in variants)
        {
            variantMap[vname] = payload;
            if (payload is not null) { payloadFields.Add(new StructField(vname, payload)); }
        }
        string? payloadTypeName = null;
        if (payloadFields.Count > 0)
        {
            payloadTypeName = name + PayloadSuffix;
            _ir.RegisterStructType(payloadTypeName, payloadFields, isUnion: true);   // [StructLayout(Explicit)], all at offset 0
        }
        var fields = new List<StructField> { new StructField(TagFieldName, tagType) };
        if (payloadTypeName is not null) { fields.Add(new StructField(PayloadFieldName, new CType.Named(payloadTypeName))); }
        _ir.RegisterStructType(name, fields, isUnion: false);
        _unions[name] = new ZigUnionInfo(name, tagType, TagFieldName, payloadTypeName, PayloadFieldName, variantMap);
    }

    /// <summary>Register a Zig UNTAGGED <c>union { a: T, b: U, … }</c> (Milestone R, part 3) — a bare
    /// overlapping-storage union with NO discriminant. Unlike a tagged union it has no outer
    /// <c>{ __tag, __payload }</c> wrapper and is NOT a <see cref="ZigUnionInfo"/>: the union TYPE
    /// itself is the overlay struct (<c>[StructLayout(Explicit)]</c>, every variant at
    /// <c>[FieldOffset(0)]</c>, via the shared C-union machinery — <c>isUnion: true</c>). Construction
    /// (<c>U{ .a = v }</c> / <c>.{ .a = v }</c>) and access (<c>u.a</c>) therefore route through the
    /// ordinary struct-init / member paths, not <see cref="BuildUnionInit"/>. Each variant must carry a
    /// payload type — a void variant needs a tagged <c>union(enum)</c> (there is no tag here to select
    /// it). Zig's safe-mode active-field tracking / type-pun checks are NOT modeled (same-field
    /// read/write is faithful; a `switch` on an untagged union is rejected, as Zig forbids it).
    /// Returns the body's method items for declaration in pass 1, and registers its consts.</summary>
    private List<Item> RegisterUnionUntagged(string name, Item variantsItem)
    {
        var (variantItems, methods, consts) = SplitUnionMembers(variantsItem);
        var variants = ParseUnionVariants(variantItems);
        var fields = new List<StructField>();
        foreach (var (vname, payload) in variants)
        {
            if (payload is null)
            {
                throw new IrUnsupportedException(
                    $"untagged union '{name}' variant '{vname}' must have a type — a void variant needs a tagged `union(enum)`");
            }
            fields.Add(new StructField(vname, payload));
        }
        _ir.RegisterStructType(name, fields, isUnion: true);   // [StructLayout(Explicit)], all at offset 0
        RegisterContainerConsts(name, consts);
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
    /// if it is not a compile-time constant. Routed through the shared
    /// <see cref="IrBuilder.ConstEval"/> interpreter (Milestone T), so a Zig enum value
    /// may now be any constant expression — binary arithmetic/bitwise/shift, parens,
    /// sizeof, a reference to an earlier member — not just a literal or unary of one.</summary>
    private long? ZigConstEval(CExpr e) => _ir.ConstEval(e);

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
        // Array literal: a positional `.{e0, e1, …}` whose sink is a `[N]T` array (Milestone K) →
        // a stackalloc'd array value. Checked BEFORE the tuple sink so `[N]T` wins over a same-arity
        // tuple. A named element here is a mistake (an array literal is all-positional).
        if (sink?.Unqualified is CType.Array arrSink)
        {
            if (anyNamed)
            {
                throw new IrUnsupportedException(
                    "zig array literal `.{…}` must use positional elements, not `.field = …`");
            }
            return BuildArrayInit(fields, arrSink);
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
        // An EMPTY `.{}` with no struct/union sink is an empty tuple (`const t = .{};`) — a zero-field
        // ValueTuple. (At a Named struct/union sink it zero-inits that aggregate, handled below.)
        if (fields.Count == 0 && sink?.Unqualified is not CType.Named)
        {
            return BuildTupleInit(fields, sink?.Unqualified as CType.Tuple);
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

    /// <summary>Build an array literal (Milestone K) — a positional `.{e0, e1, …}` at a `[N]T` sink,
    /// or a typed `[N]T{…}` / `[_]T{…}` — as a <see cref="StackArray"/> (a stackalloc'd array value;
    /// the backend hoists it to a block-local pointer temp when used outside an initializer). Each
    /// element lowers at the array's element type as its sink (so a nested `.{…}` / `.member`
    /// resolves). A fixed extent must match the element count; an inferred `[_]T` (Count null) takes
    /// the element count. An empty literal is rejected — a zeroed array uses `undefined`.</summary>
    private CExpr BuildArrayInit(IReadOnlyList<Item> posItems, CType.Array arr)
    {
        if (posItems.Count == 0)
        {
            throw new IrUnsupportedException(
                "zig empty array literal is not supported — initialize a `[N]T` with `undefined` for a zeroed array");
        }
        if (arr.Count is { } n && posItems.Count != n)
        {
            throw new IrUnsupportedException(
                $"zig array literal has {posItems.Count} element(s) but the target array `[{n}]…` expects {n}");
        }
        var elems = LowerArrayElems(posItems, arr.Element);
        var arrType = arr.Count is null ? new CType.Array(arr.Element, posItems.Count) : arr;
        return new StackArray(arr.Element, elems) { Type = arrType };
    }

    /// <summary>Lower the positional elements of an array literal, each at <paramref name="element"/>
    /// as its sink (so a nested literal / bare `.member` resolves). A named `.field = …` element is
    /// rejected — an array literal is all-positional.</summary>
    private List<CExpr> LowerArrayElems(IReadOnlyList<Item> fields, CType element)
    {
        var elems = new List<CExpr>(fields.Count);
        foreach (var f in fields)
        {
            if (f.Content is not Zig.FieldInitPositional pos)
            {
                throw new IrUnsupportedException(
                    "zig array literal must use positional elements (`.{a, b}` / `[N]T{a, b}`), not `.field = …`");
            }
            elems.Add(LowerExprSink(pos.Arg0, element));
        }
        return elems;
    }

    /// <summary>Lower a TYPED struct literal `Type{ .field = … }` — Zig's CurlySuffixExpr
    /// (`CurlySuffix -> Type '{' FieldInits '}'`). Unlike the anonymous `.{…}` form, the struct
    /// type is named explicitly by the leading <c>Type</c>, so this needs NO sink and is valid in
    /// any expression position (e.g. <c>&amp;Point{…}</c>, a bare subexpression). The leading type
    /// must resolve to a registered struct.</summary>
    private CExpr LowerTypedStructInit(Item typeItem, IReadOnlyList<Item> fieldInitItems)
    {
        // `[N]T{…}` / `[_]T{…}` — a typed array literal (Milestone K). Resolve the element type and
        // extent WITHOUT lowering the whole `[_]T` type (LowerType can't const-eval the inferred `_`);
        // `[_]T` takes the element count, `[N]T` the literal N (which must match the elements).
        if (typeItem.Content is Zig.TyArray ta)
        {
            var element = LowerType(ta.Arg3);
            var inferred = ta.Arg1.Content is Zig.Ident id && Tok(id.Arg0) == "_";
            var arr = inferred
                ? new CType.Array(element, null)
                : new CType.Array(element, ConstEvalArraySize(ta.Arg1));
            return BuildArrayInit(fieldInitItems, arr);
        }
        var t = LowerType(typeItem);
        // A user-constructed custom allocator (Milestone W, part 1b): `std.mem.Allocator{ .ptr, .vtable }`
        // and the `std.mem.Allocator.VTable{ .alloc, .resize, .remap, .free }` literal it points at.
        if (t.Unqualified is CType.Allocator) { return BuildAllocatorLiteral(fieldInitItems); }
        if (t.Unqualified is CType.Named { Name: VTableTypeName }) { return BuildAllocatorVTableLiteral(fieldInitItems); }
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

    /// <summary>The field types of the runtime <c>AllocatorVTable</c> — Zig's
    /// <c>std.mem.Allocator.VTable</c> shape (Milestone W, part 1b). Each is a MANAGED function
    /// pointer (<c>delegate*&lt;…&gt;</c>) whose signature matches the corresponding user function's
    /// lowering, so a <c>&amp;fn</c> reference in a <c>VTable{…}</c> literal binds cleanly. Returns
    /// <c>null</c> for an unknown field. Mirrors <c>AllocatorVTable</c> in <c>DotCC.Libc/ZigAlloc.cs</c>:
    /// <c>*anyopaque</c>→<c>void*</c>, <c>?[*]u8</c>→<c>byte*</c>, <c>[]u8</c>→<c>Slice&lt;byte&gt;</c>,
    /// <c>usize</c>→<c>ulong</c>, <c>std.mem.Alignment</c>→<c>Alignment</c>.</summary>
    private static CType? VTableFieldType(string field)
    {
        CType ctx = new CType.Pointer(CType.Void);     // *anyopaque
        CType optPtr = new CType.Pointer(CType.UChar); // ?[*]u8
        CType mem = new CType.Slice(CType.UChar);      // []u8
        CType usz = CType.ULong;                       // usize
        CType aln = new CType.Named(AlignmentTypeName); // std.mem.Alignment
        return field switch
        {
            "alloc"  => new CType.Func(optPtr, new[] { ctx, usz, aln, usz }, false),
            "resize" => new CType.Func(CType.Bool, new[] { ctx, mem, aln, usz, usz }, false),
            "remap"  => new CType.Func(optPtr, new[] { ctx, mem, aln, usz, usz }, false),
            "free"   => new CType.Func(CType.Void, new[] { ctx, mem, aln, usz }, false),
            _ => null,
        };
    }

    /// <summary>Lower a <c>std.mem.Allocator.VTable{ .alloc = f, .resize = g, .remap = h, .free = k }</c>
    /// literal (Milestone W, part 1b) to a <see cref="StructInit"/> of the runtime
    /// <c>AllocatorVTable</c>. Each function reference is result-located against its
    /// <see cref="VTableFieldType"/>, so a bare function name decays to <c>&amp;fn</c> matching the
    /// managed fn-pointer field. The C# backend renders <c>new AllocatorVTable { alloc = &amp;f, … }</c>
    /// purely from the node (no registered field metadata needed).</summary>
    private CExpr BuildAllocatorVTableLiteral(IReadOnlyList<Item> fieldInitItems)
    {
        var members = new List<FieldInit>();
        foreach (var fiItem in fieldInitItems)
        {
            var fi = (Zig.FieldInit)fiItem.Content!;   // FieldInit -> '.' IDENT '=' Expr
            var fname = Tok(fi.Arg1);
            var ftype = VTableFieldType(fname)
                ?? throw new IrUnsupportedException(
                    $"std.mem.Allocator.VTable has no field '{fname}' (expected alloc / resize / remap / free)");
            members.Add(new FieldInit(fname, ftype, LowerExprSink(fi.Arg3, ftype)));
        }
        return new StructInit(members) { Type = new CType.Named(VTableTypeName) };
    }

    /// <summary>Lower a <c>std.mem.Allocator{ .ptr = p, .vtable = &amp;vt }</c> literal (Milestone W,
    /// part 1b) to a <see cref="StructInit"/> of the runtime <c>Allocator</c> fat pointer. <c>.ptr</c>
    /// (a <c>*anyopaque</c> context) maps to the runtime <c>Ctx</c> (<c>void*</c>); <c>.vtable</c> is a
    /// <c>*const VTable</c> in Zig but the runtime carries the table BY VALUE, so the <c>&amp;vt</c> is
    /// dereferenced to the vtable value (<see cref="LowerVtableByValue"/>). The resulting
    /// <see cref="CType.Allocator"/> value routes through the existing indirect dispatch
    /// (<c>a.alloc</c>/<c>a.free</c> → the vtable functions).</summary>
    private CExpr BuildAllocatorLiteral(IReadOnlyList<Item> fieldInitItems)
    {
        CExpr? ctx = null;
        CExpr? vtable = null;
        foreach (var fiItem in fieldInitItems)
        {
            var fi = (Zig.FieldInit)fiItem.Content!;
            var fname = Tok(fi.Arg1);
            switch (fname)
            {
                case "ptr": ctx = LowerExprSink(fi.Arg3, new CType.Pointer(CType.Void)); break;
                case "vtable": vtable = LowerVtableByValue(fi.Arg3); break;
                default:
                    throw new IrUnsupportedException(
                        $"std.mem.Allocator literal has no field '{fname}' (expected ptr / vtable)");
            }
        }
        if (ctx is null || vtable is null)
        {
            throw new IrUnsupportedException("std.mem.Allocator literal requires both .ptr and .vtable");
        }
        var members = new List<FieldInit>
        {
            new FieldInit("Ctx", new CType.Pointer(CType.Void), ctx),
            new FieldInit("Vtable", new CType.Named(VTableTypeName), vtable),
        };
        return new StructInit(members) { Type = new CType.Allocator() };
    }

    /// <summary>Lower a <c>.vtable = &amp;vt</c> initializer to the vtable VALUE: Zig's
    /// <c>Allocator.vtable</c> is a <c>*const VTable</c>, but dotcc's runtime <c>Allocator</c> holds
    /// the table by value, so drop a leading <c>&amp;</c> (or dereference a general <c>*VTable</c>).</summary>
    private CExpr LowerVtableByValue(Item vtableExpr)
    {
        var e = LowerExpr(vtableExpr);
        if (e is Unary { Op: UnOp.AddrOf } u) { return u.Operand; }
        if (e.Type.Unqualified is CType.Pointer p) { return new Unary(UnOp.Deref, e) { Type = p.Pointee }; }
        return e;
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
            // A `@builtin(...)` at a typed sink — the result-location cast builtins
            // (`@intCast`/`@ptrCast`/…) infer their target from `sink`. Routed through the
            // shared lowering WITH the sink (vs LowerExpr's sink-free call).
            case Zig.BuiltinCall b:
                return LowerBuiltinCall(b, sink);
            // A switch EXPRESSION at a typed sink (`const x: T = switch (y) { … }`) — each arm's
            // value lowers at `sink`, so a result-located arm (`.member` / `.{…}` / a cast) resolves.
            case Zig.SwitchExpr s:         return LowerSwitchExpr(s.Arg2, s.Arg5, sink);
            case Zig.SwitchExprTrailing s: return LowerSwitchExpr(s.Arg2, s.Arg5, sink);
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
        // Zig's `*[N]T` → `[]T`: `&arr` (address-of an array) coerces to a slice. Strip the
        // address-of to recover the array lvalue — a Zig array already lowers to its element pointer
        // (a `T*` in emitted C#), which is exactly the pointer `SliceNew` wants, and its element
        // count comes from the array type. (A bare `*[N]T` pointer VALUE that isn't a literal `&arr`
        // is rarer; it falls through to the array check below and reports a clear coercion error.)
        if (value is Unary { Op: UnOp.AddrOf, Operand: var arr } && arr.Type.Unqualified is CType.Array)
        {
            value = arr;
        }
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

    /// <summary>Lower a curated <c>std.mem.&lt;name&gt;(…)</c> call (the byte-blit / compare cluster).
    /// <c>eql(T, a, b)</c> and <c>copyForwards(T, dest, source)</c> take an explicit element type as
    /// the first argument; the slice arguments coerce at a <c>[]T</c> / <c>[]const T</c> sink (so a
    /// <c>&amp;array</c> promotes to a slice via <see cref="CoerceToSlice"/>). Both lower to a
    /// <see cref="ZigMemCall"/> rendered as <c>ZigMem.{Method}&lt;T&gt;(…)</c>. An unmodeled member is
    /// a clear error — dotcc models no general <c>std</c>.</summary>
    private CExpr LowerStdMemCall(string methodName, IReadOnlyList<Item> argItems)
    {
        switch (methodName)
        {
            case "eql":
                if (argItems.Count != 3)
                {
                    throw new IrUnsupportedException($"zig `std.mem.eql` expects (type, a, b); got {argItems.Count} argument(s)");
                }
                var eqElem = LowerType(argItems[0]).Unqualified;
                var eqSink = new CType.Slice(eqElem.WithQuals(TypeQual.Const));
                var eqA = LowerExprSink(argItems[1], eqSink);
                var eqB = LowerExprSink(argItems[2], eqSink);
                return new ZigMemCall("Eql", eqElem, new List<CExpr> { eqA, eqB }) { Type = CType.Bool };
            case "copyForwards":
                if (argItems.Count != 3)
                {
                    throw new IrUnsupportedException($"zig `std.mem.copyForwards` expects (type, dest, source); got {argItems.Count} argument(s)");
                }
                var cpElem = LowerType(argItems[0]).Unqualified;
                var cpDest = LowerExprSink(argItems[1], new CType.Slice(cpElem));
                var cpSrc = LowerExprSink(argItems[2], new CType.Slice(cpElem.WithQuals(TypeQual.Const)));
                return new ZigMemCall("CopyForwards", cpElem, new List<CExpr> { cpDest, cpSrc }) { Type = CType.Void };
            case "span":
                // std.mem.span(ptr) — a NUL-sentinel pointer `[*:0]T` → the `[]const T` slice before
                // the sentinel (dotcc's V1 sentinel = 0, erased in the type; the common `[*:0]const u8`
                // C-string case). The element comes from the pointer's pointee.
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `std.mem.span` expects (pointer); got {argItems.Count} argument(s)");
                }
                var spArg = LowerExpr(argItems[0]);
                if (spArg.Type.Unqualified is not CType.Pointer sp)
                {
                    throw new IrUnsupportedException(
                        $"zig `std.mem.span` expects a sentinel-terminated pointer (`[*:0]T`), got {spArg.Type.Describe()}");
                }
                var spElem = sp.Pointee.Unqualified;
                return new ZigMemCall("SpanZ", spElem, new List<CExpr> { spArg })
                {
                    Type = new CType.Slice(spElem.WithQuals(TypeQual.Const)),
                };
            case "zeroes":
                // std.mem.zeroes(T) — an all-zero value of T → C#'s `default(T)` (zero-fills a scalar
                // or a struct uniformly). An ARRAY/slice type is a documented cut (arrays lower to a
                // pointer, so `default` would be a null pointer, not a zeroed array).
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `std.mem.zeroes` expects (type); got {argItems.Count} argument(s)");
                }
                var zt = LowerType(argItems[0]);
                if (zt.Unqualified is CType.Array or CType.Slice)
                {
                    throw new IrUnsupportedException(
                        "zig `std.mem.zeroes` of an array/slice type is not modeled yet (scalar and struct types are supported)");
                }
                return new DefaultLit { Type = zt };
            default:
                throw new IrUnsupportedException(
                    $"zig `std.mem.{methodName}` is not modeled yet (supported: eql, copyForwards, span, zeroes)");
        }
    }

    /// <summary>Lower a <c>@memcpy</c>/<c>@memset</c> slice operand, inferring the element type from
    /// the operand itself (a <c>[]T</c> slice, a <c>[N]T</c> array, or <c>&amp;array</c> = <c>*[N]T</c>)
    /// rather than an explicit type argument. Returns the coerced slice expression and reports the
    /// (unqualified) element type via <paramref name="element"/>. When <paramref name="wantConst"/>
    /// the target slice is <c>[]const T</c> (a read source); otherwise <c>[]T</c> (a write dest).</summary>
    private CExpr LowerMemSlice(Item item, bool wantConst, out CType element)
    {
        var lowered = LowerExpr(item);
        element = SliceElementOf(lowered).Unqualified;
        if (lowered.Type.Unqualified is CType.Slice) { return lowered; }
        var elemQ = wantConst ? element.WithQuals(TypeQual.Const) : element;
        return CoerceToSlice(lowered, new CType.Slice(elemQ));
    }

    /// <summary>The element type of a lowered slice-like operand — a <c>[]T</c> slice, a <c>[N]T</c>
    /// array, or a pointer-to-array (<c>&amp;array</c> = <c>*[N]T</c>) — used to infer <c>@memcpy</c>/
    /// <c>@memset</c>'s element type from its dest without an explicit type argument.</summary>
    private static CType SliceElementOf(CExpr e) => e.Type.Unqualified switch
    {
        CType.Slice s => s.Element,
        CType.Array a => a.Element,
        CType.Pointer { Pointee.Unqualified: CType.Array pa } => pa.Element,
        _ => throw new IrUnsupportedException(
            $"expected a slice, array, or `&array` operand, got {e.Type.Describe()}"),
    };

    /// <summary>Lower a slice expression <c>base[lo..hi]</c> to a fat-pointer
    /// <see cref="SliceNew"/> <c>{ base.ptr + lo, hi - lo }</c>. When <paramref name="hi"/> is
    /// null the slice is open-ended (<c>base[lo..]</c>) and the high bound is the source length:
    /// a slice's <c>.Len</c> or an array's element count. The base may be a slice (re-slice
    /// through <c>.Ptr</c>), a bare pointer (no length — open-ended is rejected, as Zig does),
    /// or an array (decays to its element pointer); the element type + const-ness ride into the
    /// resulting <c>[]T</c> / <c>[]const T</c>.</summary>
    private CExpr BuildSlice(CExpr baseExpr, CExpr lo, CExpr? hi)
    {
        CExpr basePtr;
        CType element;
        CExpr? sourceLen;   // the known source length, used for an open-ended high bound
        switch (baseExpr.Type.Unqualified)
        {
            case CType.Slice s:
                basePtr = new Member(baseExpr, "Ptr", false) { Type = new CType.Pointer(s.Element) };
                element = s.Element;
                sourceLen = new Member(baseExpr, "Len", false) { Type = CType.ULong };
                break;
            case CType.Pointer p:
                basePtr = baseExpr;
                element = p.Pointee;
                sourceLen = null;   // a bare pointer carries no length
                break;
            case CType.Array a:
                basePtr = baseExpr;   // decays to its element pointer
                element = a.Element;
                sourceLen = a.Count is int n
                    ? new LitInt(n.ToString(CultureInfo.InvariantCulture), n) { Type = CType.ULong }
                    : null;
                break;
            default:
                throw new IrUnsupportedException($"cannot slice a {baseExpr.Type.Describe()} (need a slice, pointer, or array)");
        }
        var ptr = new Binary(BinOp.Add, basePtr, lo) { Type = new CType.Pointer(element) };
        CExpr len;
        if (hi is not null)
        {
            // len = (ulong)(hi - lo). The explicit cast covers non-constant bounds, where
            // a signed `int` difference has no implicit conversion to the ctor's `ulong`.
            var diff = new Binary(BinOp.Sub, hi, lo) { Type = hi.Type };
            len = new Cast(CType.ULong, diff) { Type = CType.ULong };
        }
        else
        {
            if (sourceLen is null)
            {
                throw new IrUnsupportedException(
                    "open-ended slice `[lo..]` needs a known length (slice or array); a bare pointer has none");
            }
            // len = sourceLen - (ulong)lo. sourceLen is already ulong; cast lo to match (a
            // signed `int` index has no implicit conversion to ulong).
            var loU = new Cast(CType.ULong, lo) { Type = CType.ULong };
            len = new Binary(BinOp.Sub, sourceLen, loU) { Type = CType.ULong };
        }
        return new SliceNew(ptr, len, element.Unqualified, element.IsConst) { Type = new CType.Slice(element) };
    }

    /// <summary>Lower a <c>@builtin(...)</c> call. Several builtins are RESULT-LOCATION-typed —
    /// Zig infers their target from the sink, not an explicit type argument: <c>@intCast</c>,
    /// <c>@truncate</c>, <c>@ptrCast</c>, <c>@bitCast</c>, <c>@floatFromInt</c>,
    /// <c>@intFromFloat</c>, <c>@floatCast</c>, <c>@enumFromInt</c>. Those are valid only at a
    /// typed sink (a typed binding, <c>return</c>, assignment, call argument, or nested inside
    /// <c>@as(T, …)</c>), so without one (<paramref name="sink"/> null) they're a clear error.
    /// <c>@as</c>/<c>@intFromEnum</c>/<c>@sizeOf</c>/<c>@alignCast</c> carry or need no sink and
    /// lower the same way from either path. Called from <see cref="LowerExpr"/> (sink null) and
    /// <see cref="LowerExprSink"/> (sink set).</summary>
    private CExpr LowerBuiltinCall(Zig.BuiltinCall b, CType? sink)
    {
        var bname = Tok(b.Arg0);
        var bargs = Flatten(b.Arg2);
        switch (bname)
        {
            case "@as":
                // `@as(T, expr)` — the explicit-type cast → the C Cast IR. The type arg becomes
                // the value's sink, so a nested result-location builtin (`@as(u8, @intCast(x))`)
                // and a bare enum/struct literal both resolve.
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@as` expects (type, value); got {bargs.Count} argument(s)");
                }
                var asTarget = LowerType(bargs[0]);
                return new Cast(asTarget, LowerExprSink(bargs[1], asTarget)) { Type = asTarget };
            case "@intFromEnum":
                // `@intFromEnum(e)` — the enum's integer value → decay to the underlying type
                // (the same Cast the backend uses for C's enum→int).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@intFromEnum` expects (enum); got {bargs.Count} argument(s)");
                }
                var enumOperand = LowerExpr(bargs[0]);
                if (enumOperand.Type.Unqualified is not CType.Enum en)
                {
                    throw new IrUnsupportedException("zig `@intFromEnum` expects an enum operand");
                }
                return new Cast(en.Underlying, enumOperand) { Type = en.Underlying };
            case "@sizeOf":
                // `@sizeOf(T)` — the byte size as `usize`. Reuses the C `sizeof` IR (folded for a
                // user aggregate via the layout model, else C#'s `sizeof(T)`).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@sizeOf` expects (type); got {bargs.Count} argument(s)");
                }
                return new SizeOfExpr(LowerType(bargs[0])) { Type = CType.ULong };
            case "@alignOf":
            {
                // `@alignOf(T)` — the ABI alignment as `usize` (Milestone T, part 4). Always a
                // compile-time constant on this LP64 target (the layout model computes it), so it
                // folds straight to a literal — no IR node, and it participates in comptime arithmetic
                // (a literal already folds) and renders directly at a runtime use site.
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@alignOf` expects (type); got {bargs.Count} argument(s)");
                }
                var align = _ir.AlignOfConst(LowerType(bargs[0]));
                return new LitInt(align.ToString(System.Globalization.CultureInfo.InvariantCulture), align) { Type = CType.ULong };
            }
            case "@offsetOf":
            {
                // `@offsetOf(T, "field")` — the byte offset of a field as `usize` (Milestone T,
                // part 4). Reuses the C `offsetof` IR (`OffsetOf`): the comptime engine folds it via
                // the layout model (`OffsetOfConstPath`), and a runtime use renders the .NET
                // blittable-layout computation. The field name is a comptime string literal.
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@offsetOf` expects (type, field-name); got {bargs.Count} argument(s)");
                }
                var offStruct = LowerType(bargs[0]);
                if (offStruct.Unqualified is not CType.Named offNamed)
                {
                    throw new IrUnsupportedException("zig `@offsetOf` expects a struct/union type as the first argument");
                }
                if (bargs[1].Content is not Zig.StrLit offFieldLit)
                {
                    throw new IrUnsupportedException("zig `@offsetOf` field name must be a string literal");
                }
                var offField = UnquoteStringLiteral(Tok(offFieldLit.Arg0));
                var offMemberType = _ir.StructFieldType(offNamed, offField);
                return new OffsetOf(offStruct, new[] { offField }, offMemberType) { Type = CType.ULong };
            }
            case "@errorName":
                // `@errorName(e)` → the error's name as `[]const u8` (real zig: `[:0]const u8`).
                // The operand is a flat `ushort` error code; the name comes from the runtime
                // `__zigErrorName(code)` code→name table the backend emits from `ir.ZigErrorCodes`
                // (Milestone X, part 1). Returns a `ConstSlice<byte>` over the RVA-pinned name bytes.
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@errorName` expects (error); got {bargs.Count} argument(s)");
                }
                return new Call("__zigErrorName", new List<CExpr> { LowerExpr(bargs[0]) },
                                new List<CType> { CType.ErrorSet }, null)
                {
                    Type = new CType.Slice(CType.UChar.WithQuals(TypeQual.Const)),
                };
            case "@alignCast":
                // `@alignCast(p)` only raises the pointee's alignment requirement — unobservable
                // in dotcc's managed model — so it's the IDENTITY (the enclosing `@ptrCast` / sink
                // does the real conversion). Needs no sink, and works nested in its idiomatic
                // `@ptrCast(@alignCast(p))` (where it's reached via the sink-free LowerExpr).
                if (bargs.Count != 1)
                {
                    throw new IrUnsupportedException($"zig `@alignCast` expects (value); got {bargs.Count} argument(s)");
                }
                return LowerExpr(bargs[0]);
            case "@intCast" or "@truncate" or "@ptrCast" or "@bitCast"
                or "@floatFromInt" or "@intFromFloat" or "@floatCast" or "@enumFromInt":
                return LowerResultLocationBuiltin(bname, bargs, sink);
            case "@memcpy":
                // `@memcpy(dest, source)` — copy `source.len` elements into `dest` (equal lengths in
                // Zig; a forward element copy). The element type is inferred from the dest operand.
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@memcpy` expects (dest, source); got {bargs.Count} argument(s)");
                }
                var mcDest = LowerMemSlice(bargs[0], wantConst: false, out var mcElem);
                var mcSrc = LowerMemSlice(bargs[1], wantConst: true, out _);
                return new ZigMemCall("CopyForwards", mcElem, new List<CExpr> { mcDest, mcSrc }) { Type = CType.Void };
            case "@memset":
                // `@memset(dest, value)` — set every element of `dest` to `value` (lowered at the
                // element-type sink, so a `comptime_int` like `7` becomes `(byte)7`).
                if (bargs.Count != 2)
                {
                    throw new IrUnsupportedException($"zig `@memset` expects (dest, value); got {bargs.Count} argument(s)");
                }
                var msDest = LowerMemSlice(bargs[0], wantConst: false, out var msElem);
                var msVal = LowerExprSink(bargs[1], msElem);
                return new ZigMemCall("Set", msElem, new List<CExpr> { msDest, msVal }) { Type = CType.Void };
            default:
                throw new IrUnsupportedException(
                    $"zig builtin '{bname}' not lowered yet (supported: @as, @intCast, @truncate, @ptrCast, @bitCast, " +
                    "@floatFromInt, @intFromFloat, @floatCast, @enumFromInt, @alignCast, @intFromEnum, @sizeOf, @alignOf, " +
                    "@offsetOf, @errorName, @memcpy, @memset)");
        }
    }

    /// <summary>Lower a result-location cast builtin at its sink. Each is single-arg; the cast
    /// TARGET is the <paramref name="sink"/> (Zig infers it from the result location, unlike
    /// <c>@as(T, x)</c> which carries the type). Most map to the C <see cref="Cast"/> IR — the
    /// backend's unchecked cast truncates/converts, matching Zig's NON-safe-mode semantics (dotcc
    /// models no overflow trap, the same stance taken for plain <c>+</c>); <c>@bitCast</c>
    /// reinterprets the bit pattern via <see cref="BitCast"/>. Without a sink it's a clear error
    /// (Zig requires a result location to infer the type).</summary>
    private CExpr LowerResultLocationBuiltin(string name, List<Item> bargs, CType? sink)
    {
        if (bargs.Count != 1)
        {
            throw new IrUnsupportedException($"zig builtin '{name}' expects (value); got {bargs.Count} argument(s)");
        }
        if (sink is null)
        {
            throw new IrUnsupportedException(
                $"zig builtin '{name}' needs a result location to infer its target type — use it at a typed binding, " +
                "return, assignment, call argument, or nested inside `@as(T, …)`");
        }
        var operand = LowerExpr(bargs[0]);
        return name == "@bitCast"
            ? new BitCast(sink, operand) { Type = sink }
            : new Cast(sink, operand) { Type = sink };
    }

    // ---- statements ------------------------------------------------------

    private Block LowerBlock(Item block)
    {
        var items = new List<Item>();
        switch (block.Content)
        {
            case Zig.BlockEmpty: break;
            case Zig.Block b: items.AddRange(Flatten(b.Arg1)); break;
            default: throw new IrUnsupportedException("zig block: " + (block.Content?.GetType().Name ?? "null"));
        }
        return new Block(LowerStmtsWithDefers(items, 0));
    }

    /// <summary>Lower a block's statement items, restructuring <c>defer</c>/<c>errdefer</c> into
    /// nested <see cref="DeferGuard"/>s (Milestone H). Each guard wraps the statements that FOLLOW
    /// it within the block (built by recursion), so nesting them in lexical declaration order yields
    /// Zig's LIFO cleanup — the last-declared defer/errdefer is innermost, hence runs first. A
    /// <c>defer</c> guards every exit (a try/finally); an <c>errdefer</c> only the error exit (a
    /// try/catch). The cleanup expression is lowered AT the defer's position (before the rest), so it
    /// resolves against the variables in scope there — and runs reading their values at scope exit
    /// (its render site is the finally/catch), matching Zig's defer semantics.</summary>
    private List<CStmt> LowerStmtsWithDefers(List<Item> items, int start)
    {
        var stmts = new List<CStmt>();
        for (int i = start; i < items.Count; i++)
        {
            var it = items[i];
            Item? cleanupBody;
            bool onErrorOnly;
            switch (it.Content)
            {
                case Zig.StmtDefer d:    cleanupBody = d.Arg1; onErrorOnly = false; break;
                case Zig.StmtErrdefer d: cleanupBody = d.Arg1; onErrorOnly = true;  break;
                default:                 stmts.Add(LowerStmt(it)); continue;
            }
            // An `errdefer` makes the function's later `return error.X` propagate via a thrown
            // ZigErrorReturn (so it reaches this catch) — flagged BEFORE lowering the rest, so every
            // statement the guard wraps sees it (a guarded error-return always follows its errdefer
            // lexically, hence is lowered after this point).
            if (onErrorOnly) { _currentFnHasErrdefer = true; }
            var cleanup = LowerStmt(cleanupBody);
            var rest = new Block(LowerStmtsWithDefers(items, i + 1));
            stmts.Add(new DeferGuard(rest, cleanup, onErrorOnly));
            return stmts;   // the remaining statements now live inside the guard
        }
        return stmts;
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
            // The shared VarDecl nonterminal lets a statement-position `threadlocal var` parse;
            // real zig allows `threadlocal` only at container level, so reject it like zig does.
            case Zig.VarDeclThreadLocal:
                throw new IrUnsupportedException(
                    "'threadlocal' is only allowed on a container-level `var` (a function-local threadlocal is rejected by real zig too)");
            // `const/var x: T align(N)/linksection(".s") = e;` (Milestone R, part 5) — the modifiers
            // are a no-op on the managed target, so lower exactly like the unmodified typed decl
            // (the DeclMods arg is ignored). RhsExpr is one slot right of the Type (DeclMods between).
            case Zig.ConstDeclTypedMods d: return DeclOrComptime(d.Arg1, d.Arg3, d.Arg6);
            case Zig.VarDeclTypedMods d:   return DeclOf(d.Arg1, d.Arg3, d.Arg6);
            // `const a, const b = e;` (Milestone G) — destructure a tuple value: single-eval the
            // RHS, then bind each name to its positional element. See LowerDestructure.
            case Zig.StmtDestructure sd: return LowerDestructure(sd);
            // `return E;` — E may contain a hoistable catch/orelse in a sub-expression (ANF), so lower
            // under a hoist buffer (the hoisted temps run before the `return`).
            case Zig.StmtReturn r:      return Hoisted(() => LowerReturn(r.Arg1));
            case Zig.StmtReturnVoid:    return LowerReturnVoid();
            // `a catch return [x];` / `a orelse return [x];` as a STATEMENT (Milestone N, part 6) —
            // a control-flow early-out; the unwrapped value is discarded (common for a `!void` `a`).
            case Zig.StmtExpr e when IsControlFlowFallback(e.Arg0, out var cfL, out var cfC, out var cfR):
                return LowerControlFlowFallback(cfL, cfC, cfR, null);
            case Zig.StmtExpr e:        return Hoisted(() => new ExprStmt(LowerExpr(e.Arg0)));

            // `x = value;`  → an assignment used as a statement. `_ = value;` is Zig's
            // explicit DISCARD (it forbids ignoring a non-void result) — lower it to a
            // bare expression statement, evaluated for its side effects.
            // A `catch`/`orelse` in the RHS (or a discarded `_ = f(a catch b())`) may hoist (ANF), so
            // lower the assignment under a hoist buffer.
            case Zig.StmtAssign a:
                return Hoisted(() =>
                {
                    if (a.Arg0.Content is Zig.Ident lhs && Tok(lhs.Arg0) == "_")
                    {
                        return new ExprStmt(LowerExpr(a.Arg2));
                    }
                    var target = LowerExpr(a.Arg0);
                    // `x = blk: { … break :blk v; };` — a labeled value-block assignment (Milestone L,
                    // part 2): temp-fill against the lvalue's type, then assign the result temp into it.
                    if (a.Arg2.Content is Zig.LabeledBlock lb)
                    {
                        return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, target.Type,
                            temp => new ExprStmt(new Assign(null, target, new VarRef(temp) { Type = temp.Type }) { Type = target.Type }));
                    }
                    // `x = switch (y) { … blk: {…} };` / `x = if (c) blk:{…} else …;` — a value-position
                    // if/switch with a statement-producing branch (Milestone Y, part 1): temp-fill against
                    // the lvalue's type, then assign the result temp into it.
                    if (IsValueControlFlowStmt(a.Arg2))
                    {
                        return LowerValueControlFlowStmt(a.Arg2, target.Type,
                            temp => new ExprStmt(new Assign(null, target, new VarRef(temp) { Type = temp.Type }) { Type = target.Type }));
                    }
                    var value = LowerExprSink(a.Arg2, target.Type);   // target type is the sink (`x = .member;`)
                    return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
                });

            // `x op= y` (compound assignment) → the shared Assign node with a non-null CompoundOp.
            // Each operator maps to the SAME BinOp the matching Zig binary op uses (Add/Sub/…), so
            // `+=` stays consistent with how Zig's `+` lowers — NOT C's promotion rules. The C#
            // backend renders a native `target op= rhs`, evaluating the lvalue exactly once (correct
            // binding for `a[i()] += 1` / `p.* += 1`). Zig has no `++`/`--`; `x += 1` is the idiom.
            case Zig.StmtAddAssign a:    return CompoundAssign(a.Arg0, BinOp.Add, a.Arg2);
            case Zig.StmtSubAssign a:    return CompoundAssign(a.Arg0, BinOp.Sub, a.Arg2);
            case Zig.StmtMulAssign a:    return CompoundAssign(a.Arg0, BinOp.Mul, a.Arg2);
            case Zig.StmtDivAssign a:    return CompoundAssign(a.Arg0, BinOp.Div, a.Arg2);
            case Zig.StmtModAssign a:    return CompoundAssign(a.Arg0, BinOp.Mod, a.Arg2);
            case Zig.StmtShlAssign a:    return CompoundAssign(a.Arg0, BinOp.Shl, a.Arg2);
            case Zig.StmtShrAssign a:    return CompoundAssign(a.Arg0, BinOp.Shr, a.Arg2);
            case Zig.StmtBitAndAssign a: return CompoundAssign(a.Arg0, BinOp.BitAnd, a.Arg2);
            case Zig.StmtBitOrAssign a:  return CompoundAssign(a.Arg0, BinOp.BitOr, a.Arg2);
            case Zig.StmtBitXorAssign a: return CompoundAssign(a.Arg0, BinOp.BitXor, a.Arg2);

            // `x op%= y` (wrapping compound assignment, Milestone P) → the SAME CompoundAssign node as
            // the plain form. A native C# `target op= rhs` already truncates the result back to the LHS
            // width in the project's unchecked context — exactly two's-complement wrap — so `+%=` and
            // `+=` lower identically (dotcc doesn't model Zig's plain-`+` safe-mode overflow trap).
            case Zig.StmtAddWrapAssign a: return CompoundAssign(a.Arg0, BinOp.Add, a.Arg2);
            case Zig.StmtSubWrapAssign a: return CompoundAssign(a.Arg0, BinOp.Sub, a.Arg2);
            case Zig.StmtMulWrapAssign a: return CompoundAssign(a.Arg0, BinOp.Mul, a.Arg2);

            // `x op|= y` (saturating compound assignment, Milestone P) → `x = ZigMath.Sat…(x, y)`.
            // No native C# saturating compound op exists, so it desugars to a plain assignment of the
            // clamping call (single-eval-guarded on the lvalue — see SatCompoundAssign).
            case Zig.StmtAddSatAssign a: return SatCompoundAssign(a.Arg0, "SatAdd", a.Arg2);
            case Zig.StmtSubSatAssign a: return SatCompoundAssign(a.Arg0, "SatSub", a.Arg2);
            case Zig.StmtMulSatAssign a: return SatCompoundAssign(a.Arg0, "SatMul", a.Arg2);

            // if (cond) then [else else]  — `then`/`else`/`body` are themselves Stmts
            // (a single statement or a brace Block), which LowerStmt handles uniformly.
            case Zig.StmtIf f:          return new If(LowerExpr(f.Arg2), LowerStmt(f.Arg4), null);
            case Zig.StmtIfElse f:      return new If(LowerExpr(f.Arg2), LowerStmt(f.Arg4), LowerStmt(f.Arg6));

            // `if (opt) |x| then [else else]` — payload-capturing `if` (Milestone M). Binds the
            // optional's payload (value `?T` or niche pointer) — or, with `else |e|`, an
            // error-union's success/error (part 3) — in the matching branch. See LowerIfCapture.
            case Zig.StmtIfCapture f:        return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, null, null);
            case Zig.StmtIfCaptureElse f:    return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg9, null);
            case Zig.StmtIfCaptureErrElse f: return LowerIfCapture(f.Arg2, Tok(f.Arg5), f.Arg7, f.Arg12, Tok(f.Arg10));
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

            // `while (opt) |x| body` — optional payload capture-while (Milestone M, part 2). See
            // LowerWhileCapture (desugars to `while (true) { … if (has) { bind; body } else break; }`).
            case Zig.StmtWhileCapture w: return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg7);
            // `while (opt) |x| body else elsebody` — the else runs on natural exit (payload null / a
            // user `break` skips it, matching Zig). `while (eu) |x| body else |e| elsebody` — the error
            // branch binds `e` and runs elsebody, then exits.
            case Zig.StmtWhileCaptureElse w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg7, (w.Arg9, null));
            case Zig.StmtWhileCaptureErrElse w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg7, (w.Arg12, Tok(w.Arg10)));
            // `while (opt) |x| : (cont) body` — capture-while with a continue-expression → the C `For`
            // IR (post = cont), so `continue` runs the cont. The assign form builds an `Assign` post
            // (like stmtWhileContAssign); the bare-expr form a plain one.
            case Zig.StmtWhileCaptureCont w:
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg11, null, LowerExpr(w.Arg9));
            case Zig.StmtWhileCaptureContAssign w:
            {
                var cLhs = LowerExpr(w.Arg9);
                var cRhs = LowerExpr(w.Arg11);
                return LowerWhileCapture(w.Arg2, Tok(w.Arg5), w.Arg13, null,
                    new Assign(null, cLhs, cRhs) { Type = cLhs.Type });
            }

            // `break;` / `continue;` — reuse the C IR loop-control nodes (the C# backend
            // renders them verbatim; valid inside the while/for forms above).
            case Zig.StmtBreak:    return new Break();
            case Zig.StmtContinue: return new Continue();

            // `break v;` — an unlabeled value break (Milestone Y, part 2): yield `v` from the innermost
            // value-position loop (`while/for … else`). Assigns its result temp and jumps to its end
            // label (skipping the loop's `else`).
            case Zig.StmtBreakValue b: return LowerBreakValue(b.Arg1);

            // `break :blk v;` — yield a value from the enclosing labeled value-block (Milestone L,
            // part 2). Assigns the block's result temp and jumps to its end label (LowerLabeledBreak).
            case Zig.StmtBreakLabelValue b: return LowerLabeledBreak(Tok(b.Arg2), b.Arg3);

            // `lbl: while/for (…) { … }` — a labeled loop (Milestone L, part 3); `break :lbl;` /
            // `continue :lbl;` exit / next-iterate it (possibly an OUTER loop) via a goto.
            case Zig.LabeledLoop ll:       return LowerLabeledLoop(Tok(ll.Arg0), ll.Arg2);
            case Zig.StmtBreakLabel b:     return LowerLabeledLoopJump(Tok(b.Arg2), isContinue: false);
            case Zig.StmtContinueLabel c:  return LowerLabeledLoopJump(Tok(c.Arg2), isContinue: true);

            // `inline for (lo..hi) |i| body` — comptime loop UNROLLING (Milestone T, part 3): replicate
            // the body once per index, with `i` bound to a compile-time constant in each copy.
            case Zig.InlineLoop il:        return LowerInlineLoop(il.Arg1);

            // `comptime var i = …;` — a compile-time value local (Milestone T, part 3). Tracked at
            // lowering time, no runtime decl; references substitute its current value (the `inline
            // while` counter).
            case Zig.ComptimeVarDecl cv:   return LowerComptimeVarDecl(cv.Arg1);

            // `comptime { … }` — a compile-time block statement (Milestone T, part 3): run the block's
            // comptime-value statements at lowering time, emit no runtime code.
            case Zig.ComptimeBlock cb:     return LowerComptimeBlock(cb.Arg1);

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
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg5), null, f.Arg7, byRef: false);
            // `for (s) |*x| body` — BY-REFERENCE element capture: x is a `*T` into the slice (Milestone M, part 4).
            case Zig.StmtForSliceRef f:  // for '(' Expr ')' '|' '*' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg6), null, f.Arg8, byRef: true);
            // `for (s, 0..) |x, i| body` — also bind the usize index (counter + start).
            case Zig.StmtForSliceIdx f:  // for '(' Expr ',' Expr '..' ')' '|' IDENT ',' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg8), (Tok(f.Arg10), LowerExpr(f.Arg4)), f.Arg12, byRef: false);
            // `for (s, 0..) |*x, i| body` — BY-REFERENCE element capture WITH the usize index
            // (Milestone Z): `x` is a `*T` into the slice (so `x.* = …` writes through), `i` the index.
            case Zig.StmtForSliceIdxRef f:  // for '(' Expr ',' Expr '..' ')' '|' '*' IDENT ',' IDENT '|' Stmt
                return LowerForSlice(LowerExpr(f.Arg2), Tok(f.Arg9), (Tok(f.Arg11), LowerExpr(f.Arg4)), f.Arg13, byRef: true);

            // A brace block in statement position (`Stmt -> Block`, pass-through).
            case Zig.Block:
            case Zig.BlockEmpty:        return LowerBlock(stmt);

            default: throw new IrUnsupportedException("zig statement: " + (stmt.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>Lower a Zig compound assignment <c>target op= value</c> to the shared
    /// <see cref="Assign"/> node with a non-null <see cref="BinOp"/>. The C# backend renders a
    /// native <c>target op= value</c>, so the lvalue is evaluated EXACTLY ONCE — correct binding
    /// for a side-effecting lvalue like <c>a[i()] += 1</c> or <c>p.* += 1</c> (a textual
    /// <c>x = x op y</c> desugar would double-evaluate it). The RHS is sink-typed to the target
    /// type for parity with plain <see cref="Zig.StmtAssign"/> (harmless for a numeric RHS).</summary>
    private CStmt CompoundAssign(Item targetItem, BinOp op, Item valueItem)
    {
        var target = LowerExpr(targetItem);
        var value = LowerExprSink(valueItem, target.Type);
        return new ExprStmt(new Assign(op, target, value) { Type = target.Type });
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

    // `const`/`var x = init;` — lower under an ANF hoist buffer so a catch/orelse in a SUB-expression
    // of the initializer (`const r = 1 + (a catch b());`) lifts to a temp before the decl. A
    // WHOLE-init catch / control-flow fallback is intercepted at the top of DeclOfInner (its own
    // statement lowering), leaving the buffer empty, so this wrap is a no-op for those.
    private CStmt DeclOf(Item nameTok, Item? typeItem, Item initExpr)
        => Hoisted(() => DeclOfInner(nameTok, typeItem, initExpr));

    private CStmt DeclOfInner(Item nameTok, Item? typeItem, Item initExpr)
    {
        // Compute the declared type FIRST: a result-located init (`.member` / `.{…}`) needs
        // it as its sink, so resolve the annotation before lowering the initializer.
        var declared = typeItem is not null ? LowerType(typeItem) : null;
        // `const x = blk: { … break :blk v; };` — a labeled value-block initializer. Temp-fill it
        // (the declared type, if any, is the sink), then bind `x` to the result temp.
        if (initExpr.Content is Zig.LabeledBlock lb)
        {
            return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, declared, temp =>
            {
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = temp.Type });
                return new DeclStmt(new List<LocalDecl> { new(sym, new VarRef(temp) { Type = temp.Type }) });
            });
        }
        // `const x = switch (y) { … blk: {…} };` / `const x = if (c) blk:{…} else …;` — a value-
        // position if/switch with a labeled-block (statement-producing) branch (Milestone Y, part 1):
        // temp-fill it as a statement (the declared type, if any, is the sink), then bind `x` to the
        // result temp. An all-simple-value if/switch is NOT intercepted here (it stays the clean C#
        // ternary / switch-expression).
        if (IsValueControlFlowStmt(initExpr))
        {
            return LowerValueControlFlowStmt(initExpr, declared, temp =>
            {
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = temp.Type });
                return new DeclStmt(new List<LocalDecl> { new(sym, new VarRef(temp) { Type = temp.Type }) });
            });
        }
        // `const v = a catch return [x];` / `const v = a orelse return [x];` (Milestone N, part 6) —
        // a control-flow fallback. On the error/none path the `return` runs (early-out); on success
        // `v` binds the unwrapped payload.
        if (IsControlFlowFallback(initExpr, out var cfLhs, out var cfCatch, out var cfRet))
        {
            return LowerControlFlowFallback(cfLhs, cfCatch, cfRet, payload =>
            {
                var ptype = declared ?? payload.Type ?? CType.Int;
                var psym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = ptype });
                return new DeclStmt(new List<LocalDecl> { new(psym, payload) });
            });
        }
        // `const v = a catch |e| b;` / `const v = a catch <side-effecting>;` (Milestone N, part 3) —
        // a capturing or side-effecting catch needs a statement context (the fallback runs only on
        // error; the capture binds `e`). Hoist + (bind) + initialize `v` from the lazy ternary. A
        // simple, side-effect-free `a catch b` (no capture) yields empty pre and falls through to the
        // normal path below — the eager `ErrUnion.Catch`, unchanged.
        if (initExpr.Content is Zig.CatchOp or Zig.CatchCapture)
        {
            string? capName = initExpr.Content is Zig.CatchCapture cc ? Tok(cc.Arg3) : null;
            var unionIt = initExpr.Content switch { Zig.CatchOp co => co.Arg0, Zig.CatchCapture c2 => c2.Arg0, _ => initExpr };
            var fbIt = initExpr.Content switch { Zig.CatchOp co => co.Arg2, Zig.CatchCapture c2 => c2.Arg5, _ => initExpr };
            var (pre, value) = LowerCatchValue(unionIt, capName, fbIt);
            // Capture always lowers structurally; a no-capture catch only when it hoisted (i.e. the
            // fallback was side-effecting). A simple no-capture catch (empty pre) falls through.
            if (capName is not null || pre.Count > 0)
            {
                var ctype = declared ?? value.Type ?? CType.Int;
                var csym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = ctype });
                pre.Add(new DeclStmt(new List<LocalDecl> { new(csym, value) }));
                return pre.Count == 1 ? pre[0] : new Seq(pre);
            }
        }
        // `var b: [N]T = …;` → a stackalloc'd C array (ArrayDecl → `T* b = stackalloc T[…]`), so
        // `b[i]` / `b[lo..hi]` reuse the array paths and yield a stack-backed slice. `undefined`
        // gives a zeroed extent; an array literal (`.{…}` / `[N]T{…}`, Milestone K) gives a
        // stackalloc with the element inits. The literal lowers BEFORE the symbol is declared, so
        // the array name isn't visible in its own initializer.
        if (declared is CType.Array arr)
        {
            // `[N:s]T` sentinel array (part 4; non-zero sentinel in Milestone Z): reserve ONE extra
            // trailing slot for the sentinel. The symbol keeps the logical `CType.Array(element, N)`
            // type (so `.len` / slicing exclude the sentinel); only the stackalloc extent (and the
            // literal's element list) grow by one. A ZERO sentinel rides C#'s zero-fill; a NON-ZERO
            // sentinel is written into the trailing slot explicitly.
            var sentinel = IsSentinelArrayType(typeItem);
            var sentVal = sentinel ? SentinelArrayValue(typeItem) : 0;
            if (initExpr.Content is Zig.UndefinedLit)
            {
                var n = (arr.Count ?? 0) + (sentinel ? 1 : 0);
                var sym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                var count = new LitInt(n.ToString(CultureInfo.InvariantCulture), n) { Type = CType.Int };
                var decl = new ArrayDecl(sym, arr.Element, count, null);   // C# zero-fills the stackalloc
                if (sentinel && sentVal != 0)
                {
                    // Zero-fill left the trailing slot at 0; write the actual non-zero sentinel there.
                    var nIdx = arr.Count ?? 0;
                    var slot = new DotCC.Ir.Index(new VarRef(sym) { Type = arr, IsLValue = true },
                        new LitInt(nIdx.ToString(CultureInfo.InvariantCulture), nIdx) { Type = CType.ULong })
                        { Type = arr.Element, IsLValue = true };
                    var write = new ExprStmt(new Assign(null, slot,
                        new LitInt(sentVal.ToString(CultureInfo.InvariantCulture), sentVal) { Type = CType.Int })
                        { Type = arr.Element });
                    return new Seq(new List<CStmt> { decl, write });
                }
                return decl;
            }
            var arrInit = LowerExprSink(initExpr, arr);
            // A `comptime EXPR` initializer is a ComptimeFold until pass 3 resolves it to a
            // StackArray (e.g. `const t: [N]T = comptime buildTable();`). Route it through the
            // ordinary DeclStmt path — the symbol is array-typed (renders `T*`), and the backend
            // hoists the resolved StackArray into `T* t = stackalloc T[]{…}` exactly as the
            // inferred-type form does. (A sentinel `[N:0]T` would need the +1 stackalloc slot, which
            // this path can't add, so a comptime sentinel array stays a clear error below.)
            if (arrInit is ComptimeFold && !sentinel)
            {
                var fsym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
                return new DeclStmt(new List<LocalDecl> { new(fsym, arrInit) });
            }
            if (arrInit is not StackArray sa)
            {
                throw new IrUnsupportedException(
                    $"a `[N]T` array local '{Tok(nameTok)}' must be initialized with an array literal (`.{{…}}` / `[N]T{{…}}`) or `undefined`");
            }
            var asym = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = arr });
            // Append the trailing sentinel → `stackalloc T[]{ e0, …, eN-1, s }` lays down N+1 slots.
            // The sentinel is an `int` literal (NOT element-typed): it renders bare (e.g. `0` / `5`),
            // which C#'s constant conversion accepts into any element type — an element-typed literal
            // on an unsigned/narrow element would render `0u`/`5u` and fail the implicit byte conversion.
            var elems = sentinel
                ? new List<CExpr>(sa.Elems) { new LitInt(sentVal.ToString(CultureInfo.InvariantCulture), sentVal) { Type = CType.Int } }
                : sa.Elems;
            var countLit = new LitInt(elems.Count.ToString(CultureInfo.InvariantCulture), elems.Count) { Type = CType.Int };
            return new ArrayDecl(asym, sa.Element, countLit, elems);
        }
        var init = LowerExprSink(initExpr, declared);
        var type = declared ?? init.Type ?? CType.Int;
        var sym2 = _symbols.Declare(new Symbol { Name = Tok(nameTok), Kind = SymKind.Var, Type = type });
        return new DeclStmt(new List<LocalDecl> { new(sym2, init) });
    }

    /// <summary>Lower a destructure binding <c>&lt;binder&gt;, &lt;binder&gt;… = e;</c> (Milestone G,
    /// extended in S). A binder is a fresh <c>const</c>/<c>var</c> (optionally typed <c>: T</c>), an
    /// existing lvalue, or a <c>_</c> discard. Two lowerings, picked by the RHS shape:
    /// <list type="bullet">
    /// <item>A tuple-LITERAL RHS (<c>.{e0, e1, …}</c>) is lowered ELEMENT-WISE in source order with NO
    /// snapshot temp — each element <c>e_i</c> is bound/assigned directly. This matches Zig's
    /// sequential destructuring, where an existing-lvalue write is visible to a LATER element's read
    /// (so <c>a, b = .{ b, a }</c> is NOT a swap: <c>a←b</c>, then <c>b←</c> the new <c>a</c>). New
    /// binders can't alias (Zig forbids shadowing), so the order is also faithful for them, and a typed
    /// binder drives its element's result location (sink).</item>
    /// <item>A non-literal tuple-valued RHS (a fn call, a tuple var) is evaluated ONCE into a fresh
    /// <c>__tupN</c> temp, then each binder reads its positional element (<c>__tupN.ItemK</c>) — single
    /// eval, and a value temp can't alias an lvalue being written.</item>
    /// </list>
    /// Emitted as a brace-less <see cref="Seq"/> so any new binders land in the ENCLOSING scope (a
    /// <see cref="Block"/> would wrongly scope them). The arity must match the binder count.</summary>
    private CStmt LowerDestructure(Zig.StmtDestructure d)
    {
        // Binders in source order: the leading one (Arg0) + the rest (the Arg2 list).
        var binders = new List<Item> { d.Arg0 };
        binders.AddRange(Flatten(d.Arg2));
        var stmts = new List<CStmt>();

        // RhsExpr is transparent, so d.Arg4.Content is the underlying literal/expr directly.
        if (IsPositionalTupleLiteral(d.Arg4, out var elemItems))
        {
            if (elemItems.Count != binders.Count)
            {
                throw new IrUnsupportedException(
                    $"zig destructure binds {binders.Count} name(s) but the literal has {elemItems.Count} element(s)");
            }
            // Element-wise, source order: each binder lowers its own element expr (a typed/lvalue
            // binder passes its type as the element's sink). No temp — preserves Zig's aliasing.
            for (int i = 0; i < binders.Count; i++)
            {
                stmts.Add(LowerDestructBinder(binders[i], elemItems[i], snapshotRead: null));
            }
            return new Seq(stmts);
        }

        var rhs = LowerExpr(d.Arg4);
        if (rhs.Type.Unqualified is not CType.Tuple tup)
        {
            throw new IrUnsupportedException(
                $"zig destructure `…, … = e` needs a tuple value; got {rhs.Type.Describe()}");
        }
        if (tup.Elements.Count != binders.Count)
        {
            throw new IrUnsupportedException(
                $"zig destructure binds {binders.Count} name(s) but the tuple has {tup.Elements.Count} element(s)");
        }
        // The single-eval temp: `var __tupN = e;`, then each binder reads `__tupN.ItemK`.
        var tmp = _symbols.Declare(new Symbol { Name = "__tup" + _tupleTempCounter++, Kind = SymKind.Var, Type = tup });
        stmts.Add(new DeclStmt(new List<LocalDecl> { new(tmp, rhs) }));
        var tmpRef = new VarRef(tmp) { Type = tup, IsLValue = true };
        for (int i = 0; i < binders.Count; i++)
        {
            var et = tup.Elements[i];
            var read = new TupleIndex(tmpRef, i, et) { Type = et };
            stmts.Add(LowerDestructBinder(binders[i], elemItem: null, snapshotRead: read));
        }
        return new Seq(stmts);
    }

    /// <summary>Emit one destructure binder's statement (Milestone G + S). A fresh <c>const</c>/<c>var</c>
    /// binder (optionally typed <c>: T</c>) declares a local; an existing-lvalue binder assigns through
    /// it; <c>_</c> discards. The source value is either the tuple-literal element <paramref name="elemItem"/>
    /// (lowered at the binder's declared/lvalue type as its sink) or the snapshot read
    /// <paramref name="snapshotRead"/> (coerced to the binder's type). Exactly one of the two is non-null.</summary>
    private CStmt LowerDestructBinder(Item binder, Item? elemItem, CExpr? snapshotRead)
    {
        switch (binder.Content)
        {
            case Zig.DestructBindConst c:      return DeclareDestructLocal(Tok(c.Arg1), null, elemItem, snapshotRead);
            case Zig.DestructBindVar v:        return DeclareDestructLocal(Tok(v.Arg1), null, elemItem, snapshotRead);
            case Zig.DestructBindConstTyped c: return DeclareDestructLocal(Tok(c.Arg1), LowerType(c.Arg3), elemItem, snapshotRead);
            case Zig.DestructBindVarTyped v:   return DeclareDestructLocal(Tok(v.Arg1), LowerType(v.Arg3), elemItem, snapshotRead);
            case Zig.DestructBindLValue lv:    return AssignDestructTarget(lv.Arg0, elemItem, snapshotRead);
            default:
                throw new IrUnsupportedException(
                    "zig destructure binder: " + (binder.Content?.GetType().Name ?? "null"));
        }
    }

    /// <summary>Declare a fresh destructure local <c>name</c>. With a <paramref name="declType"/> the
    /// element lowers at that type as its sink (literal RHS) or the snapshot read is coerced to it;
    /// without one the type is inferred from the element/read.</summary>
    private CStmt DeclareDestructLocal(string name, CType? declType, Item? elemItem, CExpr? snapshotRead)
    {
        CExpr value = elemItem is not null
            ? (declType is not null ? LowerExprSink(elemItem, declType) : LowerExpr(elemItem))
            : (declType is not null ? CoerceRead(snapshotRead!, declType) : snapshotRead!);
        var symType = declType ?? value.Type;
        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = symType });
        return new DeclStmt(new List<LocalDecl> { new(sym, value) });
    }

    /// <summary>Assign a destructure element to an existing lvalue (or discard it for <c>_</c>). The
    /// lvalue is the sink for a literal element; a snapshot read is coerced to the lvalue type. A
    /// <c>_</c> binder just evaluates the element for its side effects (a value-context discard).</summary>
    private CStmt AssignDestructTarget(Item lvalueItem, Item? elemItem, CExpr? snapshotRead)
    {
        if (lvalueItem.Content is Zig.Ident id && Tok(id.Arg0) == "_")
        {
            // `_` — evaluate the element/read; ExprStmt renders a `_ = …` discard when it isn't a call.
            return new ExprStmt(elemItem is not null ? LowerExpr(elemItem) : snapshotRead!);
        }
        var target = LowerExpr(lvalueItem);
        CExpr value = elemItem is not null
            ? LowerExprSink(elemItem, target.Type)
            : CoerceRead(snapshotRead!, target.Type);
        return new ExprStmt(new Assign(null, target, value) { Type = target.Type });
    }

    /// <summary>Coerce a snapshot read (a <c>__tupN.ItemK</c> CExpr) to a binder's declared/lvalue
    /// type, inserting a <see cref="Cast"/> only when the types differ (a no-op when they match).</summary>
    private static CExpr CoerceRead(CExpr read, CType to)
        => read.Type.Unqualified.Equals(to.Unqualified) ? read : new Cast(to, read) { Type = to };

    /// <summary>True when <paramref name="rhsItem"/> is a positional tuple literal (<c>.{e0, e1, …}</c>),
    /// yielding its element expressions in <paramref name="elemItems"/>. A named <c>.{.f = v}</c> (a
    /// struct literal) or the empty <c>.{}</c> is not a positional tuple literal → false (the snapshot
    /// path then handles / rejects it).</summary>
    private static bool IsPositionalTupleLiteral(Item rhsItem, out IReadOnlyList<Item> elemItems)
    {
        elemItems = [];
        if (rhsItem.Content is not Zig.AnonStructInit a) { return false; }
        var fields = Flatten(a.Arg2);
        if (fields.Count == 0) { return false; }
        var items = new List<Item>(fields.Count);
        foreach (var f in fields)
        {
            if (f.Content is not Zig.FieldInitPositional pos) { return false; }   // a named field → struct literal
            items.Add(pos.Arg0);
        }
        elemItems = items;
        return true;
    }

    /// <summary>Lower a labeled block used as a VALUE — <c>blk: { …; break :blk v; }</c> — at a
    /// statement RHS position (Milestone L, part 2). A statement form can't be an expression, so we
    /// use the roadmap's temp-fill: a fresh result temp (<c>__blkN</c>) is declared, the block body
    /// is lowered with each <c>break :blk v</c> rewritten (in <see cref="LowerLabeledBreak"/>) to
    /// "assign the temp, <c>goto __blkN_end</c>", an end label follows the body, and <paramref
    /// name="consume"/> builds the surrounding statement that reads the temp (the decl / return /
    /// assignment). The temp's type is the <paramref name="sink"/> when known (an annotated decl, a
    /// function return, an lvalue), else the first <c>break</c> value's type. The temp is declared in
    /// the ENCLOSING scope (before the body's), so a <c>break</c> inside can assign it and the
    /// consumer outside can read it. The end label wraps an empty block (<c>__blkN_end: { }</c>) so a
    /// following declaration is legal — a C# label can't directly precede a declaration (CS1023).</summary>
    private CStmt LowerLabeledValueBlock(string label, Item blockItem, CType? sink, Func<Symbol, CStmt> consume)
    {
        var n = _blockLabelCounter++;
        var endLabel = "__blk" + n + "_end";
        // Declared with a provisional type; retyped below once the result type is resolved. The
        // counter-unique name never collides, so declaring it up front is safe.
        var temp = _symbols.Declare(new Symbol { Name = "__blk" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var target = new LabeledBlockTarget { Label = label, Temp = temp, EndLabel = endLabel, Sink = sink, ResultType = sink };
        _labeledBlocks.Push(target);
        var body = LowerBlock(blockItem);   // each `break :label v` reads `target` via LowerLabeledBreak
        _labeledBlocks.Pop();
        var resultType = target.ResultType
            ?? throw new IrUnsupportedException(
                $"labeled block ':{label}' must yield a value via `break :{label} <value>;`");
        temp.Type = resultType;
        var stmts = new List<CStmt>
        {
            // `T __blkN = default;` — default-initialized so C# definite-assignment is satisfied even
            // though every real path assigns via a `break` (the gotos defeat flow analysis).
            new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = resultType }) }),
            body,
            new Labeled(endLabel, new Block(new List<CStmt>())),
            consume(temp),
        };
        return new Seq(stmts);
    }

    /// <summary>Lower an unlabeled <c>break v;</c> (Milestone Y, part 2) — yield <paramref
    /// name="valueItem"/> from the INNERMOST value-position loop on the stack.</summary>
    private CStmt LowerBreakValue(Item valueItem)
    {
        if (_loopValues.Count == 0)
        {
            throw new IrUnsupportedException(
                "`break <value>;` is only valid inside a value-position `while`/`for … else` loop");
        }
        return BuildLoopBreakValue(_loopValues.Peek(), valueItem);
    }

    /// <summary>Lower <c>break :label v;</c> (Milestone L, part 2; extended in Milestone Y, part 2) —
    /// yield <paramref name="valueItem"/> from the enclosing construct named <paramref name="label"/>:
    /// a labeled value-position loop (<c>lbl: while/for … else</c>) or a labeled value-block. Assigns
    /// that construct's result temp, then <c>goto</c> its end label. Resolves innermost-first; the
    /// value is sink-typed to the result type when known, and the first such break fixes that type.</summary>
    private CStmt LowerLabeledBreak(string label, Item valueItem)
    {
        // A labeled value-position loop (`lbl: while/for … else`, Milestone Y part 2) — innermost-first.
        foreach (var lv in _loopValues)
        {
            if (lv.Label == label) { return BuildLoopBreakValue(lv, valueItem); }
        }
        // A value break targeting a labeled STATEMENT loop (no `else` → not a value loop) is still a
        // clear deferred error — and is invalid Zig anyway (a value `break` needs a value loop).
        if (_labeledBlocks.All(t => t.Label != label) && _labeledLoops.Any(l => l.Label == label))
        {
            throw new IrUnsupportedException(
                $"`break :{label} <value>` yields a value from a labeled loop, but ':{label}' is a statement loop " +
                "with no `else` clause — give it an `else` to make it a value loop");
        }
        var target = _labeledBlocks.FirstOrDefault(t => t.Label == label)
            ?? throw new IrUnsupportedException(
                $"`break :{label}` has no enclosing labeled block ':{label}'");
        var value = target.Sink is { } sk ? LowerExprSink(valueItem, sk) : LowerExpr(valueItem);
        target.ResultType ??= value.Type;
        var tref = new VarRef(target.Temp) { Type = target.ResultType, IsLValue = true };
        // A `Block` (not a brace-less `Seq`): this pair is a single statement, and as an `if`/`while`
        // body the backend braces a Block but renders a Seq brace-less — which would leave the `goto`
        // unconditional (`if (c) temp = v; goto end;`). A `goto` out of the block to the enclosing
        // end label is legal C#; the assign-and-goto declare nothing, so the extra scope is harmless.
        return new Block(new List<CStmt>
        {
            new ExprStmt(new Assign(null, tref, value) { Type = target.ResultType }),
            new Goto(target.EndLabel),
        });
    }

    /// <summary>Lower a labeled loop <c>lbl: while/for (…) { … }</c> (Milestone L, part 3). The loop
    /// itself lowers normally (its record name is unchanged by the grammar's <c>LoopStmt</c> factor);
    /// while its body is lowered, an enclosing <see cref="LabeledLoopTarget"/> lets a <c>break :lbl</c>
    /// / <c>continue :lbl</c> within (possibly inside a nested loop) resolve to a <c>goto</c>. After
    /// lowering, the continue label is appended to the END of the loop body (so a <c>goto</c> there
    /// falls into the loop's natural iteration step) and the break label is placed just AFTER the loop
    /// — each only when actually referenced, to avoid a C# unreferenced-label warning.</summary>
    private CStmt LowerLabeledLoop(string label, Item loopItem)
    {
        var n = _loopLabelCounter++;
        var t = new LabeledLoopTarget
        {
            Label = label, BreakLabel = "__loop" + n + "_brk", ContLabel = "__loop" + n + "_cont",
        };
        _labeledLoops.Push(t);
        var loop = LowerStmt(loopItem);   // a While / For / DoWhile; break/continue :lbl read `t`
        _labeledLoops.Pop();
        if (t.ContUsed) { loop = WithLoopBody(loop, body => AppendLabel(body, t.ContLabel)); }
        var stmts = new List<CStmt> { loop };
        if (t.BreakUsed) { stmts.Add(new Labeled(t.BreakLabel, new Block(new List<CStmt>()))); }
        return new Seq(stmts);
    }

    /// The largest number of iterations <c>inline for</c> will unroll — a backstop on an absurd
    /// <c>inline for (0..1_000_000)</c> (each iteration emits a full body copy, so the cap is far
    /// tighter than the comptime-array cap). A real <c>inline</c> loop is a handful to a few dozen.
    private const long InlineUnrollCap = 1 << 12;   // 4096

    /// <summary>Lower an <c>inline for</c> (Milestone T, part 3) by UNROLLING: the body is replicated
    /// once per iteration, each copy a block <c>{ const cap = …; body }</c> binding the capture to that
    /// iteration's value. The loop vanishes — no runtime <c>for</c> — and because each copy is plain
    /// straight-line IR, it works identically whether the enclosing function runs at runtime (the
    /// copies execute in order) or is itself <c>comptime</c>-called (the interpreter walks the unrolled
    /// copies, the per-copy binding entering its frame). Two forms are unrolled:
    /// <list type="bullet">
    /// <item><c>for (lo..hi) |i|</c> — the COUNTED range; the bounds fold to compile-time constants
    /// and the capture binds to each constant index.</item>
    /// <item><c>for (arr) |x|</c> — over a fixed <c>[N]T</c> array of comptime-known length; the
    /// capture binds to each element by value (<c>const x = arr[k];</c>).</item>
    /// </list>
    /// An <c>inline while</c>, an <c>inline for</c> over a slice (length not comptime-known) or the
    /// indexed <c>|x, i|</c> / by-ref <c>|*x|</c> forms, a non-constant range bound, or a body that
    /// <c>break</c>s/<c>continue</c>s the (now-absent) loop are clear deferred errors.</summary>
    private CStmt LowerInlineLoop(Item loopItem)
    {
        switch (loopItem.Content)
        {
            // `inline for (lo..hi) |i|` — the counted range. Bounds fold NOW (during this pass) via the
            // const-eval interpreter — literals / constant arithmetic / sizeof — so a forward-referenced
            // comptime CALL in a bound (whose callee may not be lowered yet) is intentionally not folded.
            case Zig.StmtForRange f:
            {
                if (_ir.ConstEval(LowerExpr(f.Arg2)) is not { } lo || _ir.ConstEval(LowerExpr(f.Arg4)) is not { } hi)
                {
                    throw new IrUnsupportedException(
                        "`inline for` bounds must be compile-time-known integer constants");
                }
                if (hi < lo)
                {
                    throw new IrUnsupportedException(
                        $"`inline for` upper bound ({hi}) is below the lower bound ({lo})");
                }
                // The capture is the usize index, bound to a literal in each copy.
                return UnrollInlineFor(hi - lo, Tok(f.Arg7), CType.ULong, f.Arg9,
                    k => new LitInt((lo + k).ToString(System.Globalization.CultureInfo.InvariantCulture), lo + k) { Type = CType.ULong });
            }

            // `inline for (arr) |x|` — over a fixed array of comptime-known length. The operand must be
            // a named array variable (so each element read `arr[k]` is side-effect-free across copies);
            // the capture binds to the element by value.
            case Zig.StmtForSlice fs:
            {
                var operand = LowerExpr(fs.Arg2);
                if (operand.Type.Unqualified is not CType.Array arr || arr.Count is not int n)
                {
                    throw new IrUnsupportedException(
                        "`inline for` over a value requires a fixed-size array `[N]T` of comptime-known "
                        + "length (a slice's length is a runtime value)");
                }
                if (operand is not VarRef)
                {
                    throw new IrUnsupportedException(
                        "`inline for` over an array requires a named array variable in V1 (so each "
                        + "element read is side-effect-free under unrolling)");
                }
                // Re-lower the operand per copy (a VarRef is idempotent) so each `arr[k]` is its own node.
                return UnrollInlineFor(n, Tok(fs.Arg5), arr.Element, fs.Arg7,
                    k => new DotCC.Ir.Index(LowerExpr(fs.Arg2), new LitInt(k.ToString(System.Globalization.CultureInfo.InvariantCulture), k) { Type = CType.ULong }) { Type = arr.Element });
            }

            // `inline while (cond) : (i = i + step) body` — comptime-UNROLLED while (Milestone T,
            // part 3). The loop counter must be a `comptime var` mutated by the continue-expression;
            // each round folds the condition (with the counter substituted in), unrolls a body copy,
            // then applies the continue-expr to advance the counter — all at lowering time.
            case Zig.StmtWhileContAssign w:
                return UnrollInlineWhile(w.Arg2, w.Arg6, w.Arg8, w.Arg10);

            default:
                throw new IrUnsupportedException(
                    "`inline` is only supported on a counted `for (lo..hi) |i|` range loop, a "
                    + "`for (arr) |x|` over a fixed array, or an `inline while (c) : (i = …)` with a "
                    + "`comptime var` counter (comptime unrolling) — the indexed `|x, i|` / by-ref "
                    + "`|*x|` `for` forms, `inline for` over a slice, and a bare/expr-cont `inline "
                    + "while` are not supported yet");
        }
    }

    /// <summary>Unroll an <c>inline while (cond) : (lhs = rhs) body</c> (Milestone T, part 3). The
    /// continue-expression's target must be a <c>comptime var</c> counter (so its value is known at
    /// lowering time). Each round: fold <paramref name="condItem"/> (with the counter's current value
    /// substituted) — stop when false; lower a body copy; fold the continue-expression's RHS and store
    /// it back as the counter's new value. The loop vanishes (no runtime <c>while</c>); a bare
    /// <c>break</c>/<c>continue</c> in the body, a non-comptime-var counter, or a non-foldable
    /// condition / continue value are clear errors. The unroll count is capped (a non-terminating
    /// comptime condition otherwise loops forever).</summary>
    private CStmt UnrollInlineWhile(Item condItem, Item contLhsItem, Item contRhsItem, Item bodyItem)
    {
        // The continue-expr target must resolve (WITHOUT substitution) to a tracked comptime var.
        if (contLhsItem.Content is not Zig.Ident contId
            || _symbols.Resolve(Tok(contId.Arg0)) is not { } contSym
            || !_comptimeVars.ContainsKey(contSym))
        {
            throw new IrUnsupportedException(
                "`inline while` requires a `comptime var` loop counter advanced by the "
                + "continue-expression (`comptime var i = …; inline while (i < N) : (i = i + step) { … }`)");
        }

        var copies = new List<CStmt>();
        while (true)
        {
            if (_ir.ConstEval(LowerExpr(condItem)) is not { } cond)
            {
                throw new IrUnsupportedException("`inline while` condition must be compile-time-known");
            }
            if (cond == 0) { break; }
            if (copies.Count >= InlineUnrollCap)
            {
                throw new IrUnsupportedException(
                    $"`inline while` exceeded the unroll cap ({InlineUnrollCap}) — a non-terminating comptime condition?");
            }
            // Unroll one body copy (the comptime counter substitutes to its current value within it).
            _symbols.EnterScope();
            var body = LowerStmt(bodyItem);
            _symbols.ExitScope();
            if (HasLoopEscape(body))
            {
                throw new IrUnsupportedException(
                    "`break`/`continue` inside an `inline while` body is not supported yet (the loop is unrolled)");
            }
            copies.Add(body is Block ? body : new Block(new List<CStmt> { body }));
            // Advance the counter: fold the continue-expr RHS (with the current value), store it back.
            if (_ir.ConstEval(LowerExpr(contRhsItem)) is not { } next)
            {
                throw new IrUnsupportedException("`inline while` continue-expression must be compile-time-known");
            }
            _comptimeVars[contSym] = (next, _comptimeVars[contSym].Type);
        }
        return new Seq(copies);
    }

    /// <summary>Lower a <c>comptime var</c> / <c>comptime const</c> declaration (Milestone T, part 3):
    /// fold the initializer to a compile-time integer and track it by Symbol identity, emitting NO
    /// runtime declaration — references substitute its current value (see the <c>Zig.Ident</c> case).
    /// The declared type is the explicit annotation or the initializer's type. Only the integer value
    /// subset is supported (the firewall — no comptime pointer/aggregate var).</summary>
    private CStmt LowerComptimeVarDecl(Item varDeclItem)
    {
        TrackComptimeVar(varDeclItem);
        return new Seq(new List<CStmt>());   // comptime-only — no runtime declaration
    }

    /// <summary>Fold a <c>var</c>/<c>const</c> declaration's initializer to a compile-time integer and
    /// track it by Symbol identity in <see cref="_comptimeVars"/> (so references substitute the value).
    /// Shared by <c>comptime var</c> statements and the bodies of a <c>comptime { … }</c> block, where
    /// every declaration is a compile-time value. Only the integer value subset (the firewall).</summary>
    private void TrackComptimeVar(Item varDeclItem)
    {
        string name;
        Item initItem;
        Item? typeItem = null;
        switch (varDeclItem.Content)
        {
            case Zig.ConstDecl d:      name = Tok(d.Arg1); initItem = d.Arg3; break;
            case Zig.VarDecl d:        name = Tok(d.Arg1); initItem = d.Arg3; break;
            case Zig.ConstDeclTyped d: name = Tok(d.Arg1); typeItem = d.Arg3; initItem = d.Arg5; break;
            case Zig.VarDeclTyped d:   name = Tok(d.Arg1); typeItem = d.Arg3; initItem = d.Arg5; break;
            default:
                throw new IrUnsupportedException(
                    "`comptime` here is only supported on a `var`/`const` value declaration");
        }
        var initExpr = LowerExpr(initItem);
        var ctype = typeItem is { } ti ? LowerType(ti) : initExpr.Type;
        if (_ir.ConstEval(initExpr) is not { } v)
        {
            throw new IrUnsupportedException(
                $"`comptime var {name}` initializer must be a compile-time-known integer constant");
        }
        var sym = _symbols.Declare(new Symbol { Name = name, Kind = SymKind.Var, Type = ctype });
        _comptimeVars[sym] = (v, ctype);
    }

    /// <summary>Lower a <c>comptime { … }</c> block statement (Milestone T, part 3): EXECUTE the block
    /// at lowering time, folding its comptime-value statements (var/const decls, assignments to comptime
    /// vars, comptime <c>while</c> loops) and mutating any enclosing <c>comptime var</c> in place. It
    /// emits NO runtime code — its only effect is on comptime values, which later references substitute.
    /// Block-local comptime vars are scoped so they don't leak past the block.</summary>
    private CStmt LowerComptimeBlock(Item blockItem)
    {
        _symbols.EnterScope();
        ExecuteComptimeStmt(blockItem);
        _symbols.ExitScope();
        return new Seq(new List<CStmt>());   // compile-time-only — nothing runs at runtime
    }

    /// <summary>Execute one statement of a <c>comptime { … }</c> block at lowering time. Supports the
    /// compile-time value subset: nested blocks, <c>var</c>/<c>const</c> decls (tracked as comptime),
    /// assignment to a comptime var (folded + stored), and a <c>while</c> loop (interpreted, the body's
    /// assignments updating comptime vars). Any other statement — or an assignment to a non-comptime
    /// target — is a clear error (the firewall: no runtime effect, no pointer/aggregate mutation).</summary>
    private void ExecuteComptimeStmt(Item s)
    {
        switch (s.Content)
        {
            case Zig.Block b:
                foreach (var st in Flatten(b.Arg1)) { ExecuteComptimeStmt(st); }
                break;
            case Zig.BlockEmpty:
                break;
            case Zig.ComptimeVarDecl cv:
                TrackComptimeVar(cv.Arg1);
                break;
            case Zig.ConstDecl or Zig.VarDecl or Zig.ConstDeclTyped or Zig.VarDeclTyped:
                // Inside a comptime block every declaration is a compile-time value.
                TrackComptimeVar(s);
                break;
            case Zig.StmtAssign a:          // lhs = rhs
                ExecuteComptimeAssign(a.Arg0, a.Arg2);
                break;
            // A comptime `while (cond) : (cont) body` / `while (cond) body` — interpreted (the cont or
            // the body mutates the counter). `inline while` here would unroll-to-IR (wrong in a comptime
            // block); the plain `while` IS the comptime loop.
            case Zig.StmtWhileContAssign w:
                ExecuteComptimeWhile(w.Arg2, (w.Arg6, w.Arg8), w.Arg10);
                break;
            case Zig.StmtWhile w:
                ExecuteComptimeWhile(w.Arg2, null, w.Arg4);
                break;
            default:
                throw new IrUnsupportedException(
                    $"comptime block: statement '{s.Content?.GetType().Name}' is not supported — only "
                    + "var/const decls, assignments to a comptime var, and `while` loops run at comptime");
        }
    }

    /// <summary>Apply a comptime assignment <c>lhs = rhs</c> inside a <c>comptime { … }</c> block: the
    /// target must resolve to a tracked comptime var (its bare name, NOT substituted), the value folds
    /// (with comptime vars substituted), and the result is stored back.</summary>
    private void ExecuteComptimeAssign(Item lhsItem, Item rhsItem)
    {
        if (lhsItem.Content is not Zig.Ident id
            || _symbols.Resolve(Tok(id.Arg0)) is not { } sym
            || !_comptimeVars.ContainsKey(sym))
        {
            throw new IrUnsupportedException(
                "comptime block: an assignment target must be a `comptime var` (no runtime store at comptime)");
        }
        if (_ir.ConstEval(LowerExpr(rhsItem)) is not { } v)
        {
            throw new IrUnsupportedException("comptime block: assignment value must be compile-time-known");
        }
        _comptimeVars[sym] = (v, _comptimeVars[sym].Type);
    }

    /// <summary>Interpret a comptime <c>while</c> at lowering time: fold the condition each round (with
    /// comptime vars substituted), execute the body's comptime statements, then apply the optional
    /// continue-expression — until the condition is false. Step-capped (a non-terminating comptime
    /// condition otherwise loops forever).</summary>
    private void ExecuteComptimeWhile(Item condItem, (Item Lhs, Item Rhs)? cont, Item bodyItem)
    {
        var steps = 0;
        while (true)
        {
            if (_ir.ConstEval(LowerExpr(condItem)) is not { } cond)
            {
                throw new IrUnsupportedException("comptime block: `while` condition must be compile-time-known");
            }
            if (cond == 0) { break; }
            if (++steps > InlineUnrollCap)
            {
                throw new IrUnsupportedException(
                    $"comptime block: `while` exceeded {InlineUnrollCap} iterations — a non-terminating comptime condition?");
            }
            ExecuteComptimeStmt(bodyItem);
            if (cont is { } c) { ExecuteComptimeAssign(c.Lhs, c.Rhs); }
        }
    }

    /// <summary>The literal a <c>comptime var</c> reference substitutes to — its current value at the
    /// declared type (a negative value as <c>-(magnitude)</c>, mirroring how the interpreter splices a
    /// signed constant).</summary>
    private static CExpr ComptimeVarLit(long v, CType t)
    {
        if (v >= 0)
        {
            return new LitInt(v.ToString(System.Globalization.CultureInfo.InvariantCulture), v) { Type = t };
        }
        var mag = -(System.Int128)v;
        return new Unary(UnOp.Neg, new LitInt(mag.ToString(System.Globalization.CultureInfo.InvariantCulture), v == long.MinValue ? null : -v) { Type = t }) { Type = t };
    }

    /// <summary>Build the unrolled copies of an <c>inline for</c> body: for each of
    /// <paramref name="count"/> iterations, a block <c>{ const capture = initFor(k); body }</c> with the
    /// capture freshly declared in its own scope (sibling blocks may reuse the name in C#; the symbol
    /// table's CS0136 rename covers any leak regardless). A bare <c>break</c>/<c>continue</c> in the
    /// body is rejected — unrolling removes the loop, so it would have no target. The count is capped to
    /// bound emitted-code size.</summary>
    private CStmt UnrollInlineFor(long count, string captureName, CType captureType, Item bodyItem, System.Func<long, CExpr> initFor)
    {
        if (count > InlineUnrollCap)
        {
            throw new IrUnsupportedException(
                $"`inline for` would unroll {count} iterations, exceeding the cap ({InlineUnrollCap})");
        }
        var copies = new List<CStmt>((int)count);
        for (long k = 0; k < count; k++)
        {
            _symbols.EnterScope();
            var sym = _symbols.Declare(new Symbol { Name = captureName, Kind = SymKind.Var, Type = captureType });
            var decl = new DeclStmt(new List<LocalDecl> { new(sym, initFor(k)) });
            var body = LowerStmt(bodyItem);
            _symbols.ExitScope();
            if (HasLoopEscape(body))
            {
                throw new IrUnsupportedException(
                    "`break`/`continue` inside an `inline for` body is not supported yet (the loop is "
                    + "unrolled, so there is no enclosing loop to target)");
            }
            copies.Add(new Block(new List<CStmt> { decl, body }));
        }
        return new Seq(copies);
    }

    /// <summary>Does this statement contain a bare <c>break</c>/<c>continue</c> that would target an
    /// enclosing loop (as opposed to one nested inside it)? Used to reject loop-control inside an
    /// <c>inline for</c> body, where unrolling removes the loop. Descends into blocks / sequences /
    /// <c>if</c> branches / labeled statements, but NOT into a nested loop or switch — a
    /// break/continue there binds to that construct, not to the unrolled <c>inline for</c>.</summary>
    private static bool HasLoopEscape(CStmt s) => s switch
    {
        Break or Continue => true,
        Block b           => b.Stmts.Any(HasLoopEscape),
        Seq q             => q.Stmts.Any(HasLoopEscape),
        If i              => HasLoopEscape(i.Then) || (i.Else is { } e && HasLoopEscape(e)),
        Labeled l         => HasLoopEscape(l.Body),
        _                 => false,
    };

    /// <summary>Rebuild a loop statement with its body transformed by <paramref name="f"/> — used to
    /// append a labeled loop's continue label to the end of the body. Defensive: anything that isn't a
    /// loop is returned untouched (the grammar's <c>LoopStmt</c> guarantees a loop here).</summary>
    private static CStmt WithLoopBody(CStmt loop, Func<CStmt, CStmt> f) => loop switch
    {
        While w  => new While(w.Cond, f(w.Body)),
        For fr   => new For(fr.Init, fr.Cond, fr.Post, f(fr.Body)),
        DoWhile d => new DoWhile(f(d.Body), d.Cond),
        _ => loop,
    };

    /// <summary>Append a (no-op-bodied) label to a statement, flattening into an existing block so the
    /// label sits at the body's end. The label wraps an empty <see cref="Block"/> (<c>lbl: { }</c>)
    /// because a C# label can't directly precede a declaration (CS1023).</summary>
    private static CStmt AppendLabel(CStmt body, string label)
    {
        var labeled = new Labeled(label, new Block(new List<CStmt>()));
        var stmts = body is Block b ? new List<CStmt>(b.Stmts) : new List<CStmt> { body };
        stmts.Add(labeled);
        return new Block(stmts);
    }

    /// <summary>Lower <c>break :lbl;</c> / <c>continue :lbl;</c> (Milestone L, part 3) to a <c>goto</c>
    /// to the enclosing labeled loop's break / continue label (resolved innermost-first, marking the
    /// label used so it gets emitted). A label that names a value-block (not a loop) is a clear error
    /// — a loop <c>break</c>/<c>continue</c> can't target a value-block.</summary>
    private CStmt LowerLabeledLoopJump(string label, bool isContinue)
    {
        var t = _labeledLoops.FirstOrDefault(l => l.Label == label);
        if (t is null)
        {
            var what = isContinue ? "continue" : "break";
            if (_labeledBlocks.Any(b => b.Label == label))
            {
                throw new IrUnsupportedException(
                    $"`{what} :{label}` targets a labeled block, but a labeled block isn't a loop (use `break :{label} <value>;` to yield its value)");
            }
            throw new IrUnsupportedException($"`{what} :{label}` has no enclosing labeled loop ':{label}'");
        }
        if (isContinue) { t.ContUsed = true; return new Goto(t.ContLabel); }
        t.BreakUsed = true;
        return new Goto(t.BreakLabel);
    }

    /// <summary>Lower a payload-capturing <c>if (cond) |x| then [else …]</c> (Milestone M). The
    /// branch test and the binding depend on the condition's lowered type:
    /// <list type="bullet">
    /// <item>a value optional <c>?T</c> (<see cref="CType.Optional"/>) → test <c>__cap.HasValue</c>,
    /// bind <c>x = __cap.Value</c> at the top of the then-branch;</item>
    /// <item>a niche optional pointer (lowered to a bare <c>T*</c>) → test the pointer for non-null
    /// (the <c>Cond.B(void*)</c> overload), bind <c>x = __cap</c> (the unwrapped pointer is the
    /// same value);</item>
    /// <item>an error union <c>!T</c> (<see cref="CType.ErrorUnion"/>) → bind the success payload to
    /// <c>x</c> in the then-branch and (with <c>else |e|</c>) the error code to <c>e</c> in the
    /// else-branch — a value inspection of <c>.IsErr</c>, never a propagating <c>try</c>.</item>
    /// </list>
    /// The condition is hoisted to a single-eval temp unless it is already a bare variable (the test
    /// and the binding both read it). A capture name of <c>_</c> tests without binding. An
    /// <paramref name="errCapName"/> (an <c>else |e|</c>) is only valid on an error union.</summary>
    private CStmt LowerIfCapture(Item condItem, string capName, Item thenItem, Item? elseItem, string? errCapName)
    {
        var cond = LowerExpr(condItem);
        var ct = cond.Type.Unqualified;

        // Hoist a side-effecting condition to a single-eval temp (a bare var is already re-readable).
        var pre = new List<CStmt>();
        CExpr condRef;
        if (cond is VarRef)
        {
            condRef = cond;
        }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__cap", Kind = SymKind.Var, Type = cond.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, cond) }));
            condRef = new VarRef(tmp) { Type = cond.Type, IsLValue = true };
        }

        CExpr test;
        CExpr payloadInit;
        CType payloadType;
        if (ct is CType.Optional opt)
        {
            if (errCapName is not null)
            {
                throw new IrUnsupportedException(
                    "zig `if (optional) |x| … else |e|`: an optional has no error to capture (use a plain `else`)");
            }
            test = new Member(condRef, "HasValue", false) { Type = CType.Bool };
            payloadInit = new Member(condRef, "Value", false) { Type = opt.Inner };
            payloadType = opt.Inner;
        }
        else if (ct is CType.Pointer)
        {
            if (errCapName is not null)
            {
                throw new IrUnsupportedException(
                    "zig `if (optional pointer) |x| … else |e|`: a pointer optional has no error to capture (use a plain `else`)");
            }
            test = condRef;        // Cond.B(void*) tests non-null
            payloadInit = condRef; // the unwrapped pointer is the same value
            payloadType = cond.Type;
        }
        else if (ct is CType.ErrorUnion eu)
        {
            // Error union (Milestone M, part 3): bind the success payload to `x` in the then-branch,
            // the error to `e` (the runtime `ushort Code`) in the else-branch. We test `__cap.IsErr`
            // (a clean bool) and emit the ERROR branch as the C# `if`, success as `else` — so no `!`
            // is needed. NOTE: this is a value inspection (`.IsErr`), NOT `try`, so it never throws a
            // ZigErrorReturn — the error is handled HERE and does not propagate to the function's
            // boundary catch. The captured error binds as `CType.ErrorSet` (rendered `ushort`, the
            // flat global code), so `e == error.Foo` compares codes (Milestone N) — what un-erased
            // the part-3 cut: a USED named `|e|` is now valid in both compilers.
            var errStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (errCapName is not null && errCapName != "_")
            {
                var errSym = _symbols.Declare(new Symbol { Name = errCapName, Kind = SymKind.Var, Type = CType.ErrorSet });
                errStmts.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(condRef, "Code", false) { Type = CType.ErrorSet }) }));
            }
            if (elseItem is not null) { errStmts.Add(LowerStmt(elseItem)); }
            _symbols.ExitScope();

            var okStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (capName != "_")
            {
                var okSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = eu.Payload });
                okStmts.Add(new DeclStmt(new List<LocalDecl> { new(okSym, new Member(condRef, "Value", false) { Type = eu.Payload }) }));
            }
            okStmts.Add(LowerStmt(thenItem));
            _symbols.ExitScope();

            var errTest = new Member(condRef, "IsErr", false) { Type = CType.Bool };
            CStmt euIf = new If(errTest, new Block(errStmts), new Block(okStmts));
            if (pre.Count > 0) { pre.Add(euIf); return new Block(pre); }
            return euIf;
        }
        else
        {
            throw new IrUnsupportedException(
                "zig `if (...) |x|` requires an optional (or error-union) condition");
        }

        // then-branch: bind the payload at the top, with `x` in scope while lowering the branch.
        var thenStmts = new List<CStmt>();
        _symbols.EnterScope();
        if (capName != "_")
        {
            var capSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = payloadType });
            thenStmts.Add(new DeclStmt(new List<LocalDecl> { new(capSym, payloadInit) }));
        }
        thenStmts.Add(LowerStmt(thenItem));
        _symbols.ExitScope();
        var thenBlock = new Block(thenStmts);

        var elseStmt = elseItem is null ? null : LowerStmt(elseItem);

        CStmt ifStmt = new If(test, thenBlock, elseStmt);
        if (pre.Count > 0)
        {
            pre.Add(ifStmt);
            return new Block(pre);
        }
        return ifStmt;
    }

    /// <summary>Lower an optional capture-<c>while</c> <c>while (opt) |x| body</c> (Milestone M, part
    /// 2). The condition is re-evaluated EACH iteration (it commonly advances an iterator), so it lives
    /// inside the loop body — a fresh <c>__cap</c> per turn. When it yields a payload, bind <c>x</c> and
    /// run the body; otherwise break. Desugars to
    /// <code>while (true) { var __cap = cond; if (has) { var x = payload; body } else break; }</code>
    /// which produces a real <see cref="While"/> node, so a labeled break/continue composes via the
    /// existing labeled-loop machinery. A value optional <c>?T</c> tests <c>__cap.HasValue</c> / binds
    /// <c>.Value</c>; a niche optional pointer tests non-null / binds the pointer itself. <c>_</c> tests
    /// without binding. An error-union or non-optional condition is a clear error.</summary>
    private CStmt LowerWhileCapture(Item condItem, string capName, Item bodyItem,
        (Item body, string? errName)? elseInfo = null, CExpr? contPost = null)
    {
        var cond = LowerExpr(condItem);
        var ct = cond.Type.Unqualified;

        var capTmp = _symbols.Declare(new Symbol { Name = "__cap", Kind = SymKind.Var, Type = cond.Type });
        var capRef = new VarRef(capTmp) { Type = cond.Type, IsLValue = true };

        List<CStmt> loopBody;

        // Error-union capture-while: bind the success payload each turn; on error, bind `e` (the flat
        // `ushort Code`) and run the mandatory `else |e|` branch, then break. Structured like
        // LowerIfCapture's error-union arm (error branch = the C# `if`, success = `else`, so no `!`).
        if (ct is CType.ErrorUnion eu)
        {
            if (elseInfo is not { errName: { } errName, body: var eErrBody })
            {
                throw new IrUnsupportedException(
                    "zig error-union capture `while (eu) |x|` requires an `else |e|` clause to handle the error");
            }
            var okStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (capName != "_")
            {
                var okSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = eu.Payload });
                okStmts.Add(new DeclStmt(new List<LocalDecl> { new(okSym, new Member(capRef, "Value", false) { Type = eu.Payload }) }));
            }
            okStmts.Add(LowerStmt(bodyItem));
            _symbols.ExitScope();

            var errStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (errName != "_")
            {
                var errSym = _symbols.Declare(new Symbol { Name = errName, Kind = SymKind.Var, Type = CType.ErrorSet });
                errStmts.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(capRef, "Code", false) { Type = CType.ErrorSet }) }));
            }
            errStmts.Add(LowerStmt(eErrBody));
            errStmts.Add(new Break());
            _symbols.ExitScope();

            var isErr = new Member(capRef, "IsErr", false) { Type = CType.Bool };
            loopBody = new List<CStmt>
            {
                new DeclStmt(new List<LocalDecl> { new(capTmp, cond) }),
                new If(isErr, new Block(errStmts), new Block(okStmts)),
            };
        }
        else
        {
            CExpr test;
            CExpr payloadInit;
            CType payloadType;
            if (ct is CType.Optional opt)
            {
                test = new Member(capRef, "HasValue", false) { Type = CType.Bool };
                payloadInit = new Member(capRef, "Value", false) { Type = opt.Inner };
                payloadType = opt.Inner;
            }
            else if (ct is CType.Pointer)
            {
                test = capRef;        // Cond.B(void*) tests non-null
                payloadInit = capRef; // the unwrapped pointer is the same value
                payloadType = cond.Type;
            }
            else
            {
                throw new IrUnsupportedException(
                    "zig `while (...) |x|` requires an optional condition");
            }
            if (elseInfo is { errName: not null })
            {
                throw new IrUnsupportedException(
                    "zig `while (optional) |x| … else |e|`: an optional has no error to capture (use a plain `else`)");
            }

            // then-branch: bind the payload, then the user body, with `x` in scope while lowering it.
            var thenStmts = new List<CStmt>();
            _symbols.EnterScope();
            if (capName != "_")
            {
                var capSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = payloadType });
                thenStmts.Add(new DeclStmt(new List<LocalDecl> { new(capSym, payloadInit) }));
            }
            thenStmts.Add(LowerStmt(bodyItem));
            _symbols.ExitScope();

            // exit branch (payload null): run the `else` body (if any), then break. Kept a bare
            // `break` when there's no else, preserving the plain capture-while emit shape.
            CStmt exitBranch;
            if (elseInfo is { body: var elseBody })
            {
                exitBranch = new Block(new List<CStmt> { LowerStmt(elseBody), new Break() });
            }
            else
            {
                exitBranch = new Break();
            }

            loopBody = new List<CStmt>
            {
                new DeclStmt(new List<LocalDecl> { new(capTmp, cond) }),
                new If(test, new Block(thenStmts), exitBranch),
            };
        }

        // body: re-eval the condition each turn (via the fresh __cap), then bind+run or exit. A
        // continue-expression (`: (cont)`) lowers to the C `For` post, so `continue` runs the cont.
        var trueLit = new LitBool(true) { Type = CType.Bool };
        return contPost is null
            ? new While(trueLit, new Block(loopBody))
            : new For(null, trueLit, contPost, new Block(loopBody));
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
            if (prongItem.Content is Zig.ProngCapture or Zig.ProngCaptureRef)
            {
                throw new IrUnsupportedException(
                    "zig switch payload capture `|x|` is only valid on a tagged-union switch");
            }
            // A prong body is a braced Block (`=> { … }`) or a bare expression (`=> expr`, which in
            // a STATEMENT switch is an expression statement, e.g. `1 => doThing()`).
            Item caseVals;
            List<CStmt> body;
            switch (prongItem.Content)
            {
                case Zig.Prong p:      caseVals = p.Arg0;  body = new List<CStmt> { LowerBlock(p.Arg2) }; break;
                case Zig.ProngExpr pe: caseVals = pe.Arg0; body = new List<CStmt> { new ExprStmt(LowerExpr(pe.Arg2)) }; break;
                default: throw new IrUnsupportedException("zig switch prong: " + (prongItem.Content?.GetType().Name ?? "null"));
            }
            var labels = LowerCaseVals(caseVals, subject.Type); // case values compare against the subject
            if (!EndsInJump(body)) { body.Add(new Break()); }   // no Zig fall-through
            sections.Add(new SwitchSection(labels, body));
        }
        return new Switch(subject, sections);
    }

    /// <summary>Lower a <c>switch</c> over a tagged union: switch on the <see cref="TagFieldName"/>
    /// discriminant, with <c>.variant</c> case labels resolving against the tag enum. A
    /// <c>|x|</c> payload capture binds <c>x</c> to the matched variant's payload field (by value),
    /// and a by-reference <c>|*x|</c> capture (Milestone M, part 4) binds <c>x</c> to a <c>*T</c>
    /// pointer INTO that payload field, so <c>x.* = …</c> writes through to the (mutable) union — at
    /// the top of that prong's block. The subject is hoisted to a temp first (unless it is already a
    /// simple variable) so each capture re-reads it without re-evaluating a side-effecting subject
    /// expression.</summary>
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
            // A bare-expr prong (`=> expr`) in a union STATEMENT switch is an expression statement,
            // with no payload capture (capture needs a braced block); handle it up front.
            if (prongItem.Content is Zig.ProngExpr pe)
            {
                RejectUnionRange(pe.Arg0, info);
                var exprLabels = LowerCaseVals(pe.Arg0, info.TagType);
                var exprBody = new List<CStmt> { new ExprStmt(LowerExpr(pe.Arg2)) };
                if (!EndsInJump(exprBody)) { exprBody.Add(new Break()); }
                sections.Add(new SwitchSection(exprLabels, exprBody));
                continue;
            }
            Item caseVals; string? captureName; Item block; bool captureByRef;
            switch (prongItem.Content)
            {
                case Zig.Prong p:           caseVals = p.Arg0; captureName = null;        block = p.Arg2; captureByRef = false; break;
                case Zig.ProngCapture p:    caseVals = p.Arg0; captureName = Tok(p.Arg3); block = p.Arg5; captureByRef = false; break;
                case Zig.ProngCaptureRef p: caseVals = p.Arg0; captureName = Tok(p.Arg4); block = p.Arg6; captureByRef = true;  break;
                default: throw new IrUnsupportedException("zig switch prong: " + (prongItem.Content?.GetType().Name ?? "null"));
            }
            RejectUnionRange(caseVals, info);
            var labels = LowerCaseVals(caseVals, info.TagType);   // `.variant` → EnumConstRef(U_Tag.variant)

            List<CStmt> body;
            if (captureName is not null && captureName != "_")
            {
                var variant = CaptureVariantName(caseVals, info, captureName);
                var payloadType = info.Variants[variant]
                    ?? throw new IrUnsupportedException(
                        $"union '{info.Name}' variant '{variant}' is a void variant — it has no payload to capture with `|{captureName}|`");
                _symbols.EnterScope();
                // By-value (`|x|`): `var x = __un.__payload.variant;` (a copy). By-reference (`|*x|`):
                // `T* x = &(__un.__payload.variant);` — a pointer into the union's payload field, so
                // `x.* = …` writes through to the (mutable) union value.
                var bindType = captureByRef ? new CType.Pointer(payloadType) : payloadType;
                var capSym = _symbols.Declare(new Symbol { Name = captureName, Kind = SymKind.Var, Type = bindType });
                var payloadBase = new Member(unionRef, info.PayloadFieldName, isPtr) { Type = new CType.Named(info.PayloadTypeName!), IsLValue = true };
                var payloadField = new Member(payloadBase, variant, false) { Type = payloadType, IsLValue = true };
                CExpr capInit = captureByRef ? new Unary(UnOp.AddrOf, payloadField) { Type = bindType } : payloadField;
                if (captureByRef && unionRef is VarRef { Sym: { } uvar }) { uvar.AddressTaken = true; }
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
    private CStmt LowerForSlice(CExpr sliceExpr, string elemName, (string name, CExpr start)? index, Item bodyItem, bool byRef)
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

        // body: prepend the element binding and, for the index form, `var i = __i + START;`.
        // By-value (`|x|`): `var x = __s.Ptr[__i];` (a per-iteration copy). By-reference (`|*x|`):
        // `T* x = &(__s.Ptr[__i]);` so `x.* = …` writes through to the element.
        var ptrMember = new Member(sliceRef, "Ptr", false) { Type = new CType.Pointer(slc.Element) };
        var elemAccess = new DotCC.Ir.Index(ptrMember, iRef) { Type = slc.Element, IsLValue = true };
        var elemType = byRef ? new CType.Pointer(slc.Element) : slc.Element;
        CExpr elemInit = byRef ? new Unary(UnOp.AddrOf, elemAccess) { Type = elemType } : elemAccess;
        var elemSym = _symbols.Declare(new Symbol { Name = elemName, Kind = SymKind.Var, Type = elemType });
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

    /// <summary>The payload <c>.variant</c> a tagged-union capture prong binds. A single-variant prong
    /// (<c>.a =&gt; |x|</c>) returns that variant. A MULTI-variant capture prong (<c>.a, .b =&gt; |x|</c>,
    /// Milestone Z) is allowed only when every listed variant shares the SAME payload type — then the
    /// FIRST variant's payload field is bound: in the explicit-layout payload union every variant
    /// overlaps at offset 0, so reading one (the field of the same type) aliases whichever variant
    /// actually matched. An <c>else</c>, an unknown variant, or variants with differing payload types is
    /// rejected (a capture binds to one <c>|x|</c>, so one payload type).</summary>
    private string CaptureVariantName(Item caseVals, ZigUnionInfo info, string captureName)
    {
        if (caseVals.Content is Zig.CaseElse)
        {
            throw new IrUnsupportedException(
                $"a tagged-union capture prong (`|{captureName}|`) cannot capture on `else` — it has no single payload type");
        }
        var vals = Flatten(caseVals);
        var variants = new List<string>(vals.Count);
        foreach (var v in vals)
        {
            if (v.Content is not Zig.EnumLit el)
            {
                throw new IrUnsupportedException(
                    "a tagged-union capture prong must list `.variant` values");
            }
            var name = Tok(el.Arg1);
            if (!info.Variants.ContainsKey(name))
            {
                throw new IrUnsupportedException($"union '{info.Name}' has no variant '{name}'");
            }
            variants.Add(name);
        }
        // A multi-variant capture binds to a single `|x|`, so every listed variant must carry the
        // same payload type. The first variant's payload field aliases the rest (all at offset 0).
        var first = variants[0];
        var firstType = info.Variants[first];
        for (var i = 1; i < variants.Count; i++)
        {
            var ti = info.Variants[variants[i]];
            if (firstType is null || ti is null || !firstType.Unqualified.Equals(ti.Unqualified))
            {
                throw new IrUnsupportedException(
                    $"a multi-variant capture prong `.{first}, .{variants[i]} => |{captureName}|` requires every " +
                    "listed variant to share the same payload type");
            }
        }
        return first;
    }

    /// <summary>Lower a prong's case values to switch labels: <c>else</c> → the single null
    /// (default) label; otherwise each comma-separated element → one label, lowered against the
    /// subject type as its sink (so a <c>.member</c> case resolves when switching on an enum). An
    /// inclusive range element <c>lo...hi</c> (Milestone L, part 4) becomes a range label
    /// (<see cref="SwitchLabel.HiExpr"/> set) → a relational pattern in the backend.</summary>
    private List<SwitchLabel> LowerCaseVals(Item caseVals, CType? sink)
    {
        if (caseVals.Content is Zig.CaseElse)
        {
            return new List<SwitchLabel> { new SwitchLabel(null) };
        }
        var labels = new List<SwitchLabel>();
        foreach (var (lo, hi) in WalkCaseValItems(caseVals))
        {
            labels.Add(hi is null
                ? new SwitchLabel(LowerExprSink(lo, sink))
                : new SwitchLabel(LowerExprSink(lo, sink), LowerExprSink(hi, sink)));
        }
        return labels;
    }

    /// <summary>Walk a (non-<c>else</c>) <c>CaseVals</c> comma-list into its elements, each a single
    /// value (<c>Hi</c> null) or an inclusive range <c>lo...hi</c> (<c>Hi</c> set). Mirrors the
    /// grammar's right-recursive list shape over the plain (<c>CaseVals…</c>) and range
    /// (<c>CaseRange…</c>) productions.</summary>
    private static List<(Item Lo, Item? Hi)> WalkCaseValItems(Item caseVals)
    {
        var items = new List<(Item, Item?)>();
        var it = caseVals;
        while (true)
        {
            switch (it.Content)
            {
                case Zig.CaseValsCons c:  items.Add((c.Arg0, null));   it = c.Arg2; continue;  // [Expr ',' CaseVals]
                case Zig.CaseValsOne o:   items.Add((o.Arg0, null));   return items;           // [Expr]
                case Zig.CaseRangeCons r: items.Add((r.Arg0, r.Arg2)); it = r.Arg4; continue;  // [Expr '...' Expr ',' CaseVals]
                case Zig.CaseRangeOne r:  items.Add((r.Arg0, r.Arg2)); return items;           // [Expr '...' Expr]
                default:
                    throw new IrUnsupportedException(
                        "zig switch case values: " + (it.Content?.GetType().Name ?? "null"));
            }
        }
    }

    /// <summary>True when a <c>CaseVals</c> list contains an inclusive range element
    /// (<c>lo...hi</c>) — used to reject ranges where they aren't supported yet (a switch
    /// EXPRESSION arm, a tagged-union switch).</summary>
    private static bool CaseValsContainsRange(Item caseVals) => caseVals.Content switch
    {
        Zig.CaseRangeOne or Zig.CaseRangeCons => true,
        Zig.CaseValsCons c => CaseValsContainsRange(c.Arg2),
        _ => false,
    };

    /// <summary>Reject an inclusive range in a tagged-union switch prong — a union's variants are
    /// not ordered, so <c>.a...c</c> is meaningless (and not valid Zig).</summary>
    private static void RejectUnionRange(Item caseVals, ZigUnionInfo info)
    {
        if (CaseValsContainsRange(caseVals))
        {
            throw new IrUnsupportedException(
                $"an inclusive range (`lo...hi`) isn't valid in a switch on the tagged union '{info.Name}' (its variants aren't ordered)");
        }
    }

    /// <summary>Lower a switch EXPRESSION `switch (subj) { v => e, …, else => e }` (Milestone L) to
    /// the C# switch-expression IR (<see cref="SwitchExpr"/>). Each prong must YIELD a value — a
    /// bare-expr body `v => e` lowered at the result <paramref name="sink"/> (so a nested `.member`
    /// / `.{…}` / cast resolves). `else` → the `_` default arm; a multi-value prong `a, b => e`
    /// becomes one arm with both labels (rendered `a or b`). The subject is lowered once. Deferred
    /// (clear error): a block-bodied prong (`=> { … break :blk v; }`, needs the labeled-block
    /// increment) and a tagged-union payload capture `|x|` in expression position.</summary>
    private CExpr LowerSwitchExpr(Item subjectItem, Item prongsItem, CType? sink)
    {
        var subject = LowerExpr(subjectItem);
        var arms = new List<SwitchExprArm>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            if (prongItem.Content is not Zig.ProngExpr pe)
            {
                throw new IrUnsupportedException(
                    "zig switch-expression prong must yield a value (`v => expr`); a block-bodied prong " +
                    "(a labeled `break :blk v`) is supported only as a full `const`/`var`/`return`/assignment RHS " +
                    "(Milestone Y, part 1), not in a sub-expression; a `|x|` capture in a switch expression is not supported yet");
            }
            var value = LowerExprSink(pe.Arg2, sink);
            // `else` → the `_` default arm; otherwise the prong's case values become the arm's
            // labels (a multi-value prong → several, rendered `a or b`), reusing LowerCaseVals so an
            // inclusive range `lo...hi` lowers to a relational-pattern label exactly as in a
            // statement switch.
            arms.Add(pe.Arg0.Content is Zig.CaseElse
                ? new SwitchExprArm(null, value)
                : new SwitchExprArm(LowerCaseVals(pe.Arg0, subject.Type), value));
        }
        // A Zig switch over an error set or enum is exhaustive — real zig proves the prongs cover
        // every member, so no `else` is required. dotcc erases an error set to a flat `ushort` and
        // an enum value can hold any backing int, so C# can't prove coverage and rejects the switch
        // EXPRESSION (CS8509 "not all values covered"). Mirror the union-switch fix: with no `else`,
        // collapse the LAST arm to the `_` default — for an exhaustive switch (which valid Zig
        // requires) only that arm's values reach it, so it is semantics-preserving. (Milestone X,
        // part 3b.) A plain integer subject is left alone: there an `else` IS required, so a missing
        // default reflects a genuinely non-exhaustive switch.
        if (subject.Type.Unqualified is CType.ErrorSetType or CType.Enum
            && arms.Count > 0
            && !arms.Any(a => a.Labels is null))
        {
            arms[^1] = arms[^1] with { Labels = null };
        }
        // The result type is the sink, else inferred from the first value-yielding arm.
        var resultType = sink
            ?? arms.Select(a => a.Value.Type).FirstOrDefault(t => t is not null)
            ?? CType.Int;
        return new SwitchExpr(subject, arms) { Type = resultType };
    }

    /// <summary>A result temp shared while a value-position <c>if</c>/<c>switch</c> is lowered as a
    /// statement (Milestone Y, part 1). Every branch fills <see cref="Temp"/>; <see cref="ResultType"/>
    /// is the sink when known, else fixed by the first branch's value type (so a sink-less
    /// <c>const x = switch …</c> still types the temp).</summary>
    private sealed class ValueTempTarget
    {
        public required Symbol Temp;
        public CType? ResultType;
    }

    /// <summary>True when <paramref name="rhs"/> is a value-position <c>if</c>/<c>switch</c> EXPRESSION
    /// with a branch that needs STATEMENTS to produce its value — a labeled value-block branch
    /// (<c>blk: {…; break :blk v;}</c>) or a block-bodied / capturing switch prong. Such a form can't
    /// be a C# expression (a ternary / switch-expression), so a statement context (a <c>const</c> /
    /// <c>var</c> / <c>return</c> / assignment RHS) lowers it via <see cref="LowerValueControlFlowStmt"/>
    /// into a result temp. An all-simple-value <c>if</c>/<c>switch</c> returns false and keeps the clean
    /// expression lowering (the C# ternary / switch-expression).</summary>
    private static bool IsValueControlFlowStmt(Item rhs) => rhs.Content switch
    {
        Zig.IfExpr e             => e.Arg4.Content is Zig.LabeledBlock || e.Arg6.Content is Zig.LabeledBlock,
        Zig.SwitchExpr s         => SwitchExprNeedsStmt(s.Arg5),
        Zig.SwitchExprTrailing s => SwitchExprNeedsStmt(s.Arg5),
        // A value-position loop (`while/for … else`, Milestone Y part 2) ALWAYS needs the statement
        // lowering — a loop that yields via `break v` / an `else` value can't be a C# expression.
        Zig.WhileElseExpr or Zig.ForElseExpr or Zig.LabeledWhileElseExpr or Zig.LabeledForElseExpr => true,
        _ => false,
    };

    /// <summary>True when any prong of a switch EXPRESSION needs statements to yield its value — a
    /// block-bodied (<c>=&gt; { … }</c>) or capturing (<c>=&gt; |x| { … }</c>) prong, or a bare-expr
    /// prong whose value is itself a labeled value-block (<c>=&gt; blk: { … break :blk v; }</c>).</summary>
    private static bool SwitchExprNeedsStmt(Item prongsItem) =>
        Flatten(prongsItem).Any(p => p.Content switch
        {
            Zig.Prong or Zig.ProngCapture or Zig.ProngCaptureRef => true,
            Zig.ProngExpr pe => pe.Arg2.Content is Zig.LabeledBlock,
            _ => false,
        });

    /// <summary>Lower a value-position control-flow form that needs statements to produce its value
    /// (see <see cref="IsValueControlFlowStmt"/>) as a C# STATEMENT, then hand the result temp to
    /// <paramref name="consume"/> (the decl / return / assignment that reads it). Dispatches an
    /// <c>if</c>/<c>switch</c> branch-temp-fill (Milestone Y, part 1) and a <c>while/for … else</c>
    /// value loop (part 2) to their builders.</summary>
    private CStmt LowerValueControlFlowStmt(Item rhs, CType? sink, Func<Symbol, CStmt> consume) => rhs.Content switch
    {
        Zig.IfExpr or Zig.SwitchExpr or Zig.SwitchExprTrailing => LowerValueIfSwitch(rhs, sink, consume),
        Zig.WhileElseExpr or Zig.ForElseExpr or Zig.LabeledWhileElseExpr or Zig.LabeledForElseExpr
            => LowerLoopValue(rhs, sink, consume),
        _ => throw new IrUnsupportedException(
            "internal: value control-flow statement on " + (rhs.Content?.GetType().Name ?? "null")),
    };

    /// <summary>Lower a value-position <c>if</c>/<c>switch</c> whose branch(es) need statements as a C#
    /// STATEMENT that fills a result temp. Mirrors the labeled-value-block temp-fill
    /// (<see cref="LowerLabeledValueBlock"/>): a default-initialized temp, each branch assigning it,
    /// then the consumer. The temp's type is the <paramref name="sink"/> when known, else the first
    /// branch's value type. (Milestone Y, part 1.) A union-subject value-switch and a <c>|x|</c>
    /// capture in expression position stay clear deferred errors.</summary>
    private CStmt LowerValueIfSwitch(Item rhs, CType? sink, Func<Symbol, CStmt> consume)
    {
        var n = _blockLabelCounter++;
        var temp = _symbols.Declare(new Symbol { Name = "__vcf" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var rt = new ValueTempTarget { Temp = temp, ResultType = sink };
        // The cond/subject is lowered before the branches (left-to-right C# argument evaluation), and
        // the first branch's FillValueTemp fixes rt.ResultType so a sink-less switch/if still types.
        CStmt filler = rhs.Content switch
        {
            Zig.IfExpr e => new If(LowerExpr(e.Arg2),
                                   new Block(new List<CStmt> { FillValueTemp(e.Arg4, rt) }),
                                   new Block(new List<CStmt> { FillValueTemp(e.Arg6, rt) })),
            Zig.SwitchExpr s         => BuildValueSwitch(s.Arg2, s.Arg5, rt),
            Zig.SwitchExprTrailing s => BuildValueSwitch(s.Arg2, s.Arg5, rt),
            _ => throw new IrUnsupportedException(
                "internal: value if/switch on " + (rhs.Content?.GetType().Name ?? "null")),
        };
        var resultType = rt.ResultType
            ?? throw new IrUnsupportedException("a value-position `if`/`switch` must yield a value in every branch");
        temp.Type = resultType;
        return new Seq(new List<CStmt>
        {
            // Default-initialized so C# definite-assignment is satisfied even though every real path
            // assigns the temp (a switch with no matching case is impossible in valid, exhaustive Zig).
            new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = resultType }) }),
            filler,
            consume(temp),
        });
    }

    /// <summary>Lower a value-position loop <c>while/for (…) { … } else v</c> (Milestone Y, part 2) as
    /// a C# STATEMENT filling a result temp. The loop runs normally; a <c>break v</c> (unlabeled, the
    /// innermost value loop) or <c>break :lbl v</c> (the matching labeled one) inside assigns the temp
    /// and jumps to the end label, SKIPPING the <c>else</c> value — which is assigned only on natural
    /// completion (no break). The end label is emitted only if a <c>break</c> targeted it (an else-only
    /// loop never jumps there). The temp's type is the sink when known, else the first <c>break</c> /
    /// the <c>else</c> value type. V1 cuts (deferred to the grammar): a for-RANGE / indexed / capture
    /// value loop.</summary>
    private CStmt LowerLoopValue(Item rhs, CType? sink, Func<Symbol, CStmt> consume)
    {
        string? label = null;
        bool isFor;
        Item condOrIter, blockItem, elseItem;
        string? elemName = null;
        switch (rhs.Content)
        {
            case Zig.WhileElseExpr w:        condOrIter = w.Arg2; blockItem = w.Arg4; elseItem = w.Arg6; isFor = false; break;
            case Zig.ForElseExpr f:          condOrIter = f.Arg2; elemName = Tok(f.Arg5); blockItem = f.Arg7; elseItem = f.Arg9; isFor = true; break;
            case Zig.LabeledWhileElseExpr w: label = Tok(w.Arg0); condOrIter = w.Arg4; blockItem = w.Arg6; elseItem = w.Arg8; isFor = false; break;
            case Zig.LabeledForElseExpr f:   label = Tok(f.Arg0); condOrIter = f.Arg4; elemName = Tok(f.Arg7); blockItem = f.Arg9; elseItem = f.Arg11; isFor = true; break;
            default: throw new IrUnsupportedException("internal: loop-value on " + (rhs.Content?.GetType().Name ?? "null"));
        }

        var n = _loopValueCounter++;
        var endLabel = "__lv" + n + "_end";
        var temp = _symbols.Declare(new Symbol { Name = "__lv" + n, Kind = SymKind.Var, Type = sink ?? CType.Int });
        var target = new LoopValueTarget { Temp = temp, EndLabel = endLabel, Label = label, Sink = sink, ResultType = sink };

        // Lower the loop with the value target active so a `break v` inside resolves to it. The cond /
        // iterable is lowered before the body (it can't `break`), so it never references the temp.
        _loopValues.Push(target);
        CStmt loop = isFor
            ? LowerForSlice(LowerExpr(condOrIter), elemName!, null, blockItem, byRef: false)
            : new While(LowerExpr(condOrIter), LowerBlock(blockItem));
        _loopValues.Pop();

        // The `else` value supplies the result on NORMAL completion. A `break v` jumped to `endLabel`,
        // skipping this. Sink it at the now-known result type (a `break` may have fixed it).
        var elseSink = target.ResultType ?? target.Sink;
        var elseVal = elseSink is { } sk ? LowerExprSink(elseItem, sk) : LowerExpr(elseItem);
        target.ResultType ??= elseVal.Type;
        var resultType = target.ResultType;
        temp.Type = resultType;

        var stmts = new List<CStmt>
        {
            new DeclStmt(new List<LocalDecl> { new(temp, new DefaultLit { Type = resultType }) }),
            loop,
            new ExprStmt(new Assign(null, LvRef(target), elseVal) { Type = resultType }),
        };
        if (target.BreakUsed) { stmts.Add(new Labeled(endLabel, new Block(new List<CStmt>()))); }
        stmts.Add(consume(temp));
        return new Seq(stmts);
    }

    /// <summary>An lvalue reference to a value-loop result temp at its (now-resolved) type.</summary>
    private static VarRef LvRef(LoopValueTarget t) => new VarRef(t.Temp) { Type = t.ResultType!, IsLValue = true };

    /// <summary>Lower a <c>break v</c> targeting <paramref name="target"/>: assign the value to the
    /// loop's result temp, then <c>goto</c> its end label (skipping the loop's <c>else</c>). A braced
    /// <see cref="Block"/> (not a brace-less <see cref="Seq"/>) so a conditional break (`if (c) break v;`)
    /// keeps both the assign and the goto guarded. (Milestone Y, part 2.)</summary>
    private CStmt BuildLoopBreakValue(LoopValueTarget target, Item valueItem)
    {
        var value = (target.ResultType ?? target.Sink) is { } sk ? LowerExprSink(valueItem, sk) : LowerExpr(valueItem);
        target.ResultType ??= value.Type;
        target.BreakUsed = true;
        return new Block(new List<CStmt>
        {
            new ExprStmt(new Assign(null, LvRef(target), value) { Type = target.ResultType }),
            new Goto(target.EndLabel),
        });
    }

    /// <summary>Build the statement that fills a value-control-flow result temp from one branch's value
    /// (Milestone Y, part 1). A labeled value-block branch (<c>blk: { … break :blk v; }</c>) is
    /// temp-filled — its <c>break :blk v</c> assigns its own temp — and that temp copied into
    /// <paramref name="rt"/>'s; any other expression is lowered at the running result type and assigned.
    /// The first branch lowered fixes <see cref="ValueTempTarget.ResultType"/>.</summary>
    private CStmt FillValueTemp(Item valueItem, ValueTempTarget rt)
    {
        if (valueItem.Content is Zig.LabeledBlock lb)
        {
            return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, rt.ResultType, blkTemp =>
            {
                rt.ResultType ??= blkTemp.Type;
                return new ExprStmt(new Assign(null, RtRef(rt), new VarRef(blkTemp) { Type = blkTemp.Type }) { Type = rt.ResultType });
            });
        }
        var value = rt.ResultType is { } sk ? LowerExprSink(valueItem, sk) : LowerExpr(valueItem);
        rt.ResultType ??= value.Type;
        return new ExprStmt(new Assign(null, RtRef(rt), value) { Type = rt.ResultType });
    }

    /// <summary>An lvalue reference to a value-control-flow result temp at its (now-resolved) type.
    /// Only called after a branch has fixed <see cref="ValueTempTarget.ResultType"/>, so it is non-null.</summary>
    private static VarRef RtRef(ValueTempTarget rt) => new VarRef(rt.Temp) { Type = rt.ResultType!, IsLValue = true };

    /// <summary>Build the statement <c>switch</c> that fills a value-control-flow result temp — each
    /// prong assigns the temp (via <see cref="FillValueTemp"/>) then <c>break</c>s (Zig has no
    /// fall-through). Because it's a STATEMENT switch over a default-initialized temp, C#'s
    /// switch-expression exhaustiveness rule (CS8509) doesn't apply. (Milestone Y, part 1.) A
    /// tagged-union value-switch (tag dispatch + payload capture in value position) and a void block
    /// prong / <c>|x|</c> capture in a switch expression stay clear deferred errors.</summary>
    private CStmt BuildValueSwitch(Item subjectItem, Item prongsItem, ValueTempTarget rt)
    {
        var subject = LowerExpr(subjectItem);
        var uname = subject.Type.Unqualified switch
        {
            CType.Named nm => nm.Name,
            CType.Pointer { Pointee: var pe } when pe.Unqualified is CType.Named pn => pn.Name,
            _ => null,
        };
        if (uname is not null && _unions.ContainsKey(uname))
        {
            throw new IrUnsupportedException(
                "a tagged-union value-switch with block prongs (`const x = switch (u) { .v => blk: {…} }`) is not supported yet");
        }
        var sections = new List<SwitchSection>();
        foreach (var prongItem in Flatten(prongsItem))
        {
            if (prongItem.Content is not Zig.ProngExpr pe)
            {
                throw new IrUnsupportedException(
                    "a value-position switch prong must yield a value (`v => expr` or `v => blk: {… break :blk v;}`); " +
                    "a void block prong or a `|x|` capture in a switch expression is not supported yet");
            }
            var fill = FillValueTemp(pe.Arg2, rt);
            var labels = LowerCaseVals(pe.Arg0, subject.Type);
            sections.Add(new SwitchSection(labels, new List<CStmt> { fill, new Break() }));
        }
        return new Switch(subject, sections);
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
        // `return blk: { … break :blk v; };` — a labeled value-block return (Milestone L, part 2).
        // Temp-fill against the function's return type, then `return` the result temp. (In an error-
        // union function the wrapping below would need to apply to the temp — deferred with a clear
        // error rather than silently returning an unwrapped value.)
        if (valueItem.Content is Zig.LabeledBlock lb)
        {
            if (_currentFnRet is CType.ErrorUnion)
            {
                throw new IrUnsupportedException(
                    "a labeled value-block `return blk: {…}` in an error-union (`!T`) function is not supported yet");
            }
            return LowerLabeledValueBlock(Tok(lb.Arg0), lb.Arg2, _currentFnRet,
                temp => new Return(new VarRef(temp) { Type = temp.Type }));
        }
        // `return switch (y) { … blk: {…} };` / `return if (c) blk:{…} else …;` — a value-position
        // if/switch with a statement-producing branch (Milestone Y, part 1): temp-fill against the
        // return type, then `return` the temp. An error-union `!T` function is deferred (like the
        // labeled-block return above — the ErrUnion wrapping would need to apply to the temp).
        if (IsValueControlFlowStmt(valueItem))
        {
            if (_currentFnRet is CType.ErrorUnion)
            {
                throw new IrUnsupportedException(
                    "a value-position `if`/`switch` with a block branch in an error-union (`!T`) function `return` is not supported yet");
            }
            return LowerValueControlFlowStmt(valueItem, _currentFnRet,
                temp => new Return(new VarRef(temp) { Type = temp.Type }));
        }
        if (_currentFnRet is CType.ErrorUnion eu)
        {
            // `return error.X;` or the set-qualified `return E.X;` (Milestone X, part 2) — both an
            // error return (the same flat code). Part 3: validate `E.X` membership, then reject an
            // error outside the function's DECLARED set (a good compiler rejects illegal programs).
            string? errName = null;
            if (IsErrorLit(valueItem, out var bareName)) { errName = bareName; }
            else if (TryErrorSetMember(valueItem, out var qSet, out var qName)) { ValidateSetMember(qSet, qName); errName = qName; }
            if (errName is not null)
            {
                CheckReturnedErrorInSet(errName);
                // With an `errdefer` in this function, the error must propagate via a thrown
                // ZigErrorReturn so it passes through the errdefer catch(es) on the stack (a C#
                // catch can't observe a direct return); the `!T` boundary catch converts it back
                // to an Err. Without an errdefer, keep the direct, exception-free Err return.
                if (_currentFnHasErrdefer) { return new ZigErrorThrow(ErrorCode(errName)); }
                return new Return(new ErrUnionErr(ErrorCode(errName)) { Type = eu });
            }
            var v = LowerExpr(valueItem);
            if (v.Type.Unqualified is CType.ErrorUnion) { return new Return(v); }
            return new Return(new ErrUnionOk(v) { Type = eu });
        }
        // An array-by-value return (the Milestone K cut, made sound). A `[N]T`-returning function
        // emits a `T*` signature, but `return t;` of a stackalloc array local would hand back a
        // dangling pointer into the dead callee frame — yet Zig arrays are value types. Copy the N
        // elements into a heap-owned buffer (ArrayByValReturn) so the result outlives the call. The
        // node's type is the array type, so the return coercion is a no-op. (An array in an `!T`
        // error-union function takes the path above — a follow-up; rare in practice.)
        if (_currentFnRet is CType.Array retArr && retArr.Count is int retN)
        {
            var src = LowerExprSink(valueItem, retArr);
            return new Return(new ArrayByValReturn(src, retArr.Element, retN) { Type = retArr });
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

    /// <summary>Lower a <c>catch</c> fallback's VALUE at a statement-context position (a
    /// <c>const</c>/<c>var</c> initializer), returning the pre-statements that must run first plus
    /// the value expression. Three shapes:
    /// <list type="bullet">
    /// <item>no capture + a simple (re-evaluable, side-effect-free) fallback → empty pre + the eager
    /// <see cref="ZigCatch"/> (<c>ErrUnion.Catch(a, b)</c>, unchanged from Milestone B2);</item>
    /// <item>no capture + a side-effecting fallback → hoist the union to a single-eval <c>__cE</c>
    /// temp and make the fallback LAZY via a ternary <c>__cE.IsErr ? b : __cE.Value</c> (so <c>b</c>
    /// runs only on error);</item>
    /// <item>a capture <c>catch |e| b</c> → hoist the union, bind <c>e</c> to the flat error code
    /// (<see cref="CType.ErrorSet"/>), then the same lazy ternary with <c>e</c> in scope for
    /// <c>b</c>.</item>
    /// </list>
    /// The left operand must be an error union; the lazy ternary keeps Zig's evaluate-fallback-only-
    /// on-error semantics where the eager helper can't.</summary>
    private (List<CStmt> Pre, CExpr Value) LowerCatchValue(Item unionItem, string? capName, Item fallbackItem)
    {
        var union = LowerExpr(unionItem);
        if (union.Type.Unqualified is not CType.ErrorUnion eu)
        {
            throw new IrUnsupportedException("zig `catch` requires an error-union left operand");
        }
        var payload = eu.Payload;
        var pre = new List<CStmt>();

        if (capName is null)
        {
            var fb = LowerExpr(fallbackItem);
            if (IsSimpleReeval(fb)) { return (pre, new ZigCatch(union, fb) { Type = payload }); }
            var ce = HoistCatchUnion(union, pre);
            return (pre, new CondExpr(
                new Member(ce, "IsErr", false) { Type = CType.Bool },
                fb,
                new Member(ce, "Value", false) { Type = payload }) { Type = payload });
        }

        // Capture form `catch |e| b`: hoist, bind `e`, then the lazy ternary (with `e` visible).
        var ceCap = HoistCatchUnion(union, pre);
        if (capName != "_")
        {
            var errSym = _symbols.Declare(new Symbol { Name = capName, Kind = SymKind.Var, Type = CType.ErrorSet });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(errSym, new Member(ceCap, "Code", false) { Type = CType.ErrorSet }) }));
        }
        var fbCap = LowerExpr(fallbackItem);
        return (pre, new CondExpr(
            new Member(ceCap, "IsErr", false) { Type = CType.Bool },
            fbCap,
            new Member(ceCap, "Value", false) { Type = payload }) { Type = payload });
    }

    /// <summary>Hoist a (possibly side-effecting) error-union operand to a single-eval <c>__cE</c>
    /// temp unless it is already a bare variable; append the decl to <paramref name="pre"/> and
    /// return a reference for re-reading it (the <c>.IsErr</c>/<c>.Code</c>/<c>.Value</c> sites).</summary>
    private CExpr HoistCatchUnion(CExpr union, List<CStmt> pre)
    {
        if (union is VarRef) { return union; }
        var tmp = _symbols.Declare(new Symbol { Name = "__cE", Kind = SymKind.Var, Type = union.Type });
        pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, union) }));
        return new VarRef(tmp) { Type = union.Type, IsLValue = true };
    }

    /// <summary>Recognize a control-flow <c>catch</c>/<c>orelse</c> fallback (Milestone N, part 6) —
    /// <c>a catch return [v]</c> / <c>a orelse return [v]</c> — yielding the left operand, whether it
    /// is a <c>catch</c> (vs <c>orelse</c>), and the optional return value (null = <c>return;</c>).</summary>
    private static bool IsControlFlowFallback(Item it, out Item lhs, out bool isCatch, out Item? retVal)
    {
        switch (it.Content)
        {
            case Zig.OrElseReturn r:     lhs = r.Arg0; isCatch = false; retVal = r.Arg3; return true;
            case Zig.OrElseReturnVoid r: lhs = r.Arg0; isCatch = false; retVal = null;   return true;
            case Zig.CatchReturn r:      lhs = r.Arg0; isCatch = true;  retVal = r.Arg3; return true;
            case Zig.CatchReturnVoid r:  lhs = r.Arg0; isCatch = true;  retVal = null;   return true;
            default: lhs = it; isCatch = false; retVal = null; return false;
        }
    }

    /// <summary>Lower a control-flow <c>catch</c>/<c>orelse</c> fallback (Milestone N, part 6): <c>a
    /// catch return [v]</c> / <c>a orelse return [v]</c>. The left operand — an error union (for
    /// <c>catch</c>) or an optional (for <c>orelse</c>) — is hoisted to a single-eval temp; on the
    /// error / none path the <c>return</c> runs as an EARLY-OUT (lowered via
    /// <see cref="LowerReturn"/>/<see cref="LowerReturnVoid"/>, so it wraps correctly in a <c>!T</c>
    /// function — incl. <c>return error.X</c>). On the success path the unwrapped payload is consumed
    /// by <paramref name="bind"/> (a decl initializer binds it; an expression-statement passes null
    /// and discards it). Emitted as <c>{ var __cf = a; if (Cond.B(&lt;none/error&gt;)) { return …; }
    /// [bind(payload)] }</c>.</summary>
    private CStmt LowerControlFlowFallback(Item lhsItem, bool isCatch, Item? retValItem, Func<CExpr, CStmt>? bind)
    {
        var lhs = LowerExpr(lhsItem);
        var pre = new List<CStmt>();
        CExpr lhsRef;
        if (lhs is VarRef) { lhsRef = lhs; }
        else
        {
            var tmp = _symbols.Declare(new Symbol { Name = "__cf", Kind = SymKind.Var, Type = lhs.Type });
            pre.Add(new DeclStmt(new List<LocalDecl> { new(tmp, lhs) }));
            lhsRef = new VarRef(tmp) { Type = lhs.Type, IsLValue = true };
        }

        CExpr test;       // true on the path that must `return` (error for catch, none for orelse)
        CExpr payload;    // the unwrapped success value
        var ct = lhsRef.Type.Unqualified;
        if (isCatch)
        {
            if (ct is not CType.ErrorUnion eu)
            {
                throw new IrUnsupportedException("zig `catch return` requires an error-union left operand");
            }
            test = new Member(lhsRef, "IsErr", false) { Type = CType.Bool };
            payload = new Member(lhsRef, "Value", false) { Type = eu.Payload };
            // A `create`-style error-union-over-pointer (`Error!*T`, Milestone U) carries its payload
            // as a `nuint` (a pointer can't be an `ErrUnion<T>` generic arg), so `.Value` is a `nuint`;
            // cast it back to the `T*` the payload names. Mirrors the `try` lowering (PreTry above);
            // `create` is the only producer of a pointer-payload union, so the cast is exactly correct.
            if (eu.Payload.Unqualified is CType.Pointer)
            {
                payload = new Cast(eu.Payload, payload) { Type = eu.Payload };
            }
        }
        else if (ct is CType.Optional opt)
        {
            test = new Unary(UnOp.LogNot, new Member(lhsRef, "HasValue", false) { Type = CType.Bool }) { Type = CType.Int };
            payload = new Member(lhsRef, "Value", false) { Type = opt.Inner };
        }
        else if (ct is CType.Pointer)
        {
            // A niche optional pointer (`?*T` → bare `T*`): none is null, the unwrapped value is the
            // pointer itself.
            test = new Binary(BinOp.Eq, lhsRef, new NullPtr { Type = new CType.Pointer(CType.Void) }) { Type = CType.Int };
            payload = lhsRef;
        }
        else
        {
            throw new IrUnsupportedException("zig `orelse return` requires an optional left operand");
        }

        var ret = retValItem is null ? LowerReturnVoid() : LowerReturn(retValItem);
        pre.Add(new If(test, new Block(new List<CStmt> { ret }), null));
        if (bind is not null) { pre.Add(bind(payload)); }
        return pre.Count == 1 ? pre[0] : new Seq(pre);
    }

    // ---- ANF statement-hoist (the "sub-expression positions" milestone) --------------------------
    //
    // A value-producing construct that lowers to STATEMENTS (a side-effecting/capturing `catch`, a
    // `catch return` / `orelse return`) works at a full RHS (const/var/return/assignment) but not in
    // a SUB-expression (`x + (a catch b())`). The ANF hoist lifts it to a temp before the enclosing
    // statement: `Hoisted` installs a per-statement buffer at each eval-safe point, and the construct
    // appends its pre-statements + a result temp and evaluates to a bare VarRef. Correctness rides on
    // `_hoistImpureSeen`: hoisting past an earlier side effect would reorder it, so that is rejected.

    /// <summary>Lower a statement (via <paramref name="lower"/>) under a fresh ANF hoist buffer, then
    /// prepend any hoisted statements as a brace-less <see cref="Seq"/> (the result temps stay in the
    /// enclosing block scope). A statement with no hoist returns unchanged. Installed only at
    /// eval-safe statement points — NOT a loop condition (re-evaluated per iteration).</summary>
    private CStmt Hoisted(Func<CStmt> lower)
    {
        var savedBuf = _hoist;
        var savedImpure = _hoistImpureSeen;
        _hoist = new List<CStmt>();
        _hoistImpureSeen = false;
        try
        {
            var stmt = lower();
            if (_hoist.Count == 0) { return stmt; }
            var seq = new List<CStmt>(_hoist) { stmt };
            return new Seq(seq);
        }
        finally
        {
            _hoist = savedBuf;
            _hoistImpureSeen = savedImpure;
        }
    }

    /// <summary>Guard + finish a sub-expression hoist: reject when not in a hoistable position
    /// (<see cref="_hoist"/> null) or when a side effect was already evaluated earlier in the
    /// statement (<see cref="_hoistImpureSeen"/> — hoisting past it would reorder). Otherwise lower
    /// the construct (its own internals don't count toward a LATER hoist — restore the flag), append
    /// its <paramref name="pre"/>-computing statements + a <c>__anfN</c> result temp to the buffer,
    /// and return a bare <see cref="VarRef"/> to that temp.</summary>
    private CExpr HoistLowered(string what, List<CStmt> pre, CExpr value, bool savedImpure)
    {
        // Restore the impurity watermark to its PRE-construct value: the construct's own internals
        // (lowered by the caller) are sequenced into the buffer, so they don't block a LATER sibling
        // hoist. RequireHoistable then rejects only a reordering hazard against a PRIOR side effect.
        _hoistImpureSeen = savedImpure;
        var buf = RequireHoistable(what);
        var sym = _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = value.Type });
        buf.AddRange(pre);
        buf.Add(new DeclStmt(new List<LocalDecl> { new(sym, value) }));
        return new VarRef(sym) { Type = value.Type };
    }

    /// <summary>Return the active hoist buffer, or throw a clear error when a statement-lowering
    /// construct appears where it can't be hoisted: no active buffer (e.g. a loop condition), or
    /// after an earlier side effect in the same statement (a reordering hazard — bind to a
    /// <c>const</c> first). Returning the (non-null) buffer avoids a null-forgiving deref at the
    /// call site.</summary>
    private List<CStmt> RequireHoistable(string what)
    {
        if (_hoist is not { } buf)
        {
            throw new IrUnsupportedException(
                $"zig `{what}` is lowered as a `const`/`var` initializer, `return`, assignment, or expression statement — this position (e.g. a loop condition) isn't hoistable; bind it to a `const` first");
        }
        if (_hoistImpureSeen)
        {
            throw new IrUnsupportedException(
                $"zig `{what}` in a sub-expression can't be hoisted past an earlier side-effecting operand in the same statement — bind it to a `const` first");
        }
        return buf;
    }

    /// <summary>True when an item is an <c>error.Foo</c> literal, yielding the error name.</summary>
    private static bool IsErrorLit(Item it, out string name)
    {
        if (it.Content is Zig.ErrorLit e) { name = Tok(e.Arg2); return true; }
        name = "";
        return false;
    }

    /// <summary>True when an item is a set-qualified error reference <c>E.member</c> (Milestone X,
    /// part 2) — a <see cref="Zig.Field"/> whose base names a registered <c>error{…}</c> set —
    /// yielding the member name. dotcc erases set membership, so <c>E.member</c> resolves to the same
    /// flat code as the bare <c>error.member</c> (real zig: the same global error value). Recognized
    /// wherever <see cref="IsErrorLit"/> is — the value path and the <see cref="LowerReturn"/> error
    /// return. Instance (not static like <see cref="IsErrorLit"/>) because it reads <c>_errorSets</c>.</summary>
    private bool TryErrorSetMember(Item it, out string set, out string name)
    {
        if (it.Content is Zig.Field f && f.Arg0.Content is Zig.Ident id && _errorSets.Contains(Tok(id.Arg0)))
        {
            set = Tok(id.Arg0);
            name = Tok(f.Arg2);
            return true;
        }
        set = "";
        name = "";
        return false;
    }

    /// <summary>Reject a set-qualified <c>E.member</c> whose member is not declared in set <c>E</c>
    /// (Milestone X, part 3) — an illegal program real zig rejects, so dotcc does too (a good compiler
    /// rejects illegal programs). Lenient only if <c>E</c> somehow has no recorded members.</summary>
    private void ValidateSetMember(string set, string member)
    {
        if (_errorSetMembers.TryGetValue(set, out var members) && !members.Contains(member))
        {
            throw new CompileException($"zig: error '{member}' is not a member of error set '{set}'");
        }
    }

    /// <summary>Reject a directly-returned error (<c>return error.X;</c> / <c>return E.X;</c>) whose
    /// name is outside the current function's DECLARED error set (Milestone X, part 3) — e.g.
    /// <c>fn f() error{A}!u8 { return error.B; }</c>. No-op when the function is unconstrained (an
    /// inferred <c>!T</c> / <c>anyerror!T</c>, <see cref="_currentFnErrorSet"/> null). V1 checks the
    /// direct-return forms only; an error that flows in through a CALL or <c>try</c> is not yet
    /// set-checked (a documented cut — it would need cross-function set inference).</summary>
    private void CheckReturnedErrorInSet(string errName)
    {
        if (_currentFnErrorSet is { } cs && !cs.members.Contains(errName))
        {
            var which = cs.name is { } n ? $"error set '{n}'" : "the function's declared error set";
            throw new CompileException(
                $"zig: error '{errName}' is not a member of {which} (the function's return-error set)");
        }
    }

    /// <summary>A function's DECLARED error set, for the foreign-error return check (Milestone X,
    /// part 3). Returns false (UNCONSTRAINED — any error is accepted) for an inferred bare <c>!T</c>,
    /// for <c>anyerror!T</c>, or for an unknown set name (real zig infers / widens those); returns true
    /// with the allowed member names for an <c>E!T</c> over a declared set or an inline
    /// <c>error{…}!T</c>.</summary>
    private bool TryDeclaredErrorSet(Item retType, bool errUnion, out string? setName, out HashSet<string> members)
    {
        setName = null;
        members = new HashSet<string>(System.StringComparer.Ordinal);
        if (errUnion) { return false; }                          // bare `!T` — inferred set
        if (retType.Content is not Zig.ErrUnion eu) { return false; }
        switch (eu.Arg0.Content)
        {
            case Zig.Ident id when Tok(id.Arg0) != "anyerror" && _errorSetMembers.TryGetValue(Tok(id.Arg0), out var declared):
                setName = Tok(id.Arg0);
                members = declared;
                return true;
            case Zig.ErrorSet inlineSet:
                foreach (var m in WalkErrSetMembers(inlineSet.Arg2)) { members.Add(m); }
                return true;
            case Zig.ErrorSetEmpty:
                return true;                                     // `error{}!T` — never errors
            default:
                return false;                                    // anyerror / an unknown set name
        }
    }

    /// <summary>Lower a bare <c>error.Foo</c> value to its stable code in the flat global error set,
    /// typed <see cref="CType.ErrorSet"/> (rendered <c>ushort</c>). The code IS the value, so
    /// error-value equality compares codes (<c>e == error.Foo</c> → <c>e == &lt;code&gt;</c>). Shared
    /// by the bare-value lowering (here) and the captured-error binding; <c>return error.Foo;</c>
    /// keeps its dedicated <see cref="ErrUnionErr"/> / <see cref="ZigErrorThrow"/> path in
    /// <see cref="LowerReturn"/>.</summary>
    private CExpr LowerErrorLit(string name)
    {
        var code = ErrorCode(name);
        return new LitInt(code.ToString(CultureInfo.InvariantCulture), code) { Type = CType.ErrorSet };
    }

    // ---- expressions -----------------------------------------------------

    private CExpr LowerExpr(Item expr)
    {
        switch (expr.Content)
        {
            case Zig.IntLit i: return DecodeZigInt(Tok(i.Arg0));
            case Zig.FloatLit f: return new LitFloat(LowerZigFloat(Tok(f.Arg0))) { Type = CType.Double };

            // `true`/`false` — boolean literals (a `bool` value, like `null`/`undefined`). Typed
            // `_Bool` (→ the store-normalising `CBool`, which takes a C# `bool`).
            case Zig.TrueLit:  return new LitBool(true) { Type = CType.Bool };
            case Zig.FalseLit: return new LitBool(false) { Type = CType.Bool };

            // A char literal `'x'` / `'\n'` / `'\xNN'` / `'\u{1F600}'` — Zig's `comptime_int` = the
            // codepoint. The `\u{…}` form is decoded Zig-side (the shared escape machinery has no
            // `\u{…}` arm — and adding one would change the C front-end's `\u` handling); everything
            // else reuses the shared decoder, then lowers to an int literal like a C char constant.
            case Zig.CharLit c:
            {
                var raw = Tok(c.Arg0);
                var body = raw.Length >= 2 ? raw[1..^1] : "";
                var v = body.StartsWith("\\u{", System.StringComparison.Ordinal) && body.EndsWith("}", System.StringComparison.Ordinal)
                    ? System.Convert.ToInt32(body[3..^1].Replace("_", ""), 16)
                    : DotCC.EmitHelpers.DecodeCharLiteral(body);
                return new LitInt(v.ToString(CultureInfo.InvariantCulture), v) { Type = CType.Int };
            }

            // A string literal. Zig's escape set overlaps C's for the common cases
            // (`\n`/`\t`/`\\`/`\"`/`\xNN`), so we reuse the C string machinery: the (escape-expanded)
            // quoted lexeme becomes a single LitStr segment, typed `char[N]` (decoded byte count incl.
            // NUL) so it decays to `char*` exactly like a C literal — the C# backend lowers it to the
            // same pooled `Libc.L("…"u8)` pointer. Two Zig-specific reshapes happen FIRST so the shared
            // decoder is untouched: a `\\`-prefixed multiline string is folded to one quoted lexeme of
            // its raw (un-escaped) content; a `\u{…}` unicode escape is expanded to `\xNN` UTF-8 bytes.
            case Zig.StrLit s:
            {
                var raw = Tok(s.Arg0);
                var lexeme = raw.StartsWith("\\", System.StringComparison.Ordinal)
                    ? FoldZigMultilineString(raw)
                    : ExpandZigUnicodeEscapes(raw);
                var segs = new List<string> { lexeme };
                DotCC.EmitHelpers.EncodeStringLiteral(segs, out var byteLen);
                return new LitStr(segs) { Type = new CType.Array(CType.Char, byteLen) };
            }
            case Zig.Ident id:
            {
                var name = Tok(id.Arg0);
                // A const bound to a provable allocator (Milestone F/U) emitted no runtime decl; used
                // here as a VALUE (e.g. passed to a `std.mem.Allocator` parameter) it materializes the
                // matching fat pointer — `ZigAlloc.CHeap()` for the C-heap default, or
                // `ZigAlloc.FbaAllocator(&fba)` for a devirtualized `fba.allocator()` site. (As a
                // `.alloc`/`.free` RECEIVER it never reaches this case — LowerMethodCall
                // short-circuits to the devirt path.)
                if (_defaultAllocatorBindings.TryGetValue(name, out var boundKind))
                {
                    return boundKind == AllocKind.Fba ? MaterializeFba(_fbaAllocatorSites[name]) : MaterializeCHeap();
                }
                if (_symbols.Resolve(name) is { } sym)
                {
                    // A `comptime var`/`comptime const` (Milestone T) — substitute its CURRENT
                    // lowering-time value as a literal, so an `inline while` condition / body folds.
                    if (_comptimeVars.TryGetValue(sym, out var cv)) { return ComptimeVarLit(cv.Value, cv.Type); }
                    return new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
                }
                // A bare (unqualified) sibling container const (Milestone R, part 6): inside a
                // container const's RHS re-lower (`_currentConstContainer` set), an unresolved name may
                // name a SIBLING const — inline it (comptime). Outside that, the unresolved error holds.
                if (_currentConstContainer is { } cc
                    && _containerConsts.TryGetValue(cc, out var sibs)
                    && sibs.TryGetValue(name, out var sib))
                {
                    return LowerContainerConst(cc, name, sib.typeItem, sib.rhs);
                }
                throw new IrUnsupportedException($"unresolved identifier '{name}'");
            }
            case Zig.Grouped g: { var inner = LowerExpr(g.Arg1); return new Paren(inner) { Type = inner.Type }; }

            // if (cond) a else b  — the if-EXPRESSION, lowered to a ternary. Both
            // branches are RhsExpr; the backend wraps the condition in Cond.B.
            case Zig.IfExpr e:
            {
                var then = LowerExpr(e.Arg4);
                return new CondExpr(LowerExpr(e.Arg2), then, LowerExpr(e.Arg6)) { Type = then.Type };
            }
            // A switch EXPRESSION reached with no result-location type (e.g. `x = switch(y){…}`
            // where the LHS type still flows in via LowerExprSink, or an inferred `const`). The
            // sink-carrying path is in LowerExprSink; here the arm types are inferred.
            case Zig.SwitchExpr s:         return LowerSwitchExpr(s.Arg2, s.Arg5, null);
            case Zig.SwitchExprTrailing s: return LowerSwitchExpr(s.Arg2, s.Arg5, null);

            // A labeled value-block in a pure-expression position (an if/switch-expression arm, a
            // binary sub-operand) — it produces a value via statements, which a C# expression can't
            // host, so it's supported only as a full `=` / `return` / assignment RHS (intercepted in
            // DeclOf / LowerReturn / StmtAssign before reaching here). A clear deferred error.
            case Zig.LabeledBlock lb:
                throw new IrUnsupportedException(
                    $"a labeled value-block (`{Tok(lb.Arg0)}: {{ … }}`) is supported only as a full initializer, " +
                    "`return`, or assignment right-hand side (including as a value-position if/switch branch there, " +
                    "Milestone Y part 1) — not inside a sub-expression yet");

            // arithmetic
            case Zig.Add a:     return Bin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.Sub a:     return Bin(BinOp.Sub, a.Arg0, a.Arg2);
            case Zig.Mul a:     return Bin(BinOp.Mul, a.Arg0, a.Arg2);
            // wrapping arithmetic (Milestone P) — two's-complement wrap at the operand width
            case Zig.AddWrap a: return WrapBin(BinOp.Add, a.Arg0, a.Arg2);
            case Zig.SubWrap a: return WrapBin(BinOp.Sub, a.Arg0, a.Arg2);
            case Zig.MulWrap a: return WrapBin(BinOp.Mul, a.Arg0, a.Arg2);
            // saturating arithmetic (Milestone P) — clamp to the operand-type range via ZigMath
            case Zig.AddSat a:  return SatBin("SatAdd", a.Arg0, a.Arg2);
            case Zig.SubSat a:  return SatBin("SatSub", a.Arg0, a.Arg2);
            case Zig.MulSat a:  return SatBin("SatMul", a.Arg0, a.Arg2);
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
                // `&fn` (address of a function) is a fn-POINTER VALUE — collapse pointer-to-function to
                // the bare `CType.Func` (matching the `*const fn (…)` type collapse), so an INFERRED
                // `const f = &fn;` global/local is itself callable (a `Pointer(Func)` would not be).
                if (operand.Type.Unqualified is CType.Func)
                {
                    return new Unary(UnOp.AddrOf, operand) { Type = operand.Type };
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
                var unwrapped = new ZigTry(inner) { Type = eu.Payload };
                // A `create`-style error-union-over-pointer (`Error!*T`, Milestone U) carries its
                // payload as a `nuint` (a pointer can't be an `ErrUnion<T>` generic arg), so
                // `ErrUnion.Try(...)` yields a `nuint`; cast it back to the `T*` the payload names.
                // `create` is the only producer of a pointer-payload union, so the cast is
                // exactly-and-only correct here.
                if (eu.Payload.Unqualified is CType.Pointer)
                {
                    return new Cast(eu.Payload, unwrapped) { Type = eu.Payload };
                }
                return unwrapped;
            }
            // `comptime EXPR` (Milestone T) — force compile-time evaluation of a value. The inner
            // expression is lowered now, but wrapped in a deferred ComptimeFold and queued; it is
            // evaluated + spliced after pass 2 (so a `comptime fib(10)` sees its callee's lowered
            // body regardless of declaration order). The fold carries the inner expression's type.
            case Zig.PreComptime p:
            {
                var inner = LowerExpr(p.Arg1);
                var fold = new ComptimeFold(inner) { Type = inner.Type };
                _pendingComptimeFolds.Add(fold);
                return fold;
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
                    // A namespaced container const — re-lower its RHS (comptime; inlined per use).
                    return LowerContainerConst(cContainer, fieldName, centry.typeItem, centry.rhs);
                }
                // A namespaced container `var` (Milestone R, part 6) — `Type.name` resolves to the
                // mangled global's VarRef (an lvalue, so `Type.name = x` / `+= x` write through it).
                if (fld.Arg0.Content is Zig.Ident cvid
                    && _symbols.Resolve(Tok(cvid.Arg0)) is null
                    && TryLookupContainerType(Tok(cvid.Arg0), out var cvBaseTy)
                    && ContainerTypeName(cvBaseTy) is { } cvContainer
                    && _containerVars.TryGetValue(cvContainer, out var cvars)
                    && cvars.TryGetValue(fieldName, out var cvSym))
                {
                    return new VarRef(cvSym) { Type = cvSym.Type, IsLValue = true };
                }
                if (fld.Arg0.Content is Zig.Ident bid
                    && TryLookupContainerType(Tok(bid.Arg0), out var baseTy)
                    && baseTy.Unqualified is CType.Enum en)
                {
                    return ResolveEnumLit(fieldName, en);
                }
                // `E.member` where E is a registered error set (Milestone X, part 2) — the
                // set-qualified form of `error.member`, resolving to the same flat code (membership
                // erased). A USE as a value: bound to a const/var, compared (`x == E.member`), a
                // `catch`/`switch` operand. The error-RETURN form is handled in LowerReturn. Part 3
                // rejects a member not declared in the set (a good compiler rejects illegal programs).
                if (TryErrorSetMember(expr, out var esSet, out var esMember))
                {
                    ValidateSetMember(esSet, esMember);
                    return LowerErrorLit(esMember);
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
                // Fixed-array `.len` — a `[N]T`'s length is the comptime-known element count N
                // (Zig). The array lowered to a pointer (no runtime length field), so fold to a
                // literal. A fixed array has no `.ptr` (that's a slice / many-item-pointer field —
                // take `&arr` for a pointer); reject any other field clearly.
                if (structExpr.Type.Unqualified is CType.Array arrTy)
                {
                    if (fieldName != "len")
                    {
                        throw new IrUnsupportedException($"array has no field '{fieldName}' (only .len)");
                    }
                    if (arrTy.Count is not int arrLen)
                    {
                        throw new IrUnsupportedException("array `.len` requires a compile-time-known length");
                    }
                    return new LitInt(arrLen.ToString(System.Globalization.CultureInfo.InvariantCulture), arrLen) { Type = CType.ULong };
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
                return BuildSlice(LowerExpr(sr.Arg0), LowerExpr(sr.Arg2), LowerExpr(sr.Arg4));

            // Open-ended slicing `a[lo..]` → the high bound is the source length, so the
            // result is `{ a.ptr + lo, sourceLen - lo }`. Only a known-length source (slice
            // or array) has a length; a bare pointer is rejected (as Zig does).
            case Zig.SliceOpen so:
                return BuildSlice(LowerExpr(so.Arg0), LowerExpr(so.Arg2), null);

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

            // `@builtin(...)`. Several Zig builtins are RESULT-LOCATION-typed (`@intCast`,
            // `@ptrCast`, …) — they infer the target from the sink, which only LowerExprSink
            // carries; reached here (no sink) they error clearly. The sink-carrying forms (and
            // the sink-free `@as`/`@intFromEnum`/`@sizeOf`/`@alignCast`) share one lowering.
            case Zig.BuiltinCall b: return LowerBuiltinCall(b, null);

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
            // propagation). A simple, side-effect-free `b` keeps the eager `ErrUnion.Catch(a, b)`
            // (C# evaluates `b` before the call, which is observationally identical to Zig's lazy
            // form only when `b` has no side effects). A side-effecting `b` (Milestone N, part 3)
            // needs a LAZY lowering that runs `b` only on error, which requires a statement context
            // (a `const`/`var` initializer — see DeclOf / LowerCatchValue); in a sub-expression
            // position only the eager form is available.
            case Zig.CatchOp c:
            {
                // Lower ONCE (snapshot the impurity watermark first — the union operand is a call that
                // sets it). A simple, side-effect-free fallback → the eager `ErrUnion.Catch` (no pre,
                // the union call legitimately counts as a side effect). A side-effecting fallback →
                // pre-statements; hoist them to a temp before the enclosing statement (the ANF pass).
                var savedC = _hoistImpureSeen;
                var (pre, value) = LowerCatchValue(c.Arg0, null, c.Arg2);
                if (pre.Count == 0) { return value; }
                return HoistLowered("catch", pre, value, savedC);
            }
            // `a catch |e| b` (Milestone N, part 3) — bind the error to `e` for the fallback `b`,
            // evaluated lazily (only on error). The bind is a statement, so hoist it (ANF).
            case Zig.CatchCapture cc:
            {
                var savedCc = _hoistImpureSeen;
                var (pre, value) = LowerCatchValue(cc.Arg0, Tok(cc.Arg3), cc.Arg5);
                return HoistLowered("catch |e|", pre, value, savedCc);
            }

            // `a catch return [x]` / `a orelse return [x]` (control-flow fallback) — the `return` is a
            // statement. In a full-RHS position DeclOf/LowerStmt handle it; in a SUB-expression it is
            // hoisted to a temp before the enclosing statement (ANF): the conditional `return` and the
            // payload capture become buffer statements, and the construct evaluates to the payload temp.
            case Zig.CatchReturn or Zig.CatchReturnVoid or Zig.OrElseReturn or Zig.OrElseReturnVoid:
            {
                IsControlFlowFallback(expr, out var cfLhs, out var cfIsCatch, out var cfRet);
                var buf = RequireHoistable(cfIsCatch ? "catch return" : "orelse return");
                var savedImpure = _hoistImpureSeen;
                Symbol? anfSym = null;
                var cfStmt = LowerControlFlowFallback(cfLhs, cfIsCatch, cfRet, payload =>
                {
                    anfSym = _symbols.Declare(new Symbol { Name = "__anf" + _anfTempCounter++, Kind = SymKind.Var, Type = payload.Type });
                    return new DeclStmt(new List<LocalDecl> { new(anfSym, payload) });
                });
                _hoistImpureSeen = savedImpure;   // the construct's internals are sequenced in the buffer
                if (anfSym is not { } payloadSym)
                {
                    throw new IrUnsupportedException("internal: control-flow fallback did not bind a payload");
                }
                buf.Add(cfStmt);
                return new VarRef(payloadSym) { Type = payloadSym.Type };
            }

            // A bare `error.Foo` value (Milestone N): the error's stable code in the flat global
            // set, typed `CType.ErrorSet` (rendered `ushort`). This makes error values usable
            // outside `return error.Foo;` — bound to a const/var and compared (`e == error.Foo`)
            // via the ordinary `==`/`!=` lowering, since equal codes mean equal error values. V1
            // still erases the named SET (an explicit `error{A,B}` decl / a named `E!T` is later
            // Milestone N work); the comparison just matches codes.
            case Zig.ErrorLit el:
                return LowerErrorLit(Tok(el.Arg2));

            // `error{A, B}` as a VALUE is not meaningful in dotcc's erased model — it is only a
            // `const E = error{…};` declaration (handled in TryComptimeConstBinding, emits nothing)
            // or an (ignored) set in an `E!T` return type.
            case Zig.ErrorSet:
            case Zig.ErrorSetEmpty:
                throw new IrUnsupportedException(
                    "zig `error{…}` set literal is only valid as a `const E = error{…};` declaration or an `E!T` return-type set");

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
        var result = LowerCallInner(calleeItem, argListItem);
        // A call is a side effect, evaluated AFTER its arguments. Mark the ANF impurity watermark so a
        // LATER sibling `catch`/`orelse` in the same statement is NOT hoisted past this call (which
        // would reorder it). An argument's own `catch`/`orelse` was already lowered (and checked)
        // inside LowerCallInner, BEFORE this set — so `f(a catch b)` still hoists cleanly.
        if (_hoist is not null) { _hoistImpureSeen = true; }
        return result;
    }

    private CExpr LowerCallInner(Item calleeItem, Item? argListItem)
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
        // A real (named) function → a direct, by-name call.
        if (sym.Kind is SymKind.Func && sym.Type.Unqualified is CType.Func)
        {
            return BuildCall(sym, argItems, receiver: null);
        }
        // A fn-pointer VALUE — a `delegate*` local / parameter typed `CType.Func` (Milestone W,
        // part 1a) → an INDIRECT call through the variable, each argument result-located against
        // the fn-pointer's parameter type (Zig result-locates call arguments). Renders as
        // `op(args)` over the (renamed-safe) VarRef.
        if (sym.Type.Unqualified is CType.Func fnptr)
        {
            var callee = new VarRef(sym) { Type = sym.Type, IsLValue = sym.Kind is SymKind.Var or SymKind.Param };
            if (argItems.Count != fnptr.Params.Count)
            {
                throw new IrUnsupportedException(
                    $"call through fn-pointer '{name}': expected {fnptr.Params.Count} argument(s), got {argItems.Count}");
            }
            var args = new List<CExpr>(argItems.Count);
            for (var i = 0; i < argItems.Count; i++)
            {
                args.Add(LowerExprSink(argItems[i], fnptr.Params[i]));
            }
            return new IndirectCall(callee, args) { Type = fnptr.Return };
        }
        throw new IrUnsupportedException($"'{name}' is not callable (expected a function or a fn-pointer value)");
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

        // --- curated `std.mem` helpers (a static call on the std.mem namespace) ---
        // Routed before the generic dispatch. dotcc models no `std` in general — only this curated
        // set of the most common slice utilities; an unmodeled member is a clear, specific error.
        if (TryResolveStdPath(fld.Arg0, out var stdNs) && stdNs == "std.mem")
        {
            return LowerStdMemCall(methodName, argItems);
        }

        // --- Zig allocators (Milestone F), before the generic method dispatch ---
        // `std.heap.FixedBufferAllocator.init(buf)` — a static call on the std FBA type.
        if (methodName == "init" && TryResolveStdPath(fld.Arg0, out var basePath) && basePath == "std.heap.FixedBufferAllocator")
        {
            return LowerFbaInit(argItems);
        }
        // `std.heap.ArenaAllocator.init(backing)` — a static call on the std arena type (Milestone U).
        if (methodName == "init" && TryResolveStdPath(fld.Arg0, out var arenaBase) && arenaBase == "std.heap.ArenaAllocator")
        {
            return LowerArenaInit(argItems);
        }
        // `a.alloc(T, n)` / `a.free(s)` (and the deferred `create`/`destroy`) on a known-default
        // (→ devirt) or an Allocator-typed receiver (→ indirect). A same-named method on a
        // non-allocator receiver falls through to the generic dispatch below.
        if (methodName is "alloc" or "free" or "create" or "destroy" or "realloc" or "resize" or "remap"
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
        // `fba.allocator()` / `arena.allocator()` — a FixedBufferAllocator (Milestone F) or an
        // ArenaAllocator (Milestone U) hands out an Allocator fat pointer over itself. Needs `&self`
        // as the vtable context; the result is opaque (→ the indirect dispatch path). Handled before
        // the generic _methods lookup (both are runtime types, not Zig-declared containers).
        if (methodName == "allocator" && recv.Type.Unqualified is CType.Named { Name: var allocTy }
            && allocTy is FbaTypeName or ArenaTypeName)
        {
            if (argItems.Count != 0)
            {
                throw new IrUnsupportedException($"zig `{allocTy}.allocator()` takes no arguments");
            }
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } s })
            {
                s.AddressTaken = true;
            }
            var selfAddr = new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
            var factory = allocTy == FbaTypeName ? "ZigAlloc.FbaAllocator" : "ZigAlloc.ArenaToAllocator";
            return new Call(factory, new List<CExpr> { selfAddr },
                new List<CType> { new CType.Pointer(new CType.Named(allocTy)) }, null) { Type = new CType.Allocator() };
        }
        // `arena.deinit()` — free the whole arena chunk chain (Milestone U). A static wrapper called
        // by-ref on the local, like `allocator()`. The headline use is `defer arena.deinit();`.
        if (methodName == "deinit" && recv.Type.Unqualified is CType.Named { Name: ArenaTypeName })
        {
            if (argItems.Count != 0)
            {
                throw new IrUnsupportedException("zig `arena.deinit()` takes no arguments");
            }
            if (Unparen(recv) is VarRef { Sym: { Kind: SymKind.Var or SymKind.Param } sd })
            {
                sd.AddressTaken = true;
            }
            var arenaAddr = new Unary(UnOp.AddrOf, recv) { Type = new CType.Pointer(recv.Type) };
            return new Call("ZigAlloc.ArenaDeinit", new List<CExpr> { arenaAddr },
                new List<CType> { new CType.Pointer(new CType.Named(ArenaTypeName)) }, null) { Type = CType.Void };
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

    /// <summary>Try to lower an allocator method call <c>a.alloc(T, n)</c> / <c>a.free(s)</c> /
    /// <c>a.create(T)</c> / <c>a.destroy(p)</c> (Milestone F/U). The receiver is DEVIRTUALIZED to a
    /// direct call when provable: the statically-known C-heap default (→ <c>ZigAlloc.*CHeap</c>, a
    /// direct <c>Libc.malloc</c>/<c>free</c>) or a provable <c>fba.allocator()</c> site (→
    /// <c>ZigAlloc.*Fba(&amp;fba, …)</c>, a direct FBA bump). Otherwise the receiver is lowered and,
    /// if it is an <see cref="CType.Allocator"/>, dispatched indirectly through its vtable. Returns
    /// <c>false</c> (so the caller falls through to the generic method dispatch) when the receiver is
    /// neither — i.e. a same-named method on a non-allocator type.</summary>
    private bool TryLowerAllocatorMethod(Zig.Field fld, string methodName, IReadOnlyList<Item> argItems, out CExpr result)
    {
        result = null!;
        bool devirt = TryKnownAllocatorKind(fld.Arg0, out var kind);
        CExpr? recv = null;
        // For an FBA-site devirt the `&fba` context rides on the IR node's FbaCtx (takes precedence
        // over a null Receiver, which alone would mean the C-heap default). The C-heap devirt leaves
        // both null; a non-devirt receiver is lowered and dispatched indirectly.
        CExpr? fbaCtx = devirt && kind == AllocKind.Fba ? FbaCtxFor(fld.Arg0) : null;
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
                result = new AllocCall(recv, elem, count, ErrorCode("OutOfMemory"), fbaCtx)
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
                result = new FreeCall(recv, sliceExpr, slc.Element, fbaCtx) { Type = CType.Void };
                return true;
            }
            case "create":   // single-object alloc → Error!*T (Milestone U)
            {
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig allocator `.create` expects (type); got {argItems.Count} argument(s)");
                }
                var elem = LowerType(argItems[0]);
                // `Error!*T` is represented `ErrorUnion(Pointer(T))` at the IR-type level (so `try`
                // unwraps to a `*T`); the runtime carrier is `ErrUnion<nuint>` (a pointer can't be
                // an ErrUnion<T> generic arg). The `try` lowering casts the unwrapped nuint to T*.
                result = new CreateCall(recv, elem, ErrorCode("OutOfMemory"), fbaCtx)
                {
                    Type = new CType.ErrorUnion(new CType.Pointer(elem)),
                };
                return true;
            }
            case "destroy":   // free a single object from `.create` (Milestone U)
            {
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException($"zig allocator `.destroy` expects (pointer); got {argItems.Count} argument(s)");
                }
                var ptrExpr = LowerExpr(argItems[0]);
                if (ptrExpr.Type.Unqualified is not CType.Pointer pp)
                {
                    throw new IrUnsupportedException($"zig allocator `.destroy` expects a pointer argument, got {ptrExpr.Type.Describe()}");
                }
                result = new DestroyCall(recv, ptrExpr, pp.Pointee, fbaCtx) { Type = CType.Void };
                return true;
            }
            case "realloc":   // grow/shrink a slice → Error![]T (Milestone U)
            {
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException($"zig allocator `.realloc` expects (slice, new count); got {argItems.Count} argument(s)");
                }
                var oldSlice = LowerExpr(argItems[0]);
                if (oldSlice.Type.Unqualified is not CType.Slice rslc)
                {
                    throw new IrUnsupportedException($"zig allocator `.realloc` expects a slice argument, got {oldSlice.Type.Describe()}");
                }
                var newCount = LowerExpr(argItems[1]);
                result = new ReallocCall(recv, oldSlice, rslc.Element, newCount, ErrorCode("OutOfMemory"), fbaCtx)
                {
                    Type = new CType.ErrorUnion(new CType.Slice(rslc.Element)),
                };
                return true;
            }
            case "resize":   // in-place resize → bool (deferred — see below)
            case "remap":    // resize-possibly-moving → ?[]T (deferred — see below)
                // `resize` returns whether the block grew/shrank IN PLACE (no move); `remap` returns
                // the possibly-moved slice or null. Both outcomes are allocator-page-dependent (real
                // zig's page_allocator answers from page rounding), so matching the true/false / null
                // observably would need per-allocator in-place tracking dotcc doesn't model yet. Clear
                // deferred error rather than a divergent guess — use `.realloc` (which always works).
                throw new IrUnsupportedException(
                    $"zig allocator `.{methodName}` is deferred (its in-place / optional result is allocator-page-dependent); use `.realloc`");
            default:   // unreachable: the caller only dispatches the allocator method names above
                return false;
        }
    }

    /// <summary>Build the <c>&amp;fba</c> context for a devirtualized FBA-site allocator call
    /// (Milestone U). <paramref name="allocExpr"/> is the <c>a</c> identifier bound to an
    /// <c>fba.allocator()</c> site in <see cref="_fbaAllocatorSites"/> (the kind is
    /// <see cref="AllocKind.Fba"/>, so it is necessarily a <see cref="Zig.Ident"/>).</summary>
    private CExpr FbaCtxFor(Item allocExpr)
    {
        var fbaSym = _fbaAllocatorSites[Tok(((Zig.Ident)allocExpr.Content!).Arg0)];
        fbaSym.AddressTaken = true;
        // IsLValue=true so the backend takes `&fba` directly — without it, `&<rvalue>` would
        // materialize a COPY of `fba` per call (`__clN = fba; &__clN`), so the bump cursor would
        // not be shared across allocations.
        var fbaRef = new VarRef(fbaSym) { Type = fbaSym.Type, IsLValue = true };
        return new Unary(UnOp.AddrOf, fbaRef) { Type = new CType.Pointer(fbaSym.Type) };
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

    /// <summary>Lower <c>std.heap.ArenaAllocator.init(backing)</c> (Milestone U) to
    /// <c>ArenaAllocator.Init(backing)</c>. The single argument is the backing
    /// <c>std.mem.Allocator</c> — the statically-known default materializes a runtime C-heap
    /// <see cref="CType.Allocator"/> through the ordinary value path (so the arena draws its chunks
    /// from a real allocator); an opaque allocator value is taken as-is.</summary>
    private CExpr LowerArenaInit(IReadOnlyList<Item> argItems)
    {
        if (argItems.Count != 1)
        {
            throw new IrUnsupportedException($"zig `ArenaAllocator.init` expects (backing allocator); got {argItems.Count} argument(s)");
        }
        var backing = LowerExpr(argItems[0]);
        if (backing.Type.Unqualified is not CType.Allocator)
        {
            throw new IrUnsupportedException(
                $"zig `ArenaAllocator.init` expects a `std.mem.Allocator` backing, got {backing.Type.Describe()}");
        }
        return new Call("ArenaAllocator.Init", new List<CExpr> { backing },
            new List<CType> { new CType.Allocator() }, null) { Type = new CType.Named(ArenaTypeName) };
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
        // Pointer arithmetic on a Zig many-item pointer (`[*]T` / `[*c]T`, both lowered to
        // `CType.Pointer`): `p + i` / `p - i` yields the pointer type, and `p - q` yields a
        // signed offset (`long`). `UsualArithmetic` only knows `Prim`s — it returns `int` for a
        // pointer operand — so handle the pointer cases here, mirroring the C frontend's
        // `IrBuilder.BinaryType`. (Zig fixed arrays are values and don't decay in arithmetic, so
        // only `CType.Pointer` participates; you slice an array before pointer-walking it.)
        var lPtr = left.Type.Unqualified is CType.Pointer;
        var rPtr = right.Type.Unqualified is CType.Pointer;
        var type = op switch
        {
            BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge
                or BinOp.LogAnd or BinOp.LogOr => CType.Int,
            BinOp.Shl or BinOp.Shr => CType.IntegerPromote(left.Type),
            BinOp.Add or BinOp.Sub when lPtr || rPtr
                => lPtr && rPtr ? CType.Long : (lPtr ? left.Type.Unqualified : right.Type.Unqualified),
            _ => CType.UsualArithmetic(left.Type, right.Type),
        };
        return new Binary(op, left, right) { Type = type };
    }

    /// <summary>Lower a Zig WRAPPING arithmetic operator (<c>+%</c>/<c>-%</c>/<c>*%</c>) —
    /// two's-complement arithmetic that wraps at the OPERAND width. Zig has no integer promotion,
    /// so the result type is the peer-resolved operand type (<see cref="PeerIntType"/>), and the
    /// wrap happens at that width. The emitted C# runs in the project's default <c>unchecked</c>
    /// context, where a narrowing cast truncates rather than throwing. For a sub-<c>int</c> peer
    /// width (<c>byte</c>/<c>short</c>/…) C# would promote the operands to <c>int</c> and so NOT
    /// wrap at the operand width — so a truncating <see cref="Cast"/> back to the peer type is
    /// inserted (correct even when the result is then widened: <c>u8 +% u8</c> wraps at 8 bits
    /// BEFORE any widening). At <c>int</c> and wider, native C# arithmetic already wraps at the
    /// right width, so no cast is needed.</summary>
    private CExpr WrapBin(BinOp op, Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var t = PeerIntType(left, right);
        var inner = new Binary(op, left, right) { Type = t };
        return t.SizeOf < 4 ? new Cast(t, inner) { Type = t } : inner;
    }

    /// <summary>The fixed-width integer type a wrapping/saturating operator wraps (or saturates) at —
    /// Zig's peer-resolved operand type. Valid Zig gives both operands one shared type; a bare integer
    /// literal (a <c>comptime_int</c>, lowered to a <see cref="LitInt"/>) yields to its concrete-typed
    /// peer. With both concrete the wider wins (they are equal in valid Zig; ties resolve to the left).
    /// Two comptime literals have no fixed-width peer — Zig evaluates them at comptime (exact, then
    /// coerced to the result location, erroring if it overflows), so dotcc just picks the wider integer
    /// (a fit-checking comptime engine is out of scope; a non-fitting literal pair is already a Zig
    /// error, never round-trippable code).</summary>
    private static CType PeerIntType(CExpr left, CExpr right)
    {
        var lt = left.Type.Unqualified;
        var rt = right.Type.Unqualified;
        if (left is LitInt && right is not LitInt) return rt;
        if (right is LitInt && left is not LitInt) return lt;
        return lt.SizeOf >= rt.SizeOf ? lt : rt;
    }

    /// <summary>Lower a Zig SATURATING arithmetic operator (<c>+|</c>/<c>-|</c>/<c>*|</c>) to a
    /// <c>ZigMath.Sat{Add,Sub,Mul}&lt;T&gt;</c> call (<see cref="DotCC.Libc.ZigMath"/>) that clamps
    /// the true result to the operand type's range. Zig has no integer promotion, so the result
    /// type is the peer-resolved operand type (<see cref="PeerIntType"/>); both operands are coerced
    /// to it so C# infers the generic <c>T</c> and the runtime clamps at the right width. Unlike
    /// wrapping (a truncating cast in the unchecked context), a clamp has no native C# operator, so
    /// this routes through the spliced runtime.</summary>
    private CExpr SatBin(string helper, Item l, Item r)
    {
        var left = LowerExpr(l);
        var right = LowerExpr(r);
        var t = PeerIntType(left, right);
        GuardNo128Saturation(t);
        var args = new List<CExpr> { CoerceToPeer(left, t), CoerceToPeer(right, t) };
        return new Call($"ZigMath.{helper}", args) { Type = t };
    }

    /// <summary>Reject a saturating op (<c>+|</c>/<c>-|</c>/<c>*|</c>) at a 128-bit operand width.
    /// <see cref="DotCC.Libc.ZigMath"/> clamps via an exact-in-128-bit accumulator, which a 16-byte
    /// operand would itself overflow — so it can't honor the saturation contract there. Wrapping
    /// (<c>+%</c>) and ordinary arithmetic on <c>i128</c>/<c>u128</c> are unaffected (native C#
    /// <c>Int128</c>/<c>UInt128</c>). A documented V1 cut.</summary>
    private static void GuardNo128Saturation(CType t)
    {
        if (t.Unqualified is CType.Prim { Integer: true, Bytes: >= 16 })
        {
            throw new IrUnsupportedException(
                "saturating arithmetic (+|/-|/*|) on a 128-bit integer is not supported — the exact " +
                "128-bit accumulator would itself overflow; use wrapping (+%) or clamp manually");
        }
    }

    /// <summary>Coerce a wrapping/saturating operand to the peer integer type, skipping the cast when
    /// it already has that type — so <c>i32 +| i32</c> emits <c>ZigMath.SatAdd(a, b)</c> with no
    /// redundant casts, while <c>u8 +| 5</c> casts the literal so C# infers <c>byte</c>.</summary>
    private static CExpr CoerceToPeer(CExpr e, CType t)
        => e.Type.Unqualified.Equals(t) ? e : new Cast(t, e) { Type = t };

    /// <summary>Lower a Zig SATURATING compound assignment (<c>x op|= y</c>). There is no native C#
    /// saturating compound operator, so it desugars to <c>target = ZigMath.Sat…(target, y)</c> at the
    /// LHS width. The lvalue is read on both sides; that is sound only when re-evaluating it has no
    /// side effects, so a non-repeatable target (an index/deref reached through a call) is a clear
    /// deferred error rather than a silent double-eval.</summary>
    private CStmt SatCompoundAssign(Item targetItem, string helper, Item valueItem)
    {
        var target = LowerExpr(targetItem);
        if (!IsRepeatableLValue(target))
        {
            throw new IrUnsupportedException(
                "a saturating compound assignment (`x op|= y`) to a target with side effects is not " +
                "supported yet — assign in two steps (`x = x op| y;`) with a simpler target");
        }
        GuardNo128Saturation(target.Type);
        var value = LowerExprSink(valueItem, target.Type);
        var call = new Call($"ZigMath.{helper}", new List<CExpr> { target, CoerceToPeer(value, target.Type) })
            { Type = target.Type };
        return new ExprStmt(new Assign(null, target, call) { Type = target.Type });
    }

    /// <summary>True when <paramref name="e"/> is an lvalue that can be re-evaluated without side
    /// effects — a variable / parameter, or a field / element / deref reached only through other
    /// repeatable sub-expressions and constants. A call anywhere makes it non-repeatable. Gates the
    /// double-read in <see cref="SatCompoundAssign"/>, whose desugar has no single-eval form.</summary>
    private static bool IsRepeatableLValue(CExpr e) => e switch
    {
        VarRef => true,
        LitInt or LitBool or LitFloat => true,
        Paren p => IsRepeatableLValue(p.Inner),
        Cast c => IsRepeatableLValue(c.Operand),
        Member m => IsRepeatableLValue(m.Base),
        Unary { Op: UnOp.Deref or UnOp.AddrOf } u => IsRepeatableLValue(u.Operand),
        DotCC.Ir.Index ix => IsRepeatableLValue(ix.Base) && IsRepeatableLValue(ix.Idx),
        _ => false,
    };

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
        // `const a = fba.allocator();` over a known `FixedBufferAllocator` local — DEVIRTUALIZE the
        // site (Milestone U): record `a → Fba(fbaSym)` and emit NO decl, so a later `a.alloc(…)`/
        // `.free(…)` lowers to a direct FBA bump over `&fba` (no vtable). A value use of `a` later
        // materializes `ZigAlloc.FbaAllocator(&fba)` (see the VarRef value path / MaterializeFba), so
        // this is an optimization, not a restriction — an escaping `a` still works, just indirectly.
        if (rhs.Content is Zig.CallNoArgs { Arg0.Content: Zig.Field afld }
            && Tok(afld.Arg2) == "allocator"
            && afld.Arg0.Content is Zig.Ident fbaId
            && _symbols.Resolve(Tok(fbaId.Arg0)) is { } fbaSym
            && fbaSym.Type.Unqualified is CType.Named { Name: FbaTypeName })
        {
            _defaultAllocatorBindings[name] = AllocKind.Fba;
            _fbaAllocatorSites[name] = fbaSym;
            fbaSym.AddressTaken = true;
            return true;
        }
        // `const E = error{A, B};` — an explicit error-set declaration (Milestone N, part 5). dotcc
        // erases the set into the flat global code space, so register the member names (assigning
        // each a stable code, in declaration order) and emit NO decl; `E` is then used only as the
        // (erased) set in an `E!T` return type, where LowerType ignores the set name anyway. An empty
        // `error{}` (the never-erroring set) has no members to register.
        if (rhs.Content is Zig.ErrorSet es)
        {
            var members = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var member in WalkErrSetMembers(es.Arg2)) { ErrorCode(member); members.Add(member); }
            _errorSets.Add(name);
            _errorSetMembers[name] = members;   // Milestone X, part 3 — for the membership checks
            return true;
        }
        if (rhs.Content is Zig.ErrorSetEmpty)
        {
            _errorSets.Add(name);
            _errorSetMembers[name] = new HashSet<string>(System.StringComparer.Ordinal);  // `error{}` — no members
            return true;
        }
        return false;
    }

    /// <summary>Walk an <c>error{ A, B, … }</c> member list (the right-recursive <c>ErrSetList</c>)
    /// into its member names, mirroring the grammar's one / trailing-comma / cons shapes.</summary>
    private static IEnumerable<string> WalkErrSetMembers(Item list)
    {
        var cur = list;
        while (true)
        {
            switch (cur.Content)
            {
                case Zig.ErrSetOne o:         yield return Tok(o.Arg0); yield break;
                case Zig.ErrSetOneTrailing o: yield return Tok(o.Arg0); yield break;
                case Zig.ErrSetCons c:        yield return Tok(c.Arg0); cur = c.Arg2; break;
                default:                      yield break;
            }
        }
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
        => _imports.ContainsKey(name) || _defaultAllocatorBindings.ContainsKey(name) || _errorSets.Contains(name);

    /// <summary>Strip the surrounding double quotes from a Zig string-literal lexeme. Used only
    /// for the simple identifier-shaped module name in <c>@import("…")</c> (no escapes).</summary>
    private static string UnquoteStringLiteral(string raw)
        => raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;

    /// <summary>The materialized C-heap default allocator as a runtime <see cref="CType.Allocator"/>
    /// value (<c>ZigAlloc.CHeap()</c>) — emitted when the statically-known default flows into an
    /// opaque allocator sink (a value position, not a devirtualizable <c>.alloc</c> receiver).</summary>
    private static CExpr MaterializeCHeap()
        => new Call("ZigAlloc.CHeap", new List<CExpr>(), new List<CType>(), null) { Type = new CType.Allocator() };

    /// <summary>The materialized runtime <see cref="CType.Allocator"/> for a devirtualized
    /// <c>fba.allocator()</c> site (Milestone U) — <c>ZigAlloc.FbaAllocator(&amp;fba)</c>, emitted
    /// when the FBA-bound name flows into an opaque allocator sink (a value position rather than a
    /// devirtualizable <c>.alloc</c> receiver).</summary>
    private static CExpr MaterializeFba(Symbol fbaSym)
    {
        fbaSym.AddressTaken = true;
        var fbaRef = new VarRef(fbaSym) { Type = fbaSym.Type, IsLValue = true };   // &fba direct, not a copy
        var addr = new Unary(UnOp.AddrOf, fbaRef) { Type = new CType.Pointer(fbaSym.Type) };
        return new Call("ZigAlloc.FbaAllocator", new List<CExpr> { addr },
            new List<CType> { new CType.Pointer(new CType.Named(FbaTypeName)) }, null) { Type = new CType.Allocator() };
    }

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
        Zig.TyPointer p    => PointerTo(LowerType(p.Arg1)),
        Zig.TyPtrConst p   => PointerTo(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TyCPtr p       => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyCPtrConst p  => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        // `[*]T` / `[*]const T` many-item pointers (Milestone O, part 2) — like `[*c]`,
        // a bare `T*`. They index/slice; `.len` is unavailable (a pointer has no length).
        Zig.TyManyPtr p     => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TyManyPtrConst p => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
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
        // [[CType.Slice]].
        Zig.TySlice s      => new CType.Slice(LowerType(s.Arg2)),
        Zig.TySliceConst s => new CType.Slice(LowerType(s.Arg3).WithQuals(TypeQual.Const)),
        // Sentinel-terminated types (Milestone O, part 3 — the C-string shape; V1 sentinel = 0).
        // `[*:0]T` is a NUL-terminated many-item pointer (C's `char*`) → a bare `T*`, like `[*]`;
        // `[:0]T` is a NUL-terminated slice → CType.Slice, like `[]T`. The sentinel is a type-level
        // annotation, not separately enforced (string literals are already NUL-terminated, so a
        // manual `while (p[n] != 0)` scan works); the auto-scan `p[0..]` on a sentinel pointer is
        // a documented cut. Const rides as a TypeQual on the element, same as the non-sentinel forms.
        Zig.TySentPtr p      => new CType.Pointer(LowerType(p.Arg1)),
        Zig.TySentPtrConst p => new CType.Pointer(LowerType(p.Arg2).WithQuals(TypeQual.Const)),
        Zig.TySentSlice s      => new CType.Slice(LowerType(s.Arg1)),
        Zig.TySentSliceConst s => new CType.Slice(LowerType(s.Arg2).WithQuals(TypeQual.Const)),
        // `[N]T` fixed-size array → CType.Array(element, N). N must be an integer literal
        // (a general comptime const-expr size is deferred). A `var b: [N]T` local lowers to a
        // stackalloc'd C array (see DeclOf), so slicing it (`b[lo..hi]`) yields a stack-backed slice.
        Zig.TyArray a => new CType.Array(LowerType(a.Arg3), ConstEvalArraySize(a.Arg1)),
        // `[N:s]T` sentinel-terminated array (Milestone O, part 4; non-zero sentinel in Milestone Z)
        // → CType.Array(element, N) — the LOGICAL length N (so `.len` / slicing exclude the sentinel,
        // like Zig). The extra trailing sentinel slot (N+1 total storage) is materialized only at the
        // local decl site (see DeclOf / IsSentinelArrayType / SentinelArrayValue); the type itself
        // stays an ordinary N-element array, so a `[N:0]u8` buffer is a valid NUL-terminated C string
        // without writing the terminator. A zero sentinel rides C#'s zero-fill; a NON-ZERO sentinel is
        // written into the trailing slot explicitly (the sentinel VALUE isn't carried in the type).
        Zig.TySentArray a => new CType.Array(LowerType(a.Arg5), ConstEvalArraySize(a.Arg1)),
        // Tuple TYPE `struct { T1, T2, … }` (Milestone G) → CType.Tuple → C# System.ValueTuple<…>.
        // Used as a function return type or a var/param annotation; nested tuple types compose.
        Zig.TyTuple t => LowerTupleType(t.Arg2),
        // Function-pointer TYPE `fn (Params) RetType` (Milestone W, part 1a) → a bare CType.Func
        // (the C# backend renders it as a managed `delegate*<P…, Ret>`, the same shape the Zig
        // allocator vtable uses). `*const fn (…) R` / `?*const fn (…) R` reach here as the pointee
        // and are collapsed to the bare Func by PointerTo / LowerOptional. Params are named
        // (`IDENT : Type`); their names are irrelevant to the type, only the types matter.
        Zig.TyFn f       => new CType.Func(LowerType(f.Arg4), LowerFnTypeParams(f.Arg2), Variadic: false),
        Zig.TyFnNoArgs f => new CType.Func(LowerType(f.Arg3), System.Array.Empty<CType>(), Variadic: false),
        // `!T`-returning fn-pointer types: the return is an error union `!T` (like fnDefErr). The
        // Func's Return carries the CType.ErrorUnion, so a bound fn-ptr's result is an ErrUnion<T>.
        Zig.TyFnErr f       => new CType.Func(new CType.ErrorUnion(LowerType(f.Arg5)), LowerFnTypeParams(f.Arg2), Variadic: false),
        Zig.TyFnNoArgsErr f => new CType.Func(new CType.ErrorUnion(LowerType(f.Arg4)), System.Array.Empty<CType>(), Variadic: false),
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
                // A user-constructed custom allocator (Milestone W, part 1b): the vtable struct type
                // and the alignment a vtable function receives. Both → runtime types in ZigAlloc.cs.
                "std.mem.Allocator.VTable" => new CType.Named(VTableTypeName),
                "std.mem.Alignment" => new CType.Named(AlignmentTypeName),
                "std.heap.FixedBufferAllocator" => new CType.Named(FbaTypeName),
                "std.heap.ArenaAllocator" => new CType.Named(ArenaTypeName),
                _ => throw new IrUnsupportedException(
                    $"zig type `{path}` is not modeled (std types: std.mem.Allocator, std.mem.Allocator.VTable, std.mem.Alignment, std.heap.FixedBufferAllocator, std.heap.ArenaAllocator)"),
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
    private CType LowerTypeName(string name)
    {
        if (ResolveSelfAlias(name) is { } alias) { return alias; }
        if (_containerTypes.TryGetValue(name, out var ct)) { return ct; }
        // An error-set name used as a plain VALUE type — `fn f(e: E)`, `var x: E`, a non-`!T`
        // error return `fn g() E` — or the open `anyerror`. Lowers to the flat erased error code
        // (`CType.ErrorSet`, rendered `ushort`): the error VALUE itself, NOT an `E!T` error union
        // (handled separately as `Zig.ErrUnion`). Set membership stays erased at runtime; the
        // declared-set table only drives the compile-time rejection in part 3a. (Milestone X, part 3b.)
        if (name == "anyerror" || _errorSets.Contains(name)) { return CType.ErrorSet; }
        return LowerPrim(name);
    }

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

    /// <summary>Lower a Zig optional payload type: a pointer (or function-pointer) payload stays a
    /// bare nullable pointer (the niche — a `delegate*` / `T*` is null when none); any other payload
    /// is wrapped in <see cref="CType.Optional"/> (→ C# <c>T?</c>).</summary>
    private CType LowerOptional(Item innerType)
    {
        var inner = LowerType(innerType);
        return inner.Unqualified is CType.Pointer or CType.Func ? inner : new CType.Optional(inner);
    }

    /// <summary>Form a pointer to <paramref name="pointee"/>, collapsing a pointer-to-FUNCTION to
    /// the bare <see cref="CType.Func"/> (Milestone W, part 1a). In dotcc's IR a function pointer
    /// is a bare <c>Func</c> rendered as a <c>delegate*&lt;…&gt;</c> (the C-frontend convention), so
    /// <c>*const fn (…) R</c> / <c>*fn (…) R</c> lower to the same <c>Func</c> as a bare <c>fn</c>
    /// type — keeping every downstream call / coercion / sizeof path identical to C's.</summary>
    private static CType PointerTo(CType pointee) =>
        pointee.Unqualified is CType.Func ? pointee : new CType.Pointer(pointee);

    /// <summary>Lower a function-pointer type's parameter list (the reused <c>Params</c>: each a
    /// named <c>IDENT : Type</c>) to its element types — the names are irrelevant to the type. A
    /// variadic marker (<c>...</c>) in a fn-pointer type is rejected (deferred).</summary>
    private IReadOnlyList<CType> LowerParamTypes(Item paramsItem)
    {
        var types = new List<CType>();
        foreach (var p in Flatten(paramsItem))
        {
            if (p.Content is not Zig.Param pm)
            {
                throw new IrUnsupportedException(
                    "a variadic / unnamed parameter in a function-pointer type is not supported yet");
            }
            types.Add(LowerType(pm.Arg2));
        }
        return types;
    }

    /// <summary>Extract the parameter types of a function-pointer TYPE's <c>FnTypeParams</c> list —
    /// each element is either a bare <c>Type</c> (<see cref="Zig.FnTypeParamUnnamed"/>, the common
    /// unnamed form) or <c>IDENT : Type</c> (<see cref="Zig.FnTypeParamNamed"/>, the name ignored —
    /// only the types matter to the Func type).</summary>
    private IReadOnlyList<CType> LowerFnTypeParams(Item paramsItem)
    {
        var types = new List<CType>();
        foreach (var p in Flatten(paramsItem))
        {
            types.Add(p.Content switch
            {
                Zig.FnTypeParamUnnamed u => LowerType(u.Arg0),
                Zig.FnTypeParamNamed n   => LowerType(n.Arg2),
                _ => throw new IrUnsupportedException(
                    "a function-pointer-type parameter must be a `Type` or `IDENT : Type`"),
            });
        }
        return types;
    }

    /// <summary>The compile-time sentinel value of a <c>[N:s]T</c> array type (Milestone Z lifts the
    /// earlier zero-only restriction). V1 requires a literal sentinel; it is materialized into the
    /// trailing storage slot at the decl site (a zero rides C#'s zero-fill, a non-zero is written
    /// explicitly). Returns 0 for a non-sentinel type.</summary>
    private long SentinelArrayValue(Item? typeItem)
    {
        if (typeItem?.Content is not Zig.TySentArray a) { return 0; }
        if (a.Arg3.Content is not Zig.IntLit i || DecodeZigInt(Tok(i.Arg0)).Value is not { } v)
        {
            throw new IrUnsupportedException(
                "a `[N:s]T` sentinel array requires a compile-time integer literal sentinel");
        }
        return v;
    }

    /// <summary>True when a declaration's type annotation is a <c>[N:s]T</c> sentinel array
    /// (Milestone O, part 4). Its storage reserves N+1 elements (the trailing slot is the sentinel),
    /// so a LOCAL decl lays down one extra slot beyond the <c>CType.Array(element, N)</c> logical
    /// length (see <see cref="DeclOf"/>); the symbol's type stays the N-element array.</summary>
    private static bool IsSentinelArrayType(Item? typeItem) => typeItem?.Content is Zig.TySentArray;

    /// <summary>Const-evaluate a <c>[N]T</c> array size. A bare integer literal <c>N</c> takes a
    /// fast path through <see cref="DecodeZigInt"/> (so a radix / underscored size <c>[0x10]u8</c>
    /// is accepted with no symbol context); any other form is lowered and folded by the shared
    /// <see cref="IrBuilder.ConstEval"/> comptime interpreter (Milestone T) — so a computed size
    /// <c>[N * 2]</c> or a container-const size <c>[SIZE]</c> now works. Throws on a non-constant size.</summary>
    private int ConstEvalArraySize(Item sizeExpr)
    {
        if (sizeExpr.Content is Zig.IntLit i)
        {
            return (int)(DecodeZigInt(Tok(i.Arg0)).Value
                ?? throw new IrUnsupportedException("a `[N]T` array size literal is too large"));
        }
        return _ir.ConstEval(LowerExpr(sizeExpr)) is { } n
            ? (int)n
            : throw new IrUnsupportedException("a `[N]T` array size must be a constant integer expression");
    }

    /// <summary>Decode a Zig integer literal — decimal, <c>0x</c>/<c>0o</c>/<c>0b</c> radix, with
    /// <c>_</c> digit separators (UNLIKE C's bare-<c>0</c> octal and <c>'</c> separator) — to a
    /// <see cref="LitInt"/>. The numeric core is normalized to decimal (the backend re-spells it +
    /// adds a type suffix); the signed-long <c>Value</c> is set when it fits (drives const-folding,
    /// left null past <c>long.MaxValue</c>); the carrier type is the narrowest of int/uint/long/ulong
    /// that holds the magnitude. (A Zig <c>comptime_int</c> has no fixed type — at a typed sink
    /// <see cref="LowerExprSink"/> casts it; the literal just needs a representable carrier.)</summary>
    private static LitInt DecodeZigInt(string raw)
    {
        var t = raw.Replace("_", "");
        var inv = CultureInfo.InvariantCulture;
        ulong mag = 0; bool magOk; long? val = null;
        string body; int radix;
        if (t.Length >= 2 && t[0] == '0' && t[1] is 'x' or 'X')
        {
            body = t[2..]; radix = 16;
            magOk = ulong.TryParse(body, NumberStyles.HexNumber, inv, out mag);
            if (long.TryParse(body, NumberStyles.HexNumber, inv, out var hv)) { val = hv; }
        }
        else if (t.Length >= 2 && t[0] == '0' && t[1] is 'o' or 'O')
        {
            body = t[2..]; radix = 8;
            try { mag = System.Convert.ToUInt64(body, 8); magOk = true; } catch { magOk = false; }
            if (magOk && mag <= long.MaxValue) { val = (long)mag; }
        }
        else if (t.Length >= 2 && t[0] == '0' && t[1] is 'b' or 'B')
        {
            body = t[2..]; radix = 2;
            try { mag = System.Convert.ToUInt64(body, 2); magOk = true; } catch { magOk = false; }
            if (magOk && mag <= long.MaxValue) { val = (long)mag; }
        }
        else
        {
            body = t; radix = 10;
            magOk = ulong.TryParse(t, NumberStyles.None, inv, out mag);
            if (long.TryParse(t, inv, out var dv)) { val = dv; }
        }
        if (magOk)
        {
            // The literal's decimal CORE — radix/underscores are gone; the backend re-adds a suffix
            // from the type below. (A non-decimal radix would also be valid C#, but decimal is uniform.)
            var core = mag.ToString(inv);
            var type = mag <= int.MaxValue ? CType.Int
                : mag <= uint.MaxValue ? CType.UInt
                : mag <= long.MaxValue ? CType.Long
                : CType.ULong;
            return new LitInt(core, val) { Type = type };
        }
        // Magnitude exceeds ulong: carry it as a 128-bit literal when it fits u128 (Value stays null —
        // it can't fit a long; a typed i128/u128 sink casts the carrier). Beyond u128 a literal is out
        // of scope, so keep the legacy ulong-ish carrier (any downstream use rejects it).
        if (TryParseRadix128(body, radix, out var mag128))
        {
            return new LitInt(mag128.ToString(inv), null) { Type = CType.UInt128 };
        }
        return new LitInt(t, null) { Type = CType.Long };
    }

    /// <summary>Parse a (radix-stripped) integer body into a <see cref="System.UInt128"/>, with an
    /// exact overflow guard — the >64-bit-literal path for <c>i128</c>/<c>u128</c>. Radix-uniform
    /// (10/16/8/2), since the BCL only parses <c>UInt128</c> in decimal/hex; returns false on a bad
    /// digit, an empty body, or a magnitude past <see cref="System.UInt128.MaxValue"/>.</summary>
    private static bool TryParseRadix128(string body, int radix, out System.UInt128 result)
    {
        result = 0;
        if (body.Length == 0) { return false; }
        var r = (System.UInt128)radix;
        var max = System.UInt128.MaxValue;
        System.UInt128 acc = 0;
        foreach (var ch in body)
        {
            int d = ch is >= '0' and <= '9' ? ch - '0'
                  : ch is >= 'a' and <= 'f' ? ch - 'a' + 10
                  : ch is >= 'A' and <= 'F' ? ch - 'A' + 10
                  : -1;
            if (d < 0 || d >= radix) { return false; }
            if (acc > (max - (System.UInt128)d) / r) { return false; }  // would overflow u128
            acc = acc * r + (System.UInt128)d;
        }
        result = acc;
        return true;
    }

    /// <summary>Lower a Zig float literal: strip <c>_</c> separators, and convert a hex float
    /// (<c>0x1.8p3</c>, no C# syntax) to a round-trippable decimal via the shared
    /// <see cref="EmitHelpers.LowerHexFloat"/>. A decimal float passes through (C# accepts it
    /// verbatim, typed <c>double</c> here). Zig has no <c>f</c>/<c>l</c> literal suffix.</summary>
    private static string LowerZigFloat(string raw)
    {
        var t = raw.Replace("_", "");
        return t.Length > 2 && t[0] == '0' && t[1] is 'x' or 'X' ? EmitHelpers.LowerHexFloat(t) : t;
    }

    /// <summary>Expand Zig's <c>\u{NNNN}</c> unicode escapes in a quoted string lexeme to the
    /// equivalent <c>\xNN</c> UTF-8 byte escapes, so the SHARED string decoder (which has no
    /// <c>\u{…}</c> arm) handles them unchanged. Every OTHER escape (incl. a literal <c>\\</c>)
    /// is copied verbatim, so a <c>\\u{</c> (escaped backslash then a <c>u{</c>) is not mistaken
    /// for a unicode escape. The input/output keep the surrounding quotes.</summary>
    private static string ExpandZigUnicodeEscapes(string quoted)
    {
        if (!quoted.Contains("\\u{", System.StringComparison.Ordinal)) { return quoted; }
        var sb = new System.Text.StringBuilder(quoted.Length);
        var i = 0;
        while (i < quoted.Length)
        {
            if (quoted[i] == '\\' && i + 2 < quoted.Length && quoted[i + 1] == 'u' && quoted[i + 2] == '{')
            {
                var close = quoted.IndexOf('}', i + 3);
                if (close < 0) { throw new IrUnsupportedException("unterminated `\\u{…}` escape in string literal"); }
                var cp = System.Convert.ToInt32(quoted[(i + 3)..close].Replace("_", ""), 16);
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)))
                {
                    sb.Append("\\x").Append(b.ToString("X2"));
                }
                i = close + 1;
            }
            else if (quoted[i] == '\\' && i + 1 < quoted.Length)
            {
                sb.Append(quoted[i]).Append(quoted[i + 1]);   // keep any other escape (incl. `\\`) intact
                i += 2;
            }
            else { sb.Append(quoted[i]); i++; }
        }
        return sb.ToString();
    }

    /// <summary>Fold a Zig multiline string token (a run of <c>\\</c>-prefixed lines) into a single
    /// QUOTED lexeme whose decoded content is the raw concatenation joined by <c>\n</c>. Zig
    /// multiline strings process NO escapes, so each line's content after its <c>\\</c> prefix is
    /// taken verbatim — then re-escaped for the shared C-string decoder (only <c>"</c>/<c>\\</c>/
    /// control chars need escaping; printable + UTF-8 source chars pass through and the decoder
    /// UTF-8-encodes them).</summary>
    private static string FoldZigMultilineString(string token)
    {
        var lines = token.Replace("\r", "").Split('\n');
        var parts = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = raw.TrimStart(' ', '\t');
            if (line.StartsWith("\\\\", System.StringComparison.Ordinal)) { parts.Add(line[2..]); }
        }
        var content = string.Join("\n", parts);
        var sb = new System.Text.StringBuilder(content.Length + 2);
        sb.Append('"');
        foreach (var c in content)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) { sb.Append("\\x").Append(((int)c).ToString("X2")); }
                    else { sb.Append(c); }   // printable ASCII + non-ASCII (UTF-8-encoded by the decoder)
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

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
        // `anyopaque` (Milestone W, part 1a) — Zig's opaque type, used only behind a pointer
        // (`*anyopaque` / `?*anyopaque`) as a type-erased context. Maps to C's `void`, so `*anyopaque`
        // → `void*` and `?*anyopaque` → a nullable `void*` (the pointer niche), exactly like C.
        "anyopaque" => CType.Void,
        "bool" => CType.Bool,
        "i8"  => CType.SChar,    // → C# sbyte
        "u8"  => CType.UChar,    // → C# byte
        "i16" => CType.Short,
        "u16" => CType.UShort,
        "i32" => CType.Int,
        "u32" => CType.UInt,
        "i64" => CType.Long,
        "u64" => CType.ULong,
        "i128" => CType.Int128,  // → C# System.Int128
        "u128" => CType.UInt128, // → C# System.UInt128
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
                case Zig.FnTypeParamsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [FnTypeParam, ',', FnTypeParams] (right-recursive)
                case Zig.FnTypeParamsOne o:  stack.Push(o.Arg0); break;
                case Zig.DestructBindsCons c: stack.Push(c.Arg2); stack.Push(c.Arg0); break;  // [DestructBinds, ',', DestructBind]
                case Zig.DestructBindsOne o:  stack.Push(o.Arg0); break;
                default: ordered.Add(n); break;
            }
        }
        return ordered;
    }
}
