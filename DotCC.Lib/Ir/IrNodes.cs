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
/// specially (printf-family fluent form).</summary>
public sealed record Call(string Callee, IReadOnlyList<CExpr> Args, bool Builtin) : CExpr;

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

// ---- declarations / translation unit ------------------------------------

/// <summary>A local array declaration, lowered to a C# <c>stackalloc</c>.
/// <see cref="Inits"/> non-null is the brace-initialized form
/// (<c>stackalloc T[]{ … }</c>); otherwise <see cref="CountExpr"/> gives the
/// dimension (<c>stackalloc T[n]</c>, C# zero-fills).</summary>
public sealed record ArrayDecl(Symbol Sym, CType Element, CExpr? CountExpr, IReadOnlyList<CExpr>? Inits) : CStmt;

/// <summary>A local variable declaration with optional initializer.</summary>
public sealed record LocalDecl(Symbol Sym, CExpr? Init);

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
