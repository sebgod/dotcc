#nullable enable

using System.Collections.Generic;

namespace DotCC.Ir;

// ---- operators ----------------------------------------------------------

/// <summary>Unary operators. <see cref="CodeGen"/> maps each to its C# form.</summary>
public enum UnOp { Plus, Neg, BitNot, LogNot, PreInc, PreDec, PostInc, PostDec, AddrOf, Deref }

/// <summary>Binary operators (also reused as compound-assignment operators —
/// <c>+=</c> is <see cref="BinOp.Add"/>).</summary>
public enum BinOp
{
    Add, Sub, Mul, Div, Mod,
    Shl, Shr, BitAnd, BitOr, BitXor,
    Lt, Gt, Le, Ge, Eq, Ne,
    LogAnd, LogOr,
}

// ---- expressions --------------------------------------------------------

/// <summary>Base of the typed expression IR. Every expression carries its
/// synthesized <see cref="Type"/> (the half-built "type-synthesis layer" of the
/// legacy emitter, now first-class) and a source <see cref="Pos"/>.</summary>
public abstract record CExpr
{
    public required CType Type { get; init; }
    public SrcPos Pos { get; init; }
    /// <summary>True for an assignable lvalue (drives <c>&amp;</c>, assignment targets).</summary>
    public bool IsLValue { get; init; }
}

/// <summary>An integer constant. <see cref="CsText"/> is the already-lowered C#
/// literal (octal converted, suffix normalised); <see cref="Value"/> is the
/// folded value when it fits a long (for const-expression contexts).</summary>
public sealed record LitInt(string CsText, long? Value) : CExpr;

/// <summary>A floating constant, lowered to its C# literal text.</summary>
public sealed record LitFloat(string CsText) : CExpr;

/// <summary>A string literal — the raw adjacent quoted C segments (e.g.
/// <c>["\"a\\n\"", "\"b\""]</c>), NOT yet encoded. The backend decodes the C
/// escapes and emits its own representation (the C# backend: <c>Libc.L("…"u8)</c>
/// or a byte-array); keeping the IR free of target text is what lets a different
/// backend lower the same literal differently.</summary>
public sealed record LitStr(IReadOnlyList<string> Segments) : CExpr;

/// <summary>A reference to a resolved variable / parameter / function.</summary>
public sealed record VarRef(Symbol Sym) : CExpr;

/// <summary>A reference to an enumerator of a real (named) C# enum — renders as
/// <c>EnumName.Member</c>. <see cref="Symbol.ConstValue"/> carries the integer
/// value for constant-expression contexts (array bounds, case labels). An
/// enumerator of an anonymous, un-typedef'd enum has no enum type, so the builder
/// emits a plain <see cref="LitInt"/> for it instead.</summary>
public sealed record EnumConstRef(Symbol Sym) : CExpr;

/// <summary>A unary operation.</summary>
public sealed record Unary(UnOp Op, CExpr Operand) : CExpr;

/// <summary>A binary operation.</summary>
public sealed record Binary(BinOp Op, CExpr Left, CExpr Right) : CExpr;

/// <summary>An assignment. <see cref="CompoundOp"/> is null for a plain
/// <c>=</c>, or the arithmetic/bitwise op for a compound assignment (<c>+=</c>).</summary>
public sealed record Assign(BinOp? CompoundOp, CExpr Target, CExpr Value) : CExpr;

/// <summary>A function call by name (a named function or libc builtin).
/// <see cref="ParamTypes"/> is the resolved callee's fixed-parameter types (null
/// when the callee has no known signature), used by the backend to coerce each
/// argument to its parameter type — C's implicit conversion at a call, which C#
/// requires made explicit. How a particular libc name renders (e.g. printf's
/// fluent form) is the backend's decision, keyed off <see cref="Callee"/>.</summary>
public sealed record Call(string Callee, IReadOnlyList<CExpr> Args,
    IReadOnlyList<CType>? ParamTypes = null) : CExpr;

/// <summary>A call through a computed function-pointer expression — <c>(*fp)(x)</c>,
/// <c>tbl[i](x)</c>, <c>s.fn(x)</c>. (A call of a named function or fn-ptr
/// variable uses <see cref="Call"/> instead.)</summary>
public sealed record IndirectCall(CExpr Callee, IReadOnlyList<CExpr> Args) : CExpr;

/// <summary>A cast (explicit or inserted by a coercion pass).</summary>
public sealed record Cast(CType Target, CExpr Operand) : CExpr;

/// <summary>A conditional (ternary) expression <c>c ? a : b</c>. Codegen wraps
/// the condition in <c>Cond.B(...)</c> for C-truthy semantics.</summary>
public sealed record CondExpr(CExpr Cond, CExpr Then, CExpr Else) : CExpr;

/// <summary>An array subscript <c>base[index]</c> — an lvalue.</summary>
public sealed record Index(CExpr Base, CExpr Idx) : CExpr;

/// <summary>A struct/union member access — <c>base.field</c> (<see cref="Arrow"/>
/// false) or <c>base-&gt;field</c> (true). Both forms are legal C# in the unsafe
/// context user code lives in, so codegen emits the operator verbatim.</summary>
public sealed record Member(CExpr Base, string Field, bool Arrow) : CExpr;

/// <summary>A comma-separated expression sequence used in <c>for</c>-init /
/// <c>for</c>-update position (<c>i = 0, j = n</c>) — C# accepts the same list
/// there.</summary>
public sealed record CommaSeq(IReadOnlyList<CExpr> Items) : CExpr;

/// <summary>The C comma operator <c>e1, e2, …, eN</c> in value context: every
/// operand is evaluated left-to-right and all but the last are discarded; the
/// value and type are the last operand's. C# has no comma operator, so codegen
/// picks a lowering by position: a statement-level comma becomes one statement
/// per operand; a value-context comma becomes a left-to-right ValueTuple whose
/// <c>.ItemN</c> picks the last (or, when a leading operand is <c>void</c> — a
/// void call or a <c>(void)x</c> discard — an immediately-invoked delegate, the
/// only form that keeps the side effects lazy inside a short-circuit).</summary>
public sealed record CommaOp(IReadOnlyList<CExpr> Items) : CExpr;

/// <summary><c>sizeof</c> — of a type (<c>sizeof(int)</c>) or, when synthesized
/// from <c>sizeof expr</c>, of the operand's type. Codegen prints C#'s
/// <c>sizeof(T)</c> for a scalar/struct, or <c>count * sizeof(elem)</c> for an
/// array (which lowered to a pointer, so C#'s <c>sizeof</c> would be wrong).</summary>
public sealed record SizeOfExpr(CType Of) : CExpr;

/// <summary><c>offsetof(T, member)</c> — the byte offset of <paramref name="Member"/>
/// within struct/union <paramref name="StructType"/>. Codegen computes it via the
/// address-through-a-null-pointer idiom (<c>(nint)&amp;((T*)null)-&gt;m</c>), so it
/// respects the real .NET blittable layout (alignment included).
/// <see cref="MemberType"/> is the member's declared type (null if the struct/field
/// isn't modelled) — a neutral fact from which the backend decides rendering: an
/// array member that lowers to a C# <c>fixed</c> buffer already evaluates to its own
/// address, so the backend omits the <c>&amp;</c> (taking it would be CS0211).</summary>
public sealed record OffsetOf(CType StructType, string Member, CType? MemberType) : CExpr;

/// <summary>A positional struct/union aggregate initializer — lowered from
/// <c>struct Point p = {3, 4}</c>. Codegen emits a C# object initializer
/// <c>new Point { x = 3, y = 4 }</c>; <see cref="Members"/> pairs each supplied
/// field (in declaration order) with its value. Fields the brace list doesn't
/// reach are omitted, so they take C#'s zero default — exactly C's partial-init
/// rule. A nested brace over a struct/union-typed field is itself a
/// <see cref="StructInit"/>.</summary>
public sealed record StructInit(IReadOnlyList<FieldInit> Members) : CExpr;

/// <summary>One member of a <see cref="StructInit"/>: the field name, its
/// declared <see cref="FieldType"/> (so codegen coerces the value as C would at
/// the store), and the value expression.</summary>
public readonly record struct FieldInit(string Name, CType FieldType, CExpr Value);

/// <summary>An array aggregate as a value — a C99 array compound literal
/// (<c>(int[]){1,2,3}</c>) or any array initializer. Codegen lowers it to a C#
/// <c>stackalloc T[]{ … }</c>, valid in initializer position (a stackalloc can't
/// escape to a pointer elsewhere in C#). <see cref="Elems"/> is the dense element
/// list the builder already computed (designators resolved, dimensions
/// zero-filled).</summary>
public sealed record StackArray(CType Element, IReadOnlyList<CExpr> Elems) : CExpr;

/// <summary>A file-scope / static-local array's backing store — a pinned, rooted
/// managed array exposed as a stable <c>T*</c> (the <c>Libc.GlobalArray*</c>
/// helpers). Unlike <see cref="StackArray"/> (a block-local <c>stackalloc</c>),
/// this persists for the program lifetime. <see cref="Elems"/> is the dense
/// initializer (null for a zeroed array, where <see cref="Count"/> gives the
/// length). Codegen picks the helper by element kind: a pointer / function-pointer
/// element can't be a C# generic type argument, so it round-trips through a pinned
/// <c>nint[]</c> reinterpreted as <c>T**</c>.</summary>
public sealed record PinnedArray(CType Element, IReadOnlyList<CExpr>? Elems, CExpr? Count) : CExpr;

/// <summary>The stack-value replacement for a promoted <c>malloc</c> — the
/// malloc→stack peephole rewrites <c>T* p = (T*)malloc(sizeof(T))</c> (used only
/// via <c>-&gt;</c> and freed, never escaping) to <c>T p = new T()</c>. Codegen
/// emits <c>new T()</c> (a zero-initialized struct value).</summary>
public sealed record StackNew(CType StructType) : CExpr;

/// <summary>A C23 empty initializer (<c>{}</c> / <c>(T){}</c>) — a zero value of
/// the carried <see cref="CExpr.Type"/>. Codegen emits <c>default(T)</c>, which
/// zero-fills a scalar, pointer, struct, or union uniformly.</summary>
public sealed record DefaultLit : CExpr;

/// <summary><c>va_arg(ap, T)</c> — pull the next variadic argument of type
/// <see cref="Target"/> from the <see cref="Ap"/> cursor. Codegen lowers it to the
/// matching <c>VaList</c> accessor (<c>(T)ap.Next()</c>, or <c>(T)ap.NextPtr()</c>
/// for a pointer target). Special syntax — its second operand is a type — so it's
/// a dedicated node rather than an ordinary call.</summary>
public sealed record VaArgGet(CExpr Ap, CType Target) : CExpr;

/// <summary>A parenthesized sub-expression — kept so codegen can preserve
/// explicit grouping (precedence-driven parens come later).</summary>
public sealed record Paren(CExpr Inner) : CExpr;

/// <summary>Escape hatch: a pre-rendered C# fragment. Used when the builder
/// resolves something to verbatim text it has no richer node for yet (an
/// unresolved builtin name, a macro-substituted literal). Kept rare — every use
/// is a candidate for a real node later.</summary>
public sealed record Raw(string CsText) : CExpr;

// ---- statements ---------------------------------------------------------

/// <summary>Base of the statement IR.</summary>
public abstract record CStmt
{
    public SrcPos Pos { get; init; }
}

/// <summary>A brace block with its own lexical scope.</summary>
public sealed record Block(IReadOnlyList<CStmt> Stmts) : CStmt;

/// <summary>One or more local declarations from a single declaration statement
/// (<c>int a = 0, b;</c>).</summary>
public sealed record DeclStmt(IReadOnlyList<LocalDecl> Decls) : CStmt;

/// <summary>An expression used for its side effects (<c>a = b;</c>, <c>f();</c>).</summary>
public sealed record ExprStmt(CExpr Expr) : CStmt;

public sealed record If(CExpr Cond, CStmt Then, CStmt? Else) : CStmt;
public sealed record While(CExpr Cond, CStmt Body) : CStmt;
public sealed record DoWhile(CStmt Body, CExpr Cond) : CStmt;

/// <summary>A C <c>for</c>. <see cref="Init"/> is a DeclStmt or ExprStmt (or
/// null); <see cref="Cond"/>/<see cref="Post"/> are optional.</summary>
public sealed record For(CStmt? Init, CExpr? Cond, CExpr? Post, CStmt Body) : CStmt;

public sealed record Return(CExpr? Value) : CStmt;
public sealed record Break : CStmt;
public sealed record Continue : CStmt;

/// <summary>A <c>goto label;</c>. <see cref="Label"/> is the RAW C label name; the
/// backend escapes it for emission (C# keyword collisions).</summary>
public sealed record Goto(string Label) : CStmt;

/// <summary>A labeled statement <c>name: body</c> (<see cref="Name"/> is the RAW C
/// label name; the backend escapes it for emission).</summary>
public sealed record Labeled(string Name, CStmt Body) : CStmt;

/// <summary>The desugared form of a recognised <c>setjmp</c>/<c>longjmp</c> guard
/// (<c>if (setjmp(env) [== 0]) THEN [else ELSE]</c>). Real C's "setjmp returns
/// twice" has no structured-control-flow equivalent, so codegen lowers this to
/// <c>env = new LongJmpToken(); try { TryBody } catch (LongJmpException __jmp)
/// when (__jmp.Token == env) { CatchBody }</c>. <see cref="TryBody"/> is the path
/// taken on setjmp's direct (zero) return; <see cref="CatchBody"/> is the longjmp
/// re-entry path (null for the no-recovery swallow shape — Lua's <c>LUAI_TRY</c>).
/// <see cref="Env"/> renders to the <c>jmp_buf</c> lvalue that is freshly armed
/// and matched on, so nested setjmps stay disambiguated by token identity.</summary>
public sealed record SetjmpGuard(CExpr Env, CStmt? TryBody, CStmt? CatchBody) : CStmt;

/// <summary>A C <c>switch (Subject) { … }</c>, lowered to a C# switch. The body is
/// pre-grouped into <see cref="Sections"/> (the grammar parses <c>case E:</c> /
/// <c>default:</c> as statement-level labels — possibly Duff's-device-nested — so
/// the builder flattens and groups them). C lets a section fall into the next; C#
/// forbids implicit fall-through (CS0163) and a final case falling out (CS8070),
/// so codegen inserts the explicit jump C performs (<c>goto case</c> /
/// <c>goto default</c> / a trailing <c>break</c>) on any section that doesn't
/// already end control flow.</summary>
public sealed record Switch(CExpr Subject, IReadOnlyList<SwitchSection> Sections) : CStmt;

/// <summary>One case section: its (stacked) labels and the statements that follow
/// up to the next label.</summary>
public sealed record SwitchSection(IReadOnlyList<SwitchLabel> Labels, IReadOnlyList<CStmt> Body);

/// <summary>A <c>case E:</c> / <c>default:</c> label that appears NESTED inside
/// another statement of a switch body rather than at the switch's top level —
/// Duff's device (<c>case 7:</c> interleaved into a <c>do…while</c>). The grammar
/// accepts a case/default label anywhere; <see cref="Switch"/> only models the
/// top-level sections, so a nested one becomes this free-standing labeled
/// statement. Codegen prints it verbatim as <c>case E:</c> / <c>default:</c>
/// followed by <see cref="Body"/> — structurally faithful (C# rejects a case
/// label inside a nested block, which is the known Duff's limitation).</summary>
public sealed record CaseLabelStmt(CExpr? CaseExpr, CStmt Body) : CStmt;

/// <summary>A <c>case E:</c> (<see cref="CaseExpr"/> set) or <c>default:</c>
/// (null) label. The case expression must be a constant per C# rules — an integer
/// literal, or an enumerator which codegen decays to <c>(int)EnumName.Member</c>
/// (still a constant), matching the int-decayed switch subject.</summary>
public readonly record struct SwitchLabel(CExpr? CaseExpr);

// ---- declarations / translation unit ------------------------------------

/// <summary>A local array declaration, lowered to a C# <c>stackalloc</c>.
/// <see cref="Inits"/> non-null is the brace-initialized form
/// (<c>stackalloc T[]{ … }</c>); otherwise <see cref="CountExpr"/> gives the
/// dimension (<c>stackalloc T[n]</c>, C# zero-fills).</summary>
public sealed record ArrayDecl(Symbol Sym, CType Element, CExpr? CountExpr, IReadOnlyList<CExpr>? Inits) : CStmt;

/// <summary>A local variable declaration with optional initializer.</summary>
public sealed record LocalDecl(Symbol Sym, CExpr? Init);

/// <summary>A struct or union type definition. Codegen renders it into the
/// top-level type-declarations section (a plain <c>unsafe struct</c>, or an
/// explicit-layout one for a union). Field types are also registered in the
/// builder's struct table so member access resolves a field's type.</summary>
public sealed record StructTypeDef(string Name, IReadOnlyList<StructField> Fields, bool IsUnion);

/// <summary>One field of a <see cref="StructTypeDef"/>. <see cref="BitWidth"/> is
/// 0 for a normal field, or the declared width of a bit-field (lowered to a
/// backing field + a masked/sign-extended accessor property — value semantics,
/// since C bit packing is implementation-defined).</summary>
public readonly record struct StructField(string Name, CType Type, int BitWidth = 0);

/// <summary>A C <c>enum</c> type definition (tagged or typedef-named). Codegen
/// renders it into the top-level type-declarations section as a real
/// <c>enum Name : Underlying { … }</c>. <see cref="Members"/> pairs each
/// enumerator with its (auto-incremented or explicit) integer value.</summary>
public sealed record EnumTypeDef(string Name, CType Underlying, IReadOnlyList<EnumMember> Members);

/// <summary>One enumerator of an <see cref="EnumTypeDef"/>: its C name and value.</summary>
public readonly record struct EnumMember(string Name, long Value);

/// <summary>A function parameter (or function-pointer parameter): its type and
/// name. Used while building signatures — a named record rather than a loose
/// tuple so members read as <c>.Type</c> / <c>.Name</c>.</summary>
public readonly record struct ParamInfo(CType Type, string Name);

/// <summary>A function definition: signature symbol + parameter symbols + body.</summary>
public sealed record FuncDef(Symbol Sym, IReadOnlyList<Symbol> Params, Block Body, bool Variadic);

/// <summary>A file-scope variable.</summary>
public sealed record GlobalVar(Symbol Sym, CExpr? Init);

/// <summary>The whole compiled unit: the typed IR a backend consumes.</summary>
public sealed class TranslationUnit
{
    public List<FuncDef> Functions { get; } = new();
    public List<GlobalVar> Globals { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();
}
