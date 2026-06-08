#nullable enable

using System.Linq;

namespace DotCC.Ir;

/// <summary>
/// A backend's lexical projection of the neutral IR onto its own surface
/// language — the seam that turns dotcc from a 1×1 transpiler into an N×M frame.
/// The typed IR (<see cref="CExpr"/>/<see cref="CStmt"/>/<see cref="CType"/>)
/// carries only target-neutral C facts; everything that spells a particular
/// OUTPUT language lives behind this interface, so a second target (a C emitter,
/// a WebAssembly-text emitter, …) is "implement <see cref="ITarget"/> again"
/// rather than "untangle C# from the IR".
/// <para>For now the seam is the type-spelling map; the statement / expression
/// emitter (<see cref="CodeGen"/>) is still the C#-specific one. Literal and
/// identifier projection follow.</para>
/// </summary>
internal interface ITarget
{
    /// <summary>Project a neutral <see cref="CType"/> onto this target's type
    /// spelling (e.g. C's <c>unsigned long</c> → C# <c>ulong</c>; a C array, which
    /// decays to a pointer, → <c>T*</c>).</summary>
    string RenderType(CType t);

    /// <summary>Render an integer literal — its neutral digit core plus whatever
    /// suffix this target spells for the literal's integer type.</summary>
    string RenderIntLit(LitInt lit);

    /// <summary>Render a floating-point literal in this target's syntax.</summary>
    string RenderFloatLit(LitFloat lit);
}

/// <summary>
/// The .NET / C# backend's projection. Lowers to low-level <c>unsafe</c> C#: C's
/// <c>char</c> walks bytes (so <c>byte</c>), <c>_Bool</c> is the store-normalizing
/// <c>CBool</c> value type, every array decays to a flat pointer, and a function
/// type is a <c>delegate*</c>. This reproduces exactly the spellings the IR type
/// model used to bake in via the old <c>CType.CsType</c> property.
/// </summary>
internal sealed class CSharpTarget : ITarget
{
    public string RenderType(CType t) => t switch
    {
        CType.Prim p => RenderPrim(p),
        CType.VoidType => "void",
        // A pointer-TO-array collapses to the array's own flat row pointer (no extra
        // `*`); every other pointer is the pointee's spelling plus `*`.
        CType.Pointer ptr => ptr.Pointee is CType.Array ? RenderType(ptr.Pointee) : RenderType(ptr.Pointee) + "*",
        // An array lowers to a single pointer to its innermost scalar.
        CType.Array a => RenderType(a.FlatElement) + "*",
        CType.Func f => $"delegate*<{string.Join(", ", f.Params.Select(RenderType).Append(RenderType(f.Return)))}>",
        CType.Named n => n.Name,
        CType.Enum e => e.Name,
        CType.ComplexType => "System.Numerics.Complex",
        CType.Float128Type => "Float128",
        _ => throw new IrUnsupportedException("C# target cannot render type " + t.GetType().Name),
    };

    public string RenderIntLit(LitInt lit) => lit.Digits + IntSuffix(lit.Type);

    /// <summary>The C# integer-literal suffix for a type: <c>u</c> (uint), <c>L</c>
    /// (long), <c>UL</c> (ulong), none (int / narrower) — reproducing exactly what
    /// the builder used to append before the suffix moved behind this seam.</summary>
    private static string IntSuffix(CType t) => t.Unqualified is CType.Prim { Integer: true } p
        ? (p.Signed, p.Bytes >= 8) switch
        {
            (true, false) => "",
            (false, false) => "u",
            (true, true) => "L",
            (false, true) => "UL",
        }
        : "";

    public string RenderFloatLit(LitFloat lit) => lit.Text;

    /// <summary>Map a C primitive (keyed on its canonical C name) to the C# type it
    /// lowers to. <c>char</c>→<c>byte</c> so <c>char*</c> arithmetic walks bytes;
    /// <c>_Bool</c>→<c>CBool</c> for C store-normalization; <c>long</c>/<c>long
    /// long</c> are both 64-bit <c>long</c> (LP64).</summary>
    private static string RenderPrim(CType.Prim p) => p.Name switch
    {
        "_Bool" => "CBool",
        "char" => "byte",
        "signed char" => "sbyte",
        "unsigned char" => "byte",
        "short" => "short",
        "unsigned short" => "ushort",
        "int" => "int",
        "unsigned int" => "uint",
        "long" => "long",
        "unsigned long" => "ulong",
        "long long" => "long",
        "unsigned long long" => "ulong",
        "float" => "float",
        "double" => "double",
        "long double" => "double",
        _ => throw new IrUnsupportedException("C# target has no spelling for primitive " + p.Name),
    };
}

/// <summary>
/// A target's identifier policy, injected into <see cref="SymbolTable"/> so the
/// shared name-resolution machinery doesn't bake in one output language's rules.
/// The table owns the neutral MECHANISM (scope tracking + collision counting);
/// this owns the target POLICY — how a raw source name is escaped to a legal
/// target identifier, whether the target even forbids a local shadowing an
/// enclosing one, and how a collision is uniquified.
/// </summary>
internal interface INameLegalizer
{
    /// <summary>Escape a raw source identifier into a legal target identifier
    /// (the C# backend: a reserved word → <c>@word</c>).</summary>
    string Escape(string rawName);

    /// <summary>True when the target rejects a block-local shadowing an enclosing
    /// binding (C# — CS0136), so <see cref="SymbolTable"/> must uniquify; false when
    /// shadowing is legal (C), so names pass through unchanged.</summary>
    bool ForbidsShadowing { get; }

    /// <summary>Form the <paramref name="collision"/>-th uniquified variant of an
    /// already-escaped name (the C# backend: <c>name__k</c>).</summary>
    string Uniquify(string escaped, int collision);
}

/// <summary>The .NET / C# backend's identifier policy: escape C# keywords with
/// <c>@</c>, forbid shadowing (CS0136), uniquify with a <c>__k</c> suffix.</summary>
internal sealed class CSharpNameLegalizer : INameLegalizer
{
    public string Escape(string rawName) => DotCC.EmitHelpers.Id(rawName);
    public bool ForbidsShadowing => true;
    public string Uniquify(string escaped, int collision) => $"{escaped}__{collision}";
}
