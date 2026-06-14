#nullable enable

using System.Linq;

namespace DotCC.Backends;

using DotCC.Ir;

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
        // A native-call-conv fn-ptr (a dlsym'd address) lowers to an unmanaged cdecl
        // delegate* so the JIT/AOT uses the C calling convention (matching the
        // `-shared` exports' [UnmanagedCallersOnly(CallConvs=CallConvCdecl)]); the
        // `CallConvCdecl` modifier resolves without a using. Default (managed) is
        // unchanged — `&fn` of dotcc's own methods stays a managed delegate*.
        CType.Func f => (f.IsNativeCallConv ? "delegate* unmanaged[Cdecl]<" : "delegate*<")
            + string.Join(", ", f.Params.Select(RenderType).Append(RenderType(f.Return))) + ">",
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
        "char16_t" => "char",   // C11 char16_t → C# char (both 16-bit UTF-16 code units)
        "wchar_t" => "char",    // wchar_t → C# char — dotcc's MSVC-shaped 16-bit UTF-16 wchar_t
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

/// <summary>The .NET / C# backend's identifier policy: escape C# keywords with
/// <c>@</c>, forbid shadowing (CS0136), uniquify with a <c>__k</c> suffix.</summary>
internal sealed class CSharpNameLegalizer : INameLegalizer
{
    public string Escape(string rawName) => DotCC.EmitHelpers.Id(rawName);
    public bool ForbidsShadowing => true;
    public string Uniquify(string escaped, int collision) => $"{escaped}__{collision}";
}
