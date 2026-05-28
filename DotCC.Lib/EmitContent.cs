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
}
