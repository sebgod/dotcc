#nullable enable

namespace DotCC.Libc;

/// <summary>
/// C99/C11 <c>_Bool</c> — an unsigned integer type that holds exactly 0 or 1.
/// Modeled as a distinct value type (NOT C# <c>bool</c>) so it carries C's
/// defining boolean semantics: storing ANY scalar normalizes to 0/1, and the
/// value converts freely to/from <c>int</c> in arithmetic, comparison, return,
/// argument, and <c>%d</c>-printing positions. The implicit conversions below
/// do that at every boundary, so dotcc needs neither a normalization rewrite
/// nor a type-inference pass — and <c>CBool</c> stays distinct from
/// <c>char</c> (also a byte).
/// </summary>
/// <remarks>
/// <c>bool</c> in <c>&lt;stdbool.h&gt;</c> is a macro for <c>_Bool</c>;
/// <c>true</c>/<c>false</c> lower to the integer literals <c>1</c>/<c>0</c>
/// (which normalize through the <c>int</c> conversion). <c>sizeof(_Bool)</c>
/// is 1, matching C. Pointer stores — <c>_Bool b = somePointer;</c>, meaning
/// <c>somePointer != NULL</c> — are covered by the <c>void*</c> conversion
/// below: a typed <c>T*</c> reaches it through the standard <c>T* → void*</c>
/// implicit conversion, so the chain is one standard + one user-defined
/// conversion (which C# permits). The earlier belief that C# forbids
/// pointer-involving user-defined conversions was wrong — only conversions
/// to/from a *generic type parameter* or interface are restricted, not
/// pointers. Because the conversion lives on the type, EVERY store position
/// (decl init, assignment, argument to a <c>_Bool</c> param, struct/array
/// element, <c>return</c> in a <c>_Bool</c> function) coerces uniformly with
/// no emitter rewrite.
/// </remarks>
public readonly struct CBool
{
    private readonly byte _v;
    private CBool(byte v) { _v = v; }

    // Store-normalization: any nonzero scalar becomes 1 (C's _Bool conversion).
    public static implicit operator CBool(int x) => new((byte)(x != 0 ? 1 : 0));
    public static implicit operator CBool(long x) => new((byte)(x != 0 ? 1 : 0));
    public static implicit operator CBool(double x) => new((byte)(x != 0 ? 1 : 0));
    public static implicit operator CBool(bool b) => new((byte)(b ? 1 : 0));
    // Pointer store: `_Bool b = p;` is `p != NULL`. A typed `T*` arrives here
    // via the standard `T* → void*` conversion (one standard + one user-defined
    // step — allowed). float reaches the `double` overload the same way.
    public static unsafe implicit operator CBool(void* p) => new((byte)(p != null ? 1 : 0));

    // Reads back as int 0/1 — drives arithmetic, comparison, return, args, %d.
    public static implicit operator int(CBool b) => b._v;

    // Explicit reads at every integer width: C casts `(sexp_uint_t)boolexpr`
    // (chibi's tag packing). The set must be EXHAUSTIVE — with only some
    // widths defined, a cast to another width has several viable user-defined
    // conversions and C# reports ambiguity (CS0457) instead of picking one;
    // an exact-target operator always wins.
    public static explicit operator byte(CBool b) => b._v;
    public static explicit operator sbyte(CBool b) => (sbyte)b._v;
    public static explicit operator short(CBool b) => b._v;
    public static explicit operator ushort(CBool b) => b._v;
    public static explicit operator uint(CBool b) => b._v;
    public static explicit operator long(CBool b) => b._v;
    public static explicit operator ulong(CBool b) => b._v;

    public override string ToString() => _v.ToString();
}
