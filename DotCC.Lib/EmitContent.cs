#nullable enable

using System;
using System.Collections.Generic;

namespace DotCC;

/// <summary>
/// Discriminated-union result type for <see cref="CSharpEmitter"/>'s
/// <c>IVisitor</c> implementation. Most visits produce a C# code snippet
/// (<see cref="Text"/>); the handful that produce structured intermediate
/// data — declaration-specifier multisets, call-argument lists, struct
/// field-name lists — produce dedicated variants so consumers can
/// pattern-match instead of parsing sentinel-separated strings.
/// </summary>
/// <remarks>
/// Previously the visitor was <c>IVisitor&lt;string&gt;</c> and structured
/// data round-tripped through encoded strings: <c>ArgSep</c> (U+0001) for
/// argument lists, <c>EnumSep</c> for enum items, and bracketed
/// <c>&lt;keyword&gt;</c> markers for type-specifier accumulation. That
/// worked but every consumer had to remember to split-and-decode at the
/// right place. With this type, the producer's variant is the schema —
/// callers can't accidentally treat an arg list as a code snippet (the
/// cast throws).
/// </remarks>
public abstract record EmitContent
{
    /// <summary>
    /// Implicit lift from raw <see cref="string"/> so existing visit methods
    /// can keep returning <c>"..."</c> / <c>$"..."</c> verbatim without
    /// wrapping. The result is an <see cref="Text"/> variant.
    /// </summary>
    public static implicit operator EmitContent(string s) => new Text(s);

    /// <summary>
    /// A statement that is a DECLARATION (<c>Decl ;</c>). Renders to its
    /// <see cref="Value"/> like any text (via <c>T()</c>), but the marker lets
    /// the enclosing <c>StmtList</c>/<c>Block</c> reductions tell declarations
    /// from statements — needed only by the C90 mixed-declarations dialect gate
    /// (a declaration that follows a non-declaration in the same block). When
    /// gating is off the marker is simply rendered as text and ignored.
    /// </summary>
    public sealed record DeclStmtMarker(string Value) : EmitContent;

    /// <summary>Plain C# source code text — by far the most common variant.</summary>
    /// <param name="EnumType">Non-null when this expression lowered to a value
    /// of a C# <c>enum</c> type (the enum's name). Carried so a consuming node
    /// can insert the int↔enum casts C# requires but C doesn't: an enum operand
    /// of an arithmetic/bitwise/relational op decays to <c>(int)</c>, and a
    /// non-enum value stored into an enum-typed slot is wrapped <c>(Enum)</c>.
    /// Only set by expression nodes that produce an enum value (enum var read,
    /// enumerator ref, and the transparent wrappers that propagate it).</param>
    /// <param name="Ty">The expression's synthesized <see cref="CType"/>, or
    /// null. Propagated up by expression visitors so a consumer (today
    /// <c>sizeof expr</c>) can compute a size — see <see cref="CType"/>. Distinct
    /// from <paramref name="EnumType"/>, which drives _Bool/enum coercion; this
    /// carries the full size shape (incl. array element + count).</param>
    /// <param name="Inline">True when this is a <c>Type</c> result whose specifier
    /// run included the C99 <c>inline</c> function specifier. Read by the
    /// <c>FnSig</c> visitors so the emitted method gets a
    /// <c>[MethodImpl(MethodImplOptions.AggressiveInlining)]</c>. Dropped from the
    /// resolved type itself (C# has no per-type inline keyword).</param>
    /// <param name="Noreturn">True when the specifier run included the C11
    /// <c>_Noreturn</c> function specifier — the emitted method gets a
    /// <c>[DoesNotReturn]</c>. Same flow as <paramref name="Inline"/>.</param>
    /// <param name="Volatile">True when this is a <c>Type</c> result that was
    /// <c>volatile</c>-qualified (the leading <c>Type → 'volatile' Type</c>
    /// prefix). The qualifier is dropped from <paramref name="Value"/> (the C#
    /// type string), but the flag lets the decl/member visitors record the
    /// declared name/field as volatile so its accesses lower to
    /// <c>Volatile.Read</c>/<c>Volatile.Write</c>.</param>
    /// <param name="VolatileLValue">Non-null when this EXPRESSION is a volatile
    /// lvalue: <paramref name="Value"/> already holds the read form
    /// (<c>Volatile.Read(ref X)</c>) and this carries the bare lvalue text
    /// <c>X</c>. A write-context parent (assignment, compound-assign, <c>++</c>/
    /// <c>--</c>, <c>&amp;</c>) reads it to emit the write form
    /// (<c>Volatile.Write(ref X, …)</c>) / the bare address instead of the read.</param>
    /// <param name="VolatilePointee">True for a POINTER whose pointee is volatile
    /// (<c>volatile int *p</c>). Set on the <c>Type</c> result (by <c>typePtr</c>
    /// when the pointee Type was volatile) AND carried on a pointer-valued
    /// expression (a read of such a pointer) — so a dereference / subscript of it
    /// (<c>*p</c>, <c>p[i]</c>) yields a volatile lvalue and fences. Phase V2.</param>
    /// <param name="Atomic">True when this is a <c>Type</c> result that was
    /// <c>_Atomic</c>-qualified (C11). Like <paramref name="Volatile"/> but the
    /// access lowers to seq-cst <c>Atomic.*</c> (Interlocked-backed) calls. The
    /// decl/member visitors record the name/field so reads/writes lower accordingly.</param>
    /// <param name="AtomicLValue">Non-null when this EXPRESSION is an atomic lvalue:
    /// <paramref name="Value"/> already holds the seq-cst read (<c>Atomic.Load(ref X)</c>)
    /// and this carries the bare lvalue <c>X</c>. A write-context parent emits the
    /// matching <c>Atomic.Store</c>/<c>Atomic.AddFetch</c>/… instead of the read.</param>
    /// <param name="ConstInt">The expression's folded compile-time integer value, when
    /// known — a literal, a <c>sizeof</c>, or arithmetic over them, propagated through
    /// parentheses. Lets a consumer that needs an integer-constant-expression (e.g. an
    /// array-member bound <c>T a[sizeof(void*)]</c>, which lowers to a C#
    /// <c>fixed[N]</c>/<c>[InlineArray(N)]</c> requiring a literal) fold it. Null when
    /// not a constant dotcc can fold.</param>
    /// <param name="CommaOps">When this Text is a PARENTHESIZED comma expression
    /// (<c>(a, b, c)</c>), <see cref="Value"/> is the value-context tuple form
    /// (<c>(a, b, c).Item3</c>) but this carries the raw operand list so a DISCARD
    /// context (a statement, a <c>(void)</c> cast, or a controlling expression that
    /// only tests the last operand) can re-emit the operands as statements — where
    /// a <c>void</c> side-effect operand is legal but a tuple element isn't. Null
    /// for a non-comma expression.</param>
    /// <param name="VoidCast">True when this Text is a <c>(void)X</c> discard cast
    /// (C# has no void value). <see cref="CommaOps"/> already carries the discard
    /// statement(s); this flag lets a ternary branch recognise a void arm so the
    /// whole <c>?:</c> can be lowered to an <c>if</c>/<c>else</c> statement (a void
    /// call / cast can't be a C# ternary arm). See <see cref="VoidCond"/>.</param>
    public sealed record Text(string Value, string? EnumType = null, CType? Ty = null, bool Inline = false, bool Noreturn = false, bool Volatile = false, string? VolatileLValue = null, bool VolatilePointee = false, bool Atomic = false, string? AtomicLValue = null, int? ConstInt = null, IReadOnlyList<string>? CommaOps = null, bool VoidCast = false) : EmitContent;

    /// <summary>
    /// A VOID-TYPED conditional expression — a C ternary <c>c ? a : b</c> whose
    /// branches are void (a <c>(void)X</c> discard cast or a void-returning call),
    /// e.g. Lua's GC write-barrier macros <c>(cond ? luaC_barrier_(…) : cast_void(0))</c>
    /// used as a statement. C# can't express such a <c>?:</c> as an expression (a
    /// void call/cast isn't a valid ternary arm), so it has no value — only the
    /// <see cref="IfStatement"/> form (an <c>if/else</c>, recursing for nested void
    /// ternaries). A statement / loop-body context renders <see cref="IfStatement"/>;
    /// the kept <see cref="Value"/> (the invalid ternary text) is never legitimately
    /// value-used in C, so any context that did read it trips Roslyn loudly.
    /// </summary>
    public sealed record VoidCond(string Value, string IfStatement) : EmitContent;

    /// <summary>
    /// A value-context comma whose leading operand(s) can't be C# tuple elements —
    /// a VOID operand (a void <see cref="VoidCond"/> guard ternary), e.g. Lua's
    /// `luaM_newvectorchecked` = `(luaM_checksize(…), luaM_newvector(…))` assigned
    /// to a pointer. C# can't sequence a void side-effect before a value in
    /// expression position, so this carries the leading operands as STATEMENTS to
    /// hoist plus the comma's <see cref="ValueExpr"/>. A statement-level sink
    /// (assignment / decl-init / return / expression-statement) folds itself into
    /// the value and emits <c>{ leadingStmts; sink(valueExpr); }</c>; a non-hoisting
    /// value consumer (a function argument, etc.) can't lift statements, so
    /// <c>T()</c> fails loudly rather than miscompile.
    /// </summary>
    public sealed record SeqExpr(IReadOnlyList<string> LeadingStmts, string ValueExpr, CType? Ty = null) : EmitContent;

    /// <summary>
    /// Accumulator for declaration-specifier sequences (<c>int</c>,
    /// <c>unsigned</c>, <c>long</c>, etc.). <see cref="ResolveTypeSpec"/>
    /// turns this into a final <see cref="Text"/> with the resolved C#
    /// type name after validating the combination.
    /// </summary>
    public sealed record SpecList(IReadOnlyList<string> Specs) : EmitContent;

    /// <summary>
    /// Comma-separated argument list — produced by <c>ArgsCons</c> /
    /// <c>ArgsOne</c>, consumed by <c>Call</c> / <c>CallNoArgs</c> /
    /// <c>DeclArrInit</c> for printf specialization and array initializer
    /// lowering. Each element is the already-emitted C# code for one arg.
    /// </summary>
    /// <param name="Values">The already-emitted C# code for each argument.</param>
    /// <param name="SoleArg">When the list has exactly one element, the raw
    /// <see cref="EmitContent"/> that element reduced to (before it was
    /// rendered into <paramref name="Values"/>). Lets a single-argument call
    /// inspect its argument structurally — used by <c>Visit(Call)</c> to spot
    /// <c>malloc(sizeof(T))</c> (the sole arg is a <see cref="SizeofType"/>)
    /// without re-parsing the emitted string. Null for multi-arg lists.</param>
    /// <param name="ArgEnums">Per-argument enum type (aligned with
    /// <paramref name="Values"/>), or null entry for a non-enum argument. Lets
    /// the printf-family fluent lowering decay an enum argument to <c>(int)</c>
    /// (a C enum is an int in a varargs `%d` slot, but C#'s <c>.Arg(int)</c>
    /// won't bind an enum). Null when no argument was enum-typed.</param>
    /// <param name="ArgTypes">Per-argument synthesized <see cref="CType"/> (aligned
    /// with <paramref name="Values"/>), or null entry when unknown. Lets a call that
    /// needs an argument's type without re-deriving it — e.g. the <c>&lt;stdatomic.h&gt;</c>
    /// lowering reads <c>atomic_store(&amp;x, v)</c>'s first-arg pointer type to cast
    /// <c>v</c> to the pointee. Null when no argument carried a type.</param>
    public sealed record Args(IReadOnlyList<string> Values, EmitContent? SoleArg = null, IReadOnlyList<string?>? ArgEnums = null, IReadOnlyList<CType?>? ArgTypes = null) : EmitContent;

    /// <summary>
    /// AST marker for <c>sizeof(T)</c> over a named type. Produced by
    /// <c>Visit(SizeofType)</c>; rendered back to <c>sizeof(T)</c> by <c>T()</c>
    /// in every ordinary context. Its only structural consumer is the
    /// single-arg <c>malloc</c> recognition in <c>Visit(Call)</c>, which reads
    /// the type name to seed a <see cref="MallocSizeof"/> marker.
    /// </summary>
    public sealed record SizeofType(string TypeName) : EmitContent;

    /// <summary>
    /// AST marker for a <c>malloc(sizeof(S))</c> allocation (optionally wrapped
    /// in a matching <c>(S*)</c> cast — <c>Visit(Cast)</c> propagates the marker
    /// through). Produced by <c>Visit(Call)</c>; consumed structurally by
    /// <c>Visit(DeclItemInit)</c> to register a stack-promotion candidate. In
    /// any other context it renders, via <c>T()</c>, to <see cref="LowLevelText"/>
    /// — the exact low-level allocation expression (including the cast) — so
    /// non-promoted uses are byte-identical to the un-analysed emit.
    /// </summary>
    /// <param name="StructType">The C# struct type name <c>S</c> (from the
    /// <c>sizeof</c>), used to verify the declared pointer type matches and to
    /// emit the promoted <c>S p = new S();</c> form.</param>
    /// <param name="LowLevelText">The verbatim low-level allocation expression
    /// for the non-promoted path.</param>
    public sealed record MallocSizeof(string StructType, string LowLevelText, bool AlreadyCast = false) : EmitContent;

    /// <summary>
    /// Enumerator items in an <c>enum</c> body — each entry is either
    /// just the name (<c>Item</c>) or <c>"Name=Expr"</c> when the user
    /// provided an explicit value. <c>EnumDef</c> resolves auto-numbering
    /// across the list at the end.
    /// </summary>
    public sealed record EnumItems(IReadOnlyList<string> Items) : EmitContent;

    /// <summary>
    /// Designated-initializer items (<c>.field = E</c>). Each element is
    /// <c>"field = expr"</c>; <c>DeclStructDesignated</c> joins with
    /// commas inside <c>new T { … }</c>.
    /// </summary>
    public sealed record InitMembers(IReadOnlyList<string> Members) : EmitContent;

    /// <summary>
    /// Function-signature breakdown produced by an <c>FnSig</c> reduction.
    /// Because <c>FnSig</c> reduces BEFORE the enclosing <c>Block</c>
    /// (LALR bottom-up: the left child of <c>Fn → FnSig Block</c> is fully
    /// reduced first), the <c>FnSig</c> action sets the visitor's
    /// "current function" state in time for any <c>__func__</c> reference
    /// inside the body to resolve directly — no placeholder
    /// string-substitution dance. The <c>FuncDef</c> action consumes this
    /// to add the function to the exports list (when non-static) and to
    /// concatenate header + body.
    /// </summary>
    public sealed record FnHeader(
        string Type,
        string Name,
        string Params,
        bool IsStatic,
        bool IsInline = false,
        bool IsNoreturn = false) : EmitContent;

    /// <summary>
    /// AST marker for a <c>setjmp(env)</c> call. Emitted by
    /// <c>Visit(Call)</c> when it recognises the callee as
    /// <c>setjmp</c>; consumed by either <c>Visit(Equ)</c> (when the
    /// setjmp is compared against 0) or directly by
    /// <c>Visit(StmtIfElse)</c> (when the setjmp is the bare condition).
    /// Any other context raises a <c>CompileException</c> via the
    /// <c>T()</c> accessor — setjmp is a non-local-jump magic primitive
    /// and only the documented if/else shapes get the try/catch rewrite.
    /// Carries the env identifier name so the catch filter
    /// (<c>when (__jmp.Token == env)</c>) can reference it directly.
    /// </summary>
    public sealed record SetjmpCall(string EnvName) : EmitContent;

    /// <summary>
    /// AST marker for a <c>setjmp(env) == 0</c> (or <c>0 == setjmp(env)</c>,
    /// or the corresponding <c>!=</c> shape) comparison. Produced by
    /// <c>Visit(Equ)</c> when one operand is <see cref="SetjmpCall"/>
    /// and the other is the literal <c>0</c>; consumed by
    /// <c>Visit(StmtIfElse)</c> to emit the try/catch rewrite. The
    /// <see cref="TruthyOnFirstCall"/> flag tells the consumer which
    /// branch is "normal" and which is "recovery" — <c>== 0</c> is
    /// truthy on the initial call (so the then-branch is normal),
    /// while <c>!= 0</c> flips it.
    /// </summary>
    public sealed record SetjmpCheckZero(string EnvName, bool TruthyOnFirstCall) : EmitContent;

    /// <summary>
    /// One init-declarator inside a declaration's <c>init-declarator-list</c>
    /// (C99 §6.7). <see cref="Init"/> is <c>null</c> for the bare-name form
    /// (<c>int x</c>) and the already-emitted C# expression text for the
    /// initialised form (<c>int x = 5</c>). Consumed by <c>Visit(Decl)</c>
    /// (block-scope, joins with commas into a single C# declaration) and by
    /// <c>Visit(GlobalDeclList)</c> (file-scope, emits one
    /// <c>public static unsafe</c> field per entry into <c>DotCcGlobals</c>).
    /// Carrying the (name, init?) pair structurally instead of pre-joining
    /// lets the file-scope path apply per-entry policy — e.g. auto-init
    /// reference types like <c>LongJmpToken</c> with <c>new T()</c> instead
    /// of letting them default to <c>null</c>.
    /// </summary>
    /// <param name="MallocStructType">Non-null when <see cref="Init"/> is a
    /// <c>malloc(sizeof(S))</c> allocation (see <see cref="MallocSizeof"/>):
    /// the struct type <c>S</c>. Lets <c>Visit(Decl)</c> emit the promoted
    /// stack form <c>S name = new S();</c> when the variable qualifies, while
    /// still carrying the low-level allocation in <see cref="Init"/> for the
    /// fallback.</param>
    /// <param name="InitEnumType">The C# enum type the initializer expression
    /// produced, or null. Carried so <c>Visit(Decl)</c> / <c>EmitGlobalFields</c>
    /// can insert the int↔enum cast C# needs when the declared type and the
    /// initializer disagree (`Color c = 2` → <c>(Color)2</c>; `int x = c` →
    /// <c>(int)c</c>) — the enum tag is lost once the init is rendered to text.</param>
    /// <param name="Stars">Extra pointer levels this declarator adds ON TOP of
    /// the shared base type — for a per-declarator pointer in a multi-declarator
    /// list (`int *a, *b;` → the second entry has Stars=1). Only the SUBSEQUENT
    /// (post-comma) declarators carry stars; the first declarator's stars were
    /// absorbed into the declaration's Type by the greedy `Type → Type *` rule,
    /// so its Stars is 0 and it uses Type verbatim. The emitter computes each
    /// declarator's type as (first ? Type : stripStars(Type) + Stars*'*').</param>
    public sealed record DeclEntry(string Name, string? Init, string? MallocStructType = null, string? InitEnumType = null, bool InitIsVoidPtr = false, int Stars = 0);

    /// <summary>
    /// Accumulator for an init-declarator list. <c>declItem</c>/<c>declItemInit</c>
    /// produce a single-entry list; <c>declItemListCons</c> concatenates.
    /// </summary>
    public sealed record DeclEntries(IReadOnlyList<DeclEntry> Entries) : EmitContent;

    /// <summary>
    /// A C comma-operator chain (<c>a, b, c</c>) — the operand texts in
    /// source order. Produced by <c>Visit(C.CommaOp)</c> off the full-expression
    /// <c>Expr</c> tier, which only reaches two consumers: <c>Visit(C.Paren)</c>
    /// (value context — lowers to a tuple <c>(a, b, c).Item3</c>, since C#'s
    /// tuple evaluates elements left-to-right and C# has no comma operator) and
    /// <c>Visit(C.StmtExpr)</c> (statement context — the result is discarded, so
    /// each operand becomes its own statement). Because <c>Expr</c> is used
    /// nowhere else, this variant can't escape to a <c>T()</c> that expects code.
    /// <para><c>LastType</c> carries the synthesized CType of the final operand
    /// (the value of the whole comma expression in C), so a value-context tuple
    /// can be typed — e.g. <c>sizeof((a, p->field)[0])</c> resolves.</para>
    /// <para><c>OperandTypes</c> carries each operand's CType (parallel to
    /// <c>Operands</c>, may hold nulls / be null) so the value-tuple lowering can
    /// cast a POINTER operand to <c>nint</c> — a pointer can't be a <c>ValueTuple</c>
    /// type argument (CS0306) — and cast the result back when the last operand is a
    /// pointer. Lua's <c>check_exp(c,e)</c> = <c>(lua_assert(c), e)</c> where
    /// <c>e</c> is a pointer is the motivating case.</para>
    /// </summary>
    public sealed record CommaSeq(IReadOnlyList<string> Operands, CType? LastType = null, IReadOnlyList<CType?>? OperandTypes = null) : EmitContent;

    /// <summary>
    /// A run of adjacent string literals (C concatenates them: <c>"a" "b"</c>
    /// is <c>"ab"</c>). Carries each segment's inner body (escapes intact, no
    /// surrounding quotes). Produced by <c>strSeqOne</c>/<c>strSeqCons</c> and
    /// consumed by <c>Visit(C.Str)</c>, which decodes each segment's C escapes
    /// independently and concatenates the resulting bytes — so a <c>\x…</c>
    /// escape never greedily consumes the next segment's leading hex digit.
    /// </summary>
    public sealed record StrParts(IReadOnlyList<string> Bodies) : EmitContent;

    /// <summary>
    /// A brace-initializer element — recursive, so nested aggregate initializers
    /// (<c>{{1,2},{3,4}}</c> for a 2-D array or a struct array) keep their group
    /// structure. A <see cref="InitLeaf"/> is a scalar expression; a
    /// <see cref="InitGroup"/> is a <c>{ … }</c> brace group (and the whole
    /// <c>InitList</c> reduces to one). The decl visitors interpret the tree
    /// against the target type: a scalar multi-dim array flattens it with C's
    /// per-dimension zero-fill; a struct array maps each group to <c>new T{…}</c>.
    /// </summary>
    public abstract record InitNode : EmitContent;
    public sealed record InitLeaf(string Value) : InitNode;
    public sealed record InitGroup(IReadOnlyList<InitNode> Items) : InitNode;
    /// <summary>A C99 array designator element <c>[Index] = Value</c> inside an
    /// array initializer. <paramref name="Index"/> is the designator expression
    /// text (must resolve to a constant int); <paramref name="Value"/> is the
    /// scalar value text. The decl visitor places it at that index in a dense,
    /// zero-filled array.</summary>
    public sealed record InitDesignated(string Index, string Value) : InitNode;
}
