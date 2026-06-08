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
        _ => throw new IrUnsupportedException("C# target cannot render type " + t.GetType().Name),
    };

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
