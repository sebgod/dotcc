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
        Optional o => "?" + o.Inner.Describe(),
        ErrorUnion eu => "!" + eu.Payload.Describe(),
        ErrorSetType => "anyerror",
        Slice s => "[]" + (s.Element.IsConst ? "const " : "") + s.Element.Unqualified.Describe(),
        Allocator => "std.mem.Allocator",
        ZigList l => "std.ArrayList(" + l.Element.Describe() + ")",
        Tuple t => "struct { " + string.Join(", ", t.Elements.Select(e => e.Describe())) + " }",
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

        /// <summary>True when this function-pointer type addresses NATIVE code (a
        /// <c>dlsym</c>'d symbol or other unmanaged entry point) and must therefore be
        /// called through a <c>delegate* unmanaged[Cdecl]&lt;…&gt;</c> rather than the
        /// default managed <c>delegate*&lt;…&gt;</c>. It is a .NET ABI annotation, NOT
        /// part of the C type identity — two <c>int(*)(int)</c> types are the same C
        /// type whether or not one happens to point at native code — so it is excluded
        /// from record equality / hashing (below) and is deliberately not a
        /// <see cref="TypeQual"/> (those are C qualifiers that surface in
        /// <see cref="Describe"/> / <see cref="Unqualified"/>).</summary>
        public bool IsNativeCallConv { get; init; }

        /// <summary>Equality excludes <see cref="IsNativeCallConv"/> (a .NET ABI
        /// annotation, not C type identity) and otherwise replicates the synthesized
        /// record behavior this replaces: <see cref="Params"/> compares structurally
        /// via the default comparer (reference equality for a <c>List&lt;CType&gt;</c>),
        /// plus <see cref="Return"/>, <see cref="Variadic"/> and the base
        /// <see cref="CType.Quals"/>.</summary>
        public bool Equals(Func? other) =>
            other is not null
            && Quals == other.Quals
            && Variadic == other.Variadic
            && EqualityComparer<IReadOnlyList<CType>>.Default.Equals(Params, other.Params)
            && EqualityComparer<CType>.Default.Equals(Return, other.Return);

        public override int GetHashCode() => HashCode.Combine(Quals, Variadic, Return);
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

    /// <summary>A Zig optional <c>?T</c> over a NON-pointer payload. The C# backend
    /// lowers it to <c>Nullable&lt;T&gt;</c> (<c>T?</c>), so <c>null</c> is none, <c>.?</c>
    /// is <c>.Value</c> (panics on none), and <c>orelse</c> is <c>??</c> — exactly Zig's
    /// semantics, for free. An optional POINTER <c>?*T</c> does NOT use this: it lowers to
    /// a bare nullable <c>T*</c> (Zig's own niche), so <see cref="Optional"/> only ever
    /// wraps a value type. The C front-end never produces it (C has no optionals); only the
    /// C# target renders it.</summary>
    public sealed record Optional(CType Inner) : CType
    {
        // Approximate (Nullable<T> is the payload plus a flag byte, padded). @sizeOf(?T)
        // is not a B1 feature, so an exact layout isn't needed here.
        public override int SizeOf => Inner.SizeOf + 1;
    }

    /// <summary>A Zig error union <c>E!T</c> (Milestone B2). The C# backend lowers it to
    /// the runtime <c>ErrUnion&lt;Payload&gt;</c> value type (<c>ErrUnion&lt;Unit&gt;</c> for
    /// a <c>void</c> payload, since C# has no generic-over-void) — either a payload or a
    /// non-zero error code. V1 erases the error SET: every union shares one flat global code
    /// space, so the set <c>E</c> is dropped here (<c>!T</c> / <c>anyerror!T</c> / <c>E!T</c>
    /// all carry just the payload). The C front-end never produces it (C has no error
    /// unions); only the C# target renders it.</summary>
    public sealed record ErrorUnion(CType Payload) : CType
    {
        // Approximate: the payload plus a 2-byte code, padded. @sizeOf(E!T) is not a B2
        // feature, so an exact layout isn't needed here.
        public override int SizeOf => Payload.SizeOf + 2;
    }

    /// <summary>A Zig error SET value (Milestone N) — the type of a bare <c>error.Foo</c> and of an
    /// <c>else |e|</c> / <c>catch |e|</c> capture. V1 erases the named set into one flat global code
    /// space (each <c>error.Foo</c> name → a stable <c>ushort</c> code, shared program-wide), so this
    /// is a single nameless marker the C# backend renders as <c>ushort</c> — the raw code. A bare
    /// <c>error.Foo</c> lowers to that code as a literal of this type, so error-value equality
    /// (<c>e == error.Foo</c>) and a future error <c>switch</c> compare codes. Distinct from a plain
    /// <see cref="UShort"/> so error operands are recognisable structurally (and so a named
    /// <c>error{A,B}</c> set has a type to attach to later). The C front-end never produces it; only
    /// the C# target renders it.</summary>
    public sealed record ErrorSetType : CType
    {
        // The flat global error code (a ushort). @sizeOf of an error set isn't a Zig surface
        // feature, so an exact layout isn't needed here.
        public override int SizeOf => 2;
    }

    /// <summary>A Zig slice <c>[]T</c> / <c>[]const T</c> (Milestone E) — a fat pointer
    /// <c>{ ptr, len }</c>. The C# backend lowers it to the runtime <c>Slice&lt;T&gt;</c>
    /// value type (<c>ConstSlice&lt;T&gt;</c> when <see cref="Element"/> is <c>const</c>):
    /// a blittable <c>{ T* Ptr; nuint Len; }</c>, the C++ <c>std::span</c> shape — NOT C#'s
    /// ref-struct <see cref="System.Span{T}"/> (a slice must be a struct field / cross the
    /// Zig ABI, neither of which a ref struct allows). <c>[]const T</c> is this with a
    /// <c>const</c>-qualified <see cref="Element"/>, reusing the qualifier machinery. The C
    /// front-end never produces it (C has no slices); only the C# target renders it.</summary>
    public sealed record Slice(CType Element) : CType
    {
        // A fat pointer: a data pointer (8) plus a nuint length (8). @sizeOf([]T) is not in
        // scope for Milestone E, so an exact-layout guarantee isn't needed here.
        public override int SizeOf => 16;
    }

    /// <summary>Zig's curated <c>std.ArrayList(T)</c> (wall-plan W0) — the modern UNMANAGED
    /// array list (no stored allocator; growing calls take one explicitly). The C# backend
    /// lowers it to the runtime <c>ZigList&lt;T&gt;</c> value type
    /// (<c>DotCC.Libc/ZigList.cs</c>): <c>{ ptr, len, capacity }</c>. Zig's <c>.empty</c> decl
    /// literal is <c>default</c>; the curated member set (<c>append</c> / <c>appendSlice</c> /
    /// <c>pop</c> / <c>deinit</c> / <c>clearRetainingCapacity</c> / <c>items</c> /
    /// <c>capacity</c>) routes to the runtime in the Zig lowering, and an unmodeled member is a
    /// clear error. The C front-end never produces it; only the C# target renders it.</summary>
    public sealed record ZigList(CType Element) : CType
    {
        // { T* ptr; ulong len; ulong cap } = 24. @sizeOf(std.ArrayList(T)) isn't a lowered
        // surface feature, so an exact-layout guarantee isn't needed here.
        public override int SizeOf => 24;
    }

    /// <summary>Zig's <c>std.mem.Allocator</c> (Milestone F) — a fat pointer
    /// <c>{ ptr, vtable }</c> over a runtime allocator. The C# backend lowers it to the runtime
    /// <c>Allocator</c> value type (<c>DotCC.Libc/ZigAlloc.cs</c>): an opaque context plus a
    /// by-value vtable of raw allocation function pointers. A method call <c>a.alloc(T,n)</c> /
    /// <c>a.free(s)</c> on an operand of this type dispatches through the vtable (the indirect
    /// path); the lowering devirtualizes to a direct <c>Libc.malloc</c>/<c>free</c> when it can
    /// prove the operand is the statically-known C-heap default. The C front-end never produces
    /// it (C has no allocator abstraction — its <c>malloc</c> is already a direct call); only the
    /// C# target renders it.</summary>
    public sealed record Allocator : CType
    {
        // { void* Ctx; AllocatorVTable Vtable } where the vtable carries two fn-ptrs by value:
        // 8 (ctx) + 8 + 8 (two delegate*). @sizeOf(Allocator) isn't a Zig surface feature, so an
        // exact layout isn't needed.
        public override int SizeOf => 24;
    }

    /// <summary>A Zig tuple <c>struct { T1, T2, … }</c> (Milestone G) — an anonymous positional
    /// struct. The C# backend lowers it to <c>System.ValueTuple&lt;T1, …&gt;</c> (arity-uniform,
    /// including arity 1), so a positional literal <c>.{a, b}</c> constructs one
    /// (<see cref="DotCC.Ir.TupleNew"/>), <c>t[N]</c> reads <c>.ItemN+1</c>
    /// (<see cref="DotCC.Ir.TupleIndex"/>), and <c>const a, const b = e</c> destructures it. Only
    /// the runtime subset maps — comptime / type-valued tuple fields stay out. The C front-end
    /// never produces it; only the C# target renders it.</summary>
    public sealed record Tuple(IReadOnlyList<CType> Elements) : CType
    {
        // Approximate — Σ element sizes, ignoring ValueTuple field layout/padding. @sizeOf of a
        // tuple isn't a Zig surface feature, so an exact layout isn't needed here.
        public override int SizeOf => Elements.Sum(e => e.SizeOf);

        // Structural equality over the element list (the default record comparer would use
        // reference equality on the list), plus the base qualifiers — mirrors the Func record.
        public bool Equals(Tuple? other) =>
            other is not null && Quals == other.Quals && Elements.SequenceEqual(other.Elements);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Quals);
            foreach (var e in Elements) { h.Add(e); }
            return h.ToHashCode();
        }
    }

    // ---- well-known instances -------------------------------------------

    public static readonly CType Void = new VoidType();
    public static readonly CType Bool = new Prim("_Bool", 1, true, false);
    public static readonly CType Char = new Prim("char", 1, true, true);
    public static readonly CType SChar = new Prim("signed char", 1, true, true);
    public static readonly CType UChar = new Prim("unsigned char", 1, true, false);

    /// <summary>C23 <c>char8_t</c> (<c>&lt;uchar.h&gt;</c>) — an unsigned 8-bit UTF-8
    /// code unit (<c>unsigned char</c>). dotcc lowers it to C# <c>byte</c>, exactly
    /// like <see cref="Char"/> (dotcc's <c>char</c> IS <c>byte</c>), so a <c>u8"…"</c>
    /// literal rides the existing narrow UTF-8 string path (<c>Libc.L(…u8)</c>) with
    /// no new machinery. A distinct Prim (not <see cref="Char"/> / <see cref="UChar"/>)
    /// purely for type fidelity (<c>_Generic</c>, diagnostics); it renders to
    /// <c>byte</c>, so every byte-coercion rule applies unchanged.</summary>
    public static readonly CType Char8 = new Prim("char8_t", 1, true, false);
    public static readonly CType Short = new Prim("short", 2, true, true);
    public static readonly CType UShort = new Prim("unsigned short", 2, true, false);

    /// <summary>C11 <c>char16_t</c> (<c>&lt;uchar.h&gt;</c>) — an unsigned 16-bit code
    /// unit (<c>uint_least16_t</c>). dotcc lowers it to C# <c>char</c> (also a 16-bit
    /// UTF-16 code unit), so <c>char16_t*</c> arithmetic walks 2 bytes and <c>u"…"</c>
    /// carries real UTF-16. A distinct Prim (not <see cref="UShort"/>) so the backend
    /// can spell it <c>char</c> while keeping the unsigned-16-bit arithmetic identity.</summary>
    public static readonly CType Char16 = new Prim("char16_t", 2, true, false);

    /// <summary>C <c>wchar_t</c> (<c>&lt;wchar.h&gt;</c>). dotcc commits to the
    /// <em>MSVC-shaped</em> wchar_t — an unsigned 16-bit UTF-16 code unit — so it
    /// lowers to C# <c>char</c> exactly like <see cref="Char16"/> (a documented ABI
    /// choice, same flavour as dotcc's LP64 / little-endian commitments; on gcc/Linux
    /// <c>wchar_t</c> is instead 32-bit, so the gcc oracle is opted out per fixture).
    /// A <em>distinct</em> Prim (not <see cref="Char16"/>) for type fidelity in
    /// diagnostics/<c>sizeof</c>; the backend renders both to <c>char</c>, so every
    /// <c>char</c>-coercion rule applies unchanged.</summary>
    public static readonly CType WChar = new Prim("wchar_t", 2, true, false);

    /// <summary>C11 <c>char32_t</c> (<c>&lt;uchar.h&gt;</c>) — an unsigned 32-bit code
    /// unit (<c>uint_least32_t</c>). dotcc lowers it to C# <c>uint</c> (a plain 32-bit
    /// unsigned integer, NOT <see cref="System.Text.Rune"/> — char32_t is any value,
    /// not a validated scalar), so <c>char32_t*</c> arithmetic walks 4 bytes and a
    /// <c>U"…"</c> literal carries real UTF-32 (one code unit per Unicode scalar — an
    /// astral char is ONE char32_t, unlike the two UTF-16 units of <see cref="Char16"/>).
    /// A distinct Prim (not <see cref="UInt"/>) so the backend keeps type fidelity for
    /// diagnostics/<c>sizeof</c>; it renders to <c>uint</c>, which is already a fully
    /// wired C# integer type (no coercion-table entry needed, unlike <c>char</c>).</summary>
    public static readonly CType Char32 = new Prim("char32_t", 4, true, false);
    public static readonly CType Int = new Prim("int", 4, true, true);
    public static readonly CType UInt = new Prim("unsigned int", 4, true, false);
    public static readonly CType Long = new Prim("long", 8, true, true);
    public static readonly CType ULong = new Prim("unsigned long", 8, true, false);
    public static readonly CType LongLong = new Prim("long long", 8, true, true);
    public static readonly CType ULongLong = new Prim("unsigned long long", 8, true, false);

    /// <summary>The 128-bit integer types — GCC's <c>__int128</c> / <c>unsigned __int128</c> in C,
    /// Zig's <c>i128</c> / <c>u128</c>. Both lower to C# <see cref="System.Int128"/> /
    /// <see cref="System.UInt128"/> (BCL primitives — all arithmetic comes for free). 16 bytes;
    /// <see cref="UsualArithmetic"/> ranks them above <c>long</c> structurally on <c>Bytes</c>.</summary>
    public static readonly CType Int128 = new Prim("__int128", 16, true, true);
    public static readonly CType UInt128 = new Prim("unsigned __int128", 16, true, false);
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

    /// <summary>The Zig error-set value type — a bare <c>error.Foo</c> value / a captured error
    /// (Milestone N). See <see cref="ErrorSetType"/>.</summary>
    public static readonly CType ErrorSet = new ErrorSetType();

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
