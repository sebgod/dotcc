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
/// <see cref="CExpr"/> carries one and name resolution attaches one to every
/// <see cref="Symbol"/>. The type is target-NEUTRAL — it spells no output
/// language. A backend's <see cref="ITarget.RenderType"/> projects it onto its
/// own surface spelling (the C# backend's <c>CSharpTarget</c>:
/// <c>unsigned long</c> → <c>ulong</c>, <c>T[]</c> → <c>T*</c>, …); for
/// diagnostics, <see cref="Describe"/> gives a source-C spelling. Qualifiers ride
/// along via <see cref="Quals"/>.
/// </summary>
public abstract record CType
{
    /// <summary>Qualifiers applied to this type (const/volatile/_Atomic/restrict).</summary>
    public TypeQual Quals { get; init; } = TypeQual.None;

    /// <summary>C <c>sizeof</c> in bytes (ILP32-pointer-on-64 model: pointers are 8).</summary>
    public abstract int SizeOf { get; }

    public virtual bool IsInteger => false;
    public virtual bool IsArithmetic => false;
    public bool IsConst => (Quals & TypeQual.Const) != 0;
    public bool IsVolatile => (Quals & TypeQual.Volatile) != 0;
    public bool IsAtomic => (Quals & TypeQual.Atomic) != 0;

    /// <summary>True when this type lowers to a C# pointer (<c>T*</c>) or function
    /// pointer (<c>delegate*&lt;…&gt;</c>). Neither is a legal generic type argument,
    /// so any <c>Unsafe.AsPointer&lt;T&gt;</c> / <c>Volatile.*&lt;T&gt;</c> /
    /// <c>Atomic.*&lt;T&gt;</c> access to such an lvalue must reinterpret its storage
    /// as <c>nint</c> first (CS0306).</summary>
    public bool IsPointerLowered => Unqualified is Pointer or Func;

    /// <summary>Return a copy of this type with the given qualifiers OR-ed in.</summary>
    public CType WithQuals(TypeQual add) => add == TypeQual.None ? this : this with { Quals = Quals | add };

    /// <summary>Drop all qualifiers (the unqualified shape, for comparisons/casts).</summary>
    public CType Unqualified => Quals == TypeQual.None ? this : this with { Quals = TypeQual.None };

    /// <summary>A human-readable source-C spelling of this type, for diagnostics.
    /// Target-neutral — built from the type's own canonical names, NOT from any
    /// output-language projection (that is the backend's <see cref="ITarget.RenderType"/>).</summary>
    public string Describe() => this switch
    {
        Prim p => p.Name,
        VoidType => "void",
        Pointer ptr => ptr.Pointee.Describe() + "*",
        Array a => a.Element.Describe() + "[" + (a.Count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "") + "]",
        Func f => f.Return.Describe() + "(*)(" + string.Join(", ", f.Params.Select(p => p.Describe())) + ")",
        Named n => n.Name,
        Enum e => "enum " + e.Name,
        ComplexType => "_Complex",
        Float128Type => "_Float128",
        _ => GetType().Name,
    };

    // ---- the kinds -------------------------------------------------------

    /// <summary>A scalar (arithmetic) type — integer or floating. <see cref="Name"/>
    /// is the canonical C spelling, doubling as the stable identity a backend's
    /// type-mapper keys on; <see cref="Signed"/> distinguishes signedness for
    /// integer conversions and <see cref="Bytes"/> gives the width.</summary>
    public sealed record Prim(string Name, int Bytes, bool Integer, bool Signed) : CType
    {
        public override int SizeOf => Bytes;
        public override bool IsInteger => Integer;
        public override bool IsArithmetic => true;
    }

    /// <summary><c>void</c> — no value, no size (C makes <c>sizeof(void)</c> ill-formed;
    /// we report 1 to match gcc's extension rather than throw).</summary>
    public sealed record VoidType : CType
    {
        public override int SizeOf => 1;
    }

    /// <summary>A pointer to <see cref="Pointee"/>. (The C# backend lowers it to the
    /// pointee's type plus <c>*</c> — except a pointer-TO-array, which like the array
    /// itself collapses to one flat pointer to the innermost scalar, <c>int (*p)[3]</c>
    /// → <c>int*</c>, a row pointer subscripted with the array's stride.)</summary>
    public sealed record Pointer(CType Pointee) : CType
    {
        public override int SizeOf => 8;
        public override bool IsInteger => false;
    }

    /// <summary>A C array <c>T[N]</c> (the element may itself be an <see cref="Array"/>
    /// for a multi-dimensional array). It decays to a pointer at use sites, and a
    /// multi-dim array is stored as one flat buffer, so the C# backend collapses any
    /// nesting to a single pointer to the innermost scalar (<c>int[2][3]</c> →
    /// <c>int*</c>); <see cref="SizeOf"/> is the true aggregate size <c>N *
    /// sizeof(element)</c> (recursing through the dimensions).</summary>
    public sealed record Array(CType Element, int? Count) : CType
    {
        public override int SizeOf => Element.SizeOf * (Count ?? 0);

        /// <summary>The innermost non-array element (peels all array dimensions) —
        /// the flat scalar/struct the storage holds.</summary>
        public CType FlatElement { get { var e = Element; while (e is Array a) { e = a.Element; } return e; } }
    }

    /// <summary>A function type. Not a value type in C; carried so a function
    /// symbol's signature (return + params + variadic) is available for
    /// call-site coercion and diagnostics.</summary>
    public sealed record Func(CType Return, IReadOnlyList<CType> Params, bool Variadic) : CType
    {
        public override int SizeOf => 8;
    }

    /// <summary>A named type the IR doesn't model structurally yet (a typedef
    /// target / opaque libc struct like <c>FILE</c>). <see cref="Name"/> is the
    /// spelling the backend emits verbatim — a residual leak for the few names whose
    /// spelling differs per target (e.g. <c>Float128</c>); size is unknown (0) until
    /// struct layout lands in a later phase.</summary>
    public sealed record Named(string Name) : CType
    {
        public override int SizeOf => 0;
    }

    /// <summary>A C <c>enum</c> with a name (its tag, or the typedef alias for an
    /// anonymous-but-typedef'd enum). Lowers to a real C# <c>enum Name : underlying</c>
    /// — the typed IR knows statically when an operand is enum-typed, so codegen can
    /// decay it to <see cref="Underlying"/> at C's plain-int contexts (arithmetic,
    /// conditions, switch) and recast at enum-typed sinks. In C an enum IS an integer
    /// type, so it reports <see cref="IsInteger"/>/<see cref="IsArithmetic"/> true;
    /// because it is not a <see cref="Prim"/>, <see cref="UsualArithmetic"/> already
    /// collapses any enum operand to <see cref="Int"/> (the decay). An anonymous,
    /// un-typedef'd enum has no C# name, so its enumerators stay plain int constants
    /// instead (named constants) rather than synthesizing a type.</summary>
    public sealed record Enum(string Name, CType Underlying) : CType
    {
        public override int SizeOf => Underlying.SizeOf;
        public override bool IsInteger => true;
        public override bool IsArithmetic => true;
    }

    /// <summary>C99 <c>_Complex</c> — a complex number. dotcc lowers every width
    /// (<c>float</c>/<c>double</c>/<c>long double</c> complex) to one double-backed
    /// complex, so the element width is intentionally erased here; the backend spells
    /// it (the C# backend: <c>System.Numerics.Complex</c>). Complex arithmetic is
    /// recognised structurally (<c>is ComplexType</c>) and routed before the usual
    /// arithmetic conversions, so — like the prior <c>Named</c> lowering — it reports
    /// <see cref="IsArithmetic"/> false and an unmodelled size (0).</summary>
    public sealed record ComplexType : CType
    {
        public override int SizeOf => 0;
    }

    /// <summary>C23 <c>_Float128</c> / <c>__float128</c> — a 128-bit IEEE float. dotcc
    /// lowers it to a software binary128 type the backend spells (the C# backend:
    /// <c>Float128</c>, a <c>DotCC.Libc</c> value type). Like the prior <c>Named</c>
    /// lowering it is NOT modelled as a <see cref="Prim"/> — its arithmetic rides the
    /// backend type's operators — so size/arithmetic stay unmodelled (a correctness
    /// follow-up, preserved here for a no-behaviour-change neutralization).</summary>
    public sealed record Float128Type : CType
    {
        public override int SizeOf => 0;
    }

    // ---- well-known instances -------------------------------------------

    public static readonly CType Void = new VoidType();
    public static readonly CType Bool = new Prim("_Bool", 1, true, false);
    public static readonly CType Char = new Prim("char", 1, true, true);
    public static readonly CType SChar = new Prim("signed char", 1, true, true);
    public static readonly CType UChar = new Prim("unsigned char", 1, true, false);
    public static readonly CType Short = new Prim("short", 2, true, true);
    public static readonly CType UShort = new Prim("unsigned short", 2, true, false);
    public static readonly CType Int = new Prim("int", 4, true, true);
    public static readonly CType UInt = new Prim("unsigned int", 4, true, false);
    public static readonly CType Long = new Prim("long", 8, true, true);
    public static readonly CType ULong = new Prim("unsigned long", 8, true, false);
    public static readonly CType LongLong = new Prim("long long", 8, true, true);
    public static readonly CType ULongLong = new Prim("unsigned long long", 8, true, false);
    public static readonly CType Float = new Prim("float", 4, false, true);
    public static readonly CType Double = new Prim("double", 8, false, true);
    public static readonly CType LongDouble = new Prim("long double", 8, false, true);

    /// <summary>C <c>size_t</c> — dotcc lowers it to C# <c>ulong</c>.</summary>
    public static readonly CType SizeT = ULong;

    /// <summary>C99 <c>_Complex</c> — every width lowers to one double-backed complex
    /// (see <see cref="ComplexType"/>); the backend's operators cover complex×real too.</summary>
    public static readonly CType Complex = new ComplexType();

    /// <summary>C23 <c>_Float128</c> — software binary128. See <see cref="Float128Type"/>.</summary>
    public static readonly CType Float128 = new Float128Type();

    /// <summary>The decayed type of a string literal: <c>char*</c> (i.e. <c>byte*</c>).</summary>
    public static CType CharPtr => new Pointer(Char);

    // ---- conversions ----------------------------------------------------

    /// <summary>
    /// C's usual arithmetic conversions (§6.3.1.8): the common type two
    /// arithmetic operands are converted to before a binary <c>+ - * / %</c>,
    /// bitwise <c>&amp; | ^</c>, or relational/equality operator applies. Derived
    /// structurally from operand width (<see cref="Prim.Bytes"/>) and signedness
    /// (<see cref="Prim.Signed"/>) — there is no per-type table. For a
    /// non-arithmetic operand the result is meaningless; callers resolve those
    /// (pointer arithmetic, the relational <c>int</c> result) before calling
    /// this, so it falls back to <see cref="Int"/>.
    /// </summary>
    /// <summary>C's integer promotions (§6.3.1.1): an integer type of rank below
    /// <c>int</c> — <c>char</c>/<c>short</c>/<c>_Bool</c> and their unsigned
    /// twins — promotes to <c>int</c> (all its values fit). Anything else passes
    /// through. The type of unary <c>+ - ~</c>, and the first step of
    /// <see cref="UsualArithmetic"/>.</summary>
    public static CType IntegerPromote(CType t) =>
        t.Unqualified is Prim { Integer: true, Bytes: < 4 } ? Int : t;

    public static CType UsualArithmetic(CType a, CType b)
    {
        if (a.Unqualified is not Prim pa || b.Unqualified is not Prim pb) { return Int; }

        // Either operand floating → the wider floating operand wins (double
        // outranks float); an integer operand doesn't participate further.
        if (!pa.Integer || !pb.Integer)
        {
            if (pa.Integer) { return pb; }
            if (pb.Integer) { return pa; }
            return pa.Bytes >= pb.Bytes ? pa : pb;
        }

        // Integer promotion: a type of rank below `int` promotes to `int` (every
        // such type — byte/sbyte/short/ushort — is representable in int).
        static Prim Promote(Prim p) => p.Bytes < 4 ? (Prim)Int : p;
        var x = Promote(pa);
        var y = Promote(pb);

        // Same signedness → the higher rank (wider) type.
        if (x.Signed == y.Signed) { return x.Bytes >= y.Bytes ? x : y; }

        // Mixed signedness: the unsigned operand wins unless the signed operand's
        // rank is strictly greater (a wider signed type represents every value of
        // the narrower unsigned type, so C keeps the result signed).
        var (uns, sig) = x.Signed ? (y, x) : (x, y);
        return sig.Bytes > uns.Bytes ? sig : uns;
    }
}
