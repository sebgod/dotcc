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
    public sealed record Text(string Value) : EmitContent;

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
    public sealed record Args(IReadOnlyList<string> Values) : EmitContent;

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
    public sealed record DeclEntry(string Name, string? Init);

    /// <summary>
    /// Accumulator for an init-declarator list. <c>declItem</c>/<c>declItemInit</c>
    /// produce a single-entry list; <c>declItemListCons</c> concatenates.
    /// </summary>
    public sealed record DeclEntries(IReadOnlyList<DeclEntry> Entries) : EmitContent;
}
