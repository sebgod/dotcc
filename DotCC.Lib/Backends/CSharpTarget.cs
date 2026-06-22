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
        // A Zig value optional `?T` → C# Nullable<T> (`T?`): null = none, `.?` = .Value,
        // `orelse` = `??`. (An optional POINTER `?*T` is a bare nullable `T*`, never this.)
        CType.Optional o => RenderType(o.Inner) + "?",
        // A Zig error union `E!T` → the runtime `ErrUnion<Payload>` value type. A `void`
        // payload (`!void`) has no generic-over-void in C#, so it uses the `Unit` payload.
        CType.ErrorUnion eu => "ErrUnion<" + (eu.Payload is CType.VoidType ? "Unit" : RenderType(eu.Payload)) + ">",
        // A Zig error-set value (a bare `error.Foo` / a captured error, Milestone N) → the raw
        // `ushort` error code. V1 erases the named set into one flat code space, so an error value
        // IS its code; error-value equality and a future error `switch` compare the codes directly.
        CType.ErrorSetType => "ushort",
        // A Zig slice `[]T` → the runtime `Slice<T>` fat-pointer value type; `[]const T`
        // (a const-qualified element) → `ConstSlice<T>`. The element is rendered unqualified
        // (the const lives in the slice type's identity, not a C# `const`).
        CType.Slice s => (s.Element.IsConst ? "ConstSlice<" : "Slice<") + RenderType(s.Element.Unqualified) + ">",
        // A Zig `std.mem.Allocator` → the runtime `Allocator` fat-pointer value type
        // (Milestone F). The concrete `FixedBufferAllocator` is a `CType.Named` (renders its name).
        CType.Allocator => "Allocator",
        // A Zig tuple `struct { T1, T2, … }` → `System.ValueTuple<T1, …>` (Milestone G).
        // Arity-uniform — including arity 1 (`System.ValueTuple<T>`), where C#'s `(T)` shorthand
        // would be a parenthesised expression, not a tuple.
        CType.Tuple tup => "System.ValueTuple<" + string.Join(", ", tup.Elements.Select(e => RenderType(e.Unqualified))) + ">",
        _ => throw new IrUnsupportedException("C# target cannot render type " + t.GetType().Name),
    };

    public string RenderIntLit(LitInt lit) =>
        lit.Type.Unqualified is CType.Prim { Integer: true, Bytes: >= 16 } p128
            ? Render128Lit(lit.Digits, p128.Signed)
            : lit.Digits + IntSuffix(lit.Type);

    /// <summary>Emit a 128-bit integer literal. C# has no <c>Int128</c>/<c>UInt128</c> literal
    /// suffix, so a magnitude that fits <c>ulong</c> is written as a plain literal cast to the
    /// 128-bit type (the cast pins overload resolution), and a larger magnitude — which has no C#
    /// literal form at all — is materialized via <c>Parse</c>. The digit string is the normalized
    /// decimal magnitude (any sign rides on an outer <c>Unary(Neg)</c>).</summary>
    private static string Render128Lit(string digits, bool signed)
    {
        var ty = signed ? "System.Int128" : "System.UInt128";
        return ulong.TryParse(digits, out _) ? $"({ty}){digits}UL" : $"{ty}.Parse(\"{digits}\")";
    }

    /// <summary>The C# integer-literal suffix for a type: <c>u</c> (uint), <c>L</c>
    /// (long), <c>UL</c> (ulong), none (int / narrower) — reproducing exactly what
    /// the builder used to append before the suffix moved behind this seam. 128-bit types are
    /// handled separately by <see cref="Render128Lit"/> (no C# suffix exists for them).</summary>
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
        "__int128" => "System.Int128",            // C __int128 / Zig i128 → BCL Int128
        "unsigned __int128" => "System.UInt128",  // C unsigned __int128 / Zig u128 → BCL UInt128
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
