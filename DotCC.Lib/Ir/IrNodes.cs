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

/// <summary>A string literal, already lowered to the full <c>Libc.L("…"u8)</c>
/// (or byte-array) expression text.</summary>
public sealed record LitStr(string CsExpr) : CExpr;

/// <summary>A reference to a resolved variable / parameter / function.</summary>
public sealed record VarRef(Symbol Sym) : CExpr;

/// <summary>A unary operation.</summary>
public sealed record Unary(UnOp Op, CExpr Operand) : CExpr;

/// <summary>A binary operation.</summary>
public sealed record Binary(BinOp Op, CExpr Left, CExpr Right) : CExpr;

/// <summary>An assignment. <see cref="CompoundOp"/> is null for a plain
/// <c>=</c>, or the arithmetic/bitwise op for a compound assignment (<c>+=</c>).</summary>
public sealed record Assign(BinOp? CompoundOp, CExpr Target, CExpr Value) : CExpr;

/// <summary>A function call by name (the slice only calls named functions /
/// libc builtins). <see cref="Builtin"/> marks a libc name that codegen lowers
/// specially (printf-family fluent form). <see cref="ParamTypes"/> is the
/// resolved callee's fixed-parameter types (null when the callee has no known
/// signature), used by codegen to coerce each argument to its parameter type —
/// C's implicit conversion at a call, which C# requires made explicit.</summary>
public sealed record Call(string Callee, IReadOnlyList<CExpr> Args, bool Builtin,
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
/// there. (The value-context comma operator is a separate, later concern.)</summary>
public sealed record CommaSeq(IReadOnlyList<CExpr> Items) : CExpr;

/// <summary><c>sizeof</c> — of a type (<c>sizeof(int)</c>) or, when synthesized
/// from <c>sizeof expr</c>, of the operand's type. Codegen prints C#'s
/// <c>sizeof(T)</c> for a scalar/struct, or <c>count * sizeof(elem)</c> for an
/// array (which lowered to a pointer, so C#'s <c>sizeof</c> would be wrong).</summary>
public sealed record SizeOfExpr(CType Of) : CExpr;

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

/// <summary>A <c>goto label;</c>. <see cref="Label"/> is the already-escaped C# label.</summary>
public sealed record Goto(string Label) : CStmt;

/// <summary>A labeled statement <c>name: body</c> (<see cref="Name"/> escaped).</summary>
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

/// <summary>A <c>case E:</c> (<see cref="CaseExpr"/> set) or <c>default:</c>
/// (null) label. The case expression must be a constant per C# rules — enum
/// constants already lowered to integer literals, so this holds.</summary>
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

/// <summary>One field of a <see cref="StructTypeDef"/>.</summary>
public readonly record struct StructField(string Name, CType Type);

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
