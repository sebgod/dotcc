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

    /// <summary>Plain C# source code text — by far the most common variant.</summary>
    /// <param name="EnumType">Non-null when this expression lowered to a value
    /// of a C# <c>enum</c> type (the enum's name). Carried so a consuming node
    /// can insert the int↔enum casts C# requires but C doesn't: an enum operand
    /// of an arithmetic/bitwise/relational op decays to <c>(int)</c>, and a
    /// non-enum value stored into an enum-typed slot is wrapped <c>(Enum)</c>.
    /// Only set by expression nodes that produce an enum value (enum var read,
    /// enumerator ref, and the transparent wrappers that propagate it).</param>
    public sealed record Text(string Value, string? EnumType = null) : EmitContent;

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
    public sealed record Args(IReadOnlyList<string> Values, EmitContent? SoleArg = null, IReadOnlyList<string?>? ArgEnums = null) : EmitContent;

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
    public sealed record MallocSizeof(string StructType, string LowLevelText) : EmitContent;

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
        bool IsStatic) : EmitContent;

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
    public sealed record DeclEntry(string Name, string? Init, string? MallocStructType = null, string? InitEnumType = null);

    /// <summary>
    /// Accumulator for an init-declarator list. <c>declItem</c>/<c>declItemInit</c>
    /// produce a single-entry list; <c>declItemListCons</c> concatenates.
    /// </summary>
    public sealed record DeclEntries(IReadOnlyList<DeclEntry> Entries) : EmitContent;
}
