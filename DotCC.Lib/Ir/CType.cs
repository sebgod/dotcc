#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace DotCC.Ir;

/// <summary>
/// C type qualifiers, carried as first-class flags on a <see cref="CType"/> —
/// unlike the legacy pipeline, which strips <c>const</c> in
/// <c>QualifierStripper</c> before the parser ever sees it. Keeping them on the
/// type is what makes a const-correctness pass (and faithful volatile/_Atomic
/// lowering) expressible in the IR.
/// </summary>
[Flags]
public enum TypeQual
{
    None = 0,
    Const = 1,
    Volatile = 2,
    Atomic = 4,
    Restrict = 8,
}

/// <summary>
/// A real C type. Unlike the legacy <see cref="DotCC.CType"/> (a sizeof-only
/// helper threaded through emitted strings), this is the spine of the IR: every
/// <see cref="CExpr"/> carries one, name resolution attaches one to every
/// <see cref="Symbol"/>, and <see cref="CodeGen"/> reads <see cref="CsType"/> to
/// print the lowered C#. Qualifiers ride along via <see cref="Quals"/>.
/// </summary>
public abstract record CType
{
    /// <summary>Qualifiers applied to this type (const/volatile/_Atomic/restrict).</summary>
    public TypeQual Quals { get; init; } = TypeQual.None;

    /// <summary>The lowered C# type string <see cref="CodeGen"/> emits.</summary>
    public abstract string CsType { get; }

    /// <summary>C <c>sizeof</c> in bytes (ILP32-pointer-on-64 model: pointers are 8).</summary>
    public abstract int SizeOf { get; }

    public virtual bool IsInteger => false;
    public virtual bool IsArithmetic => false;
    public bool IsConst => (Quals & TypeQual.Const) != 0;
    public bool IsVolatile => (Quals & TypeQual.Volatile) != 0;
    public bool IsAtomic => (Quals & TypeQual.Atomic) != 0;

    /// <summary>Return a copy of this type with the given qualifiers OR-ed in.</summary>
    public CType WithQuals(TypeQual add) => add == TypeQual.None ? this : this with { Quals = Quals | add };

    /// <summary>Drop all qualifiers (the unqualified shape, for comparisons/casts).</summary>
    public CType Unqualified => Quals == TypeQual.None ? this : this with { Quals = TypeQual.None };

    // ---- the kinds -------------------------------------------------------

    /// <summary>A scalar (arithmetic) type — integer or floating. <see cref="CsType"/>
    /// is the exact lowered C# primitive; <see cref="Signed"/> distinguishes
    /// signedness for integer conversions.</summary>
    public sealed record Prim(string Name, string CsName, int Bytes, bool Integer, bool Signed) : CType
    {
        public override string CsType => CsName;
        public override int SizeOf => Bytes;
        public override bool IsInteger => Integer;
        public override bool IsArithmetic => true;
    }

    /// <summary><c>void</c> — no value, no size (C makes <c>sizeof(void)</c> ill-formed;
    /// we report 1 to match gcc's extension rather than throw).</summary>
    public sealed record VoidType : CType
    {
        public override string CsType => "void";
        public override int SizeOf => 1;
    }

    /// <summary>A pointer. Lowers to the pointee's C# type plus <c>*</c>.</summary>
    public sealed record Pointer(CType Pointee) : CType
    {
        public override string CsType => Pointee.CsType + "*";
        public override int SizeOf => 8;
        public override bool IsInteger => false;
    }

    /// <summary>A C array <c>T[N]</c>. Lowered to a C# pointer at use sites (it
    /// decays), so <see cref="CsType"/> is the pointer form, but <see cref="SizeOf"/>
    /// is the true aggregate size <c>N * sizeof(T)</c>.</summary>
    public sealed record Array(CType Element, int? Count) : CType
    {
        public override string CsType => Element.CsType + "*";
        public override int SizeOf => Element.SizeOf * (Count ?? 0);
    }

    /// <summary>A function type. Not a value type in C; carried so a function
    /// symbol's signature (return + params + variadic) is available for
    /// call-site coercion and diagnostics.</summary>
    public sealed record Func(CType Return, IReadOnlyList<CType> Params, bool Variadic) : CType
    {
        public override string CsType =>
            $"delegate*<{string.Join(", ", Params.Select(p => p.CsType).Append(Return.CsType))}>";
        public override int SizeOf => 8;
    }

    /// <summary>A named type the IR doesn't model structurally yet (a typedef
    /// target / opaque libc struct like <c>FILE</c>). <see cref="CsType"/> is the
    /// spelling codegen emits; size is unknown (0) until struct layout lands in a
    /// later phase.</summary>
    public sealed record Named(string Name) : CType
    {
        public override string CsType => Name;
        public override int SizeOf => 0;
    }

    // ---- well-known instances -------------------------------------------

    public static readonly CType Void = new VoidType();
    public static readonly CType Bool = new Prim("_Bool", "CBool", 1, true, false);
    public static readonly CType Char = new Prim("char", "byte", 1, true, true);
    public static readonly CType SChar = new Prim("signed char", "sbyte", 1, true, true);
    public static readonly CType UChar = new Prim("unsigned char", "byte", 1, true, false);
    public static readonly CType Short = new Prim("short", "short", 2, true, true);
    public static readonly CType UShort = new Prim("unsigned short", "ushort", 2, true, false);
    public static readonly CType Int = new Prim("int", "int", 4, true, true);
    public static readonly CType UInt = new Prim("unsigned int", "uint", 4, true, false);
    public static readonly CType Long = new Prim("long", "long", 8, true, true);
    public static readonly CType ULong = new Prim("unsigned long", "ulong", 8, true, false);
    public static readonly CType LongLong = new Prim("long long", "long", 8, true, true);
    public static readonly CType ULongLong = new Prim("unsigned long long", "ulong", 8, true, false);
    public static readonly CType Float = new Prim("float", "float", 4, false, true);
    public static readonly CType Double = new Prim("double", "double", 8, false, true);
    public static readonly CType LongDouble = new Prim("long double", "double", 8, false, true);

    /// <summary>C <c>size_t</c> — dotcc lowers it to C# <c>ulong</c>.</summary>
    public static readonly CType SizeT = ULong;

    /// <summary>The decayed type of a string literal: <c>char*</c> (i.e. <c>byte*</c>).</summary>
    public static CType CharPtr => new Pointer(Char);
}
